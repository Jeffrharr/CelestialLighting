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
//
// THAT WINDOW IS ALSO THE COST, and it is paid in full every time anything asks for a fresh occluder
// set: 961 cells and a silhouette extraction over two edge grids the same size. Issue #188 item C is
// the observation that one door swing asks nine times and gets the same wall on eight of them, so
// the assembly is split — this file reads live state, and VectorLightSilhouetteMath decides when the
// previous read is still the answer and turns either one into segments. The field hands in the
// emitter's memo; the probes hand in null, because a probe asking what the map says must not be
// answered by a cache.
public static class VectorLightBlockers
{
    // Doors found by the current scan, and doors re-read from a memo. Two lists rather than one
    // because the reuse test compares them against each other, and static rather than per-call
    // because the gather runs serially on the calling thread — see VectorLightField.BakeSelected for
    // the argument that makes that true, and note that it is an argument about the CALLER.
    private static readonly List<VectorLightSilhouetteMath.Door> ScanDoors =
        new List<VectorLightSilhouetteMath.Door>(4);

    private static readonly List<VectorLightSilhouetteMath.Door> FreshDoors =
        new List<VectorLightSilhouetteMath.Door>(4);

    private static readonly List<VectorLightMath.Segment> LeafScratch =
        new List<VectorLightMath.Segment>(8);

    // How often the recorded silhouette answered, and how often the window had to be rescanned.
    //
    // A RATIO, NOT A COUNT, is what these are for. A bake count cannot tell a working memo from a
    // scene where nothing ever reuses one, and a timing probe cannot either — it measures a call and
    // never asks how many of them there were. Read beside VectorLightField.PolygonBakes: hits plus
    // rebuilds is the number of times an occluder set was assembled at all.
    public static int SilhouetteHits;
    public static int SilhouetteRebuilds;

    public static void ResetCounters()
    {
        SilhouetteHits = 0;
        SilhouetteRebuilds = 0;
    }

    // Occluding segments within one light's reach, in world cell coordinates.
    //
    // The light's OWN cell is deliberately treated as open even when something on it blocks light. A
    // wall-mounted lamp, or a mod's glowing wall, would otherwise be sealed inside its own occluder
    // and light nothing at all — where vanilla's flood simply starts on that cell and spreads out.
    // This is the one place §27 knowingly disagrees with the blocker grid, and it disagrees in the
    // direction that keeps a lit thing lit.
    //
    // `memo` may be null, and passing null is a full rescan. It is a REQUIRED parameter rather than
    // an optional one, and this method is deliberately NOT an overload pair, because the profiler
    // arms it BY NAME: `circ_vlsegments` resolves CelestialLighting.VectorLightBlockers:SegmentsAround
    // through AccessTools, and two methods of that name make the arm throw "Ambiguous match" at
    // scenario time. That was not hypothetical -- the first cut of the memo added a four-argument
    // overload beside the three-argument one and took the arm out of the bake storm, which is the
    // scenario that exists to watch this exact call.
    //
    // BYTE-IDENTICAL WITH AND WITHOUT A MEMO BY CONSTRUCTION rather than by luck: both paths end in
    // VectorLightSilhouetteMath.Assemble over the same door records in the same window scan order,
    // and the only thing the memo supplies is a silhouette array a rescan would have rebuilt element
    // for element. VectorLightSilhouetteMathTests pins that over a whole nine-step swing against an
    // oracle that rescans every step.
    public static VectorLightMath.Segment[] SegmentsAround(
        Map map, IntVec3 centre, float radius, VectorLightSilhouetteMath.Memo memo)
    {
        if (map == null)
            return new VectorLightMath.Segment[0];

        if (CanReuse(map, centre, radius, memo))
        {
            SilhouetteHits++;
            return VectorLightSilhouetteMath.Assemble(memo.Silhouette, FreshDoors, LeafScratch);
        }

        SilhouetteRebuilds++;
        return Rescan(map, centre, radius, memo);
    }

    // Walks the window, extracts the silhouette, and leaves the memo describing what it found.
    private static VectorLightMath.Segment[] Rescan(
        Map map, IntVec3 centre, float radius, VectorLightSilhouetteMath.Memo memo)
    {
        int pad = (int)System.Math.Ceiling(radius) + 1;
        int minX = System.Math.Max(centre.x - pad, 0);
        int minZ = System.Math.Max(centre.z - pad, 0);
        int maxX = System.Math.Min(centre.x + pad, map.Size.x - 1);
        int maxZ = System.Math.Min(centre.z + pad, map.Size.z - 1);

        int width = maxX - minX + 1;
        int height = maxZ - minZ + 1;

        if (width <= 0 || height <= 0)
        {
            Forget(memo);
            return new VectorLightMath.Segment[0];
        }

        bool[] blocked = new bool[width * height];
        ScanDoors.Clear();
        FillWindow(map, centre, minX, minZ, width, height, blocked, ScanDoors);

        VectorLightMath.Segment[] segments = VectorLightSilhouetteMath.Build(
            blocked, width, height, minX, minZ, ScanDoors, LeafScratch,
            out VectorLightMath.Segment[] silhouette);

        Record(memo, centre, radius, silhouette);
        return segments;
    }

    // Whether the memo still describes the world, and — as a side effect — leaves FreshDoors holding
    // this bake's live reading of each recorded door, ready for the leaf assembly.
    //
    // THE SIDE EFFECT IS THE POINT rather than a shortcut. The apertures have to be read live
    // whatever the answer turns out to be, because they are what moves; reading them once and using
    // them for both the comparison and the leaves is what keeps the reused path at one edifice
    // lookup per door instead of one per cell of the window.
    private static bool CanReuse(
        Map map, IntVec3 centre, float radius, VectorLightSilhouetteMath.Memo memo)
    {
        if (!CelestialLightingFeatures.VectorLightSilhouetteCache)
        {
            return false;
        }

        if (!VectorLightSilhouetteMath.CoversWindow(memo, centre.x, centre.z, radius))
        {
            return false;
        }

        EdificeGrid edifices = map.edificeGrid;
        FreshDoors.Clear();

        for (int i = 0; i < memo.Doors.Count; i++)
        {
            IntVec3 cell = new IntVec3(memo.Doors[i].X, 0, memo.Doors[i].Z);
            ReadCell(edifices[cell], cell, FreshDoors);
        }

        return VectorLightSilhouetteMath.OcclusionUnchanged(memo, FreshDoors);
    }

    private static void Record(
        VectorLightSilhouetteMath.Memo memo, IntVec3 centre, float radius,
        VectorLightMath.Segment[] silhouette)
    {
        if (memo == null)
        {
            return;
        }

        memo.Silhouette = silhouette;
        memo.CentreX = centre.x;
        memo.CentreZ = centre.z;
        memo.Radius = radius;
        memo.Doors.Clear();
        memo.Doors.AddRange(ScanDoors);
        memo.Valid = true;
    }

    // A window that clamped away to nothing tells us nothing about the world, so the memo is left
    // holding no claim rather than an empty one.
    private static void Forget(VectorLightSilhouetteMath.Memo memo)
    {
        if (memo != null)
        {
            memo.Invalidate();
        }
    }

    private static void FillWindow(
        Map map, IntVec3 centre, int minX, int minZ, int width, int height, bool[] blocked,
        List<VectorLightSilhouetteMath.Door> doors)
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
                    // Doors are recorded in the SAME pass that fills the grid rather than in a second
                    // sweep: the window is the hot loop, and a door is rare enough that the list
                    // stays empty on nearly every bake.
                    blocked[z * width + x] = ReadCell(edifices[cell], cell, doors);
                }
            }
        }
    }

    // One cell, answered once: whether it occludes, and — if it is a light-blocking door — a record
    // of it appended to `doors`.
    //
    // ONE edifice lookup and ONE read of the aperture, shared by both questions, because they have to
    // agree. Asking separately let a closing door be a whole-cell occluder in the grid while
    // simultaneously handing out the leaf edges of the gap in it — two occluders for one cell, the
    // wider one winning, which is the shape the closing bug took. It is also half the grid lookups
    // the previous shape made: the window loop asked the edifice grid twice per cell, once for the
    // aperture and once for the occlusion.
    //
    // EVERY light-blocking door is recorded, not only the ones caught mid-slide. A shut door and an
    // open one both produce no leaves and DIFFERENT grids, so the memo has to be able to notice one
    // becoming the other; recording only the mid-slide ones would hold a silhouette across the very
    // transition that invalidates it.
    private static bool ReadCell(
        Building edifice, IntVec3 cell, List<VectorLightSilhouetteMath.Door> doors)
    {
        if (edifice == null || edifice.def == null)
        {
            return false;
        }

        // The Building_Door cast is the only new work per cell, and it is a type check on a reference
        // we had already fetched — no grid lookup, no allocation. Building_Door rather than an
        // interface or a def flag because `Open` is where vanilla itself keeps the answer, and every
        // modded door worth supporting derives from it (Steve's Doors' Building_UnmirroredDoor does,
        // so its glass doors and its opaque ones both answer correctly without a compat entry).
        Building_Door door = edifice as Building_Door;
        float openPct = ApertureOf(door, edifice.def.blockLight);

        // The rule itself lives in DoorOcclusionMath so it can be exhausted offline; everything here
        // is the reading of live state that a pure function cannot do. `openPct` is the aperture read
        // just above, and once ApertureTracked is true it is the ONLY thing that speaks for the door:
        // it is what lets a door mid-CLOSE stay a hole in the grid while its leaves slide in
        // (vanilla's `Open` is already false by then), and equally what keeps a door drawn shut
        // occluding on the tick it is told to OPEN, when `Open` has gone true but OpenPct is still 0.
        // `door.Open` is still passed because it is the whole rule when the aperture is not tracked.
        // See DoorOcclusionMath for both ends and why they are one question.
        bool blocks = DoorOcclusionMath.Occludes(
            edifice.def.blockLight,
            door != null,
            door != null && door.Open,
            CelestialLightingFeatures.VectorLightOpenDoors,
            openPct,
            ApertureTracked);

        // A see-through door is skipped on purpose, and at both ends. It never occludes and it never
        // emits leaves, so a record of it could never change an answer — and it is also the
        // population that would go unnoticed if it could, because Building.SpawnSetup only writes
        // lightBlockers when def.blockLight is true, so a glass door being built fires no
        // invalidation at all and no memo would ever hear about it.
        if (door != null && edifice.def.blockLight)
        {
            doors.Add(new VectorLightSilhouetteMath.Door(
                cell.x, cell.z, DoorAccess.LeavesSlideAlongX(door), openPct, blocks));
        }

        return blocks;
    }

    // Whether the aperture is a real reading this bake, as opposed to the 0 that stands in for
    // "not tracked". ONE definition, asked by both ApertureOf and the Occludes call above, because
    // DoorOcclusionMath picks which rule to apply from it: if the two ever disagreed, a door would be
    // measured by the aperture while being told the aperture is meaningless, which reads as a door
    // that blocks light while visibly standing open.
    private static bool ApertureTracked =>
        CelestialLightingFeatures.VectorLightOpenDoors
        && CelestialLightingFeatures.VectorLightDoorAperture;

    // How far the door on this cell has slid, or 0 for anything that is not a light-blocking door
    // under the feature flag. Returns 0 rather than a nullable so the hot loop above stays
    // branch-light.
    //
    // Quantised HERE rather than in the caller, so every consumer of the aperture sees the same
    // stepped value: an unquantised read anywhere would make the field dirty on a tick the geometry
    // did not actually change on, which is precisely the cost the quantisation exists to avoid. It
    // is also what makes the memo worth having — an unquantised aperture would move the leaves every
    // tick, and the silhouette would still be reusable, but nothing else would ever be still.
    private static float ApertureOf(Building_Door door, bool blocksLight)
    {
        if (!ApertureTracked || door == null || !blocksLight)
        {
            return 0f;
        }

        return DoorApertureMath.Quantise(
            DoorAccess.OpenFraction(door), DoorApertureMath.DefaultQuantisationSteps);
    }
}
