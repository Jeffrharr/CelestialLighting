using System;
using CelestialLighting;
using NUnit.Framework;

namespace CelestialLighting.Tests;

// Offline coverage for §22's hourly cloud-cover wobble. Mirrors AerosolDriftTests.cs's shape — same
// engine underneath — but the properties worth pinning are different because the composition and the
// correlation time are deliberately different from AerosolDrift's:
//
//   * amplitude 0 is exactly the seasonal mean -> the noise term must be provably removable, same
//                                                 promise AerosolDrift makes about §20b,
//   * mean over a long sample is the wetFraction -> otherwise a "40% cloudy" biome silently reads as
//                                                    more or less cloudy than SeasonalWetFraction says,
//   * always in [0, 1], whatever the inputs      -> this is a fraction rendered straight into a UI
//                                                    label and a sky colour lerp; NaN or out-of-range
//                                                    output is not a subtle bug, it is a broken frame,
//   * the fastest octave sits INSIDE a day       -> the opposite structural guard from AerosolDrift's
//                                                    (which keeps its fastest octave outside a day),
//                                                    because clouds are supposed to move within one,
//   * deterministic, tile-independent            -> same reload/adjacent-colony guarantees as §20c.
[TestFixture]
public class CloudCoverDriftTests
{
    // Same rationale as AerosolDriftTests.Seeds: awkward rather than tidy tile ids.
    private static readonly int[] Seeds = { 0, 1, 7, 12345, -998, 4242424 };

    private static readonly float[] WetFractions = { 0f, 0.25f, 0.5f, 0.75f, 1f };

    // --- Regression pin: switched off, the result is exactly the seasonal mean ---

    [Test]
    public void AmplitudeZero_IsExactlyTheWetFraction_NotMerelyCloseToIt()
    {
        // Exact equality, deliberately, and for the same reason as AerosolDrift's own version of this
        // test: a "40% cloudy" tile silently reading as 41% the moment this file's noise is disabled
        // would be a real regression with nothing on screen to catch it. Unlike AerosolDrift this file
        // has no explicit early-return for amplitude 0 — multiplying the noise term by an exact 0f is
        // exact in IEEE754 regardless of what Field() returns, so the identity falls out of the
        // arithmetic rather than needing a branch to guarantee it.
        foreach (float wetFraction in WetFractions)
        {
            for (int sampleIndex = 0; sampleIndex < 3000; sampleIndex++)
            {
                foreach (int seed in Seeds)
                {
                    Assert.That(CloudCoverDrift.FractionWithAmplitude(sampleIndex, seed, wetFraction, 0f),
                        Is.EqualTo(wetFraction),
                        $"amplitude 0 must be exactly wetFraction (sample {sampleIndex}, seed {seed}, wetFraction {wetFraction})");
                }
            }
        }
    }

    [TestCase(-0.5f)]
    [TestCase(-1e9f)]
    [TestCase(float.NaN)]
    public void NonsenseAmplitudes_CollapseToTheWetFractionAlone(float amplitude)
    {
        // Same reasoning as AerosolDrift's own NonsenseAmplitudes test: a negative amplitude would be
        // indistinguishable from a positive one (the field is symmetric) and would silently hide a
        // sign error upstream; NaN is the one that matters, since it would otherwise flow straight
        // into a sky colour or a UI percentage.
        foreach (int seed in Seeds)
            Assert.That(CloudCoverDrift.FractionWithAmplitude(1234, seed, 0.4f, amplitude), Is.EqualTo(0.4f));
    }

    // --- Bounds: no configuration can produce a value outside [0, 1] or a NaN ---

    [TestCase(0f)]
    [TestCase(0.05f)]
    [TestCase(CloudCoverDrift.WobbleAmplitude)]
    [TestCase(CloudCoverDrift.MaxWobbleAmplitude)]
    [TestCase(1f)]
    [TestCase(5f)]
    [TestCase(1e9f)]
    [TestCase(float.PositiveInfinity)]
    public void NoAmplitude_CanPushTheFractionOutsideTheUnitInterval(float amplitude)
    {
        foreach (int seed in Seeds)
        {
            for (int step = 0; step <= 20; step++)
            {
                float wetFraction = step / 20f;
                for (int sampleIndex = 0; sampleIndex < 500; sampleIndex++)
                {
                    float fraction = CloudCoverDrift.FractionWithAmplitude(sampleIndex, seed, wetFraction, amplitude);
                    Assert.That(fraction, Is.InRange(0f, 1f),
                        $"amplitude {amplitude} produced {fraction} at sample {sampleIndex}, wetFraction {wetFraction}");
                }
            }
        }
    }

    [TestCase(-1f)]
    [TestCase(2f)]
    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(float.NegativeInfinity)]
    public void NonsenseWetFractions_StillProduceAnInRangeResult(float wetFraction)
    {
        // SeasonalWetFraction.Fraction is contractually [0, 1], but this file's own contract has to
        // hold even if a future caller (or a modded classifier) hands it something outside that —
        // the same defensive posture AerosolDrift takes toward its amplitude rather than toward its
        // baseline, because here the "baseline" is a per-call argument, not a compile-time constant.
        foreach (int seed in Seeds)
        {
            for (int sampleIndex = 0; sampleIndex < 500; sampleIndex++)
            {
                float fraction = CloudCoverDrift.Fraction(sampleIndex, seed, wetFraction);
                Assert.That(fraction, Is.InRange(0f, 1f).Or.EqualTo(0f),
                    $"wetFraction {wetFraction} produced {fraction} at sample {sampleIndex}");
            }
        }
    }

    // --- Baseline preservation: the long-run mean must equal wetFraction ---

    [Test]
    public void MeanOverAFullPeriodEqualsTheWetFraction()
    {
        // Swept over the entire lattice period rather than a sample of it, same rationale as
        // AerosolDrift.MeanOverAFullPeriodIsOne: a partial sweep of a correlated signal has sampling
        // error large enough to hide a real bias in the composition.
        foreach (float wetFraction in WetFractions)
        {
            double sum = 0;
            long count = 0;

            foreach (int seed in Seeds)
            {
                for (int sampleIndex = 0; sampleIndex < CloudCoverDrift.SamplesPerPeriod; sampleIndex++)
                {
                    sum += CloudCoverDrift.Fraction(sampleIndex, seed, wetFraction);
                    count++;
                }
            }

            double mean = sum / count;

            // Wider tolerance at the extremes (0 and 1) than AerosolDrift uses, because clamp01 is
            // asymmetric there by construction: a wetFraction of 0 can only wobble upward, so its
            // long-run mean is pulled above 0 by exactly the amount the lower half of the noise gets
            // clipped, and symmetrically for a wetFraction of 1. That skew is a property of the model
            // (see CloudCoverDrift.cs's header on why the clamp is asymmetric at wetFraction 0 the same
            // way AerosolDrift.ApplyMultiplier's clamp is asymmetric at baseline 1), not a bug.
            double tolerance = (wetFraction <= 0f || wetFraction >= 1f) ? CloudCoverDrift.WobbleAmplitude / 2.0 : 0.01;

            Assert.That(mean, Is.EqualTo(wetFraction).Within(tolerance),
                $"wetFraction {wetFraction}: long-run mean is {mean:F6}, so the shipped fraction has "
                + "drifted away from what SeasonalWetFraction actually estimated");
        }
    }

    // --- The correlation time is hours, not days: the opposite structural guard from AerosolDrift ---

    [Test]
    public void TheFastestOctaveSitsInsideASingleDay()
    {
        // The mirror image of AerosolDrift.TheFastestOctaveStillSpansMoreThanADay. That test guards
        // against a component of the aerosol drift completing inside one evening, because that would
        // read as flicker; this one guards the opposite direction, because a cloud-cover field with
        // every component OUTSIDE a day would never produce the "changed since this morning" character
        // the design asks for, and would be indistinguishable from SeasonalWetFraction alone.
        float fastestOctaveHours = CloudCoverDrift.LatticeCellHours / (1 << (CloudCoverDrift.Octaves - 1));

        Assert.That(fastestOctaveHours, Is.LessThan(24f),
            "an octave whose cell spans a full day or more means nothing in the field ever changes within one");
        Assert.That(CloudCoverDrift.LatticeCellHours, Is.EqualTo(8),
            "the base cell width is a design decision (DESIGN.md §22), not free to drift silently");
    }

    [Test]
    public void OneSampleCoversExactlyOneInGameHour()
    {
        // Same cadence contract as AerosolDrift.OneSampleCoversExactlyOneInGameHour, pinned separately
        // here because CloudCoverDrift.SampleIndex is its own forwarding entry point — see
        // LatticeDriftNoise.cs's header on why each consumer pins the shared engine independently.
        Assert.That(CloudCoverDrift.SampleIndex(0), Is.EqualTo(0));
        Assert.That(CloudCoverDrift.SampleIndex(LatticeDriftNoise.TicksPerSample - 1), Is.EqualTo(0));
        Assert.That(CloudCoverDrift.SampleIndex(LatticeDriftNoise.TicksPerSample), Is.EqualTo(1));
        Assert.That(CloudCoverDrift.SampleIndex(60000), Is.EqualTo(LatticeDriftNoise.SamplesPerDay));
    }

    [Test]
    public void SampleIndexFloorsRatherThanTruncatingTowardZero()
    {
        Assert.That(CloudCoverDrift.SampleIndex(-1), Is.EqualTo(-1));
        Assert.That(CloudCoverDrift.SampleIndex(-LatticeDriftNoise.TicksPerSample), Is.EqualTo(-1));
        Assert.That(CloudCoverDrift.SampleIndex(-LatticeDriftNoise.TicksPerSample - 1), Is.EqualTo(-2));
    }

    // --- Determinism and seeding ---

    [Test]
    public void TheSequenceIsAPureFunctionOfSampleAndSeed()
    {
        // Same order-independence pin as AerosolDrift.TheSequenceIsAPureFunctionOfSampleAndSeed, for
        // the same save/load reason: the live adapter derives both inputs from values RimWorld itself
        // persists (the tile id and the absolute tick), so "no hidden state" is the whole contract.
        float[] forward = new float[2000];
        for (int sampleIndex = 0; sampleIndex < forward.Length; sampleIndex++)
            forward[sampleIndex] = CloudCoverDrift.Fraction(sampleIndex, 12345, 0.4f);

        for (int sampleIndex = forward.Length - 1; sampleIndex >= 0; sampleIndex--)
        {
            CloudCoverDrift.Fraction(sampleIndex, 999, 0.6f);
            Assert.That(CloudCoverDrift.Fraction(sampleIndex, 12345, 0.4f), Is.EqualTo(forward[sampleIndex]),
                $"sample {sampleIndex} depends on what was evaluated before it");
        }
    }

    [Test]
    public void DifferentTilesGetIndependentClouds()
    {
        double meanDifference = 0;
        int count = 0;

        for (int sampleIndex = 0; sampleIndex < 20000; sampleIndex++)
        {
            meanDifference += Math.Abs(CloudCoverDrift.Fraction(sampleIndex, 1, 0.5f) - CloudCoverDrift.Fraction(sampleIndex, 2, 0.5f));
            count++;
        }

        meanDifference /= count;
        Assert.That(meanDifference, Is.GreaterThan(0.02),
            $"adjacent tile ids produce nearly the same cloud cover (mean difference {meanDifference:F5})");
    }

    [Test]
    public void TheSequenceWrapsAtItsStatedPeriod()
    {
        foreach (int seed in Seeds)
        {
            for (int sampleIndex = 0; sampleIndex < 300; sampleIndex++)
            {
                Assert.That(CloudCoverDrift.Fraction(sampleIndex + CloudCoverDrift.SamplesPerPeriod, seed, 0.5f),
                    Is.EqualTo(CloudCoverDrift.Fraction(sampleIndex, seed, 0.5f)).Within(1e-6f));
            }
        }
    }
}
