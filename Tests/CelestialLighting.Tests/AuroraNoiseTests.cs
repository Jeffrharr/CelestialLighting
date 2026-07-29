using CelestialLighting;
using NUnit.Framework;

namespace CelestialLighting.Tests;

// Offline coverage for §11a's noise primitive. The properties pinned here are not stylistic — each one
// is a specific visible artifact the curtain would show if it broke:
//
//   * tileability  -> a hard seam sweeping across the colony once per pan cycle,
//   * determinism  -> a tear along the boundary between rows baked in different frames,
//   * sign safety  -> a field mirrored about the origin once drift or warp goes negative,
//   * range        -> alphas outside [0,1] clipping to black or white bands.
[TestFixture]
public class AuroraNoiseTests
{
    private const float Tolerance = 1e-6f;

    // --- Tileability: the property the whole pan depends on ---

    [Test]
    public void Value_WrapsExactly_OnXPeriod()
    {
        // Sampling one whole period further along x must return the identical value, not a close one:
        // this is what makes the texture seamless under an arbitrary UV offset.
        for (int i = 0; i < 20; i++)
        {
            float x = i * 0.37f;
            float y = i * 0.11f;

            Assert.That(
                AuroraNoise.Value(x + 3f, y, 3, 7, 99),
                Is.EqualTo(AuroraNoise.Value(x, y, 3, 7, 99)).Within(Tolerance),
                $"x wrap broken at x={x}");
        }
    }

    [Test]
    public void Value_WrapsExactly_OnYPeriod()
    {
        for (int i = 0; i < 20; i++)
        {
            float x = i * 0.23f;
            float y = i * 0.41f;

            Assert.That(
                AuroraNoise.Value(x, y + 7f, 3, 7, 99),
                Is.EqualTo(AuroraNoise.Value(x, y, 3, 7, 99)).Within(Tolerance),
                $"y wrap broken at y={y}");
        }
    }

    [Test]
    public void Fbm_WrapsExactly_DespiteOctaveDoubling()
    {
        // The subtle one. Each octave samples at 2x the coordinate, so it only stays tile-periodic
        // because its lattice period doubles too. If someone "simplifies" Fbm by holding the period
        // fixed across octaves, this is the test that catches it.
        for (int i = 0; i < 20; i++)
        {
            float x = i * 0.29f;
            float y = i * 0.17f;

            Assert.That(
                AuroraNoise.Fbm(x + 3f, y + 7f, 3, 7, 1234, octaves: 3),
                Is.EqualTo(AuroraNoise.Fbm(x, y, 3, 7, 1234, octaves: 3)).Within(Tolerance),
                $"fBm wrap broken at ({x}, {y})");
        }
    }

    // --- Determinism: the row-slicing contract ---

    [Test]
    public void Value_IsDeterministic_RegardlessOfCallOrder()
    {
        // AuroraCurtain bakes a few rows per frame, so two rows generated seconds apart must agree
        // about the lattice points they share. Any hidden state in the hash would break that as a
        // visible tear.
        float first = AuroraNoise.Value(1.234f, 5.678f, 3, 7, 42);

        for (int i = 0; i < 50; i++)
            AuroraNoise.Value(i * 0.9f, i * 1.3f, 5, 11, i);

        Assert.That(AuroraNoise.Value(1.234f, 5.678f, 3, 7, 42), Is.EqualTo(first).Within(0f));
    }

    // --- Range ---

    [TestCase(3, 7, 3)]
    [TestCase(2, 2, 2)]
    [TestCase(1, 1, 1)]
    public void Fbm_StaysWithinUnitRange(int xPeriod, int yPeriod, int octaves)
    {
        for (int i = 0; i < 400; i++)
        {
            float x = i * 0.13f;
            float y = i * 0.07f;
            float v = AuroraNoise.Fbm(x, y, xPeriod, yPeriod, 7, octaves);

            Assert.That(v, Is.GreaterThanOrEqualTo(0f).And.LessThanOrEqualTo(1f),
                $"fBm out of range at ({x}, {y}) = {v}");
        }
    }

    // --- Sign safety ---

    [Test]
    public void Value_DoesNotMirrorAcrossTheOrigin()
    {
        // A truncating cast would put -0.3 and +0.3 in the same lattice cell, reflecting the field about
        // x=0. Negative coordinates reach here from the drift and warp offsets, so this matters.
        // The check is that -0.3 agrees with its WRAPPED equivalent (period - 0.3), not with +0.3.
        float negative = AuroraNoise.Value(-0.3f, 0.5f, 3, 7, 5);

        Assert.That(negative, Is.EqualTo(AuroraNoise.Value(2.7f, 0.5f, 3, 7, 5)).Within(Tolerance));
        Assert.That(negative, Is.Not.EqualTo(AuroraNoise.Value(0.3f, 0.5f, 3, 7, 5)).Within(1e-4f));
    }

    // --- Degenerate inputs ---

    [Test]
    public void Value_TreatsNonPositivePeriodAsOne()
    {
        // A period of 1 collapses the lattice to a single point, so the field is constant rather than
        // an index-out-of-range. Guarding rather than throwing keeps a mistuned constant from taking
        // the whole render down.
        Assert.That(AuroraNoise.Value(0.4f, 0.6f, 0, 0, 3),
            Is.EqualTo(AuroraNoise.Value(1.4f, 1.6f, 1, 1, 3)).Within(Tolerance));
    }

    [Test]
    public void Fbm_TreatsNonPositiveOctavesAsOne()
    {
        Assert.That(AuroraNoise.Fbm(0.4f, 0.6f, 3, 7, 3, octaves: 0),
            Is.EqualTo(AuroraNoise.Fbm(0.4f, 0.6f, 3, 7, 3, octaves: 1)).Within(Tolerance));
    }

    // --- Field quality ---

    [Test]
    public void Value_ActuallyVaries_AndIsNotDegenerate()
    {
        // Cheap smoke test against a hash that avalanches badly enough to return near-constant values,
        // which would render as an empty sky rather than as an obvious bug.
        float min = 1f;
        float max = 0f;

        for (int i = 0; i < 500; i++)
        {
            float v = AuroraNoise.Value(i * 0.31f, i * 0.19f, 5, 11, 8);
            min = v < min ? v : min;
            max = v > max ? v : max;
        }

        Assert.That(max - min, Is.GreaterThan(0.5f), "field spans too little of [0,1] to read as noise");
    }

    [Test]
    public void Fbm_IsSmootherThanItsBaseOctave()
    {
        // 1/f weighting means added octaves must perturb the field, not dominate it. Comparing
        // mean absolute step between neighbouring samples pins that the sum stays dominated by its
        // coarsest layer — which is what makes the ribbons large-scale features.
        float oneOctave = MeanAbsoluteStep(octaves: 1);
        float threeOctaves = MeanAbsoluteStep(octaves: 3);

        Assert.That(threeOctaves, Is.GreaterThan(oneOctave),
            "extra octaves should add fine detail");
        Assert.That(threeOctaves, Is.LessThan(oneOctave * 3f),
            "extra octaves should perturb the base layer, not overwhelm it");
    }

    private static float MeanAbsoluteStep(int octaves)
    {
        const int samples = 400;
        const float step = 0.02f;
        float total = 0f;

        for (int i = 0; i < samples; i++)
        {
            float a = AuroraNoise.Fbm(i * step, 1.5f, 3, 7, 55, octaves);
            float b = AuroraNoise.Fbm((i + 1) * step, 1.5f, 3, 7, 55, octaves);
            total += a > b ? a - b : b - a;
        }

        return total / samples;
    }
}
