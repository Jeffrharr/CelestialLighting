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
}
