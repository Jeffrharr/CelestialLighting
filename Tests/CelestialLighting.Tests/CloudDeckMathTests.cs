namespace CelestialLighting.Tests;

// Offline coverage for §25b's cloud varieties (Source/CloudDeckMath.cs) — the deck table, the
// mixture that decomposes §13's single classified deck altitude into it, and the per-sheet draw.
// Linked into this project via <Compile Include> so these exercise the exact code that ships.
[TestFixture]
public class CloudDeckMathTests
{
    private const float Tolerance = 1e-3f;

    private static float[] MixtureAt(float altitudeMetres)
    {
        float[] weights = new float[CloudDeckMath.DeckCount];
        CloudDeckMath.MixtureFor(weights, altitudeMetres);
        return weights;
    }

    // --- The table ---

    // The decks have to be ordered by height, because everything else in the subsystem reads the
    // index as a height: CloudSheetLayout.BlobFor turns it into an atlas row, CloudDeckMath.DeckFor
    // walks the mixture in index order so the sky layers upward, and the sunset sequence in
    // CloudSheetMathTests is stated in terms of low-then-mid-then-high.
    [Test]
    public void DecksAreOrderedByAltitude()
    {
        float previous = -1f;
        for (int deck = 0; deck < CloudDeckMath.DeckCount; deck++)
        {
            float altitude = CloudDeckMath.DeckAt(deck).AltitudeMetres;
            Assert.That(altitude, Is.GreaterThan(previous), $"deck {deck} is not above deck {deck - 1}");
            previous = altitude;
        }
    }

    // Optically thin higher up, which is the one column of the table that is a description rather
    // than a calibration — and the one that reaches all three cloud lanes at once, because
    // CloudSheetLayout folds it into Placement.Alpha rather than handing it to the renderer.
    [Test]
    public void HigherDecksAreThinnerFasterAndAtMostAsOpaqueAsTheLowOne()
    {
        float previousOpacity = 2f;
        float previousSpeed = 0f;

        for (int deck = 0; deck < CloudDeckMath.DeckCount; deck++)
        {
            CloudDeckMath.Deck spec = CloudDeckMath.DeckAt(deck);
            Assert.That(spec.Opacity, Is.LessThan(previousOpacity).And.GreaterThan(0f));
            Assert.That(spec.SpeedScale, Is.GreaterThan(previousSpeed));
            Assert.That(spec.SizeScale, Is.GreaterThan(0f));
            Assert.That(spec.FrequencyU, Is.GreaterThan(0f));
            Assert.That(spec.FrequencyV, Is.GreaterThan(0f));
            previousOpacity = spec.Opacity;
            previousSpeed = spec.SpeedScale;
        }
    }

    // §25 SHIPPED ONE SHAPING CURVE, AND THE LOW DECK IS STILL IT. This is what makes "a sky that
    // draws only low cloud draws exactly what §25 drew" a checkable statement rather than a comment:
    // if someone retunes the low row, the claim in CloudSheetOverlay.BuildAtlas stops being true and
    // this fails rather than the frames quietly moving.
    [Test]
    public void TheLowDeckKeepsTheCurveTheAtlasShippedWith()
    {
        CloudDeckMath.Deck low = CloudDeckMath.DeckAt(CloudDeckMath.LowDeck);

        Assert.That(low.ShapeCut, Is.EqualTo(CloudField.DefaultShapeCut).Within(Tolerance));
        Assert.That(low.ShapeGain, Is.EqualTo(CloudField.DefaultShapeGain).Within(Tolerance));
        Assert.That(low.FrequencyU, Is.EqualTo(1f).Within(Tolerance));
        Assert.That(low.FrequencyV, Is.EqualTo(1f).Within(Tolerance));
        Assert.That(low.SizeScale, Is.EqualTo(1f).Within(Tolerance));
        Assert.That(low.SpeedScale, Is.EqualTo(1f).Within(Tolerance));
        Assert.That(low.Opacity, Is.EqualTo(1f).Within(Tolerance));
    }

    // Out-of-range indices clamp rather than throw: the deck rides through Placement and out to
    // three overlays, and a rendering path is the wrong place to raise.
    [Test]
    public void AnOutOfRangeDeckClampsIntoTheTable()
    {
        Assert.That(CloudDeckMath.DeckAt(-5).AltitudeMetres,
            Is.EqualTo(CloudDeckMath.DeckAt(CloudDeckMath.LowDeck).AltitudeMetres));
        Assert.That(CloudDeckMath.DeckAt(99).AltitudeMetres,
            Is.EqualTo(CloudDeckMath.DeckAt(CloudDeckMath.HighDeck).AltitudeMetres));
    }

    // --- The geometry ---

    // Delegated to CloudUnderlightMath rather than re-derived, so §23's flat lane and §25's sheet
    // cannot end up disagreeing about when a sunset ends. Pinned by identity, not by value: a test
    // that restated arccos(R/(R+h)) here would be the second copy this delegation exists to avoid.
    [Test]
    public void ShadowEntryComesFromTheOneEarthShadowModel()
    {
        for (int deck = 0; deck < CloudDeckMath.DeckCount; deck++)
        {
            Assert.That(
                CloudDeckMath.ShadowEntryDegrees(deck),
                Is.EqualTo(CloudUnderlightMath.ShadowEntryDepressionDegrees(
                    CloudDeckMath.DeckAt(deck).AltitudeMetres)).Within(1e-5f));
        }
    }

    // The window widths the whole visual effect rests on. Loose bounds rather than exact values —
    // the point is the SPREAD, that a low deck's sunset is over in about a degree while cirrus gets
    // three times as long, not that any one of them is 2.267 rather than 2.3.
    [TestCase(CloudDeckMath.LowDeck, 0.9f, 1.2f)]
    [TestCase(CloudDeckMath.MidDeck, 2.0f, 2.5f)]
    [TestCase(CloudDeckMath.HighDeck, 2.9f, 3.4f)]
    public void EachDeckHoldsTheSunForAboutAsLongAsItsHeightSays(int deck, float low, float high)
    {
        Assert.That(CloudDeckMath.ShadowEntryDegrees(deck), Is.InRange(low, high));
    }

    // --- The mixture ---

    [Test]
    public void EveryMixtureIsANormalisedDistribution()
    {
        for (float altitude = 0f; altitude <= 14000f; altitude += 250f)
        {
            float[] weights = MixtureAt(altitude);
            float total = 0f;

            foreach (float weight in weights)
            {
                Assert.That(weight, Is.GreaterThanOrEqualTo(0f), $"negative weight at {altitude} m");
                total += weight;
            }

            Assert.That(total, Is.EqualTo(1f).Within(Tolerance), $"weights do not sum to 1 at {altitude} m");
        }
    }

    // THE ANCHOR THAT MAKES THE DECOMPOSITION WELL-POSED. Rain falls out of the low deck, so a
    // raining sky is the low deck — and because the low deck's altitude IS
    // WeatherDimmingMath.PrecipitatingDeckDefaultAltitudeMetres rather than a nearby round number,
    // the mixture's own mean altitude reproduces the classifier exactly at this end instead of
    // nearly. That exactness is the reason the two constants are shared rather than restated.
    [Test]
    public void ARainingSkyIsAllLowDeckAndAgreesWithTheClassifierExactly()
    {
        float[] weights = MixtureAt(WeatherDimmingMath.PrecipitatingDeckDefaultAltitudeMetres);

        Assert.That(weights[CloudDeckMath.LowDeck], Is.EqualTo(1f).Within(Tolerance));
        Assert.That(weights[CloudDeckMath.MidDeck], Is.EqualTo(0f).Within(Tolerance));
        Assert.That(weights[CloudDeckMath.HighDeck], Is.EqualTo(0f).Within(Tolerance));

        Assert.That(
            CloudDeckMath.MeanAltitudeMetres(weights),
            Is.EqualTo(WeatherDimmingMath.PrecipitatingDeckDefaultAltitudeMetres).Within(Tolerance));

        // And a heavier-than-classified deck cannot go below it.
        Assert.That(MixtureAt(0f)[CloudDeckMath.LowDeck], Is.EqualTo(1f).Within(Tolerance));
        Assert.That(MixtureAt(-500f)[CloudDeckMath.LowDeck], Is.EqualTo(1f).Within(Tolerance));
    }

    // THE CASE A KERNEL GOT WRONG, which is why this is pinned. A Gaussian in log-altitude centred
    // on the classified value hands a dry sky almost entirely to the 5 km mid deck, because
    // DryDeckDefaultAltitudeMetres is 4000 — and a partly-cloudy Clear evening is both the commonest
    // sunset and the one §22 exists to create. A fair-weather sky has to have fair-weather cumulus
    // in it, and it has to have some cirrus, or the varieties are invisible on the sky that matters.
    [Test]
    public void ADrySkyIsLayeredWithRealCumulusAndRealCirrus()
    {
        float[] weights = MixtureAt(WeatherDimmingMath.DryDeckDefaultAltitudeMetres);

        Assert.That(weights[CloudDeckMath.LowDeck], Is.GreaterThan(0.35f),
            "a fair-weather sky is mostly fair-weather cumulus");
        Assert.That(weights[CloudDeckMath.HighDeck], Is.GreaterThan(0.15f),
            "and it has cirrus in it, which no kernel centred on 4000 m produces");
        Assert.That(weights[CloudDeckMath.MidDeck], Is.GreaterThan(0f));
    }

    // A def stating a high altitude through WeatherCloudDeck is saying "this weather is high thin
    // cloud" — the escape hatch WeatherDimmingMath.DefaultAltitudeMetres names as the only way to
    // reach issue #88's cirrus row. It has to actually get one.
    [Test]
    public void ADeclaredHighDeckIsMostlyCirrusButNotOnlyCirrus()
    {
        float[] weights = MixtureAt(CloudDeckMath.DeckAt(CloudDeckMath.HighDeck).AltitudeMetres);

        Assert.That(weights[CloudDeckMath.HighDeck], Is.GreaterThan(0.7f));
        Assert.That(weights[CloudDeckMath.MidDeck], Is.GreaterThan(0f),
            "even a cirrus sky has something under it — a single-member mixture is the uniform slab");
        Assert.That(MixtureAt(30000f)[CloudDeckMath.HighDeck],
            Is.EqualTo(weights[CloudDeckMath.HighDeck]).Within(Tolerance));
    }

    // MONOTONE IN ALTITUDE, which matters more than it looks. The mixture is re-evaluated every
    // frame and a weather transition slides the classified altitude continuously, so a non-monotone
    // mixture would have sheets converting back and forth across a boundary while the sky made up
    // its mind — visible as clouds flickering between types.
    [Test]
    public void LiftingTheDeckMovesWeightUpwardAndNeverBack()
    {
        float previousLow = 2f;
        float previousHigh = -1f;

        for (float altitude = 500f; altitude <= 12000f; altitude += 100f)
        {
            float[] weights = MixtureAt(altitude);

            Assert.That(weights[CloudDeckMath.LowDeck], Is.LessThanOrEqualTo(previousLow + Tolerance),
                $"low deck gained weight as the deck lifted, at {altitude} m");
            Assert.That(weights[CloudDeckMath.HighDeck], Is.GreaterThanOrEqualTo(previousHigh - Tolerance),
                $"high deck lost weight as the deck lifted, at {altitude} m");

            previousLow = weights[CloudDeckMath.LowDeck];
            previousHigh = weights[CloudDeckMath.HighDeck];
        }
    }

    // The mixture's mean altitude has to rise with the classified one it decomposes. It is NOT equal
    // to it in the middle — the whole point of the decomposition is that a dry sky's clouds are not
    // all at 4 km, they are at 1 and 5 and 9.5 — but it must not go the other way.
    [Test]
    public void TheMixturesCentreOfMassRisesWithTheClassifiedDeck()
    {
        float previous = -1f;
        for (float altitude = 500f; altitude <= 12000f; altitude += 100f)
        {
            float mean = CloudDeckMath.MeanAltitudeMetres(MixtureAt(altitude));
            Assert.That(mean, Is.GreaterThanOrEqualTo(previous - Tolerance), $"fell at {altitude} m");
            previous = mean;
        }
    }

    // --- The per-sheet draw ---

    // The draw walks cumulative weights in deck order, so the bottom of the [0,1) range is the low
    // deck and the top is the high one. That ordering is what makes a lifting sky convert sheets
    // upward one at a time rather than reshuffling them.
    [Test]
    public void TheDrawPartitionsTheUnitRangeInDeckOrder()
    {
        float[] weights = MixtureAt(WeatherDimmingMath.DryDeckDefaultAltitudeMetres);

        int previous = -1;
        for (float draw = 0f; draw < 1f; draw += 0.01f)
        {
            int deck = CloudDeckMath.DeckFor(weights, draw);
            Assert.That(deck, Is.GreaterThanOrEqualTo(previous), $"deck went backwards at draw {draw}");
            Assert.That(deck, Is.InRange(0, CloudDeckMath.DeckCount - 1));
            previous = deck;
        }
    }

    // The realised split over many draws has to match the weights it was drawn from, or the mixture
    // is a decoration rather than a distribution.
    [Test]
    public void ManyDrawsReproduceTheMixture()
    {
        float[] weights = MixtureAt(WeatherDimmingMath.DryDeckDefaultAltitudeMetres);
        int[] counts = new int[CloudDeckMath.DeckCount];

        const int Samples = 10000;
        for (int i = 0; i < Samples; i++)
            counts[CloudDeckMath.DeckFor(weights, (i + 0.5f) / Samples)]++;

        for (int deck = 0; deck < CloudDeckMath.DeckCount; deck++)
        {
            Assert.That(counts[deck] / (float)Samples, Is.EqualTo(weights[deck]).Within(0.01f),
                $"deck {deck} came out at the wrong share");
        }
    }

    // Degenerate inputs draw the low deck rather than throwing or landing out of range — the same
    // "a rendering path is the wrong place to raise" discipline DeckAt keeps. An all-zero mixture is
    // reachable: WeatherDimming.CloudAltitudeMetresFor returns 0 on a map with no sky.
    [Test]
    public void DegenerateMixturesDrawTheLowDeck()
    {
        Assert.That(CloudDeckMath.DeckFor(null, 0.5f), Is.EqualTo(CloudDeckMath.LowDeck));
        Assert.That(CloudDeckMath.DeckFor(new float[CloudDeckMath.DeckCount], 0.5f),
            Is.EqualTo(CloudDeckMath.LowDeck));
        Assert.That(CloudDeckMath.DeckFor(new[] { 1f }, 0.5f), Is.EqualTo(CloudDeckMath.LowDeck));

        float[] weights = MixtureAt(WeatherDimmingMath.DryDeckDefaultAltitudeMetres);
        Assert.That(CloudDeckMath.DeckFor(weights, float.NaN), Is.InRange(0, CloudDeckMath.DeckCount - 1));
        Assert.That(CloudDeckMath.DeckFor(weights, 1f), Is.InRange(0, CloudDeckMath.DeckCount - 1));
        Assert.That(CloudDeckMath.DeckFor(weights, -2f), Is.EqualTo(CloudDeckMath.LowDeck));
    }

    // The shaping columns are what CloudField.FillBlobAtlas indexes BY ROW, so there must be exactly
    // one entry per deck — an atlas row with no curve silently falls back to the low deck's and the
    // sky quietly loses a variety.
    [Test]
    public void TheShapingColumnsHaveOneEntryPerDeck()
    {
        Assert.That(CloudDeckMath.ShapeCuts().Length, Is.EqualTo(CloudDeckMath.DeckCount));
        Assert.That(CloudDeckMath.ShapeGains().Length, Is.EqualTo(CloudDeckMath.DeckCount));
        Assert.That(CloudDeckMath.FrequenciesU().Length, Is.EqualTo(CloudDeckMath.DeckCount));
        Assert.That(CloudDeckMath.FrequenciesV().Length, Is.EqualTo(CloudDeckMath.DeckCount));

        // Handed out fresh each call rather than shared, because a cached array returned from a
        // static class is a mutable global with extra steps.
        Assert.That(CloudDeckMath.ShapeCuts(), Is.Not.SameAs(CloudDeckMath.ShapeCuts()));
    }
}
