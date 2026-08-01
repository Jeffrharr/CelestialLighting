using System;
using System.Reflection;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace CelestialLighting;

// Soft interop with Planetsmith (aspctt.planetsmith).
//
// Planetsmith is a world-GENERATION overhaul: a climate simulation and a biome-scoring pass that
// replace vanilla's biome placement. One of its world parameters is an axial tilt (0-90 degrees,
// default 23.4), and it uses that tilt to decide how strongly the seasons shape the planet it is
// about to build — pole temperatures, seasonality, where the tundra ends and the boreal forest
// starts. Nothing about that reaches the sky. Their own world-params dialog says as much, warning
// that this tilt "decides how the seasons shape the planet's biomes while it is generated" and is
// separate from the one Worldbuilder uses for in-game temperature swings.
//
// So there is no conflict to resolve here — unlike Realistic Axial Tilt, Planetsmith patches nothing
// we patch (its three Harmony targets are Page_CreateWorldParams.DoWindowContents/PreOpen and
// WorldGenStep_Terrain.GenerateFresh) and it never renders a sun. What there IS, is a disagreement:
// a player who generates a world at 60 degrees of tilt gets biomes laid out for a planet with savage
// seasons, and then we light it with Earth's 23.44 because that is the only number we had. The
// biomes say one planet and the sky says another. This class is the wire between them.
//
// WHY THIS READS THE TILT ANGLE WHERE AxialTiltCompat DELIBERATELY DOES NOT. That file argues at
// length that taking RAT's obliquity and re-applying our own seasonal curve would be wrong, and it
// is — for RAT. RAT reckons the year from a different point than we do (sin where we use -cos, a
// quarter-year apart), so their tilt without their phase would agree with them at the equinoxes and
// be a season out in between. Planetsmith has no phase to disagree about: it does not model the
// in-game year at all, and its tilt is a scalar that only ever multiplied a seasonality term during
// generation. Scaling OUR curve by THEIR obliquity is therefore the whole of the correct answer
// here, not a shortcut around a missing API.
//
// THEIR TILT, OUR CLOCK. Planetsmith publishes no day length: it patches no day-length member and
// its assembly contains no reference to CelestialSunGlowPercent, SunPosition, DayPercent,
// GenCelestial, TwelfthOfYear or SeasonalShiftAmplitude at all. So we take the obliquity and leave
// §14's default LockedToVanilla clock alone, and that is forced rather than chosen — there is no day
// length of theirs to adopt.
//
// It is also where Planetsmith and RAT differ most, which matters when reading this next to
// AxialTiltCompat. RAT patches GenCelestial.CelestialSunGlowPercent, so it re-tilts VANILLA'S OWN
// glow curve; when our locked clock snaps to vanilla it is snapping to a clock that already carries
// the planet's obliquity, and locked mode is exactly right there. Nothing re-tilts that curve for
// Planetsmith, so the same snap lands on Earth's day length over a planet built for another tilt.
//
// We do it anyway, deliberately. Locked mode's standing bargain is already "faithful sun altitude,
// vanilla day length" — that is what the warp IS — so the obliquity lands on the axis locked mode
// reproduces honestly and never touches the one it was always going to fake. Measured at 45 degrees
// latitude on day 30, the altitude axis arrives in full at noon (68.4 -> 75.0 degrees, shadows 32%
// shorter) and tapers toward sunrise and sunset, because WarpDayPercent works in offset-from-noon
// space and so maps noon to noon exactly. Gating the whole interop on the realistic sun clock was
// considered and rejected: it would leave the setting silently inert for everyone who never changes
// the default, which is worse than a partial effect that is real.
//
// PRECEDENCE. If both mods are installed, RAT wins outright and this is not consulted — see the
// chain in AxialTiltCompat.SolarDeclinationDegrees. RAT owns the live planet's geometry (phase,
// tilt and moon) and drives the running game's seasons; Planetsmith's tilt was spent at world
// generation and is, by then, a record of how the map was built rather than a claim about the sky.
// Two mods cannot both define the obliquity, and the one still simulating the year is the one to
// believe.
//
// NO HARD REFERENCE, same as AxialTiltCompat: every member is a string resolved at runtime, so a
// player without Planetsmith loads a build that has never heard of it. Unlike RAT there is no
// negotiated API to bind against — these are their internal field and property names, which is a
// weaker contract and is treated as one. Every resolve is null-checked, a miss logs once and leaves
// us on our own tilt, and nothing here can throw into a caller.
public static class PlanetsmithCompat
{
    private const string PackageId = "aspctt.planetsmith";
    private const string WorldComponentTypeName = "Planetsmith.PlanetsmithWorldComponent";

    // Their per-world settings object, reached as PlanetsmithWorldComponent.Settings.axialTilt.
    // Deliberately NOT their mod-settings default (PlanetsmithMod.Settings.axialTilt): that one is
    // the value the NEXT world will be generated with, and it moves whenever the player opens the
    // settings screen. The world component's copy is the tilt THIS planet was actually built for,
    // saved alongside it, and it is the only one that describes the biomes now on the map.
    private const string SettingsPropertyName = "Settings";
    private const string TiltFieldName = "axialTilt";

    private static bool bound;
    private static bool triedBind;

    private static Type worldComponentType;
    private static PropertyInfo settingsProperty;
    private static FieldInfo tiltField;

    // What is cached is the COMPONENT LOOKUP, not the tilt.
    //
    // The lookup is the part worth avoiding — a walk of World.components with a type test per entry —
    // and it is the part that genuinely cannot change while one world is loaded. The tilt itself is
    // re-read from the field every time, which costs two reflective gets on the per-frame geometry
    // path and buys two things worth more than they cost: a mid-run change is picked up (which is how
    // Tests/Scenarios/planetsmith_tilt.json can A/B one world against two tilts at all), and there is
    // no second copy of the number to go stale against a save-reload that reassigns Settings under a
    // World instance we would otherwise still consider current.
    //
    // A WeakReference rather than a plain field so the cache key cannot keep a discarded World — and
    // the whole map graph hanging off it — alive after the player returns to the main menu. Fully
    // qualified because Verse declares a WeakReference<T> of its own and `using Verse` makes the bare
    // name ambiguous; we want the BCL's, whose TryGetTarget is the identity check below.
    private static readonly System.WeakReference<World> CachedWorld =
        new System.WeakReference<World>(null);
    private static object cachedComponent;

    // True when Planetsmith is installed, its world component is readable, and the player has left
    // the feature on. Exposed for the probe and the settings screen; the geometry path below reads
    // the tilt directly rather than testing this first, so that "active" and "which number" can
    // never be answered from two different reads of the world.
    public static bool Active => CelestialLightingFeatures.PlanetsmithGeometry && TryReadTilt(out _);

    // Their obliquity when we can read one, else Earth's. This is the only value this class
    // contributes — there is no phase, no moon and no shadow direction here, because Planetsmith
    // models none of those.
    public static float ObliquityDegrees =>
        TryReadTilt(out float tilt) && CelestialLightingFeatures.PlanetsmithGeometry
            ? tilt
            : Formulas.AxialTiltDegrees;

    // The declination our sun actually runs on when RAT is absent: our seasonal phase, their swing.
    //
    // Sanitizing happens inside Formulas.SolarDeclinationDegrees rather than at the read below, so
    // that the clamp is part of the pure, tested formula rather than a property of how we got the
    // number. A tilt that arrives out of range is then equally handled whether it came from
    // reflection, a test, or some future third mod.
    public static float SolarDeclinationDegrees(float dayOfYear) =>
        Formulas.SolarDeclinationDegrees(dayOfYear, ObliquityDegrees);

    // Reads the tilt off the live world, using the memo when the world has not changed.
    //
    // Returns false for every ordinary "no" — Planetsmith absent, no world loaded yet (the main
    // menu, or early startup before Find.World exists), or a world generated before the player
    // installed it — and those are silent, because they are not problems. Only a Planetsmith that IS
    // loaded and whose members we cannot find warrants a warning, and Bind() issues that once.
    private static bool TryReadTilt(out float tilt)
    {
        tilt = 0f;

        if (!Bind())
            return false;

        object component = ComponentForCurrentWorld();
        if (component == null)
            return false;

        return TryReadTiltFrom(component, out tilt);
    }

    // Resolves Planetsmith's world component for whatever world is loaded now, reusing the last
    // answer while the world is the same object.
    //
    // A null result covers every ordinary case as well as the failure ones: no world (main menu, or
    // startup before Find.World exists) and a world with no Planetsmith component are both simply
    // "nothing to read", and neither is worth a log line.
    private static object ComponentForCurrentWorld()
    {
        World world = Find.World;
        if (world == null)
            return null;

        if (CachedWorld.TryGetTarget(out World cached) && ReferenceEquals(cached, world))
            return cachedComponent;

        cachedComponent = FindComponent(world);
        CachedWorld.SetTarget(world);
        return cachedComponent;
    }

    // The actual field read, kept separate so the caller above is about caching and this is about
    // reflection.
    //
    // Wrapped in a catch because this walks two hops into another assembly's object graph (component
    // -> settings -> field) and either can be null or, on a save written by a Planetsmith version we
    // have not seen, a different shape. A throw here would come out of
    // SolarPosition.ComputeInputsForMap — the per-frame geometry path — and so would not be one error
    // but one per frame forever.
    private static bool TryReadTiltFrom(object component, out float tilt)
    {
        tilt = 0f;

        try
        {
            object settings = settingsProperty.GetValue(component);
            if (settings == null)
                return false;

            return tiltField.GetValue(settings) is float value && Finite(value, out tilt);
        }
        catch (Exception e)
        {
            Log.WarningOnce(
                "[CelestialLighting] Could not read Planetsmith's axial tilt for this world "
                + $"({e.GetType().Name}: {e.Message}). Lighting this planet on our own tilt of "
                + $"{Formulas.AxialTiltDegrees} degrees instead, which may look out of step with the "
                + "biomes Planetsmith generated. This is an upstream change rather than a mod-list "
                + "mistake — please report it.",
                PlanetsmithReadWarningKey);
            return false;
        }
    }

    // Verse.Log.WarningOnce keys on an int the caller supplies; an arbitrary constant, unique within
    // this mod, is the convention (Planetsmith's own ModCompat does the same thing).
    private const int PlanetsmithReadWarningKey = 0x0C11_5417;

    // A NaN tilt is rejected here rather than clamped, so that the fallback below is Earth's tilt
    // and not a silent zero. Formulas.SanitizeObliquityDegrees would also catch it — this is the
    // belt to its braces, and it is what lets Active answer "no" rather than "yes, garbage".
    private static bool Finite(float value, out float finite)
    {
        finite = value;
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    // World.components is a plain list; there is no non-generic GetComponent to reach through, and
    // building a closed generic over a type we only have by name would cost more than a walk of a
    // list that has a handful of entries and is only walked once per world.
    private static object FindComponent(World world)
    {
        foreach (WorldComponent component in world.components)
        {
            if (worldComponentType.IsInstanceOfType(component))
                return component;
        }

        return null;
    }

    // Resolved once and cached, including the negative case: a player without Planetsmith pays one
    // pass over the running mod list at first read and nothing thereafter.
    private static bool Bind()
    {
        if (triedBind)
            return bound;

        triedBind = true;

        if (!ModIsActive())
            return false;

        worldComponentType = AccessTools.TypeByName(WorldComponentTypeName);
        if (worldComponentType == null)
            return WarnUnbindable($"its {WorldComponentTypeName} type is gone");

        settingsProperty = AccessTools.Property(worldComponentType, SettingsPropertyName);
        if (settingsProperty == null)
            return WarnUnbindable($"its {SettingsPropertyName} property is gone");

        tiltField = AccessTools.Field(settingsProperty.PropertyType, TiltFieldName);
        if (tiltField == null)
            return WarnUnbindable($"its {TiltFieldName} field is gone");

        if (tiltField.FieldType != typeof(float))
            return WarnUnbindable($"its {TiltFieldName} field is a {tiltField.FieldType.Name} rather than a float");

        Log.Message(
            "[CelestialLighting] Planetsmith detected; lighting each world on the axial tilt it was "
            + "generated with.");

        bound = true;
        return true;
    }

    // Says the consequence rather than the fault, the same way AxialTiltCompat's warnings do: the
    // player cannot act on "a field moved", but they can recognise "the sky does not match the
    // biomes" once someone has told them to expect it.
    private static bool WarnUnbindable(string problem)
    {
        Log.Warning(
            $"[CelestialLighting] Planetsmith is installed but {problem}. Lighting every world on our "
            + $"own tilt of {Formulas.AxialTiltDegrees} degrees instead, so a world generated with a "
            + "different tilt will have a sky that looks out of step with its biomes. This is an "
            + "upstream change rather than a mod-list mistake — please report it.");

        ClearBindings();
        return false;
    }

    // Left in the state a never-bound CelestialLighting is in, so a half-resolved type cannot leave a
    // live MemberInfo behind a `bound == false` gate.
    private static void ClearBindings()
    {
        worldComponentType = null;
        settingsProperty = null;
        tiltField = null;
    }

    // Whether the mod is in the load order at all, regardless of whether we can read a tilt out of it
    // or the player has left the feature on. The settings screen asks this — and only this — because a
    // toggle should appear for anyone who has Planetsmith, including at the main menu where no world
    // is loaded and Active is therefore false.
    public static bool ModIsInstalled => ModIsActive();

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
