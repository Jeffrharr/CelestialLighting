namespace CelestialLighting.Tests;

/// <summary>
/// Offline edge-case coverage for BloodMoonMath.cs (DESIGN.md §12) — no RimWorld/Unity assembly
/// required, since BloodMoonMath has no dependency on either. Complements ApiCompatibilityTests.cs,
/// which only checks that the vanilla members the adapter reads still exist; these tests check that
/// the recolour math itself is correct.
/// </summary>
[TestFixture]
public class BloodMoonMathTests
{
    private const float Tolerance = 0.0001f;

    // --- Luma ---

    [TestCase(0f, 0f, 0f, 0f)]
    [TestCase(1f, 1f, 1f, 1f)] // coefficients sum to 1, so white == 1
    [TestCase(1f, 0f, 0f, 0.299f)]
    [TestCase(0f, 1f, 0f, 0.587f)]
    [TestCase(0f, 0f, 1f, 0.114f)]
    public void Luma_MatchesRec601(float r, float g, float b, float expected)
    {
        Assert.That(BloodMoonMath.Luma(r, g, b), Is.EqualTo(expected).Within(Tolerance));
    }

    // --- NightFactor ---

    [TestCase(1f, 0f)]     // full day: no tint
    [TestCase(0.5f, 0f)]   // NightStartGlow: ramp start, still 0
    [TestCase(0.3f, 0.5f)] // midway through the ramp
    [TestCase(0.1f, 1f)]   // NightFullGlow: full night
    [TestCase(0f, 1f)]     // darker than full night: clamps, no overshoot
    [TestCase(0.6f, 0f)]   // brighter than ramp start: clamps to 0, no negative
    public void NightFactor_MatchesExpected(float sunGlow, float expected)
    {
        Assert.That(BloodMoonMath.NightFactor(sunGlow), Is.EqualTo(expected).Within(Tolerance));
    }

    // --- TintStrength ---

    [TestCase(0.1f, 0.85f, 0.85f)] // full night at default max
    [TestCase(0.3f, 0.85f, 0.425f)] // half ramp at default max
    [TestCase(1f, 0.85f, 0f)]      // daytime: no tint regardless of max
    [TestCase(0.1f, 0f, 0f)]       // max 0 disables the effect entirely
    [TestCase(0.1f, 2f, 1f)]       // max clamps to 1, doesn't overshoot
    public void TintStrength_MatchesExpected(float sunGlow, float maxTint, float expected)
    {
        Assert.That(BloodMoonMath.TintStrength(sunGlow, maxTint), Is.EqualTo(expected).Within(Tolerance));
    }

    // --- CrimsonTint ---

    [Test]
    public void CrimsonTint_IsUnchanged_AtZeroStrength()
    {
        BloodMoonMath.Rgb result = BloodMoonMath.CrimsonTint(0.2f, 0.3f, 0.5f, strength: 0f);
        Assert.That(result.R, Is.EqualTo(0.2f).Within(Tolerance));
        Assert.That(result.G, Is.EqualTo(0.3f).Within(Tolerance));
        Assert.That(result.B, Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void CrimsonTint_StaysBlack_ForBlackInput()
    {
        // Zero luma means the crimson target scales to zero: an unlit patch of sky must not be lit
        // up red out of nothing. Holds at full strength.
        BloodMoonMath.Rgb result = BloodMoonMath.CrimsonTint(0f, 0f, 0f, strength: 1f);
        Assert.That(result.R, Is.EqualTo(0f).Within(Tolerance));
        Assert.That(result.G, Is.EqualTo(0f).Within(Tolerance));
        Assert.That(result.B, Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void CrimsonTint_PreservesLuma_ForDimNightColor()
    {
        // A realistically dim, slightly-bluish night colour: the same-luma crimson stays well
        // within [0, 1] on every channel, so no clamp bites and brightness is preserved exactly.
        float r = 0.1f, g = 0.12f, b = 0.18f;
        float baseLuma = BloodMoonMath.Luma(r, g, b);
        BloodMoonMath.Rgb result = BloodMoonMath.CrimsonTint(r, g, b, strength: 1f);
        Assert.That(BloodMoonMath.Luma(result.R, result.G, result.B), Is.EqualTo(baseLuma).Within(Tolerance));
    }

    [Test]
    public void CrimsonTint_ShiftsTowardRed_ForBluishInput()
    {
        // The whole point: a silver-blue moonlit night reads red. Red channel goes up, green and
        // blue come down.
        float r = 0.1f, g = 0.12f, b = 0.18f;
        BloodMoonMath.Rgb result = BloodMoonMath.CrimsonTint(r, g, b, strength: 1f);
        Assert.That(result.R, Is.GreaterThan(r));
        Assert.That(result.G, Is.LessThan(g));
        Assert.That(result.B, Is.LessThan(b));
    }

    [Test]
    public void CrimsonTint_ClampsRedChannel_ForBrightInput()
    {
        // Bright mid-grey: a same-luma crimson would need R > 1, so the red channel clamps to 1
        // and green/blue still drop. The result is a saturated red rather than an out-of-range colour.
        BloodMoonMath.Rgb result = BloodMoonMath.CrimsonTint(0.5f, 0.5f, 0.5f, strength: 1f);
        Assert.That(result.R, Is.EqualTo(1f).Within(Tolerance));
        Assert.That(result.G, Is.LessThan(0.5f));
        Assert.That(result.B, Is.LessThan(0.5f));
    }

    [Test]
    public void CrimsonTint_IsMidpoint_AtHalfStrength()
    {
        // Half strength lands exactly halfway between the base colour and the full-strength target,
        // confirming the blend is a plain lerp.
        float r = 0.1f, g = 0.12f, b = 0.18f;
        BloodMoonMath.Rgb full = BloodMoonMath.CrimsonTint(r, g, b, strength: 1f);
        BloodMoonMath.Rgb half = BloodMoonMath.CrimsonTint(r, g, b, strength: 0.5f);
        Assert.That(half.R, Is.EqualTo((r + full.R) / 2f).Within(Tolerance));
        Assert.That(half.G, Is.EqualTo((g + full.G) / 2f).Within(Tolerance));
        Assert.That(half.B, Is.EqualTo((b + full.B) / 2f).Within(Tolerance));
    }

    [Test]
    public void CrimsonTint_ClampsStrengthAboveOne()
    {
        // Over-range strength must not extrapolate past the full-crimson target.
        float r = 0.1f, g = 0.12f, b = 0.18f;
        BloodMoonMath.Rgb full = BloodMoonMath.CrimsonTint(r, g, b, strength: 1f);
        BloodMoonMath.Rgb over = BloodMoonMath.CrimsonTint(r, g, b, strength: 5f);
        Assert.That(over.R, Is.EqualTo(full.R).Within(Tolerance));
        Assert.That(over.G, Is.EqualTo(full.G).Within(Tolerance));
        Assert.That(over.B, Is.EqualTo(full.B).Within(Tolerance));
    }
}
