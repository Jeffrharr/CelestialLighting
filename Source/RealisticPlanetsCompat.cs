using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace CelestialLighting;

// Soft interop with Realistic Planets 2 (koth.RealisticPlanets2).
//
// RP2 is a worldgen and climate overhaul: terrain, hydrology, a layered climate model, biome
// placement, and a world-map mode framework. One of the parameters the player picks when generating
// a world is an axial tilt, chosen from five steps (VeryLow/Low/Normal/High/VeryHigh, which their
// AxialTiltCurves maps to 0/11.25/22.5/33.75/45 degrees), and that tilt shapes the seasonal
// temperature amplitude, the biome layout, and the size of the day/night temperature swing at every
// tile. What it does NOT do any more is reach the sky.
//
// WHY THIS FILE EXISTS NOW AND NOT BEFORE. The mod's first Workshop release shipped a
// Planets.PlanetaryLighting namespace — its own sky pipeline, its own geometric eclipses, its own
// dynamically-regenerated shadow layer — and patched GenCelestial.CelestialSunGlow,
// SkyManager.CurrentSkyTarget, GenCelestial.CurShadowStrength, GlowGrid and Printer_Shadow to drive
// them. Two mods rendering one sky is the conflict class this mod exists to avoid, so there was
// nothing to integrate with, only something to stay out of the way of. The current release deletes
// that subsystem outright: the assembly no longer references SkyManager, GenCelestial, GlowGrid,
// SkyColorSet, Printer_Shadow or SectionLayer at all, and its remaining Harmony targets are
// worldgen, world-map UI and GenTemperature. Zero overlap with ours.
//
// What the deletion leaves behind is the same hole Planetsmith had: a planet that was BUILT for a
// tilt, whose sky nobody lights on it. A world generated at VeryHigh gets biomes and temperature
// curves for a planet with savage seasons, and then — without this file — a sun on Earth's 23.44.
//
// THEIR MAGNITUDE AND THEIR PHASE, which is where this parts company with PlanetsmithCompat.
// Planetsmith's tilt is a scalar spent during generation; it models no year, so there is no phase of
// theirs to disagree with and scaling our own curve by their obliquity is the whole correct answer.
// RP2 still runs a live seasonal model after generation — Planets.WorldGen.SolarGeometry computes a
// sun altitude every time GenTemperature.OffsetFromSunCycle is asked for a tile's diurnal swing —
// and that model has a phase: tilt * sin(2*pi * yearPhase), a quarter-year ahead of vanilla's (and
// our) -cos. Taking the tilt but keeping our phase would put our solstice on day 30 while the
// weather they simulate peaks its daily swing on day 15, so we take both. See
// Formulas.RealisticPlanetsSolarDeclinationDegrees for the arithmetic.
//
// THE COST OF THAT, STATED PLAINLY BECAUSE IT IS REAL. RP2 does not patch
// GenTemperature.OffsetFromSeasonCycle, so vanilla still owns the SEASONAL temperature cycle and
// still runs it on -cos — coldest at day 0, warmest at day 30. Following RP2's phase therefore puts
// our sky's longest day a fortnight before RimWorld's warmest and its growing season. On a vanilla
// or Planetsmith world those two agree; on an RP2 world they cannot, because RP2's own two halves
// already disagree. The choice here is which of the two to match, and matching the mod that owns
// the planet is the one that keeps a single mod answering for the planet's geometry — the same
// ruling AxialTiltCompat makes for RAT. A player who notices the sun peaking early is seeing RP2's
// year, not a bug in ours.
//
// PRECEDENCE. RAT above this, this above Planetsmith, our constant below all three; the chain lives
// in AxialTiltCompat.SolarDeclinationDegrees and the reasoning for each link is there. RP2 sits
// above Planetsmith because it supplies a phase as well as a scale and is still simulating the
// running year, and below RAT for the same reason RAT beats everything: RAT owns the live planet's
// geometry including its moon. All three at once is not a mod list anyone should have — two worldgen
// overhauls fight over biome placement long before they reach us — but the chain answers it anyway
// rather than asking who is installed in one place.
//
// NO HARD REFERENCE, same as the other two compat files: every member is a string resolved at
// runtime, so a player without RP2 loads a build that has never heard of it. These are their
// internal names with no negotiated API behind them, which is a weaker contract than RAT's and is
// treated as one — every resolve is null-checked, a miss logs once and leaves us on our own tilt,
// and nothing here can throw into a caller.
public static class RealisticPlanetsCompat
{
    private const string PackageId = "koth.RealisticPlanets2";

    // Their tilt lives on a GameComponent as a PUBLIC STATIC field, scribed in ExposeData, so it is
    // per-save rather than per-instance and there is no component to go and find. That is why this
    // file has none of PlanetsmithCompat's world-component lookup or WeakReference cache: the read
    // is a static field get, and the only thing worth binding once is the reflection.
    private const string GameComponentTypeName = "Planets.Core.Planets_GameComponent";
    private const string TiltFieldName = "axialTilt";

    // Their enum-to-degrees table. Called rather than copied so that a retune upstream moves our sky
    // with it — the five steps are their design decision, not a physical constant, and a mirrored
    // copy here would drift silently the first time they change one.
    private const string TiltCurvesTypeName = "Planets.WorldGen.AxialTiltCurves";
    private const string TiltDegreesMethodName = "GetTiltDegrees";

    private static bool bound;
    private static bool triedBind;

    private static FieldInfo tiltField;

    // Degrees by enum ordinal, resolved once at bind time by calling their GetTiltDegrees for every
    // value the enum declares.
    //
    // Populating the whole table up front rather than invoking per read is what keeps this off the
    // per-frame budget: SolarPosition asks for a declination once per map per frame, and a
    // MethodInfo.Invoke there would be the most expensive thing in the geometry path by an order of
    // magnitude. A dictionary rather than an array because nothing guarantees their enum stays
    // zero-based and contiguous, and an out-of-range index would be a crash where a missing key is
    // a fallback.
    private static Dictionary<int, float> degreesByOrdinal;

    // True when RP2 is installed, a game is loaded, its tilt is readable, and the player has left the
    // feature on. Exposed for the probe and the settings screen; the geometry path below reads the
    // tilt directly rather than testing this first, so that "active" and "which number" can never be
    // answered from two different reads.
    public static bool Active => CelestialLightingFeatures.RealisticPlanetsGeometry && TryReadTilt(out _);

    // Their obliquity when we can read one, else whatever the next provider down offers.
    public static float ObliquityDegrees =>
        TryReadTilt(out float tilt) && CelestialLightingFeatures.RealisticPlanetsGeometry
            ? tilt
            : PlanetsmithCompat.ObliquityDegrees;

    // The declination our sun runs on when RAT is absent and RP2 is not: their swing on their year.
    //
    // The tilt is read once for both the test and the value, rather than going through
    // ObliquityDegrees, so that a save-load between the two reads cannot produce a declination built
    // from one world's answer to "is this active" and another's answer to "how tilted".
    //
    // Sanitizing happens inside Formulas rather than here, so the clamp is part of the pure, tested
    // formula rather than a property of how we got the number.
    public static float SolarDeclinationDegrees(float dayOfYear) =>
        TryReadTilt(out float tilt) && CelestialLightingFeatures.RealisticPlanetsGeometry
            ? Formulas.RealisticPlanetsSolarDeclinationDegrees(dayOfYear, tilt)
            : PlanetsmithCompat.SolarDeclinationDegrees(dayOfYear);

    // Reads the tilt off the running game.
    //
    // Returns false for every ordinary "no" — RP2 absent, or no game loaded — and those are silent,
    // because they are not problems. The game check is not a formality: their field is static and
    // keeps the last save's value after the player returns to the main menu, so without it a menu
    // backdrop would be lit on a planet that is no longer loaded.
    private static bool TryReadTilt(out float tilt)
    {
        tilt = 0f;

        if (!Bind() || Current.Game == null)
            return false;

        return TryReadTiltFrom(out tilt);
    }

    // The actual field read, kept separate so the caller above is about gating and this is about
    // reflection.
    //
    // Wrapped in a catch for the same reason PlanetsmithCompat's is: a throw here would surface from
    // SolarPosition.ComputeInputsForMap — the per-frame geometry path — and so would not be one
    // error but one per frame forever.
    private static bool TryReadTiltFrom(out float tilt)
    {
        tilt = 0f;

        try
        {
            object value = tiltField.GetValue(null);
            if (value == null)
                return false;

            // Convert.ToInt32 rather than a cast: the boxed value is their enum type, which we only
            // have by name, so there is no static type here to unbox through.
            if (degreesByOrdinal.TryGetValue(Convert.ToInt32(value), out float degrees))
                return Finite(degrees, out tilt);

            // A tilt step they added after this build was compiled. Falling back is right — a step
            // we have never seen has no degrees we could honestly claim — but it is worth saying
            // once, because the visible symptom is a sky that quietly ignores one setting.
            Log.WarningOnce(
                $"[CelestialLighting] Realistic Planets 2 reports an axial tilt step ({value}) that "
                + $"was not in its table when this build resolved it. Lighting this world on "
                + $"{Formulas.AxialTiltDegrees} degrees instead, which may look out of step with its "
                + "biomes. This is an upstream change rather than a mod-list mistake — please report "
                + "it.",
                RealisticPlanetsStepWarningKey);
            return false;
        }
        catch (Exception e)
        {
            Log.WarningOnce(
                "[CelestialLighting] Could not read Realistic Planets 2's axial tilt for this world "
                + $"({e.GetType().Name}: {e.Message}). Lighting this planet on our own tilt of "
                + $"{Formulas.AxialTiltDegrees} degrees instead, which may look out of step with the "
                + "biomes it generated. This is an upstream change rather than a mod-list mistake — "
                + "please report it.",
                RealisticPlanetsReadWarningKey);
            return false;
        }
    }

    // Verse.Log.WarningOnce keys on an int the caller supplies; arbitrary constants, unique within
    // this mod, are the convention (see PlanetsmithCompat).
    private const int RealisticPlanetsReadWarningKey = 0x0C11_5418;
    private const int RealisticPlanetsStepWarningKey = 0x0C11_5419;

    // A NaN tilt is rejected here rather than clamped, so that the fallback is the next provider down
    // and not a silent zero. Formulas.SanitizeObliquityDegrees would also catch it — this is the belt
    // to its braces, and it is what lets Active answer "no" rather than "yes, garbage".
    private static bool Finite(float value, out float finite)
    {
        finite = value;
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    // Resolved once and cached, including the negative case: a player without RP2 pays one pass over
    // the running mod list at first read and nothing thereafter.
    private static bool Bind()
    {
        if (triedBind)
            return bound;

        triedBind = true;

        if (!ModIsActive())
            return false;

        Type componentType = AccessTools.TypeByName(GameComponentTypeName);
        if (componentType == null)
            return WarnUnbindable($"its {GameComponentTypeName} type is gone");

        tiltField = AccessTools.Field(componentType, TiltFieldName);
        if (tiltField == null)
            return WarnUnbindable($"its {TiltFieldName} field is gone");

        if (!tiltField.IsStatic)
            return WarnUnbindable($"its {TiltFieldName} field is no longer static");

        if (!tiltField.FieldType.IsEnum)
            return WarnUnbindable(
                $"its {TiltFieldName} field is a {tiltField.FieldType.Name} rather than an enum");

        if (!TryBuildDegreesTable(tiltField.FieldType))
            return false;

        Log.Message(
            "[CelestialLighting] Realistic Planets 2 detected; lighting each world on the axial tilt "
            + "and seasonal phase it was generated with.");

        bound = true;
        return true;
    }

    // Asks their own table for the degrees behind every step of their own enum.
    //
    // Done eagerly, at bind, for two reasons beyond the per-frame cost: a table that cannot be built
    // is a binding failure we want reported at the same moment as a missing field rather than on
    // whatever frame first needed it, and calling every value once here means a step that throws
    // takes the whole interop down cleanly instead of intermittently, on the world where it is used.
    private static bool TryBuildDegreesTable(Type tiltEnumType)
    {
        MethodInfo tiltDegrees = AccessTools.Method(
            AccessTools.TypeByName(TiltCurvesTypeName), TiltDegreesMethodName, new[] { tiltEnumType });

        if (tiltDegrees == null)
            return WarnUnbindable($"its {TiltCurvesTypeName}.{TiltDegreesMethodName} method is gone");

        if (tiltDegrees.ReturnType != typeof(float))
            return WarnUnbindable(
                $"its {TiltDegreesMethodName} returns a {tiltDegrees.ReturnType.Name} rather than a float");

        var table = new Dictionary<int, float>();

        try
        {
            foreach (object step in Enum.GetValues(tiltEnumType))
                table[Convert.ToInt32(step)] = (float)tiltDegrees.Invoke(null, new[] { step });
        }
        catch (Exception e)
        {
            return WarnUnbindable(
                $"its {TiltDegreesMethodName} threw while being read ({e.GetType().Name}: {e.Message})");
        }

        degreesByOrdinal = table;
        return true;
    }

    // Says the consequence rather than the fault, the same way the other two compat files' warnings
    // do: the player cannot act on "a field moved", but they can recognise "the sky does not match
    // the biomes" once someone has told them to expect it.
    private static bool WarnUnbindable(string problem)
    {
        Log.Warning(
            $"[CelestialLighting] Realistic Planets 2 is installed but {problem}. Lighting every "
            + $"world on our own tilt of {Formulas.AxialTiltDegrees} degrees instead, so a world "
            + "generated with a different tilt will have a sky that looks out of step with its "
            + "biomes. This is an upstream change rather than a mod-list mistake — please report it.");

        ClearBindings();
        return false;
    }

    // Left in the state a never-bound CelestialLighting is in, so a half-resolved type cannot leave a
    // live MemberInfo behind a `bound == false` gate.
    private static void ClearBindings()
    {
        tiltField = null;
        degreesByOrdinal = null;
    }

    // Whether the mod is in the load order at all, regardless of whether we can read a tilt out of it
    // or the player has left the feature on. The settings screen asks this — and only this — because
    // a report should appear for anyone who has RP2, including at the main menu where no game is
    // loaded and Active is therefore false.
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
