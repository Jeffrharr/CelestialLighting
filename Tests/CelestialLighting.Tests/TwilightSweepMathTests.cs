namespace CelestialLighting.Tests;

// Offline coverage for the pure §26 twilight-sweep core (Source/TwilightSweepMath.cs and
// Source/TwilightSweepField.cs, issue #140), linked into this project via <Compile Include> so these
// exercise the exact code that ships. See those files' headers for why the boundary is drawn at all
// and why its timing rather than its geometry is the honest part.
[TestFixture]
public class TwilightSweepMathTests
{
    private const float Tolerance = 1e-3f;

    // --- The window ---

    [Test]
    public void SweepFloor_MatchesSkyColorTemperaturesOwnTwilightFloor()
    {
        // THE POINT OF THIS TEST is that TwilightSweepMath cannot reference SkyColorTemperature — the
        // pure core has no adapter-side dependencies — so SweepFloorDegrees is a DUPLICATE of
        // NightFadeFloorDegrees rather than a reference to it. A duplicate constant is exactly the
        // drift DESIGN.md §20/§20d warn about, and the whole reason §26's sweep finishes cleanly is
        // that it ends at the instant §8's tint reaches zero. If someone retunes §8's floor, this
        // fails rather than the sweep quietly finishing early and leaving an additive band hanging in
        // an already-black sky.
        Assert.That(TwilightSweepMath.SweepFloorDegrees,
            Is.EqualTo(SkyColorTemperature.NightFadeFloorDegrees).Within(Tolerance));
    }

    [TestCase(10f, 0f, TestName = "Sweep_HighSun_NotStarted")]
    [TestCase(0.1f, 0f, TestName = "Sweep_JustAboveHorizon_NotStarted")]
    [TestCase(0f, 0f, TestName = "Sweep_AtHorizon_AtTheAntiSolarEdge")]
    [TestCase(-1.5f, 0.25f, TestName = "Sweep_QuarterCrossed")]
    [TestCase(-3f, 0.5f, TestName = "Sweep_HalfCrossed")]
    [TestCase(-6f, 1f, TestName = "Sweep_AtTheFloor_FullyCrossed")]
    [TestCase(-20f, 1f, TestName = "Sweep_DeepNight_StaysCrossed")]
    public void SweepPosition_RunsLinearlyFromHorizonToFloor(float elevation, float expected)
    {
        Assert.That(TwilightSweepMath.SweepPosition(elevation, inVacuum: false),
            Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void SweepPosition_IsConstantSpeed_WhichIsTheWholeVisualClaim()
    {
        // Equal steps of elevation must move the boundary equally far. §26's thesis is that MOTION
        // reads where brightness does not (epic #103), and an edge that accelerated or stalled
        // mid-crossing would read as a stutter rather than as dusk — so "linear" here is a
        // requirement of the effect, not an implementation convenience.
        float a = TwilightSweepMath.SweepPosition(-1f, inVacuum: false);
        float b = TwilightSweepMath.SweepPosition(-2f, inVacuum: false);
        float c = TwilightSweepMath.SweepPosition(-3f, inVacuum: false);

        Assert.That(b - a, Is.EqualTo(c - b).Within(Tolerance));
    }

    [Test]
    public void SweepPosition_InVacuum_IsZeroAtEveryElevation()
    {
        // No air, no antitwilight arch: the band exists because the atmosphere scatters light into
        // the sightline above the shadow, and there is nothing above an airless horizon to light.
        // Pinned across the window rather than at one elevation, per Vacuum.cs's convention of
        // sweeping the vacuum value beside its sea-level counterpart.
        for (float elevation = 0f; elevation >= -6f; elevation -= 0.5f)
        {
            Assert.That(TwilightSweepMath.SweepPosition(elevation, inVacuum: true),
                Is.EqualTo(0f), $"elevation {elevation}");
        }
    }

    [TestCase(0f, 0f, TestName = "Envelope_AtSunset_IsZero")]
    [TestCase(0.5f, 1f, TestName = "Envelope_MidWindow_IsFull")]
    [TestCase(1f, 0f, TestName = "Envelope_AtTheFloor_IsZero")]
    public void WindowEnvelope_IsZeroAtBothEnds(float sweep, float expected)
    {
        // Zero at both ends is what makes a step impossible at either boundary, at any latitude.
        // A step is the failure mode that would read as a bug rather than as an effect, which for a
        // prototype shipping off is the difference between "not yet convincing" and "obviously wrong".
        Assert.That(TwilightSweepMath.WindowEnvelope(sweep), Is.EqualTo(expected).Within(Tolerance));
    }

    // --- The band ---

    [Test]
    public void Intensity_BehindTheBoundary_IsExactlyZero()
    {
        // Inside Earth's shadow nothing is added. An additive pass cannot darken, so "shadow" here
        // has to mean "no contribution" — and it has to be EXACTLY zero rather than merely small,
        // because the contrast against the lit side is the entire readable signal.
        Assert.That(TwilightSweepMath.Intensity(0.1f, sweep: 0.6f, amplitude: 1f), Is.EqualTo(0f));
    }

    [Test]
    public void Intensity_AheadOfTheBoundary_IsPositive()
    {
        Assert.That(TwilightSweepMath.Intensity(0.7f, sweep: 0.4f, amplitude: 1f), Is.GreaterThan(0f));
    }

    [Test]
    public void Intensity_PeaksNearTheBoundary_NotAtTheSunwardEdge()
    {
        // The Belt of Venus rides ON the shadow's top edge; if the peak sat at the sunward map edge
        // instead, §26 would just be a static horizon glow that happened to fade in — which is what
        // the flat lanes already do. Sampled just ahead of the boundary rather than at it, because
        // the soft trailing edge deliberately holds the value down to half at the boundary itself.
        const float sweep = 0.4f;
        float belt = TwilightSweepMath.Intensity(sweep + 0.08f, sweep, amplitude: 1f);
        float farSide = TwilightSweepMath.Intensity(0.95f, sweep, amplitude: 1f);

        Assert.That(belt, Is.GreaterThan(farSide));
    }

    [Test]
    public void Intensity_RisesMonotonicallyAcrossTheTrailingEdge()
    {
        // The soft edge has to be genuinely monotone: a non-monotone ramp would show a dark line
        // inside the lit side, which reads as a seam — the artifact issue #140 names as the thing
        // most likely to sink the feature.
        const float sweep = 0.5f;
        float previous = -1f;

        for (float p = sweep - TwilightSweepMath.EdgeSoftness; p <= sweep; p += 0.01f)
        {
            float value = TwilightSweepMath.Intensity(p, sweep, amplitude: 1f);
            Assert.That(value, Is.GreaterThanOrEqualTo(previous), $"p {p}");
            previous = value;
        }
    }

    [Test]
    public void Intensity_ScalesLinearlyWithAmplitude()
    {
        // The amplitude is the one knob a live harness sweep moves within a single boot
        // (TwilightSweep.AmplitudeScale), so "twice the amplitude is twice the alpha" has to hold or
        // frames captured at different settings are not comparable.
        float single = TwilightSweepMath.Intensity(0.7f, sweep: 0.3f, amplitude: 0.1f);
        float doubled = TwilightSweepMath.Intensity(0.7f, sweep: 0.3f, amplitude: 0.2f);

        Assert.That(doubled, Is.EqualTo(single * 2f).Within(Tolerance));
    }

    // --- The colour, and why it is anchored to the boundary ---

    [Test]
    public void Warmth_AtTheBoundary_IsTheBeltsOwnHue()
    {
        // At the boundary the belt is at full strength and the horizon glow is nearly absent, so the
        // intensity-weighted hue should sit close to BeltWarmth — salmon-pink — rather than at
        // §19c's pure anti-solar magenta. The tolerance is loose because the glow does contribute a
        // little even here; what is being pinned is which source owns the colour, not an exact value.
        Assert.That(TwilightSweepMath.Warmth(0.3f, sweep: 0.3f),
            Is.EqualTo(TwilightSweepMath.BeltWarmth).Within(0.05f));
    }

    [Test]
    public void Warmth_AtTheSunwardEdge_IsFullyHot()
    {
        // Far ahead of the belt only the horizon glow is left, so the hue is §8's reddened tint with
        // nothing mixed into it.
        Assert.That(TwilightSweepMath.Warmth(1f, sweep: 0.3f), Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void Warmth_CrossesOverWhereTheGlowOvertakesTheBelt()
    {
        // The two lights hand over to each other by which is brighter, with no tuned crossover point.
        // Pinning the ordering rather than the position is what keeps this test meaningful if the
        // weights are retuned — which they will be, since BeltWeight/GlowWeight is explicitly a taste
        // call the live A/B settles.
        const float sweep = 0.3f;
        float atBelt = TwilightSweepMath.Warmth(sweep, sweep);
        float midway = TwilightSweepMath.Warmth(sweep + (TwilightSweepMath.BeltWidth * 0.75f), sweep);
        float farSide = TwilightSweepMath.Warmth(0.95f, sweep);

        Assert.That(midway, Is.GreaterThan(atBelt));
        Assert.That(farSide, Is.GreaterThan(midway));
    }

    [Test]
    public void Warmth_IsNeverColderThanTheBeltAnywhereItIsDrawn()
    {
        // §19c's pure hue is a near-white magenta, so anything that pushed the drawn colour BELOW
        // BeltWarmth would be reintroducing the colourless-streak bug the preview caught. Swept
        // across the whole band rather than sampled, because the failure was a region rather than a
        // point.
        const float sweep = 0.4f;

        for (float p = sweep; p <= 1f; p += 0.01f)
        {
            Assert.That(TwilightSweepMath.Warmth(p, sweep),
                Is.GreaterThanOrEqualTo(TwilightSweepMath.BeltWarmth - Tolerance), $"p {p}");
        }
    }

    [Test]
    public void Warmth_AtAFIXEDPLACE_ChangesAsTheBoundaryPasses()
    {
        // THIS IS THE TEST THAT SEPARATES A SUNSET FROM A STAIN. If warmth were simply the axis
        // position, the pink would sit on the same patch of map all evening while only the alpha
        // moved — a permanently purple east side. Anchoring it to the boundary means one cell's
        // colour changes as the boundary sweeps past it, which is what a real twilight does.
        float early = TwilightSweepMath.Warmth(0.5f, sweep: 0.0f);
        float late = TwilightSweepMath.Warmth(0.5f, sweep: 0.4f);

        Assert.That(late, Is.LessThan(early));
    }

    // --- The deck offset: issue #140's depth half ---

    [Test]
    public void DeckSweep_LagsTheGround_BecauseTheShadowReachesTheDeckLater()
    {
        // Earth's shadow reaches a cloud base at height h later than it reaches the ground, so the
        // deck's boundary is BEHIND the ground's — the clouds are still catching light where the
        // ground has already gone out. That gap is the parallax the depth question asked for.
        const float elevation = -2f;
        float ground = TwilightSweepMath.SweepPosition(elevation, inVacuum: false);
        float deck = TwilightSweepMath.DeckSweepPosition(
            elevation, CloudUnderlightMath.ShadowEntryDepressionDegrees(4000f), inVacuum: false);

        Assert.That(deck, Is.LessThan(ground));
    }

    [Test]
    public void DeckSweep_LagsFurtherForAHigherDeck()
    {
        // The ordering issue #88 is built around, rendered spatially: a 10 km cirrus stays lit
        // noticeably longer than a 1 km stratus, so its boundary sits further back. Same input,
        // ordered output, from §23's own geometry rather than a tuned offset.
        const float elevation = -2f;
        float low = TwilightSweepMath.DeckSweepPosition(
            elevation, CloudUnderlightMath.ShadowEntryDepressionDegrees(1000f), inVacuum: false);
        float high = TwilightSweepMath.DeckSweepPosition(
            elevation, CloudUnderlightMath.ShadowEntryDepressionDegrees(10000f), inVacuum: false);

        Assert.That(high, Is.LessThan(low));
    }

    [Test]
    public void DeckSweep_WithAGroundLevelDeck_IsTheGroundSweep()
    {
        // The degenerate case has to collapse exactly, or the two lanes would disagree about where
        // the boundary is on a map whose weather declares no altitude at all.
        const float elevation = -2.5f;
        Assert.That(TwilightSweepMath.DeckSweepPosition(elevation, 0f, inVacuum: false),
            Is.EqualTo(TwilightSweepMath.SweepPosition(elevation, inVacuum: false)).Within(Tolerance));
    }

    // --- LitFraction: what §25's sheets consume ---

    [Test]
    public void LitFraction_IsTheBandShapeWithoutTheAmplitude()
    {
        // §25 does not want §26's alpha added to its clouds; it wants the SHAPE, so it can pick its
        // own colour between "still catching light" and "gone out" while CloudSheetMath.
        // SheetBrightness keeps sole ownership of how bright a cloud is (and stays keyed on sky glow,
        // so eclipses darken clouds for free). Pinning the identity keeps a later "optimisation" from
        // folding an amplitude into it and silently coupling the two lanes' strengths.
        Assert.That(TwilightSweepMath.LitFraction(0.7f, sweep: 0.3f),
            Is.EqualTo(TwilightSweepMath.Intensity(0.7f, 0.3f, 1f)).Within(Tolerance));
    }

    [Test]
    public void LitFraction_IsZeroBehindTheBoundaryAndPositiveAheadOfIt()
    {
        // The whole visual point of the deck offset: sheets behind the boundary have gone out, sheets
        // ahead of it are still lit. If this were nonzero behind, the deck would warm together again
        // and the parallax would be gone.
        Assert.That(TwilightSweepMath.LitFraction(0.1f, sweep: 0.6f), Is.EqualTo(0f));
        Assert.That(TwilightSweepMath.LitFraction(0.8f, sweep: 0.6f), Is.GreaterThan(0f));
    }

    [Test]
    public void ASheetIsStillLitWhereTheGroundBesideItHasGoneOut()
    {
        // THE DEPTH CLAIM, END TO END, as one assertion. Take a point the GROUND's boundary has
        // already passed, and check that a 10 km cirrus deck over that same point is still catching
        // light — because its own boundary lags. This is the thing a viewer is meant to see, and it
        // is worth pinning as a conjunction rather than trusting that two separately-correct
        // functions compose the way the design says.
        const float elevation = -2f;
        const float axisPosition = 0.2f;

        float groundSweep = TwilightSweepMath.SweepPosition(elevation, inVacuum: false);
        float deckSweep = TwilightSweepMath.DeckSweepPosition(
            elevation, CloudUnderlightMath.ShadowEntryDepressionDegrees(10000f), inVacuum: false);

        Assert.That(TwilightSweepMath.LitFraction(axisPosition, groundSweep), Is.EqualTo(0f),
            "the ground here should already be in shadow");
        Assert.That(TwilightSweepMath.LitFraction(axisPosition, deckSweep), Is.GreaterThan(0f),
            "the cirrus deck above it should still be lit");
    }

    // --- The projection onto the sun axis ---

    [TestCase(0f, 1f, TestName = "Axis_DueNorth")]
    [TestCase(90f, 0f, TestName = "Axis_DueEast")]
    public void SunwardAxis_FollowsCloudFieldsOwnConvention(float azimuth, float expectedV)
    {
        // Same convention as CloudField.GradientAxis — u follows +x (east), v follows +z (north),
        // azimuth clockwise from north — but unrounded. If these two ever disagreed, §26's band and
        // §23b's gradient would claim different suns on one screen.
        TwilightSweepField.SunwardAxis(azimuth, out float axisU, out float axisV);
        CloudField.GradientAxis(azimuth, out int latticeU, out int latticeV);

        Assert.That(axisV, Is.EqualTo(expectedV).Within(Tolerance));
        Assert.That(MathF.Sign(axisU), Is.EqualTo(latticeU == 0 ? MathF.Sign(axisU) : latticeU));
        Assert.That(MathF.Sign(axisV), Is.EqualTo(latticeV == 0 ? MathF.Sign(axisV) : latticeV));
    }

    [Test]
    public void SunwardAxis_NaNAzimuth_FallsBackToDueNorthRatherThanVanishing()
    {
        // A NaN axis bakes a field of zero bytes, i.e. the feature silently absent. Falling back
        // keeps the band on screen where it can be SEEN to be wrong — the same reasoning
        // CloudField.GradientAxis records for its own (0,0) fallback.
        TwilightSweepField.SunwardAxis(float.NaN, out float axisU, out float axisV);

        Assert.That(axisU, Is.EqualTo(0f));
        Assert.That(axisV, Is.EqualTo(1f));
    }

    [Test]
    public void AxisPosition_SpansTheFullRangeCornerToCorner_OnADiagonalSun()
    {
        // WITHOUT the (|u| + |v|) normalisation a diagonal axis runs out of range before reaching the
        // far corner, and the sweep finishes while a wedge of map is still lit — a bug that only
        // appears on diagonal azimuths, i.e. exactly the one a test written on a north-south sun
        // would miss. 45 degrees is the worst case.
        TwilightSweepField.SunwardAxis(45f, out float axisU, out float axisV);

        float antiSolarCorner = TwilightSweepField.AxisPosition(0f, 0f, axisU, axisV);
        float sunwardCorner = TwilightSweepField.AxisPosition(1f, 1f, axisU, axisV);
        float centre = TwilightSweepField.AxisPosition(0.5f, 0.5f, axisU, axisV);

        Assert.That(antiSolarCorner, Is.EqualTo(0f).Within(Tolerance));
        Assert.That(sunwardCorner, Is.EqualTo(1f).Within(Tolerance));
        Assert.That(centre, Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void AxisPosition_OnACardinalSun_AlsoSpansTheFullRange()
    {
        TwilightSweepField.SunwardAxis(0f, out float axisU, out float axisV);

        Assert.That(TwilightSweepField.AxisPosition(0.5f, 0f, axisU, axisV),
            Is.EqualTo(0f).Within(Tolerance));
        Assert.That(TwilightSweepField.AxisPosition(0.5f, 1f, axisU, axisV),
            Is.EqualTo(1f).Within(Tolerance));
    }

    // --- The bake ---

    [Test]
    public void WriteRgba_LeavesTheShadowSideAtZeroAlphaAndLightsTheSunwardSide()
    {
        const int size = 16;
        byte[] pixels = new byte[size * size * 4];

        // Sun due north, so v runs from anti-solar (row 0) to sunward (last row), and the boundary
        // is halfway across.
        TwilightSweepField.SunwardAxis(0f, out float axisU, out float axisV);
        TwilightSweepField.WriteRgba(
            pixels, size, size, axisU, axisV, sweep: 0.5f, amplitude: 1f,
            hotR: 1f, hotG: 0.5f, hotB: 0.2f, coolR: 0.4f, coolG: 0.2f, coolB: 0.6f);

        Assert.That(pixels[3], Is.EqualTo(0), "anti-solar row should be in shadow");
        Assert.That(pixels[(((size - 1) * size) + 0) * 4 + 3], Is.GreaterThan(0),
            "sunward row should be lit");
    }

    [Test]
    public void WriteRgba_UndersizedBuffer_WritesNothingRatherThanThrowing()
    {
        // The overlay reuses one buffer forever, so a mismatch can only arrive via a resolution
        // change — but it would arrive on a draw path inside Map.MapUpdate, where an exception is a
        // per-frame red wall rather than a one-off. Returning leaves the previous frame's texture up.
        byte[] tooSmall = new byte[16];

        Assert.DoesNotThrow(() => TwilightSweepField.WriteRgba(
            tooSmall, 16, 16, 0f, 1f, 0.5f, 1f, 1f, 1f, 1f, 0f, 0f, 0f));
    }
}
