using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace CelestialLighting;

// Soft interop with Realistic Axial Tilt (RAT, dsweber.RealisticAxialTilt).
//
// RAT lets the player pick their planet's obliquity at world gen (0-90 degrees) and reshapes the
// seasons around it. We render the sky. Left alone the two mods fight: both postfix
// GenCelestial.GetLightSourceInfo and CurShadowStrength and both overwrite __result, and both
// prefix SectionLayer_SunShadows.Regenerate with `return false` — so one mod's shadow mesh simply
// never builds, with no error, depending on Harmony registration order.
//
// The split we've agreed with RAT upstream: they own the planet's solar geometry, we own every
// pixel of lighting. This class is our half of that. It claims lighting through their API (which
// stands their rendering patches down, present and future) and reads their declination so our sun,
// shadows and moon sit where their planet says they should.
//
// WHY WE READ DECLINATION AND NOT THE TILT ANGLE: RAT's seasonal phase is not vanilla's. Vanilla
// (and our Formulas.DeclinationSign) uses -cos(dayOfYear/60 * 2pi); RAT uses sin(...), which is a
// quarter-year offset in where the solstices land. Taking their declination directly means their
// phase is their contract — if they re-phase the year, we follow with no code change here. Reading
// AxialTiltDegrees and re-applying our own curve would look right at the equinoxes and be wrong in
// between, which is exactly the kind of disagreement that takes a season of play to notice.
//
// NO HARD REFERENCE. Everything is late-bound by reflection through their public Api type, the same
// way their own Compat/ classes bind to the mods they support. We never reference their assembly,
// so a user without RAT loads a CelestialLighting that has simply never heard of it.
public static class AxialTiltCompat
{
    private const string PackageId = "dsweber.RealisticAxialTilt";
    private const string ApiTypeName = "RealisticAxialTilt.Api.RealisticAxialTiltApi";

    // We bind against ApiVersion 1. Their contract is that additive changes leave this alone and
    // only breaking ones bump it, so `>=` is the correct test — pinning equality would lock us out
    // of every future release for no reason.
    private const int RequiredApiVersion = 1;

    // Deliberately NOT a fallback-to-reflection-into-internals path. An older RAT without the Api
    // type is treated as absent, and About.xml declares it incompatible. Reaching into their
    // internals is what this whole arrangement exists to avoid: it re-creates the unbounded
    // maintenance (chasing their patch list release by release) that the upstream flag removed.
    private static bool bound;
    private static bool triedBind;

    private static Func<float, float> ratDeclinationDegrees;
    private static Func<bool> ratGeometryReady;
    private static Func<float> ratAxialTiltDegrees;
    private static Func<int> ratGeometryGeneration;
    private static Func<string, bool> ratTryClaimLighting;

    // Optional, and so nullable where the five above are not: RAT's lunar geometry landed after
    // ApiVersion 1 was minted and landed additively, which by their own contract does NOT bump the
    // version. The version gate therefore cannot see it and we probe for the method itself.
    private static Func<float, float, float> ratLunarDeclinationDegrees;

    // True once RAT is present, exposes an Api we understand, and has seeded its geometry.
    //
    // GeometryReady is not a formality: before their world comp's FinalizeInit runs, their cosTilt
    // is 0 rather than 1, so an early call doesn't return Earth-like defaults, it returns a
    // degenerate planet. Every read below goes through this.
    public static bool Active => Bind() && ratGeometryReady();

    // Their obliquity in degrees when active, else our own constant. Used for the moon, which needs
    // the tilt magnitude rather than a single day's declination, and for the settings screen.
    public static float ObliquityDegrees =>
        Active ? ratAxialTiltDegrees() : Formulas.AxialTiltDegrees;

    // Bumped by RAT every time a world is generated or loaded. Anything of ours that caches a
    // derived solar quantity across days must drop that cache when this changes, or a save with a
    // different tilt silently reuses the previous world's numbers.
    public static int GeometryGeneration => Active ? ratGeometryGeneration() : 0;

    // THE seam. Every sun-derived effect in this mod resolves its declination here, and the moon
    // resolves its own through the same call at a shifted day-of-year (see MoonPosition), so the
    // two can never end up on different models of the year.
    public static float SolarDeclinationDegrees(float dayOfYear) =>
        Active ? ratDeclinationDegrees(dayOfYear) : Formulas.SolarDeclinationDegrees(dayOfYear);

    // True only when the feature is on, RAT is active, AND their build carries lunar geometry. All
    // three, because all three vary independently: the flag is the player's (and the harness's)
    // switch, Active is whether RAT is there and seeded, and the binding is whether THIS RAT build
    // has a moon at all. That last one is not hypothetical — RAT shipped the interop API before it
    // shipped the moon, additively, so both builds answer Active identically and only the resolved
    // method tells them apart. Flag first: it is a field read, and it short-circuits the bind.
    public static bool LunarGeometryActive =>
        CelestialLightingFeatures.AxialTiltLunarGeometry
        && Active
        && ratLunarDeclinationDegrees != null;

    // The moon's half of the seam above.
    //
    // WHOSE MOON IS THIS. RAT now models the moon as a body on an INCLINED orbit — inclination and
    // ascending-node regression, both player-tunable — rather than one riding the ecliptic exactly.
    // That is planet geometry, which is theirs under the split, so we take their declination when
    // they have one. What we keep is the phase: cyclePosition is ours, from GameComponent_MoonPhase,
    // and we hand it to them (their API documents exactly this call — "supply your own cycle
    // position ... for any offset"). Phase is what drives illumination, moonlight, the HUD label and
    // eclipse staging, all of which are lighting and all of which are ours. The consequence worth
    // knowing: with both mods installed RAT's own moonOrbitalDays slider does not move our moon,
    // because the cycle it would set is the one we are overriding. Their moonInclinationDeg does.
    //
    // Reachable three ways, which is why the fallback below is a first-class path and not an error
    // case: no RAT, a RAT predating their lunar block, or a player who turned
    // CelestialLightingFeatures.AxialTiltLunarGeometry off.
    //
    // The fallback is not a lesser approximation bolted on; it is exactly this same model at
    // inclination 0. RAT builds the moon's ecliptic longitude as (dayOfYear/60 + cyclePosition)*2pi
    // — the sun's longitude advanced by the elongation — which is our MoonEquivalentSunDayOfYear fed
    // through whichever solar declination function is live. So an older RAT, or no RAT at all, lands
    // on a moon that differs from the inclined one only by their inclination term (5.1 degrees by
    // default), and never by a phase or a season. MoonMathTests pins that equivalence from our side.
    public static float MoonDeclinationDegrees(float dayOfYear, float cyclePosition) =>
        LunarGeometryActive
            ? ratLunarDeclinationDegrees(dayOfYear, cyclePosition)
            : SolarDeclinationDegrees(MoonMath.MoonEquivalentSunDayOfYear(dayOfYear, cyclePosition));

    // Called once at mod init. Tells RAT to stand its lighting patches down.
    //
    // Failure is not fatal and not ours to resolve: it means a third lighting mod claimed first, in
    // which case RAT logs the collision and we simply keep rendering. Two mods drawing the sun is a
    // user-facing mod-list problem, not something we can paper over.
    public static void ClaimLighting()
    {
        if (!Bind())
            return;

        ratTryClaimLighting("joof.celestiallighting");
    }

    // Reflection binding, done once and cached. Failures here are silent-by-design for the common
    // case (RAT not installed) and logged once for the interesting case (RAT installed but too old
    // or reshaped), because that is a real mod-list problem the player can act on.
    private static bool Bind()
    {
        if (triedBind)
            return bound;

        triedBind = true;

        if (!ModIsActive())
            return false;

        Type api = AccessTools.TypeByName(ApiTypeName);
        if (api == null)
        {
            Log.Warning(
                "[CelestialLighting] Realistic Axial Tilt is installed but exposes no interop API. "
                + "Both mods draw the sun and shadows and will conflict. Update Realistic Axial Tilt, "
                + "or disable one of the two.");
            return false;
        }

        int version = (int)AccessTools.Field(api, "ApiVersion").GetValue(null);
        if (version < RequiredApiVersion)
        {
            Log.Warning(
                $"[CelestialLighting] Realistic Axial Tilt's interop API is version {version}, "
                + $"but {RequiredApiVersion} or newer is required. Update it, or disable one of the two mods.");
            return false;
        }

        ratGeometryReady = Getter<bool>(api, "GeometryReady");
        ratAxialTiltDegrees = Getter<float>(api, "AxialTiltDegrees");
        ratGeometryGeneration = Getter<int>(api, "GeometryGeneration");
        ratDeclinationDegrees = (Func<float, float>)Delegate.CreateDelegate(
            typeof(Func<float, float>), AccessTools.Method(api, "SolarDeclinationDegrees"));
        ratTryClaimLighting = (Func<string, bool>)Delegate.CreateDelegate(
            typeof(Func<string, bool>), AccessTools.Method(api, "TryClaimLighting"));
        BindLunarGeometry(api);

        Log.Message(
            "[CelestialLighting] Realistic Axial Tilt detected; using its solar geometry for sun, "
            + "shadows and moon"
            + (ratLunarDeclinationDegrees != null
                ? ", including its inclined lunar orbit."
                : " (no lunar geometry in this RAT build; the moon rides the ecliptic exactly)."));

        bound = true;
        return true;
    }

    // Optional binding, so a miss is silent: an older RAT that has the API but not the moon is a
    // perfectly healthy mod list, not a mismatch to warn about. AccessTools.Method returns null
    // rather than throwing when the member is absent, which is the whole reason the probe is cheap.
    private static void BindLunarGeometry(Type api)
    {
        MethodInfo lunarDeclination = AccessTools.Method(api, "LunarDeclinationDegrees");
        if (lunarDeclination == null)
            return;

        ratLunarDeclinationDegrees = (Func<float, float, float>)Delegate.CreateDelegate(
            typeof(Func<float, float, float>), lunarDeclination);
    }

    // Bound as delegates rather than called through PropertyInfo.GetValue every time: Active is read
    // on the per-frame geometry path (SolarPosition.ComputeInputsForMap), and reflective property
    // access there would show up in a profile.
    private static Func<T> Getter<T>(Type api, string propertyName) =>
        (Func<T>)Delegate.CreateDelegate(
            typeof(Func<T>), AccessTools.PropertyGetter(api, propertyName));

    private static bool ModIsActive()
    {
        foreach (ModContentPack pack in LoadedModManager.RunningMods)
        {
            if (pack.PackageId.Equals(PackageId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
