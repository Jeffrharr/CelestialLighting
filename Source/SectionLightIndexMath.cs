namespace CelestialLighting;

// Which lights touch which section, as a counting sort — the pure half of the index that stops the
// lighting-overlay mask walking every light on the map once per section regenerate.
//
// THE PROBLEM IT SOLVES, IN THE NUMBERS THAT PROVOKED IT. At 503 emitters a whole-map rebake ran 91
// section regenerates, and the mask's saturation pass scanned 28,671 lights across them to fold
// 2,826 — 9.9% useful. CollectReaching scanned 45,773 of our own emitters to admit 3,143 — 6.9%.
// Both loops ask "which of these touches this section", which is a question a map-sized list should
// not have to be re-read to answer.
//
// WHY A COUNTING SORT AND NOT A LIST PER SECTION. A List<int>[] rebuilt every frame is one
// allocation per section plus the backing arrays, and this repo has already measured what that
// litter does: the visibility polygon's first cull allocated per segment and slowed an untouched
// neighbouring stage by 60% through nothing but garbage collection. Two arrays, grown and never
// shrunk, have no per-frame allocation at all once they are big enough.
//
// THE ASCENDING ORDER IS LOAD-BEARING AND IT IS FREE. Vanilla's glow accumulation projects after
// every addition, so the fold is lossy and therefore NOT commutative — reproducing what vanilla
// displayed means visiting the lights in the order CombineColorsJob.Execute added them, which is
// ascending index. The fill pass below walks items in ascending order and appends, so every bucket
// comes out ascending with no sort and no comparison. Reordering the fill would silently change the
// colour of every saturated cell, which is why this is stated rather than left to be noticed.
//
// The buckets are DISJOINT BY CONSTRUCTION at query time: an item is inserted into every section its
// bounds overlap, so a query that reads exactly one section's bucket needs no deduplication. That is
// only true because the caller expands each item's bounds by the margin its own reads use BEFORE
// handing them here — see VectorLightMask.CellMargin and GlowGridPerLight's index build.
public static class SectionLightIndexMath
{
    // Four ints per item: minSectionX, maxSectionX, minSectionZ, maxSectionZ. A minSectionX of -1
    // means the item is not on the map at all and is skipped — SectionDirtyMath.SectionRange
    // already distinguishes "off the map" from "clamped to the edge", and collapsing the two here
    // would file an emitter at x = -400 under section 0.
    public const int IntsPerItem = 4;

    public const int Absent = -1;

    // Builds the index in place. Returns the number of (item, section) pairs placed, which is what
    // `items` is filled to — the arrays may be longer, and reading past the return value reads a
    // previous frame's index.
    //
    // `starts` comes back with sectionCount + 1 entries, so a section's bucket is
    // items[starts[s] .. starts[s + 1]) and the last section needs no special case.
    //
    // GROWN AND NEVER SHRUNK, and nothing is cleared between builds beyond the counts this pass
    // writes itself. Every entry of `starts` is written by the count pass and every entry of `items`
    // below the return value is written by the fill pass, so a stale value cannot survive into a
    // region anything goes on to read. That claim is what ARebuiltIndexMatchesAFreshOne holds.
    public static int Build(
        int[] ranges, int itemCount, int sectionsAcross, int sectionCount,
        ref int[] starts, ref int[] items)
    {
        // ONLY THE DEGENERATE GRID SHORT-CIRCUITS, and an empty item list deliberately does not.
        // The first cut returned early on itemCount == 0 having written starts[0] alone, which left
        // every other offset holding the PREVIOUS build's values -- so an empty frame on a reused
        // buffer handed out a bucket pointing into a stale index. The passes below are already
        // correct for zero items (nothing is counted, the prefix sum leaves zeros, nothing is
        // placed), so falling through is both shorter and the only version that clears the array.
        // ARebuiltIndexMatchesAFreshOne caught it on the first run and is why it exists.
        if (ranges == null || sectionsAcross <= 0 || sectionCount <= 0)
        {
            Grow(ref starts, 1);
            starts[0] = 0;
            return 0;
        }

        if (itemCount < 0)
            itemCount = 0;

        Grow(ref starts, sectionCount + 1);

        for (int s = 0; s <= sectionCount; s++)
            starts[s] = 0;

        // PASS ONE: how many items each section holds. Counted into starts[s + 1] rather than
        // starts[s], so the prefix sum below turns the same array into offsets without a second
        // buffer — the "two allocations, not six" rule the polygon cull's index arrived at.
        int total = 0;

        for (int i = 0; i < itemCount; i++)
        {
            int at = i * IntsPerItem;

            if (ranges[at] == Absent)
                continue;

            int minSx = ranges[at];
            int maxSx = ranges[at + 1];
            int minSz = ranges[at + 2];
            int maxSz = ranges[at + 3];

            for (int sz = minSz; sz <= maxSz; sz++)
            {
                int row = sz * sectionsAcross;

                for (int sx = minSx; sx <= maxSx; sx++)
                {
                    int section = row + sx;

                    if (section >= 0 && section < sectionCount)
                    {
                        starts[section + 1]++;
                        total++;
                    }
                }
            }
        }

        for (int s = 0; s < sectionCount; s++)
            starts[s + 1] += starts[s];

        Grow(ref items, total);

        // PASS TWO: place them. `starts` doubles as the write cursor and is walked forward, then
        // shifted back at the end — which is why there is no separate cursor array. Items are
        // visited in ascending order, so each bucket ends up ascending; see the header for why that
        // is the whole correctness argument rather than a tidy detail.
        for (int i = 0; i < itemCount; i++)
        {
            int at = i * IntsPerItem;

            if (ranges[at] == Absent)
                continue;

            int minSx = ranges[at];
            int maxSx = ranges[at + 1];
            int minSz = ranges[at + 2];
            int maxSz = ranges[at + 3];

            for (int sz = minSz; sz <= maxSz; sz++)
            {
                int row = sz * sectionsAcross;

                for (int sx = minSx; sx <= maxSx; sx++)
                {
                    int section = row + sx;

                    if (section >= 0 && section < sectionCount)
                    {
                        items[starts[section]] = i;
                        starts[section]++;
                    }
                }
            }
        }

        // Shift the cursors back into offsets. Walked downwards so each entry reads its predecessor
        // before that predecessor is overwritten.
        for (int s = sectionCount; s > 0; s--)
            starts[s] = starts[s - 1];

        starts[0] = 0;

        return total;
    }

    // The section a cell belongs to, as a flat index, or Absent when it is off the map. Kept here
    // rather than at the call site so the build and the query cannot drift apart on the one piece of
    // arithmetic they must agree about exactly.
    public static int SectionAt(int cellX, int cellZ, int sectionSize, int mapWidth, int mapHeight,
        int sectionsAcross)
    {
        if (sectionSize <= 0 || cellX < 0 || cellZ < 0 || cellX >= mapWidth || cellZ >= mapHeight)
            return Absent;

        return (cellZ / sectionSize) * sectionsAcross + (cellX / sectionSize);
    }

    private static void Grow(ref int[] buffer, int needed)
    {
        if (buffer == null || buffer.Length < needed)
            buffer = new int[needed < 16 ? 16 : needed];
    }
}
