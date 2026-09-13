using System.Collections.Generic;

namespace CelestialLighting.Tests;

// Offline unit tests for the pawn-shadow cache's invalidation arithmetic (see
// CelestialLightingFeatures.VectorLightShadowPawnCache's header for the two-trigger design this
// backs). VectorLightPawnShadows.DecideCacheHits combines these with a per-pawn position check that
// needs a live Pawn and cannot be driven from here -- what belongs in this project is the part that
// decides whether a CHANGED LAMP could possibly matter to a given cell, which is exactly the
// question LampCouldReach / PositionMayBeAffectedByChangedLamps answer.
[TestFixture]
public class PawnShadowCacheTests
{
    private static PawnShadowMath.LampChangeData Lamp(int cellX, int cellZ, float radius) =>
        new PawnShadowMath.LampChangeData { CellX = cellX, CellZ = cellZ, Radius = radius };

    // --- LampCouldReach: the reach circle itself ---

    // Distance is measured from the CELL'S CENTRE (cellX + 0.5, cellZ + 0.5), never from its corner
    // -- so even a pawn standing in the lamp's own cell is never distance zero, and a radius smaller
    // than that half-cell offset (~0.71) would wrongly call it unreached.
    [Test]
    public void PositionAtLampCellIsAlwaysReached()
    {
        Assert.That(PawnShadowMath.LampCouldReach(5, 5, 5, 5, radius: 1f), Is.True);
    }

    [Test]
    public void PositionWellInsideRadiusIsReached()
    {
        Assert.That(PawnShadowMath.LampCouldReach(10, 10, 10, 15, radius: 10f), Is.True);
    }

    [Test]
    public void PositionWellOutsideRadiusIsNotReached()
    {
        Assert.That(PawnShadowMath.LampCouldReach(0, 0, 100, 100, radius: 5f), Is.False);
    }

    // The boundary is inclusive (<=, not <), matching Gather's own distance-squared-vs-radius-squared
    // test -- see LampCouldReach's own header for why an exact match to that circle is the point.
    [Test]
    public void PositionExactlyOnBoundaryIsReached()
    {
        // Lamp at (0, 0.5) so the cell centre used internally (0.5, 0.5) sits distance 3.0 from a
        // pawn cell at (3, 0): a clean number so float rounding cannot flip the comparison.
        Assert.That(PawnShadowMath.LampCouldReach(3, 0, 0, 0, radius: 3f), Is.True);
    }

    [Test]
    public void PositionJustPastBoundaryIsNotReached()
    {
        Assert.That(PawnShadowMath.LampCouldReach(4, 0, 0, 0, radius: 3f), Is.False);
    }

    // --- PositionMayBeAffectedByChangedLamps: the per-pawn aggregate the cache actually consults ---

    [Test]
    public void NoChangedLampsNeverAffectsAnyPosition()
    {
        List<PawnShadowMath.LampChangeData> changed = new List<PawnShadowMath.LampChangeData>();

        Assert.That(PawnShadowMath.PositionMayBeAffectedByChangedLamps(0, 0, changed), Is.False);
    }

    [Test]
    public void ChangedLampOutOfReachDoesNotAffectPosition()
    {
        List<PawnShadowMath.LampChangeData> changed = new List<PawnShadowMath.LampChangeData>
        {
            Lamp(50, 50, radius: 5f),
        };

        Assert.That(PawnShadowMath.PositionMayBeAffectedByChangedLamps(0, 0, changed), Is.False);
    }

    [Test]
    public void ChangedLampInReachAffectsPosition()
    {
        List<PawnShadowMath.LampChangeData> changed = new List<PawnShadowMath.LampChangeData>
        {
            Lamp(0, 0, radius: 5f),
        };

        Assert.That(PawnShadowMath.PositionMayBeAffectedByChangedLamps(2, 2, changed), Is.True);
    }

    // Only the SECOND of two changed lamps reaches -- proves the check walks the whole list rather
    // than short-circuiting on the first entry's verdict.
    [Test]
    public void OnlyOneOfSeveralChangedLampsNeedsToReach()
    {
        List<PawnShadowMath.LampChangeData> changed = new List<PawnShadowMath.LampChangeData>
        {
            Lamp(100, 100, radius: 2f),
            Lamp(0, 0, radius: 5f),
        };

        Assert.That(PawnShadowMath.PositionMayBeAffectedByChangedLamps(1, 1, changed), Is.True);
    }

    // A lamp whose radius SHRANK still has to be checked at its OLD, larger radius -- this is what
    // VectorLightPawnShadows.UpdateChangedLamps' max(old, new) is for: a pawn standing where the
    // lamp used to reach but no longer does must still be invalidated, because its cached shadow was
    // built assuming that light was there.
    [Test]
    public void ShrunkenLampStillReachesItsOldRadius()
    {
        // Radius passed here stands in for max(oldRadius, newRadius) -- the caller's job, not this
        // function's -- so this test is really pinning that the aggregate check has no opinion about
        // which of the two radii produced the number it was handed.
        List<PawnShadowMath.LampChangeData> changed = new List<PawnShadowMath.LampChangeData>
        {
            Lamp(0, 0, radius: 8f),
        };

        Assert.That(PawnShadowMath.PositionMayBeAffectedByChangedLamps(7, 0, changed), Is.True);
    }
}
