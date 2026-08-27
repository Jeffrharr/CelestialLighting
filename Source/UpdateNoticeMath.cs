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
    // `installedBefore` is what keeps the notice off a first-time install. Somebody who has never run
    // this mod has no "update" to be told about: everything the notice would name is simply part of
    // what they just installed, five minutes ago. They get vector lighting switched on instead of
    // asked about it — see FirstRunSwitches for why the two populations get different answers.
    //
    // It is answered by whether a persisted settings file existed when the game read one
    // (CelestialLightingSettings.LoadedFromDisk) — the only durable trace an earlier version of this
    // mod leaves behind, since nothing it does is written into a save. ITS ONE BLIND SPOT: RimWorld
    // only writes a mod's settings file when the settings window closes, so a returning player who
    // has never opened this mod's settings screen reads as new. They lose the notice and gain vector
    // lighting without being asked, which is the same thing every genuinely new install gets, so the
    // failure mode is "treated as a new player" rather than "silently relit mid-colony".
    //
    // `acknowledgedVersion` is what keeps it to once. It is persisted the moment the notice is
    // resolved — including when the player dismisses it with Escape — rather than when they accept,
    // so "no thanks" is as final as "yes".
    //
    // A FIRST-TIME INSTALL STILL WRITES THE ACKNOWLEDGEMENT (see AcknowledgeOnFirstRun): the notice is
    // skipped, but the version is recorded. Without that, the first time such a player opens and
    // closes the settings screen they gain a settings file, and on the boot after that they read as a
    // returning player and are told that a feature they have always had is new.
    public static bool ShouldShow(bool installedBefore, int acknowledgedVersion) =>
        installedBefore && acknowledgedVersion < CurrentNoticeVersion;

    // What the acknowledgement becomes once the notice has been resolved, however it was resolved.
    // Never moves backwards: a player who ran a later build and then rolled back keeps their higher
    // mark rather than being shown a notice they have already answered.
    public static int Acknowledge(int acknowledgedVersion) =>
        acknowledgedVersion > CurrentNoticeVersion ? acknowledgedVersion : CurrentNoticeVersion;

    // The same value, for the install that never saw the notice because it is brand new. A separate
    // named function rather than a second call to Acknowledge because the two are the same number for
    // entirely different reasons, and a future notice that wants to greet new installs differently
    // should find one of them and not the other.
    public static int AcknowledgeOnFirstRun() => CurrentNoticeVersion;

    // WHAT A BRAND-NEW INSTALL GETS, and it is not the same as what an upgrade gets: vector lighting
    // is ON out of the box, and stays off for everybody who already had the mod until they say yes.
    //
    // The asymmetry is about EXPECTATION, and it is the only argument left for it. Vector lighting
    // used to be the most expensive thing the mod does by a wide margin, and for one revision that
    // cost was the reason nobody was defaulted into it. The optimisation work took that argument
    // away — it is now an ordinary per-frame cost among the mod's others — so what remains is that it
    // changes how a colony is LIT: light that vanilla delivered along a path bending around a corner
    // no longer arrives, so indirectly lit rooms are genuinely darker. Somebody installing today has
    // no prior expectation to violate; this is simply how the mod lights a colony, and shipping its
    // best look by default is the right call. Somebody fifty hours into a colony lit the other way has
    // a very specific expectation, and silently rewriting it on update is not a default, it is a
    // surprise. They get asked.
    //
    // THIS IS WHY THE SCRIBED DEFAULT FOR `vectorLights` MUST STAY `false` even though a new install
    // now starts true, and it is the sharpest edge in this file. Scribe_Values omits any value equal
    // to its default, so an existing config written while the feature was off has NO vectorLights
    // node — flip the scribed default to true and every one of those configs reads back as `on`,
    // which is exactly the silent rewrite the paragraph above rules out. The new-install default
    // therefore lives here, on a path that only runs when no settings file existed at all, and the
    // serialisation default stays as the honest answer to "what did an absent node mean when it was
    // written".
    public static UpdateNoticeSwitches FirstRunSwitches(in UpdateNoticeSwitches switches) =>
        switches.WithVectorLights();

    // The one row with a button — and the one that is either offered or not mentioned, never merely
    // announced. Only an upgrading player ever sees it, since ShouldShow is the gate above it.
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
    // No `installedBefore` parameter: this row is only ever evaluated for a window that ShouldShow
    // has already decided to raise, and it raises one only for an install that had the mod before.
    // "New to you" is therefore true of everybody who can see this row.
    public static UpdateNoticeRow VolumetricCloudRow(in UpdateNoticeSwitches switches) =>
        VolumetricCloudsRunning(switches) ? UpdateNoticeRow.Announce : UpdateNoticeRow.Hidden;

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
    // Left as a function rather than inlined into the one call site so the day a second offer exists,
    // there is one place that already asks the question.
    public static bool AnyOffer(in UpdateNoticeSwitches switches) =>
        VectorLightRow(switches) == UpdateNoticeRow.Offer;

    // Whether the notice has anything to SAY. A window that names nothing is worse than no window:
    // it spends the one appearance this notice gets and tells the player nothing they can act on.
    public static bool AnythingToShow(in UpdateNoticeSwitches switches) =>
        VectorLightRow(switches) != UpdateNoticeRow.Hidden
        || VolumetricCloudRow(switches) != UpdateNoticeRow.Hidden;

    // The switches as they should be after the player's answer.
    //
    // Nothing is ever turned OFF here. A `false` answer means "leave it as it was", not "disable" —
    // a player who declines must end up exactly where they started, including if they had already
    // enabled the feature themselves between the update and the notice appearing.
    public static UpdateNoticeSwitches Apply(in UpdateNoticeSwitches switches, bool enableVectorLights) =>
        enableVectorLights ? switches.WithVectorLights() : switches;
}
