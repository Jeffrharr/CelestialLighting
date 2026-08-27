namespace CelestialLighting.Tests;

// Offline coverage for the one-time "what's new" notice's policy half (Source/UpdateNoticeMath.cs),
// linked into this project via <Compile Include> so these exercise the exact code that ships.
//
// EVERY FAILURE THIS FILE GUARDS AGAINST IS INVISIBLE. A notice that shows twice, or shows to
// somebody on their first ever boot, or announces a feature that is not actually running, all look
// identical in a screenshot to one that works — the difference is a boolean that nobody sees until
// a player complains. So the cases here are written as the requirement rather than as coverage:
// once, only to a player who had the mod before, never relighting an existing colony without being
// told to, and never a claim about the screen that the screen does not support.
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

    // The population the notice exists for: had the mod before, has not answered yet.
    [Test]
    public void AnUpgradingInstallIsShownTheNotice()
    {
        Assert.That(
            UpdateNoticeMath.ShouldShow(installedBefore: true, UpdateNoticeMath.NeverAcknowledged),
            Is.True);
    }

    // A FIRST-TIME INSTALL IS NEVER SHOWN IT, whatever it has acknowledged. Nothing in this mod is an
    // "update" to somebody for whom every effect arrived at the same moment, and the one feature the
    // window would offer them is switched on for them instead (FirstRunSwitches).
    [TestCase(0)]
    [TestCase(1)]
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

    // THE FIRST-RUN SEED HAS TO SUPPRESS THE NOTICE ON EVERY LATER BOOT, and this is the case that
    // makes AcknowledgeOnFirstRun necessary rather than decorative. A player who installs fresh stops
    // looking like a first install the moment they open and close the settings screen — that is when
    // RimWorld writes the file. Without the seeded acknowledgement, the boot after that reads as a
    // returning player and tells them a feature they have always had is new.
    [Test]
    public void TheFirstRunSeedSuppressesTheNoticeOnceThePlayerLooksLikeAnUpgrade()
    {
        int seeded = UpdateNoticeMath.AcknowledgeOnFirstRun();
        Assert.That(UpdateNoticeMath.ShouldShow(installedBefore: true, seeded), Is.False);
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

    // --- The two populations get different answers about vector lighting ---

    // A NEW INSTALL GETS IT ON. Somebody installing today has no prior expectation to violate, so
    // this is simply how the mod lights a colony.
    [Test]
    public void ANewInstallStartsWithVectorLightingOn()
    {
        Assert.That(UpdateNoticeMath.FirstRunSwitches(Typical(vectorLights: false)).VectorLights,
            Is.True);
    }

    // THE SEED TOUCHES NOTHING ELSE. It is the new-install default for one switch, not a "turn
    // everything on" path — the cloud switches and the two reachability facts have to come through
    // exactly as they went in, or a first boot would silently override defaults set elsewhere.
    [Test]
    public void TheFirstRunSeedMovesNothingButVectorLighting()
    {
        UpdateNoticeSwitches before = Typical(cloudCover: false, cloudSheet: true, cloudVolume: false,
            shaderLoaded: false, externalClouds: true);
        UpdateNoticeSwitches seeded = UpdateNoticeMath.FirstRunSwitches(before);

        Assert.That(seeded.CloudCover, Is.EqualTo(before.CloudCover));
        Assert.That(seeded.CloudSheet, Is.EqualTo(before.CloudSheet));
        Assert.That(seeded.CloudVolume, Is.EqualTo(before.CloudVolume));
        Assert.That(seeded.CloudVolumeShaderLoaded, Is.EqualTo(before.CloudVolumeShaderLoaded));
        Assert.That(seeded.ExternalCloudsInstalled, Is.EqualTo(before.ExternalCloudsInstalled));
    }

    // AN EXISTING COLONY IS NEVER RELIT WITHOUT AN EXPLICIT YES, which is the half of the asymmetry
    // that matters. Apply is the ONLY way the switch moves for this population, and it needs a true
    // handed to it — a decline leaves them exactly where they were.
    [Test]
    public void AnUpgradeNeverGetsVectorLightingWithoutSayingYes()
    {
        UpdateNoticeSwitches switches = Typical(vectorLights: false);

        Assert.That(UpdateNoticeMath.Apply(switches, enableVectorLights: false).VectorLights, Is.False);
        // And they are actually shown the offer, which is what makes the line above a choice rather
        // than a feature nobody can reach.
        Assert.That(UpdateNoticeMath.VectorLightRow(switches), Is.EqualTo(UpdateNoticeRow.Offer));
        Assert.That(UpdateNoticeMath.AnythingToShow(switches), Is.True);
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
        Assert.That(UpdateNoticeMath.VolumetricCloudRow(Typical()),
            Is.EqualTo(UpdateNoticeRow.Announce));
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
                Typical(cloudCover: cover, cloudSheet: sheet, cloudVolume: volume)),
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
        Assert.That(UpdateNoticeMath.VolumetricCloudRow(switches),
            Is.EqualTo(UpdateNoticeRow.Hidden));
    }

    // No shader, no volumetric path — the sheets fall back to the baked atlas and the feature does
    // not exist on this machine.
    [Test]
    public void VolumetricCloudsAreHiddenWithoutTheShader()
    {
        Assert.That(UpdateNoticeMath.VolumetricCloudRow(Typical(shaderLoaded: false)),
            Is.EqualTo(UpdateNoticeRow.Hidden));
    }

    // With the Clouds mod installed our positional lanes stand down, the drawn sheets included, so
    // there is nothing for the volumetric renderer to render.
    [Test]
    public void VolumetricCloudsAreHiddenWhenAnotherModOwnsTheDeck()
    {
        Assert.That(UpdateNoticeMath.VolumetricCloudRow(Typical(externalClouds: true)),
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
        Assert.That(UpdateNoticeMath.AnythingToShow(Typical(vectorLights: true)), Is.True);
    }

    // A hidden feature is not an offer — the notice must not put an enable button under something it
    // never drew.
    [Test]
    public void AHiddenFeatureIsNotAnOffer()
    {
        UpdateNoticeSwitches switches = Typical(vectorLights: true, shaderLoaded: false);
        Assert.That(UpdateNoticeMath.AnyOffer(switches), Is.False);
        Assert.That(UpdateNoticeMath.AnythingToShow(switches), Is.False);
    }

    // The window is not worth raising with nothing in it — it would spend the one appearance this
    // notice gets and say nothing. That state is reachable: vector lighting already on plus a
    // machine with no shader bundle, which is every Mac and Windows install today.
    [Test]
    public void AWindowWithNothingToSayIsNotRaised()
    {
        Assert.That(
            UpdateNoticeMath.AnythingToShow(Typical(vectorLights: true, externalClouds: true)),
            Is.False);
        Assert.That(UpdateNoticeMath.AnythingToShow(Typical(vectorLights: false, shaderLoaded: false)),
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
