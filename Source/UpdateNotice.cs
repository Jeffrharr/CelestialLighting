using System;
using Verse;

namespace CelestialLighting;

// The live half of the one-time "what's new" notice: reads the persisted settings and this
// machine's two reachability facts, asks UpdateNoticeMath what to do, raises Dialog_UpdateNotice if
// it says so, and writes the answer back. Every branch worth arguing about is in UpdateNoticeMath;
// what is left here is state access, which is exactly the split the house style asks for.
//
// See DESIGN.md "Update notice" for the whole design, including why the notice keys on a settings
// file existing rather than on anything in the save.
public static class UpdateNotice
{
    // Called once from Patch_UpdateNotice, at the point RimWorld builds the main menu. Never throws
    // out: it runs inside a Harmony postfix on the entry UI, and a "what's new" window is not worth
    // a broken main menu to anybody.
    public static void ShowIfDue()
    {
        try
        {
            RaiseIfDue();
        }
        catch (Exception ex)
        {
            Log.Error($"[CelestialLighting] Update notice failed to open; continuing without it. {ex}");
        }
    }

    private static void RaiseIfDue()
    {
        CelestialLightingSettings settings = CelestialLightingSettingsMod.Settings;
        if (settings == null)
            return;

        // A brand-new install: no settings file existed, so there is no earlier version of this mod
        // to have an update FROM, and everything the notice would announce is simply part of what
        // this player just installed. They get vector lighting switched on instead of asked about
        // it — see UpdateNoticeMath.FirstRunSwitches for why the two populations differ.
        if (!settings.LoadedFromDisk)
        {
            SeedFirstRun(settings);
            return;
        }

        if (!UpdateNoticeMath.ShouldShow(settings.LoadedFromDisk, settings.updateNoticeVersion))
            return;

        UpdateNoticeSwitches switches = ReadSwitches(settings);

        // A window that names nothing is worse than no window: it would spend the one appearance
        // this notice gets and tell the player nothing. Acknowledge it instead, so the version does
        // not sit unwritten and re-evaluate this every boot.
        if (!UpdateNoticeMath.AnythingToShow(switches))
        {
            RecordAnswer(enableVectorLights: false);
            return;
        }

        Find.WindowStack.Add(new Dialog_UpdateNotice(switches));
    }

    // Applies the new-install defaults and records the notice against an install that will never be
    // shown it, then persists both immediately — an unwritten acknowledgement is the same as no
    // acknowledgement on the next boot.
    //
    // Writing on this path is the point of it rather than an afterthought. Without it, the first
    // time this player opens and closes the settings screen they gain a settings file, and the boot
    // after that they read as a returning player and are told that a feature they have always had
    // is new.
    private static void SeedFirstRun(CelestialLightingSettings settings)
    {
        UpdateNoticeSwitches seeded = UpdateNoticeMath.FirstRunSwitches(ReadSwitches(settings));
        int acknowledged = UpdateNoticeMath.AcknowledgeOnFirstRun();

        if (settings.updateNoticeVersion == acknowledged && settings.vectorLights == seeded.VectorLights)
            return;

        settings.vectorLights = seeded.VectorLights;
        settings.updateNoticeVersion = acknowledged;
        CelestialLightingSettingsMod.Save();
    }

    // Applies the player's answer and marks the notice answered. Called from
    // Dialog_UpdateNotice.PostClose — for EVERY way out of that window, including Escape, so
    // declining is as final as accepting.
    public static void RecordAnswer(bool enableVectorLights)
    {
        try
        {
            CelestialLightingSettings settings = CelestialLightingSettingsMod.Settings;
            if (settings == null)
                return;

            UpdateNoticeSwitches applied = UpdateNoticeMath.Apply(ReadSwitches(settings), enableVectorLights);

            settings.vectorLights = applied.VectorLights;
            settings.updateNoticeVersion = UpdateNoticeMath.Acknowledge(settings.updateNoticeVersion);

            // Save rather than a bare field write: WriteSettings persists AND re-runs ApplyToRuntime,
            // which is what pushes the switch into the static flags every patch reads — and, for this
            // one specifically, what runs VectorLightRedraw.SyncTo, since half of vector lighting is
            // baked into the lighting overlay's vertex colours and would otherwise keep rendering the
            // previous answer until the player happened to build something.
            CelestialLightingSettingsMod.Save();
        }
        catch (Exception ex)
        {
            Log.Error($"[CelestialLighting] Update notice failed to apply its answer. {ex}");
        }
    }

    // The persisted switches plus the two live facts that decide whether the volumetric path is
    // reachable at all on this install. Both reads are cheap and side-effect-free: ShaderLoaded is
    // three field tests (deliberately NOT CloudVolumeShader.Available, which additionally waits on
    // the background bake and would read false at the main menu simply because the bake had not
    // finished), and ModIsInstalled is a memoised walk of the running mod list.
    private static UpdateNoticeSwitches ReadSwitches(CelestialLightingSettings settings) =>
        new UpdateNoticeSwitches(
            vectorLights: settings.vectorLights,
            cloudCover: settings.cloudCover,
            cloudSheet: settings.cloudSheet,
            cloudVolume: settings.cloudVolume,
            cloudVolumeShaderLoaded: CloudVolumeShader.ShaderLoaded,
            externalCloudsInstalled: CloudsCompat.ModIsInstalled);
}
