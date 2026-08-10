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
// Also covers §7c's sliders (NativeSkyFalloffSettings) and §7d's door-strength sensitivity, not just
// §7b's own: SkyFalloffSource feeds CapOcclusion's same third argument, so a change to any of the three
// subsystems' knobs invalidates the identical baked alpha and needs the identical rebuild — one redraw
// trigger for the whole occlusion term rather than three that would have to stay in sync by hand.
//
// Change-detected rather than unconditional because CelestialLightingSettings.ApplyToRuntime runs
// every frame the settings window is open, and WholeMapChanged rebuilds every section on the map —
// fine once on a click, wasteful at 60 Hz.
public static class IndoorOcclusionRedraw
{
    // Seeded with the shipped defaults so a startup ApplyToRuntime that changes nothing also queues
    // nothing. (Harmless either way — no map exists yet at that point — but it keeps the invariant
    // "these fields are what the baked meshes were built from" true from the first frame.)
    private static bool lastEnabled = true;
    private static float lastMinIndoorBrightness;
    private static int lastNativeMaxDepth = NativeSkyFalloffMath.DefaultMaxDepth;
    private static float lastNativePassThroughPercent = NativeSkyFalloffMath.DefaultPassThroughPercent;
    private static float lastDoorStrengthSensitivity = DoorLeakMath.DefaultSensitivity;

    // minIndoorBrightness is the floor the bake actually applies (IndoorOcclusionSettings.
    // accessibility slider — either knob moving must trigger a rebuild, and comparing the resolved value
    // catches both without duplicating the max() rule here.
    public static void SyncTo(
        bool enabled, float minIndoorBrightness,
        int nativeMaxDepth, float nativePassThroughPercent, float doorStrengthSensitivity)
    {
        bool unchanged = enabled == lastEnabled
            && minIndoorBrightness == lastMinIndoorBrightness
            && nativeMaxDepth == lastNativeMaxDepth
            && nativePassThroughPercent == lastNativePassThroughPercent
            && doorStrengthSensitivity == lastDoorStrengthSensitivity;
        if (unchanged)
            return;

        lastEnabled = enabled;
        lastMinIndoorBrightness = minIndoorBrightness;
        lastNativeMaxDepth = nativeMaxDepth;
        lastNativePassThroughPercent = nativePassThroughPercent;
        lastDoorStrengthSensitivity = doorStrengthSensitivity;
        RebuildLightingMeshes();
    }

    // Unconditional rebuild, for callers that change what the alphas would be *without* going through
    // the settings object — the harness's SetFeature step writes CelestialLightingFeatures directly, and
    // without this a scenario's A/B screenshots would both show whatever was baked before the toggle.
    public static void ForceRebuild() => RebuildLightingMeshes();

    // Same rebuild, but for exactly one map — GameComponent_SkyFalloffRedraw calls this per map whose
    // CurSkyGlow has actually drifted, rather than paying for every map on the tick just one of them
    // needs a rebuild. GroundGlow is the flag SectionLayer_LightingOverlay's own constructor registers
    // as relevant, so dirtying it regenerates exactly the layer whose alphas we rewrite.
    public static void ForceRebuildMap(Map map) => map.mapDrawer?.WholeMapChanged((ulong)MapMeshFlagDefOf.GroundGlow);

    // Find.Maps is empty during startup and on the main menu, which also keeps the MapMeshFlagDefOf
    // lookup from running before defs are loaded.
    private static void RebuildLightingMeshes()
    {
        List<Map> maps = Find.Maps;
        if (maps == null)
            return;

        for (int i = 0; i < maps.Count; i++)
            ForceRebuildMap(maps[i]);
    }
}
