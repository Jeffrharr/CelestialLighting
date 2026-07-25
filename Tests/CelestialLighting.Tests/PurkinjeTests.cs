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
    [TestCase(PurkinjeMath.OnsetGlow)] // exactly at onset == RimWorld's "fully lit" (0.5)
    public void PurkinjeFactor_IsZeroAtOrAboveOnset(float glow)
    {
        Assert.That(PurkinjeMath.PurkinjeFactor(glow), Is.EqualTo(0f).Within(Tolerance));
    }

    // A lamp-lit cell is the whole reason OnsetGlow is 0.5: Verse.GlowGrid.GroundGlowAt caps ordinary
    // artificial light at exactly that (`b = Mathf.Min(0.5f, b)`) and PlantProperties.growMinGlow is
    // 0.51, so a lamp-lit room is as bright as normal artificial light ever gets and must render at
    // full colour.
    [Test]
    public void PurkinjeFactor_IsZeroAtRimWorldsArtificialLightCap()
    {
        Assert.That(PurkinjeMath.PurkinjeFactor(0.5f), Is.EqualTo(0f).Within(Tolerance));
        Assert.That(PurkinjeMath.SaturationMultiplier(0.5f), Is.EqualTo(1f).Within(Tolerance));
    }

    // Golden hour is the constraint the ramp exponent was derived from. §2's twilight peak sits at
    // glow ~0.35, where §2 and §8 are warming the sky; a LINEAR ramp from an onset of 0.5 would drain
    // ~20% of the scene's colour there and grey out exactly the moment meant to read warm. The
    // ease-in keeps it under 5%, which is imperceptible.
    [Test]
    public void PurkinjeFactor_LeavesGoldenHourEssentiallyUntouched()
    {
        Assert.That(PurkinjeMath.PurkinjeFactor(0.35f), Is.LessThan(0.05f),
            "the twilight peak must keep ~95% of its saturation or golden hour reads grey");
        Assert.That(PurkinjeMath.SaturationMultiplier(0.35f), Is.GreaterThan(0.95f));
    }

    // The ease-in must not be so aggressive that the shift never arrives — it should be clearly
    // present once the scene is genuinely dim, well before the FullGlow plateau.
    [Test]
    public void PurkinjeFactor_IsWellDevelopedByDeepDusk()
    {
        Assert.That(PurkinjeMath.PurkinjeFactor(0.15f), Is.GreaterThan(0.4f));
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
    public void PurkinjeFactor_IsBelowHalfAtMidpointGlow()
    {
        // The ramp eases in rather than running linearly (see PurkinjeMath.RampExponent), so the
        // midpoint sits deliberately BELOW 0.5 — the curve holds colour through the brighter half
        // and spends its range in the genuinely dim part. Pinned as a band rather than a point so
        // the exponent can be retuned without rewriting the test, while still asserting the shape.
        float midGlow = (PurkinjeMath.OnsetGlow + PurkinjeMath.FullGlow) / 2f;
        float factor = PurkinjeMath.PurkinjeFactor(midGlow);
        Assert.That(factor, Is.InRange(0.1f, 0.4f));
        Assert.That(factor, Is.LessThan(0.5f), "an eased ramp must sit below the linear midpoint");
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
