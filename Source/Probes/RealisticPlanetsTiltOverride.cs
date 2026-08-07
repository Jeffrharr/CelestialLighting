using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead. This
// WRITES to another mod's state and must never reach a player's game; living under Probes/ is what
// guarantees that.
//
// Why it has to exist, same argument as PlanetsmithTiltOverride's: Realistic Planets 2's tilt is
// chosen in its world-generation UI and frozen into the save, and our fixture (minimal_colony.rws)
// was generated before RP2 existed, so its game component loads at the scribe default of Normal —
// 22.5 degrees, less than a degree from our own 23.44. A world where the interop is live and
// invisible is the one world a scenario cannot prove anything with.
//
// Easier than Planetsmith's version in one respect and harder in another. Easier because the field is
// a public static on their game component, so there is no world walk and no settings hop: resolve the
// type, set the field. Harder because it is an ENUM of theirs, not a float, so the value has to be
// parsed out of their own type by name rather than written as a number — which is also the safer
// thing to do, since it fails loudly if they rename a step instead of writing an ordinal that means
// something else.
public static class RealisticPlanetsTiltOverride
{
    public const string FeatureKey = "realistic_planets_steep_tilt";

    // VeryHigh, their 45-degree step. Their enum has five values and no slider, so there is no
    // "steep but not maximal" choice to make here the way there was for Planetsmith — the honest
    // options are 33.75 and 45, and 45 is the one no tolerance can absorb. The clamp-bug worry that
    // ruled out 90 degrees for Planetsmith does not apply: 45 is not the ceiling of anything of ours
    // (Formulas.MaxObliquityDegrees is 90), so a clamp pinning every tilt to its maximum would show
    // up here as 90 and fail.
    private const string SteepStepName = "VeryHigh";

    // What that step is worth, asserted rather than assumed. The scenario pins a declination computed
    // from this number, so if RP2 ever retunes VeryHigh the pin should fail with a wrong value rather
    // than pass against a silently rescaled table.
    public const float SteepTiltDegrees = 45f;

    private const string GameComponentTypeName = "Planets.Core.Planets_GameComponent";
    private const string TiltFieldName = "axialTilt";

    private static bool overridden;
    private static object originalTilt;

    public static void Set(bool steep)
    {
        FieldInfo tilt = ResolveTiltField();
        if (tilt == null)
            return;

        if (steep)
        {
            object steepValue = ResolveSteepStep(tilt.FieldType);
            if (steepValue == null)
                return;

            RememberOriginal(tilt);
            tilt.SetValue(null, steepValue);
            // Logged on SUCCESS, not only on failure. A test hook that silently does nothing is
            // indistinguishable from the feature under test not working, and telling those apart from
            // the report alone cost two live runs on the Planetsmith equivalent.
            Log.Message(
                $"[CelestialLighting.Probes] Realistic Planets 2 tilt override: {originalTilt} -> "
                + $"{tilt.GetValue(null)}.");
            return;
        }

        // Restoring without having overridden would write a stale originalTilt over a world we never
        // touched — which, between scenarios in a suite, is exactly how one scenario's setup leaks
        // into the next one's world. Their field is a static, so that leak would outlive the world
        // itself and reach the next scenario's save-load.
        if (!overridden)
            return;

        tilt.SetValue(null, originalTilt);
        overridden = false;
    }

    // Captured on the first override only, so that flipping the flag on twice does not record
    // VeryHigh as the "original" and make the restore a no-op.
    private static void RememberOriginal(FieldInfo tilt)
    {
        if (overridden)
            return;

        originalTilt = tilt.GetValue(null);
        overridden = true;
    }

    private static object ResolveSteepStep(Type tiltEnumType)
    {
        if (Enum.IsDefined(tiltEnumType, SteepStepName))
            return Enum.Parse(tiltEnumType, SteepStepName);

        return Unresolved($"{tiltEnumType.FullName} declares no {SteepStepName} step");
    }

    // Resolved fresh each call rather than cached: this runs a handful of times per scenario, and a
    // cache here would have to be invalidated on world reload — which is precisely the between-
    // scenario moment this hook must not get wrong.
    private static FieldInfo ResolveTiltField()
    {
        try
        {
            Type componentType = AccessTools.TypeByName(GameComponentTypeName);
            if (componentType == null)
                return (FieldInfo)Unresolved($"{GameComponentTypeName} did not resolve");

            FieldInfo tilt = AccessTools.Field(componentType, TiltFieldName);
            if (tilt == null)
                return (FieldInfo)Unresolved($"{TiltFieldName} did not resolve on {componentType.FullName}");

            if (!tilt.IsStatic || !tilt.FieldType.IsEnum)
                return (FieldInfo)Unresolved($"{TiltFieldName} is no longer a static enum field");

            return tilt;
        }
        catch (Exception e)
        {
            Log.Warning($"[CelestialLighting.Probes] Could not reach Realistic Planets 2's tilt: {e}");
            return null;
        }
    }

    // Every way this can decline to resolve says so. A silent null here does not fail the scenario —
    // the probe simply reads the tilt the world already had — which presents as an interop that did
    // not take effect rather than as a test hook that did not run. That distinction cost a live run
    // once already on the Planetsmith side.
    private static object Unresolved(string reason)
    {
        Log.Warning(
            $"[CelestialLighting.Probes] Realistic Planets 2 tilt override did not apply: {reason}.");
        return null;
    }
}
