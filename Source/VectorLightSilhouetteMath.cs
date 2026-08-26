using System.Collections.Generic;

namespace CelestialLighting;

// Pure core, DESIGN.md §27e: assembling one light's occluder set out of a static part and a moving
// part, and deciding when the static part can be reused rather than rescanned.
//
// Verse-free on purpose, same discipline as DoorOcclusionMath and EavesMath — VectorLightBlockers
// reads the edifice grid and the door state off live things and hands primitives down here.
// Compiled into both Source (net481) and Tests (net8.0) through a linked <Compile Include>, so the
// tests exercise the exact shipped file.
//
// WHY THE SPLIT EXISTS AT ALL. Opening a door is the most frequent geometry change in a colony, and
// the aperture is quantised into eight steps, so one swing asks every light that can see the door
// for a fresh occluder set nine times. Eight of those nine answers are the same wall: the only
// thing that moved is the two door leaves, which are sub-cell segments riding alongside the
// silhouette rather than through it. Rescanning a 31x31 window and re-extracting its silhouette to
// discover that is the work this file exists to skip.
//
// THE DIVIDING LINE IS THE WHOLE-CELL GRID, NOT THE DOOR. A door cell is a whole-cell occluder when
// its aperture is zero and a hole otherwise (DoorOcclusionMath.Occludes), so the grid does change
// once per swing — between the shut step and the first open one. That transition is detected by
// comparing each recorded door's occlusion against a fresh reading, which costs one edifice lookup
// per door rather than one per cell of the window. Everything else about the swing leaves the grid
// alone, which is why the silhouette can be held across it verbatim rather than approximately.
//
// WHAT MAKES HOLDING IT SAFE is that the static half moves only when a light blocker is built or
// removed, and vanilla tells us exactly when that happens: Verse.Building.SpawnSetup and DeSpawn
// call GlowGrid.LightBlockerAdded/Removed, which Patch_VectorLightInvalidation postfixes. A door
// SLIDING fires neither, which is the entire reason a door swing is cheap here and a wall going up
// is not. See Memo.Valid.
public static class VectorLightSilhouetteMath
{
    // One light-blocking door inside a light's window, as the occluder assembly needs to see it.
    //
    // A struct of primitives rather than a Building_Door so this file stays Verse-free — and, more
    // usefully, so the memo below never holds a reference to a Thing. A cached Thing that has been
    // despawned is a staleness class of its own; re-reading the cell is two grid lookups for a
    // population that is nearly always zero and never more than a handful.
    public readonly struct Door
    {
        public readonly int X;
        public readonly int Z;

        // Which way this door's leaves slide, which is fixed by the door's rotation at build time.
        public readonly bool AlongX;

        // The quantised aperture: 0 shut, 1 fully open, anything between mid-slide.
        public readonly float Open;

        // Whether the cell counts as a whole-cell occluder in the silhouette grid. Kept beside the
        // aperture rather than derived from it because the rule is DoorOcclusionMath's and depends
        // on two feature flags as well as on the aperture — a flag flipped mid-session has to read
        // as a changed grid, and it does, because this is what the comparison below looks at.
        public readonly bool Blocks;

        public Door(int x, int z, bool alongX, float open, bool blocks)
        {
            X = x;
            Z = z;
            AlongX = alongX;
            Open = open;
            Blocks = blocks;
        }
    }

    // One light's static occluder set, held between bakes.
    //
    // Mutable and owned by the LightEntry it belongs to, rather than a keyed dictionary: the entry
    // already IS the per-emitter record, and a second lookup structure would need its own lifetime
    // rules against a roster that resyncs. It is filled and read only from the gather, which runs
    // serially on the calling thread — see VectorLightField.BakeSelected for why that matters.
    public sealed class Memo
    {
        // The silhouette as SilhouetteSegments last extracted it, INCLUDING every door cell that was
        // a whole-cell occluder at the time. Doors are not held out of the grid: doing that would
        // split a merged wall run into three pieces around every doorway and hand Build extra corner
        // rays for endpoints that are not corners, which is a slower bake and a different polygon.
        // The grid is kept whole and the reuse test asks whether it still describes the world.
        public VectorLightMath.Segment[] Silhouette;

        // The window this was extracted for. Held rather than assumed because an entry's cell and
        // radius are snapshots re-read on roster resync, so a light that moved or was recoloured can
        // arrive here with the same Memo attached and a different window.
        public int CentreX;
        public int CentreZ;
        public float Radius;

        // Every light-blocking door inside that window, in window scan order.
        //
        // Only X, Z and Blocks are read after the rebuild that filled this. Open and AlongX are
        // whatever they were at build time and are deliberately not refreshed — the cached path
        // reads both live, because they are what moves.
        public readonly List<Door> Doors = new List<Door>();

        // False once a light blocker has been built or removed inside this window. Nothing else
        // clears it: the point of the whole file is that a door sliding does not.
        public bool Valid;

        public void Invalidate()
        {
            Valid = false;
        }
    }

    // Whether the memo describes the window being asked about. Separate from the door comparison
    // because it can be answered before any live state is read, and the door comparison cannot.
    public static bool CoversWindow(Memo memo, int centreX, int centreZ, float radius)
    {
        return memo != null
            && memo.Valid
            && memo.Silhouette != null
            && memo.CentreX == centreX
            && memo.CentreZ == centreZ
            && memo.Radius == radius;
    }

    // Whether every recorded door still occludes the way it did when the silhouette was extracted.
    //
    // ONLY THE WHOLE-CELL ANSWER IS COMPARED, not the aperture. A door at one-eighth open and one at
    // seven-eighths produce different leaves and the same grid, and the leaves are rebuilt every
    // bake regardless — so comparing apertures here would reject a reusable silhouette eight times
    // a swing and leave nothing to reuse.
    //
    // The count check is not defensive padding: a door built or removed changes it, and although
    // that also fires LightBlockerAdded/Removed and clears Valid, a mod that spawns a door without
    // going through Building.SpawnSetup would otherwise walk off the end of the shorter list.
    public static bool OcclusionUnchanged(Memo memo, List<Door> fresh)
    {
        if (memo == null || fresh == null || memo.Doors.Count != fresh.Count)
        {
            return false;
        }

        for (int i = 0; i < fresh.Count; i++)
        {
            if (memo.Doors[i].Blocks != fresh[i].Blocks)
            {
                return false;
            }
        }

        return true;
    }

    // The occluder set for one light: the whole-cell silhouette, plus the leaf edges of any door
    // caught mid-slide.
    //
    // Partly-open doors ride ALONGSIDE the silhouette rather than through it. The grid can only
    // carry whole cells, and Build takes an arbitrary segment array and fires a corner ray at every
    // endpoint it is handed — so a sub-cell occluder needs no new grid concept, just a couple more
    // segments. This is also why the penumbra tracks the leaf edges for free.
    //
    // `leafScratch` is the caller's list, cleared here. A shut or fully-open door contributes
    // nothing, which is the common case, and the array is then returned unwrapped — the same object
    // the caller handed in, with no copy, exactly as this read before the split.
    public static VectorLightMath.Segment[] Assemble(
        VectorLightMath.Segment[] silhouette,
        List<Door> doors,
        List<VectorLightMath.Segment> leafScratch)
    {
        leafScratch.Clear();

        for (int i = 0; i < doors.Count; i++)
        {
            AppendLeaves(leafScratch, doors[i]);
        }

        if (leafScratch.Count == 0)
        {
            return silhouette;
        }

        VectorLightMath.Segment[] combined =
            new VectorLightMath.Segment[silhouette.Length + leafScratch.Count];
        silhouette.CopyTo(combined, 0);
        leafScratch.CopyTo(combined, silhouette.Length);
        return combined;
    }

    // The whole path from a freshly scanned window, for the case where nothing could be reused. The
    // cached path calls Assemble directly with a silhouette it did not extract; both end in the same
    // assembly so the two cannot disagree about what an occluder set is.
    public static VectorLightMath.Segment[] Build(
        bool[] blocked,
        int width,
        int height,
        int originX,
        int originZ,
        List<Door> doors,
        List<VectorLightMath.Segment> leafScratch,
        out VectorLightMath.Segment[] silhouette)
    {
        silhouette = VectorLightMath.SilhouetteSegments(blocked, width, height, originX, originZ);
        return Assemble(silhouette, doors, leafScratch);
    }

    // The two leaf edges of one partly-open door, on both of the faces light can cross.
    //
    // A door in a wall running along Z occludes with its west and east faces, each spanning Z, and
    // its leaves slide along Z — so the split axis and the face's span axis are the same one, on
    // both orientations. That is why this is a single routine with an axis flag rather than two.
    //
    // A shut door (0) is an ordinary blocker and a fully open one (1) is an ordinary hole; only the
    // interval between them needs sub-cell geometry, which is what keeps this off the common path.
    private static void AppendLeaves(List<VectorLightMath.Segment> into, Door door)
    {
        if (door.Open <= 0f || door.Open >= 1f)
        {
            return;
        }

        float axisMin = door.AlongX ? door.X : door.Z;

        DoorApertureMath.LeafSpans(
            axisMin, door.Open, out float aStart, out float aEnd, out float bStart, out float bEnd);

        // The two faces perpendicular to the direction light crosses the door.
        float faceA = door.AlongX ? door.Z : door.X;
        float faceB = faceA + 1f;

        AddLeaf(into, door.AlongX, faceA, aStart, aEnd);
        AddLeaf(into, door.AlongX, faceA, bStart, bEnd);
        AddLeaf(into, door.AlongX, faceB, aStart, aEnd);
        AddLeaf(into, door.AlongX, faceB, bStart, bEnd);
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
}
