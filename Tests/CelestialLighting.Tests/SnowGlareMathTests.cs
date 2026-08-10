namespace CelestialLighting.Tests;

/// <summary>
/// Offline coverage for SnowGlareMath.cs — §24, the additive glare pass (issue #90). No
/// RimWorld/Unity assembly required, since the file is dependency-free.
///
/// The property these tests exist to hold is a PARTITION: AlbedoCavityMath.RecoveredDimming renders
/// as much of §21's daytime amplification as a multiply into a (1,1,1) ceiling can express, and
/// SnowGlareMath.UndrawableExcess returns exactly the remainder. Between them they must account for
/// the whole product, with nothing counted twice and nothing dropped — so most of what follows
/// asserts the two functions against the SAME (dimming, gain) pairs rather than testing this file
/// alone.
/// </summary>
[TestFixture]
public class SnowGlareMathTests
{
    private const float Tolerance = 1e-4f;

    // --- The residual itself ---

    // Every case pins the sea-level value AND its vacuum counterpart in one sweep, per the
    // convention Source/Vacuum.cs sets out. The vacuum answer is exactly 0 — no atmosphere, no cloud
    // base, no cavity to overflow anything — so it is asserted as an exact equality.
    //
    // The gains below are AlbedoCavityMath.CavityGain's own outputs (A(surface)/A(bare)), and the
    // dimmings are §13-shaped: ~0.18 for a plain overcast, heavier under precipitation.
    [TestCase(0.18f, 2.345f, 0.9229f)]   // fresh snow, thick overcast: the case #90 says is undrawable
    [TestCase(0.18f, 1.360f, 0.1152f)]   // settled snow, same deck: a modest overflow
    [TestCase(0.18f, 1.000f, 0.0000f)]   // bare ground: no cavity, so nothing to overflow
    [TestCase(0.18f, 1.070f, 0.0000f)]   // thin deck over snow: 0.82 * 1.07 == 0.877, no overflow
    [TestCase(0.00f, 1.070f, 0.0700f)]   // the SAME gain with no dimming DOES overflow — see below
    [TestCase(0.40f, 1.600f, 0.0000f)]   // heavy dimming eats the gain: 0.6 * 1.6 == 0.96, no overflow
    [TestCase(0.40f, 2.345f, 0.4070f)]   // same weather over fresh snow: overflows anyway
    public void UndrawableExcess_IsWhatTheMultiplyLaneCouldNotRender_AndIsZeroInVacuum(
        float dimming, float cavityGain, float expected)
    {
        Assert.That(
            SnowGlareMath.UndrawableExcess(dimming, cavityGain, false),
            Is.EqualTo(expected).Within(Tolerance),
            "(1 - dimming) * cavityGain - 1, floored at zero, at sea level");

        Assert.That(
            SnowGlareMath.UndrawableExcess(dimming, cavityGain, true),
            Is.EqualTo(0f),
            "no atmosphere means no cavity, so there is no overflow to draw");
    }

    // THE HEADLINE CLAIM OF ISSUE #90, stated as the ordering it says cannot currently be drawn.
    // Under the multiply lane alone these two conditions tie — both clamp to zero dimming, i.e.
    // clear-day parity — which is exactly the flattening #90 documents. The residual breaks the tie
    // in the direction the physics predicts, and that is the entire reason this subsystem exists.
    // WHY A CLEAR SKY'S SMALL CAVITY IS THE WHOLE OF WHAT §24 DRAWS THERE (issue #134). With no
    // dimming to spend, a clear sky's 1.07x does NOT fit under the multiply lane's ceiling — it
    // overflows completely, and the additive lane carries all of it. That was already true when the
    // gate upstream stopped Clear weather from ever reaching this function; what #134 changed is that
    // WeatherDimming now hands Clear a deck opacity from §22's cloud-cover fraction instead of §13's
    // abstention, so the gain arriving here is the partly-cloudy one and this arithmetic is what
    // renders.
    //
    // Kept as a named test rather than folded into the TestCase table because it is the case most
    // likely to be "corrected" to return zero for a clear sky — which would look like a tidy-up and
    // would silently switch the new behaviour back off from the far side of the codebase.
    [Test]
    public void AClearSkyGain_OverflowsCompletely_BecauseThereIsNoDimmingToAbsorbIt()
    {
        Assert.That(
            SnowGlareMath.UndrawableExcess(0f, 1.070f, false),
            Is.EqualTo(0.0700f).Within(Tolerance),
            "with no dimming, even a clear sky's small cavity overflows in full");
    }

    // ISSUE #134's LADDER, pinned end to end: §22's cloud-cover fraction -> cloud-base albedo ->
    // cavity gain -> residual, over the full-map fresh snow the live scenario builds. The fractions
    // are §22's own measured hourly values from cloud_cover.json rather than round numbers, so this
    // test fails if either §22's range or §21's albedo endpoints move under §24 without anyone
    // re-measuring what glare does on a partly-cloudy day.
    //
    // THE RESIDUAL IS EXACTLY `gain - 1` HERE, and that is the structural point rather than a
    // coincidence of these numbers: Clear weather contributes no dimming, so `(1 - 0) * gain - 1`
    // leaves the multiply lane with nothing it can render and hands §24 the entire cavity. It is the
    // only condition in the mod where that happens.
    [TestCase(0.1400f, 1.1482f)]
    [TestCase(0.2113f, 1.1933f)]
    [TestCase(0.2710f, 1.2346f)]   // §22's measured peak
    [TestCase(0.3556f, 1.2996f)]
    public void OnClearWeather_TheWholeCavityOverflows_SoTheResidualIsGainMinusOne(
        float cloudCoverFraction, float expectedGain)
    {
        // The same two calls WeatherDimming/SurfaceBuildup make on a Clear frame, with the surface
        // albedo of the full-map fresh snow the scenario lays down.
        float cloudAlbedo = AlbedoCavityMath.CloudBaseAlbedo(cloudCoverFraction);
        float gain = AlbedoCavityMath.CavityGain(
            AlbedoCavityMath.FreshSnowAlbedo, AlbedoCavityMath.BareGroundAlbedo, cloudAlbedo, false);

        Assert.That(
            gain,
            Is.EqualTo(expectedGain).Within(0.0005f),
            "§22's fraction reaches the cavity through CloudBaseAlbedo's clear->overcast lerp");

        Assert.That(
            SnowGlareMath.UndrawableExcess(0f, gain, false),
            Is.EqualTo(gain - 1f).Within(Tolerance),
            "no dimming means the multiply lane renders none of it and §24 renders all of it");

        Assert.That(
            AlbedoCavityMath.RecoveredDimming(0f, gain),
            Is.EqualTo(0f),
            "and DimmingFor still reports exactly zero dimming on Clear, as it always has");
    }

    [Test]
    public void SnowyOvercast_OverflowsFurtherThanSnowyClearSky_WhichIsTheInversionTheMultiplyLaneFlattens()
    {
        const float overcastDimming = 0.18f;
        const float snowyOvercastGain = 2.345f;   // fresh snow under a thick deck
        const float snowyClearGain = 1.070f;      // the same snow under a clear sky

        float overcastRecovered = AlbedoCavityMath.RecoveredDimming(overcastDimming, snowyOvercastGain);
        float clearRecovered = AlbedoCavityMath.RecoveredDimming(0f, snowyClearGain);

        Assert.That(
            overcastRecovered,
            Is.EqualTo(clearRecovered).Within(Tolerance),
            "the multiply lane renders both as clear-day parity — the tie #90 exists to break");

        Assert.That(
            SnowGlareMath.UndrawableExcess(overcastDimming, snowyOvercastGain, false),
            Is.GreaterThan(SnowGlareMath.UndrawableExcess(0f, snowyClearGain, false)),
            "the additive lane orders them the way the physics does");
    }

    // The partition property, asserted directly: whatever RecoveredDimming clamped away is what this
    // returns. Written as "the multiply lane saturated AND there is a residual" versus "the multiply
    // lane had headroom AND there is none", because the two must flip at the same point — a gap
    // between them would render as a visible discontinuity as a colony's snow accumulates, and an
    // overlap would double-count the amplification at the seam.
    [TestCase(0.18f, 1.000f)]
    [TestCase(0.18f, 1.070f)]
    [TestCase(0.18f, 1.2195f)]   // (1 - 0.18) * 1.2195 == 0.99999..., just under the ceiling
    [TestCase(0.18f, 1.360f)]
    [TestCase(0.18f, 2.345f)]
    [TestCase(0.40f, 1.600f)]
    [TestCase(0.00f, 1.070f)]
    public void TheTwoLanesPartitionTheSameProduct_WithNoGapAndNoOverlap(float dimming, float gain)
    {
        float recovered = AlbedoCavityMath.RecoveredDimming(dimming, gain);
        float excess = SnowGlareMath.UndrawableExcess(dimming, gain, false);

        bool multiplyLaneSaturated = recovered <= 0f;
        bool hasResidual = excess > 0f;

        Assert.That(
            hasResidual,
            Is.EqualTo(multiplyLaneSaturated && (1f - dimming) * gain > 1f),
            "a residual exists exactly when the multiply lane ran out of headroom");

        // And the two sum back to the unclamped product, which is the arithmetic statement of the
        // same thing: surviving == (1 - recoveredDimming) + excess whenever the lane saturated.
        if (hasResidual)
        {
            Assert.That(
                (1f - recovered) + excess,
                Is.EqualTo((1f - dimming) * gain).Within(Tolerance),
                "the rendered part plus the overflowed part is the whole amplification");
        }
    }

    // --- Residual to overlay alpha ---

    // THE REGRESSION FOR THE BUG THE LIVE HARNESS FOUND AND THESE OFFLINE TESTS ORIGINALLY MISSED.
    // The first cut scaled by skyGlow alone and asserted "night is zero" by feeding skyGlow 0 — which
    // passed, and was wrong about the game: §7 holds a night floor of starlight/airglow/moonlight,
    // §21 amplifies that floor over snow, and a snowed-in overcast night measured skyGlow well above
    // zero (snow_glare.json read an alpha of 0.0372 where it expected 0). The double-count the
    // original comment claimed to prevent was happening.
    //
    // So the case that matters is not "glow is zero" — that never happens on a snowy map — it is
    // "glow is ENTIRELY the night floor", which is what a real night looks like. Pinned here with a
    // floor at the amplified magnitude §21 actually produces rather than a token one.
    [TestCase(1.00f, 0.00f, 0.0055f)]   // noon, no floor to speak of
    [TestCase(1.00f, 0.17f, 0.0046f)]   // noon over snow: the floor is subtracted, and it is small
    [TestCase(0.50f, 0.17f, 0.0018f)]   // afternoon
    [TestCase(0.17f, 0.17f, 0.0000f)]   // night: all of the glow IS the floor, so no glare at all
    [TestCase(0.12f, 0.17f, 0.0000f)]   // deeper night: floor exceeds glow, and must not go negative
    [TestCase(0.00f, 0.00f, 0.0000f)]
    public void GlareAlpha_ScalesWithDaylightAboveTheNightFloor_SoASnowyNightIsExactlyZero(
        float skyGlow, float nightFloorGlow, float expected)
    {
        Assert.That(
            SnowGlareMath.GlareAlpha(
                0.0923f, skyGlow, nightFloorGlow,
                SnowGlareMath.DefaultIntensityScale, SnowGlareMath.MaxIntensity, false),
            Is.EqualTo(expected).Within(Tolerance));

        Assert.That(
            SnowGlareMath.GlareAlpha(
                0.0923f, skyGlow, nightFloorGlow,
                SnowGlareMath.DefaultIntensityScale, SnowGlareMath.MaxIntensity, true),
            Is.EqualTo(0f),
            "vacuum draws no glare at any hour");
    }

    // The subtraction itself, floored at zero. A floor above the current glow is not a contrived
    // input: §7's floor is what the sky is being held UP to, so during the handover the two cross,
    // and a negative daylight term would flip the glare's sign into a dark quad drawn additively —
    // which MoteGlow cannot express and would silently clamp, hiding the error.
    [TestCase(1.00f, 0.00f, 1.00f)]
    [TestCase(1.00f, 0.17f, 0.83f)]
    [TestCase(0.17f, 0.17f, 0.00f)]
    [TestCase(0.10f, 0.17f, 0.00f)]
    [TestCase(1.50f, 0.17f, 0.83f)]   // an out-of-range glow clamps before subtracting, not after
    public void DaylightAboveNightFloor_NeverGoesNegative(
        float skyGlow, float nightFloorGlow, float expected)
    {
        Assert.That(
            SnowGlareMath.DaylightAboveNightFloor(skyGlow, nightFloorGlow),
            Is.EqualTo(expected).Within(Tolerance));
    }

    // The ceiling clips what reaches the screen rather than reshaping the ramp below it — see
    // GlareAlpha's note on clamp order. Fed a residual far past anything vanilla weather can produce,
    // so the assertion is about the clamp and not about a plausible input.
    [Test]
    public void GlareAlpha_ClipsAtMaxIntensity_RatherThanWhitingOutTheScreen()
    {
        Assert.That(
            SnowGlareMath.GlareAlpha(
                50f, 1f, 0f, SnowGlareMath.DefaultIntensityScale, SnowGlareMath.MaxIntensity, false),
            Is.EqualTo(SnowGlareMath.MaxIntensity),
            "an absurd residual clips to the ceiling exactly");
    }

    // A zero or negative residual must produce a hard zero, because SnowGlareOverlay branches on
    // exactly this to skip the draw call entirely. "No snow costs nothing" is a claim about the frame
    // budget on every non-snowy map in the game, so it should be exact rather than approximately
    // true — a tiny positive alpha would still cost a material write and a full-map additive quad.
    [TestCase(0f)]
    [TestCase(-0.5f)]
    public void GlareAlpha_IsExactlyZero_WhenThereIsNoResidual_SoTheDrawCallIsSkipped(float excess)
    {
        Assert.That(
            SnowGlareMath.GlareAlpha(
                excess, 1f, 0f, SnowGlareMath.DefaultIntensityScale, SnowGlareMath.MaxIntensity, false),
            Is.EqualTo(0f));
    }
}
