using System.Collections.Generic;
using RimWorld;
using Verse;

namespace CelestialLighting;

// Mesh invalidation for §15's shadow half — the direct counterpart of IndoorOcclusionRedraw, and
// there for the same reason: the caster heights EaveShadowGrid resolves are baked into section
// meshes, which RimWorld only rebuilds when a section is dirtied. Unlike §1's shadow direction
// (a per-frame shader global), flipping the eave toggle changes nothing on screen until those
// meshes are regenerated, which reads as "the setting did nothing".
//
// Buildings rather than Roofs is the flag to dirty here: it is what SectionLayer_SunShadows has
// always subscribed to, so it is guaranteed to reach the layer even if
// Patch_ShadowRoofInvalidation's widening ever failed to apply.
//
// It reaches BOTH halves of §15, and that is now load-bearing rather than incidental.
// SectionLayer_EaveShade subscribes to Roofs | Buildings and skips its bake entirely while the eave
// feature is off, so this call is what repopulates the shade meshes when a player ticks the setting
// back on. Narrowing this flag, or moving the shade layer off Buildings, would leave the shade
// missing until some unrelated edit happened to dirty the sections — the exact "the setting did
// nothing" failure this class exists to prevent, one layer over.
//
// Change-detected rather than unconditional because CelestialLightingSettings.ApplyToRuntime runs
// every frame the settings window is open, and WholeMapChanged rebuilds every section on the map —
// fine once on a click, wasteful at 60 Hz.
public static class EaveShadowRedraw
{
    // Seeded with the shipped default so a startup ApplyToRuntime that changes nothing also queues
    // nothing.
    private static bool lastEnabled = true;

    public static void SyncTo(bool enabled)
    {
        if (enabled == lastEnabled)
            return;

        lastEnabled = enabled;
        RebuildShadowMeshes();
    }

    // Unconditional rebuild, for callers that change what the mesh would be *without* going through
    // the settings object — the harness's SetFeature step writes CelestialLightingFeatures directly,
    // and without this a scenario's A/B screenshots would both show whatever was baked before.
    public static void ForceRebuild() => RebuildShadowMeshes();

    // Find.Maps is empty during startup and on the main menu, which also keeps the MapMeshFlagDefOf
    // lookup from running before defs are loaded.
    private static void RebuildShadowMeshes()
    {
        List<Map> maps = Find.Maps;
        if (maps == null)
            return;

        for (int i = 0; i < maps.Count; i++)
            maps[i].mapDrawer?.WholeMapChanged((ulong)MapMeshFlagDefOf.Buildings);
    }
}
