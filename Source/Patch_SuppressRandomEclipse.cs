using HarmonyLib;
using RimWorld;

namespace CelestialLighting;

// While the natural (§10a) eclipse mode is on, WE fire the Eclipse GameCondition from real moon
// geometry (GameComponent_NaturalEclipse), so the storyteller's random Eclipse *incident* must stand
// down — otherwise a scripted eclipse on some arbitrary day would double up with our geometric ones.
// This prefixes IncidentWorker.CanFireNow and vetoes exactly the Eclipse incident while the mode is
// enabled. Every other incident, and the whole default (mode-off) path, is untouched: when
// NaturalEclipseEnabled is false this returns immediately and the original runs unchanged, so it is a
// true no-op for the shipped-default cosmetic mod.
[HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.CanFireNow))]
public static class Patch_SuppressRandomEclipse
{
    // Return true => let the original CanFireNow run (all non-Eclipse incidents, and everything when
    // the mode is off). Return false with __result=false => veto just the random Eclipse.
    static bool Prefix(IncidentWorker __instance, ref bool __result)
    {
        if (!EclipseSettings.NaturalEclipseEnabled || __instance.def != IncidentDefOf.Eclipse)
            return true;

        __result = false;
        return false;
    }
}
