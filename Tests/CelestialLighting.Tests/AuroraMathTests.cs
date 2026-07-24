namespace CelestialLighting.Tests;

/// <summary>
/// Offline edge-case coverage for AuroraMath.cs (DESIGN.md §11) — no RimWorld/Unity assembly
/// required, since AuroraMath has no dependency on either. Checks the night-visibility ramp, the
/// condition fade in/out, the shimmer phase/colour, and that the tint stays green-dominant and
/// colour-only.
/// </summary>
[TestFixture]
public class AuroraMathTests
{
    private const float Tolerance = 0.0001f;

    // --- NightVisibility ---

    [TestCase(0.5f, 0f)]   // at MaxVisibleGlow: sky too bright, no aurora
    [TestCase(0.6f, 0f)]   // brighter still: clamps to 0, doesn't go negative
    [TestCase(0.1f, 1f)]   // at FullVisibilityGlow: fully visible
    [TestCase(0f, 1f)]     // pitch dark: clamps to 1, doesn't overshoot
    [TestCase(0.3f, 0.5f)] // midpoint of the 0.1..0.5 ramp
    public void NightVisibility_MatchesExpected(float sunGlow, float expected)
    {
        Assert.That(AuroraMath.NightVisibility(sunGlow), Is.EqualTo(expected).Within(Tolerance));
    }

    // --- ConditionRampFactor ---

    [Test]
    public void ConditionRampFactor_IsZero_AtConditionStart()
    {
        // ticksPassed == 0: the tint hasn't begun fading in yet.
        Assert.That(AuroraMath.ConditionRampFactor(0f, 100000f, AuroraMath.DefaultFadeTicks),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void ConditionRampFactor_IsOne_MidCondition()
    {
        // Well past fade-in, well before fade-out: full strength.
        Assert.That(AuroraMath.ConditionRampFactor(50000f, 50000f, AuroraMath.DefaultFadeTicks),
            Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void ConditionRampFactor_FadesInLinearly()
    {
        // Halfway through the fade-in window (and far from the end): 0.5.
        float ramp = AuroraMath.ConditionRampFactor(
            ticksPassed: AuroraMath.DefaultFadeTicks * 0.5f, ticksLeft: 100000f, fadeTicks: AuroraMath.DefaultFadeTicks);
        Assert.That(ramp, Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void ConditionRampFactor_FadesOutLinearly()
    {
        // Halfway through the fade-out window (and far from the start): 0.5.
        float ramp = AuroraMath.ConditionRampFactor(
            ticksPassed: 100000f, ticksLeft: AuroraMath.DefaultFadeTicks * 0.5f, fadeTicks: AuroraMath.DefaultFadeTicks);
        Assert.That(ramp, Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void ConditionRampFactor_PeaksBelowOne_ForConditionShorterThanBothFades()
    {
        // A condition only 1*fadeTicks long, sampled at its midpoint: fade-in (0.5) and fade-out
        // (0.5) meet, so it never reaches full strength — Min keeps it at 0.5, not >1.
        float half = AuroraMath.DefaultFadeTicks * 0.5f;
        float ramp = AuroraMath.ConditionRampFactor(half, half, AuroraMath.DefaultFadeTicks);
        Assert.That(ramp, Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void ConditionRampFactor_HandlesPermanent_HugeTicksLeft()
    {
        // Permanent conditions pass float.MaxValue for ticksLeft (see AuroraConditions.RampFor);
        // the fade-out term must clamp to 1 rather than overflow, holding full strength.
        float ramp = AuroraMath.ConditionRampFactor(100000f, float.MaxValue, AuroraMath.DefaultFadeTicks);
        Assert.That(ramp, Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void ConditionRampFactor_IsOne_WhenFadeTicksNonPositive()
    {
        // Defensive: a zero/negative fade window means "no fade", full strength immediately.
        Assert.That(AuroraMath.ConditionRampFactor(0f, 0f, 0f), Is.EqualTo(1f).Within(Tolerance));
    }

    // --- SkyTintStrength / OverlayTintStrength ---

    [Test]
    public void SkyTintStrength_IsZero_InDaylight()
    {
        // Bright sky (glow above MaxVisibleGlow) yields no tint even at full ramp.
        Assert.That(AuroraMath.SkyTintStrength(sunGlow: 0.9f, ramp: 1f), Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void SkyTintStrength_IsZero_WhenRampZero()
    {
        // Fully dark sky but the condition has fully faded: still no tint.
        Assert.That(AuroraMath.SkyTintStrength(sunGlow: 0f, ramp: 0f), Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void SkyTintStrength_IsMaxAtPeak()
    {
        // Pitch dark and mid-condition: peaks exactly at MaxSkyTintStrength.
        Assert.That(AuroraMath.SkyTintStrength(sunGlow: 0f, ramp: 1f),
            Is.EqualTo(AuroraMath.MaxSkyTintStrength).Within(Tolerance));
    }

    [Test]
    public void OverlayTintStrength_IsWeakerThanSky_AtSamePeak()
    {
        float sky = AuroraMath.SkyTintStrength(sunGlow: 0f, ramp: 1f);
        float overlay = AuroraMath.OverlayTintStrength(sunGlow: 0f, ramp: 1f);
        Assert.That(overlay, Is.LessThan(sky));
        Assert.That(overlay, Is.EqualTo(AuroraMath.MaxOverlayTintStrength).Within(Tolerance));
    }

    [Test]
    public void SkyTintStrength_ClampsRampAboveOne()
    {
        // Defensive: a ramp passed in above 1 must not push strength past the peak.
        Assert.That(AuroraMath.SkyTintStrength(sunGlow: 0f, ramp: 5f),
            Is.EqualTo(AuroraMath.MaxSkyTintStrength).Within(Tolerance));
    }

    // --- PhaseFromTicks ---

    [TestCase(0L, 0f)]
    [TestCase(4500L, 0.5f)]  // half of ShimmerPeriodTicks (9000)
    [TestCase(9000L, 0f)]    // full period wraps back to 0
    [TestCase(13500L, 0.5f)] // one and a half periods
    public void PhaseFromTicks_WrapsOverPeriod(long ticks, float expected)
    {
        Assert.That(AuroraMath.PhaseFromTicks(ticks, AuroraMath.ShimmerPeriodTicks),
            Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void PhaseFromTicks_IsZero_ForNonPositivePeriod()
    {
        Assert.That(AuroraMath.PhaseFromTicks(1234L, 0f), Is.EqualTo(0f).Within(Tolerance));
    }

    // --- ShimmerRedMix ---

    [TestCase(0f, 0f)]                 // start of cycle: pure green
    [TestCase(1f, 0f)]                 // end of cycle: back to pure green
    public void ShimmerRedMix_IsZero_AtCycleEnds(float phase, float expected)
    {
        Assert.That(AuroraMath.ShimmerRedMix(phase), Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void ShimmerRedMix_PeaksAtMaxRedMix_MidCycle()
    {
        // Phase 0.5 is the reddest point; cosine shaping puts the peak exactly at MaxRedMix.
        Assert.That(AuroraMath.ShimmerRedMix(0.5f), Is.EqualTo(AuroraMath.MaxRedMix).Within(Tolerance));
    }

    [Test]
    public void ShimmerRedMix_NeverExceedsMaxRedMix()
    {
        // Sample across the whole cycle: the red mix must stay green-dominant (bounded by MaxRedMix < 1).
        for (int i = 0; i <= 20; i++)
        {
            float phase = i / 20f;
            float mix = AuroraMath.ShimmerRedMix(phase);
            Assert.That(mix, Is.GreaterThanOrEqualTo(-Tolerance));
            Assert.That(mix, Is.LessThanOrEqualTo(AuroraMath.MaxRedMix + Tolerance));
        }
    }

    // --- AuroralColor / AuroralColorAtPhase ---

    [Test]
    public void AuroralColor_IsGreen_AtZeroMix()
    {
        AuroraMath.Rgb c = AuroraMath.AuroralColor(0f);
        Assert.That(c.R, Is.EqualTo(AuroraMath.OxygenGreen.R).Within(Tolerance));
        Assert.That(c.G, Is.EqualTo(AuroraMath.OxygenGreen.G).Within(Tolerance));
        Assert.That(c.B, Is.EqualTo(AuroraMath.OxygenGreen.B).Within(Tolerance));
    }

    [Test]
    public void AuroralColor_IsRed_AtFullMix()
    {
        AuroraMath.Rgb c = AuroraMath.AuroralColor(1f);
        Assert.That(c.R, Is.EqualTo(AuroraMath.OxygenRed.R).Within(Tolerance));
        Assert.That(c.G, Is.EqualTo(AuroraMath.OxygenRed.G).Within(Tolerance));
        Assert.That(c.B, Is.EqualTo(AuroraMath.OxygenRed.B).Within(Tolerance));
    }

    [Test]
    public void AuroralColor_IsGreenDominant_AcrossShimmer()
    {
        // Because the shimmer never reaches full red, green should stay the strongest channel at
        // every phase — the aurora reads green, occasionally warming toward red, never turning red.
        for (int i = 0; i <= 20; i++)
        {
            float phase = i / 20f;
            AuroraMath.Rgb c = AuroraMath.AuroralColorAtPhase(phase);
            Assert.That(c.G, Is.GreaterThan(c.R), $"green not dominant at phase {phase}");
            Assert.That(c.G, Is.GreaterThan(c.B), $"green not dominant at phase {phase}");
        }
    }

    [Test]
    public void AuroralColor_ClampsNegativeMixToGreen()
    {
        AuroraMath.Rgb c = AuroraMath.AuroralColor(-1f);
        Assert.That(c.G, Is.EqualTo(AuroraMath.OxygenGreen.G).Within(Tolerance));
    }
}
