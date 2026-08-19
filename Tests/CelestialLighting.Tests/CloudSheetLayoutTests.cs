using System;

namespace CelestialLighting.Tests;

// Offline coverage for §25's sheet placement (Source/CloudSheetLayout.cs) — where the bounded cloud
// sheets are, how many there are, and how they move.
[TestFixture]
public class CloudSheetLayoutTests
{
    private const int MapX = 250;
    private const int MapZ = 250;
    private const int Seed = 4242;

    // An all-low-deck mixture, so the placement tests below measure placement rather than §25b's
    // deck spread. The low deck's size and speed scales are both exactly 1, so these are the numbers
    // this file pinned before decks existed — which is the point: adding varieties must not have
    // moved where an ordinary cloud goes. CloudDeckMathTests owns the mixture itself.
    private static readonly float[] LowDeckOnly = { 1f, 0f, 0f };

    private static CloudSheetLayout.Placement Place(int index, int ticks) =>
        CloudSheetLayout.PlacementFor(index, Seed, ticks, MapX, MapZ, LowDeckOnly);

    // Coverage is a COUNT here, not a threshold — which is the headline difference from the tiled
    // version §25 replaced, and from §23b/§23c, where coverage moves a threshold instead.
    [Test]
    public void MoreCloudIsMoreClouds()
    {
        Assert.That(CloudSheetLayout.SheetCount(0f), Is.EqualTo(0));

        // ShippedSheetCap, NOT MaxSheets. §25d made the two different things and the distinction is
        // worth stating rather than inferring: MaxSheets is now the capacity of the placement array
        // both layouts share, while the CAP is how many sheets a given layout puts up — twelve big
        // ones for §25b, forty small ones for §25d. This assertion read EqualTo(MaxSheets) and caught
        // the change, which is the only reason the two names got separated properly.
        Assert.That(CloudSheetLayout.SheetCount(1f),
            Is.EqualTo(CloudSheetLayout.ShippedSheetCap));
        Assert.That(CloudSheetLayout.SheetCount(1f, CloudSheetLayout.PresentSheetCap),
            Is.EqualTo(CloudSheetLayout.PresentSheetCap));

        // Neither layout may ask for more placements than the shared array can hold.
        Assert.That(CloudSheetLayout.ShippedSheetCap,
            Is.LessThanOrEqualTo(CloudSheetLayout.MaxSheets));
        Assert.That(CloudSheetLayout.PresentSheetCap,
            Is.LessThanOrEqualTo(CloudSheetLayout.MaxSheets));

        int previous = 0;
        for (float fraction = 0f; fraction <= 1f; fraction += 0.05f)
        {
            int count = CloudSheetLayout.SheetCount(fraction);
            Assert.That(count, Is.GreaterThanOrEqualTo(previous), $"went backwards at {fraction}");
            Assert.That(count, Is.LessThanOrEqualTo(CloudSheetLayout.ShippedSheetCap));
            previous = count;
        }
    }

    // Any cloud at all should put a cloud in the sky. Rounding a thin morning down to zero would make
    // the feature silently absent on exactly the skies it is most flattering on.
    [Test]
    public void AnyCloudAtAllGetsAtLeastOneSheet()
    {
        Assert.That(CloudSheetLayout.SheetCount(0.001f), Is.EqualTo(1));
        Assert.That(CloudSheetLayout.SheetCount(0.0001f), Is.EqualTo(1));
    }

    // THE OFF-SCREEN INVARIANT, and the reason the wrap is invisible: a sheet must be entirely outside
    // the map at both ends of its travel, so nothing ever appears or vanishes in view. Tested against
    // the full quad extent rather than the visible blob inside it, which is the conservative direction
    // — the blob's alpha reaches zero before its quad does.
    [Test]
    public void EverySheetIsFullyOffMapAtBothEndsOfItsCrossing()
    {
        for (int index = 0; index < CloudSheetLayout.MaxSheets; index++)
        {
            // Find this sheet's own crossing period by walking until its x resets, then check the
            // instants either side of the wrap.
            CloudSheetLayout.Placement start = Place(index, 0);
            float previousX = start.CenterX;

            for (int ticks = 1; ticks < CloudSheetLayout.BaseCrossingTicks * 2; ticks += 5)
            {
                CloudSheetLayout.Placement now = Place(index, ticks);

                // A wrap is the only way x goes backwards.
                if (now.CenterX < previousX)
                {
                    CloudSheetLayout.Placement before = Place(index, ticks - 5);
                    Assert.That(CloudSheetLayout.OnScreen(before, MapX, MapZ), Is.False,
                        $"sheet {index} was still on-map at the end of its crossing");
                    Assert.That(CloudSheetLayout.OnScreen(now, MapX, MapZ), Is.False,
                        $"sheet {index} appeared on-map at the start of its crossing");
                    break;
                }

                previousX = now.CenterX;
            }
        }
    }

    // Reproducible from the tick alone — the same property §23b's drift has, and for the same reason:
    // an accumulated position depends on how many frames have been drawn, which makes a harness
    // screenshot of a moving sky meaningless.
    [Test]
    public void PlacementIsAPureFunctionOfTheTick()
    {
        for (int index = 0; index < 4; index++)
        {
            CloudSheetLayout.Placement first = Place(index, 12345);
            CloudSheetLayout.Placement again = Place(index, 12345);

            Assert.That(again.CenterX, Is.EqualTo(first.CenterX));
            Assert.That(again.CenterZ, Is.EqualTo(first.CenterZ));
            Assert.That(again.Size, Is.EqualTo(first.Size));
        }
    }

    // The sky must not translate rigidly: one panning plane reads as the camera moving rather than as
    // weather moving, which is the observation §11a already records. Different speeds are what buy
    // that, so a test that the speeds actually differ is a test of the whole design.
    [Test]
    public void SheetsMoveAtDifferentSpeedsInTheSameGeneralDirection()
    {
        float[] speeds = new float[CloudSheetLayout.MaxSheets];
        for (int index = 0; index < speeds.Length; index++)
        {
            CloudSheetLayout.Placement before = Place(index, 1000);
            CloudSheetLayout.Placement after = Place(index, 1100);

            // Skip a sheet that wrapped inside the sample window; its delta is not a speed.
            speeds[index] = after.CenterX > before.CenterX ? after.CenterX - before.CenterX : float.NaN;
        }

        float min = float.MaxValue;
        float max = float.MinValue;
        int sampled = 0;
        foreach (float speed in speeds)
        {
            if (float.IsNaN(speed))
                continue;

            Assert.That(speed, Is.GreaterThan(0f), "every sheet travels the same way");
            min = System.MathF.Min(min, speed);
            max = System.MathF.Max(max, speed);
            sampled++;
        }

        Assert.That(sampled, Is.GreaterThan(4));
        Assert.That(max, Is.GreaterThan(min * 1.3f), "the speeds are too close to break rigid motion");
    }

    // Slow, which is the other half of the brief. One crossing of a 250-cell map takes hours of game
    // time, not minutes — a cloud that crosses a colony while you watch one pawn eat is a race.
    [Test]
    public void TheCrossingIsMeasuredInHoursNotMinutes()
    {
        const int TicksPerHour = 2500;
        Assert.That(CloudSheetLayout.BaseCrossingTicks, Is.GreaterThan(TicksPerHour * 2));
    }

    // Sheets wander a few degrees off the shared heading, and no further: a cloud field is one air
    // mass. Pinned as a bound on the cross-axis excursion over a whole crossing.
    [Test]
    public void CrossDriftStaysWithinOneAirMass()
    {
        for (int index = 0; index < CloudSheetLayout.MaxSheets; index++)
        {
            float min = float.MaxValue;
            float max = float.MinValue;

            for (int ticks = 0; ticks < CloudSheetLayout.BaseCrossingTicks; ticks += 100)
            {
                CloudSheetLayout.Placement placement = Place(index, ticks);
                min = System.MathF.Min(min, placement.CenterZ);
                max = System.MathF.Max(max, placement.CenterZ);
            }

            Assert.That(max - min, Is.LessThanOrEqualTo(MapZ * CloudSheetLayout.CrossDriftFraction + 1f),
                $"sheet {index} wandered further across than one air mass allows");
        }
    }

    // --- Overlap: the "partially additive up to a limit" behaviour ---

    [Test]
    public void OverlapDepthIsZeroForASheetOnItsOwn()
    {
        CloudSheetLayout.Placement[] one = { Place(0, 0) };
        Assert.That(CloudSheetLayout.OverlapDepth(one, 1, 0), Is.EqualTo(0f));
    }

    [Test]
    public void StackedSheetsReportDepthAndSeparatedOnesDoNot()
    {
        CloudSheetLayout.Placement[] stacked =
        {
            new CloudSheetLayout.Placement(100f, 100f, 80f, false, false, 1f, 0, CloudDeckMath.LowDeck),
            new CloudSheetLayout.Placement(100f, 100f, 80f, false, false, 1f, 0, CloudDeckMath.LowDeck),
        };

        CloudSheetLayout.Placement[] apart =
        {
            new CloudSheetLayout.Placement(0f, 0f, 80f, false, false, 1f, 0, CloudDeckMath.LowDeck),
            new CloudSheetLayout.Placement(400f, 400f, 80f, false, false, 1f, 0, CloudDeckMath.LowDeck),
        };

        Assert.That(CloudSheetLayout.OverlapDepth(stacked, 2, 0), Is.EqualTo(1f).Within(1e-4f));
        Assert.That(CloudSheetLayout.OverlapDepth(apart, 2, 0), Is.EqualTo(0f));
    }

    // Weighted by the other sheet's alpha, so haze drifting across a thunderhead does not read as a
    // second storey of cloud.
    [Test]
    public void ThinnerSheetsContributeLessDepth()
    {
        CloudSheetLayout.Placement[] thick =
        {
            new CloudSheetLayout.Placement(100f, 100f, 80f, false, false, 1f, 0, CloudDeckMath.LowDeck),
            new CloudSheetLayout.Placement(100f, 100f, 80f, false, false, 1f, 0, CloudDeckMath.LowDeck),
        };

        CloudSheetLayout.Placement[] thin =
        {
            new CloudSheetLayout.Placement(100f, 100f, 80f, false, false, 1f, 0, CloudDeckMath.LowDeck),
            new CloudSheetLayout.Placement(100f, 100f, 80f, false, false, 0.3f, 0, CloudDeckMath.LowDeck),
        };

        Assert.That(CloudSheetLayout.OverlapDepth(thin, 2, 0),
            Is.LessThan(CloudSheetLayout.OverlapDepth(thick, 2, 0)));
    }

    // The cap is the point: unbounded accumulation makes a busy sky a white slab, which is the exact
    // failure the tiled version had at full cover, reached from the other direction.
    [Test]
    public void TheOverlapBoostIsCapped()
    {
        Assert.That(CloudSheetMath.OverlapBoost(0f), Is.EqualTo(1f));
        Assert.That(CloudSheetMath.OverlapBoost(1f), Is.GreaterThan(1f));
        Assert.That(CloudSheetMath.OverlapBoost(100f),
            Is.EqualTo(CloudSheetMath.MaxOverlapBoost).Within(1e-4f));
        Assert.That(CloudSheetMath.OverlapBoost(float.NaN), Is.EqualTo(1f));
    }

    // §25d keeps §25b's sheet size and cuts the COUNT instead (issue #144).
    //
    // WHAT THIS PINS IS THE PAIRING, because the failure was never size alone. Twelve sheets of
    // two-thirds the map blanket the view and overlap into one flat wash — differenced against a
    // cloudless frame it measured as a uniform lift over most of the screen showing the terrain's own
    // texture, with one soft edge and no shape anywhere. A sheet's footprint goes with the SQUARE of
    // its size, so size and count are one decision; whichever way the size goes, the count has to go
    // the other way. This asserts that relationship rather than either number, so a later change to
    // one that forgets the other fails here.
    [Test]
    public void PresentLayoutTradesCountForSize()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CloudSheetLayout.PresentSheetCap,
                Is.LessThan(CloudSheetLayout.ShippedSheetCap),
                "§25d's sheets are not smaller than §25b's, so there must be fewer of them");

            // Expected footprint of a full sky, as a multiple of the map. Both layouts are allowed to
            // overlap — clouds do — but neither may ask for so much that overlap is all there is.
            float shipped = ShippedSheetCap_Coverage();
            float present = PresentCoverage();

            Assert.That(present, Is.LessThan(shipped),
                "§25d must cover less sky at full cloud than the wash it replaced");
        });

        float ShippedSheetCap_Coverage() =>
            CloudSheetLayout.ShippedSheetCap
            * CloudSheetLayout.BaseSizeFraction * CloudSheetLayout.BaseSizeFraction;

        float PresentCoverage() =>
            CloudSheetLayout.PresentSheetCap
            * CloudSheetLayout.PresentSizeFraction * CloudSheetLayout.PresentSizeFraction;
    }

    // A partly-cloudy sky gets a HANDFUL, not the cap: SheetCount rounds `fraction * cap` up, so the
    // cap is the count of a full overcast. Pinned because "five clouds" is the intent and it is easy
    // to read the cap as the number always drawn.
    [Test]
    public void PresentLayoutGivesAFairDayAFewClouds()
    {
        int fair = CloudSheetLayout.SheetCount(0.35f, CloudSheetLayout.PresentSheetCap);
        int overcast = CloudSheetLayout.SheetCount(1f, CloudSheetLayout.PresentSheetCap);

        Assert.Multiple(() =>
        {
            Assert.That(fair, Is.InRange(1, CloudSheetLayout.PresentSheetCap));
            Assert.That(fair, Is.LessThan(overcast));
            Assert.That(overcast, Is.EqualTo(CloudSheetLayout.PresentSheetCap));
        });
    }

    // §25d's sheet is smaller than §25b's but still large enough to feel overhead, which is the
    // narrow band both failures sit outside of: at full size a few sheets overlap into one flat
    // wash, and at a fifth they read as puffs rather than as weather.
    [Test]
    public void APresentSheetIsSmallerButStillOverhead()
    {
        CloudSheetLayout.Placement shipped = CloudSheetLayout.PlacementFor(
            0, Seed, 0, MapX, MapZ, LowDeckOnly);
        CloudSheetLayout.Placement present = CloudSheetLayout.PlacementFor(
            0, Seed, 0, MapX, MapZ, LowDeckOnly, CloudSheetLayout.PresentSizeFraction);

        int shorter = MapX < MapZ ? MapX : MapZ;

        Assert.Multiple(() =>
        {
            Assert.That(present.Size, Is.LessThan(shipped.Size));
            Assert.That(present.Size, Is.GreaterThan(shorter * 0.25f),
                "smaller than this and a cloud reads as a puff on the ground rather than weather above it");
        });
    }

    // --- The coverage weight and the entry latch: what stops a cloud vanishing where it stands ---

    // Every sheet below the marginal one is fully there, and the marginal one is however far into its
    // own share the cover has got. This is the count restated as a continuous quantity, so the two
    // must agree: CoverageAlpha is only allowed to be partial on the LAST sheet SheetCount returns.
    [TestCase(CloudSheetLayout.ShippedSheetCap)]
    [TestCase(CloudSheetLayout.PresentSheetCap)]
    public void OnlyTheMarginalSheetIsPartial(int cap)
    {
        for (float fraction = 0f; fraction <= 1f; fraction += 0.017f)
        {
            int count = CloudSheetLayout.SheetCount(fraction, cap);
            for (int index = 0; index < count - 1; index++)
            {
                Assert.That(CloudSheetLayout.CoverageAlpha(index, fraction, cap), Is.EqualTo(1f),
                    $"sheet {index} should be whole at {fraction} (cap {cap})");
            }

            if (count > 0)
            {
                Assert.That(CloudSheetLayout.CoverageAlpha(count - 1, fraction, cap),
                    Is.GreaterThan(0f).And.LessThanOrEqualTo(1f),
                    $"the marginal sheet should be present but may be partial at {fraction}");
            }

            Assert.That(CloudSheetLayout.CoverageAlpha(count, fraction, cap), Is.EqualTo(0f),
                $"a sheet past the count should not be drawn at {fraction}");
        }
    }

    // THE POP THIS EXISTS TO REMOVE. Crossing a sheet's share of the cover used to delete a whole
    // cloud from the sky in one step, wherever it was — and sheets spend most of their crossing over
    // the map, so "wherever it was" was usually mid-screen. Across the threshold the drawn amount must
    // be continuous: a small change in cover may only make a small change to any one sheet.
    [Test]
    public void CrossingASheetThresholdIsContinuous()
    {
        int cap = CloudSheetLayout.ShippedSheetCap;
        for (int boundary = 1; boundary < cap; boundary++)
        {
            float threshold = boundary / (float)cap;
            for (int index = 0; index < cap; index++)
            {
                float below = CloudSheetLayout.CoverageAlpha(index, threshold - 1e-4f, cap);
                float above = CloudSheetLayout.CoverageAlpha(index, threshold + 1e-4f, cap);
                Assert.That(Math.Abs(above - below), Is.LessThan(0.01f),
                    $"sheet {index} jumped across the {boundary}/{cap} threshold");
            }
        }
    }

    // The weight is reversible and drives all three lanes at once, which is the point of putting it on
    // the placement rather than in any one renderer: a sheet half-faded out of the sky is half-faded
    // out of its own shadow and its own underlight, because all three read this one number.
    [Test]
    public void ScalingAPlacementsAlphaLeavesEverythingElseWhereItWas()
    {
        CloudSheetLayout.Placement placement = Place(3, 12345);
        CloudSheetLayout.Placement faded = placement.WithAlphaScale(0.25f);

        Assert.That(faded.CenterX, Is.EqualTo(placement.CenterX));
        Assert.That(faded.CenterZ, Is.EqualTo(placement.CenterZ));
        Assert.That(faded.Size, Is.EqualTo(placement.Size));
        Assert.That(faded.FlipU, Is.EqualTo(placement.FlipU));
        Assert.That(faded.FlipV, Is.EqualTo(placement.FlipV));
        Assert.That(faded.ShapeSeed, Is.EqualTo(placement.ShapeSeed));
        Assert.That(faded.Deck, Is.EqualTo(placement.Deck));
        Assert.That(faded.Alpha, Is.EqualTo(placement.Alpha * 0.25f).Within(1e-6f));

        // Nonsense in, nothing drawn — the same defensive posture the rest of the layout takes toward
        // a caller-supplied number, since this one is multiplied straight into a material colour.
        Assert.That(placement.WithAlphaScale(float.NaN).Alpha, Is.EqualTo(0f));
        Assert.That(placement.WithAlphaScale(-1f).Alpha, Is.EqualTo(0f));
        Assert.That(placement.WithAlphaScale(5f).Alpha, Is.EqualTo(placement.Alpha));
    }

    // Nonsense cover is answered the same way SheetCount answers it, rather than each having its own
    // opinion about a NaN that reached them from a modded biome's data.
    [Test]
    public void NonsenseCoverDrawsNothing()
    {
        int cap = CloudSheetLayout.ShippedSheetCap;
        Assert.That(CloudSheetLayout.CoverageAlpha(0, float.NaN, cap), Is.EqualTo(0f));
        Assert.That(CloudSheetLayout.CoverageAlpha(0, -1f, cap), Is.EqualTo(0f));
        Assert.That(CloudSheetLayout.CoverageAlpha(-1, 0.5f, cap), Is.EqualTo(0f));
        Assert.That(CloudSheetLayout.CoverageAlpha(0, 5f, cap), Is.EqualTo(1f));
        Assert.That(CloudSheetLayout.CoverageAlpha(cap, 1f, cap), Is.EqualTo(0f));
    }

    // The tick a sheet entered on is in the past, and stays put for as long as the sheet is on its
    // current crossing. If it moved, the cover read through it would move too, and a cloud's existence
    // would be back to being decided while it is on screen.
    [Test]
    public void TheEntryTickIsFixedForAWholeCrossing()
    {
        for (int index = 0; index < CloudSheetLayout.ShippedSheetCap; index++)
        {
            int entry = CloudSheetLayout.EntryTickFor(index, Seed, 500000, LowDeckOnly);
            Assert.That(entry, Is.LessThanOrEqualTo(500000), $"sheet {index} entered in the future");

            int previousEntry = entry;
            float previousX = Place(index, 500000).CenterX;
            for (int ticks = 500001; ticks < 500000 + CloudSheetLayout.BaseCrossingTicks * 2; ticks += 7)
            {
                int now = CloudSheetLayout.EntryTickFor(index, Seed, ticks, LowDeckOnly);
                float x = Place(index, ticks).CenterX;
                bool wrapped = x < previousX;

                Assert.That(now == previousEntry || wrapped, Is.True,
                    $"sheet {index} changed entry tick at {ticks} without wrapping");

                previousEntry = now;
                previousX = x;
            }
        }
    }

    // THE PROPERTY THE WHOLE LATCH EXISTS FOR: with cover read at the entry tick, a sheet's coverage
    // weight can only change while that sheet is entirely off the map. Driven by a deliberately
    // vicious cover signal — one that swings across every threshold several times a crossing —
    // because a gentle one would pass even if the latch were not there.
    [Test]
    public void ACoverChangeCanOnlyTakeEffectWhileTheSheetIsOffMap()
    {
        static float CoverAt(int tick) =>
            0.5f + 0.5f * MathF.Sin(tick * 0.0013f) * MathF.Cos(tick * 0.00021f);

        int cap = CloudSheetLayout.ShippedSheetCap;
        int changes = 0;

        for (int index = 0; index < cap; index++)
        {
            float previous = CloudSheetLayout.CoverageAlpha(
                index, CoverAt(CloudSheetLayout.EntryTickFor(index, Seed, 0, LowDeckOnly)), cap);

            for (int ticks = 1; ticks < CloudSheetLayout.BaseCrossingTicks * 3; ticks++)
            {
                float now = CloudSheetLayout.CoverageAlpha(
                    index, CoverAt(CloudSheetLayout.EntryTickFor(index, Seed, ticks, LowDeckOnly)), cap);

                if (now != previous)
                {
                    changes++;
                    CloudSheetLayout.Placement placement = Place(index, ticks);
                    Assert.That(CloudSheetLayout.OnScreen(placement, MapX, MapZ), Is.False,
                        $"sheet {index} changed weight at tick {ticks} while on screen "
                        + $"({previous} -> {now}, centre {placement.CenterX})");
                }

                previous = now;
            }
        }

        // Otherwise the assertion above is satisfied by a signal that never moved: a latch that froze
        // every sheet's weight forever would pass a test that only checks WHERE changes happen.
        Assert.That(changes, Is.GreaterThan(cap),
            "the cover signal never reached the sheets, so nothing was actually tested");
    }
}
