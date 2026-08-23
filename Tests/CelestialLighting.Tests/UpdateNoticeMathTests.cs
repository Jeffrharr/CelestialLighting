namespace CelestialLighting.Tests;

// Offline coverage for the one-time "what's new" notice's policy half (Source/UpdateNoticeMath.cs),
// linked into this project via <Compile Include> so these exercise the exact code that ships.
//
// EVERY FAILURE THIS FILE GUARDS AGAINST IS INVISIBLE. A notice that shows twice, or shows to
// somebody on their first ever boot, or announces a feature that is not actually running, all look
// identical in a screenshot to one that works — the difference is a boolean that nobody sees until
// a player complains. So the cases here are written as the requirement rather than as coverage:
// once, to every install, never opting anybody into the expensive feature, and never a claim about
// the screen that the screen does not support.
[TestFixture]
public class UpdateNoticeMathTests
{
    // Convenience: the switch set as an existing player who has never touched the cloud settings
    // would have it after this update — clouds on by their defaults, vector lighting off by its
    // default, and a machine where the volumetric path is genuinely reachable.
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

    // EVERY INSTALL IS ASKED, new or upgrading, and the population is no longer an input at all.
    // Vector lighting is the most expensive thing the mod does, so nobody is opted into it silently
    // — including the first-time install an earlier cut of this deliberately skipped.
    [Test]
    public void EveryUnansweredInstallIsShownTheNotice()
    {
        Assert.That(UpdateNoticeMath.ShouldShow(UpdateNoticeMath.NeverAcknowledged), Is.True);
    }

    // THE "ONLY ONCE" REQUIREMENT. Having answered is having answered — the notice does not come
    // back for a player who said no, which is the failure the requirement actually names (saying yes
    // changes the settings, so a repeat would at least look deliberate; saying no changes nothing,
    // so a repeat looks like the mod ignoring them).
    [Test]
    public void AnAnsweredNoticeIsNeverShownAgain()
    {
        int acknowledged = UpdateNoticeMath.Acknowledge(UpdateNoticeMath.NeverAcknowledged);
        Assert.That(UpdateNoticeMath.ShouldShow(acknowledged), Is.False);
    }

    // A player who ran a later build and rolled back keeps their higher mark. Without the guard in
    // Acknowledge this clamps down to the current version and re-shows a notice they answered on the
    // newer build — a small case, but the one that turns "only once" into "once per downgrade".
    [Test]
    public void AcknowledgementNeverMovesBackwards()
    {
        int fromTheFuture = UpdateNoticeMath.CurrentNoticeVersion + 5;
        Assert.That(UpdateNoticeMath.Acknowledge(fromTheFuture), Is.EqualTo(fromTheFuture));
        Assert.That(UpdateNoticeMath.ShouldShow(fromTheFuture), Is.False);
    }

    // A first install answers the notice like anybody else and the acknowledgement it writes has to
    // suppress it on every later boot — including the boot after that player first opens and closes
    // the settings screen, which is the moment they stop looking like a first install.
    [Test]
    public void AFirstInstallsOwnAnswerSuppressesTheNoticeForever()
    {
        int acknowledged = UpdateNoticeMath.Acknowledge(UpdateNoticeMath.NeverAcknowledged);
        Assert.That(UpdateNoticeMath.ShouldShow(acknowledged), Is.False);
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

    // --- Nobody is opted into vector lighting ---

    // THE REQUIREMENT THAT REPLACED THE NEW-INSTALL DEFAULT. Vector lighting is the mod's most
    // expensive feature, so no population gets it without saying yes — and this is the test that
    // would fail if a seeding path were ever reintroduced, because Apply is the ONLY way the switch
    // moves and it needs an explicit true.
    [TestCase(true, TestName = "AnUpgradeNeverGetsVectorLightingWithoutSayingYes")]
    [TestCase(false, TestName = "ANewInstallNeverGetsVectorLightingWithoutSayingYes")]
    public void NobodyGetsVectorLightingWithoutSayingYes(bool installedBefore)
    {
        UpdateNoticeSwitches switches = Typical(vectorLights: false);

        Assert.That(UpdateNoticeMath.Apply(switches, enableVectorLights: false).VectorLights, Is.False);
        // And both populations are actually shown the offer, which is what makes the line above a
        // choice rather than a feature nobody can reach.
        Assert.That(UpdateNoticeMath.VectorLightRow(switches), Is.EqualTo(UpdateNoticeRow.Offer));
        Assert.That(UpdateNoticeMath.AnythingToShow(switches, installedBefore), Is.True);
    }

    // --- The vector-light row: the only one with a button ---

    [Test]
    public void VectorLightsAreOfferedWhenOff()
    {
        Assert.That(UpdateNoticeMath.VectorLightRow(Typical(vectorLights: false)),
            Is.EqualTo(UpdateNoticeRow.Offer));
    }

    // Somebody who found the switch themselves between installing the update and seeing the notice.
    // NOT MENTIONED, rather than offered or announced: an offer whose acceptance changes nothing is
    // a lie about what the button did, and announcing it is the mod informing a player of a decision
    // they took minutes ago. It is also what lets AnythingToShow mean something below.
    [Test]
    public void VectorLightsAreHiddenWhenAlreadyOn()
    {
        Assert.That(UpdateNoticeMath.VectorLightRow(Typical(vectorLights: true)),
            Is.EqualTo(UpdateNoticeRow.Hidden));
    }

    // --- The volumetric-cloud row: announced or not mentioned, never offered ---

    // The default case for a returning player: all three cloud switches sit at their shipped
    // defaults, so the volumetric renderer is already running and the notice is telling them what
    // changed.
    [Test]
    public void VolumetricCloudsAreAnnouncedWhenTheWholeChainIsOn()
    {
        Assert.That(UpdateNoticeMath.VolumetricCloudRow(Typical(), installedBefore: true),
            Is.EqualTo(UpdateNoticeRow.Announce));
    }

    // A FIRST-TIME INSTALL IS TOLD NOTHING ABOUT CLOUDS, even with the whole chain running. Nothing
    // in this mod is "new" to somebody who has never run it — every effect arrived at the same
    // moment — so singling one out as a recent addition is a sentence that means nothing to them.
    // This is now the only thing `installedBefore` decides.
    [Test]
    public void AFirstTimeInstallIsNotToldAboutClouds()
    {
        Assert.That(UpdateNoticeMath.VolumetricCloudRow(Typical(), installedBefore: false),
            Is.EqualTo(UpdateNoticeRow.Hidden));
        // ...while the same switches on an upgrade do get the announcement, which is what makes the
        // line above a statement about the population rather than about the cloud settings.
        Assert.That(UpdateNoticeMath.VolumetricCloudRow(Typical(), installedBefore: true),
            Is.EqualTo(UpdateNoticeRow.Announce));
    }

    // And a first install with vector lighting already on has nothing left to say, so no window is
    // raised at all. Only reachable by hand-editing the config before first boot, but it is the case
    // that would otherwise put an empty modal in front of a brand-new player.
    [Test]
    public void AFirstTimeInstallWithNothingToOfferShowsNothing()
    {
        Assert.That(UpdateNoticeMath.AnythingToShow(Typical(vectorLights: true), installedBefore: false),
            Is.False);
    }

    // THE CLOUD ROW IS NEVER AN OFFER, whatever the switches say. That is a design ruling, not an
    // accident of the current values: switching "Partial cloud cover" or "Visible clouds" back on
    // would override a decision the player made deliberately, for a rendering change to clouds they
    // have already said they do not want to see.
    [TestCase(true, true, true)]
    [TestCase(false, true, true)]
    [TestCase(true, false, true)]
    [TestCase(true, true, false)]
    [TestCase(false, false, false)]
    public void VolumetricCloudsAreNeverOffered(bool cover, bool sheet, bool volume)
    {
        Assert.That(
            UpdateNoticeMath.VolumetricCloudRow(
                Typical(cloudCover: cover, cloudSheet: sheet, cloudVolume: volume),
                installedBefore: true),
            Is.Not.EqualTo(UpdateNoticeRow.Offer));
    }

    // ANY LINK BROKEN MEANS NOT RUNNING, AND THEREFORE NOT MENTIONED. This is the case the pure core
    // exists for: with partial cloud cover or the drawn sheets switched off there are no sheets to
    // march through, so announcing the volumetric renderer would send the player looking at a sky
    // they have switched off.
    [TestCase(false, true, true, TestName = "PartialCloudCoverOffBreaksTheChain")]
    [TestCase(true, false, true, TestName = "VisibleCloudsOffBreaksTheChain")]
    [TestCase(true, true, false, TestName = "TheVolumetricSwitchItselfOffBreaksTheChain")]
    [TestCase(false, false, false, TestName = "EveryCloudSwitchOffBreaksTheChain")]
    public void VolumetricCloudsAreHiddenWheneverTheChainIsIncomplete(bool cover, bool sheet, bool volume)
    {
        UpdateNoticeSwitches switches =
            Typical(cloudCover: cover, cloudSheet: sheet, cloudVolume: volume);

        Assert.That(UpdateNoticeMath.VolumetricCloudsRunning(switches), Is.False);
        Assert.That(UpdateNoticeMath.VolumetricCloudRow(switches, installedBefore: true),
            Is.EqualTo(UpdateNoticeRow.Hidden));
    }

    // No shader, no volumetric path — the sheets fall back to the baked atlas and the feature does
    // not exist on this machine.
    [Test]
    public void VolumetricCloudsAreHiddenWithoutTheShader()
    {
        Assert.That(UpdateNoticeMath.VolumetricCloudRow(Typical(shaderLoaded: false), installedBefore: true),
            Is.EqualTo(UpdateNoticeRow.Hidden));
    }

    // With the Clouds mod installed our positional lanes stand down, the drawn sheets included, so
    // there is nothing for the volumetric renderer to render.
    [Test]
    public void VolumetricCloudsAreHiddenWhenAnotherModOwnsTheDeck()
    {
        Assert.That(UpdateNoticeMath.VolumetricCloudRow(Typical(externalClouds: true), installedBefore: true),
            Is.EqualTo(UpdateNoticeRow.Hidden));
    }

    // UNREACHABILITY OUTRANKS THE SWITCHES. A Clouds user with all three of our cloud switches on is
    // not seeing volumetric clouds and must not be told they are; those settings are still live and
    // still mean something the moment they uninstall it, which is exactly why reading them alone
    // would be wrong.
    [TestCase(true, true, true, TestName = "AllCloudSwitchesOnIsStillNotRunning")]
    [TestCase(false, false, false, TestName = "AllCloudSwitchesOffIsStillNotRunning")]
    public void UnreachabilityIsDecidedBeforeTheSwitches(bool cover, bool sheet, bool volume)
    {
        Assert.That(
            UpdateNoticeMath.VolumetricCloudsRunning(
                Typical(cloudCover: cover, cloudSheet: sheet, cloudVolume: volume, externalClouds: true)),
            Is.False);
        Assert.That(
            UpdateNoticeMath.VolumetricCloudsRunning(
                Typical(cloudCover: cover, cloudSheet: sheet, cloudVolume: volume, shaderLoaded: false)),
            Is.False);
    }

    // --- AnyOffer / AnythingToShow: whether the window is a question, a note, or not raised ---

    [Test]
    public void ThereIsSomethingToAskWhileVectorLightsAreOff()
    {
        Assert.That(UpdateNoticeMath.AnyOffer(Typical(vectorLights: false)), Is.True);
    }

    // Both features already running: nothing to ask, but the cloud row is still worth saying, so the
    // window is raised with a single OK button rather than an enable button it cannot act on.
    [Test]
    public void ThereIsNothingToAskWhenBothFeaturesAreAlreadyRunning()
    {
        Assert.That(UpdateNoticeMath.AnyOffer(Typical(vectorLights: true)), Is.False);
        Assert.That(UpdateNoticeMath.AnythingToShow(Typical(vectorLights: true), installedBefore: true), Is.True);
    }

    // A hidden feature is not an offer — the notice must not put an enable button under something it
    // never drew.
    [Test]
    public void AHiddenFeatureIsNotAnOffer()
    {
        UpdateNoticeSwitches switches = Typical(vectorLights: true, shaderLoaded: false);
        Assert.That(UpdateNoticeMath.AnyOffer(switches), Is.False);
        Assert.That(UpdateNoticeMath.AnythingToShow(switches, installedBefore: true), Is.False);
    }

    // The window is not worth raising with nothing in it — it would spend the one appearance this
    // notice gets and say nothing. That state is reachable: vector lighting already on plus a
    // machine with no shader bundle, which is every Mac and Windows install today.
    [Test]
    public void AWindowWithNothingToSayIsNotRaised()
    {
        Assert.That(
            UpdateNoticeMath.AnythingToShow(Typical(vectorLights: true, externalClouds: true), installedBefore: true),
            Is.False);
        Assert.That(UpdateNoticeMath.AnythingToShow(Typical(vectorLights: false, shaderLoaded: false), installedBefore: true),
            Is.True, "an offerable vector-light row is on its own enough to raise the window");
    }

    // --- Apply: what the button does ---

    [Test]
    public void PressingTheButtonTurnsVectorLightingOn()
    {
        Assert.That(
            UpdateNoticeMath.Apply(Typical(vectorLights: false), enableVectorLights: true).VectorLights,
            Is.True);
    }

    // Declining leaves the player exactly where they were. Not "sets it false" — a decline is not a
    // disable, and this is the case that breaks if Apply is ever written as a plain assignment.
    [Test]
    public void DecliningNeverTurnsAnythingOff()
    {
        UpdateNoticeSwitches before = Typical(vectorLights: true);
        UpdateNoticeSwitches applied = UpdateNoticeMath.Apply(before, enableVectorLights: false);

        Assert.That(applied.VectorLights, Is.EqualTo(before.VectorLights));
        Assert.That(applied.CloudCover, Is.EqualTo(before.CloudCover));
        Assert.That(applied.CloudSheet, Is.EqualTo(before.CloudSheet));
        Assert.That(applied.CloudVolume, Is.EqualTo(before.CloudVolume));
    }

    // THE NOTICE NEVER TOUCHES THE CLOUD SWITCHES, in either direction. The cloud row has no button,
    // so an answer that moved them would be moving something the player was never shown a control
    // for — the failure mode of a "turn on everything new" button written without this constraint.
    [TestCase(true)]
    [TestCase(false)]
    public void AnAnswerNeverMovesTheCloudSwitches(bool enable)
    {
        UpdateNoticeSwitches before = Typical(cloudCover: false, cloudSheet: false, cloudVolume: false);
        UpdateNoticeSwitches applied = UpdateNoticeMath.Apply(before, enableVectorLights: enable);

        Assert.That(applied.CloudCover, Is.False);
        Assert.That(applied.CloudSheet, Is.False);
        Assert.That(applied.CloudVolume, Is.False);
    }

    // The two reachability facts are properties of the machine, not preferences, so an answer must
    // carry them through untouched — otherwise a result fed back into VolumetricCloudsRunning would
    // report a feature as live on a machine that cannot run it.
    [Test]
    public void ApplyCarriesTheReachabilityFactsThrough()
    {
        UpdateNoticeSwitches applied = UpdateNoticeMath.Apply(
            Typical(shaderLoaded: false, externalClouds: true), enableVectorLights: true);

        Assert.That(applied.CloudVolumeShaderLoaded, Is.False);
        Assert.That(applied.ExternalCloudsInstalled, Is.True);
    }
}
