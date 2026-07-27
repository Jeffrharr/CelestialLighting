using HarmonyLib;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// §7b indoor sky occlusion — the thin adapter over IndoorOcclusionMath (which carries the full "why"
// for this subsystem, including how vanilla's RoofedAreaMinSkyCover == 100 leaves a sealed cave lit at
// ~61% of the sky). This file only bridges live Map/mesh state: it re-walks the section vanilla just
// baked and raises the lighting mesh's per-vertex sky-cover alpha for interior cells.
//
// Why a Postfix on Regenerate rather than a transpiler:
//   The vertex alphas are the *output* of vanilla's GenerateLightingOverlay, so rewriting them
//   afterwards needs no knowledge of its IL and cannot desync from it. Regenerate only runs when a
//   section is dirtied (MapMeshFlagDefOf.Roofs or GroundGlow — see the layer's relevantChangeTypes),
//   never per frame, so the extra pass is off the render hot path.
//
// FAN-OUT. "Off the render hot path" is true and is not the same as cheap. This is one of four
// separate places that add work to a map-mesh dirty flag (the other three are
// SectionLayer_NightDesaturation, SectionLayer_EaveShade and Patch_ShadowRoofInvalidation), and no
// one of the four can show the total — DESIGN.md §16 has the flag-to-layers table and the live
// timings. Measured, this postfix adds 95 µs per section regenerate against the 63 µs vanilla's
// whole lighting overlay takes, i.e. it costs 2.5x the method it postfixes, and it is the second
// largest term in what the mod adds to a roof edit. Also worth knowing before reasoning about how
// often this runs: GlowGrid.DirtyCell raises Roofs as well as GroundGlow, so every lamp toggle
// re-runs this pass over the sections it covers.
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
//
// Two passes in a fixed order, mirroring vanilla's: corners first, then centres — because an
// *uncovered* cell's centre is the mean of its own four corners, so the corner values have to exist
// before any centre can be resolved. They are kept in a local float array rather than read back out of
// `colors`, since what lands in `colors` is already max()'d against whatever vanilla (or another mod)
// baked, and averaging those would fold someone else's decision into our geometry.
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
        float[] corners = BuildCornerOcclusion(map, rect, settings);
        WriteCorners(colors, corners);
        WriteCentres(map, rect, colors, firstCenterInd, corners, settings);
        mesh.colors32 = colors;
    }

    // The lattice: one more row and column than there are cells, in the same row-major order as the
    // mesh's leading corner vertices, so index i here is vertex i there.
    private static float[] BuildCornerOcclusion(Map map, CellRect rect, IndoorOcclusionSettings settings)
    {
        int stride = rect.Width + 1;
        float[] corners = new float[stride * (rect.Height + 1)];
        for (int z = rect.minZ; z <= rect.maxZ + 1; z++)
        {
            for (int x = rect.minX; x <= rect.maxX + 1; x++)
            {
                corners[(z - rect.minZ) * stride + (x - rect.minX)] = CornerOcclusion(map, x, z, settings);
            }
        }

        return corners;
    }

    private static void WriteCorners(Color32[] colors, float[] corners)
    {
        for (int i = 0; i < corners.Length; i++)
            colors[i].a = IndoorOcclusionMath.CoverAlpha(corners[i], colors[i].a);
    }

    // One centre vertex per cell. An interior cell is fully occluded outright; a boundary cell (wall,
    // door) or open ground takes the mean of the four corners computed above, which is what turns the
    // wall line into a straight ramp instead of a per-tile starburst. The four corner indices are the
    // same neighbourhood vanilla's own centre pass averages: (x,z), (x+1,z), (x,z+1), (x+1,z+1).
    private static void WriteCentres(
        Map map, CellRect rect, Color32[] colors, int firstCenterInd, float[] corners,
        IndoorOcclusionSettings settings)
    {
        int stride = rect.Width + 1;
        for (int z = rect.minZ; z <= rect.maxZ; z++)
        {
            for (int x = rect.minX; x <= rect.maxX; x++)
            {
                int corner = (z - rect.minZ) * stride + (x - rect.minX);
                float cornerSum = corners[corner] + corners[corner + 1]
                    + corners[corner + stride] + corners[corner + stride + 1];

                bool blocksSky = BlocksSky(map, new IntVec3(x, 0, z));
                float occlusion = IndoorOcclusionMath.CentreOcclusion(blocksSky, cornerSum);

                int vertex = firstCenterInd + (z - rect.minZ) * rect.Width + (x - rect.minX);
                colors[vertex].a = IndoorOcclusionMath.CoverAlpha(
                    IndoorOcclusionMath.CapOcclusion(occlusion, settings.MinIndoorBrightness), colors[vertex].a);
            }
        }
    }

    // The four cells that meet at lattice point (x, z) — the same neighbour set vanilla scans when it
    // bakes this vertex. Cells outside the map contribute nothing: they are neither interior nor a
    // door, so an edge corner is judged only by the cells that actually exist, and the two sections
    // sharing a boundary vertex compute an identical value (no seam).
    private static float CornerOcclusion(Map map, int x, int z, IndoorOcclusionSettings settings)
    {
        bool anyBlocksSky = false;
        bool touchesDoor = false;
        for (int i = 0; i < 4; i++)
        {
            IntVec3 cell = new IntVec3(x - (i & 1), 0, z - (i >> 1));
            if (cell.InBounds(map))
            {
                anyBlocksSky |= BlocksSky(map, cell);
                touchesDoor |= IsDoor(map.edificeGrid[cell]);
            }
        }

        float occlusion = IndoorOcclusionMath.CornerOcclusion(anyBlocksSky, touchesDoor, settings.DoorSkyLeak);
        return IndoorOcclusionMath.CapOcclusion(occlusion, settings.MinIndoorBrightness);
    }

    // Live-state lookup for one cell, handed straight to the pure core. Reads the roof *def* rather
    // than the Roofed() bool because thick roof (a mountain) is one of the inputs — under it even a
    // wall counts as buried, which is vanilla's own exception too.
    //
    // The roof an EAVE carries (§15: roofed, but part of a room that breathes outdoor air — a porch,
    // a lean-to, the overhang that oversails a wall) is not passed on as "roofed" at all. Blacking a
    // porch out at noon while it stood open to the sky on three sides was this feature's most
    // conspicuous artifact (issue #33), and Room.UsesOutdoorTemperature is the game's own test for
    // the distinction — sharing it with §15's shadow half means the two halves cannot disagree about
    // which cells are inside. Deliberately NOT gated on CelestialLightingFeatures.EaveShadows: that
    // flag turns a new *effect* on and off, whereas this is a correction to a question §7b was
    // already asking wrongly.
    private static bool BlocksSky(Map map, IntVec3 cell)
    {
        RoofDef roof = map.roofGrid.RoofAt(cell);
        Building edifice = map.edificeGrid[cell];
        bool holdsRoof = edifice != null && edifice.def.holdsRoof;

        return IndoorOcclusionMath.BlocksSky(
            EaveCells.Encloses(map, cell, roof), roof != null && roof.isThickRoof, holdsRoof, IsDoor(edifice));
    }

    // Mirrors vanilla's own door test — SectionLayer_LightingOverlay identifies doors by
    // AltitudeLayer.DoorMoveable, not by type — so our notion of "this is a doorway" can never drift
    // from the cell vanilla already treats specially.
    private static bool IsDoor(Building edifice) =>
        edifice != null && edifice.def.altitudeLayer == AltitudeLayer.DoorMoveable;
}
