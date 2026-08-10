namespace CelestialLighting.Tests;

// Offline unit tests for the §7b staleness fix's pure core (Source/SkyFalloffRedrawMath.cs, linked so
// this runs against the exact shipped file).
[TestFixture]
public class SkyFalloffRedrawMathTests
{
    [Test]
    public void ShouldRedraw_NoChange_False()
    {
        Assert.That(SkyFalloffRedrawMath.ShouldRedraw(0.5f, 0.5f, SkyFalloffRedrawMath.DefaultThreshold),
            Is.False);
    }

    [Test]
    public void ShouldRedraw_TinyDrift_False()
    {
        // Below the threshold, a rebuild is not yet worth its cost.
        Assert.That(SkyFalloffRedrawMath.ShouldRedraw(0.50f, 0.52f, SkyFalloffRedrawMath.DefaultThreshold),
            Is.False);
    }

    [Test]
    public void ShouldRedraw_PastThreshold_True()
    {
        Assert.That(SkyFalloffRedrawMath.ShouldRedraw(0.50f, 0.60f, SkyFalloffRedrawMath.DefaultThreshold),
            Is.True);
    }

    [Test]
    public void ShouldRedraw_ExactlyAtThreshold_True()
    {
        // The boundary is inclusive (>=), so a drift landing exactly on the threshold still redraws
        // rather than silently rounding down to "not yet".
        Assert.That(SkyFalloffRedrawMath.ShouldRedraw(0.50f, 0.55f, SkyFalloffRedrawMath.DefaultThreshold),
            Is.True);
    }

    [Test]
    public void ShouldRedraw_DiminishingAtDusk_StillTrue()
    {
        // The drift is symmetric — glow FALLING past the threshold (dusk) redraws exactly like glow
        // rising past it (dawn); the predicate has no notion of direction, only magnitude.
        Assert.That(SkyFalloffRedrawMath.ShouldRedraw(0.60f, 0.50f, SkyFalloffRedrawMath.DefaultThreshold),
            Is.True);
    }

    [Test]
    public void ShouldRedraw_ZeroThreshold_TrueOnAnyChange()
    {
        Assert.That(SkyFalloffRedrawMath.ShouldRedraw(0.5000f, 0.5001f, threshold: 0f),
            Is.True);
    }
}
