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

        if (!UpdateNoticeMath.ShouldShow(settings.updateNoticeVersion))
            return;

        UpdateNoticeSwitches switches = ReadSwitches(settings);

        // A window that names nothing is worse than no window: it would spend the one appearance
        // this notice gets and tell the player nothing. Acknowledge it instead, so the version does
        // not sit unwritten and re-evaluate this every boot.
        if (!UpdateNoticeMath.AnythingToShow(switches, settings.LoadedFromDisk))
        {
            RecordAnswer(enableVectorLights: false);
            return;
        }

        Find.WindowStack.Add(new Dialog_UpdateNotice(switches, settings.LoadedFromDisk));
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

    // The switches as they stand right now. Public for Dialog_UpdateNotice's parameterless
    // constructor, which is how the test harness raises the window by type name; everything inside
    // this file has a settings instance in hand already and uses the private overload.
    public static UpdateNoticeSwitches CurrentSwitches()
    {
        CelestialLightingSettings settings = CelestialLightingSettingsMod.Settings;
        return settings == null
            // Not a plausible state at any point a window can be constructed — the Mod subclass is
            // built during mod loading — but the dialog reads this before it can check anything, so
            // an all-off answer is better than a null dereference in a constructor.
            ? new UpdateNoticeSwitches(false, false, false, false, false, false)
            : ReadSwitches(settings);
    }

    // Whether this install has run an earlier version of the mod. Public for the same reason as
    // CurrentSwitches above. See CelestialLightingSettings.LoadedFromDisk for what answers it and
    // for the one case it gets wrong.
    public static bool InstalledBefore() =>
        CelestialLightingSettingsMod.Settings?.LoadedFromDisk ?? false;

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
