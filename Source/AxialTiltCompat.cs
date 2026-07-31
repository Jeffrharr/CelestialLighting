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
//
// AND SO EVERY MEMBER IS A STRING, which is the price of that. A rename or a signature change
// upstream cannot fail at compile time here; it fails at Bind(), and it must fail SOFTLY. Every
// resolve below is null-checked and every CreateDelegate is guarded, so drift degrades to "RAT
// treated as absent, our own geometry, one warning naming the member" instead of throwing out of a
// StaticConstructorOnStartup — which is what it did before, and which takes the whole mod down over
// one renamed method in someone else's assembly. ApiVersion is not a substitute for this: it moves
// only when THEY decide a change is breaking, and a rename they consider a tidy-up is exactly the
// case where they would not think to bump it.
public static class AxialTiltCompat
{
    private const string PackageId = "dsweber.RealisticAxialTilt";

    // Ours, as RAT records it in LightingOwner and shows on their settings screen.
    private const string OurPackageId = "joof.celestiallighting";
    private const string ApiTypeName = "RealisticAxialTilt.Api.RealisticAxialTiltApi";

    // We bind against ApiVersion 1. Their contract is that additive changes leave this alone and
    // only breaking ones bump it, so `>=` is the correct test — pinning equality would lock us out
    // of every future release for no reason.
    private const int RequiredApiVersion = 1;

    // Deliberately NOT a fallback-to-reflection-into-internals path. An older RAT without the Api
    // type is treated as absent — this gate is now the ONLY thing standing between a pre-API RAT
    // and the double-render (About.xml used to declare that mod incompatible outright; it no longer
    // can, because the published build composes correctly and the tag cannot tell the two apart).
    // Reaching into their internals is what this whole arrangement exists to avoid: it re-creates
    // the unbounded maintenance (chasing their patch list release by release) the upstream flag
    // removed.
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

        ratTryClaimLighting(OurPackageId);
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

        if (!TryReadApiVersion(api, out int version))
            return false;

        if (version < RequiredApiVersion)
        {
            Log.Warning(
                $"[CelestialLighting] Realistic Axial Tilt's interop API is version {version}, "
                + $"but {RequiredApiVersion} or newer is required. Update it, or disable one of the two mods.");
            return false;
        }

        if (!TryBindRequiredMembers(api))
            return false;

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

        // Present but unusable is worth a warning where absent is not: absent means an older RAT,
        // reshaped means the contract moved under us.
        TryCreateDelegate(
            "LunarDeclinationDegrees", lunarDeclination, LunarMemberConsequence,
            out ratLunarDeclinationDegrees);
    }

    // Every member the interop cannot work without, resolved one at a time so a failure can say
    // WHICH one. `&&` short-circuits, so the first missing member is the one reported and the rest
    // are not chased — a renamed API would otherwise log five warnings for one upstream edit.
    //
    // TryClaimLighting is bound FIRST on purpose, so the salvage path below has it even when the
    // geometry is unreadable.
    private static bool TryBindRequiredMembers(Type api)
    {
        bool complete =
            TryBindMethod(api, "TryClaimLighting", out ratTryClaimLighting)
            && TryBindGetter(api, "GeometryReady", out ratGeometryReady)
            && TryBindGetter(api, "AxialTiltDegrees", out ratAxialTiltDegrees)
            && TryBindGetter(api, "GeometryGeneration", out ratGeometryGeneration)
            && TryBindMethod(api, "SolarDeclinationDegrees", out ratDeclinationDegrees);

        if (complete)
            return true;

        SalvageLightingClaim();
        return false;
    }

    // The one thing still worth doing when the geometry is unreadable: take the lighting anyway.
    //
    // The alternative is worse, not safer. Our patches are registered either way, and so are theirs;
    // declining to claim doesn't hand the sky back to RAT, it puts both mods on
    // SectionLayer_SunShadows.Regenerate with a `return false` prefix each, where the loser silently
    // never builds a shadow mesh and which one loses is Harmony registration order. Claiming leaves
    // exactly one renderer. The cost is that our sky then runs on OUR obliquity while their tilt
    // still drives temperature and growing seasons, so a high-tilt world looks a season off — bad,
    // visible, and reported in the warning, which is the whole difference from silent.
    private static void SalvageLightingClaim()
    {
        ratTryClaimLighting?.Invoke(OurPackageId);
        ClearBindings();
    }

    // Left in the state a never-bound CelestialLighting is in, so a half-resolved API cannot leave a
    // live delegate behind a `bound == false` gate. Nothing reads these while bound is false; this is
    // belt and braces against a future reader who assumes non-null means usable.
    private static void ClearBindings()
    {
        ratTryClaimLighting = null;
        ratGeometryReady = null;
        ratAxialTiltDegrees = null;
        ratGeometryGeneration = null;
        ratDeclinationDegrees = null;
        ratLunarDeclinationDegrees = null;
    }

    // ApiVersion is read reflectively too, and it is the member most likely to move if they ever
    // restructure the type — so it gets the same treatment as the rest rather than the
    // `AccessTools.Field(...).GetValue(null)` that used to throw a NullReferenceException here.
    private static bool TryReadApiVersion(Type api, out int version)
    {
        version = 0;
        FieldInfo field = AccessTools.Field(api, "ApiVersion");
        if (field == null)
        {
            WarnMemberUnusable("ApiVersion", "is gone", RequiredMemberConsequence);
            return false;
        }

        object value = field.GetValue(null);
        if (value is int parsed)
        {
            version = parsed;
            return true;
        }

        WarnMemberUnusable(
            "ApiVersion", $"is a {value?.GetType().Name ?? "null"} rather than an int", RequiredMemberConsequence);
        return false;
    }

    // Bound as delegates rather than called through PropertyInfo.GetValue every time: Active is read
    // on the per-frame geometry path (SolarPosition.ComputeInputsForMap), and reflective property
    // access there would show up in a profile.
    private static bool TryBindGetter<T>(Type api, string propertyName, out Func<T> binding) =>
        TryCreateDelegate(
            propertyName, AccessTools.PropertyGetter(api, propertyName), RequiredMemberConsequence, out binding);

    private static bool TryBindMethod<TDelegate>(Type api, string methodName, out TDelegate binding)
        where TDelegate : Delegate =>
        TryCreateDelegate(methodName, AccessTools.Method(api, methodName), RequiredMemberConsequence, out binding);

    // The single place a name or a signature from their assembly turns into one of our delegates,
    // and so the single place upstream drift can be caught.
    //
    // Both failures it handles are the same event seen from two sides: they renamed or reshaped a
    // member of a type we bind by string. A rename leaves AccessTools returning null; a signature
    // change (an added parameter, float to double, an instance method) leaves the name resolving and
    // CreateDelegate throwing ArgumentException. Neither is exceptional enough to take the mod down
    // with it — before this, both propagated out of a StaticConstructorOnStartup — and neither is
    // silent, because "your sun is subtly wrong" is not something a player can debug unaided.
    private static bool TryCreateDelegate<TDelegate>(
        string memberName, MethodInfo method, string consequence, out TDelegate binding)
        where TDelegate : Delegate
    {
        binding = null;

        if (method == null)
        {
            WarnMemberUnusable(memberName, "is not there", consequence);
            return false;
        }

        try
        {
            binding = (TDelegate)Delegate.CreateDelegate(typeof(TDelegate), method);
            return true;
        }
        catch (Exception e)
        {
            WarnMemberUnusable(
                memberName, $"has an unexpected signature ({e.GetType().Name}: {e.Message})", consequence);
            return false;
        }
    }

    // Split from the message so each arm states the consequence it actually has. A missing required
    // member costs us the whole planet; a missing lunar one costs an inclination.
    private const string RequiredMemberConsequence =
        "Falling back to our own solar geometry: the sky still renders, but on OUR axial tilt rather "
        + "than this world's, so seasons may look out of step with RAT's temperature and growing "
        + "periods.";

    private const string LunarMemberConsequence =
        "Falling back to the moon we place on the ecliptic ourselves — still RAT's seasonal model, "
        + "just without their orbital inclination. The sun is unaffected.";

    private static void WarnMemberUnusable(string memberName, string problem, string consequence)
    {
        Log.Warning(
            $"[CelestialLighting] Realistic Axial Tilt's interop API declares ApiVersion "
            + $"{RequiredApiVersion} or newer, but its {memberName} {problem}. {consequence} This is an "
            + "upstream API change rather than a mod-list mistake — please report it.");
    }

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
