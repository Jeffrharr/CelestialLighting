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
// Why the cell verdicts are baked into a window first (SkyOcclusionWindow):
//   The two passes below read each cell up to five times — four times as a neighbour of one of the
//   corner lattice points it meets at, once as its own centre — and each read used to reach a Room
//   query. Resolving the section's cells plus their one-cell skirt once, up front, is the same trade
//   §15's EaveShadowGrid already makes for the same reason; the window carries the arithmetic.
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

        // Biomes that declare disableSkyLighting already have no sky
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
        SkyOcclusionWindow window = BuildWindow(map, rect);
        float[] corners = BuildCornerOcclusion(map, window, rect, settings);
        WriteCorners(colors, corners);
        WriteCentres(map, window, rect, colors, firstCenterInd, corners, settings);
        mesh.colors32 = colors;
    }

    // Resolves every cell the two passes below can look at — this section plus the one-cell skirt its
    // boundary lattice points reach into — exactly once. This is the only place in the postfix that
    // touches the live map.
    private static SkyOcclusionWindow BuildWindow(Map map, CellRect rect)
    {
        SkyOcclusionWindow window = SkyOcclusionWindow.ForSection(
            rect.minX, rect.minZ, rect.maxX, rect.maxZ, map.Size.x, map.Size.z);

        for (int z = window.MinZ; z <= window.MaxZ; z++)
        {
            for (int x = window.MinX; x <= window.MaxX; x++)
                ResolveCell(map, window, x, z);
        }

        return window;
    }

    // The lattice: one more row and column than there are cells, in the same row-major order as the
    // mesh's leading corner vertices, so index i here is vertex i there.
    private static float[] BuildCornerOcclusion(
        Map map, SkyOcclusionWindow window, CellRect rect, IndoorOcclusionSettings settings)
    {
        int stride = rect.Width + 1;
        float[] corners = new float[stride * (rect.Height + 1)];
        for (int z = rect.minZ; z <= rect.maxZ + 1; z++)
        {
            for (int x = rect.minX; x <= rect.maxX + 1; x++)
            {
                corners[(z - rect.minZ) * stride + (x - rect.minX)] = CornerOcclusion(map, window, x, z, settings);
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
        Map map, SkyOcclusionWindow window, CellRect rect, Color32[] colors, int firstCenterInd, float[] corners,
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

                float occlusion = IndoorOcclusionMath.CentreOcclusion(window.BlocksSky(x, z), cornerSum);

                float ambientLightSkyFraction = AmbientLightCompat.SkyFractionAt(map, new IntVec3(x, 0, z));
                int vertex = firstCenterInd + (z - rect.minZ) * rect.Width + (x - rect.minX);
                colors[vertex].a = IndoorOcclusionMath.CoverAlpha(
                    IndoorOcclusionMath.CapOcclusion(occlusion, settings.MinIndoorBrightness, ambientLightSkyFraction),
                    colors[vertex].a);
            }
        }
    }

    // The four cells that meet at lattice point (x, z) — the same neighbour set vanilla scans when it
    // bakes this vertex. Cells outside the map contribute nothing: they are neither interior nor a
    // door, so an edge corner is judged only by the cells that actually exist, and the two sections
    // sharing a boundary vertex compute an identical value (no seam). The explicit InBounds guard
    // this loop used to carry now lives in the window, which answers for an off-map cell with exactly
    // the `false` those two ORs were already contributing.
    private static float CornerOcclusion(
        Map map, SkyOcclusionWindow window, int x, int z, IndoorOcclusionSettings settings)
    {
        bool anyBlocksSky = false;
        bool touchesDoor = false;
        // A corner is shared by up to four cells, and — unlike anyBlocksSky, which is an OR because one
        // blocked neighbour is enough to occlude — the Ambient Light term takes their MAX: whichever of
        // the four cells currently has the most sky reaching it is the floor this corner should honour,
        // the same "more generous floor wins" reasoning IndoorOcclusionMath.CapOcclusion's own doc
        // comment gives for composing it with MinIndoorBrightness.
        float ambientLightSkyFraction = 0f;
        for (int i = 0; i < 4; i++)
        {
            int cellX = x - (i & 1);
            int cellZ = z - (i >> 1);
            anyBlocksSky |= window.BlocksSky(cellX, cellZ);
            touchesDoor |= window.IsDoor(cellX, cellZ);
            ambientLightSkyFraction = Mathf.Max(ambientLightSkyFraction, AmbientLightFractionAt(map, cellX, cellZ));
        }

        float occlusion = IndoorOcclusionMath.CornerOcclusion(anyBlocksSky, touchesDoor, settings.DoorSkyLeak);
        return IndoorOcclusionMath.CapOcclusion(occlusion, settings.MinIndoorBrightness, ambientLightSkyFraction);
    }

    // AmbientLightCompat.SkyFractionAt assumes an in-bounds cell (it indexes straight into Ambient
    // Light's own per-map depth array) — the window's BlocksSky/IsDoor already answer `false` for an
    // off-map neighbour without needing this guard, but a corner on the map edge still asks about a
    // cell one step past it, so this checks InBounds itself rather than pushing that contract onto
    // AmbientLightCompat.
    private static float AmbientLightFractionAt(Map map, int x, int z)
    {
        IntVec3 cell = new IntVec3(x, 0, z);
        return cell.InBounds(map) ? AmbientLightCompat.SkyFractionAt(map, cell) : 0f;
    }

    // Live-state lookup for one cell, baked into the window and never repeated. Reads the roof *def*
    // rather than the Roofed() bool because thick roof (a mountain) is one of the inputs — under it
    // even a wall counts as buried, which is vanilla's own exception too.
    //
    // The roof an EAVE carries (§15: roofed, but part of a room that breathes outdoor air — a porch,
    // a lean-to, the overhang that oversails a wall) is not passed on as "roofed" at all. Blacking a
    // porch out at noon while it stood open to the sky on three sides was this feature's most
    // conspicuous artifact, and Room.UsesOutdoorTemperature is the game's own test for
    // the distinction — sharing it with §15's shadow half means the two halves cannot disagree about
    // which cells are inside. Deliberately NOT gated on CelestialLightingFeatures.EaveShadows: that
    // flag turns a new *effect* on and off, whereas this is a correction to a question §7b was
    // already asking wrongly.
    //
    // EaveCells.Encloses short-circuits on the roof grid before it reaches the room query, which is
    // the expensive half — the overwhelming majority of cells on any map are unroofed, and baking
    // this window resolves exactly the cells the lattice was already resolving (no more), so no cell
    // that used to exit early is now forced through a full resolution.
    private static void ResolveCell(Map map, SkyOcclusionWindow window, int x, int z)
    {
        IntVec3 cell = new IntVec3(x, 0, z);
        RoofDef roof = map.roofGrid.RoofAt(cell);
        Building edifice = map.edificeGrid[cell];
        bool isDoor = IsDoor(edifice);
        bool holdsRoof = edifice != null && edifice.def.holdsRoof;

        bool blocksSky = IndoorOcclusionMath.BlocksSky(
            EaveCells.Encloses(map, cell, roof), roof != null && roof.isThickRoof, holdsRoof, isDoor);

        window.Resolve(x, z, blocksSky, isDoor);
    }

    // Mirrors vanilla's own door test — SectionLayer_LightingOverlay identifies doors by
    // AltitudeLayer.DoorMoveable, not by type — so our notion of "this is a doorway" can never drift
    // from the cell vanilla already treats specially.
    private static bool IsDoor(Building edifice) =>
        edifice != null && edifice.def.altitudeLayer == AltitudeLayer.DoorMoveable;
}
