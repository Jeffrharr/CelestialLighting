using System;
using Verse;

namespace CelestialLighting;

// Soft interop with Clouds (brrainz.clouds, Andreas Pardeike, Workshop 3039192325).
//
// WHAT THAT MOD IS, because it decides the whole shape of this file. Clouds is a Unity
// ParticleSystem hung over the map: one GameObject per map, a billboard particle material whose
// tint/contrast/opacity/edge power are driven from the current WeatherDef through
// WeatherCloudProfile, drifting on the map's wind. Decompiled, it patches only CameraDriver.Update,
// WorldCameraDriver.Update, TickManager.DoSingleTick, Map.MapPreTick, MapDeiniter.Deinit,
// Current.ProgramState and MemoryUtility — camera, tick and lifecycle. It touches nothing of ours:
// no SkyManager, no GenCelestial, no GlowGrid, no SectionLayer, no shadow anything. It never writes
// a sky colour and it never writes `.glow`.
//
// So this is not a conflict. There is no double-patch to order and no light being written twice.
// What there is, is a SECOND OPINION ABOUT WHERE THE CLOUDS ARE, and that is worse than a conflict
// because both halves work perfectly and the result still looks wrong.
//
// THE FAILURE, CONCRETELY. §25 draws its own bounded sheets moving across the map, and §23b/§23c
// draw underlight and shadow at exactly those sheets' positions (DESIGN.md §25, "All three lanes
// draw the same sheets" — that section exists because we already shipped the bug where two of our
// OWN lanes disagreed about cloud placement, which is the same failure at smaller scale). Add
// Clouds and there are now two independently-placed cloud fields on one screen: their particles,
// and our sheets plus the light we key to them. A shadow crosses the colony under open sky; one of
// their clouds sails over casting nothing.
//
// So the three positional lanes stand down and Clouds owns the deck (CloudsCompatMath). The
// non-positional ones do not: §22's Clear-day sky tint, its label, and §23's colour-temperature
// scaling are statements about the sky as a whole, they stay true whoever renders the shapes, and
// Clouds has no opinion about sky colour to disagree with — its particles read the WeatherDef's
// palette, not the live sky target, so it cannot even see what we did. A player who installs Clouds
// for clouds still gets our atmosphere; they just get Pardeike's clouds in it.
//
// PRESENCE, NOT PER-MAP DEFERRAL. Clouds exposes a public CloudVisibility.IsAllowedOn(Map) and
// refuses to draw on pocket maps, fully-roofed maps and maps with disableSunShadows, so we could in
// principle resume our own sheets wherever theirs decline. Deliberately not doing that. Two of those
// three cases are ones OUR gates already refuse (MapSky.HasSky / SkyBlackedOut, DESIGN.md §17), and
// the third — a surface map that happens to be 100% roofed — is a case where they decided there
// should be no clouds; taking it as an invitation to draw ours would be arguing with the mod we just
// handed the deck to. Their presence is the whole signal, and it means we skip the reflection into
// their internals entirely.
//
// NOT A SETTING. Clouds has no ModSettings — installing it is switching it on — so there is no
// "clouds are installed but disabled" state to detect, and a switch here would only let a player
// turn both cloud fields on at once. The settings screen REPORTS this instead
// (CelestialLightingSettingsMod.ShowExternalCloudSource), the same way the axial-tilt line reports
// which mod owns the obliquity rather than offering to fight it.
//
// NO HARD REFERENCE, same as the other three compat files: the only thing named is a packageId
// string, so a player without Clouds loads a build that has never heard of it and nothing here can
// throw into a caller.
public static class CloudsCompat
{
    private const string PackageId = "brrainz.clouds";

    // Resolved once and cached, unlike the other compat files' per-call RunningMods walks. Those are
    // read from worldgen and from the settings screen; this one is read from CloudLayers' three
    // per-frame gates, and LoadedModManager.RunningMods cannot change while a game is running, so
    // walking it 60 times a second would be a list scan per frame to answer a question fixed at load.
    private static bool? active;

    // The harness's A/B seam, and the ONLY writer of it is Source/Probes/CloudsCompatOverride.cs —
    // nothing the player can reach touches this, exactly like CloudLayers.AmplitudeScale and for the
    // same reason. The comparison this interop has to justify itself with is "their clouds alone"
    // against "their clouds and ours at once", and both frames have to come out of one RimWorld boot
    // with Clouds loaded, which no feature flag on the shipped side can arrange: the thing being
    // switched is our reading of the load order, not one of our effects.
    //
    // Deliberately NOT a player setting. The only game a switch here could produce is two mods
    // placing clouds independently over one map, which is the failure this file exists to prevent —
    // see the settings screen's ShowExternalCloudSource for why that reports rather than offers.
    public static bool? OverrideInstalled;

    // Whether Clouds is in the load order at all. Public because the settings screen reports it.
    public static bool ModIsInstalled
    {
        get
        {
            if (OverrideInstalled.HasValue)
                return OverrideInstalled.Value;

            active ??= ModIsActive();
            return active.Value;
        }
    }

    // Whether one of our cloud lanes draws right now, given its own feature flag. The single entry
    // point the rest of the mod uses — call sites ask this instead of asking about Clouds, so the
    // ruling about which lanes stand down lives in one testable place (CloudsCompatMath) rather than
    // being re-decided at each guard.
    public static bool LaneDraws(CloudLane lane, bool featureEnabled) =>
        CloudsCompatMath.LaneDraws(lane, featureEnabled, ModIsInstalled);

    private static bool ModIsActive()
    {
        foreach (ModContentPack pack in LoadedModManager.RunningMods)
        {
            if (pack.PackageId.Equals(PackageId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
