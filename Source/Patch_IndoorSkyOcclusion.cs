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
// timings. Measured at §16, this postfix added 95 µs per section regenerate against the 63 µs
// vanilla's whole lighting overlay takes, i.e. it cost 2.5x the method it postfixes, and it is the
// second largest term in what the mod adds to a roof edit. Also worth knowing before reasoning about
// how often this runs: GlowGrid.DirtyCell raises Roofs as well as GroundGlow, so every lamp toggle
// re-runs this pass over the sections it covers.
//
// That 95 µs then drifted to 790 µs and nobody noticed for a month, which is the more useful lesson
// than either number. §7c/§7d added a per-read sky-falloff lookup to both passes below and left it
// wired to the live map, so the 1,585 lattice reads a section takes were each doing two glow-grid
// reads — one of them GroundGlowAt, the patched surface every interop mod postfixes. Circinus caught
// it at 7.38% of frame, 2.0 calls/frame, 790 µs/call, worst frame 95.92 ms (a whole-map GroundGlow
// change re-baking most sections at once). The fix was to bake that verdict into SkyOcclusionWindow
// alongside blocksSky, where everything else a pass reads per cell already lived. Anything added to
// these passes later belongs in the window too; the comment above claiming this runs "never per
// frame" is what made the drift easy to miss, and it is only true of a colony with no lit torches.
//
// Measured after the fix (Tests/Scenarios/perf_indoor_occlusion.json, 7 interleaved runs per build,
// 1,120 postfix calls each): 729.6 -> 238.9 µs/call median, 3.05x, and the two arms do not overlap —
// the slowest memo run is still 2.3x faster than the fastest main one. The whole vanilla bake this
// sits inside went 814 -> 310 µs/call, i.e. the postfix was 90% of it and is now 77%. Interleave the
// arms if you re-measure: two runs of ONE build came out 883 ms and 2151 ms on this machine, so a
// block design would hand whichever arm ran in the quiet half a win it did not earn.
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
//   query, then later a pair of glow-grid reads as well. Resolving the section's cells plus their
//   one-cell skirt once, up front, is the same trade §15's EaveShadowGrid already makes for the same
//   reason; the window carries the arithmetic.
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

        float indoorFloor = ResolveIndoorFloor(map);
        SkyOcclusionWindow window = BuildWindow(map, rect);
        float[] corners = BuildCornerOcclusion(window, rect, indoorFloor);
        WriteCorners(colors, corners);
        WriteCentres(window, rect, colors, firstCenterInd, corners, indoorFloor);
        mesh.colors32 = colors;
    }

    // The floor CapOcclusion should actually cap at, resolved ONCE per section rather than at each of the
    // ~1,700 lattice reads below — it is map-wide, and NightOverlayKeep reaches the sky manager, the ozone
    // band and the game-condition list to answer. Same reasoning as the window itself.
    //
    // The division is what stops MinIndoorBrightness and MinNightBrightness compounding; see
    // IndoorOcclusionMath.EffectiveIndoorFloor for why they were in different units to begin with and what
    // saturates when the sky cannot deliver the floor. With the flag off the raw setting goes through
    // untouched, which is the pre-feature formula exactly.
    private static float ResolveIndoorFloor(Map map)
    {
        float minIndoorBrightness = IndoorOcclusionSettings.Current.MinIndoorBrightness;
        if (!CelestialLightingFeatures.DecoupledIndoorFloor)
            return minIndoorBrightness;

        return IndoorOcclusionMath.EffectiveIndoorFloor(minIndoorBrightness, NightOverlayKeep.For(map));
    }

    // Resolves every cell the two passes below can look at — this section plus the one-cell skirt its
    // boundary lattice points reach into — exactly once. This is the only place in the postfix that
    // touches the live map.
    //
    // The falloff reader is built once here rather than per cell, which is the second half of the same
    // idea the window itself is: SkyFalloffSource.FractionAt re-read two feature flags, the owner
    // check, the settings object and CurSkyGlow on every one of the 361 calls, and reached the grid
    // through a path that null-checked, bounds-checked and index-computed twice per call. Measured,
    // that ceremony was 109.1 µs of this postfix's 228 — several times what the two array reads it
    // guarded actually cost. See NativeSkyFalloffGrid.Reader.
    private static SkyOcclusionWindow BuildWindow(Map map, CellRect rect)
    {
        SkyOcclusionWindow window = SkyOcclusionWindow.ForSection(
            rect.minX, rect.minZ, rect.maxX, rect.maxZ, map.Size.x, map.Size.z);
        SkyFalloffSource.SectionReader falloff = SkyFalloffSource.ForSection(map);

        for (int z = window.MinZ; z <= window.MaxZ; z++)
        {
            for (int x = window.MinX; x <= window.MaxX; x++)
                ResolveCell(map, window, in falloff, x, z);
        }

        return window;
    }

    // The lattice: one more row and column than there are cells, in the same row-major order as the
    // mesh's leading corner vertices, so index i here is vertex i there.
    private static float[] BuildCornerOcclusion(
        SkyOcclusionWindow window, CellRect rect, float indoorFloor)
    {
        int stride = rect.Width + 1;
        float[] corners = new float[stride * (rect.Height + 1)];
        for (int z = rect.minZ; z <= rect.maxZ + 1; z++)
        {
            for (int x = rect.minX; x <= rect.maxX + 1; x++)
            {
                corners[(z - rect.minZ) * stride + (x - rect.minX)] = CornerOcclusion(window, x, z, indoorFloor);
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
        SkyOcclusionWindow window, CellRect rect, Color32[] colors, int firstCenterInd, float[] corners,
        float indoorFloor)
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

                float skyFalloffFraction = window.SkyFalloffFraction(x, z);
                int vertex = firstCenterInd + (z - rect.minZ) * rect.Width + (x - rect.minX);
                colors[vertex].a = IndoorOcclusionMath.CoverAlpha(
                    IndoorOcclusionMath.CapOcclusion(occlusion, indoorFloor, skyFalloffFraction),
                    colors[vertex].a);
            }
        }
    }

    // The four cells that meet at lattice point (x, z) — the same neighbour set vanilla scans when it
    // bakes this vertex. Cells outside the map contribute nothing: they are neither interior nor a
    // door, so an edge corner is judged only by the cells that actually exist, and the two sections
    // sharing a boundary vertex compute an identical value (no seam). The explicit InBounds guard
    // this loop used to carry now lives in the window, which answers for an off-map cell with exactly
    // the `false` those two ORs were already contributing — and, since the falloff term joined the
    // window, with exactly the 0f that term's own guard was substituting.
    private static float CornerOcclusion(
        SkyOcclusionWindow window, int x, int z, float indoorFloor)
    {
        bool anyBlocksSky = false;
        // A corner is shared by up to four cells, and — unlike anyBlocksSky, which is an OR because one
        // blocked neighbour is enough to occlude — the sky-falloff term takes their MAX: whichever of
        // the four cells currently has the most sky reaching it is the floor this corner should honour,
        // the same "more generous floor wins" reasoning IndoorOcclusionMath.CapOcclusion's own doc
        // comment gives for composing it with MinIndoorBrightness.
        float skyFalloffFraction = 0f;
        for (int i = 0; i < 4; i++)
        {
            int cellX = x - (i & 1);
            int cellZ = z - (i >> 1);
            anyBlocksSky |= window.BlocksSky(cellX, cellZ);
            skyFalloffFraction = Mathf.Max(skyFalloffFraction, window.SkyFalloffFraction(cellX, cellZ));
        }

        float occlusion = IndoorOcclusionMath.CornerOcclusion(anyBlocksSky);
        return IndoorOcclusionMath.CapOcclusion(occlusion, indoorFloor, skyFalloffFraction);
    }

    // Live-state lookup for one cell, baked into the window and never repeated. Reads the roof *def*
    // rather than the Roofed() bool because thick roof (a mountain) is one of the inputs — paired with
    // natural rock it is what tells unmined stone, which is buried, apart from a built wall standing
    // under a mined-out mountain roof, which is not (#129; BlocksSky carries why they differ).
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
    //
    // The sky-falloff fraction is fetched here too, once, rather than at each of the five reads that
    // want it. Safe as a memo because SkyFalloffSource.FractionAt is a pure function of (map, cell)
    // for the duration of one Regenerate: its inputs are the glow grid, the BFS grid behind a dirty
    // flag, and CurSkyGlow, none of which anything between this fill loop and the last vertex write
    // touches — the per-read version was reading the same value five times, not a fresher one.
    // No InBounds guard needed on it (the deleted SkyFalloffFractionAt carried one for
    // IndoorGlowPassthrough's direct glow-grid indexing) — ForSection has already clipped the window,
    // so every cell this loop visits is on the map by construction.
    private static void ResolveCell(
        Map map, SkyOcclusionWindow window, in SkyFalloffSource.SectionReader falloff, int x, int z)
    {
        IntVec3 cell = new IntVec3(x, 0, z);
        RoofDef roof = map.roofGrid.RoofAt(cell);
        Building edifice = map.edificeGrid[cell];
        bool isDoor = IsDoor(edifice);
        bool holdsRoof = edifice != null && edifice.def.holdsRoof;

        bool blocksSky = IndoorOcclusionMath.BlocksSky(
            EaveCells.Encloses(map, cell, roof), roof != null && roof.isThickRoof, holdsRoof, isDoor,
            NaturalRock(edifice));

        window.Resolve(x, z, blocksSky, falloff.FractionAt(cell));
    }

    // Unmined stone: the game's own flag for it, set on RockBase and so inherited by every vanilla
    // rock and ore alike (the ore defs only add isResourceRock on top, which is why that one is not
    // tested here). Modded rock that forgets the flag reads as a wall and gets the ramp rather than
    // the blackout — the safe way round, since the failure is then a stone face a shade too light
    // rather than a wall ring swallowed whole. CollapsedRocks sets it false deliberately and is
    // likewise treated as the debris pile it is.
    private static bool NaturalRock(Building edifice) =>
        edifice != null && edifice.def.building != null && edifice.def.building.isNaturalRock;

    // Mirrors vanilla's own door test — SectionLayer_LightingOverlay identifies doors by
    // AltitudeLayer.DoorMoveable, not by type — so our notion of "this is a doorway" can never drift
    // from the cell vanilla already treats specially.
    private static bool IsDoor(Building edifice) =>
        edifice != null && edifice.def.altitudeLayer == AltitudeLayer.DoorMoveable;
}
