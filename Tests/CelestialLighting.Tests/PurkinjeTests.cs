namespace CelestialLighting.Tests;

// Offline coverage for the pure Purkinje brightness->saturation falloff (Source/PurkinjeMath.cs),
// linked into this project via <Compile Include> so these exercise the exact code that ships.
// Focus is the curve's edge cases: the daylight plateau, the deep-night plateau, monotonicity of
// the hand-over ramp, the midpoint, and the saturation-multiplier endpoints.
[TestFixture]
public class PurkinjeTests
{
    private const float Tolerance = 1e-5f;

    // --- PurkinjeFactor: daylight plateau (no shift at/above onset) ---

    [TestCase(1.0f)]   // full daylight
    [TestCase(0.6f)]   // vanilla dusk threshold — cones still dominate
    [TestCase(0.35f)]  // §2 twilight peak — must stay untouched so golden hour reads warm, not grey
    [TestCase(PurkinjeMath.OnsetGlow)] // exactly at onset
    public void PurkinjeFactor_IsZeroAtOrAboveOnset(float glow)
    {
        Assert.That(PurkinjeMath.PurkinjeFactor(glow), Is.EqualTo(0f).Within(Tolerance));
    }

    // --- PurkinjeFactor: deep-night plateau (full shift at/below full) ---

    [TestCase(PurkinjeMath.FullGlow)] // exactly at full
    [TestCase(0.02f)]
    [TestCase(0f)]     // pitch black
    [TestCase(-0.1f)]  // defensive: below zero still clamps to full, never overshoots
    public void PurkinjeFactor_IsOneAtOrBelowFull(float glow)
    {
        Assert.That(PurkinjeMath.PurkinjeFactor(glow), Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void PurkinjeFactor_IsHalfAtMidpointGlow()
    {
        float midGlow = (PurkinjeMath.OnsetGlow + PurkinjeMath.FullGlow) / 2f;
        Assert.That(PurkinjeMath.PurkinjeFactor(midGlow), Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void PurkinjeFactor_RisesMonotonicallyAsGlowFalls()
    {
        // Sweep from bright to dark; the factor must never decrease as it gets darker.
        float previous = -1f;
        for (int i = 0; i <= 20; i++)
        {
            float glow = 1f - i * 0.05f; // 1.0 down to 0.0
            float factor = PurkinjeMath.PurkinjeFactor(glow);
            Assert.That(factor, Is.GreaterThanOrEqualTo(previous - Tolerance),
                $"factor decreased while darkening at glow={glow}");
            Assert.That(factor, Is.InRange(0f, 1f));
            previous = factor;
        }
    }

    // --- SaturationMultiplier: endpoints and range ---

    [TestCase(1.0f)]
    [TestCase(PurkinjeMath.OnsetGlow)]
    public void SaturationMultiplier_IsOneInDaylight(float glow)
    {
        Assert.That(PurkinjeMath.SaturationMultiplier(glow), Is.EqualTo(1f).Within(Tolerance));
    }

    [TestCase(PurkinjeMath.FullGlow)]
    [TestCase(0f)]
    public void SaturationMultiplier_HitsFloorAtFullRodVision(float glow)
    {
        Assert.That(PurkinjeMath.SaturationMultiplier(glow),
            Is.EqualTo(1f - PurkinjeMath.MaxSaturationDrop).Within(Tolerance));
    }

    [Test]
    public void SaturationMultiplier_StaysWithinFloorAndOne()
    {
        for (int i = 0; i <= 20; i++)
        {
            float glow = i * 0.05f;
            float mult = PurkinjeMath.SaturationMultiplier(glow);
            Assert.That(mult, Is.InRange(1f - PurkinjeMath.MaxSaturationDrop, 1f));
        }
    }
}
