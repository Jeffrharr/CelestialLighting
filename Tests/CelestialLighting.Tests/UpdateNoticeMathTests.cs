namespace CelestialLighting.Tests;

// Offline coverage for the one-time "what's new" notice's policy half (Source/UpdateNoticeMath.cs),
// linked into this project via <Compile Include> so these exercise the exact code that ships.
//
// EVERY FAILURE THIS FILE GUARDS AGAINST IS INVISIBLE. A notice that shows twice, or shows to
// somebody on their first ever boot, or offers a feature that then renders nothing, all look
// identical in a screenshot to one that works — the difference is a boolean that nobody sees until
// a player complains. So the cases here are written as the requirement rather than as coverage:
// once, never to a new install, and never an offer that cannot be delivered.
[TestFixture]
public class UpdateNoticeMathTests
{
    // Convenience: the switch set as an existing player who has never touched the cloud settings
    // would have it after this update — clouds on by their defaults, §27 off by its default, and a
    // machine where the volumetric path is genuinely reachable.
    private static UpdateNoticeSwitches Typical(
        bool vectorLights = false,
        bool cloudCover = true,
        bool cloudSheet = true,
        bool cloudVolume = true,
        bool shaderLoaded = true,
        bool externalClouds = false) =>
        new UpdateNoticeSwitches(vectorLights, cloudCover, cloudSheet, cloudVolume, shaderLoaded,
            externalClouds);

    // --- ShouldShow: who sees it, and how many times ---

    // The population the notice is for: somebody who already had the mod, and has not answered yet.
    [Test]
    public void AnUpgradingPlayerIsShownTheNotice()
    {
        Assert.That(
            UpdateNoticeMath.ShouldShow(installedBefore: true, UpdateNoticeMath.NeverAcknowledged),
            Is.True);
    }

    // THE "NOT FOR NEW GAMES" REQUIREMENT. A first-ever install has nothing to be updated about, and
    // this must hold whatever the acknowledgement happens to say — including the un-persisted zero a
    // brand-new settings object carries, which is the same value an ancient config reads back as.
    // That collision is precisely why the previous-install signal exists as a second input.
    [TestCase(UpdateNoticeMath.NeverAcknowledged)]
    [TestCase(UpdateNoticeMath.CurrentNoticeVersion)]
    public void AFirstTimeInstallIsNeverShownTheNotice(int acknowledged)
    {
        Assert.That(UpdateNoticeMath.ShouldShow(installedBefore: false, acknowledged), Is.False);
    }

    // THE "ONLY ONCE" REQUIREMENT. Having answered is having answered — the notice does not come
    // back for a player who said no, which is the failure the requirement actually names (saying yes
    // changes the settings, so a repeat would at least look deliberate; saying no changes nothing,
    // so a repeat looks like the mod ignoring them).
    [Test]
    public void AnAnsweredNoticeIsNeverShownAgain()
    {
        int acknowledged = UpdateNoticeMath.Acknowledge(UpdateNoticeMath.NeverAcknowledged);
        Assert.That(UpdateNoticeMath.ShouldShow(installedBefore: true, acknowledged), Is.False);
    }

    // A player who ran a later build and rolled back keeps their higher mark. Without the guard in
    // Acknowledge this clamps down to the current version and re-shows a notice they answered on the
    // newer build — a small case, but the one that turns "only once" into "once per downgrade".
    [Test]
    public void AcknowledgementNeverMovesBackwards()
    {
        int fromTheFuture = UpdateNoticeMath.CurrentNoticeVersion + 5;
        Assert.That(UpdateNoticeMath.Acknowledge(fromTheFuture), Is.EqualTo(fromTheFuture));
        Assert.That(UpdateNoticeMath.ShouldShow(installedBefore: true, fromTheFuture), Is.False);
    }

    // The value written for an install that is never shown the notice, which has to be high enough
    // to suppress it on every later boot — including the boot after that player first opens the
    // settings screen and thereby gains the settings file that makes them look like a returning one.
    [Test]
    public void AFirstRunAcknowledgementSuppressesTheNoticeForever()
    {
        int acknowledged = UpdateNoticeMath.AcknowledgeOnFirstRun();
        Assert.That(UpdateNoticeMath.ShouldShow(installedBefore: true, acknowledged), Is.False);
    }

    // NeverAcknowledged has to be the scribed default, and it has to sort below the current notice.
    // Both are properties a later release could break by renumbering, and neither shows up anywhere
    // else — a notice numbered 0 would simply never appear again for anybody.
    [Test]
    public void TheCurrentNoticeSortsAboveNeverAcknowledged()
    {
        Assert.That(UpdateNoticeMath.NeverAcknowledged, Is.EqualTo(0));
        Assert.That(UpdateNoticeMath.CurrentNoticeVersion,
            Is.GreaterThan(UpdateNoticeMath.NeverAcknowledged));
    }

    // --- The vector-light row ---

    [Test]
    public void VectorLightsAreOfferedWhenOff()
    {
        Assert.That(UpdateNoticeMath.VectorLightOffer(Typical(vectorLights: false)),
            Is.EqualTo(UpdateNoticeOffer.OfferToEnable));
    }

    // Somebody who found the switch themselves between installing the update and seeing the notice.
    // Announced, not offered — an offer whose acceptance changes nothing is a lie about what the
    // button did.
    [Test]
    public void VectorLightsAreAnnouncedWhenAlreadyOn()
    {
        Assert.That(UpdateNoticeMath.VectorLightOffer(Typical(vectorLights: true)),
            Is.EqualTo(UpdateNoticeOffer.AlreadyOn));
    }

    // --- The volumetric-cloud row, which is the one with a chain above it ---

    // The default case for a returning player: all three cloud switches sit at their shipped
    // defaults, so §25c is already running and the notice is telling them what changed, not asking.
    [Test]
    public void VolumetricCloudsAreAnnouncedWhenTheWholeChainIsOn()
    {
        Assert.That(UpdateNoticeMath.VolumetricCloudOffer(Typical()),
            Is.EqualTo(UpdateNoticeOffer.AlreadyOn));
    }

    // ANY LINK BROKEN MEANS NOT RUNNING. This is the case the pure core exists for: with partial
    // cloud cover or the drawn sheets switched off there are no sheets to march through, so
    // `cloudVolume == true` on its own is a setting that renders nothing, and reporting it as
    // "already on" would tell the player to go looking for a sky that is not there.
    [TestCase(false, true, true, TestName = "PartialCloudCoverOffBreaksTheChain")]
    [TestCase(true, false, true, TestName = "VisibleCloudsOffBreaksTheChain")]
    [TestCase(true, true, false, TestName = "TheVolumetricSwitchItselfOffBreaksTheChain")]
    [TestCase(false, false, false, TestName = "EveryCloudSwitchOffBreaksTheChain")]
    public void VolumetricCloudsAreOfferedWheneverTheChainIsIncomplete(bool cover, bool sheet, bool volume)
    {
        Assert.That(
            UpdateNoticeMath.VolumetricCloudOffer(
                Typical(cloudCover: cover, cloudSheet: sheet, cloudVolume: volume)),
            Is.EqualTo(UpdateNoticeOffer.OfferToEnable));
    }

    // No shader, no volumetric path — the sheets fall back to §25b's baked atlas and the feature does
    // not exist on this machine. Not mentioned at all rather than offered and quietly ineffective.
    [Test]
    public void VolumetricCloudsAreUnavailableWithoutTheShader()
    {
        Assert.That(UpdateNoticeMath.VolumetricCloudOffer(Typical(shaderLoaded: false)),
            Is.EqualTo(UpdateNoticeOffer.Unavailable));
    }

    // With the Clouds mod installed our positional lanes stand down, §25's sheets included, so there
    // is nothing for the volumetric renderer to render.
    [Test]
    public void VolumetricCloudsAreUnavailableWhenAnotherModOwnsTheDeck()
    {
        Assert.That(UpdateNoticeMath.VolumetricCloudOffer(Typical(externalClouds: true)),
            Is.EqualTo(UpdateNoticeOffer.Unavailable));
    }

    // UNREACHABILITY OUTRANKS THE SWITCHES, in both directions. A Clouds user with all three of our
    // cloud switches on is not seeing volumetric clouds, and must not be told they are; the switches
    // are still live and still mean something the moment they uninstall it, which is exactly why
    // reading them here would be wrong.
    [TestCase(true, true, true, TestName = "AllCloudSwitchesOnIsStillUnavailable")]
    [TestCase(false, false, false, TestName = "AllCloudSwitchesOffIsStillUnavailable")]
    public void UnreachabilityIsDecidedBeforeTheSwitches(bool cover, bool sheet, bool volume)
    {
        Assert.That(
            UpdateNoticeMath.VolumetricCloudOffer(
                Typical(cloudCover: cover, cloudSheet: sheet, cloudVolume: volume, externalClouds: true)),
            Is.EqualTo(UpdateNoticeOffer.Unavailable));
        Assert.That(
            UpdateNoticeMath.VolumetricCloudOffer(
                Typical(cloudCover: cover, cloudSheet: sheet, cloudVolume: volume, shaderLoaded: false)),
            Is.EqualTo(UpdateNoticeOffer.Unavailable));
    }

    // --- AnyOffer: whether the window is a question or an announcement ---

    [Test]
    public void ThereIsSomethingToAskWhileVectorLightsAreOff()
    {
        Assert.That(UpdateNoticeMath.AnyOffer(Typical(vectorLights: false)), Is.True);
    }

    // Both features already running, so the window has nothing to ask and draws a single OK button
    // rather than a yes/no it cannot act on.
    [Test]
    public void ThereIsNothingToAskWhenBothFeaturesAreAlreadyRunning()
    {
        Assert.That(UpdateNoticeMath.AnyOffer(Typical(vectorLights: true)), Is.False);
    }

    // An unavailable feature is not an offer either — the notice must not draw a tickbox for
    // something it cannot deliver just because the switch behind it happens to be off.
    [Test]
    public void AnUnavailableFeatureIsNotAnOffer()
    {
        UpdateNoticeSwitches switches =
            Typical(vectorLights: true, cloudSheet: false, shaderLoaded: false);
        Assert.That(UpdateNoticeMath.AnyOffer(switches), Is.False);
    }

    // --- Apply: what the answer does to the switches ---

    [Test]
    public void AcceptingVectorLightsTurnsThemOn()
    {
        UpdateNoticeSwitches applied = UpdateNoticeMath.Apply(
            Typical(vectorLights: false), enableVectorLights: true, enableVolumetricClouds: false);

        Assert.That(applied.VectorLights, Is.True);
    }

    // THE WHOLE POINT OF ROUTING THE ANSWER THROUGH A FUNCTION. Accepting the cloud offer has to
    // raise every link, not just the leaf — a player who turned partial cover off long ago and ticks
    // this box would otherwise get a settings screen that agrees with them and a sky that has not
    // changed.
    [Test]
    public void AcceptingVolumetricCloudsRaisesTheWholeChain()
    {
        UpdateNoticeSwitches applied = UpdateNoticeMath.Apply(
            Typical(cloudCover: false, cloudSheet: false, cloudVolume: false),
            enableVectorLights: false, enableVolumetricClouds: true);

        Assert.That(applied.CloudCover, Is.True);
        Assert.That(applied.CloudSheet, Is.True);
        Assert.That(applied.CloudVolume, Is.True);

        // And the chain is now complete, which is the property the player was actually promised.
        Assert.That(UpdateNoticeMath.VolumetricCloudOffer(applied),
            Is.EqualTo(UpdateNoticeOffer.AlreadyOn));
    }

    // Declining leaves the player exactly where they were. Not "sets them false" — a decline is not a
    // disable, and this is the case that breaks if Apply is ever written as a plain assignment.
    [Test]
    public void DecliningNeverTurnsAnythingOff()
    {
        UpdateNoticeSwitches before = Typical(vectorLights: true);
        UpdateNoticeSwitches applied = UpdateNoticeMath.Apply(
            before, enableVectorLights: false, enableVolumetricClouds: false);

        Assert.That(applied.VectorLights, Is.EqualTo(before.VectorLights));
        Assert.That(applied.CloudCover, Is.EqualTo(before.CloudCover));
        Assert.That(applied.CloudSheet, Is.EqualTo(before.CloudSheet));
        Assert.That(applied.CloudVolume, Is.EqualTo(before.CloudVolume));
    }

    // The two answers are independent: taking one feature must not drag the other in. They are
    // unrelated effects and the window offers them separately, so the arithmetic has to keep them
    // separate too.
    [Test]
    public void TheTwoAnswersDoNotBleedIntoEachOther()
    {
        UpdateNoticeSwitches cloudsOnly = UpdateNoticeMath.Apply(
            Typical(vectorLights: false, cloudSheet: false),
            enableVectorLights: false, enableVolumetricClouds: true);

        Assert.That(cloudsOnly.VectorLights, Is.False);
        Assert.That(cloudsOnly.CloudSheet, Is.True);

        UpdateNoticeSwitches lightsOnly = UpdateNoticeMath.Apply(
            Typical(vectorLights: false, cloudSheet: false),
            enableVectorLights: true, enableVolumetricClouds: false);

        Assert.That(lightsOnly.VectorLights, Is.True);
        Assert.That(lightsOnly.CloudSheet, Is.False);
    }

    // The two reachability facts are properties of the machine, not preferences, so an answer must
    // carry them through untouched — Apply's result is fed straight back to VolumetricCloudOffer in
    // the test above, and would report a false "already on" if these were dropped.
    [Test]
    public void ApplyCarriesTheReachabilityFactsThrough()
    {
        UpdateNoticeSwitches applied = UpdateNoticeMath.Apply(
            Typical(shaderLoaded: false, externalClouds: true),
            enableVectorLights: true, enableVolumetricClouds: true);

        Assert.That(applied.CloudVolumeShaderLoaded, Is.False);
        Assert.That(applied.ExternalCloudsInstalled, Is.True);
    }
}
