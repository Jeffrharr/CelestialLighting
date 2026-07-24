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

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref preset, "preset", CelestialPreset.Realistic);
        Scribe_Values.Look(ref shadowLengthScale, "shadowLengthScale", Presets.Realistic.ShadowLengthScale);
        Scribe_Values.Look(ref shadowStrength, "shadowStrength", Presets.Realistic.ShadowStrength);
        Scribe_Values.Look(ref nightRadianceFloor, "nightRadianceFloor", Presets.Realistic.NightRadianceFloor);
        Scribe_Values.Look(ref desaturation, "desaturation", Presets.Realistic.Desaturation);
        Scribe_Values.Look(ref brightnessFloorEnabled, "brightnessFloorEnabled", false);
        Scribe_Values.Look(ref brightnessFloor, "brightnessFloor", 0.15f);
    }
}
