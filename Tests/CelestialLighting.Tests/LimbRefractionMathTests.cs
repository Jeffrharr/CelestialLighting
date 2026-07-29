using System;

namespace CelestialLighting.Tests;

/// <summary>
/// Offline coverage for §18d, the limb-refraction flash (DESIGN.md §18d, pure core in
/// Source/LimbRefractionMath.cs): the deep-red spike an orbital platform gets at sunset in place of
/// the ground twilight §18a removed.
///
/// Two jobs here, and they are different jobs.
///
/// The first is to pin the DERIVATION. §18d is the one subsystem in the mod that had to pick its own
/// physical constants, because RimWorld exposes no planet radius and no atmospheric depth. The whole
/// defence of that is that the five anchors are named once and everything else follows from them, so
/// the geometry cases below re-derive the dip angle, the band width and the ramp endpoints from the
/// anchors independently of the implementation. If someone "tunes" a derived constant to make a
/// screenshot look better, these fail.
///
/// The second is to pin the CONTRAST with sea level, which is what the subsystem is actually for.
/// Per Vacuum.cs's convention every case pins both halves of the gate together: asserting only the
/// vacuum value passes just as happily when the sea-level path has itself regressed, which would hide
/// a broken subsystem behind a green vacuum test.
///
/// Live A/B validation on an actual orbital map is blocked on Jeffrharr/RimWorldTestHarness#17
/// (scenarios cannot currently reach the Orbit planet layer), so this fixture plus the
/// limb_* probes are the whole verification story for §18d until that lands.
/// </summary>
[TestFixture]
public class LimbRefractionMathTests
{
    private const float Tolerance = 0.001f;

    // The anchors, restated as literals rather than read off LimbRefractionMath. That is the point:
    // every geometric expectation below is computed here from these five numbers, so the tests are an
    // independent derivation rather than a transcription of whatever the source currently returns.
    private const double AnchorPlanetRadiusKm = 6371.0;
    private const double AnchorOrbitAltitudeKm = 200.0;
    private const double AnchorShellDepthKm = 50.0;
    private const double AnchorScaleHeightKm = 8.0;
    private const double AnchorSolarDiameterDegrees = 0.53;

    private const double OrbitRadiusKm = AnchorPlanetRadiusKm + AnchorOrbitAltitudeKm;

    private static double Degrees(double radians) => radians * 180.0 / Math.PI;

    // Depression of a tangent line to a sphere of the given radius, seen from OrbitRadiusKm.
    private static double TangentDepressionDegrees(double sphereRadiusKm) =>
        Degrees(Math.Acos(sphereRadiusKm / OrbitRadiusKm));

    // --- The anchors themselves ---
    //
    // Not a tautology: these are the five numbers DESIGN.md §18d commits to in prose, and a silent
    // edit to any of them moves every derived quantity below. Pinning them here means such an edit
    // fails as "you changed the anchor" rather than as six unrelated geometry failures.

    [Test]
    public void Anchors_AreTheFiveValuesDesignDocumentsAsEarthLike()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LimbRefractionMath.PlanetRadiusKm, Is.EqualTo(6371f),
                "planet radius anchor moved");
            Assert.That(LimbRefractionMath.OrbitAltitudeKm, Is.EqualTo(200f),
                "orbit altitude anchor moved — it comes from PlanetLayerDef.elevationString's "
                + "\"200km\", which is a display string, so it is an anchor and not a lookup");
            Assert.That(LimbRefractionMath.RefractingShellDepthKm, Is.EqualTo(50f),
                "refracting shell depth anchor moved");
            Assert.That(LimbRefractionMath.ScaleHeightKm, Is.EqualTo(8f),
                "scale height anchor moved — this sets the ramp's shape, not just its width");
            Assert.That(LimbRefractionMath.SolarAngularDiameterDegrees, Is.EqualTo(0.53f),
                "solar angular diameter anchor moved");
        });
    }

    [Test]
    public void ShellDepthAnchor_IsConsistentWithTheScaleHeightAnchor()
    {
        // The two atmospheric anchors are stated independently, so this checks they still agree:
        // 50 km is 6.25 scale heights, where density has fallen to about 0.2% of sea level and there
        // is nothing left to scatter. If someone raises one without the other, the "shell depth is
        // where the air runs out" justification quietly stops being true.
        double scaleHeights = AnchorShellDepthKm / AnchorScaleHeightKm;
        Assert.That(scaleHeights, Is.EqualTo(6.25).Within(0.001));

        double densityAtShellTop = Math.Exp(-scaleHeights);
        Assert.That(densityAtShellTop, Is.LessThan(0.005),
            "the shell top is supposed to be where the atmosphere has effectively run out");
    }

    // --- Consequence 1: the horizon is depressed ---

    [Test]
    public void HorizonDip_IsAcosOfRadiusOverOrbitRadius()
    {
        double expected = TangentDepressionDegrees(AnchorPlanetRadiusKm);

        Assert.That(expected, Is.EqualTo(14.1724).Within(0.0005),
            "the anchors no longer produce the ~14.2 degree dip §18d is designed around");
        Assert.That(LimbRefractionMath.HorizonDipDegrees, Is.EqualTo((float)expected).Within(Tolerance));
    }

    [Test]
    public void ShellTopDip_IsTheSameConstructionOnTheShellTopRadius()
    {
        double expected = TangentDepressionDegrees(AnchorPlanetRadiusKm + AnchorShellDepthKm);

        Assert.That(expected, Is.EqualTo(12.2658).Within(0.0005));
        Assert.That(LimbRefractionMath.ShellTopDipDegrees, Is.EqualTo((float)expected).Within(Tolerance));
    }

    [Test]
    public void LimbDistance_IsTheTangentSlantRange()
    {
        double expected = Math.Sqrt(OrbitRadiusKm * OrbitRadiusKm - AnchorPlanetRadiusKm * AnchorPlanetRadiusKm);

        Assert.That(expected, Is.EqualTo(1608.85).Within(0.01));
        Assert.That(LimbRefractionMath.LimbDistanceKm, Is.EqualTo((float)expected).Within(0.05f));
    }

    [Test]
    public void SunlitOvershoot_KeepsThePlatformLitRoughlyAnHourPastTheGround()
    {
        // Consequence 1 as the thing a player would notice. Measured against
        // Formulas.AtmosphericRefractionDegrees, the same sea-level horizon every surface subsystem
        // uses, so this is genuinely "how much longer than the ground below" rather than an
        // arbitrary offset from zero.
        Assert.That(LimbRefractionMath.SunlitOvershootDegrees, Is.EqualTo(13.607f).Within(0.01f));
        Assert.That(LimbRefractionMath.SunlitOvershootMinutes, Is.EqualTo(54.43f).Within(0.05f),
            "at the equatorial 15 deg/h; higher latitudes descend obliquely and get more");
    }

    // --- Consequence 2: the ramp is short ---

    [Test]
    public void ShellArc_IsTheDifferenceOfTheTwoTangentDepressions()
    {
        double expected = TangentDepressionDegrees(AnchorPlanetRadiusKm)
            - TangentDepressionDegrees(AnchorPlanetRadiusKm + AnchorShellDepthKm);

        Assert.That(expected, Is.EqualTo(1.9066).Within(0.0005));
        Assert.That(LimbRefractionMath.ShellArcDegrees, Is.EqualTo((float)expected).Within(Tolerance));
    }

    [Test]
    public void LinearisedShellArc_MatchesExactFormToWithinAnEighthOfADegree()
    {
        // The back-of-envelope form the design was reasoned from — atan(shellDepth / limbDistance),
        // treating the shell as a bar held perpendicular at the tangent point — is the first-order
        // term of the exact difference of tangent lines. It runs about 7% low because sin grows
        // across the band, and the shipped code uses the exact form.
        //
        // Pinned so the estimate stays a documented cross-check. If this gap ever widens materially,
        // the linearisation has stopped being a fair description of the geometry and DESIGN.md §18d's
        // derivation prose needs rewriting rather than quietly diverging from the code.
        double limbDistance = Math.Sqrt(OrbitRadiusKm * OrbitRadiusKm - AnchorPlanetRadiusKm * AnchorPlanetRadiusKm);
        double linearised = Degrees(Math.Atan(AnchorShellDepthKm / limbDistance));

        Assert.That(linearised, Is.EqualTo(1.7801).Within(0.0005));
        Assert.That(LimbRefractionMath.ShellArcDegrees - linearised, Is.EqualTo(0.1265).Within(0.001));
        Assert.That(linearised, Is.LessThan(LimbRefractionMath.ShellArcDegrees),
            "the linearisation should under-estimate, not over-estimate");
    }

    [Test]
    public void BandEndpoints_AreTheShellTopAndSolidLimbSmearedByHalfASolarDisc()
    {
        double solarRadius = AnchorSolarDiameterDegrees / 2.0;
        double expectedTop = -(TangentDepressionDegrees(AnchorPlanetRadiusKm + AnchorShellDepthKm) - solarRadius);
        double expectedBottom = -(TangentDepressionDegrees(AnchorPlanetRadiusKm) + solarRadius);

        Assert.Multiple(() =>
        {
            Assert.That(expectedTop, Is.EqualTo(-12.0008).Within(0.0005));
            Assert.That(expectedBottom, Is.EqualTo(-14.4374).Within(0.0005));
            Assert.That(LimbRefractionMath.BandTopElevationDegrees,
                Is.EqualTo((float)expectedTop).Within(Tolerance));
            Assert.That(LimbRefractionMath.BandBottomElevationDegrees,
                Is.EqualTo((float)expectedBottom).Within(Tolerance));
        });
    }

    [Test]
    public void TheBandSitsAboveTheDip_AndTheStepHappensAtIt()
    {
        // The orientation correction, pinned because the issue's prose reads the other way round
        // ("full sun until -14.2, then a ramp"). The dip IS the solid limb by construction, so the
        // refraction band necessarily sits above it and the light stops there. Getting this backwards
        // would put the whole red phase below the terminator, i.e. after the sun had already gone.
        Assert.That(LimbRefractionMath.BandTopElevationDegrees,
            Is.GreaterThan(-LimbRefractionMath.HorizonDipDegrees),
            "the band must open ABOVE the solid-limb dip");
        Assert.That(LimbRefractionMath.BandBottomElevationDegrees,
            Is.LessThan(-LimbRefractionMath.HorizonDipDegrees),
            "the band must close just BELOW the dip, by exactly the sun's angular radius");
        Assert.That(-LimbRefractionMath.HorizonDipDegrees - LimbRefractionMath.BandBottomElevationDegrees,
            Is.EqualTo(LimbRefractionMath.SolarAngularRadiusDegrees).Within(Tolerance));
    }

    [Test]
    public void BandWidthAndDuration_AreTheShellArcPlusOneWholeSolarDisc()
    {
        double expected = LimbRefractionMath.ShellArcDegrees + AnchorSolarDiameterDegrees;

        Assert.That(LimbRefractionMath.BandWidthDegrees, Is.EqualTo((float)expected).Within(Tolerance));
        Assert.That(LimbRefractionMath.BandWidthDegrees, Is.EqualTo(2.4366f).Within(0.001f));
        Assert.That(LimbRefractionMath.BandDurationMinutes, Is.EqualTo(9.746f).Within(0.01f),
            "at the equatorial 15 deg/h — a lower bound, since higher latitudes take longer");
    }

    [Test]
    public void TheVacuumRampIsStrictlyShorterThanTheSeaLevelOne()
    {
        // The headline comparison, and the reason it is worth building at all. Sea level runs from the
        // refraction horizon to the end of astronomical twilight — the same two anchors §7's night
        // floor fades across (NightRadianceMath.NightFloorStartElevation / NightFloorFullElevation),
        // so this is the mod's own twilight span rather than a textbook number chosen for effect.
        float seaLevelSpan = NightRadianceMath.NightFloorStartElevation
            - NightRadianceMath.NightFloorFullElevation;

        Assert.That(seaLevelSpan, Is.EqualTo(17.17f).Within(Tolerance));
        Assert.That(LimbRefractionMath.BandWidthDegrees, Is.LessThan(seaLevelSpan));
        Assert.That(seaLevelSpan / LimbRefractionMath.BandWidthDegrees, Is.EqualTo(7.05f).Within(0.05f),
            "about one-seventh the angular width of a sea-level twilight");
    }

    // --- The brightness ramp, both halves of the gate ---
    //
    // Sea level is 1 everywhere by design: a surface map's extinction is an entirely different model
    // that §2/§7/§8 own end to end, so this function is a strict no-op there rather than a second
    // opinion. Pinning it alongside the vacuum column is what makes a regression show up as a
    // diverging pair.

    [TestCase(30f, 1f)] // full day
    [TestCase(0f, 1f)] // the GROUND's sunset — the platform has not even started to lose the sun
    [TestCase(-6f, 1f)] // end of ground civil twilight, still full sun up here
    [TestCase(-11f, 1f)] // still above the band
    [TestCase(-12.0008f, 1f)] // band top, to the last float
    [TestCase(-12.5f, 0.971075f)]
    [TestCase(-13f, 0.868764f)]
    [TestCase(-13.5f, 0.506515f)] // half gone, a third of the way down the band
    [TestCase(-13.8f, 0.184225f)]
    [TestCase(-14f, 0.043181f)] // the collapse
    [TestCase(-14.2f, 0.003294f)]
    [TestCase(-14.4374f, 0f)] // band bottom: the disc is behind the solid limb
    [TestCase(-20f, 0f)] // orbital night
    [TestCase(-60f, 0f)]
    public void SunlightFraction_RunsTheBandInVacuum_AndIsInertAtSeaLevel(float elevation, float vacuum)
    {
        Assert.That(LimbRefractionMath.SunlightFraction(elevation, inVacuum: true),
            Is.EqualTo(vacuum).Within(Tolerance),
            "vacuum sunlight fraction moved");
        Assert.That(LimbRefractionMath.SunlightFraction(elevation, inVacuum: false),
            Is.EqualTo(1f).Within(Tolerance),
            "sea level must be a strict no-op — §2/§7/§8 own the surface sky");
    }

    [Test]
    public void SunlightFraction_LosesMostOfItsLightInTheLastThirdOfTheBand()
    {
        // Consequence 3 as a shape assertion rather than a set of points: the exponential means the
        // first two thirds of the band barely move and the last third does nearly all the work. A
        // linear ramp of the same width would fail this, and a linear ramp is exactly what a future
        // "simplification" would reach for.
        float twoThirdsDown = Lerp(
            LimbRefractionMath.BandTopElevationDegrees, LimbRefractionMath.BandBottomElevationDegrees, 2f / 3f);

        float remaining = LimbRefractionMath.SunlightFraction(twoThirdsDown, inVacuum: true);
        Assert.That(remaining, Is.GreaterThan(0.25f),
            "two thirds of the way down the band, a LINEAR ramp would be at 0.33 — the exponential "
            + "should still be above that, not below it");
        Assert.That(remaining, Is.LessThan(0.45f));
    }

    [Test]
    public void SunlightFraction_IsMonotonicThroughTheBand()
    {
        float previous = 1.0001f;
        for (int i = 0; i <= 200; i++)
        {
            float elevation = Lerp(
                LimbRefractionMath.BandTopElevationDegrees,
                LimbRefractionMath.BandBottomElevationDegrees,
                i / 200f);
            float value = LimbRefractionMath.SunlightFraction(elevation, inVacuum: true);

            Assert.That(value, Is.LessThanOrEqualTo(previous),
                $"sunlight fraction rose as the sun kept setting, at elevation {elevation}");
            previous = value;
        }

        Assert.That(previous, Is.EqualTo(0f).Within(1e-6f));
    }

    // --- The colour ramp, both halves of the gate ---

    [TestCase(-11f, 0f)] // above the band: nothing shifted yet
    [TestCase(-12.0008f, 0f)] // band top
    [TestCase(-12.5f, 0.014647f)]
    [TestCase(-13f, 0.069258f)]
    [TestCase(-13.5f, 0.310003f)]
    [TestCase(-13.8f, 0.640785f)]
    [TestCase(-14f, 0.767872f)] // near the peak of the spike
    [TestCase(-14.2f, 0.423406f)] // falling again as the limb swallows the disc
    [TestCase(-14.4374f, 0f)] // band bottom: nothing left to colour the sky with
    [TestCase(-20f, 0f)] // orbital night is §18b's planetshine, NOT a leftover red wash
    public void TintStrength_IsASpikeInVacuum_AndZeroAtSeaLevel(float elevation, float vacuum)
    {
        Assert.That(LimbRefractionMath.TintStrength(elevation, inVacuum: true),
            Is.EqualTo(vacuum).Within(Tolerance));
        Assert.That(LimbRefractionMath.TintStrength(elevation, inVacuum: false),
            Is.EqualTo(0f).Within(Tolerance),
            "ground dusk is §2's — a different colour arriving by a different path");
    }

    [Test]
    public void TintStrength_PeaksInsideTheBandAndReturnsToZeroAtBothEnds()
    {
        // The spike, asserted as a shape. This is what stops the tint outliving the light: if the
        // strength ended the band at 1 instead of 0, the sky's colour field would stay deep red for
        // the whole of orbital night, since colour and glow are written separately.
        float peak = 0f;
        float peakElevation = 0f;
        for (int i = 0; i <= 400; i++)
        {
            float elevation = Lerp(
                LimbRefractionMath.BandTopElevationDegrees,
                LimbRefractionMath.BandBottomElevationDegrees,
                i / 400f);
            float value = LimbRefractionMath.TintStrength(elevation, inVacuum: true);
            if (value > peak)
            {
                peak = value;
                peakElevation = elevation;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(peak, Is.EqualTo(0.789f).Within(0.005f));
            Assert.That(peakElevation, Is.EqualTo(-13.95f).Within(0.05f),
                "the spike should peak deep in the band, just before the limb cuts the disc off");
            Assert.That(LimbRefractionMath.TintStrength(
                LimbRefractionMath.BandTopElevationDegrees, inVacuum: true), Is.EqualTo(0f));
            Assert.That(LimbRefractionMath.TintStrength(
                LimbRefractionMath.BandBottomElevationDegrees, inVacuum: true), Is.EqualTo(0f));
        });
    }

    [TestCase(-12.0008f, 0.996789f)] // band top: essentially white, so it joins §18a's flat sky seamlessly
    [TestCase(-13f, 0.930742f)]
    [TestCase(-13.5f, 0.689997f)]
    [TestCase(-14f, 0.130195f)]
    [TestCase(-14.2f, 0.024211f)] // deep copper — the eclipsed-moon colour
    public void LimbTint_DrivesGreenOutOfTheSpectrumInVacuum_AndIsWhiteAtSeaLevel(
        float elevation, float vacuumGreen)
    {
        LimbRefractionMath.Rgb vacuum = LimbRefractionMath.LimbTint(elevation, inVacuum: true);
        Assert.Multiple(() =>
        {
            Assert.That(vacuum.R, Is.EqualTo(1f).Within(Tolerance),
                "red is the normalisation channel and is pinned at 1 by construction");
            Assert.That(vacuum.G, Is.EqualTo(vacuumGreen).Within(Tolerance));
            Assert.That(vacuum.B, Is.LessThan(vacuum.G),
                "blue must always be further gone than green — that is what Rayleigh means");
        });

        LimbRefractionMath.Rgb seaLevel = LimbRefractionMath.LimbTint(elevation, inVacuum: false);
        Assert.Multiple(() =>
        {
            Assert.That(seaLevel.R, Is.EqualTo(1f).Within(Tolerance));
            Assert.That(seaLevel.G, Is.EqualTo(1f).Within(Tolerance));
            Assert.That(seaLevel.B, Is.EqualTo(1f).Within(Tolerance));
        });
    }

    // --- Redder than sea level, at matched sun elevations ---

    // §2's warm-tint result at a given sun elevation, at full latitude strength — i.e. the reddest
    // ground twilight the mod can produce anywhere. Replicates Patch_TwilightColor's own blend
    // (Lerp toward RGB(1, 0.45, 0.15) at factor * 0.35) applied to a neutral sky, so the comparison
    // is against what a player actually sees on the ground rather than against a bare factor.
    private static float SeaLevelTwilightRedness(float elevationDegrees)
    {
        float glow = SunGlowAtElevation(elevationDegrees);
        float factor = Formulas.TwilightWarmthFactor(glow, elevationDegrees, strength: 1f, inVacuum: false);
        float weight = factor * 0.35f;

        float red = 1f; // the warm target's red is 1, so a neutral sky's red never moves
        float blue = Lerp(1f, 0.15f, weight);
        return red / blue;
    }

    private static float LimbRedness(float elevationDegrees)
    {
        LimbRefractionMath.Rgb tint = LimbRefractionMath.LimbTint(elevationDegrees, inVacuum: true);
        return tint.R / Math.Max(tint.B, 1e-9f);
    }

    // Vanilla's GenCelestial.CurCelestialSunGlow one-liner, replicated so the sea-level comparand
    // walks a physically coherent sun rather than an arbitrary (glow, elevation) pairing — the same
    // helper and the same reasoning as VacuumSuppressionTests.
    private static float SunGlowAtElevation(float elevationDegrees) =>
        Clamp01(MathF.Sin(elevationDegrees * MathF.PI / 180f) / 0.7f);

    [TestCase(-12.0008f)]
    [TestCase(-12.5f)]
    [TestCase(-13f)]
    [TestCase(-13.5f)]
    [TestCase(-14f)]
    [TestCase(-14.2f)]
    [TestCase(-14.4374f)]
    public void TheVacuumRampIsStrictlyRedderThanSeaLevel_AtMatchedSunElevations(float elevation)
    {
        // The literal comparison the issue asks for, and it is not a walkover in the direction you
        // might expect: the sea-level column is 1.0 (perfectly neutral) across this whole range only
        // because ground civil twilight ended at -6 degrees, thirteen degrees higher. The platform is
        // producing its most saturated colour of the day at elevations where the ground below it has
        // been fully dark for the better part of an hour. That inversion IS the subsystem.
        float vacuum = LimbRedness(elevation);
        float seaLevel = SeaLevelTwilightRedness(elevation);

        Assert.That(seaLevel, Is.EqualTo(1f).Within(Tolerance),
            "ground twilight is long over this far below the horizon");
        Assert.That(vacuum, Is.GreaterThan(seaLevel),
            $"vacuum redness {vacuum} was not above sea level's {seaLevel} at {elevation} degrees");
    }

    [Test]
    public void TheVacuumRampGetsFarRedderThanGroundTwilightEverDoes()
    {
        // The other honest reading of "redder": compare each ramp at its own reddest point, so the
        // elevations no longer have to line up. Ground twilight tops out barely off neutral because
        // §2 deliberately nudges rather than replaces; the limb band ends up effectively
        // monochromatic. Five orders of magnitude apart, and both numbers are derived rather than
        // dialled.
        float groundPeak = 0f;
        for (int i = 0; i <= 1800; i++)
        {
            groundPeak = Math.Max(groundPeak, SeaLevelTwilightRedness(-i / 100f));
        }

        float limbPeak = LimbRedness(LimbRefractionMath.BandBottomElevationDegrees);

        Assert.That(groundPeak, Is.EqualTo(1.196f).Within(0.005f),
            "§2's warm nudge at full latitude strength, and no more than a nudge");
        Assert.That(limbPeak, Is.GreaterThan(1000f * groundPeak));
    }

    [Test]
    public void LimbTint_IsAlreadyRedderThanGroundTwilightsPeakByHalfwayDownTheBand()
    {
        // Stronger than the matched-elevation case above, which sea level wins by default at these
        // depths. This one asks whether the band is genuinely more saturated than the best ground
        // dusk on offer, and finds it crosses over less than halfway down.
        float halfway = Lerp(
            LimbRefractionMath.BandTopElevationDegrees, LimbRefractionMath.BandBottomElevationDegrees, 0.5f);

        Assert.That(LimbRedness(halfway), Is.GreaterThan(1.196f));
    }

    // --- Composition: glow, and the injected floor ---

    [TestCase(30f, 0.991965f)] // high sun
    [TestCase(0f, 0.700703f)] // the ground's sunset; the platform is still in broad daylight
    [TestCase(-6f, 0.641702f)]
    [TestCase(-11f, 0.518675f)]
    [TestCase(-12.5f, 0.314910f)] // into the band
    [TestCase(-13.5f, 0.098619f)]
    [TestCase(-14f, 0.005610f)]
    [TestCase(-14.4374f, 0f)] // the step
    [TestCase(-30f, 0f)]
    public void VacuumSkyGlow_ShiftsTheSunClockCurveOntoThePlatformsOwnHorizon(float elevation, float expected)
    {
        // Consequence 1 and consequences 2-3 in one value. The first factor is literally §14's
        // elevation->glow curve evaluated at (elevation + dip): re-referencing the mod's existing
        // brightness statement from the ground's horizon to the platform's is the whole of the
        // "curve swap" this subsystem was described as.
        Assert.That(
            LimbRefractionMath.VacuumSkyGlow(elevation, seaLevelGlow: 0.5f, planetshineFloor: 0f, inVacuum: true),
            Is.EqualTo(expected).Within(Tolerance));

        // Sea level: the incoming value passes through untouched at every elevation, so a surface map
        // never sees this subsystem at all.
        Assert.That(
            LimbRefractionMath.VacuumSkyGlow(elevation, seaLevelGlow: 0.5f, planetshineFloor: 0f, inVacuum: false),
            Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void VacuumSkyGlow_StepsDownToWhateverFloorIsInjected()
    {
        // The #30 seam. The floor is not defined here and must not be: §18b owns the vacuum night
        // budget, and this asserts only that whatever it hands over is honoured exactly, at three
        // different values, with nothing in §18d quietly imposing a minimum of its own.
        foreach (float floor in new[] { 0f, 0.004f, 0.02f, 0.15f })
        {
            Assert.That(
                LimbRefractionMath.VacuumSkyGlow(-30f, seaLevelGlow: 1f, planetshineFloor: floor, inVacuum: true),
                Is.EqualTo(floor).Within(1e-6f),
                "deep orbital night must be exactly the injected floor");
            Assert.That(
                LimbRefractionMath.VacuumSkyGlow(30f, seaLevelGlow: 1f, planetshineFloor: floor, inVacuum: true),
                Is.GreaterThan(floor),
                "full sun must dominate the floor, not be clamped by it");
        }
    }

    [Test]
    public void VacuumSkyGlow_NeverDipsBelowTheInjectedFloorAnywhereInTheBand()
    {
        // The floor is a floor. Swept rather than sampled, because the crossing point where sunlight
        // stops dominating moves with the floor's value and no fixed set of elevations would find it.
        const float floor = 0.02f;
        for (int i = 0; i <= 300; i++)
        {
            float elevation = Lerp(0f, -20f, i / 300f);
            float glow = LimbRefractionMath.VacuumSkyGlow(
                elevation, seaLevelGlow: 1f, planetshineFloor: floor, inVacuum: true);

            Assert.That(glow, Is.GreaterThanOrEqualTo(floor - 1e-6f),
                $"glow fell through the planetshine floor at elevation {elevation}");
        }
    }

    [Test]
    public void VacuumSkyGlow_IsSymmetricAboutSolarNoonByConstruction()
    {
        // Sunrise gets the same treatment as sunset for free: elevation is the only input, and
        // Formulas.SolarElevationDegrees is even in hour angle. So this checks the property that
        // actually matters — nothing in the ramp reads the time of day or a rising/setting flag,
        // which a future "only apply at dusk" optimisation would break.
        foreach (float elevation in new[] { -12.5f, -13f, -13.5f, -14f })
        {
            float morning = LimbRefractionMath.SunlightFraction(elevation, inVacuum: true);
            float evening = LimbRefractionMath.SunlightFraction(elevation, inVacuum: true);
            Assert.That(morning, Is.EqualTo(evening));
        }

        Assert.That(LimbRefractionMath.SunlightFraction(-13f, inVacuum: true),
            Is.EqualTo(0.868764f).Within(Tolerance),
            "elevation alone determines the ramp — there is no dusk-only branch to get wrong");
    }

    // --- Supporting geometry ---

    [TestCase(0f, 200f)] // straight up: the ray never enters the atmosphere at all
    [TestCase(-12.0008f, 56.408f)] // band top, just clear of the shell (the disc's lower edge is in it)
    [TestCase(-13f, 31.586f)]
    [TestCase(-14f, 4.813f)]
    [TestCase(-14.1724f, 0f)] // the dip: the ray grazes the surface exactly
    [TestCase(-20f, 0f)] // clamped — the sun is simply behind the planet
    public void TangentAltitude_FallsFromOrbitAltitudeToZeroAtTheDip(float elevation, float expected)
    {
        // Tolerance is 50 m rather than 10 because this is the one expression in the subsystem with a
        // catastrophic cancellation in it: (R + h) * cos(delta) - R subtracts two numbers near 6400 to
        // get one near 50, and float32 has about 24 bits to spend on that. Measured worst case here is
        // 19 m. It does not matter — the altitude only ever enters as exp(-z / 8 km), so 19 m is a
        // quarter of a percent on an optical depth whose anchors are themselves round numbers — but it
        // is worth pinning as a known bound rather than discovering later as flake.
        Assert.That(LimbRefractionMath.TangentAltitudeKm(elevation), Is.EqualTo(expected).Within(0.05f));
    }

    [TestCase(-13f, 1f)] // whole disc clear of the limb
    [TestCase(-14.1724f, 0.5f)] // centre exactly on the limb: half the disc, by symmetry
    [TestCase(-14.4374f, 0f)] // upper edge just gone
    [TestCase(-16f, 0f)]
    public void SolarDiscVisibleFraction_IsTheCircularSegmentArea(float elevation, float expected)
    {
        Assert.That(LimbRefractionMath.SolarDiscVisibleFraction(elevation), Is.EqualTo(expected).Within(0.002f));
    }

    [Test]
    public void LimbPathAmplification_IsTheGrazingOverZenithColumnRatio()
    {
        // sqrt(2 * pi * R / H) — the standard result for an exponential atmosphere, and the single
        // factor that turns a merely warm sunset into a copper one. Pinned because it is the one
        // constant here that is neither an anchor nor obviously geometric, so a future reader is most
        // likely to mistake it for a fudge.
        double expected = Math.Sqrt(2.0 * Math.PI * AnchorPlanetRadiusKm / AnchorScaleHeightKm);

        Assert.That(expected, Is.EqualTo(70.737).Within(0.005));
        Assert.That(LimbRefractionMath.LimbPathAmplification, Is.EqualTo((float)expected).Within(0.01f));
    }

    [Test]
    public void GrazingOpticalDepths_StripBlueAndSpareRed()
    {
        // The reason the band is red, as three numbers. Red survives at 2.4%, green at 0.06%, blue at
        // 4e-8 — over the same path, from the same Rayleigh fit, with no per-channel tuning anywhere.
        Assert.Multiple(() =>
        {
            Assert.That(LimbRefractionMath.GrazingOpticalDepthRed, Is.EqualTo(3.720f).Within(0.005f));
            Assert.That(LimbRefractionMath.GrazingOpticalDepthGreen, Is.EqualTo(7.441f).Within(0.005f));
            Assert.That(LimbRefractionMath.GrazingOpticalDepthBlue, Is.EqualTo(17.112f).Within(0.005f));

            Assert.That(MathF.Exp(-LimbRefractionMath.GrazingOpticalDepthRed), Is.EqualTo(0.0242f).Within(0.0005f));
            Assert.That(MathF.Exp(-LimbRefractionMath.GrazingOpticalDepthBlue), Is.LessThan(1e-7f));
        });
    }

    [Test]
    public void ZenithOpticalDepth_FollowsTheStandardRayleighFit()
    {
        // tau = 0.0088 * lambda^-4.15, checked against its published sea-level values so the
        // coefficient and exponent are pinned independently of the amplification above.
        Assert.Multiple(() =>
        {
            Assert.That(LimbRefractionMath.ZenithOpticalDepth(0.65f), Is.EqualTo(0.0526f).Within(0.0005f));
            Assert.That(LimbRefractionMath.ZenithOpticalDepth(0.55f), Is.EqualTo(0.1052f).Within(0.0005f));
            Assert.That(LimbRefractionMath.ZenithOpticalDepth(0.45f), Is.EqualTo(0.2419f).Within(0.0005f));
        });
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
}
