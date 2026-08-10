namespace CelestialLighting.Tests;

// Offline coverage for the pure §23b underlit-cloud FIELD (Source/CloudUnderlightField.cs, issue #88
// option 2), linked into this project via <Compile Include> so these exercise the exact code that
// ships. §23's own geometry is pinned next door in CloudUnderlightMathTests; this file is only about
// the spatial half — what gets drawn WHERE, and the mean subtraction that keeps this lane from
// re-rendering what §23's flat lane already does.
[TestFixture]
public class CloudUnderlightFieldTests
{
    private const float Tolerance = 1e-4f;
    private const int Seed = 1234;

    // A whole tile's worth of intensity, at the shipped resolution, for one cloud fraction.
    private static (float[] Intensity, float Mean) Bake(float cloudFraction, int seed = Seed)
    {
        int n = CloudUnderlightField.Resolution;
        float[] intensity = new float[n * n];
        float mean = CloudUnderlightField.FillIntensity(intensity, n, n, cloudFraction, seed);
        return (intensity, mean);
    }

    private static float MeanResidual(float[] intensity, float mean)
    {
        double sum = 0.0;
        for (int i = 0; i < intensity.Length; i++)
            sum += CloudUnderlightField.Residual(intensity[i], mean);

        return (float)(sum / intensity.Length);
    }

    // --- The headline claim: this lane draws structure, and only structure ---

    // Issue #88's mechanism is warm cloud against a COOL VAULT, i.e. a difference between two places.
    // Both skies without one — a cloudless sky and a solid overcast — are exactly the skies a single
    // flat colour describes perfectly, and both must therefore leave this lane silent and hand the
    // whole effect back to §23. The clear end is obvious; the OVERCAST end is the one worth pinning,
    // because "more cloud is more effect" is the intuition a later change would follow.
    [TestCase(0f, TestName = "NothingDrawnUnderAClearSky")]
    [TestCase(1f, TestName = "NothingDrawnUnderASolidOvercast")]
    public void AUniformSkyDrawsNoStructure(float cloudFraction)
    {
        (float[] intensity, float mean) = Bake(cloudFraction);

        for (int i = 0; i < intensity.Length; i++)
        {
            Assert.That(CloudUnderlightField.Residual(intensity[i], mean),
                Is.EqualTo(0f).Within(Tolerance), $"texel {i} of a uniform sky drew something");
        }
    }

    // And the middle is where it lives. Not asserted as an exact number — the peak's height depends on
    // the noise's own histogram — but as an ORDERING across the whole sweep, which is the shape the
    // subsystem claims: nothing at either end, most in the middle.
    [Test]
    public void StructurePeaksAtPartialCoverAndVanishesAtBothEnds()
    {
        float[] fractions = { 0f, 0.15f, 0.3f, 0.5f, 0.7f, 0.85f, 1f };
        float[] drawn = new float[fractions.Length];

        for (int i = 0; i < fractions.Length; i++)
        {
            (float[] intensity, float mean) = Bake(fractions[i]);
            drawn[i] = MeanResidual(intensity, mean);
        }

        Assert.That(drawn[0], Is.EqualTo(0f).Within(Tolerance), "a clear sky drew structure");
        Assert.That(drawn[^1], Is.EqualTo(0f).Within(Tolerance), "a solid overcast drew structure");

        int peak = 0;
        for (int i = 1; i < drawn.Length; i++)
        {
            if (drawn[i] > drawn[peak])
                peak = i;
        }

        Assert.That(fractions[peak], Is.EqualTo(0.5f).Within(0.21f),
            "the strongest structure should sit near half cover, not at an end");
        Assert.That(drawn[peak], Is.GreaterThan(0.05f),
            "half cover should draw something worth drawing");
    }

    // --- The partition with §23's flat lane ---

    // The residual is by definition what is left after the mean is taken out, so the mean of the
    // INTENSITY has to equal the mean this lane subtracts. Stated as a test rather than as a comment
    // because the two are computed in different places (FillIntensity returns it, Residual consumes
    // it) and nothing but this stops them drifting apart — which would silently double-count §23.
    [Test]
    public void TheSubtractedMeanIsTheFieldsOwnMean()
    {
        (float[] intensity, float mean) = Bake(0.4f);

        double sum = 0.0;
        for (int i = 0; i < intensity.Length; i++)
            sum += intensity[i];

        Assert.That(mean, Is.EqualTo((float)(sum / intensity.Length)).Within(Tolerance));
    }

    // Nothing this lane draws is ever negative, which is not a formality: an additive pass has no way
    // to remove light, so a below-average point must read as "leave it to §23" rather than wrapping
    // round into a byte that brightens it.
    [Test]
    public void ResidualNeverGoesNegative()
    {
        Assert.That(CloudUnderlightField.Residual(0.1f, 0.6f), Is.EqualTo(0f));
        Assert.That(CloudUnderlightField.Residual(0.6f, 0.6f), Is.EqualTo(0f));
        Assert.That(CloudUnderlightField.Residual(0.9f, 0.6f), Is.EqualTo(0.3f).Within(Tolerance));
    }

    // --- The field's own claim about itself ---

    // The cloud fraction this lane is handed means "this much of the sky is cloud", so the covered
    // AREA has to be that. It is exact by construction rather than by tuning — the threshold is read
    // off the tile's own histogram (see ThresholdFor) — and that is worth pinning on more than one
    // seed, because the fixed-threshold version this replaced passed on the seed it was tuned against
    // and gave 0.41 / 0.78 / 0.70 for the same requested 0.5 on three others.
    [TestCase(0.25f)]
    [TestCase(0.5f)]
    [TestCase(0.75f)]
    public void CoveredAreaTracksTheRequestedFraction(float cloudFraction)
    {
        foreach (int seed in new[] { 1234, 77, 9001 })
        {
            (float[] intensity, _) = Bake(cloudFraction, seed);

            double covered = 0.0;
            for (int i = 0; i < intensity.Length; i++)
                covered += intensity[i];

            Assert.That((float)(covered / intensity.Length), Is.EqualTo(cloudFraction).Within(0.03f),
                $"seed {seed}");
        }
    }

    // The two ends are handled explicitly inside ThresholdFor rather than being left to a quantile of
    // a discrete histogram, because "a uniform sky draws nothing" is a claim this subsystem makes and
    // a bin's worth of slop would quietly break it.
    [Test]
    public void ThresholdForPushesTheBandFullyOutsideTheFieldAtBothEnds()
    {
        float[] coverage = { 0f, 0.25f, 0.5f, 0.75f, 1f };

        Assert.That(CloudUnderlightField.PatchIntensity(
                1f, CloudUnderlightField.ThresholdFor(coverage, coverage.Length, 0f)),
            Is.EqualTo(0f).Within(Tolerance));
        Assert.That(CloudUnderlightField.PatchIntensity(
                0f, CloudUnderlightField.ThresholdFor(coverage, coverage.Length, 1f)),
            Is.EqualTo(1f).Within(Tolerance));
    }

    // Coverage rises monotonically with the fraction at every single point — a stronger statement than
    // "the area rises", and the one that rules out a threshold slide that shuffles WHICH parts of the
    // sky are cloudy as it goes. Clouds thickening should not make a patch move.
    [Test]
    public void EveryPointOnlyGetsCloudierAsTheFractionRises()
    {
        int n = CloudUnderlightField.Resolution;
        float[] lower = new float[n * n];
        float[] higher = new float[n * n];
        CloudUnderlightField.FillIntensity(lower, n, n, 0.35f, Seed);
        CloudUnderlightField.FillIntensity(higher, n, n, 0.55f, Seed);

        for (int i = 0; i < lower.Length; i++)
            Assert.That(higher[i], Is.GreaterThanOrEqualTo(lower[i] - Tolerance), $"texel {i} went the wrong way");
    }

    // Soft edges are epic #103's first-class requirement, and here they mean one thing measurable
    // offline: the field spends real area strictly between 0 and 1 rather than being a two-valued
    // stencil. A hard-edged version passes every test above and reads as a decal on the ground.
    [Test]
    public void PatchEdgesAreSoftRatherThanAStencil()
    {
        (float[] intensity, _) = Bake(0.5f);

        int partial = 0;
        for (int i = 0; i < intensity.Length; i++)
        {
            if (intensity[i] > 0.02f && intensity[i] < 0.98f)
                partial++;
        }

        Assert.That(partial / (float)intensity.Length, Is.GreaterThan(0.1f),
            "less than a tenth of the field is edge, so the patches are effectively hard-edged");
    }

    // --- Tileability, which the drift depends on ---

    // The whole drift mechanism is a UV pan over a repeating texture (see CloudUnderlightField's
    // header), so a field that does not wrap seamlessly shows a hard seam sweeping across the colony
    // once per cycle. AuroraNoise guarantees the wrap on its own lattice; this pins that §23b actually
    // samples it on lattice-aligned coordinates, which is the part a resolution change could break.
    [Test]
    public void TheFieldWrapsSeamlessly()
    {
        for (float v = 0f; v < 1f; v += 0.13f)
        {
            Assert.That(CloudUnderlightField.Coverage(1f, v, Seed),
                Is.EqualTo(CloudUnderlightField.Coverage(0f, v, Seed)).Within(Tolerance));
            Assert.That(CloudUnderlightField.Coverage(v, 1f, Seed),
                Is.EqualTo(CloudUnderlightField.Coverage(v, 0f, Seed)).Within(Tolerance));
        }
    }

    // Two colonies on one planet must not share a sky. Same reasoning as §22's tile-seeded cloud
    // fraction and §20c's aerosol drift, and the same cheap guarantee: the seed is the tile id.
    [Test]
    public void DifferentTilesGetDifferentFields()
    {
        int differing = 0;
        for (int i = 0; i < 64; i++)
        {
            float u = i / 64f;
            if (System.MathF.Abs(CloudUnderlightField.Coverage(u, 0.5f, 11)
                    - CloudUnderlightField.Coverage(u, 0.5f, 12)) > 0.01f)
                differing++;
        }

        Assert.That(differing, Is.GreaterThan(48));
    }

    // --- Drift ---

    // Reproducible from the absolute tick alone, which is what makes a harness screenshot of this
    // subsystem meaningful at all: an accumulated per-frame pan would put the field somewhere
    // different on every run of the same scenario.
    [Test]
    public void DriftIsAFunctionOfTheTickAndWrapsCleanly()
    {
        Assert.That(CloudUnderlightField.DriftOffsetU(0), Is.EqualTo(0f).Within(Tolerance));
        Assert.That(CloudUnderlightField.DriftOffsetU(CloudUnderlightField.DriftTileTicks),
            Is.EqualTo(0f).Within(Tolerance));
        Assert.That(CloudUnderlightField.DriftOffsetU(CloudUnderlightField.DriftTileTicks / 4),
            Is.EqualTo(0.25f).Within(Tolerance));

        // Both axes stay in [0, 1) at every tick across a full cycle, including the V axis whose
        // scaled phase would otherwise leave the unit square.
        for (int tick = 0; tick < CloudUnderlightField.DriftTileTicks * 3; tick += 37)
        {
            Assert.That(CloudUnderlightField.DriftOffsetU(tick), Is.InRange(0f, 1f));
            Assert.That(CloudUnderlightField.DriftOffsetV(tick), Is.InRange(0f, 1f));
        }
    }

    // --- The bytes handed to the texture ---

    [Test]
    public void WriteRgbaCarriesTheTintInRgbAndTheResidualInAlpha()
    {
        float[] intensity = { 0f, 0.5f, 1f };
        byte[] rgba = new byte[intensity.Length * 4];

        CloudUnderlightField.WriteRgba(rgba, intensity, intensity.Length, 0.25f, 1f, 0.5f, 0.25f);

        Assert.That(rgba[0], Is.EqualTo(255));
        Assert.That(rgba[1], Is.EqualTo(128));
        Assert.That(rgba[2], Is.EqualTo(64));

        // Alpha is the residual, floored at zero for the below-mean texel.
        Assert.That(rgba[3], Is.EqualTo(0));
        Assert.That(rgba[7], Is.EqualTo(64));
        Assert.That(rgba[11], Is.EqualTo(191));
    }

    // Every consumer of these bytes is a texture upload, and a NaN reaching one is a corrupt pixel
    // rather than an exception — the failure mode a malformed WeatherDef or a NaN tint would produce.
    [Test]
    public void WriteRgbaClampsNonsense()
    {
        float[] intensity = { float.NaN, 5f, -5f };
        byte[] rgba = new byte[intensity.Length * 4];

        CloudUnderlightField.WriteRgba(rgba, intensity, intensity.Length, 0f, float.NaN, 9f, -9f);

        Assert.That(rgba[0], Is.EqualTo(0));
        Assert.That(rgba[1], Is.EqualTo(255));
        Assert.That(rgba[2], Is.EqualTo(0));
        Assert.That(rgba[3], Is.EqualTo(0));
        Assert.That(rgba[7], Is.EqualTo(255));
        Assert.That(rgba[11], Is.EqualTo(0));
    }
}
