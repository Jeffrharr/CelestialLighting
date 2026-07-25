using System.Collections.Generic;
using RimWorld;
using Verse;

namespace CelestialLighting;

// Mesh invalidation for §7b. The sky-cover alphas Patch_IndoorSkyOcclusion writes live in *baked*
// section meshes, which RimWorld only rebuilds when a section is dirtied (a roof or glow change) —
// unlike §7a's material colour, they are not recomputed per frame. So toggling the feature or moving
// its sliders has no visible effect until something dirties the map, which reads as "the setting did
// nothing". This forces the rebuild on an actual change.
//
// Change-detected rather than unconditional because CelestialLightingSettings.ApplyToRuntime runs
// every frame the settings window is open, and WholeMapChanged rebuilds every section on the map —
// fine once on a click, wasteful at 60 Hz.
public static class IndoorOcclusionRedraw
{
    // Seeded with the shipped defaults so a startup ApplyToRuntime that changes nothing also queues
    // nothing. (Harmless either way — no map exists yet at that point — but it keeps the invariant
    // "these three fields are what the baked meshes were built from" true from the first frame.)
    private static bool lastEnabled = true;
    private static float lastDoorSkyLeak = IndoorOcclusionMath.DefaultDoorSkyLeak;
    private static float lastBrightnessFloor;

    // brightnessFloor is the *resolved* indoor floor (IndoorOcclusionSettings.IndoorFloor), not the raw
    // accessibility slider — either knob moving must trigger a rebuild, and comparing the resolved value
    // catches both without duplicating the max() rule here.
    public static void SyncTo(bool enabled, float doorSkyLeak, float brightnessFloor)
    {
        bool unchanged = enabled == lastEnabled
            && doorSkyLeak == lastDoorSkyLeak
            && brightnessFloor == lastBrightnessFloor;
        if (unchanged)
            return;

        lastEnabled = enabled;
        lastDoorSkyLeak = doorSkyLeak;
        lastBrightnessFloor = brightnessFloor;
        RebuildLightingMeshes();
    }

    // Unconditional rebuild, for callers that change what the alphas would be *without* going through
    // the settings object — the harness's SetFeature step writes CelestialLightingFeatures directly, and
    // without this a scenario's A/B screenshots would both show whatever was baked before the toggle.
    public static void ForceRebuild() => RebuildLightingMeshes();

    // GroundGlow is the flag the lighting overlay layer itself registers as relevant (see
    // SectionLayer_LightingOverlay's constructor), so dirtying it regenerates exactly the layer whose
    // alphas we rewrite. Find.Maps is empty during startup and on the main menu, which also keeps the
    // MapMeshFlagDefOf lookup from running before defs are loaded.
    private static void RebuildLightingMeshes()
    {
        List<Map> maps = Find.Maps;
        if (maps == null)
            return;

        for (int i = 0; i < maps.Count; i++)
            maps[i].mapDrawer?.WholeMapChanged((ulong)MapMeshFlagDefOf.GroundGlow);
    }
}
