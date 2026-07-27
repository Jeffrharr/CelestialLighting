using System.Collections.Generic;
using RimWorld;
using Verse;

namespace CelestialLighting;

// Mesh invalidation for §9's per-cell half — the direct counterpart of IndoorOcclusionRedraw (§7b)
// and EaveShadowRedraw (§15), and deliberately the same shape rather than a third one.
//
// This became necessary the moment SectionLayer_NightDesaturation grew its `!Visible` early return.
// Before that, the layer baked its wash whether or not the feature was on and DrawLayer's own Visible
// check decided whether to draw it, so a toggle took effect on the next frame with nothing to
// rebuild. Now the bake is skipped while the feature is off — and Verse.Section.TryUpdate clears a
// layer's Dirty flag even when Regenerate returns early — so by the time a player ticks the box back
// on, nothing on the map is marked dirty and the wash would stay absent until the next lamp or roof
// edit. That reads as "the setting did nothing", which is exactly the failure §7b and §15 hit.
//
// GroundGlow is the flag to dirty: it is one of the two SectionLayer_NightDesaturation subscribes to,
// and unlike Roofs it is not also consumed by half the vanilla layers, so it is the narrower of the
// two ways to reach our layer.
//
// Change-detected rather than unconditional because CelestialLightingSettings.ApplyToRuntime runs
// every frame the settings window is open, and WholeMapChanged rebuilds every section on the map —
// fine once on a click, wasteful at 60 Hz.
public static class NightDesaturationRedraw
{
    // Seeded with the shipped default so a startup ApplyToRuntime that changes nothing also queues
    // nothing.
    private static bool lastEnabled = true;

    public static void SyncTo(bool enabled)
    {
        if (enabled == lastEnabled)
            return;

        lastEnabled = enabled;
        RebuildWashMeshes();
    }

    // Unconditional rebuild, for callers that change what the mesh would be *without* going through
    // the settings object — the harness's SetFeature step writes CelestialLightingFeatures directly,
    // and without this a scenario's A/B screenshots would both show whatever was baked before.
    public static void ForceRebuild() => RebuildWashMeshes();

    // Find.Maps is empty during startup and on the main menu, which also keeps the MapMeshFlagDefOf
    // lookup from running before defs are loaded.
    private static void RebuildWashMeshes()
    {
        List<Map> maps = Find.Maps;
        if (maps == null)
            return;

        for (int i = 0; i < maps.Count; i++)
            maps[i].mapDrawer?.WholeMapChanged((ulong)MapMeshFlagDefOf.GroundGlow);
    }
}
