using System.Collections.Generic;
using CelestialLighting;
using NUnit.Framework;

namespace CelestialLighting.Tests;

// Offline coverage for §22's "how cloudy is a typical Clear day here" classifier. This file has no
// noise in it at all — every case below is a small, hand-computed weighted average, chosen to pin the
// specific edge cases a live BiomeDef's weather list can actually produce:
//
//   * empty / all-ineligible list -> 0    -> a biome with no climate signal reads as clear, not as a
//                                             divide-by-zero or a fabricated middle value,
//   * temperature-ineligible entries are excluded from BOTH masses, not just the wet one -> otherwise
//     a biome full of snow entries that can never occur at the tile's temperature would still drag
//     the estimate toward "wet" by sheer weight of entries,
//   * a negative Commonality*RainfallFactor product cannot subtract from the totals -> modded content
//     controls the rainfall curve, and a dip below zero must not be able to swing the ratio in a
//     direction no vanilla weather roll could ever produce,
//   * a NaN-poisoned entry collapses the whole result to 0 rather than propagating -> see
//     SeasonalWetFraction.Clamp01's header.
[TestFixture]
public class SeasonalWetFractionTests
{
    private static SeasonalWetFraction.Entry Wet(float commonality, float rainfallFactor, bool eligible = true) =>
        new(commonality, rainfallFactor, isWet: true, temperatureEligible: eligible);

    private static SeasonalWetFraction.Entry Dry(float commonality, float rainfallFactor, bool eligible = true) =>
        new(commonality, rainfallFactor, isWet: false, temperatureEligible: eligible);

    [Test]
    public void EmptyList_IsZero()
    {
        Assert.That(SeasonalWetFraction.Fraction(new List<SeasonalWetFraction.Entry>()), Is.EqualTo(0f));
    }

    [Test]
    public void AllEntriesIneligible_IsZero_NotADivideByZero()
    {
        List<SeasonalWetFraction.Entry> entries = new()
        {
            Wet(1f, 1f, eligible: false),
            Dry(1f, 1f, eligible: false),
        };

        Assert.That(SeasonalWetFraction.Fraction(entries), Is.EqualTo(0f));
    }

    [Test]
    public void OnlyWetEntries_IsOne()
    {
        List<SeasonalWetFraction.Entry> entries = new() { Wet(0.6f, 1f), Wet(0.2f, 1f) };
        Assert.That(SeasonalWetFraction.Fraction(entries), Is.EqualTo(1f));
    }

    [Test]
    public void OnlyDryEntries_IsZero()
    {
        List<SeasonalWetFraction.Entry> entries = new() { Dry(0.6f, 1f), Dry(0.2f, 1f) };
        Assert.That(SeasonalWetFraction.Fraction(entries), Is.EqualTo(0f));
    }

    [Test]
    public void MixedEntries_IsTheCommonalityWeightedRatio()
    {
        // Rain: commonality 0.3, rainfall factor 2 -> mass 0.6, wet.
        // Clear: commonality 0.7, rainfall factor 1 -> mass 0.7, dry.
        // Fraction = 0.6 / (0.6 + 0.7) = 0.4615...
        List<SeasonalWetFraction.Entry> entries = new() { Wet(0.3f, 2f), Dry(0.7f, 1f) };

        Assert.That(SeasonalWetFraction.Fraction(entries), Is.EqualTo(0.6f / 1.3f).Within(1e-6f));
    }

    [Test]
    public void TemperatureIneligibleEntries_AreExcludedFromBothMasses_NotJustTheWetOne()
    {
        // A snow entry that cannot occur at the current (hot) temperature must not drag the estimate
        // toward "wet" merely by being listed — nor toward "dry" by inflating totalMass with a mass
        // vanilla's own roll could never actually select.
        List<SeasonalWetFraction.Entry> eligibleOnly = new() { Dry(0.5f, 1f) };
        List<SeasonalWetFraction.Entry> withIneligibleSnow = new()
        {
            Dry(0.5f, 1f),
            Wet(10f, 10f, eligible: false),
        };

        Assert.That(SeasonalWetFraction.Fraction(withIneligibleSnow),
            Is.EqualTo(SeasonalWetFraction.Fraction(eligibleOnly)));
    }

    [Test]
    public void ANegativeMassContribution_CannotPushTheFractionOutsideItsIngredients()
    {
        // A rainfall curve dipping below zero (modded content controls this curve) must be clamped to
        // zero mass, not allowed to subtract — otherwise a "barely possible" dry weather type with a
        // negative factor could make the estimate read WETTER than either entry alone implies.
        List<SeasonalWetFraction.Entry> entries = new() { Wet(0.5f, 1f), Dry(0.5f, -1f) };

        // The dry entry contributes zero mass on both sides, so this reduces to the single wet entry.
        Assert.That(SeasonalWetFraction.Fraction(entries), Is.EqualTo(1f));
    }

    [Test]
    public void ANaNMassContribution_CollapsesTheWholeResultToZero_RatherThanPropagating()
    {
        // totalMass ends up NaN the moment any single entry poisons the running sum, and
        // "totalMass > 0f" is false for NaN — so the zero-guard branch catches this case for free,
        // without needing every intermediate sum to be independently clamped. Pinned explicitly because
        // that is a consequence of how the guard is written, not something the guard was designed
        // around, and a refactor could break it silently.
        List<SeasonalWetFraction.Entry> entries = new() { Wet(0.5f, 1f), Dry(float.NaN, 1f) };
        Assert.That(SeasonalWetFraction.Fraction(entries), Is.EqualTo(0f));
    }

    [Test]
    public void AnInfiniteMassContribution_DrownsOutTheRestRatherThanBreaking()
    {
        // Unlike NaN, an infinite mass leaves totalMass positive, so this takes the ordinary division
        // path rather than the zero-guard: wetMass / totalMass is a finite number over an infinite one,
        // which IEEE754 gives as exactly 0 without needing a special case. A dry entry with infinite
        // mass therefore reads as "certainly dry" rather than as undefined.
        List<SeasonalWetFraction.Entry> entries = new() { Wet(0.5f, 1f), Dry(float.PositiveInfinity, 1f) };
        Assert.That(SeasonalWetFraction.Fraction(entries), Is.EqualTo(0f));
    }

    [Test]
    public void TheResultIsAlwaysInTheUnitInterval()
    {
        // A small sweep of hand-built lists rather than a formal proof, covering the shapes an actual
        // BiomeDef.baseWeatherCommonalities list can take: many entries, one entry, all one class, an
        // extreme commonality alongside a tiny one.
        List<List<SeasonalWetFraction.Entry>> cases = new()
        {
            new() { Wet(1000f, 1f), Dry(0.001f, 1f) },
            new() { Wet(0.001f, 1f), Dry(1000f, 1f) },
            new() { Wet(1f, 1f), Wet(1f, 1f), Dry(1f, 1f), Dry(1f, 1f), Dry(1f, 1f) },
            new() { Wet(0f, 0f), Dry(0f, 0f) },
        };

        foreach (List<SeasonalWetFraction.Entry> entries in cases)
            Assert.That(SeasonalWetFraction.Fraction(entries), Is.InRange(0f, 1f));
    }
}
