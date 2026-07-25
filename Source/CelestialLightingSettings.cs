using Verse;

namespace CelestialLighting;

// Persisted, user-tunable state for the whole mod. Thin Verse adapter over the pure
// CelestialSettingsMath: the aesthetic knob fields mirror PresetKnobs one-for-one, and ApplyPreset
// just copies a resolved bundle into them, so — per DESIGN.md — a preset is never a separate code
// path, only "a bundle of the same values".
//
// Field defaults describe the out-of-box experience: the mod ships on the Realistic preset (its
// realism focus), with the accessibility brightness floor OFF so nights are atmospheric by default
// and one keypress away from legible (see GameComponent_BrightnessFloorHotkey).
public class CelestialLightingSettings : ModSettings
{
    // Which named bundle (if any) the aesthetic knobs currently reflect. Goes to Custom the instant
    // a player moves an individual slider, so the UI can show the aesthetic knobs as "no longer a
    // preset" without a separate dirty flag.
    public CelestialPreset preset = CelestialPreset.Realistic;

    // --- Aesthetic knobs (mirror PresetKnobs; placeholders until §1/§3/§7/§9 land) ---
    public float shadowLengthScale = Presets.Realistic.ShadowLengthScale;
    public float shadowStrength = Presets.Realistic.ShadowStrength;
    public float nightRadianceFloor = Presets.Realistic.NightRadianceFloor;
    public float desaturation = Presets.Realistic.Desaturation;

    // --- Per-effect on/off toggles (drive the CelestialLightingFeatures flags each patch reads;
    //     default true == the shipped, everything-on behaviour). These make the settings screen the
    //     user-facing front-end of the same flags the dev harness flips via its SetFeature step. ---
    public bool civilTwilightPersistence = true;
    public bool penumbraContrast = true;
    public bool moonShadows = true;
    public bool nightRadiance = true;
    public bool lowLightDesaturation = true;
    public bool skyColorTemperature = true;
    public bool aurora = true;
    public bool eclipseDarkening = true;
    public bool bloodMoon = true;
    public bool pitchBlackNights = true;
    public bool indoorSkyOcclusion = true;

    // --- Night-radiance tunables (drive NightRadianceSettings.Current) ---
    // The atmospheric starlight+airglow floor ("true pitch-black" when off), and the pitch-black
    // overlay's minimum-brightness clamp (0 == genuinely black nights; raise it for playability).
    public bool atmosphericGlow = true;
    public float minNightBrightness = NightRadianceMath.DefaultMinNightBrightness;

    // --- Indoor sky-occlusion tunables (drive IndoorOcclusionSettings.Current) ---
    // How much sky a doorway lets past once roofed cells are fully occluded; see
    // IndoorOcclusionMath.DefaultDoorSkyLeak for why doors need this at all.
    public float doorSkyLeak = IndoorOcclusionMath.DefaultDoorSkyLeak;

    // --- Eclipse (drives EclipseSettings.Mode) — which eclipse flavour(s) are live. Defaults to Both:
    //     geometric natural eclipses AND the storyteller's unnatural-rendered ones. See EclipseMode. ---
    public EclipseMode eclipseMode = EclipseMode.Both;

    // --- Accessibility brightness floor (live now; see Patch_BrightnessFloor) ---
    // Off by default: pitch-black atmosphere until a player actively opts in (slider or hotkey).
    public bool brightnessFloorEnabled = false;
    // The minimum displayed sky glow, 0–1, when the floor is enabled. 0.15 is a legible-but-still-
    // clearly-night default: enough to make out tiles without erasing the sense of nighttime.
    public float brightnessFloor = 0.15f;

    // Copies a named preset's bundle into the aesthetic knob fields. Kept trivial and delegating to
    // the pure resolver so the correlation between knobs lives in exactly one tested place.
    public void ApplyPreset(CelestialPreset chosen)
    {
        PresetKnobs knobs = Presets.Resolve(chosen);
        shadowLengthScale = knobs.ShadowLengthScale;
        shadowStrength = knobs.ShadowStrength;
        nightRadianceFloor = knobs.NightRadianceFloor;
        desaturation = knobs.Desaturation;
        preset = chosen;
    }

    // Called by the settings UI right after it mutates any individual aesthetic knob: the knobs no
    // longer match a named bundle, so the state is Custom. The accessibility floor fields are
    // deliberately NOT covered here — they are orthogonal to the aesthetic presets and never part of
    // a bundle, so changing them leaves the preset selection alone.
    public void MarkAestheticKnobsCustom()
    {
        preset = CelestialPreset.Custom;
    }

    // Copies the persisted settings into the static flags/fields the Harmony patches actually read
    // (CelestialLightingFeatures, NightRadianceSettings, EclipseSettings). Those statics are the one
    // source of truth the patches consult; this is the single place the user's saved choices are
    // pushed into them. Called at startup (CelestialLightingSettingsMod's constructor) and whenever
    // the settings window changes/closes, so a toggle takes effect immediately and survives reload.
    public void ApplyToRuntime()
    {
        CelestialLightingFeatures.CivilTwilightPersistence = civilTwilightPersistence;
        CelestialLightingFeatures.PenumbraContrast = penumbraContrast;
        CelestialLightingFeatures.MoonShadows = moonShadows;
        CelestialLightingFeatures.NightRadiance = nightRadiance;
        CelestialLightingFeatures.LowLightDesaturation = lowLightDesaturation;
        CelestialLightingFeatures.SkyColorTemperature = skyColorTemperature;
        CelestialLightingFeatures.Aurora = aurora;
        CelestialLightingFeatures.EclipseDarkening = eclipseDarkening;
        CelestialLightingFeatures.BloodMoon = bloodMoon;
        CelestialLightingFeatures.PitchBlackNights = pitchBlackNights;
        CelestialLightingFeatures.IndoorSkyOcclusion = indoorSkyOcclusion;

        NightRadianceSettings.Current.AtmosphericGlowEnabled = atmosphericGlow;
        NightRadianceSettings.Current.MinNightBrightness = minNightBrightness;

        EclipseSettings.Mode = eclipseMode;

        // The accessibility floor reaches interiors through §7b rather than through glow: roofed cells
        // never take sky glow (GlowGrid.GroundGlowAt returns early for them), so lifting CurSkyGlow
        // cannot brighten a sealed cave. Resolving the checkbox to 0 here keeps that gating in one
        // place — the occlusion core only ever sees a plain fraction.
        IndoorOcclusionSettings.Current.DoorSkyLeak = doorSkyLeak;
        IndoorOcclusionSettings.Current.BrightnessFloor = brightnessFloorEnabled ? brightnessFloor : 0f;

        // §7b's alphas live in baked section meshes, not in a per-frame material, so a change here is
        // invisible until the meshes are rebuilt. Must run after the assignments above.
        IndoorOcclusionRedraw.SyncTo(
            indoorSkyOcclusion, IndoorOcclusionSettings.Current.DoorSkyLeak,
            IndoorOcclusionSettings.Current.BrightnessFloor);
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref preset, "preset", CelestialPreset.Realistic);
        Scribe_Values.Look(ref civilTwilightPersistence, "civilTwilightPersistence", true);
        Scribe_Values.Look(ref penumbraContrast, "penumbraContrast", true);
        Scribe_Values.Look(ref moonShadows, "moonShadows", true);
        Scribe_Values.Look(ref nightRadiance, "nightRadiance", true);
        Scribe_Values.Look(ref lowLightDesaturation, "lowLightDesaturation", true);
        Scribe_Values.Look(ref skyColorTemperature, "skyColorTemperature", true);
        Scribe_Values.Look(ref aurora, "aurora", true);
        Scribe_Values.Look(ref eclipseDarkening, "eclipseDarkening", true);
        Scribe_Values.Look(ref bloodMoon, "bloodMoon", true);
        Scribe_Values.Look(ref pitchBlackNights, "pitchBlackNights", true);
        Scribe_Values.Look(ref indoorSkyOcclusion, "indoorSkyOcclusion", true);
        Scribe_Values.Look(ref doorSkyLeak, "doorSkyLeak", IndoorOcclusionMath.DefaultDoorSkyLeak);
        Scribe_Values.Look(ref atmosphericGlow, "atmosphericGlow", true);
        Scribe_Values.Look(ref minNightBrightness, "minNightBrightness", NightRadianceMath.DefaultMinNightBrightness);
        Scribe_Values.Look(ref eclipseMode, "eclipseMode", EclipseMode.Both);
        Scribe_Values.Look(ref shadowLengthScale, "shadowLengthScale", Presets.Realistic.ShadowLengthScale);
        Scribe_Values.Look(ref shadowStrength, "shadowStrength", Presets.Realistic.ShadowStrength);
        Scribe_Values.Look(ref nightRadianceFloor, "nightRadianceFloor", Presets.Realistic.NightRadianceFloor);
        Scribe_Values.Look(ref desaturation, "desaturation", Presets.Realistic.Desaturation);
        Scribe_Values.Look(ref brightnessFloorEnabled, "brightnessFloorEnabled", false);
        Scribe_Values.Look(ref brightnessFloor, "brightnessFloor", 0.15f);
    }
}
