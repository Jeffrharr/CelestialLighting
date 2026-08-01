using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace CelestialLighting;

// TEMPORARY DIAGNOSTIC for docs/EAVE-SEAM.md — deleted once the eave seam is understood.
//
// State of play when this was written: the drawn sun-shadow triangles covering the seam strip are
// IDENTICAL between the enclosed room and the gap room (position, alpha, winding, per section, at
// draw time, on the screenshot frame), the material and shader globals are shared, and the matrix
// is identity — yet the enclosed room's band is missing its roofline-adjacent fragments. Identical
// submissions cannot rasterise differently, so some OTHER draw — invisible in colour — must be
// occluding those fragments (depth/stencil), or a layer we have not considered owns them.
//
// This is the layer-level bisect: each seam_skip_* feature flag suppresses one layer type's
// DrawLayer wholesale. The scenario shoots one frame per skip; the skip that makes the enclosed
// band reach the roofline names the occluder. seam_skip_sunshadows is the positive control — it
// must erase the band entirely, proving the mechanism catches the layer that draws it.
public static class SeamSkip
{
    private static readonly HashSet<string> Skipped = new HashSet<string>();

    public static bool ShouldSkip(MapDrawLayer layer) =>
        Skipped.Count > 0 && Skipped.Contains(layer.GetType().Name);

    public static void Set(string typeName, bool on)
    {
        if (on)
            Skipped.Add(typeName);
        else
            Skipped.Remove(typeName);
    }
}

// Base-method prefix: every override that submits through base.DrawLayer (all the candidates —
// SunShadows calls base after RefreshSubMeshBounds; FogOfWar/EdgeShadows/LightingOverlay/our own
// layers don't override at all) passes through here with the runtime type intact.
[HarmonyPatch(typeof(MapDrawLayer), nameof(MapDrawLayer.DrawLayer))]
public static class Patch_SeamSkip
{
    static bool Prefix(MapDrawLayer __instance) => !SeamSkip.ShouldSkip(__instance);
}
