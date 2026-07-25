using HarmonyLib;
using RimWorld;

namespace CelestialLighting;

// While the natural (§10a) eclipse mode is on, WE fire the Eclipse GameCondition from real moon
// geometry (GameComponent_NaturalEclipse), so the storyteller's random Eclipse *incident* must stand
// down — otherwise a scripted eclipse on some arbitrary day would double up with our geometric ones.
// This prefixes IncidentWorker.CanFireNow and vetoes exactly the Eclipse incident while the mode is
// NaturalOnly. Every other incident, and every other mode, is untouched: outside NaturalOnly (and
// when the master "Eclipse effects" toggle is off) this returns immediately and the original runs
// unchanged. In the default Both mode the storyteller's eclipses are deliberately kept alongside the
// geometric ones, so nothing is suppressed there.
[HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.CanFireNow))]
public static class Patch_SuppressRandomEclipse
{
    // Return true => let the original CanFireNow run (all non-Eclipse incidents, and every mode that
    // keeps the random eclipse). Return false with __result=false => veto just the random Eclipse.
    // Only NaturalOnly suppresses it — in Both we deliberately keep the storyteller's eclipses
    // alongside the geometric ones, and in UnnaturalOnly they are the whole feature.
    static bool Prefix(IncidentWorker __instance, ref bool __result)
    {
        // Master off => the mod leaves eclipses entirely alone, so don't suppress the random one either.
        if (!CelestialLightingFeatures.EclipseDarkening)
            return true;

        if (!EclipseModeRules.SuppressRandomEclipse(EclipseSettings.Mode) || __instance.def != IncidentDefOf.Eclipse)
            return true;

        __result = false;
        return false;
    }
}
