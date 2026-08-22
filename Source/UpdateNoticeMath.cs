namespace CelestialLighting;

// The pure policy behind the one-time "what's new" notice (Source/Dialog_UpdateNotice.cs): given
// what the player already has switched on and what this machine can actually render, does the
// notice appear at all, what does it say about each new feature, and what do the switches become if
// they say yes? No UnityEngine, no Verse — the live half is only "read the settings, draw a window,
// write the settings back".
//
// WHY THIS IS A SEPARATE FILE AND NOT THREE `if`s IN THE DIALOG. Every branch here is a way to get
// the notice WRONG in a manner nobody notices for a release: showing it to somebody installing for
// the first time, showing it twice, showing it forever because the acknowledgement never persisted,
// or — the worst one — offering to enable something that then visibly does nothing because a second
// switch upstream of it is still off. None of those can be caught by looking at a screenshot, and
// all of them are one boolean expression each, so they belong somewhere they can be enumerated as
// test cases. See DESIGN.md "Update notice".

// Which of the new features the notice can talk about, and in what terms.
public enum UpdateNoticeOffer
{
    // The feature cannot run on this install at all — the shader bundle did not load, or another
    // mod already owns the thing it would draw. NOT MENTIONED, rather than mentioned and greyed:
    // an offer the player cannot take is worse than silence, because they go looking for the result
    // and find nothing, and the notice has spent its one appearance saying so.
    Unavailable,

    // Already on. There is nothing to ask, but it is still named in the notice: "here is what is
    // new" is the other half of what this window is for, and a feature that arrived switched on is
    // exactly the one a player would otherwise never learn the name of.
    AlreadyOn,

    // Off, and reachable. This is the row that gets a tickbox.
    OfferToEnable,
}

// Every persisted switch the notice reads or writes, plus the two live facts that decide whether
// the volumetric path is reachable. Passed as one value rather than six parameters because the
// interesting operations (Apply below) return a whole new set, and threading six `out`s through a
// dialog's button handler is how the cloud chain gets half-applied.
public readonly struct UpdateNoticeSwitches
{
    // §27's master switch. Ships off; nothing upstream of it, so it is the simple case.
    public readonly bool VectorLights;

    // The three-deep chain §25c sits at the bottom of. `CloudVolume` alone renders nothing: with
    // `CloudSheet` off there are no sheets to march through, and with `CloudCover` off there is no
    // cloud at all (CloudLayers.SheetAlphaFor asks for both before it asks for a fraction).
    public readonly bool CloudCover;
    public readonly bool CloudSheet;
    public readonly bool CloudVolume;

    // CloudVolumeShader.ShaderLoaded — whether the custom shader is present, supported and is
    // actually ours rather than the default vanilla substitutes for a failed load. False on any
    // platform whose bundle is missing, where §25c silently draws §25b's baked atlas instead.
    public readonly bool CloudVolumeShaderLoaded;

    // CloudsCompat.ModIsInstalled — the Clouds mod hangs its own particle deck over the map, and
    // our positional cloud lanes (§25 among them) stand down for it. With it installed our sheets
    // never draw, so there is nothing for the volumetric renderer to render.
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
    // `acknowledgedVersion` is what keeps it to once. It is persisted the moment the notice is
    // resolved — including when the player dismisses it with Escape — rather than when they accept,
    // so "no thanks" is as final as "yes".
    //
    // A FIRST-TIME INSTALL STILL WRITES THE ACKNOWLEDGEMENT (see AcknowledgeOnFirstRun): the notice
    // is skipped, but the version is recorded, or the first time that player opens and closes the
    // settings screen they would gain a settings file and then be told on the next boot that a
    // feature they have always had is new.
    public static bool ShouldShow(bool installedBefore, int acknowledgedVersion) =>
        installedBefore && acknowledgedVersion < CurrentNoticeVersion;

    // What the acknowledgement becomes once the notice has been resolved, however it was resolved.
    // Never moves backwards: a player who ran a later build and then rolled back keeps their higher
    // mark rather than being shown a notice they have already answered.
    public static int Acknowledge(int acknowledgedVersion) =>
        acknowledgedVersion > CurrentNoticeVersion ? acknowledgedVersion : CurrentNoticeVersion;

    // The same value, for the install that never saw the notice because it is brand new. A separate
    // named function rather than a second call to Acknowledge because the two are the same number
    // for entirely different reasons, and a future notice that wants to greet new installs
    // differently should find one of them and not the other.
    public static int AcknowledgeOnFirstRun() => CurrentNoticeVersion;

    // §27. No chain above it and no hardware requirement — the shader path and the flat fallback
    // both render something — so the switch alone decides.
    public static UpdateNoticeOffer VectorLightOffer(in UpdateNoticeSwitches switches) =>
        switches.VectorLights ? UpdateNoticeOffer.AlreadyOn : UpdateNoticeOffer.OfferToEnable;

    // §25c, and the interesting one. Two ways to be unreachable, then the chain.
    //
    // Order matters: unreachability is checked BEFORE the switches, because a player with the Clouds
    // mod installed may well have all three of ours on — the settings are live and mean something
    // again the moment they uninstall it — and telling them their volumetric clouds are already
    // running would be a straightforwardly false statement about their screen.
    public static UpdateNoticeOffer VolumetricCloudOffer(in UpdateNoticeSwitches switches)
    {
        if (!switches.CloudVolumeShaderLoaded || switches.ExternalCloudsInstalled)
            return UpdateNoticeOffer.Unavailable;

        bool drawnAndMarched = switches.CloudCover && switches.CloudSheet && switches.CloudVolume;
        return drawnAndMarched ? UpdateNoticeOffer.AlreadyOn : UpdateNoticeOffer.OfferToEnable;
    }

    // Whether the notice has anything to ask, as opposed to only things to announce. Drives which
    // buttons the window draws: with nothing offerable it is an OK box, not a yes/no.
    public static bool AnyOffer(in UpdateNoticeSwitches switches) =>
        VectorLightOffer(switches) == UpdateNoticeOffer.OfferToEnable
        || VolumetricCloudOffer(switches) == UpdateNoticeOffer.OfferToEnable;

    // The switches as they should be after the player's answer.
    //
    // ENABLING VOLUMETRIC CLOUDS RAISES THE WHOLE CHAIN, and that is the point of routing this
    // through a function. Setting `CloudVolume` on its own is the failure this file exists to
    // prevent: the box ticks, the settings screen agrees, and the sky is unchanged because the
    // player turned "Partial cloud cover" off eleven months ago and has long since forgotten. The
    // notice asked "do you want this feature", so it delivers the feature, not the leaf switch.
    //
    // Nothing is ever turned OFF here. A `false` answer means "leave it as it was", not "disable" —
    // a player who declines the offer must end up exactly where they started, including if they had
    // already enabled the feature themselves between the update and the notice appearing.
    public static UpdateNoticeSwitches Apply(in UpdateNoticeSwitches switches,
        bool enableVectorLights, bool enableVolumetricClouds) =>
        new UpdateNoticeSwitches(
            vectorLights: switches.VectorLights || enableVectorLights,
            cloudCover: switches.CloudCover || enableVolumetricClouds,
            cloudSheet: switches.CloudSheet || enableVolumetricClouds,
            cloudVolume: switches.CloudVolume || enableVolumetricClouds,
            cloudVolumeShaderLoaded: switches.CloudVolumeShaderLoaded,
            externalCloudsInstalled: switches.ExternalCloudsInstalled);
}
