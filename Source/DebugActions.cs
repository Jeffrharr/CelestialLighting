using LudeonTK;
using RimWorld;
using Verse;

namespace CelestialLighting;

// Dev-mode debug actions. The mod's first — everything until now was A/B'd through the harness, which
// can flip any feature key from a scenario but only on the map a scenario builds, and only between
// screenshots nobody is standing in front of.
//
// WHY THESE EXIST AT ALL, given the harness already toggles the same flags. A held run (`run_test.sh
// --hold`) hands back a live game with the scenario's world state intact, and at that point the harness
// is out of the loop: the report is written, the driver is done, and there is no channel left to flip a
// feature through. So a held game can only ever show ONE arm — whichever the scenario left set — which
// is exactly the wrong shape for "I want to see this one myself". These put the toggle back in the hands
// of whoever is looking at the screen, in the held game and equally in an ordinary colony save.
//
// Dev mode gates the whole menu, so nothing here is reachable by a player who has not deliberately
// turned on developer mode. That is why a taste toggle can live here rather than in the settings screen:
// the settings screen is a promise to support a knob forever, and this is a way to look at something.
//
// Every action that changes a BAKED quantity must force the rebuild itself. The lighting overlay's
// alphas only regenerate when a section is dirtied (a roof edit, a lamp toggle), so a flag flipped
// without the rebuild appears to do nothing until something unrelated happens to touch the map — which
// reads as "the toggle is broken" rather than "the mesh is stale". The harness's own feature
// registrations carry the same call for the same reason.
public static class DebugActions
{
    private const string Category = "Celestial Lighting";

    [DebugAction(Category, "Toggle decoupled indoor floor", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    private static void ToggleDecoupledIndoorFloor()
    {
        CelestialLightingFeatures.DecoupledIndoorFloor = !CelestialLightingFeatures.DecoupledIndoorFloor;
        IndoorOcclusionRedraw.ForceRebuild();

        // The message names both halves of what a viewer needs to judge the toggle, because the effect is
        // invisible in daylight BY CONSTRUCTION (the keep factor is 1, the compensation is the identity)
        // and somebody flipping this at noon would otherwise conclude it does nothing. Quoting the live
        // keep alongside the flag state turns "nothing happened" into "nothing was supposed to happen
        // yet, come back at dusk".
        Map map = Find.CurrentMap;
        float keep = NightOverlayKeep.For(map);
        float floor = IndoorOcclusionSettings.Current.MinIndoorBrightness;
        string state = CelestialLightingFeatures.DecoupledIndoorFloor ? "decoupled" : "compounded";
        float effective = CelestialLightingFeatures.DecoupledIndoorFloor
            ? IndoorOcclusionMath.EffectiveIndoorFloor(floor, keep)
            : floor;

        Messages.Message(
            $"Indoor floor: {state}. Overlay keep {keep:F2}, indoor floor {floor:F2} -> cap {effective:F2}"
            + (keep >= 1f ? " (daylight: identical either way)" : ""),
            MessageTypeDefOf.TaskCompletion, historical: false);
    }

    // The other half of the same comparison, and the reason it is here rather than left to the settings
    // screen: the settings window is a modal over the map, so changing the floor there means closing the
    // window to see the result and reopening it to change it again. This steps the floor in place.
    [DebugAction(Category, "Cycle minimum indoor brightness", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    private static void CycleMinIndoorBrightness()
    {
        // The two preset values plus the midpoint and full black: Realistic's 0, Cinematic's 0.50, and
        // enough between them to see which way the knob is pulling. Deliberately does NOT write the
        // persisted setting — this is a look, not a preference, and a dev action that silently rewrote a
        // player's saved settings would be a genuinely nasty surprise on the next launch.
        float[] stops = { 0f, 0.15f, 0.25f, 0.5f, 0.75f };
        float current = IndoorOcclusionSettings.Current.MinIndoorBrightness;

        float next = stops[0];
        for (int i = 0; i < stops.Length; i++)
        {
            if (current < stops[i] - 0.001f)
            {
                next = stops[i];
                i = stops.Length;
            }
        }

        IndoorOcclusionSettings.Current.MinIndoorBrightness = next;
        IndoorOcclusionRedraw.ForceRebuild();
        Messages.Message(
            $"Minimum indoor brightness {next:F2} (runtime only — the saved setting is untouched)",
            MessageTypeDefOf.TaskCompletion, historical: false);
    }
}
