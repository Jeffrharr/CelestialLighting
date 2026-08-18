namespace CelestialLighting.Tests;

// Offline coverage for the Clouds interop's policy half (Source/CloudsCompatMath.cs), linked into
// this project via <Compile Include> so these exercise the exact code that ships.
//
// The point of testing something this small is that the arithmetic is not what can go wrong here —
// the TABLE is. "Which of our six cloud lanes give way to another mod's clouds" is a design ruling,
// and the way it breaks is a seventh lane being added later and quietly inheriting the wrong half of
// it. These cases are the ruling written down.
[TestFixture]
public class CloudsCompatMathTests
{
    // The three lanes whose appearance depends on where WE decided a cloud is. With someone else's
    // clouds overhead these are the ones that make a visibly false claim — a shadow under clear sky,
    // a warm patch on the ground with nothing above it, a second set of cloud shapes entirely.
    [TestCase(CloudLane.UnderlightLayer, TestName = "TheUnderlitCloudLayerIsPositional")]
    [TestCase(CloudLane.GroundShadow, TestName = "TheDaylightCloudShadowIsPositional")]
    [TestCase(CloudLane.DrawnSheet, TestName = "TheDrawnSheetIsPositional")]
    public void PositionalLanes(CloudLane lane)
    {
        Assert.That(CloudsCompatMath.LaneIsPositional(lane), Is.True);
    }

    // The three that are statements about the sky as a whole. A greyer, less saturated afternoon is
    // still a correct description of a 40%-cloudy afternoon no matter which mod draws the shapes, and
    // Clouds has no sky-colour opinion of its own to disagree with it.
    [TestCase(CloudLane.SkyTint, TestName = "TheClearDaySkyTintIsNotPositional")]
    [TestCase(CloudLane.CoverLabel, TestName = "TheCloudinessLabelIsNotPositional")]
    [TestCase(CloudLane.ColourTemperature, TestName = "TheColourTemperatureScalingIsNotPositional")]
    public void NonPositionalLanes(CloudLane lane)
    {
        Assert.That(CloudsCompatMath.LaneIsPositional(lane), Is.False);
    }

    // Nothing changes for the overwhelming majority of players: without Clouds installed, every lane
    // does exactly what its own feature flag says and the interop is invisible.
    [TestCase(CloudLane.SkyTint)]
    [TestCase(CloudLane.CoverLabel)]
    [TestCase(CloudLane.ColourTemperature)]
    [TestCase(CloudLane.UnderlightLayer)]
    [TestCase(CloudLane.GroundShadow)]
    [TestCase(CloudLane.DrawnSheet)]
    public void WithoutCloudsEveryLaneFollowsItsOwnFlag(CloudLane lane)
    {
        Assert.That(CloudsCompatMath.LaneDraws(lane, featureEnabled: true, externalCloudsDrawn: false),
            Is.True);
        Assert.That(CloudsCompatMath.LaneDraws(lane, featureEnabled: false, externalCloudsDrawn: false),
            Is.False);
    }

    // The interop itself: with Clouds installed the positional three stop and the rest carry on.
    [TestCase(CloudLane.UnderlightLayer, false, TestName = "CloudsTakesTheUnderlitLayer")]
    [TestCase(CloudLane.GroundShadow, false, TestName = "CloudsTakesTheGroundShadow")]
    [TestCase(CloudLane.DrawnSheet, false, TestName = "CloudsTakesTheDrawnSheet")]
    [TestCase(CloudLane.SkyTint, true, TestName = "CloudsLeavesTheSkyTint")]
    [TestCase(CloudLane.CoverLabel, true, TestName = "CloudsLeavesTheCloudinessLabel")]
    [TestCase(CloudLane.ColourTemperature, true, TestName = "CloudsLeavesTheColourTemperature")]
    public void WithCloudsOnlyPositionalLanesStandDown(CloudLane lane, bool expected)
    {
        Assert.That(CloudsCompatMath.LaneDraws(lane, featureEnabled: true, externalCloudsDrawn: true),
            Is.EqualTo(expected));
    }

    // THE DIRECTION IS ONE-WAY, and this is the case worth pinning rather than the ones above. The
    // interop may only ever take a lane from on to off; it must never turn something on that the
    // player switched off, which is the failure mode a "use theirs instead of ours" swap invites if
    // it is ever written as a branch rather than as an AND.
    [TestCase(CloudLane.SkyTint)]
    [TestCase(CloudLane.CoverLabel)]
    [TestCase(CloudLane.ColourTemperature)]
    [TestCase(CloudLane.UnderlightLayer)]
    [TestCase(CloudLane.GroundShadow)]
    [TestCase(CloudLane.DrawnSheet)]
    public void CloudsNeverRevivesADisabledLane(CloudLane lane)
    {
        Assert.That(CloudsCompatMath.LaneDraws(lane, featureEnabled: false, externalCloudsDrawn: true),
            Is.False);
    }
}
