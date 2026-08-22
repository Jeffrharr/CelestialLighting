namespace CelestialLighting.Tests;

// Offline unit tests for Source/SectionDirtyMath.cs (linked into this project, so these run against
// the exact shipped file).
//
// WHAT IS ACTUALLY AT RISK HERE, because it is not "is the arithmetic right". These bounds decide
// which sections get told to rebake after an emitter's polygon moved. Dirty too many and the change
// is merely less of an optimisation than it claimed; dirty too FEW and a section keeps rendering a
// shadow that has already moved — with no exception, no log line and every probe still healthy,
// because the section simply never asks. That is the failure this file exists to make loud, and it
// is why the load-bearing test below is the differential one rather than any of the examples.
[TestFixture]
public class SectionDirtyMathTests
{
    private const int SectionSize = 17;

    // ---- the oracle -----------------------------------------------------------------------
    //
    // A HAND TRANSCRIPTION of the admission predicate in VectorLightMask.CollectReaching, kept
    // deliberately in its original shape — the same comparisons, the same rearrangement, the same
    // `- 1` — rather than tidied into an interval overlap. Tidying it would be re-deriving it, and a
    // differential test whose two sides are both derived from the code under test asserts x - x == 0.
    //
    // This is the only copy of anything in this repo that is knowingly a duplicate of shipped logic.
    // It earns that by being the thing SectionDirtyMath.Reach is checked against: if somebody changes
    // the margin at one end and not the other, the shipped files agree with each other and disagree
    // with this, which is exactly the direction the check has to point.
    private static bool MaskWouldAdmit(
        int emitterX, int emitterZ, float radius, int reachMargin,
        int rectMinX, int rectMinZ, int rectMaxX, int rectMaxZ)
    {
        int reach = (int)System.Math.Ceiling(radius) + reachMargin;

        return emitterX + reach >= rectMinX - 1
            && emitterX - reach <= rectMaxX
            && emitterZ + reach >= rectMinZ - 1
            && emitterZ - reach <= rectMaxZ;
    }

    // Every section of a map of this size, as the rects the mask would be handed — clipped to the
    // map exactly the way Verse's Section.CellRect clips them, because a map whose size is not a
    // multiple of the section size has a short row and a short column and those are where an
    // off-by-one lives.
    private static IEnumerable<(int Sx, int Sz, int MinX, int MinZ, int MaxX, int MaxZ)> Sections(
        int mapWidth, int mapHeight)
    {
        int sectionsX = (mapWidth + SectionSize - 1) / SectionSize;
        int sectionsZ = (mapHeight + SectionSize - 1) / SectionSize;

        for (int sx = 0; sx < sectionsX; sx++)
        {
            for (int sz = 0; sz < sectionsZ; sz++)
            {
                yield return (
                    sx, sz,
                    sx * SectionSize, sz * SectionSize,
                    System.Math.Min(sx * SectionSize + SectionSize - 1, mapWidth - 1),
                    System.Math.Min(sz * SectionSize + SectionSize - 1, mapHeight - 1));
            }
        }
    }

    // ---- the differential -------------------------------------------------------------------

    // THE TEST THIS FILE IS FOR. For every emitter placement and every section of the map, the set
    // of sections SectionDirtyMath says to dirty must be exactly the set of sections that would
    // admit this emitter. Not a superset — exactly. A superset would be safe and would also hide a
    // margin drifting, and the margin drifting in the other direction is the silent failure.
    //
    // Map sizes chosen so one is a whole number of sections (51 = 3 x 17) and the others are not,
    // since the short edge row is where clipping errors surface. Emitter positions include the
    // corners and one hanging off the edge, which a colony genuinely produces — a wall lamp on the
    // map border, or a light whose radius exceeds its distance to the edge.
    [TestCase(51, 51)]
    [TestCase(60, 35)]
    [TestCase(17, 17)]
    public void DirtiedSectionsAreExactlyTheSectionsThatAdmitTheEmitter(int mapWidth, int mapHeight)
    {
        float[] radii = { 0f, 1f, 3.5f, 8f, 12f, 25f };

        for (int emitterX = -4; emitterX < mapWidth + 4; emitterX += 3)
        {
            for (int emitterZ = -4; emitterZ < mapHeight + 4; emitterZ += 3)
            {
                foreach (float radius in radii)
                {
                    AssertRangeMatchesOracle(emitterX, emitterZ, radius, mapWidth, mapHeight);
                }
            }
        }
    }

    private static void AssertRangeMatchesOracle(
        int emitterX, int emitterZ, float radius, int mapWidth, int mapHeight)
    {
        SectionDirtyMath.CellBounds bounds =
            SectionDirtyMath.Reach(emitterX, emitterZ, radius, ReachMargin);

        bool any = SectionDirtyMath.SectionRange(
            bounds, SectionSize, mapWidth, mapHeight,
            out int minSectionX, out int minSectionZ, out int maxSectionX, out int maxSectionZ);

        foreach (var section in Sections(mapWidth, mapHeight))
        {
            bool admitted = MaskWouldAdmit(
                emitterX, emitterZ, radius, ReachMargin,
                section.MinX, section.MinZ, section.MaxX, section.MaxZ);

            bool dirtied = any
                && section.Sx >= minSectionX && section.Sx <= maxSectionX
                && section.Sz >= minSectionZ && section.Sz <= maxSectionZ;

            Assert.That(
                dirtied, Is.EqualTo(admitted),
                $"emitter ({emitterX},{emitterZ}) r={radius} on {mapWidth}x{mapHeight}: "
                + $"section ({section.Sx},{section.Sz}) admitted={admitted} dirtied={dirtied}");
        }
    }

    // The margin the shipped adapter passes, mirrored here rather than read off VectorLightMask —
    // that type is Verse-bound and cannot be linked into an offline test project. Kept as a named
    // constant so the mirroring is visible; the Cecil API tests are where a divergence in the value
    // itself would have to be caught.
    private const int ReachMargin = 1;

    // ---- reach ------------------------------------------------------------------------------

    // The asymmetry pinned as an example, because it looks like a bug every time somebody reads it.
    // A radius-3 emitter at x = 20 reaches down to 20 - 4 = 16 and up to 20 + 4 + 1 = 25: one further
    // on the max side, because a section's corner vertices run one cell past its own maximum and
    // average the cell beyond.
    [Test]
    public void ReachIsOneWiderOnTheMaxSide()
    {
        SectionDirtyMath.CellBounds bounds = SectionDirtyMath.Reach(20, 40, 3f, 1);

        Assert.Multiple(() =>
        {
            Assert.That(bounds.Any, Is.True);
            Assert.That(bounds.MinX, Is.EqualTo(16));
            Assert.That(bounds.MaxX, Is.EqualTo(25));
            Assert.That(bounds.MinZ, Is.EqualTo(36));
            Assert.That(bounds.MaxZ, Is.EqualTo(45));
        });
    }

    // A fractional radius rounds up, matching Mathf.CeilToInt in the mask. Truncating instead would
    // lose the outermost cell of every emitter whose radius is not a whole number, which is most of
    // them once a mod starts handing out radius 11.5.
    [TestCase(3.0f, 4)]
    [TestCase(3.1f, 5)]
    [TestCase(3.9f, 5)]
    [TestCase(0.0f, 1)]
    public void ReachCeilingsTheRadius(float radius, int expectedReach)
    {
        SectionDirtyMath.CellBounds bounds = SectionDirtyMath.Reach(100, 100, radius, 1);

        Assert.That(100 - bounds.MinX, Is.EqualTo(expectedReach));
    }

    // ---- union ------------------------------------------------------------------------------

    [Test]
    public void DefaultBoundsAreEmpty()
    {
        Assert.That(default(SectionDirtyMath.CellBounds).Any, Is.False);
    }

    // The empty bounds are the identity, which is what lets EnsurePolygons accumulate into a bare
    // local without a first-iteration special case.
    [Test]
    public void EmptyBoundsAreTheUnionIdentity()
    {
        SectionDirtyMath.CellBounds real = new SectionDirtyMath.CellBounds(3, 4, 9, 10);
        SectionDirtyMath.CellBounds empty = default;

        Assert.Multiple(() =>
        {
            AssertSameBounds(SectionDirtyMath.Union(empty, real), real);
            AssertSameBounds(SectionDirtyMath.Union(real, empty), real);
            Assert.That(SectionDirtyMath.Union(empty, empty).Any, Is.False);
        });
    }

    [Test]
    public void UnionSpansBothOperandsAndIsOrderIndependent()
    {
        SectionDirtyMath.CellBounds a = new SectionDirtyMath.CellBounds(3, 40, 9, 44);
        SectionDirtyMath.CellBounds b = new SectionDirtyMath.CellBounds(-2, 41, 5, 90);

        SectionDirtyMath.CellBounds forward = SectionDirtyMath.Union(a, b);
        SectionDirtyMath.CellBounds backward = SectionDirtyMath.Union(b, a);

        Assert.Multiple(() =>
        {
            AssertSameBounds(forward, new SectionDirtyMath.CellBounds(-2, 40, 9, 90));
            AssertSameBounds(backward, forward);
        });
    }

    // ---- intersects and whole-map (issue #188 item B) ------------------------------------------

    // The cull's predicate, as a truth table over the four ways two rectangles can miss plus the
    // touching case. Touching counts as intersecting because both rectangles are INCLUSIVE: an
    // emitter whose reach ends on the view's first column does reach a cell the camera shows.
    [TestCase(0, 0, 5, 5, 6, 0, 10, 5, false, TestName = "Intersects_missesToTheEast")]
    [TestCase(0, 0, 5, 5, 5, 0, 10, 5, true, TestName = "Intersects_touchesToTheEast")]
    [TestCase(6, 0, 10, 5, 0, 0, 5, 5, false, TestName = "Intersects_missesToTheWest")]
    [TestCase(0, 0, 5, 5, 0, 6, 5, 10, false, TestName = "Intersects_missesToTheNorth")]
    [TestCase(0, 6, 5, 10, 0, 0, 5, 5, false, TestName = "Intersects_missesToTheSouth")]
    [TestCase(0, 0, 5, 5, 3, 3, 9, 9, true, TestName = "Intersects_overlapsAtACorner")]
    [TestCase(0, 0, 20, 20, 5, 5, 9, 9, true, TestName = "Intersects_containsEntirely")]
    public void IntersectsAnswersTheOverlapQuestion(
        int aMinX, int aMinZ, int aMaxX, int aMaxZ,
        int bMinX, int bMinZ, int bMaxX, int bMaxZ, bool expected)
    {
        SectionDirtyMath.CellBounds a = new SectionDirtyMath.CellBounds(aMinX, aMinZ, aMaxX, aMaxZ);
        SectionDirtyMath.CellBounds b = new SectionDirtyMath.CellBounds(bMinX, bMinZ, bMaxX, bMaxZ);

        Assert.Multiple(() =>
        {
            Assert.That(SectionDirtyMath.Intersects(a, b), Is.EqualTo(expected));
            Assert.That(SectionDirtyMath.Intersects(b, a), Is.EqualTo(expected), "not symmetric");
        });
    }

    // Empty intersects nothing, including itself. An emitter with no reach cannot affect a view and
    // a view with no extent cannot be affected, so the cull declining both is the useful answer.
    [Test]
    public void EmptyBoundsIntersectNothing()
    {
        SectionDirtyMath.CellBounds real = new SectionDirtyMath.CellBounds(0, 0, 10, 10);

        Assert.Multiple(() =>
        {
            Assert.That(SectionDirtyMath.Intersects(default, real), Is.False);
            Assert.That(SectionDirtyMath.Intersects(real, default), Is.False);
            Assert.That(SectionDirtyMath.Intersects(default, default), Is.False);
        });
    }

    // THE PROPERTY THE FLAG-OFF ARM DEPENDS ON. Passing WholeMap as the build window must cull
    // nothing, or "off reproduces the previous behaviour exactly" is false and the baseline arm is
    // measuring a third thing that never shipped. Swept over emitters hanging off every edge,
    // because those are the ones whose reach is partly outside the map.
    [TestCase(51, 51)]
    [TestCase(60, 35)]
    [TestCase(250, 250)]
    public void WholeMapCullsNothingThatOverlapsTheMap(int mapWidth, int mapHeight)
    {
        SectionDirtyMath.CellBounds whole = SectionDirtyMath.WholeMap(mapWidth, mapHeight);

        for (int x = 0; x < mapWidth; x += 7)
        {
            for (int z = 0; z < mapHeight; z += 7)
            {
                SectionDirtyMath.CellBounds reach = SectionDirtyMath.Reach(x, z, 12f, ReachMargin);

                Assert.That(
                    SectionDirtyMath.Intersects(reach, whole), Is.True,
                    $"emitter ({x},{z}) culled against the whole map");
            }
        }
    }

    [TestCase(0, 51)]
    [TestCase(51, 0)]
    public void WholeMapOfADegenerateMapIsEmpty(int mapWidth, int mapHeight)
    {
        Assert.That(SectionDirtyMath.WholeMap(mapWidth, mapHeight).Any, Is.False);
    }

    // ---- section range ------------------------------------------------------------------------

    // THE TRUNCATION TRAP, stated as its own test because the differential above would catch it only
    // if the sweep happened to place an emitter off the west edge — which it does, but a future
    // narrowing of that sweep must not quietly take this with it. Integer division truncates towards
    // zero, so (-3) / 17 is 0 and not -1; clipping to the map before dividing is what makes an
    // emitter hanging off the edge report section 0 for the right reason rather than by accident.
    [Test]
    public void EmitterOffTheWestEdgeStillStartsAtSectionZero()
    {
        SectionDirtyMath.CellBounds bounds = new SectionDirtyMath.CellBounds(-30, -30, 5, 5);

        bool any = SectionDirtyMath.SectionRange(
            bounds, SectionSize, 51, 51, out int minSx, out int minSz, out int maxSx, out int maxSz);

        Assert.Multiple(() =>
        {
            Assert.That(any, Is.True);
            Assert.That(minSx, Is.EqualTo(0));
            Assert.That(minSz, Is.EqualTo(0));
            Assert.That(maxSx, Is.EqualTo(0));
            Assert.That(maxSz, Is.EqualTo(0));
        });
    }

    // Bounds wholly off the map dirty NOTHING, rather than collapsing onto the nearest edge. Clamping
    // both ends of such an interval would put them both on the same edge cell, which reads as a
    // perfectly ordinary one-section range and would dirty a square of map the emitter cannot see.
    [TestCase(-40, -40, -20, -20)]
    [TestCase(80, 5, 120, 20)]
    [TestCase(5, 80, 20, 120)]
    [TestCase(-40, 5, -1, 20)]
    public void BoundsEntirelyOffTheMapDirtyNothing(int minX, int minZ, int maxX, int maxZ)
    {
        SectionDirtyMath.CellBounds bounds = new SectionDirtyMath.CellBounds(minX, minZ, maxX, maxZ);

        Assert.That(
            SectionDirtyMath.SectionRange(bounds, SectionSize, 51, 51, out _, out _, out _, out _),
            Is.False);
    }

    [Test]
    public void EmptyBoundsDirtyNothing()
    {
        Assert.That(
            SectionDirtyMath.SectionRange(
                default, SectionSize, 51, 51, out _, out _, out _, out _),
            Is.False);
    }

    // Every anchor a returned range can produce is inside the map, which is what makes it safe to
    // hand straight to MapDrawer.MapMeshDirty — that method indexes its section array off the cell
    // with no bounds check of its own.
    [TestCase(51, 51)]
    [TestCase(60, 35)]
    [TestCase(250, 250)]
    public void EverySectionAnchorInRangeIsInsideTheMap(int mapWidth, int mapHeight)
    {
        SectionDirtyMath.CellBounds bounds =
            SectionDirtyMath.Reach(mapWidth - 1, mapHeight - 1, 30f, 1);

        bool any = SectionDirtyMath.SectionRange(
            bounds, SectionSize, mapWidth, mapHeight,
            out int minSx, out int minSz, out int maxSx, out int maxSz);

        Assert.That(any, Is.True);

        for (int sx = minSx; sx <= maxSx; sx++)
        {
            for (int sz = minSz; sz <= maxSz; sz++)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(SectionDirtyMath.SectionAnchor(sx, SectionSize), Is.InRange(0, mapWidth - 1));
                    Assert.That(SectionDirtyMath.SectionAnchor(sz, SectionSize), Is.InRange(0, mapHeight - 1));
                });
            }
        }
    }

    // A degenerate map or section size returns nothing rather than dividing by zero. Neither can
    // arise from a live Map, but SectionRange is a public pure function and this is cheaper than
    // trusting every future caller.
    [TestCase(0, 51, 51)]
    [TestCase(17, 0, 51)]
    [TestCase(17, 51, 0)]
    public void DegenerateDimensionsDirtyNothing(int sectionSize, int mapWidth, int mapHeight)
    {
        SectionDirtyMath.CellBounds bounds = new SectionDirtyMath.CellBounds(0, 0, 10, 10);

        Assert.That(
            SectionDirtyMath.SectionRange(
                bounds, sectionSize, mapWidth, mapHeight, out _, out _, out _, out _),
            Is.False);
    }

    private static void AssertSameBounds(
        SectionDirtyMath.CellBounds actual, SectionDirtyMath.CellBounds expected)
    {
        Assert.Multiple(() =>
        {
            Assert.That(actual.Any, Is.EqualTo(expected.Any));
            Assert.That(actual.MinX, Is.EqualTo(expected.MinX));
            Assert.That(actual.MinZ, Is.EqualTo(expected.MinZ));
            Assert.That(actual.MaxX, Is.EqualTo(expected.MaxX));
            Assert.That(actual.MaxZ, Is.EqualTo(expected.MaxZ));
        });
    }
}
