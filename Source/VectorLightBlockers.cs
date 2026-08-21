using System.Collections.Generic;
using RimWorld;
using Verse;

namespace CelestialLighting;

// §27's live half of the occlusion question: which cells around a light stop it, expressed as the
// occluding segments VectorLightMath casts rays at.
//
// THE BLOCKER SET IS VANILLA'S, EXACTLY. `ThingDef.blockLight` on the edifice is the same test
// Verse.Building writes into GlowGrid's own lightBlockers bit array on spawn and despawn
// (Building.cs SpawnSetup/DeSpawn), which is the set vanilla's flood refuses to pass. Issue #48
// names the consequence of getting this wrong: a drawn shadow appearing across a wall that the glow
// grid itself passed straight through, disagreeing with the gameplay light in exactly the places a
// player is looking. Asking the same question of the same grid makes that disagreement impossible
// rather than unlikely.
//
// WHY A WINDOW PER LIGHT. Every ray is tested against every segment handed over, so cost scales with
// how much wall a light is given rather than with how much it can see. On a 250x250 colony a single
// torch handed the whole map would be tested against every wall in the base. The window is the
// light's own reach plus a cell, which for a radius-14 lamp is 31x31 — three hundredths of a percent
// of that map.
public static class VectorLightBlockers
{
    // Occluding segments within one light's reach, in world cell coordinates.
    //
    // The light's OWN cell is deliberately treated as open even when something on it blocks light. A
    // wall-mounted lamp, or a mod's glowing wall, would otherwise be sealed inside its own occluder
    // and light nothing at all — where vanilla's flood simply starts on that cell and spreads out.
    // This is the one place §27 knowingly disagrees with the blocker grid, and it disagrees in the
    // direction that keeps a lit thing lit.
    public static VectorLightMath.Segment[] SegmentsAround(Map map, IntVec3 centre, float radius)
    {
        if (map == null)
            return new VectorLightMath.Segment[0];

        int pad = (int)System.Math.Ceiling(radius) + 1;
        int minX = System.Math.Max(centre.x - pad, 0);
        int minZ = System.Math.Max(centre.z - pad, 0);
        int maxX = System.Math.Min(centre.x + pad, map.Size.x - 1);
        int maxZ = System.Math.Min(centre.z + pad, map.Size.z - 1);

        int width = maxX - minX + 1;
        int height = maxZ - minZ + 1;

        if (width <= 0 || height <= 0)
            return new VectorLightMath.Segment[0];

        bool[] blocked = new bool[width * height];
        List<VectorLightMath.Segment> leaves = null;
        FillWindow(map, centre, minX, minZ, width, height, blocked, ref leaves);

        VectorLightMath.Segment[] silhouette =
            VectorLightMath.SilhouetteSegments(blocked, width, height, minX, minZ);

        if (leaves == null)
        {
            return silhouette;
        }

        // Partly-open doors ride ALONGSIDE the silhouette rather than through it. The grid can only
        // carry whole cells, and Build takes an arbitrary segment array and fires a corner ray at
        // every endpoint it is handed — so a sub-cell occluder needs no new grid concept, just a
        // couple more segments. This is also why the penumbra tracks the leaf edges for free.
        VectorLightMath.Segment[] combined =
            new VectorLightMath.Segment[silhouette.Length + leaves.Count];
        silhouette.CopyTo(combined, 0);
        leaves.CopyTo(combined, silhouette.Length);
        return combined;
    }

    // The two leaf edges of one partly-open door, on both of the faces light can cross.
    //
    // A door in a wall running along Z occludes with its west and east faces, each spanning Z, and
    // its leaves slide along Z — so the split axis and the face's span axis are the same one, on
    // both orientations. That is why this is a single routine with an axis flag rather than two.
    private static void AddDoorLeaves(
        List<VectorLightMath.Segment> into, Building_Door door, IntVec3 cell, float openPct)
    {
        bool alongX = DoorAccess.LeavesSlideAlongX(door);
        float axisMin = alongX ? cell.x : cell.z;

        DoorApertureMath.LeafSpans(
            axisMin, openPct, out float aStart, out float aEnd, out float bStart, out float bEnd);

        // The two faces perpendicular to the direction light crosses the door.
        float faceA = alongX ? cell.z : cell.x;
        float faceB = faceA + 1f;

        AddLeaf(into, alongX, faceA, aStart, aEnd);
        AddLeaf(into, alongX, faceA, bStart, bEnd);
        AddLeaf(into, alongX, faceB, aStart, aEnd);
        AddLeaf(into, alongX, faceB, bStart, bEnd);
    }

    private static void AddLeaf(
        List<VectorLightMath.Segment> into, bool alongX, float face, float start, float end)
    {
        if (!DoorApertureMath.LeafWorthEmitting(start, end))
        {
            return;
        }

        into.Add(alongX
            ? new VectorLightMath.Segment(start, face, end, face)
            : new VectorLightMath.Segment(face, start, face, end));
    }

    private static void FillWindow(
        Map map, IntVec3 centre, int minX, int minZ, int width, int height, bool[] blocked,
        ref List<VectorLightMath.Segment> leaves)
    {
        EdificeGrid edifices = map.edificeGrid;

        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                IntVec3 cell = new IntVec3(minX + x, 0, minZ + z);

                // The light's own cell stays open whatever stands on it — see the type comment. It
                // also contributes no leaves, for the same reason: a lamp built into a door should
                // light, not be shuttered by its own housing.
                if (cell != centre)
                {
                    // Leaves are collected in the SAME pass that fills the grid rather than in a
                    // second sweep: the window is the hot loop, and a part-open door is rare enough
                    // that the list stays null on nearly every bake.
                    //
                    // ONE read of the aperture, shared by both questions, because they have to agree.
                    // Asking separately let a closing door be a whole-cell occluder in the grid while
                    // simultaneously handing out the leaf edges of the gap in it -- two occluders for
                    // one cell, the wider one winning, which is the shape the closing bug took.
                    float openPct = OpenFractionOf(edifices, cell);
                    CollectLeaves(edifices, cell, openPct, ref leaves);
                    blocked[z * width + x] = BlocksLight(edifices, cell, openPct);
                }
            }
        }
    }

    // Appends this cell's leaf edges if it holds a door that is part-way through its slide. A shut
    // door (0) is an ordinary blocker and a fully open one (1) is an ordinary hole; only the interval
    // between them needs sub-cell geometry, which is what keeps this off the common path.
    private static void CollectLeaves(
        EdificeGrid edifices, IntVec3 cell, float openPct,
        ref List<VectorLightMath.Segment> leaves)
    {
        if (openPct <= 0f || openPct >= 1f)
        {
            return;
        }

        leaves = leaves ?? new List<VectorLightMath.Segment>(4);
        AddDoorLeaves(leaves, (Building_Door)edifices[cell], cell, openPct);
    }

    // How far the door on this cell has slid, or 0 for anything that is not a partly-open door under
    // the feature flag. Returns 0 rather than a nullable so the hot loop above stays branch-light.
    //
    // Quantised HERE rather than in the caller, so every consumer of the aperture sees the same
    // stepped value: an unquantised read anywhere would make the field dirty on a tick the geometry
    // did not actually change on, which is precisely the cost the quantisation exists to avoid.
    private static float OpenFractionOf(EdificeGrid edifices, IntVec3 cell)
    {
        if (!CelestialLightingFeatures.VectorLightOpenDoors
            || !CelestialLightingFeatures.VectorLightDoorAperture)
        {
            return 0f;
        }

        Building_Door door = edifices[cell] as Building_Door;
        if (door == null || door.def == null || !door.def.blockLight)
        {
            return 0f;
        }

        return DoorApertureMath.Quantise(
            DoorAccess.OpenFraction(door), DoorApertureMath.DefaultQuantisationSteps);
    }

    // The per-cell occlusion question. The rule itself lives in DoorOcclusionMath so it can be
    // exhausted offline; everything here is the reading of live state that a pure function cannot do.
    //
    // The Building_Door cast is the only new work per cell, and it is a type check on a reference we
    // had already fetched — no grid lookup, no allocation. Building_Door rather than an interface or
    // a def flag because `Open` is where vanilla itself keeps the answer, and every modded door worth
    // supporting derives from it (Steve's Doors' Building_UnmirroredDoor does, so its glass doors and
    // its opaque ones both answer correctly without a compat entry).
    // `openPct` is the same quantised aperture CollectLeaves was handed, and it is what lets a door
    // mid-CLOSE stay a hole in the grid while its leaves slide in: vanilla's `Open` is already false
    // by then. See DoorOcclusionMath for why the two terms are OR-ed rather than one replacing the
    // other, and for the known asymmetry left standing at the start of an open.
    private static bool BlocksLight(EdificeGrid edifices, IntVec3 cell, float openPct)
    {
        Building edifice = edifices[cell];
        if (edifice == null || edifice.def == null)
        {
            return false;
        }

        Building_Door door = edifice as Building_Door;
        return DoorOcclusionMath.Occludes(
            edifice.def.blockLight,
            door != null,
            door != null && door.Open,
            CelestialLightingFeatures.VectorLightOpenDoors,
            openPct);
    }
}
