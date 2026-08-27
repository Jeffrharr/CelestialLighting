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

    // The cells a set of CHANGED cells obliges us to regenerate, as opposed to the cells an
    // emitter's whole reach does.
    //
    // WHY A SECOND ENTRY POINT AND NOT A NARROWER Reach. Reach answers "which sections admit this
    // emitter", because that is the question the emitter existed to ask: it had just been rebuilt
    // and anything that reads it might now read something different. Once the two coverage grids can
    // be compared, the better question is "which sections read a cell whose coverage actually
    // moved" — and for a lamp sealed away from the door that dirtied it, the honest answer is none.
    // Measured on the stress colony's own geometry: a door swing dirties 146 sections through Reach
    // and 18 through this, because two thirds of the lamps it marks rebake to a byte-identical grid
    // and the third that do change change a wedge rather than a disc.
    //
    // THE MARGIN IS SYMMETRIC HERE AND ASYMMETRIC IN Reach, and that difference is real rather than
    // an oversight in one of them. The mask accumulates into a grid covering the section plus one
    // cell on every side — VectorLightMask.CellsWide — so a section reads cell c exactly when
    //
    //     section.minX - 1 <= c.x <= section.maxX + 1
    //
    // which rearranges to "the section's rect intersects [c.x - 1, c.x + 1]": one cell either way.
    // Reach's extra cell on the max side is not the same quantity — it comes from CollectReaching's
    // own admission predicate, which carries ReachMargin's deliberate slack on top of the geometry.
    // Solving the two questions to the same answer would mean one of them was solved wrongly, so the
    // offline tests pin the containment (this is never wider than Reach over the same emitter)
    // rather than pinning them equal.
    public static CellBounds Changed(int minX, int minZ, int maxX, int maxZ, int margin)
    {
        if (maxX < minX || maxZ < minZ)
        {
            return default;
        }

        return new CellBounds(minX - margin, minZ - margin, maxX + margin, maxZ + margin);
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

    // Whether two inclusive rectangles share any cell. Used to ask "can this emitter change anything
    // the camera is looking at", which is issue #188 item B's whole question.
    //
    // Empty bounds intersect nothing, including other empty bounds. That is the useful answer rather
    // than a philosophical one: an emitter with no reach cannot affect a view, and a view with no
    // extent cannot be affected by anything.
    public static bool Intersects(CellBounds a, CellBounds b)
    {
        if (!a.Any || !b.Any)
        {
            return false;
        }

        return a.MinX <= b.MaxX && a.MaxX >= b.MinX
            && a.MinZ <= b.MaxZ && a.MaxZ >= b.MinZ;
    }

    // The whole of a map, as bounds. The "cull nothing" argument for EnsurePolygons, and the thing a
    // flag turned off passes so that off reproduces the pre-cull behaviour exactly rather than
    // approximately.
    public static CellBounds WholeMap(int mapWidth, int mapHeight)
    {
        if (mapWidth <= 0 || mapHeight <= 0)
        {
            return default;
        }

        return new CellBounds(0, 0, mapWidth - 1, mapHeight - 1);
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

    // How many sections a map of this size has, matching MapDrawer.SectionCount's ceiling division.
    //
    // EXISTS FOR THE BASELINE ARM, not for the shipped path. WholeMapChanged dirties every section
    // without ever counting them, so a section-dirty counter that only the new path increments would
    // read 0 for the arm it is being compared against — and an A/B whose baseline is zero is not a
    // comparison, it is a feature-present/absent picture. The flag-off branch charges itself this
    // number so both arms report the same quantity.
    public static int SectionCount(int mapWidth, int mapHeight, int sectionSize)
    {
        if (mapWidth <= 0 || mapHeight <= 0 || sectionSize <= 0)
        {
            return 0;
        }

        int across = (mapWidth + sectionSize - 1) / sectionSize;
        int up = (mapHeight + sectionSize - 1) / sectionSize;

        return across * up;
    }

    // Local rather than Mathf, because this file may not reference UnityEngine. Ceiling of a float
    // to an int, matching Mathf.CeilToInt for the non-negative radii an emitter can have.
    private static int CeilToInt(float value) => (int)System.Math.Ceiling(value);

    private static int Clamp(int value, int min, int max) =>
        value < min ? min : (value > max ? max : value);
}
