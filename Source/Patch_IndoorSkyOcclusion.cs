using HarmonyLib;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// §7b indoor sky occlusion — the thin adapter over IndoorOcclusionMath (which carries the full "why"
// for this subsystem, including how vanilla's RoofedAreaMinSkyCover == 100 leaves a sealed cave lit at
// ~61% of the sky). This file only bridges live Map/mesh state: it re-walks the section vanilla just
// baked and raises the lighting mesh's per-vertex sky-cover alpha for roofed cells.
//
// Why a Postfix on Regenerate rather than a transpiler:
//   The vertex alphas are the *output* of vanilla's GenerateLightingOverlay, so rewriting them
//   afterwards needs no knowledge of its IL and cannot desync from it. Regenerate only runs when a
//   section is dirtied (MapMeshFlagDefOf.Roofs or GroundGlow — see the layer's relevantChangeTypes),
//   never per frame, so the extra pass is off the render hot path.
//
// Why Priority.First:
//   Dub's Skylights brackets this same method — its Prefix nulls map.roofGrid for skylit cells so
//   vanilla's roofed branch never fires, and its Postfix puts the roofs back. Running first means we
//   read the roof grid while those cells are still "unroofed", so a skylit room stays sky-lit instead
//   of being blacked out by us. (Its patch was decompiled from the user's installed 1.6 copy to
//   confirm this ordering is sufficient; About.xml already lists Dubwise.DubsSkylights in loadAfter.)
//
// Vertex layout: MakeBaseGeometry lays out (Width+1)*(Height+1) corner vertices in row-major order
// over z-then-x, followed by Width*Height cell-centre vertices in the same order — the indices
// vanilla's own private CalculateVertexIndices computes. We recompute the section's CellRect the same
// way Regenerate does (a Section.Size square at botLeft, clipped to the map) rather than reflecting
// its private cache, and bail out unless the vertex count matches that layout exactly, so an upstream
// mesh change makes this a no-op instead of scribbling on the wrong vertices.
[HarmonyPatch(typeof(SectionLayer_LightingOverlay), nameof(SectionLayer_LightingOverlay.Regenerate))]
public static class Patch_IndoorSkyOcclusion
{
    [HarmonyPriority(Priority.First)]
    static void Postfix(SectionLayer_LightingOverlay __instance)
    {
        if (!CelestialLightingFeatures.IndoorSkyOcclusion)
            return;

        Section section = SectionLayerAccess.GetSection(__instance);
        Map map = section?.map;
        if (map == null)
            return;

        // Biomes that declare disableSkyLighting (the Odyssey undercave) already have no sky
        // contribution at all — vanilla zeroes the whole overlay for them — so there is nothing here
        // to occlude, and touching it would only fight that explicit vanilla contract.
        if (map.Biome != null && map.Biome.disableSkyLighting)
            return;

        LayerSubMesh subMesh = __instance.GetSubMesh(MatBases.LightOverlay);
        Mesh mesh = subMesh?.mesh;
        if (mesh == null)
            return;

        CellRect rect = new CellRect(section.botLeft.x, section.botLeft.z, Section.Size, Section.Size);
        rect.ClipInsideMap(map);

        Color32[] colors = mesh.colors32;
        int firstCenterInd = (rect.Width + 1) * (rect.Height + 1);
        if (colors == null || colors.Length != firstCenterInd + rect.Width * rect.Height)
            return;

        IndoorOcclusionSettings settings = IndoorOcclusionSettings.Current;
        RewriteCentres(map, rect, colors, firstCenterInd, settings);
        RewriteCorners(map, rect, colors, settings);
        mesh.colors32 = colors;
    }

    // One centre vertex per cell, and the cell's own roof state decides it outright. The centre
    // dominates how the cell reads on screen (the quad is four triangles fanning from it), so this is
    // what actually turns a sealed cave black.
    private static void RewriteCentres(
        Map map, CellRect rect, Color32[] colors, int firstCenterInd, IndoorOcclusionSettings settings)
    {
        for (int z = rect.minZ; z <= rect.maxZ; z++)
        {
            for (int x = rect.minX; x <= rect.maxX; x++)
            {
                int vertex = firstCenterInd + (z - rect.minZ) * rect.Width + (x - rect.minX);
                float occlusion = CellOcclusion(map, new IntVec3(x, 0, z), settings);
                colors[vertex].a = IndoorOcclusionMath.CoverAlpha(occlusion, colors[vertex].a);
            }
        }
    }

    // Corner vertices span one more row/column than there are cells, and each is shared by the (up
    // to) four cells touching it — averaging them is what fades the interior blackness out across the
    // wall line instead of printing a hard black edge on the ground outside.
    private static void RewriteCorners(Map map, CellRect rect, Color32[] colors, IndoorOcclusionSettings settings)
    {
        int stride = rect.Width + 1;
        for (int z = rect.minZ; z <= rect.maxZ + 1; z++)
        {
            for (int x = rect.minX; x <= rect.maxX + 1; x++)
            {
                int vertex = (z - rect.minZ) * stride + (x - rect.minX);
                float occlusion = CornerOcclusion(map, x, z, settings);
                colors[vertex].a = IndoorOcclusionMath.CoverAlpha(occlusion, colors[vertex].a);
            }
        }
    }

    // The four cells that meet at lattice point (x, z) — the same neighbour set vanilla averages glow
    // over when it bakes this vertex. Cells outside the map are skipped and drop out of the mean, so a
    // corner on the map edge is judged only by the cells that actually exist.
    private static float CornerOcclusion(Map map, int x, int z, IndoorOcclusionSettings settings)
    {
        float sum = 0f;
        int valid = 0;
        for (int i = 0; i < 4; i++)
        {
            IntVec3 cell = new IntVec3(x - (i & 1), 0, z - (i >> 1));
            if (cell.InBounds(map))
            {
                sum += CellOcclusion(map, cell, settings);
                valid++;
            }
        }

        return IndoorOcclusionMath.CornerOcclusion(sum, valid);
    }

    // Live-state lookup for one cell, handed straight to the pure core. The door test mirrors
    // vanilla's own (SectionLayer_LightingOverlay identifies doors by AltitudeLayer.DoorMoveable, not
    // by type) so our notion of "this is a doorway" can never drift from the cell vanilla already
    // treats specially.
    private static float CellOcclusion(Map map, IntVec3 cell, IndoorOcclusionSettings settings)
    {
        bool roofed = map.roofGrid.Roofed(cell);
        Building edifice = map.edificeGrid[cell];
        bool isDoor = edifice != null && edifice.def.altitudeLayer == AltitudeLayer.DoorMoveable;

        float occlusion = IndoorOcclusionMath.CellOcclusion(roofed, isDoor, settings.DoorSkyLeak);
        return IndoorOcclusionMath.CapOcclusion(occlusion, settings.IndoorFloor);
    }
}
