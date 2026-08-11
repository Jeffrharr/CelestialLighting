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
        Assert.That(CloudSheetLayout.SheetCount(1f), Is.EqualTo(CloudSheetLayout.MaxSheets));

        int previous = 0;
        for (float fraction = 0f; fraction <= 1f; fraction += 0.05f)
        {
            int count = CloudSheetLayout.SheetCount(fraction);
            Assert.That(count, Is.GreaterThanOrEqualTo(previous), $"went backwards at {fraction}");
            Assert.That(count, Is.LessThanOrEqualTo(CloudSheetLayout.MaxSheets));
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
}
