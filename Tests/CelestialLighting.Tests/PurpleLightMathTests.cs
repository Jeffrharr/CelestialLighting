namespace CelestialLighting.Tests;

/// <summary>
/// Offline edge-case coverage for PurpleLightMath.cs (subsystem 19c, the twilight purple light) —
/// no RimWorld/Unity assembly required, since the file has no dependency on either. Complements
/// ApiCompatibilityTests.cs (which only checks vanilla members still exist); these check that our
/// own composition arithmetic is correct, that it actually produces the green minimum that DEFINES
/// purple, that it is bit-identical outside its window, and — the unusual half — that the
/// construction issue #85 originally proposed is impossible rather than merely unchosen.
/// </summary>
[TestFixture]
public class PurpleLightMathTests
{
    private const float Tolerance = 0.0005f;

    /// <summary>The pivot latitude, so every number here describes the same mid-latitude map §19's
    /// own measured transmission table describes.</summary>
    private const float PivotLatitude = OzoneTwilightMath.ColumnPivotLatitudeDegrees;

    /// <summary>Sea level, clean air: the default map, and the one the refutation is sharpest at.</summary>
    private const float SeaLevel = 1f;
    private const float CleanAir = 0f;

    /// <summary>The window's midpoint, where the envelope peaks. Every "is it purple" assertion is
    /// anchored here or side-sampled around it.</summary>
    private const float WindowMidpoint = -5f;

    /// <summary>Vanilla's Clear sky colour, quoted from Data/Core/Defs/WeatherDefs/Weathers.xml.
    /// Both skyColorsNightMid and skyColorsNightEdge carry this exact triple, and glow is
    /// clamp01(sin(elevation)/0.7), so below +4.01 degrees vanilla's sky colour is this CONSTANT at
    /// every elevation — which is what makes the composition tests below describe the real game
    /// rather than a plausible stand-in.</summary>
    private const float VanillaSkyR = 0.482f;
    private const float VanillaSkyG = 0.603f;
    private const float VanillaSkyB = 0.682f;

    /// <summary>The three adapters' sky blends. Mirrored here rather than referenced because the
    /// adapters need UnityEngine and cannot be linked into this project.</summary>
    private const float WarmSkyBlend = 0.35f;
    private const float BlueSkyBlend = 0.45f;
    private const float PurpleSkyBlend = 1f - (1f - WarmSkyBlend) * (1f - BlueSkyBlend);

    // ------------------------------------------------------------------------------------------
    // WindowStrength: the envelope, and the regression pin
    // ------------------------------------------------------------------------------------------

    [TestCase(45f, 0f)] // broad daylight
    [TestCase(0f, 0f)] // geometric horizon
    [TestCase(-3.99f, 0f)] // just above the window
    [TestCase(-4f, 0f)] // §19's onset, exclusive: still exactly nothing
    [TestCase(-4.5f, 0.75f)] // 4 * 0.75 * 0.25
    [TestCase(-5f, 1f)] // midpoint, full strength
    [TestCase(-5.5f, 0.75f)] // symmetric with -4.5
    [TestCase(-6f, 0f)] // §8's night floor, exclusive
    [TestCase(-6.01f, 0f)] // just below the window
    [TestCase(-12f, 0f)] // deep in §19's plateau, and none of our business
    [TestCase(-30f, 0f)] // §7 owns the sky
    public void WindowStrength_MatchesExpected(float elevation, float expected)
    {
        Assert.That(
            PurpleLightMath.WindowStrength(elevation, inVacuum: false),
            Is.EqualTo(expected).Within(Tolerance));
    }

    /// <summary>
    /// The regression pin issue #85 asked for, stated as strongly as it can be: OUTSIDE the two
    /// degree window the envelope is not merely small but exactly zero, at every elevation the sun
    /// can occupy, so every sunset the mod already shipped renders bit-identically. A near-zero
    /// tail would still move already-measured scenario pins.
    /// </summary>
    [Test]
    public void WindowStrength_IsExactlyZeroOutsideTheWindow()
    {
        for (float elevation = -90f; elevation <= 90f; elevation += 0.01f)
        {
            bool inside = elevation > PurpleLightMath.WindowLowerDegrees
                && elevation < PurpleLightMath.WindowUpperDegrees;
            if (!inside)
            {
                Assert.That(
                    PurpleLightMath.WindowStrength(elevation, inVacuum: false),
                    Is.EqualTo(0f),
                    $"elevation {elevation} is outside the window and must contribute nothing at all");
            }
        }
    }

    /// <summary>
    /// Zero AT both edges as well as outside them, which is what removes the seam. §8 is still at
    /// roughly 0.39 strength at -4 and §19 at 0.63 at -6, so an envelope that switched on abruptly
    /// at either boundary would put a visible step in the middle of a live dusk. Sampled as a
    /// continuity check rather than only at the endpoints, since a discontinuity one step inside the
    /// boundary would pass an endpoints-only test.
    /// </summary>
    [Test]
    public void WindowStrength_IsContinuousAcrossBothBoundaries()
    {
        float previous = PurpleLightMath.WindowStrength(-3.5f, inVacuum: false);
        for (float elevation = -3.5f; elevation >= -6.5f; elevation -= 0.001f)
        {
            float current = PurpleLightMath.WindowStrength(elevation, inVacuum: false);
            Assert.That(
                System.Math.Abs(current - previous),
                Is.LessThan(0.005f),
                $"envelope jumped at elevation {elevation}");
            previous = current;
        }
    }

    /// <summary>
    /// The window is symmetric about its midpoint. Not decoration: the whole justification for the
    /// hump is that it has no free parameters, and an asymmetric shape would mean one had crept in.
    /// </summary>
    [TestCase(0.25f)]
    [TestCase(0.5f)]
    [TestCase(0.9f)]
    public void WindowStrength_IsSymmetricAboutTheMidpoint(float offset)
    {
        Assert.That(
            PurpleLightMath.WindowStrength(WindowMidpoint + offset, inVacuum: false),
            Is.EqualTo(PurpleLightMath.WindowStrength(WindowMidpoint - offset, inVacuum: false))
                .Within(Tolerance));
    }

    /// <summary>
    /// The window is DEFINED as §8's and §19's overlap, so it must read its bounds from them rather
    /// than carry its own copies. A structural pin: if either subsystem's boundary moves and this
    /// one silently does not, the purple detaches into a third independently-tuned band, which is
    /// the failure mode the constants exist to prevent.
    /// </summary>
    [Test]
    public void Window_IsExactlyTheOverlapOfSubsystems8And19()
    {
        Assert.That(PurpleLightMath.WindowUpperDegrees, Is.EqualTo(OzoneTwilightMath.BlueOnsetDegrees));
        Assert.That(PurpleLightMath.WindowLowerDegrees, Is.EqualTo(SkyColorTemperature.NightFadeFloorDegrees));
        Assert.That(
            PurpleLightMath.WindowUpperDegrees,
            Is.GreaterThan(PurpleLightMath.WindowLowerDegrees),
            "the window must be non-empty, or the subsystem is unreachable");
    }

    /// <summary>
    /// §18's vacuum gate. The purple light superposes two atmospheric scattering sources, neither of
    /// which exists on an airless world, so unlike §8 — which still pins an honest unreddened
    /// ZenithKelvin — there is nothing to report and the whole effect is zero. Threaded as a
    /// parameter rather than early-returned in the adapter, per §18a, so this is the enforcement
    /// point no caller can route around.
    /// </summary>
    [TestCase(-4.5f)]
    [TestCase(-5f)]
    [TestCase(-5.5f)]
    public void WindowStrength_IsZeroInVacuumEverywhereInsideTheWindow(float elevation)
    {
        Assert.That(
            PurpleLightMath.WindowStrength(elevation, inVacuum: true),
            Is.EqualTo(0f),
            "vacuum has no atmosphere to superpose, so the effect is absent rather than shallow");
        Assert.That(
            PurpleLightMath.WindowStrength(elevation, inVacuum: false),
            Is.GreaterThan(0f),
            "non-vacuity: if the atmospheric arm were also zero this test would pass vacuously");
    }

    // ------------------------------------------------------------------------------------------
    // ComposedHue: the green minimum, which is what purple IS
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// The headline invariant, and the one issue #85 named. Across the window the composed hue puts
    /// GREEN BELOW BOTH other channels. That ordering inverts §8's tested R >= G >= B and §19's
    /// B > G > R at once, which is exactly why this composition cannot live inside either file.
    /// </summary>
    [TestCase(-4.2f)]
    [TestCase(-4.5f)]
    [TestCase(-5f)]
    [TestCase(-5.5f)]
    [TestCase(-5.8f)]
    public void ComposedHue_PutsGreenBelowBothOtherChannels(float elevation)
    {
        SkyColorTemperature.Rgb hue = Composed(elevation);

        Assert.That(hue.G, Is.LessThan(hue.R), "green must sit below red");
        Assert.That(hue.G, Is.LessThan(hue.B), "green must sit below blue");
    }

    /// <summary>
    /// The deficit is not merely present but LARGE — at least 10% of the peak channel across the
    /// window, and around 15% at the midpoint. §9's and §19's headers both record subsystems that
    /// died from being technically-present-and-invisible, so a bare ordering assertion would repeat
    /// that mistake. 8-bit terms: 31/255 at -4 rising to 44/255 at -6.
    /// </summary>
    [TestCase(-4f, 0.1218f)]
    [TestCase(-4.5f, 0.1355f)]
    [TestCase(-5f, 0.1487f)]
    [TestCase(-5.5f, 0.1616f)]
    [TestCase(-6f, 0.1741f)]
    public void ComposedHue_GreenDeficitIsDeepEnoughToSee(float elevation, float expectedDeficit)
    {
        SkyColorTemperature.Rgb hue = Composed(elevation);
        float deficit = System.Math.Min(hue.R, hue.B) - hue.G;

        Assert.That(deficit, Is.EqualTo(expectedDeficit).Within(0.001f));
        Assert.That(deficit, Is.GreaterThan(0.10f), "a deficit under 10% would not read on screen");
    }

    /// <summary>
    /// BalancedBlueFraction does what its name says: after the mix, red and blue are EQUAL. That is
    /// the definition the weight is solved from, and it is also what makes the trough maximal —
    /// green is the one channel neither source carries, so tilting the mix either way dims one peak
    /// toward green's level and fills the trough in.
    /// </summary>
    [TestCase(-4f)]
    [TestCase(-4.5f)]
    [TestCase(-5f)]
    [TestCase(-5.5f)]
    [TestCase(-6f)]
    public void ComposedHue_BalancesRedAgainstBlue(float elevation)
    {
        SkyColorTemperature.Rgb hue = Composed(elevation);

        Assert.That(hue.R, Is.EqualTo(hue.B).Within(Tolerance));
        Assert.That(hue.R, Is.EqualTo(1f).Within(Tolerance), "normalisation pins the peak at 1");
    }

    /// <summary>
    /// Non-vacuity for the test above: the balance is a real solve, not an artefact of two sources
    /// that happened to be symmetric. The weight moves with elevation because both source spectra do
    /// — the warm end is fixed but the Chappuis notch deepens as the slant path grows — and it sits
    /// well away from a trivial 0.5.
    /// </summary>
    [TestCase(-4f, 0.6715f)]
    [TestCase(-5f, 0.6365f)]
    [TestCase(-6f, 0.6094f)]
    public void BalancedBlueFraction_IsASolveRatherThanAConstant(float elevation, float expected)
    {
        SkyColorTemperature.Rgb warm = NormalisedWarm(elevation);
        SkyColorTemperature.Rgb blue = OzoneTwilightMath.ChappuisTransmission(elevation, PivotLatitude);

        Assert.That(
            PurpleLightMath.BalancedBlueFraction(warm, blue),
            Is.EqualTo(expected).Within(0.001f));
    }

    /// <summary>
    /// The sanity bound issue #85 asked for against a composition running away: the result is never
    /// more selective than the more selective of its two inputs. Structural rather than tuned — a
    /// normalised convex combination cannot leave the hull of its endpoints — but pinned anyway,
    /// because "structural" is a property of the current implementation and not of the signature.
    /// </summary>
    [TestCase(-4.2f)]
    [TestCase(-5f)]
    [TestCase(-5.8f)]
    public void ComposedHue_IsNeverMoreSelectiveThanItsInputs(float elevation)
    {
        SkyColorTemperature.Rgb warm = NormalisedWarm(elevation);
        SkyColorTemperature.Rgb blue = OzoneTwilightMath.ChappuisTransmission(elevation, PivotLatitude);
        SkyColorTemperature.Rgb hue = Composed(elevation);

        float mostSelectiveInput = System.Math.Max(Selectivity(warm), Selectivity(blue));

        Assert.That(Selectivity(hue), Is.LessThanOrEqualTo(mostSelectiveInput + Tolerance));
        Assert.That(mostSelectiveInput, Is.GreaterThan(0.4f), "non-vacuity: the inputs really are selective");
    }

    /// <summary>
    /// Latitude reaches the composition only through §19's ozone COLUMN (issue #82), so a polar map
    /// crosses more absorber and gets a deeper trough at the same elevation. Airmass is held fixed by
    /// sampling one elevation, which is what makes this a column test rather than a geometry one.
    /// </summary>
    [Test]
    public void ComposedHue_DeepensWithLatitudeAtFixedElevation()
    {
        SkyColorTemperature.Rgb tropical = PurpleLightMath.ComposedHue(
            WindowMidpoint, 0f, SeaLevel, CleanAir, inVacuum: false);
        SkyColorTemperature.Rgb polar = PurpleLightMath.ComposedHue(
            WindowMidpoint, 88f, SeaLevel, CleanAir, inVacuum: false);

        Assert.That(polar.G, Is.LessThan(tropical.G));
        Assert.That(
            tropical.G - polar.G,
            Is.GreaterThan(0.005f),
            "non-vacuity: the two ends must actually differ, not merely order correctly");
    }

    // ------------------------------------------------------------------------------------------
    // The composition against the real vanilla palette
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// What the player actually sees. Applied to vanilla's real Clear sky colour after §8 and §19
    /// have had their turn, the nudge moves RED ABOVE GREEN — the sky crosses from "differently
    /// blue" to the purple side of neutral. Today it does not: §8 and §19 alone leave green above
    /// red at every elevation in the window, which is the whole complaint issue #85 opens with.
    /// </summary>
    [TestCase(-4.6f)]
    [TestCase(-4.8f)]
    [TestCase(-5f)]
    [TestCase(-5.2f)]
    [TestCase(-5.4f)]
    public void AgainstVanillaPalette_PurpleLightMovesRedAboveGreen(float elevation)
    {
        (float r, float g, float b) before = WarmAndBlueOnly(elevation);
        (float r, float g, float b) after = WithPurpleLight(elevation);

        Assert.That(
            before.g,
            Is.GreaterThan(before.r),
            "baseline check: without §19c the window is green-above-red, i.e. not purple");
        Assert.That(
            after.r,
            Is.GreaterThan(after.g),
            "with §19c the sky sits on the purple side of neutral");
        Assert.That(after.g, Is.LessThan(after.b), "and green stays the minimum channel");
    }

    /// <summary>
    /// The same pin at the window edges, from the other direction: at -4 and -6 the composed result
    /// is BIT-IDENTICAL to what §8 and §19 alone produce. This is the seam test — the envelope's two
    /// zeros expressed against the real palette rather than in isolation.
    /// </summary>
    [TestCase(-4f)]
    [TestCase(-6f)]
    [TestCase(-3f)]
    [TestCase(-8f)]
    [TestCase(10f)]
    public void AgainstVanillaPalette_IsUnchangedAtAndOutsideTheWindowEdges(float elevation)
    {
        (float r, float g, float b) before = WarmAndBlueOnly(elevation);
        (float r, float g, float b) after = WithPurpleLight(elevation);

        Assert.That(after.r, Is.EqualTo(before.r));
        Assert.That(after.g, Is.EqualTo(before.g));
        Assert.That(after.b, Is.EqualTo(before.b));
    }

    /// <summary>
    /// Issue #85's third diagnosis — that §8 and §19 compose as "two independent additive tints"
    /// which "blend the purple away into a muddy neutral" — is arithmetically false, and this pins
    /// why. Successive Color.Lerps toward FIXED targets are EXACTLY one lerp toward the
    /// weight-averaged target: with W = 1 - (1 - a)(1 - b) the two forms agree to float precision.
    /// Nothing is lost to sequencing that a single composed lerp would have kept, which is why §19c
    /// adds a source rather than restructuring the blend.
    /// </summary>
    [TestCase(-4.5f)]
    [TestCase(-5f)]
    [TestCase(-5.5f)]
    public void SequentialLerps_AreOneCompositeLerp(float elevation)
    {
        SkyColorTemperature.Rgb warm = SkyColorTemperature.SkyColorForElevation(
            elevation, SeaLevel, CleanAir, inVacuum: false);
        SkyColorTemperature.Rgb blue = OzoneTwilightMath.ChappuisTransmission(elevation, PivotLatitude);

        float a = SkyColorTemperature.TintStrength(elevation, SeaLevel, inVacuum: false) * WarmSkyBlend;
        float b = OzoneTwilightMath.BandStrength(elevation, inVacuum: false) * BlueSkyBlend;

        // Sequential: lerp toward warm, then toward the blue hue rescaled to a FIXED brightest, so
        // the two forms are comparing the same thing. (§19's live rescale reads the intermediate
        // colour, which is the one genuine non-commutativity and is not what this test is about.)
        float brightest = System.Math.Max(VanillaSkyR, System.Math.Max(VanillaSkyG, VanillaSkyB));
        (float r, float g, float b) blueTarget = (blue.R * brightest, blue.G * brightest, blue.B * brightest);

        (float r, float g, float b) step = (
            Lerp(VanillaSkyR, warm.R, a), Lerp(VanillaSkyG, warm.G, a), Lerp(VanillaSkyB, warm.B, a));
        (float r, float g, float b) sequential = (
            Lerp(step.r, blueTarget.r, b), Lerp(step.g, blueTarget.g, b), Lerp(step.b, blueTarget.b, b));

        // One lerp toward the weight-averaged target.
        float combined = a + b - a * b;
        (float r, float g, float b) averaged = (
            (warm.R * a * (1f - b) + blueTarget.r * b) / combined,
            (warm.G * a * (1f - b) + blueTarget.g * b) / combined,
            (warm.B * a * (1f - b) + blueTarget.b * b) / combined);
        (float r, float g, float b) single = (
            Lerp(VanillaSkyR, averaged.r, combined),
            Lerp(VanillaSkyG, averaged.g, combined),
            Lerp(VanillaSkyB, averaged.b, combined));

        Assert.That(single.r, Is.EqualTo(sequential.r).Within(1e-5f));
        Assert.That(single.g, Is.EqualTo(sequential.g).Within(1e-5f));
        Assert.That(single.b, Is.EqualTo(sequential.b).Within(1e-5f));
    }

    // ------------------------------------------------------------------------------------------
    // The refutation: why the multiplicative construction of issue #85 is impossible
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// The colorimetric refutation. At §8's sea-level horizon endpoint the series composition's
    /// feasible band on s/m is EMPTY — its lower bound sits above its upper bound — so no pair of
    /// strengths anywhere produces a green notch. The condition is scale-free, which is what makes
    /// this a refutation rather than a tuning note: turning either subsystem up or down cannot
    /// change it.
    /// </summary>
    [Test]
    public void SeriesComposition_CannotNotchGreenAtSeaLevel()
    {
        Assert.That(
            PurpleLightMath.SeriesGreenNotchIsReachable(SkyColorTemperature.HorizonKelvin, PivotLatitude),
            Is.False);

        PurpleLightMath.SeriesGreenNotchBand(
            SkyColorTemperature.HorizonKelvin, PivotLatitude, out float lower, out float upper);

        Assert.That(lower, Is.EqualTo(0.0202f).Within(0.0002f));
        Assert.That(upper, Is.EqualTo(0.0134f).Within(0.0002f));
        Assert.That(lower, Is.GreaterThan(upper), "an empty band is what 'impossible' means here");
    }

    /// <summary>
    /// Pollution makes it worse, not better. §20b walks the horizon endpoint DOWN toward 1000 K,
    /// which deepens the blackbody's blue attenuation and drives the two bounds further apart — so
    /// the one condition under which a reader might expect the multiply to start working is exactly
    /// where it fails hardest.
    /// </summary>
    [Test]
    public void SeriesComposition_IsFurtherFromWorkingUnderPollution()
    {
        PurpleLightMath.SeriesGreenNotchBand(
            SkyColorTemperature.HorizonKelvin, PivotLatitude, out float cleanLower, out float cleanUpper);
        PurpleLightMath.SeriesGreenNotchBand(
            SkyColorTemperature.AerosolHorizonKelvin, PivotLatitude, out float dirtyLower, out float dirtyUpper);

        Assert.That(
            PurpleLightMath.SeriesGreenNotchIsReachable(SkyColorTemperature.AerosolHorizonKelvin, PivotLatitude),
            Is.False);
        Assert.That(
            dirtyLower - dirtyUpper,
            Is.GreaterThan(cleanLower - cleanUpper),
            "a browner horizon endpoint widens the gap the series construction has to close");
    }

    /// <summary>
    /// The hole in the refutation, kept honest and kept under test: the band DOES open once §20's
    /// site-altitude term walks the horizon endpoint past roughly 2181 K. The refutation survives
    /// because the strength wall then bites — see the test below — but a reader who checks only the
    /// sea-level case deserves to find this here rather than discover it themselves.
    /// </summary>
    [TestCase(2100f, false)]
    [TestCase(2200f, true)]
    [TestCase(2650f, true)] // §20's endpoint at 4000 m
    public void SeriesComposition_BandOpensOnlyForAThinnerAirColumn(float horizonKelvin, bool expected)
    {
        Assert.That(
            PurpleLightMath.SeriesGreenNotchIsReachable(horizonKelvin, PivotLatitude),
            Is.EqualTo(expected));
    }

    /// <summary>
    /// …and the second wall, which is what closes that hole. Where the shape condition is
    /// satisfiable the required §8 weight is far larger than §8's own TintStrength ever reaches
    /// inside the window — and §20 scales TintStrength DOWN by the very pressureFraction that opened
    /// the band, so the two conditions pull in opposite directions. There is no map on which both
    /// hold at once.
    /// </summary>
    [TestCase(0.87f)] // ~1200 m, just past where the band opens
    [TestCase(0.70f)] // ~3000 m
    [TestCase(0.55f)] // ~5000 m
    public void SeriesComposition_NeedsMoreWarmthThanSubsystem8EverHasInTheWindow(float pressureFraction)
    {
        float horizonKelvin = SkyColorTemperature.HorizonKelvinForPressure(pressureFraction);
        PurpleLightMath.SeriesGreenNotchBand(horizonKelvin, PivotLatitude, out float lower, out float upper);
        Assert.That(lower, Is.LessThan(upper), "non-vacuity: the shape condition really is open here");

        for (float elevation = PurpleLightMath.WindowLowerDegrees;
             elevation <= PurpleLightMath.WindowUpperDegrees;
             elevation += 0.05f)
        {
            float airmass = OzoneTwilightMath.SlantAirmass(elevation);
            float available = SkyColorTemperature.TintStrength(elevation, pressureFraction, inVacuum: false);

            Assert.That(
                available,
                Is.LessThan(lower * airmass),
                $"at elevation {elevation} §8 would need weight {lower * airmass} and only has {available}");
        }
    }

    /// <summary>
    /// The geometric refutation, which is the deeper of the two: across this window the Earth's
    /// shadow already stands above the whole troposphere, so the ozone path and the reddened path
    /// are not the same ray and cannot be composed in series at all. Anchored on the standard
    /// h = R(sec(theta) - 1).
    /// </summary>
    [TestCase(-4f, 15.56f)]
    [TestCase(-5f, 24.34f)]
    [TestCase(-6f, 35.09f)]
    public void ShadowHeight_MatchesTheClosedForm(float elevation, float expectedKm)
    {
        Assert.That(PurpleLightMath.ShadowHeightKm(elevation), Is.EqualTo(expectedKm).Within(0.02f));
    }

    /// <summary>
    /// The same fact stated as the claim it supports: everywhere in the window the shadow is above
    /// the troposphere (~12 km) and inside or below the ozone layer (20-30 km), which is precisely
    /// the geometry that makes the two sources parallel rather than serial.
    /// </summary>
    [Test]
    public void ShadowHeight_ClearsTheTroposphereEverywhereInTheWindow()
    {
        for (float elevation = PurpleLightMath.WindowUpperDegrees;
             elevation >= PurpleLightMath.WindowLowerDegrees;
             elevation -= 0.05f)
        {
            Assert.That(
                PurpleLightMath.ShadowHeightKm(elevation),
                Is.GreaterThan(12f),
                $"at elevation {elevation} the troposphere must already be in shadow");
        }
    }

    [TestCase(0f)]
    [TestCase(5f)]
    [TestCase(45f)]
    public void ShadowHeight_IsZeroWithTheSunUp(float elevation)
    {
        Assert.That(PurpleLightMath.ShadowHeightKm(elevation), Is.EqualTo(0f));
    }

    // ------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------

    private static SkyColorTemperature.Rgb Composed(float elevation) =>
        PurpleLightMath.ComposedHue(elevation, PivotLatitude, SeaLevel, CleanAir, inVacuum: false);

    private static SkyColorTemperature.Rgb NormalisedWarm(float elevation)
    {
        SkyColorTemperature.Rgb warm = SkyColorTemperature.SkyColorForElevation(
            elevation, SeaLevel, CleanAir, inVacuum: false);
        float brightest = System.Math.Max(warm.R, System.Math.Max(warm.G, warm.B));
        return new SkyColorTemperature.Rgb(warm.R / brightest, warm.G / brightest, warm.B / brightest);
    }

    /// <summary>1 - min/max: how far the colour is from grey. "Selectivity" rather than "saturation"
    /// because §8's lane rule forbids touching SkyColorSet.saturation and reusing the word invites
    /// exactly that confusion.</summary>
    private static float Selectivity(SkyColorTemperature.Rgb c)
    {
        float max = System.Math.Max(c.R, System.Math.Max(c.G, c.B));
        float min = System.Math.Min(c.R, System.Math.Min(c.G, c.B));
        return max <= 0f ? 0f : 1f - min / max;
    }

    /// <summary>Vanilla's Clear sky put through §8's and §19's lerps exactly as the two adapters do
    /// them, in the order Harmony runs them.</summary>
    private static (float r, float g, float b) WarmAndBlueOnly(float elevation)
    {
        SkyColorTemperature.Rgb warm = SkyColorTemperature.SkyColorForElevation(
            elevation, SeaLevel, CleanAir, inVacuum: false);
        float a = SkyColorTemperature.TintStrength(elevation, SeaLevel, inVacuum: false) * WarmSkyBlend;

        (float r, float g, float b) afterWarm = (
            Lerp(VanillaSkyR, warm.R, a), Lerp(VanillaSkyG, warm.G, a), Lerp(VanillaSkyB, warm.B, a));

        SkyColorTemperature.Rgb blue = OzoneTwilightMath.ChappuisTransmission(elevation, PivotLatitude);
        float b19 = OzoneTwilightMath.BandStrength(elevation, inVacuum: false) * BlueSkyBlend;

        return LerpTowardHue(afterWarm, blue, b19);
    }

    private static (float r, float g, float b) WithPurpleLight(float elevation)
    {
        (float r, float g, float b) baseline = WarmAndBlueOnly(elevation);
        float window = PurpleLightMath.WindowStrength(elevation, inVacuum: false) * PurpleSkyBlend;

        return LerpTowardHue(baseline, Composed(elevation), window);
    }

    /// <summary>The adapters' shared BlendTowardHue, mirrored: rescale the normalised hue to the
    /// source colour's own brightest channel so only channel RATIOS move.</summary>
    private static (float r, float g, float b) LerpTowardHue(
        (float r, float g, float b) from, SkyColorTemperature.Rgb hue, float t)
    {
        float brightest = System.Math.Max(from.r, System.Math.Max(from.g, from.b));
        return (
            Lerp(from.r, hue.R * brightest, t),
            Lerp(from.g, hue.G * brightest, t),
            Lerp(from.b, hue.B * brightest, t));
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
