using System;
using System.Collections.Generic;
using CelestialLighting;
using NUnit.Framework;

namespace CelestialLighting.Tests;

// Offline coverage for §11a's display schedule — when each aurora display is in the sky and for how
// long.
//
// This is the file that can only be checked offline. Watching a real aurora tells you what the sky
// looks like in the next few minutes; it cannot tell you that the four slots never resynchronise, that
// no band ends up systematically brighter than another, or that a display is never replaced by a copy
// of itself. Those are properties of a whole night, and they are the ones a retune would break.
[TestFixture]
public class AuroraDisplaysTests
{
    // One in-game hour, in ticks (GenDate.TicksPerHour). Spelled out rather than imported, because the
    // pure core must not depend on Verse.
    private const int TicksPerHour = 2500;

    private const int TicksPerDay = 60000;

    // A handful of unrelated auroras, so nothing below passes only for one lucky seed.
    private static readonly int[] EventSeeds = { 0, 1, 7, 12345, -8080, 1_800_000 };

    [Test]
    public void EveryDisplay_LivesAFewInGameHours()
    {
        // The whole point of the change: a display owns its own screen time rather than lasting exactly
        // as long as the condition, which on a solar flare is a whole game day.
        for (int slot = 0; slot < AuroraDisplays.MaxLive; slot++)
        {
            int life = AuroraDisplays.LifeTicksFor(slot);

            Assert.That(
                life, Is.GreaterThanOrEqualTo(2 * TicksPerHour),
                $"slot {slot} blinks rather than hangs in the sky");
            Assert.That(
                life, Is.LessThanOrEqualTo(6 * TicksPerHour),
                $"slot {slot} outlasts a night, which is what this replaced");
        }
    }

    [Test]
    public void EveryDisplay_HasRoomToReachFullAlpha()
    {
        // ConditionRampFactor combines fade-in and fade-out with Min, so a lifetime shorter than two
        // fades never reaches 1 — the display would ghost in and straight back out, at a peak that
        // depends on arithmetic nobody meant to tune.
        for (int slot = 0; slot < AuroraDisplays.MaxLive; slot++)
            Assert.That(
                AuroraDisplays.LifeTicksFor(slot),
                Is.GreaterThan(2f * AuroraDisplays.FadeTicks),
                $"slot {slot} can never reach full alpha");
    }

    [Test]
    public void EverySlot_IsLitTheSameShareOfTheTime()
    {
        // Load-bearing, not cosmetic. Each slot owns a fixed horizontal band of the map, so a slot lit
        // more of the time than its neighbours paints a permanent north-south brightness gradient
        // across the colony for no physical reason at all. Unequal PERIODS are the point; unequal duty
        // is the bug.
        double first = Duty(0);

        for (int slot = 1; slot < AuroraDisplays.MaxLive; slot++)
            Assert.That(
                Duty(slot), Is.EqualTo(first).Within(0.02),
                $"slot {slot}'s band would be systematically brighter or dimmer than slot 0's");
    }

    [Test]
    public void Slots_NeverFallBackIntoStep()
    {
        // Round periods (10000 and 20000, say) resynchronise every few in-game hours and the sky
        // visibly pulses. Pairwise-coprime periods cannot: the true repeat of the whole pattern is the
        // LCM, and this pins it well past any aurora, any colony and any playthrough.
        long repeat = AuroraDisplays.CycleTicksFor(0);

        for (int slot = 1; slot < AuroraDisplays.MaxLive; slot++)
            repeat = Lcm(repeat, AuroraDisplays.CycleTicksFor(slot));

        Assert.That(
            repeat, Is.GreaterThan(100L * TicksPerDay),
            "the four slots realign inside a hundred game days, so the sequence visibly repeats");
    }

    [Test]
    public void TheSkyIsNeverEmptyForLong()
    {
        // Lulls are wanted — a display that never pauses is a colour filter, not an event, and §11's
        // flat tint still colours the sky underneath one. What is not wanted is a lull long enough that
        // a player who looks up during an aurora concludes there isn't one.
        Assert.That(
            LongestDarkStretch(), Is.LessThanOrEqualTo(2 * TicksPerHour),
            "the sky goes completely empty for over two in-game hours mid-aurora");
    }

    [Test]
    public void TheSkyIsMostlyNotEmpty()
    {
        // The other end of the same trade. Measured over four days so no single slot's period lines up
        // with the window.
        int dark = 0;
        int samples = 0;

        for (int t = 0; t < 4 * TicksPerDay; t += 10)
        {
            samples++;
            if (PeakAlphaAt(t) <= 0f)
                dark++;
        }

        Assert.That(
            dark / (double)samples, Is.LessThan(0.1),
            "the aurora is absent for over a tenth of its own event");
    }

    [Test]
    public void MoreThanOneDisplay_IsUsuallyInTheSky()
    {
        // Issue #59.4: the shipping path used to draw exactly one sheet however many the layout could
        // hold. Several at once is the visible half of the change.
        var buffer = new AuroraDisplay[AuroraDisplays.MaxLive];
        int multiple = 0;
        int samples = 0;

        for (int t = 0; t < 4 * TicksPerDay; t += 10)
        {
            samples++;
            if (AuroraDisplays.Resolve(1234, t, buffer) >= 2)
                multiple++;
        }

        Assert.That(
            multiple / (double)samples, Is.GreaterThan(0.5),
            "two or more displays share the sky less than half the time");
    }

    [Test]
    public void ResolveNeverOverrunsTheMaterialsAllocatedAtStartup()
    {
        // Materials are built once in a static constructor because `new Material` must be on the main
        // thread, so a schedule offering more displays than that would index past the array.
        var buffer = new AuroraDisplay[AuroraDisplays.MaxLive];

        foreach (int seed in EventSeeds)
            for (int t = 0; t < 2 * TicksPerDay; t += 37)
            {
                int count = AuroraDisplays.Resolve(seed, t, buffer);

                Assert.That(count, Is.InRange(0, AuroraDisplays.MaxLive), $"seed {seed} at {t}");

                for (int i = 0; i < count; i++)
                {
                    Assert.That(buffer[i].Slot, Is.InRange(0, AuroraDisplays.MaxLive - 1));
                    Assert.That(buffer[i].Alpha, Is.GreaterThan(0f).And.LessThanOrEqualTo(1f));
                }
            }
    }

    [Test]
    public void EveryLiveDisplay_OccupiesADistinctSlot()
    {
        // Two live displays on one slot would share a material and a band: the second would overwrite
        // the first's UV scale and colour, and only one of the two would be drawn where it was placed.
        var buffer = new AuroraDisplay[AuroraDisplays.MaxLive];

        for (int t = 0; t < 2 * TicksPerDay; t += 13)
        {
            int count = AuroraDisplays.Resolve(99, t, buffer);
            var seen = new HashSet<int>();

            for (int i = 0; i < count; i++)
                Assert.That(seen.Add(buffer[i].Slot), Is.True, $"slot {buffer[i].Slot} twice at tick {t}");
        }
    }

    [Test]
    public void ASlotRelightsAsANewDisplay_NotTheSameOneAgain()
    {
        // The reason the seed carries a generation at all. Without it a slot would blink back on in the
        // same place at the same size, which reads as a bug rather than as a new display.
        foreach (int seed in EventSeeds)
        {
            var seeds = new HashSet<int>();

            for (int gen = 0; gen < 40; gen++)
                Assert.That(
                    seeds.Add(AuroraDisplays.SeedFor(seed, 0, gen)), Is.True,
                    $"event {seed} reuses a display seed within 40 generations");
        }
    }

    [Test]
    public void TwoAurorasAreNotTheSameSequence()
    {
        // A rare event cannot afford the second one a player sees being a replay of the first.
        for (int gen = 0; gen < 8; gen++)
            for (int slot = 0; slot < AuroraDisplays.MaxLive; slot++)
                Assert.That(
                    AuroraDisplays.SeedFor(11, slot, gen),
                    Is.Not.EqualTo(AuroraDisplays.SeedFor(12, slot, gen)),
                    $"slot {slot} generation {gen} is identical across two auroras");
    }

    [Test]
    public void ADisplayFadesRatherThanSnaps()
    {
        // Issue #59.2: the condition's own ramp fades the whole EFFECT. This is the missing per-display
        // fade — a thing appearing and dissolving in the sky, rather than the sky's brightness being
        // turned up and down.
        for (int slot = 0; slot < AuroraDisplays.MaxLive; slot++)
        {
            Assert.That(AuroraDisplays.AlphaAt(slot, PhaseOf(slot, 0)), Is.EqualTo(0f), $"slot {slot} spawns lit");

            // One tick before the end of its life it must still be almost out, and one tick after it,
            // gone. Sampling the two sides of the boundary is what catches a life/cycle table where a
            // slot's lit stretch runs past the end of its own cycle.
            int life = AuroraDisplays.LifeTicksFor(slot);
            Assert.That(
                AuroraDisplays.AlphaAt(slot, PhaseOf(slot, life - 1)), Is.LessThan(0.01f),
                $"slot {slot} is still bright at the end of its life");
            Assert.That(
                AuroraDisplays.AlphaAt(slot, PhaseOf(slot, life)), Is.EqualTo(0f),
                $"slot {slot} is still lit past the end of its life");
        }
    }

    [Test]
    public void AlphaClimbsAndFallsMonotonically()
    {
        // A fade that wobbles reads as flicker. Walk one whole life of each slot and check the envelope
        // rises to its peak and then falls, with no reversal in either half.
        for (int slot = 0; slot < AuroraDisplays.MaxLive; slot++)
        {
            int life = AuroraDisplays.LifeTicksFor(slot);
            int peak = life / 2;

            for (int t = 1; t < peak; t++)
                Assert.That(
                    AuroraDisplays.AlphaAt(slot, PhaseOf(slot, t)),
                    Is.GreaterThanOrEqualTo(AuroraDisplays.AlphaAt(slot, PhaseOf(slot, t - 1))),
                    $"slot {slot} dips while fading in, at {t}");

            for (int t = peak + 1; t < life; t++)
                Assert.That(
                    AuroraDisplays.AlphaAt(slot, PhaseOf(slot, t)),
                    Is.LessThanOrEqualTo(AuroraDisplays.AlphaAt(slot, PhaseOf(slot, t - 1))),
                    $"slot {slot} brightens while fading out, at {t}");
        }
    }

    [Test]
    public void TheSequenceStartsAtItsBeginning()
    {
        // The schedule is measured from the tick the aurora lit, not from TicksGame, so slot 0 always
        // spawns as the aurora does. Keyed on a global clock instead, an aurora could open with slot 0
        // already halfway through a life and the first thing a player saw would be a display fading
        // out.
        var buffer = new AuroraDisplay[AuroraDisplays.MaxLive];

        Assert.That(AuroraDisplays.AlphaAt(0, 0), Is.EqualTo(0f));
        Assert.That(AuroraDisplays.AlphaAt(0, (int)AuroraDisplays.FadeTicks), Is.EqualTo(1f).Within(1e-5f));

        // ...and a negative age (a clock that ran backwards under a dev tool or a save reload) is
        // clamped rather than folded onto some other generation by C#'s truncating division.
        Assert.That(AuroraDisplays.Resolve(5, -1000, buffer), Is.EqualTo(AuroraDisplays.Resolve(5, 0, buffer)));
    }

    // --- helpers --------------------------------------------------------------------------------

    // An age at which slot `slot` is `into` ticks into its own life, undoing its stagger.
    //
    // Offset a whole cycle on, so the result is non-negative whatever the slot's phase and lands on
    // generation 1 rather than on the clamped start — which would hide exactly the negative-age
    // truncation this file also tests for.
    private static int PhaseOf(int slot, int into) =>
        AuroraDisplays.CycleTicksFor(slot) - AuroraDisplays.PhaseTicksFor(slot) + into;

    private static double Duty(int slot)
    {
        int lit = 0;
        int cycle = AuroraDisplays.CycleTicksFor(slot);

        for (int t = 0; t < cycle; t++)
            if (AuroraDisplays.AlphaAt(slot, t) > 0f)
                lit++;

        return lit / (double)cycle;
    }

    private static float PeakAlphaAt(int ticks)
    {
        float peak = 0f;

        for (int slot = 0; slot < AuroraDisplays.MaxLive; slot++)
            peak = Math.Max(peak, AuroraDisplays.AlphaAt(slot, ticks));

        return peak;
    }

    private static int LongestDarkStretch()
    {
        int longest = 0;
        int run = 0;

        for (int t = 0; t < 8 * TicksPerDay; t += 10)
        {
            if (PeakAlphaAt(t) > 0f)
                run = 0;
            else
            {
                run += 10;
                longest = Math.Max(longest, run);
            }
        }

        return longest;
    }

    private static long Lcm(long a, long b) => a / Gcd(a, b) * b;

    private static long Gcd(long a, long b) => b == 0 ? a : Gcd(b, a % b);
}
