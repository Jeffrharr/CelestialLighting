namespace CelestialLighting.Tests;

// Offline unit tests for §7c's pure core (Source/NativeSkyFalloffMath.cs, linked into this project so
// these run against the exact shipped file). Deliberately mirrors IndoorOcclusionMathTests'
// AmbientLightSkyFraction cases below, one-for-one, since the two formulas share the same shape by
// design — see NativeSkyFalloffMath's header for why they are not literally the same code.
[TestFixture]
public class NativeSkyFalloffMathTests
{
    private const float Tolerance = 1e-5f;

    [Test]
    public void FractionAt_NoSkyGlow_IsZero()
    {
        // A genuinely pitch-black night (curSkyGlow == 0) yields zero redistributed light regardless
        // of depth — mirrors AmbientLightSkyFraction's own early-out and Ambient Light's stated design
        // intent (dynamically lightens based on OUTDOOR sky brightness; nothing to redistribute once
        // the source itself is dark).
        Assert.That(NativeSkyFalloffMath.FractionAt(depth: 1, curSkyGlow: 0f, maxDepth: 12, passThroughPercent: 55f),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void FractionAt_UnreachedCell_IsZero()
    {
        // depth <= 0 means the BFS never reached this cell (unroofed, or beyond maxDepth) — no
        // fraction to compute there.
        Assert.That(NativeSkyFalloffMath.FractionAt(depth: 0, curSkyGlow: 1f, maxDepth: 12, passThroughPercent: 55f),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void FractionAt_AtTheOpening_IsPassThroughTimesSkyGlow()
    {
        // depth 1, the cell right beside the opening: the depth term is 1/maxDepth, close to but not
        // quite 1, so the fraction sits just under curSkyGlow * passThrough.
        float maxDepth = 12f;
        float passThrough = 0.55f;
        float expected = 1f * passThrough * (1f - 1f / maxDepth);
        Assert.That(NativeSkyFalloffMath.FractionAt(depth: 1, curSkyGlow: 1f, maxDepth: 12, passThroughPercent: 55f),
            Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void FractionAt_AtMaxDepth_IsZero()
    {
        // depthFraction clamps to exactly 1 at maxDepth, zeroing the falloff term — the BFS's reach has
        // a hard edge, not an asymptote.
        Assert.That(NativeSkyFalloffMath.FractionAt(depth: 12, curSkyGlow: 1f, maxDepth: 12, passThroughPercent: 55f),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void FractionAt_BeyondMaxDepth_ClampsRatherThanGoingNegative()
    {
        Assert.That(NativeSkyFalloffMath.FractionAt(depth: 20, curSkyGlow: 1f, maxDepth: 12, passThroughPercent: 55f),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void FractionAt_ScalesLinearlyWithCurSkyGlow()
    {
        float atFullGlow = NativeSkyFalloffMath.FractionAt(depth: 3, curSkyGlow: 1f, maxDepth: 12, passThroughPercent: 55f);
        float atHalfGlow = NativeSkyFalloffMath.FractionAt(depth: 3, curSkyGlow: 0.5f, maxDepth: 12, passThroughPercent: 55f);

        Assert.That(atHalfGlow, Is.EqualTo(atFullGlow * 0.5f).Within(Tolerance));
    }

    [Test]
    public void FractionAt_ZeroOrNegativeMaxDepth_DoesNotDivideByZero()
    {
        // AmbientLightFalloffNoSky's own guard (clampedMaxDepth = maxDepth < 1 ? 1 : maxDepth) mirrored
        // here — a misconfigured slider must degrade gracefully, not throw or produce NaN/Infinity.
        Assert.That(NativeSkyFalloffMath.FractionAt(depth: 1, curSkyGlow: 1f, maxDepth: 0, passThroughPercent: 55f),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void FractionAt_ZeroPassThrough_IsZeroEverywhere()
    {
        Assert.That(NativeSkyFalloffMath.FractionAt(depth: 1, curSkyGlow: 1f, maxDepth: 12, passThroughPercent: 0f),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void FractionAt_FullPassThroughAtOpening_ApproachesFullSkyGlow()
    {
        // depth 1 of a large maxDepth: depthFraction is near-zero, so the fraction approaches
        // curSkyGlow * passThrough almost exactly.
        float result = NativeSkyFalloffMath.FractionAt(depth: 1, curSkyGlow: 1f, maxDepth: 1000, passThroughPercent: 100f);
        Assert.That(result, Is.EqualTo(1f).Within(0.01f));
    }

    // --- BlocksFlood / AffectsFlood: which cells stop the BFS, and what has to invalidate it ---
    //
    // Cases are named by the def that motivates them, because the whole point of this predicate is
    // which real buildings fall on which side. The (blocksLight, holdsRoof) shape of each is the one
    // read out of the shipped Core XML.

    // A Vent: Impassable, fillPercent 1, blockLight true, holdsRoof FALSE. The regression this fixture
    // exists for -- the old rule required holdsRoof, so this was crossed like an open doorway and a
    // sealed room with a vent in its wall was lit by the sky through it.
    [TestCase(true, false, true, TestName = "BlocksFlood_Vent_Blocks")]
    // A granite Wall: blockLight true, not a door. Unchanged by the fix, and the control the live
    // scenario pins beside the vent.
    [TestCase(true, false, true, TestName = "BlocksFlood_Wall_Blocks")]
    // A wood Door: blockLight true, but a door. The flood must still cross a threshold -- DoorLeakMath
    // is what dims it, not this predicate. Core's FenceGate is the same shape and the same answer.
    [TestCase(true, true, false, TestName = "BlocksFlood_Door_DoesNotBlock")]
    // A glass wall (Vanilla Furniture Expanded - Architect's VFEArch_CellWall): holdsRoof true but
    // blockLight FALSE. Crossable, which is the behaviour glass_wall_leak2.json pins live.
    [TestCase(false, false, false, TestName = "BlocksFlood_GlassWall_DoesNotBlock")]
    public void BlocksFlood_MatchesVanillasOwnBlockerSet(bool blocksLight, bool isDoor, bool expected)
    {
        Assert.That(NativeSkyFalloffMath.BlocksFlood(blocksLight, isDoor), Is.EqualTo(expected));
    }

    [TestCase(true, false, true, TestName = "AffectsFlood_Vent_Invalidates")]
    [TestCase(true, false, true, TestName = "AffectsFlood_Wall_Invalidates")]
    // A door changes no BlocksFlood answer but does change the crossing multiplier, so it still has to
    // dirty the grid -- this is why the invalidation set is a union rather than the blocker set.
    [TestCase(true, true, true, TestName = "AffectsFlood_Door_Invalidates")]
    // A glass wall crosses freely whether it is there or not, and it is not a door, so nothing about
    // the flood changes when one is built.
    [TestCase(false, false, false, TestName = "AffectsFlood_GlassWall_DoesNot")]
    // An ordinary chair or a dropped item: neither blocks light nor is a door. The reason this
    // predicate is a filter at all rather than an unconditional MarkDirty on every Building spawn.
    [TestCase(false, false, false, TestName = "AffectsFlood_Furniture_DoesNot")]
    public void AffectsFlood_IsTheUnionOfBlockersAndDoors(bool blocksLight, bool isDoor, bool expected)
    {
        Assert.That(NativeSkyFalloffMath.AffectsFlood(blocksLight, isDoor), Is.EqualTo(expected));
    }

    [Test]
    public void AffectsFlood_IsTrueWhereverBlocksFloodIs()
    {
        // The one relationship between the two that must hold whatever either says: a cell that stops
        // the flood must also be one whose arrival or removal invalidates the cached grid. Asserted
        // over the whole 2x2 input space rather than by inspection, since the two live in different
        // files at the call site (NativeSkyFalloffGrid vs Patch_SkyFalloffDirty) and drift silently.
        foreach (bool blocksLight in new[] { false, true })
        {
            foreach (bool isDoor in new[] { false, true })
            {
                if (NativeSkyFalloffMath.BlocksFlood(blocksLight, isDoor))
                {
                    Assert.That(NativeSkyFalloffMath.AffectsFlood(blocksLight, isDoor), Is.True,
                        $"blocksLight={blocksLight}, isDoor={isDoor} blocks the flood but would not dirty the grid");
                }
            }
        }
    }

    [Test]
    public void DefaultPassThroughPercent_IsLowerThanAmbientLights55_PerPlaytestFeedback()
    {
        // Pinned rather than left to drift: a live playtester found the shipped defaults (originally
        // matched to Ambient Light's own 55f) lit up a whole roofed room rather than grading near the
        // door, which is why NativeSkyFalloffSettings exists as a slider at all. If this constant moves
        // again, NativeSkyFalloffSettings.Defaults and CelestialLightingSettings' field initializer both
        // need to move with it — they read this constant, not a hardcoded literal, so a rebuild alone
        // will not catch a silent revert here.
        Assert.That(NativeSkyFalloffMath.DefaultPassThroughPercent, Is.EqualTo(25f).Within(Tolerance));
    }
}
