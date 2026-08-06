using System;
using CelestialLighting;
using NUnit.Framework;

namespace CelestialLighting.Tests;

// Offline coverage for §20c's day-to-day aerosol drift. Every property pinned here is a specific
// failure the live game would show, and most of them are failures with no exception and nothing that
// looks wrong on any single screenshot:
//
//   * amplitude 0 is exactly 1   -> §20b's shipped behaviour has to be reproducible bit for bit,
//   * mean 1 over a long sample  -> otherwise every polluted map silently gets hazier (or cleaner)
//                                   than the physics of §20b says it should be, forever,
//   * strictly positive, bounded -> a negative optical depth is a sky lerped past its own endpoint,
//   * evening << day             -> THE failure mode: haze that wobbles inside one evening reads as
//                                   flicker, not weather, and fights §8's smooth elevation ramp,
//   * deterministic on the seed  -> a colony reloaded must get the evening it had before the reload.
[TestFixture]
public class AerosolDriftTests
{
    // Tiles chosen to be awkward rather than tidy: zero, adjacent small ids (worldgen numbers tiles
    // sequentially, so neighbouring colonies really do differ by 1 and must not share weather), a
    // negative (nothing produces one today, but the seed is an int and the hash must not care) and a
    // large one from the middle of a real world's tile range.
    private static readonly int[] Seeds = { 0, 1, 7, 12345, -998, 4242424 };

    // --- Regression pin: switched off, §20b is untouched ---

    [Test]
    public void AmplitudeZero_IsExactlyOne_NotMerelyCloseToIt()
    {
        // Exact equality, deliberately. §20b shipped as a fixed function of the tile, and "almost
        // unchanged" would mean the mod's colour output had moved for every existing polluted colony
        // the moment this subsystem landed. An early return is the only way to promise bit-identity
        // without depending on the noise field's own arithmetic happening to land somewhere that
        // multiplies out cleanly.
        for (int sampleIndex = 0; sampleIndex < 5000; sampleIndex++)
        {
            foreach (int seed in Seeds)
            {
                Assert.That(AerosolDrift.MultiplierWithAmplitude(sampleIndex, seed, 0f), Is.EqualTo(1f),
                    $"amplitude 0 must be exactly 1 (sample {sampleIndex}, seed {seed})");
            }
        }
    }

    [TestCase(-0.5f)]
    [TestCase(-1e9f)]
    [TestCase(float.NaN)]
    public void NonsenseAmplitudes_CollapseToTheDisabledCase(float amplitude)
    {
        // A negative amplitude is not "the drift running backwards" — the field is symmetric, so it
        // would be indistinguishable from a positive one and would silently hide a sign error
        // upstream. NaN is the one that matters most: it would propagate through the multiply into
        // the aerosol fraction and out into a NaN sky colour, which Unity renders as black.
        foreach (int seed in Seeds)
            Assert.That(AerosolDrift.MultiplierWithAmplitude(1234, seed, amplitude), Is.EqualTo(1f));
    }

    [Test]
    public void MultiplierOfOne_LeavesTheBaselineColumnBitIdentical()
    {
        // The other half of the regression pin. ApplyMultiplier is what the live boundary calls, so
        // it is what has to be transparent when the drift is off — a clamp that rounded here would
        // move §20b's output even with the noise disabled.
        for (int step = 0; step <= 1000; step++)
        {
            float baseline = step / 1000f;
            Assert.That(AerosolDrift.ApplyMultiplier(baseline, 1f), Is.EqualTo(baseline));
        }
    }

    // --- Baseline preservation: the mean must not drift ---

    [Test]
    public void MeanOverAFullPeriodIsOne()
    {
        // Swept over the ENTIRE sequence rather than a sample of it, for every seed, because a
        // partial sweep of a correlated signal has a sampling error large enough to hide a real bias.
        // 294912 samples per seed x 6 seeds is ~1.8M evaluations and runs in well under a second.
        //
        // This is asserted on the MULTIPLIER, not on the driven aerosol fraction, and that is the
        // honest place for it: ApplyMultiplier clamps at 1, so a maximally polluted sea-level tile
        // genuinely does average below its baseline. That clamp is a property of where the tile sits
        // relative to the model's ceiling, not a drift in the model — see ApplyMultiplier's header.
        double sum = 0;
        long count = 0;

        foreach (int seed in Seeds)
        {
            for (int sampleIndex = 0; sampleIndex < AerosolDrift.SamplesPerPeriod; sampleIndex++)
            {
                sum += AerosolDrift.Multiplier(sampleIndex, seed);
                count++;
            }
        }

        double mean = sum / count;
        Assert.That(mean, Is.EqualTo(1.0).Within(0.005),
            $"the drift multiplier's long-run mean is {mean:F6}, so every polluted map's aerosol "
            + "column has silently moved away from the §20b baseline it is supposed to wander around");
    }

    [Test]
    public void MeanIsOneForEverySeedIndividually_NotOnlyAveragedOverSeeds()
    {
        // A per-seed assertion as well as the pooled one above, because a single map is what a player
        // experiences. Pooling could mask one tile that is permanently hazy against another that is
        // permanently clean. The per-seed tolerance is looser (one seed is a sixth of the sample) but
        // still an order of magnitude tighter than the +-0.35 excursion it is guarding.
        foreach (int seed in Seeds)
        {
            double sum = 0;
            for (int sampleIndex = 0; sampleIndex < AerosolDrift.SamplesPerPeriod; sampleIndex++)
                sum += AerosolDrift.Multiplier(sampleIndex, seed);

            Assert.That(sum / AerosolDrift.SamplesPerPeriod, Is.EqualTo(1.0).Within(0.01),
                $"seed {seed} sits off the §20b baseline over its whole sequence");
        }
    }

    // --- Bounds: no configuration can produce a negative or absurd optical depth ---

    [TestCase(0f)]
    [TestCase(0.05f)]
    [TestCase(AerosolDrift.DriftAmplitude)]
    [TestCase(AerosolDrift.MaxDriftAmplitude)]
    [TestCase(1f)]
    [TestCase(5f)]
    [TestCase(1e9f)]
    [TestCase(float.PositiveInfinity)]
    public void NoAmplitude_CanPushTheMultiplierToZeroOrBelow(float amplitude)
    {
        // The invariant is "no CONFIGURATION of the noise", not "not the amplitude we ship" — so the
        // sweep deliberately includes values a settings slider or a dev override could plausibly hand
        // in, and values nothing should ever hand in.
        foreach (int seed in Seeds)
        {
            for (int sampleIndex = 0; sampleIndex < 20000; sampleIndex++)
            {
                float multiplier = AerosolDrift.MultiplierWithAmplitude(sampleIndex, seed, amplitude);

                Assert.That(multiplier, Is.GreaterThan(0f),
                    $"amplitude {amplitude} produced a non-positive column multiplier at sample {sampleIndex}");
                Assert.That(multiplier, Is.InRange(1f - AerosolDrift.MaxDriftAmplitude, 1f + AerosolDrift.MaxDriftAmplitude));
            }
        }
    }

    [Test]
    public void TheAmplitudeCeilingIsBelowOne_WhichIsWhatMakesPositivityStructural()
    {
        // Structural, not numeric. The multiplier is 1 + a * u with u in [-1, 1], so positivity is a
        // consequence of a < 1 rather than of any clamp downstream. If someone raises the ceiling to
        // 1 or beyond, the test above would still pass for the particular samples it happens to
        // visit; this one fails immediately and says why.
        Assert.That(AerosolDrift.MaxDriftAmplitude, Is.LessThan(1f),
            "an amplitude ceiling of 1 or more allows a zero or negative aerosol column");
        Assert.That(AerosolDrift.DriftAmplitude, Is.LessThanOrEqualTo(AerosolDrift.MaxDriftAmplitude));
    }

    [Test]
    public void TheShippedAmplitudeStaysInsideItsNominalBand()
    {
        // The measured extremes over the full period, pinned so a retune of the cell width, the
        // octave count or the noise itself cannot quietly widen the range the amplitude names. Value
        // noise does not reach its own extrema often, so the observed band sits just inside the
        // nominal one rather than on it.
        float min = float.MaxValue;
        float max = float.MinValue;

        foreach (int seed in Seeds)
        {
            for (int sampleIndex = 0; sampleIndex < AerosolDrift.SamplesPerPeriod; sampleIndex++)
            {
                float multiplier = AerosolDrift.Multiplier(sampleIndex, seed);
                min = Math.Min(min, multiplier);
                max = Math.Max(max, multiplier);
            }
        }

        Assert.That(min, Is.GreaterThanOrEqualTo(1f - AerosolDrift.DriftAmplitude));
        Assert.That(max, Is.LessThanOrEqualTo(1f + AerosolDrift.DriftAmplitude));
        Assert.That(min, Is.LessThan(0.7f), "the drift never gets near its clean end — amplitude is not being applied");
        Assert.That(max, Is.GreaterThan(1.3f), "the drift never gets near its loaded end — amplitude is not being applied");
    }

    [Test]
    public void TheDrivenColumnStaysInsideTheUnitInterval()
    {
        // Every consumer of an aerosol fraction — SkyColorTemperature.HorizonKelvinForColumns above
        // all — is written against [0, 1], and a fraction above 1 would EXTRAPOLATE the lerp past
        // AerosolHorizonKelvin instead of interpolating to it. That is the "runs off the end of the
        // world" failure §20b's own design notes argue against, so it is closed off here at the
        // boundary rather than left to the curve's defensive clamp.
        foreach (int seed in Seeds)
        {
            for (int step = 0; step <= 20; step++)
            {
                float baseline = step / 20f;
                for (int sampleIndex = 0; sampleIndex < 500; sampleIndex++)
                {
                    float driven = AerosolDrift.ApplyMultiplier(baseline, AerosolDrift.Multiplier(sampleIndex, seed));
                    Assert.That(driven, Is.InRange(0f, 1f));
                }
            }
        }
    }

    [Test]
    public void AnUnpollutedTileStaysExactlyUnpolluted()
    {
        // The single most load-bearing consequence of making this multiplicative rather than additive:
        // every tile in a game without Biotech has a baseline of exactly 0, and multiplying 0 by
        // anything is 0. So §20c is provably inert wherever §20b was, with no gate anywhere — the
        // same shape as §20b needing no ModsConfig.BiotechActive check.
        foreach (int seed in Seeds)
        {
            for (int sampleIndex = 0; sampleIndex < 5000; sampleIndex++)
                Assert.That(AerosolDrift.ApplyMultiplier(0f, AerosolDrift.Multiplier(sampleIndex, seed)), Is.EqualTo(0f));
        }
    }

    // --- Weather, not flicker: the correlation time is days ---

    [Test]
    public void ChangeAcrossADayIsAnOrderOfMagnitudeLargerThanChangeWithinAnHour()
    {
        // THE property this subsystem is really about. Stated as a ratio of mean absolute changes
        // rather than pair by pair, because a single pair can and does violate it near a local
        // extremum of the field — the claim is about the character of the signal, not about every
        // sample of it. The per-sample form of the same claim is the next test.
        //
        // Measured ratio at the shipped constants is ~22x.
        double hourly = MeanAbsoluteChange(lagInSamples: 1);
        double daily = MeanAbsoluteChange(lagInSamples: AerosolDrift.SamplesPerDay);

        Assert.That(daily / hourly, Is.GreaterThan(10.0),
            $"an hour of drift ({hourly:F5}) is not much smaller than a day of it ({daily:F5}) — the "
            + "column is wobbling inside a single evening, which reads as flicker rather than weather");
    }

    [Test]
    public void NoSingleHourlyStepIsAsLargeAsATypicalDayToDayChange()
    {
        // The deterministic companion to the ratio above: the WORST hour anywhere in the whole
        // sequence still moves the column less than an average day does. That is what makes "the sky
        // does not visibly jump between two consecutive hours" a guarantee rather than a tendency.
        //
        // Measured: worst hourly step 0.018 against a mean daily change of 0.072, so this passes with
        // a factor of four in hand. It is the assertion that fails first if an octave is added, since
        // a third octave would put a component on an 18-hour cell.
        float worstHour = 0f;
        foreach (int seed in Seeds)
        {
            for (int sampleIndex = 0; sampleIndex < AerosolDrift.SamplesPerPeriod; sampleIndex++)
            {
                float step = Math.Abs(AerosolDrift.Multiplier(sampleIndex + 1, seed) - AerosolDrift.Multiplier(sampleIndex, seed));
                worstHour = Math.Max(worstHour, step);
            }
        }

        double meanDaily = MeanAbsoluteChange(lagInSamples: AerosolDrift.SamplesPerDay);
        Assert.That(worstHour, Is.LessThan(meanDaily),
            $"the worst hourly step ({worstHour:F5}) is at least as big as a typical day's change "
            + $"({meanDaily:F5}) — successive samples within one evening are no longer smaller than "
            + "successive samples across days");
    }

    [Test]
    public void AWholeEveningMovesLessThanASingleDayDoes()
    {
        // Four hours is about as long as a sunset watch lasts, and §8's ramp is smooth across it by
        // construction. If the haze moved as much over that window as it does between one night and
        // the next, the two effects would be fighting: the ramp would be smoothly warming while its
        // own endpoint slid underneath it.
        double evening = MeanAbsoluteChange(lagInSamples: 4);
        double daily = MeanAbsoluteChange(lagInSamples: AerosolDrift.SamplesPerDay);

        Assert.That(daily / evening, Is.GreaterThan(3.0),
            $"an evening of drift ({evening:F5}) is comparable to a day of it ({daily:F5})");
    }

    [Test]
    public void TheFastestOctaveStillSpansMoreThanADay()
    {
        // Structural guard on the two constants that set the correlation time, asserted because the
        // statistical tests above measure the CONSEQUENCE and this measures the CAUSE. Each octave
        // halves the cell width, so the fastest layer sits at LatticeCellDays / 2^(Octaves-1). Below
        // a day, some component of the field completes a full excursion inside one night.
        float fastestOctaveDays = AerosolDrift.LatticeCellDays / (1 << (AerosolDrift.Octaves - 1));

        Assert.That(fastestOctaveDays, Is.GreaterThanOrEqualTo(1f),
            "an octave whose cell is under a day puts a component of the drift inside a single evening");
        Assert.That(AerosolDrift.LatticeCellDays, Is.GreaterThanOrEqualTo(2f),
            "the base correlation time is supposed to be a couple of days — air masses persist");
    }

    // --- Determinism and seeding ---

    [Test]
    public void TheSequenceIsAPureFunctionOfSampleAndSeed()
    {
        // "Same seed, same sequence, across save/load" reduces offline to "no hidden state", because
        // the live adapter derives both inputs from values RimWorld itself saves (the tile id and the
        // absolute tick). So the pin is order-independence: walking the sequence backwards, and
        // interleaving two seeds, must reproduce the forward single-seed walk exactly.
        float[] forward = new float[2000];
        for (int sampleIndex = 0; sampleIndex < forward.Length; sampleIndex++)
            forward[sampleIndex] = AerosolDrift.Multiplier(sampleIndex, 12345);

        for (int sampleIndex = forward.Length - 1; sampleIndex >= 0; sampleIndex--)
        {
            AerosolDrift.Multiplier(sampleIndex, 999);
            Assert.That(AerosolDrift.Multiplier(sampleIndex, 12345), Is.EqualTo(forward[sampleIndex]),
                $"sample {sampleIndex} depends on what was evaluated before it");
        }
    }

    [Test]
    public void DifferentTilesGetIndependentWeather()
    {
        // Two colonies on one planet should not share a sky. Worldgen numbers tiles sequentially, so
        // the adjacent-id case is the one that matters and the one a weak hash would fail: AuroraNoise
        // avalanches the seed precisely so seeds 1 and 2 do not produce visibly related fields.
        double meanDifference = 0;
        int count = 0;

        for (int sampleIndex = 0; sampleIndex < 20000; sampleIndex++)
        {
            meanDifference += Math.Abs(AerosolDrift.Multiplier(sampleIndex, 1) - AerosolDrift.Multiplier(sampleIndex, 2));
            count++;
        }

        meanDifference /= count;
        Assert.That(meanDifference, Is.GreaterThan(0.1),
            $"adjacent tile ids produce nearly the same weather (mean difference {meanDifference:F5})");
    }

    [Test]
    public void TheSequenceWrapsAtItsStatedPeriod()
    {
        // AuroraNoise is a tiling generator, so a period is not optional — it is a number we pick, and
        // the wrap is what lets Field() reduce an unbounded tick count to a small coordinate without
        // losing mantissa bits. Pinned so the integer wrap in Field and the period the octaves are
        // given cannot drift apart, which would show up as a discontinuity once every 12288 days and
        // never in testing.
        foreach (int seed in Seeds)
        {
            for (int sampleIndex = 0; sampleIndex < 300; sampleIndex++)
            {
                Assert.That(AerosolDrift.Multiplier(sampleIndex + AerosolDrift.SamplesPerPeriod, seed),
                    Is.EqualTo(AerosolDrift.Multiplier(sampleIndex, seed)).Within(1e-6f));
                Assert.That(AerosolDrift.Multiplier(sampleIndex - AerosolDrift.SamplesPerPeriod, seed),
                    Is.EqualTo(AerosolDrift.Multiplier(sampleIndex, seed)).Within(1e-6f));
            }
        }
    }

    // --- The cadence itself ---

    [Test]
    public void OneSampleCoversExactlyOneInGameHour()
    {
        // The bucket boundary is what makes the memo in AerosolDriftClock correct: it recomputes when
        // and only when this index moves. An off-by-one here would either recompute every tick (the
        // performance regression the memo exists to prevent) or hold a value for two hours.
        Assert.That(AerosolDrift.SampleIndex(0), Is.EqualTo(0));
        Assert.That(AerosolDrift.SampleIndex(AerosolDrift.TicksPerSample - 1), Is.EqualTo(0));
        Assert.That(AerosolDrift.SampleIndex(AerosolDrift.TicksPerSample), Is.EqualTo(1));
        Assert.That(AerosolDrift.SampleIndex(60000), Is.EqualTo(AerosolDrift.SamplesPerDay),
            "a RimWorld day of 60000 ticks must be exactly SamplesPerDay buckets");
    }

    [Test]
    public void SampleIndexFloorsRatherThanTruncatingTowardZero()
    {
        // Nothing in a real game hands this a negative absolute tick, but dev tooling and tests do,
        // and C#'s / truncates toward zero — which would fold the bucket either side of tick 0 into
        // one double-width bucket and put a visible discontinuity in the sequence at exactly the point
        // a test is most likely to sample.
        Assert.That(AerosolDrift.SampleIndex(-1), Is.EqualTo(-1));
        Assert.That(AerosolDrift.SampleIndex(-AerosolDrift.TicksPerSample), Is.EqualTo(-1));
        Assert.That(AerosolDrift.SampleIndex(-AerosolDrift.TicksPerSample - 1), Is.EqualTo(-2));
    }

    [Test]
    public void SampleIndexIsMonotonicAcrossTheZeroCrossing()
    {
        int previous = AerosolDrift.SampleIndex(-100000);
        for (int ticks = -100000 + 1; ticks <= 100000; ticks++)
        {
            int current = AerosolDrift.SampleIndex(ticks);
            Assert.That(current, Is.InRange(previous, previous + 1));
            previous = current;
        }
    }

    // Mean |multiplier(i + lag) - multiplier(i)| over a long stretch of every seed. Shared by the
    // three correlation-time tests so they are all measuring the same thing at different lags.
    private static double MeanAbsoluteChange(int lagInSamples)
    {
        double sum = 0;
        long count = 0;

        foreach (int seed in Seeds)
        {
            for (int sampleIndex = 0; sampleIndex < 40000; sampleIndex++)
            {
                sum += Math.Abs(AerosolDrift.Multiplier(sampleIndex + lagInSamples, seed)
                    - AerosolDrift.Multiplier(sampleIndex, seed));
                count++;
            }
        }

        return sum / count;
    }
}
