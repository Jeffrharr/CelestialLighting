namespace CelestialLighting;

// Pure core, DESIGN.md §27 / issue #188 item A: which map sections a rebuilt emitter obliges us to
// regenerate, so an invalidation costs the sections it touched rather than the whole viewport.
//
// Verse-free, like VectorLightMath beside it. The adapter (Patch_VectorLightDraw) turns the section
// range this hands back into MapDrawer.MapMeshDirty calls; everything about WHICH sections lives here.
//
// WHY THIS EXISTS AT ALL. VectorLightField.EnsurePolygons used to answer a bool — "did I build
// anything, anywhere on this map" — and the draw turned that into WholeMapChanged(GroundGlow). That
// is map-wide and says nothing about WHERE the change was, so a pawn opening a door on the far side
// of the colony regenerated the lighting overlay, the darkness layer, night desaturation, eave shade
// and our own mask for every section under the camera, none of which the door could reach. Door
// aperture tracking quantises a swing into eight steps, so that happened nine times per door use,
// and a door is the most frequent geometry change a colony has.
//
// THE ARITHMETIC IS SMALL AND THE EDGE CASES ARE NOT, which is the whole reason it is a pure file
// rather than four lines inlined in the adapter. Getting the margin wrong here does not throw and
// does not look like a bug: it leaves a section holding the previous bake, so one square of the map
// keeps rendering a shadow that has already moved. That reads as a formula error somewhere else
// entirely. The offline tests pin the bound against an independent transcription of the predicate
// VectorLightMask.CollectReaching actually uses.
public static class SectionDirtyMath
{
    // An inclusive cell rectangle, plus whether it holds anything at all. A plain struct rather than
    // Verse's CellRect because this file may not reference Verse, and because CellRect.Empty is
    // (0, 0, 0, 0) — a zero-WIDTH rect anchored at the map's corner, which is a different thing from
    // "no rectangle" and would quietly dirty section (0,0) on every frame that built nothing.
    public readonly struct CellBounds
    {
        public readonly int MinX;
        public readonly int MinZ;
        public readonly int MaxX;
        public readonly int MaxZ;

        // Default(CellBounds) is deliberately the empty one, so a caller can accumulate into a
        // freshly-declared local without seeding it from the first element.
        public readonly bool Any;

        public CellBounds(int minX, int minZ, int maxX, int maxZ)
        {
            MinX = minX;
            MinZ = minZ;
            MaxX = maxX;
            MaxZ = maxZ;
            Any = true;
        }
    }

    // The cells an emitter at (cellX, cellZ) of this radius can change the appearance of.
    //
    // THE BOUND IS THE MASK'S OWN PREDICATE, SOLVED FOR THE SECTION. VectorLightMask.CollectReaching
    // admits an emitter into a section when
    //
    //     cell.x + reach >= rect.minX - 1   and   cell.x - reach <= rect.maxX
    //
    // with reach = ceil(radius) + margin. Rearranged, that is exactly "the section's rect intersects
    // [cell.x - reach, cell.x + reach + 1]". So the interval below is not a conservative guess with
    // slack in it; it is the same condition read the other way round, and the two cannot drift apart
    // without the differential test noticing.
    //
    // THE +1 ON THE MAX SIDE ONLY IS REAL, NOT A TYPO, and it is the same asymmetry
    // VectorLightMask.CellsWide carries a warning about. A section's corner vertices run from minX to
    // maxX + 1 inclusive and each averages the cells on both sides of it, so the outermost vertex of
    // a section reads a cell one past the section's own maximum. An emitter sitting in that one cell
    // changes a vertex of the section next door while touching none of its cells.
    public static CellBounds Reach(int cellX, int cellZ, float radius, int margin)
    {
        int reach = CeilToInt(radius) + margin;

        return new CellBounds(
            cellX - reach, cellZ - reach,
            cellX + reach + 1, cellZ + reach + 1);
    }

    // Accumulate. An empty operand returns the other side untouched, so the caller's loop needs no
    // first-iteration special case.
    public static CellBounds Union(CellBounds a, CellBounds b)
    {
        if (!a.Any)
        {
            return b;
        }

        if (!b.Any)
        {
            return a;
        }

        return new CellBounds(
            a.MinX < b.MinX ? a.MinX : b.MinX,
            a.MinZ < b.MinZ ? a.MinZ : b.MinZ,
            a.MaxX > b.MaxX ? a.MaxX : b.MaxX,
            a.MaxZ > b.MaxZ ? a.MaxZ : b.MaxZ);
    }

    // The inclusive range of section indices these bounds overlap, clipped to a map of this size.
    //
    // Returns false when the bounds are empty or fall entirely outside the map, which the adapter
    // treats as "dirty nothing" — the out values are meaningless in that case rather than merely
    // unhelpful, so it must not loop over them.
    //
    // Clipping to the map before dividing, rather than dividing and clamping the section index, is
    // what keeps a negative coordinate correct: integer division truncates towards zero, so
    // (-3) / 17 is 0 rather than -1, and an emitter hanging off the west edge would silently report
    // its west margin as section 0 whether or not it reached it. Here it cannot arise, because the
    // clip has already moved the value to 0.
    public static bool SectionRange(
        CellBounds bounds, int sectionSize, int mapWidth, int mapHeight,
        out int minSectionX, out int minSectionZ, out int maxSectionX, out int maxSectionZ)
    {
        minSectionX = 0;
        minSectionZ = 0;
        maxSectionX = 0;
        maxSectionZ = 0;

        if (!bounds.Any || sectionSize <= 0 || mapWidth <= 0 || mapHeight <= 0)
        {
            return false;
        }

        int minX = Clamp(bounds.MinX, 0, mapWidth - 1);
        int maxX = Clamp(bounds.MaxX, 0, mapWidth - 1);
        int minZ = Clamp(bounds.MinZ, 0, mapHeight - 1);
        int maxZ = Clamp(bounds.MaxZ, 0, mapHeight - 1);

        // Clamping both ends of an interval that lies wholly off one side of the map collapses it
        // onto that edge rather than emptying it, so an emitter at x = -400 would come back as
        // section 0 and dirty a square of map it cannot see. Detected here rather than prevented
        // above, because the clamp is also what makes the ordinary straddling case right.
        if (bounds.MaxX < 0 || bounds.MinX > mapWidth - 1
            || bounds.MaxZ < 0 || bounds.MinZ > mapHeight - 1)
        {
            return false;
        }

        minSectionX = minX / sectionSize;
        maxSectionX = maxX / sectionSize;
        minSectionZ = minZ / sectionSize;
        maxSectionZ = maxZ / sectionSize;

        return true;
    }

    // The cell a section index pair anchors on — its bottom-left corner, which is what MapMeshDirty
    // wants and is always inside the map for any index this file returns.
    public static int SectionAnchor(int sectionIndex, int sectionSize) => sectionIndex * sectionSize;

    // Local rather than Mathf, because this file may not reference UnityEngine. Ceiling of a float
    // to an int, matching Mathf.CeilToInt for the non-negative radii an emitter can have.
    private static int CeilToInt(float value) => (int)System.Math.Ceiling(value);

    private static int Clamp(int value, int min, int max) =>
        value < min ? min : (value > max ? max : value);
}
