namespace CelestialLighting;

// The pure policy behind the one-time "what's new" notice (Source/Dialog_UpdateNotice.cs): given
// what the player already has switched on and what this machine can actually render, does the
// notice appear at all, what does it say about each new feature, and which switch does the one
// button in it move? No UnityEngine, no Verse — the live half is only "read the settings, draw a
// window, write the settings back".
//
// WHY THIS IS A SEPARATE FILE AND NOT THREE `if`s IN THE DIALOG. Every branch here is a way to get
// the notice WRONG in a manner nobody notices for a release: showing it to somebody installing for
// the first time, showing it twice, showing it forever because the acknowledgement never persisted,
// or announcing a feature that is not actually running on this machine. None of those can be caught
// by looking at a screenshot, and all of them are one boolean expression each, so they belong
// somewhere they can be enumerated as test cases. See DESIGN.md "Update notice".

// What the notice does about a given feature.
public enum UpdateNoticeRow
{
    // Not mentioned. Either the feature cannot run on this install, or the player has switched off
    // something upstream of it — in both cases naming it would send them looking for something that
    // is not on their screen, and the notice has spent its one appearance doing it.
    Hidden,

    // Named, with nothing to press. "Here is what is new" is half of what this window is for, and a
    // feature that arrived already switched on is exactly the one a player would otherwise never
    // learn the name of.
    Announce,

    // Named, with a button that turns it on. Exactly one feature is ever in this state — see
    // VectorLightRow.
    Offer,
}

// Every persisted switch the notice reads or writes, plus the two live facts that decide whether
// the volumetric path is reachable. Passed as one value rather than six parameters because the
// operations here return a whole new set, and threading six `out`s through a dialog's button
// handler is how half of a decision gets applied.
public readonly struct UpdateNoticeSwitches
{
    // The one switch this notice can move. Nothing upstream of it and no hardware requirement — the
    // shader path and the flat fallback both render something — so the switch alone decides.
    public readonly bool VectorLights;

    // The three-deep chain the volumetric renderer sits at the bottom of. `CloudVolume` alone
    // renders nothing: with `CloudSheet` off there are no sheets to march through, and with
    // `CloudCover` off there is no cloud at all (CloudLayers.SheetAlphaFor asks for both before it
    // asks for a fraction). READ-ONLY here — the notice never moves these, see VolumetricCloudRow.
    public readonly bool CloudCover;
    public readonly bool CloudSheet;
    public readonly bool CloudVolume;

    // CloudVolumeShader.ShaderLoaded — whether the custom shader is present, supported and is
    // actually ours rather than the default vanilla substitutes for a failed load. False on any
    // platform whose bundle is missing, where the sheets silently draw the baked atlas instead.
    public readonly bool CloudVolumeShaderLoaded;

    // CloudsCompat.ModIsInstalled — the Clouds mod hangs its own particle deck over the map, and
    // our positional cloud lanes stand down for it. With it installed our sheets never draw, so
    // there is nothing for the volumetric renderer to render.
    public readonly bool ExternalCloudsInstalled;

    public UpdateNoticeSwitches(bool vectorLights, bool cloudCover, bool cloudSheet, bool cloudVolume,
        bool cloudVolumeShaderLoaded, bool externalCloudsInstalled)
    {
        VectorLights = vectorLights;
        CloudCover = cloudCover;
        CloudSheet = cloudSheet;
        CloudVolume = cloudVolume;
        CloudVolumeShaderLoaded = cloudVolumeShaderLoaded;
        ExternalCloudsInstalled = externalCloudsInstalled;
    }

    // Same switches with vector lighting on. The one mutation this whole subsystem performs.
    public UpdateNoticeSwitches WithVectorLights() =>
        new UpdateNoticeSwitches(true, CloudCover, CloudSheet, CloudVolume, CloudVolumeShaderLoaded,
            ExternalCloudsInstalled);
}

public static class UpdateNoticeMath
{
    // The value a settings file that predates the notice reads back as. Scribe_Values omits any
    // value equal to its default, so this MUST be the scribed default: every config written by an
    // earlier release has no node at all, and "no node" has to mean "has not seen the notice".
    public const int NeverAcknowledged = 0;

    // Bumped when there is a NEW notice to show, not when this file changes. The notice is a
    // sequence, not a flag, so that a later release can show its own without resurrecting this one
    // for the players who already dismissed it.
    public const int VectorLightsAndVolumetricClouds = 1;

    public const int CurrentNoticeVersion = VectorLightsAndVolumetricClouds;

    // THE WHOLE SUPPRESSION RULE, and the two halves are there for different reasons.
    //
    // `installedBefore` is what keeps the notice off a first-time install: somebody who has never
    // run this mod has no "update" to be told about, and the features the notice names are simply
    // part of what they just installed. It is answered by whether a persisted settings file existed
    // when the game read one (CelestialLightingSettings.LoadedFromDisk) — the only durable trace an
    // earlier version of this mod leaves behind, since nothing it does is written into a save.
    //
    // SHOWN TO EVERY INSTALL, NEW OR UPGRADING, and that is a deliberate reversal of where this
    // started. The first cut suppressed it for a first-time install and switched vector lighting on
    // for them instead, on the reasoning that somebody installing today has no prior expectation to
    // violate. What that reasoning left out is COST: vector lighting is the most expensive thing this
    // mod does, by a wide margin as a share of its per-frame budget. A default that quietly spends a
    // player's frame budget is not the same kind of default as one that quietly changes a colour, and
    // nobody should be opted into the expensive one without being told it exists. So everybody is
    // asked, and the mod ships with it off for everybody.
    //
    // `installedBefore` therefore no longer gates the window — it only decides what the window SAYS
    // (see VolumetricCloudRow, and the title Dialog_UpdateNotice picks). That is a much better place
    // for its blind spot to sit: a returning player who never opened the settings screen reads as new
    // and now loses only the cloud announcement, rather than the whole notice plus a wrong default.
    //
    // `acknowledgedVersion` is what keeps it to once. It is persisted the moment the notice is
    // resolved — including when the player dismisses it with Escape — rather than when they accept,
    // so "no thanks" is as final as "yes". A first install writes it on close like anybody else,
    // which is why there is no longer a seeding path that has to remember to write it.
    public static bool ShouldShow(int acknowledgedVersion) =>
        acknowledgedVersion < CurrentNoticeVersion;

    // What the acknowledgement becomes once the notice has been resolved, however it was resolved.
    // Never moves backwards: a player who ran a later build and then rolled back keeps their higher
    // mark rather than being shown a notice they have already answered.
    public static int Acknowledge(int acknowledgedVersion) =>
        acknowledgedVersion > CurrentNoticeVersion ? acknowledgedVersion : CurrentNoticeVersion;

    // The one row with a button — and the one that is either offered or not mentioned, never merely
    // announced. Offered to EVERY install; see ShouldShow for why a new one is asked rather than
    // given it.
    //
    // Off is the case the whole notice exists for. ON means this player already went and found the
    // switch themselves, which for a feature arriving in THIS release means a deliberate act taken
    // in the last few minutes of settings-screen reading. There is nothing to tell them: an offer
    // would be a button that changes nothing, and an announcement would be the mod informing
    // somebody of a decision they just made. So the row disappears, which is also what lets
    // AnythingToShow below mean something.
    public static UpdateNoticeRow VectorLightRow(in UpdateNoticeSwitches switches) =>
        switches.VectorLights ? UpdateNoticeRow.Hidden : UpdateNoticeRow.Offer;

    // ANNOUNCED OR NOT MENTIONED — never offered, and that is a deliberate narrowing rather than a
    // missing feature.
    //
    // The volumetric renderer ships ON underneath switches that also ship on, so for almost every
    // upgrading player it is simply already running and the honest thing to do is name it. The
    // players it is NOT running for are the ones who turned "Partial cloud cover" or "Visible
    // clouds" off, and offering to switch those back on would be this notice overriding a decision
    // the player made deliberately — for a rendering change to clouds they have already said they
    // do not want to see. So they are not asked, and not told either: an announcement about a sky
    // they have switched off is noise.
    //
    // Unreachability is folded into the same Hidden result but is checked FIRST, because it is a
    // different claim. A Clouds user may well have all three of ours on — those settings are live
    // again the moment they uninstall it — so reading the switches alone would announce volumetric
    // clouds to somebody whose sky is being drawn by another mod entirely.
    //
    // AND IT IS HIDDEN OUTRIGHT FROM A FIRST-TIME INSTALL, which is the one thing `installedBefore`
    // still decides. Nothing here is "new" to somebody who has never run this mod: every effect it
    // has arrived at once, five minutes ago, and singling one out as a recent addition is a sentence
    // that means nothing to them. They get the vector-light question and nothing else, which is also
    // why Dialog_UpdateNotice does not call their window "what's new".
    public static UpdateNoticeRow VolumetricCloudRow(in UpdateNoticeSwitches switches, bool installedBefore) =>
        installedBefore && VolumetricCloudsRunning(switches)
            ? UpdateNoticeRow.Announce
            : UpdateNoticeRow.Hidden;

    // Whether the volumetric path is actually drawing on this install right now: reachable at all,
    // and every link of the chain above it closed. Split out from the row above because it is the
    // interesting half and reads as a claim about the screen rather than about the UI.
    public static bool VolumetricCloudsRunning(in UpdateNoticeSwitches switches)
    {
        if (!switches.CloudVolumeShaderLoaded || switches.ExternalCloudsInstalled)
            return false;

        return switches.CloudCover && switches.CloudSheet && switches.CloudVolume;
    }

    // Whether the notice has anything to ask, as opposed to only things to announce. Drives which
    // buttons the window draws: with nothing offerable it is an OK box, not a yes/no.
    //
    // Takes no `installedBefore` because it cannot matter: the only offerable row is the vector-light
    // one, and that is offered to both populations alike. Left as a function rather than inlined so
    // the day a second offer exists, there is one place that already asks the question.
    public static bool AnyOffer(in UpdateNoticeSwitches switches) =>
        VectorLightRow(switches) == UpdateNoticeRow.Offer;

    // Whether the notice has anything to SAY. A window that names nothing is worse than no window:
    // it spends the one appearance this notice gets and tells the player nothing they can act on.
    public static bool AnythingToShow(in UpdateNoticeSwitches switches, bool installedBefore) =>
        VectorLightRow(switches) != UpdateNoticeRow.Hidden
        || VolumetricCloudRow(switches, installedBefore) != UpdateNoticeRow.Hidden;

    // The switches as they should be after the player's answer.
    //
    // Nothing is ever turned OFF here. A `false` answer means "leave it as it was", not "disable" —
    // a player who declines must end up exactly where they started, including if they had already
    // enabled the feature themselves between the update and the notice appearing.
    public static UpdateNoticeSwitches Apply(in UpdateNoticeSwitches switches, bool enableVectorLights) =>
        enableVectorLights ? switches.WithVectorLights() : switches;
}
