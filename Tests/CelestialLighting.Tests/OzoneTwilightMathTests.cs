namespace CelestialLighting.Tests;

/// <summary>
/// Offline edge-case coverage for OzoneTwilightMath.cs (subsystem 19, polar night blue) — no
/// RimWorld/Unity assembly required, since the file has no dependency on either. Complements
/// ApiCompatibilityTests.cs (which only checks vanilla members still exist); these check that our
/// own absorption math is correct, that it is actually VISIBLE through the multiply overlay, and
/// that the reachability gate is correctness-preserving rather than merely plausible.
/// </summary>
[TestFixture]
public class OzoneTwilightMathTests
{
    private const float Tolerance = 0.0005f;

    // Vanilla's Clear night sky colour, quoted from NightDesaturationMath's dead-end note. Used by
    // the visibility tests below to reproduce what the multiply overlay actually does to the ground.
    private const float VanillaNightR = 0.482f;
    private const float VanillaNightG = 0.603f;
    private const float VanillaNightB = 0.682f;

    // The adapter's sky blend (Patch_PolarNightBlue.SkyBlend). Mirrored here rather than referenced
    // because the adapter needs UnityEngine and cannot be linked into this project.
    private const float SkyBlend = 0.45f;

    // The latitude every pre-issue-#82 assertion below is expressed at, so the numbers those tests
    // pin are still exactly the numbers they pinned when the column was one global constant. Using
    // the named pivot rather than a literal 45 means that if the pivot ever moves, these read as a
    // deliberate recalibration rather than silently drifting off their measured anchors.
    private const float PivotLatitude = OzoneTwilightMath.ColumnPivotLatitudeDegrees;

    // A high-latitude tile inside civil polar night — the same latitude Tests/Scenarios/
    // polar_night_blue.json holds, so the offline column assertions and the live A/B describe the
    // same place.
    private const float PolarLatitude = 88f;

    // --- BandStrength: the trapezoid envelope ---

    [TestCase(45f, 0f)] // broad daylight
    [TestCase(0f, 0f)] // geometric horizon: the warm terms still own this
    [TestCase(-0.83f, 0f)] // refraction horizon, still nothing
    [TestCase(-4f, 0f)] // onset, exclusive
    [TestCase(-5.6f, 0.5f)] // midpoint of the fade-in
    [TestCase(-7.2f, 1f)] // the measurement anchor: full strength
    [TestCase(-9f, 1f)] // plateau
    [TestCase(-12f, 1f)] // plateau end, still full
    [TestCase(-15f, 0.5f)] // midpoint of the fade-out
    [TestCase(-18f, 0f)] // astronomical twilight ends
    [TestCase(-30f, 0f)] // deep night: §7 owns the sky
    public void BandStrength_MatchesExpected(float elevation, float expected)
    {
        Assert.That(OzoneTwilightMath.BandStrength(elevation, inVacuum: false), Is.EqualTo(expected).Within(Tolerance));
    }

    /// <summary>
    /// Guards against the fade-out ramp's reversed endpoints leaking above the horizon — the easiest
    /// way to get this trapezoid wrong is a sign error that makes daylight faintly blue.
    /// </summary>
    [Test]
    public void BandStrength_IsZeroEverywhereAboveOnset()
    {
        for (float elevation = OzoneTwilightMath.BlueOnsetDegrees; elevation <= 90f; elevation += 2.5f)
        {
            Assert.That(OzoneTwilightMath.BandStrength(elevation, inVacuum: false), Is.EqualTo(0f).Within(Tolerance),
                $"expected no blue at elevation {elevation}");
        }
    }

    /// <summary>
    /// The plateau is the dwell-time property: a shallow polar sun oscillating inside the band must
    /// hold a steady blue rather than pulsing. If someone later "simplifies" the trapezoid back to a
    /// triangle peaking at one elevation, this is the test that fails.
    /// </summary>
    [Test]
    public void BandStrength_HasAFlatPlateau()
    {
        for (float elevation = OzoneTwilightMath.BluePeakDegrees; elevation >= OzoneTwilightMath.BluePlateauEndDegrees; elevation -= 0.1f)
        {
            Assert.That(OzoneTwilightMath.BandStrength(elevation, inVacuum: false), Is.EqualTo(1f).Within(Tolerance),
                $"plateau broken at elevation {elevation}");
        }
    }

    /// <summary>
    /// Catches a sign or endpoint error at any of the four anchors: a discontinuity here would show
    /// in game as the sky snapping colour partway through dusk.
    /// </summary>
    [Test]
    public void BandStrength_IsContinuous()
    {
        float previous = OzoneTwilightMath.BandStrength(-20f, inVacuum: false);
        for (float elevation = -20f; elevation <= 2f; elevation += 0.05f)
        {
            float current = OzoneTwilightMath.BandStrength(elevation, inVacuum: false);
            Assert.That(System.MathF.Abs(current - previous), Is.LessThan(0.02f),
                $"jump at elevation {elevation}");
            previous = current;
        }
    }

    /// <summary>
    /// §18's vacuum gate. Asserted alongside the sea-level value in the same sweep so a regression
    /// shows up as a diverging pair rather than as two independently-passing tests.
    /// </summary>
    [TestCase(0f)]
    [TestCase(-4f)]
    [TestCase(-7.2f)]
    [TestCase(-12f)]
    [TestCase(-18f)]
    [TestCase(-40f)]
    public void BandStrength_IsZeroInVacuum_AtEveryElevation(float elevation)
    {
        Assert.That(OzoneTwilightMath.BandStrength(elevation, inVacuum: true), Is.EqualTo(0f).Within(Tolerance));
        Assert.That(OzoneTwilightMath.BandStrength(elevation, inVacuum: false), Is.GreaterThanOrEqualTo(0f));
    }

    // --- SlantAirmass and ChappuisTransmission: the hue ---

    [TestCase(10f, 0f)] // sun up: no long slant path
    [TestCase(0f, 0f)] // horizon
    [TestCase(-6f, 22.5f)] // halfway to the plateau
    [TestCase(-12f, OzoneTwilightMath.MaxSlantAirmass)] // plateau
    [TestCase(-30f, OzoneTwilightMath.MaxSlantAirmass)] // clamped below
    public void SlantAirmass_MatchesExpected(float elevation, float expected)
    {
        Assert.That(OzoneTwilightMath.SlantAirmass(elevation), Is.EqualTo(expected).Within(0.01f));
    }

    /// <summary>
    /// The whole hue rests on the cross-section ordering R > G >> B. This is the exact inverse of
    /// §8's BlackbodyToRgb_StaysWarm_AcrossOurWholeRange (which asserts R >= G >= B), and that
    /// inversion is precisely why the two subsystems cannot share a file.
    ///
    /// Swept across latitude as well as elevation since issue #82: the column now varies, and the
    /// hue ordering is the one property that must survive at every column depth — a tropical map
    /// gets a shallower notch, never a differently-signed one.
    /// </summary>
    [TestCase(-4f, 0f)]
    [TestCase(-7.2f, 0f)]
    [TestCase(-4f, PivotLatitude)]
    [TestCase(-7.2f, PivotLatitude)]
    [TestCase(-12f, PivotLatitude)]
    [TestCase(-18f, PivotLatitude)]
    [TestCase(-7.2f, PolarLatitude)]
    [TestCase(-12f, PolarLatitude)]
    public void ChappuisTransmission_IsBlue(float elevation, float latitude)
    {
        SkyColorTemperature.Rgb rgb = OzoneTwilightMath.ChappuisTransmission(elevation, latitude);
        Assert.That(rgb.B, Is.GreaterThan(rgb.G), "blue must survive the notch better than green");
        Assert.That(rgb.G, Is.GreaterThan(rgb.R), "green must survive better than red (603 nm is the band peak)");
    }

    /// <summary>
    /// Pins the multiply-channel correction: the transmission must carry HUE ONLY. If the maximum
    /// channel ever drifts below 1 the adapter would be smuggling a brightness change into a patch
    /// documented as colour-only, and §7a would multiply it away anyway.
    ///
    /// Latitude matters here too (issue #82): a deeper polar column attenuates every channel harder,
    /// so if the normalisation were ever dropped the polar case is where the smuggled brightness
    /// loss would be largest.
    /// </summary>
    [TestCase(-4f, 0f)]
    [TestCase(-12f, 0f)]
    [TestCase(-4f, PivotLatitude)]
    [TestCase(-7.2f, PivotLatitude)]
    [TestCase(-12f, PivotLatitude)]
    [TestCase(-18f, PivotLatitude)]
    [TestCase(-12f, PolarLatitude)]
    [TestCase(-18f, PolarLatitude)]
    public void ChappuisTransmission_IsLuminanceNormalised(float elevation, float latitude)
    {
        SkyColorTemperature.Rgb rgb = OzoneTwilightMath.ChappuisTransmission(elevation, latitude);
        float brightest = System.MathF.Max(rgb.R, System.MathF.Max(rgb.G, rgb.B));
        Assert.That(brightest, Is.EqualTo(1f).Within(Tolerance));
    }

    /// <summary>
    /// The notch deepens as the slant path lengthens — the progression a fixed blackbody colour
    /// cannot express at all, and the reason the hue is a function of elevation rather than a
    /// constant. Strict below the horizon down to the plateau; flat below that, by design.
    /// </summary>
    [Test]
    public void ChappuisTransmission_DeepensMonotonically_DownToThePlateau()
    {
        float previousRedOverBlue = 2f;
        for (float elevation = 0f; elevation >= OzoneTwilightMath.BluePlateauEndDegrees; elevation -= 0.25f)
        {
            SkyColorTemperature.Rgb rgb = OzoneTwilightMath.ChappuisTransmission(elevation, PivotLatitude);
            float redOverBlue = rgb.R / rgb.B;
            Assert.That(redOverBlue, Is.LessThan(previousRedOverBlue),
                $"hue stopped deepening at elevation {elevation}");
            previousRedOverBlue = redOverBlue;
        }
    }

    /// <summary>
    /// THE test this subsystem exists to pass, and the one that would have caught the rejected
    /// blackbody design before a line of adapter code was written.
    ///
    /// MatBases.LightOverlay.color multiplies the scene, so what the player sees is the per-channel
    /// RATIO change against an unmodified night. §9 measured its own first two attempts at 0.001 and
    /// recorded them as dead ends — "the effect was, measurably, not there". A full-strength
    /// Planckian 20,000 K tint manages only ~5% red attenuation here for the same reason: vanilla's
    /// night sky is already almost exactly that blue. The absorption model must clear that bar by a
    /// wide margin at the band's peak, at the shipped blend, or it is not worth shipping.
    /// </summary>
    [Test]
    public void GroundRedAttenuation_ExceedsVisibleThreshold()
    {
        float attenuation = 1f - GroundChannelFactor(OzoneTwilightMath.BluePeakDegrees, PivotLatitude, VanillaNightR, 0);
        Assert.That(attenuation, Is.GreaterThan(0.20f),
            "the blue must be visible through the multiply overlay, not another §9-style dead end");
    }

    /// <summary>
    /// The blue must also deepen on screen, not just in the model — the payoff of the airmass ramp.
    /// </summary>
    [Test]
    public void GroundRedAttenuation_GrowsAsTheSunSinks()
    {
        float atOnset = 1f - GroundChannelFactor(-5f, PivotLatitude, VanillaNightR, 0);
        float atPeak = 1f - GroundChannelFactor(OzoneTwilightMath.BluePeakDegrees, PivotLatitude, VanillaNightR, 0);
        float atPlateau = 1f - GroundChannelFactor(OzoneTwilightMath.BluePlateauEndDegrees, PivotLatitude, VanillaNightR, 0);

        Assert.That(atPeak, Is.GreaterThan(atOnset));
        Assert.That(atPlateau, Is.GreaterThan(atPeak));
    }

    /// <summary>
    /// Pins DESIGN.md §19's transmission table, every cell of every row, so the shipped document and
    /// the shipped code cannot drift apart again.
    ///
    /// Issue #89 is why this exists. The onset row's attenuation figure read 18.3% from §19's first
    /// commit onward while the other two rows were correct — a single stale cell, invisible because
    /// nothing reproduced the table mechanically, and it survived two passes over the section before
    /// anyone recomputed it. 18.3% is this same chain evaluated at airmass ≈21 (−5.6°, the midpoint
    /// of the fade-in) rather than at the row's own airmass 15; the correct figure is 10.8%.
    ///
    /// Every number here is derived from Beer–Lambert with the shipped constants, not recorded from
    /// a run: T = exp(−σ·N·m) normalised to blue, then run through GroundChannelFactor, which is
    /// itself a transcription of Patch_PolarNightBlue.BlendTowardHue. Expectations are quoted at the
    /// document's own precision so a reader can check the table against this test by eye.
    ///
    /// The onset row's 10.8% is deliberately a HYPOTHETICAL, and that is the one subtlety worth
    /// holding onto: BandStrength(−4°) is exactly 0, so nothing is on screen at that instant. The
    /// column reports the hue that is waiting at the top of the ramp, which is what makes the three
    /// rows comparable — all three are quoted at full band strength, and only the airmass moves.
    /// </summary>
    [TestCase(-4f, 15f, 0.537f, 0.639f, 0.108f)]
    [TestCase(-7.2f, 27f, 0.327f, 0.447f, 0.242f)]
    [TestCase(-12f, 45f, 0.155f, 0.261f, 0.351f)]
    public void TransmissionTable_ReproducesTheDocumentedFigures(
        float elevation, float expectedAirmass, float expectedRed, float expectedGreen, float expectedAttenuation)
    {
        // The table's first column is an airmass, its row label an elevation. Pin the mapping too,
        // or a change to MaxSlantAirmass would silently re-label every row.
        Assert.That(OzoneTwilightMath.SlantAirmass(elevation), Is.EqualTo(expectedAirmass).Within(0.01f));

        SkyColorTemperature.Rgb rgb = OzoneTwilightMath.ChappuisTransmission(elevation, PivotLatitude);
        Assert.That(rgb.R, Is.EqualTo(expectedRed).Within(0.001f), "transmission R drifted from the documented table");
        Assert.That(rgb.G, Is.EqualTo(expectedGreen).Within(0.001f), "transmission G drifted from the documented table");
        Assert.That(rgb.B, Is.EqualTo(1f).Within(Tolerance), "blue is the normalisation channel and must stay 1");

        float attenuation = 1f - GroundChannelFactor(elevation, PivotLatitude, VanillaNightR, 0);
        Assert.That(attenuation, Is.EqualTo(expectedAttenuation).Within(0.0005f),
            "the documented ground red attenuation no longer matches the model");
    }

    /// <summary>
    /// The trap issue #89 fell into, pinned as an inequality so nobody re-derives the attenuation
    /// column the wrong way a third time. `1 − blend·(1 − R/B)` looks like the obvious reading of
    /// "red attenuated on ground @ blend 0.45" and is wrong at every row: it omits the adapter's
    /// rescale of the normalised hue to the source colour's brightest channel, so it overstates the
    /// onset row by nearly a factor of two (20.8% against the true 10.8%).
    /// </summary>
    [TestCase(-4f)]
    [TestCase(-7.2f)]
    [TestCase(-12f)]
    public void NaiveBlendModel_DoesNotReproduceTheTable(float elevation)
    {
        SkyColorTemperature.Rgb rgb = OzoneTwilightMath.ChappuisTransmission(elevation, PivotLatitude);
        float naive = SkyBlend * (1f - rgb.R / rgb.B);
        float actual = 1f - GroundChannelFactor(elevation, PivotLatitude, VanillaNightR, 0);

        Assert.That(naive, Is.Not.EqualTo(actual).Within(0.005f),
            "if these have converged the rescale has been dropped from the adapter or from this file");
    }

    /// <summary>
    /// Reproduces what the adapter does to one channel of vanilla's night sky, then expresses it as
    /// the factor the multiply overlay applies to the ground. channel: 0 = R, 1 = G, 2 = B.
    /// </summary>
    private static float GroundChannelFactor(float elevation, float latitude, float vanillaChannel, int channel)
    {
        SkyColorTemperature.Rgb hue = OzoneTwilightMath.ChappuisTransmission(elevation, latitude);
        float hueChannel = channel == 0 ? hue.R : (channel == 1 ? hue.G : hue.B);

        // The adapter scales the pure hue against the colour it blends from, so the tint stays
        // luminance-neutral — see Patch_PolarNightBlue.
        float target = hueChannel * VanillaNightB;
        float blended = vanillaChannel + (target - vanillaChannel) * SkyBlend;
        return blended / vanillaChannel;
    }

    // --- OzoneColumnForLatitude: the absorber abundance (issue #82) ---
    //
    // Read the two latitude paragraphs in OzoneTwilightMath's header before touching anything here.
    // These tests are about N_column, the number of molecules along the path. They are NOT the
    // latitude term §19 rejected, which was about the path's GEOMETRY and is still rejected — none
    // of the BandStrength or SlantAirmass tests above gained a latitude argument, and that is the
    // whole point.

    /// <summary>
    /// THE regression pin for issue #82: at the pivot the curve must return the single constant the
    /// subsystem was calibrated against, so the entire pre-#82 measured record — the transmission
    /// table in DESIGN.md §19, the 24.2% red attenuation, the ~20,000 K CCT cross-check — still
    /// describes a mid-latitude map exactly. A latitude curve that moved the midpoint would have
    /// invalidated all of it silently.
    ///
    /// Asserted as a RELATIVE tolerance rather than Tolerance's absolute 0.0005 because these are
    /// 1e18-scale floats where an absolute epsilon is meaningless: float carries ~7 significant
    /// digits, and the curve reaches the pivot through a sine, so "exactly" here means "to the
    /// precision the type has", not "bit-identical".
    /// </summary>
    [Test]
    public void OzoneColumnForLatitude_ReproducesTheGlobalMeanAtThePivot()
    {
        float atPivot = OzoneTwilightMath.OzoneColumnForLatitude(PivotLatitude);
        Assert.That(atPivot, Is.EqualTo(OzoneTwilightMath.OzoneColumn).Within(0.001f).Percent);
    }

    /// <summary>
    /// The climatology the curve is fitted to, in Dobson Units so the numbers are checkable against
    /// published zonal means rather than against our own molecules/cm² scaling: ~260 DU over the
    /// tropics, 300 DU at mid-latitudes, 380-420 DU at high latitudes. Tolerances are wide on
    /// purpose — this pins the SHAPE, not a fit to three decimal places, and a reader retuning the
    /// shape function should be able to do so without repainting this table.
    /// </summary>
    [TestCase(0f, 260f)] // Brewer-Dobson's rising branch: young, ozone-poor tropical air
    [TestCase(15f, 261f)] // still flat — the tropical column barely moves out to ~20 degrees
    [TestCase(30f, 270f)] // the subtropical gradient starting to bite
    [TestCase(PivotLatitude, 300f)] // the global mean, and today's constant
    [TestCase(60f, 350f)]
    [TestCase(70f, 385f)] // where polar night actually happens
    [TestCase(80f, 410f)]
    [TestCase(90f, 420f)] // forced by the other anchors, not chosen; lands in the Arctic-spring range
    public void OzoneColumnForLatitude_MatchesTheClimatology(float latitude, float expectedDobsonUnits)
    {
        float dobsonUnits = OzoneTwilightMath.OzoneColumnForLatitude(latitude) / OzoneTwilightMath.OzoneColumn * 300f;
        Assert.That(dobsonUnits, Is.EqualTo(expectedDobsonUnits).Within(2f));
    }

    /// <summary>
    /// Poleward transport only ever piles ozone UP, so the column must never thin as |latitude|
    /// grows. The failure this guards is a shape function that folds back over — sin⁴ does exactly
    /// that past 90 degrees, which is why PolewardColumnShape clamps before the sine.
    /// </summary>
    [Test]
    public void OzoneColumnForLatitude_IsMonotonicNonDecreasing_InAbsoluteLatitude()
    {
        float previous = OzoneTwilightMath.OzoneColumnForLatitude(0f);
        for (float latitude = 0f; latitude <= 120f; latitude += 0.5f)
        {
            float current = OzoneTwilightMath.OzoneColumnForLatitude(latitude);
            Assert.That(current, Is.GreaterThanOrEqualTo(previous), $"column thinned going poleward at latitude {latitude}");
            previous = current;
        }
    }

    /// <summary>
    /// Hemispheric symmetry, which is the constraint that ruled out a straight |latitude| ramp: the
    /// column is an even function of latitude, so a southern tile must read exactly as its northern
    /// mirror. (The real ANNUAL cycle is antisymmetric between hemispheres, but §82 ships the
    /// latitude half only — see DESIGN.md §19b on why the seasonal term was deferred.)
    /// </summary>
    [TestCase(0f)]
    [TestCase(23.44f)]
    [TestCase(PivotLatitude)]
    [TestCase(PolarLatitude)]
    public void OzoneColumnForLatitude_IsSymmetricAcrossTheEquator(float latitude)
    {
        float north = OzoneTwilightMath.OzoneColumnForLatitude(latitude);
        float south = OzoneTwilightMath.OzoneColumnForLatitude(-latitude);
        Assert.That(south, Is.EqualTo(north).Within(0.001f).Percent);
    }

    /// <summary>
    /// The notch must deepen with latitude at a FIXED elevation — same slant path, more absorber
    /// along it. This is the property that distinguishes issue #82's change from the geometry term
    /// §19 rejected: the airmass argument is held constant throughout and only the column moves.
    /// </summary>
    [TestCase(-5f)]
    [TestCase(-7.2f)]
    [TestCase(-12f)]
    public void ChappuisTransmission_DeepensWithLatitude_AtFixedElevation(float elevation)
    {
        float previousRedOverBlue = float.MaxValue;
        for (float latitude = 0f; latitude <= 90f; latitude += 5f)
        {
            SkyColorTemperature.Rgb rgb = OzoneTwilightMath.ChappuisTransmission(elevation, latitude);
            float redOverBlue = rgb.R / rgb.B;
            Assert.That(redOverBlue, Is.LessThanOrEqualTo(previousRedOverBlue),
                $"the notch got shallower going poleward at latitude {latitude}");
            previousRedOverBlue = redOverBlue;
        }

        // Non-vacuity: the sweep above is satisfied by a flat curve, so pin that the two ends
        // genuinely differ. Blue is 1 after normalisation, so R/B is just R — spelled out as the
        // ratio anyway to stay in the same units the sweep uses.
        SkyColorTemperature.Rgb equator = OzoneTwilightMath.ChappuisTransmission(elevation, 0f);
        Assert.That(previousRedOverBlue, Is.LessThan(equator.R / equator.B * 0.95f),
            "the pole must be measurably deeper than the equator, not merely non-worse");
    }

    /// <summary>
    /// Issue #82's payoff, and the guard the ticket asked for: the existing 20% visibility threshold
    /// must still be cleared at high latitude, and cleared by MORE than the old global-mean column
    /// managed. That "by more" half is the part that matters — a change that only preserved the
    /// threshold would have delivered nothing where the complaint (#78) originates.
    /// </summary>
    [Test]
    public void GroundRedAttenuation_AtPolarLatitude_ClearsTheThresholdByMoreThanThePivot()
    {
        float atPivot = 1f - GroundChannelFactor(OzoneTwilightMath.BluePeakDegrees, PivotLatitude, VanillaNightR, 0);
        float atPole = 1f - GroundChannelFactor(OzoneTwilightMath.BluePeakDegrees, PolarLatitude, VanillaNightR, 0);

        Assert.That(atPole, Is.GreaterThan(0.20f), "polar night must still clear the visibility threshold");
        Assert.That(atPole, Is.GreaterThan(atPivot), "the poleward column must deepen the blue, not merely preserve it");
    }

    /// <summary>
    /// §19's standing requirement that the equator is not artificially zeroed, restated for the
    /// column: the tropical map gets a THINNER absorber, never an absent one. A curve that ran the
    /// tropics down toward zero would delete the brief equatorial blue hour by the back door — the
    /// exact outcome the elevation-only geometry was chosen to avoid — while looking like a
    /// physical refinement.
    ///
    /// Held against the 20% threshold rather than merely against zero because "non-zero" is a
    /// uselessly weak bar next to §9's measured 0.001 dead ends: the equatorial blue must still be
    /// something a player could see during its ~34-minute window.
    /// </summary>
    [Test]
    public void EquatorialBlueHour_StaysVisible()
    {
        float atEquator = 1f - GroundChannelFactor(OzoneTwilightMath.BluePeakDegrees, 0f, VanillaNightR, 0);
        float atPivot = 1f - GroundChannelFactor(OzoneTwilightMath.BluePeakDegrees, PivotLatitude, VanillaNightR, 0);

        Assert.That(atEquator, Is.GreaterThan(0.20f), "the equatorial blue hour must stay visible, not just non-zero");
        Assert.That(atEquator, Is.LessThan(atPivot), "the tropics must be the shallow end of the gradient");
        Assert.That(OzoneTwilightMath.BandStrength(OzoneTwilightMath.BluePeakDegrees, inVacuum: false),
            Is.EqualTo(1f).Within(Tolerance),
            "the band envelope itself must still be latitude-blind — the geometry argument is unchanged");
    }

    /// <summary>
    /// The cross-subsystem invariant, expressed purely within §19's own inputs: the ozone layer sits
    /// at 20-30 km, above the bulk atmosphere and far above the boundary layer, so a mountain map
    /// and a polluted map cross the same ozone column as a sea-level clean one. §8 is where site
    /// altitude and aerosol loading belong (issues #81 and #83); §19 must never grow a second copy
    /// of them.
    ///
    /// Enforced as a SIGNATURE guard rather than a value comparison, because the invariant is about
    /// what the function is allowed to depend on and there is deliberately no altitude input here to
    /// vary. If someone later threads one in, this fails and sends them to the paragraph above
    /// rather than letting the two subsystems' location terms quietly bleed together.
    /// </summary>
    [Test]
    public void OzoneTwilightMath_TakesNoSiteAltitudeOrAerosolInput()
    {
        string[] banned = { "altitude", "aerosol", "pollution", "haze", "smog", "turbidity", "boundarylayer" };

        foreach (System.Reflection.MemberInfo member in typeof(OzoneTwilightMath).GetMembers())
        {
            AssertNameIsClean(member.Name, banned, "member");
        }

        foreach (System.Reflection.MethodInfo method in typeof(OzoneTwilightMath).GetMethods())
        {
            foreach (System.Reflection.ParameterInfo parameter in method.GetParameters())
            {
                AssertNameIsClean(parameter.Name!, banned, $"parameter of {method.Name}");
            }
        }
    }

    private static void AssertNameIsClean(string name, string[] banned, string what)
    {
        string lowered = name.ToLowerInvariant();
        foreach (string term in banned)
        {
            Assert.That(lowered.Contains(term), Is.False,
                $"§19 grew a '{term}' {what} ('{name}') — site altitude and air quality belong to §8, not to a 20-30 km layer");
        }
    }

    /// <summary>
    /// Pins the exact shape of the impure boundary the adapter has to satisfy: the hue is a function
    /// of sun elevation and latitude, in that order, and of nothing else. Guards the ordering as much
    /// as the arity — both arguments are floats in degrees, so a transposed call site would compile
    /// silently and produce a plausible-looking wrong colour.
    /// </summary>
    [Test]
    public void ChappuisTransmission_TakesElevationThenLatitude()
    {
        System.Reflection.ParameterInfo[] parameters =
            typeof(OzoneTwilightMath).GetMethod(nameof(OzoneTwilightMath.ChappuisTransmission))!.GetParameters();

        Assert.That(parameters.Length, Is.EqualTo(2));
        Assert.That(parameters[0].Name, Is.EqualTo("elevationDegrees"));
        Assert.That(parameters[1].Name, Is.EqualTo("latitudeDegrees"));
    }

    // --- CanReachBandToday: the gate ---

    [TestCase(0f, 0f, true)] // equator, equinox
    [TestCase(0f, 23.44f, true)] // equator, solstice
    [TestCase(45f, -23.44f, true)] // mid-latitude winter
    [TestCase(78f, -23.44f, true)] // CIVIL POLAR NIGHT — the money case, must never be skipped
    [TestCase(70f, 23.44f, false)] // midnight sun: never dips to -4
    [TestCase(90f, 23.44f, false)] // pole in summer
    [TestCase(86f, -23.44f, false)] // true polar night: never climbs to -18
    public void CanReachBandToday_MatchesExpected(float latitude, float declination, bool expected)
    {
        Assert.That(OzoneTwilightMath.CanReachBandToday(latitude, declination), Is.EqualTo(expected));
    }

    /// <summary>
    /// The gate is an optimisation, so it must be correctness-preserving, not merely plausible: on
    /// every (latitude, declination) it rejects, the band strength must be zero at every hour of
    /// that day. Cross-checked against Formulas.SolarElevationDegrees — the same elevation function
    /// the live adapter feeds — rather than against the gate's own closed-form extremes, so the two
    /// derivations have to agree independently.
    /// </summary>
    [Test]
    public void CanReachBandToday_NeverSkipsAReachableDay()
    {
        int skippedPairs = 0;

        for (float latitude = -90f; latitude <= 90f; latitude += 1f)
        {
            for (float declination = -23.44f; declination <= 23.44f; declination += 1f)
            {
                bool skipped = !OzoneTwilightMath.CanReachBandToday(latitude, declination);
                if (skipped)
                {
                    skippedPairs++;
                    AssertBandNeverOpensDuringTheDay(latitude, declination);
                }
            }
        }

        // Without this the property above passes vacuously if the gate degenerates to "always run" —
        // which is exactly what a sign error in the closed-form extremes would produce, and it would
        // look like a green test while the optimisation silently did nothing.
        Assert.That(skippedPairs, Is.GreaterThan(0), "the gate never skipped anything, so nothing was proven");
    }

    private static void AssertBandNeverOpensDuringTheDay(float latitude, float declination)
    {
        for (float dayPercent = 0f; dayPercent <= 1f; dayPercent += 0.005f)
        {
            float elevation = Formulas.SolarElevationDegrees(latitude, declination, dayPercent);
            Assert.That(OzoneTwilightMath.BandStrength(elevation, inVacuum: false), Is.EqualTo(0f).Within(Tolerance),
                $"gate skipped lat {latitude} decl {declination}, but the band opens at day-percent {dayPercent} (elevation {elevation})");
        }
    }

    // --- OverlayFloor: the visual-only brightness floor ---

    [TestCase(0f, 1f, 1f, OzoneTwilightMath.DefaultOverlayFloor)] // full band, full strength
    [TestCase(0f, 0.5f, 1f, 0.15f)] // half band: floor fades with the blue
    [TestCase(0f, 1f, 0f, 0f)] // strength 0 is a true no-op — the A/B baseline
    [TestCase(0f, 0f, 1f, 0f)] // outside the band
    [TestCase(0.5f, 1f, 1f, 0.5f)] // Cinematic's 0.50 already higher: inert by construction
    [TestCase(0.2f, 1f, 1f, OzoneTwilightMath.DefaultOverlayFloor)] // raises a low floor
    public void OverlayFloor_MatchesExpected(float baseMin, float band, float strength, float expected)
    {
        Assert.That(OzoneTwilightMath.OverlayFloor(baseMin, band, strength), Is.EqualTo(expected).Within(Tolerance));
    }

    /// <summary>
    /// The safety property the whole floor design rests on: it may brighten a night, never darken
    /// one the player's own setting made brighter.
    /// </summary>
    [Test]
    public void OverlayFloor_NeverLowersTheCallerFloor()
    {
        for (float baseMin = 0f; baseMin <= 1f; baseMin += 0.05f)
        {
            for (float band = 0f; band <= 1f; band += 0.1f)
            {
                for (float strength = 0f; strength <= 1f; strength += 0.25f)
                {
                    Assert.That(OzoneTwilightMath.OverlayFloor(baseMin, band, strength), Is.GreaterThanOrEqualTo(baseMin));
                }
            }
        }
    }

    /// <summary>
    /// The only test that proves the floor changes what the screen actually does. Exercises the real
    /// cross-subsystem seam into §7a: at a deep-twilight glow the raw overlay keeps a functionally
    /// black fraction of vanilla brightness, and our floor is what lifts it to something the blue is
    /// visible against.
    /// </summary>
    [Test]
    public void OverlayFloor_ComposesWithOverlayBrightnessFactor()
    {
        const float DeepTwilightGlow = 0.014f;

        float unfloored = NightRadianceMath.OverlayBrightnessFactor(DeepTwilightGlow, minBrightness: 0f);
        Assert.That(unfloored, Is.LessThan(0.15f), "precondition: an unfloored deep twilight is near-black");

        float floor = OzoneTwilightMath.OverlayFloor(0f, bandStrength: 1f, tintStrength: 1f);
        float floored = NightRadianceMath.OverlayBrightnessFactor(DeepTwilightGlow, floor);

        Assert.That(floored, Is.EqualTo(OzoneTwilightMath.DefaultOverlayFloor).Within(Tolerance));
        Assert.That(floored, Is.GreaterThan(unfloored));
    }

    // --- Composition with the warm subsystems ---

    /// <summary>
    /// Pins the "the bands are disjoint enough that ordering does not matter" claim that justifies
    /// shipping without a HarmonyPriority. §8's warm tint and our blue overlap only in -6..-4, and
    /// only ever weakly. If someone retunes either curve so the product climbs, the no-priority
    /// decision has quietly become load-bearing and this test says so.
    /// </summary>
    [Test]
    public void WarmAndBlue_AreNeverBothAtFullStrength()
    {
        for (float elevation = -20f; elevation <= 10f; elevation += 0.1f)
        {
            // Sea level (pressureFraction 1) is deliberately the worst case for this claim: §20
            // only ever scales §8's warm tint DOWN with site altitude, so if the product stays
            // small here it stays small on every tile.
            float warm = SkyColorTemperature.TintStrength(elevation, pressureFraction: 1f, inVacuum: false);
            float blue = OzoneTwilightMath.BandStrength(elevation, inVacuum: false);
            Assert.That(warm * blue, Is.LessThan(0.10f), $"warm and blue both strong at elevation {elevation}");
        }
    }

    /// <summary>
    /// §2's civil-twilight warmth dies at -6 and our blue starts at -4, so the two share exactly the
    /// documented 2-degree handover window and nothing else.
    /// </summary>
    [Test]
    public void WarmPersistenceAndBlue_OverlapOnlyInTheDocumentedWindow()
    {
        for (float elevation = -30f; elevation <= 5f; elevation += 0.1f)
        {
            float product = Formulas.CivilTwilightPersistence(elevation) * OzoneTwilightMath.BandStrength(elevation, inVacuum: false);
            bool insideWindow = elevation <= OzoneTwilightMath.BlueOnsetDegrees && elevation >= Formulas.CivilTwilightEndDegrees;
            if (!insideWindow)
                Assert.That(product, Is.EqualTo(0f).Within(Tolerance), $"unexpected overlap at elevation {elevation}");
        }
    }
}
