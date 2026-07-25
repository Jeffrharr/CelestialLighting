using UnityEngine;
using Verse;

namespace CelestialLighting;

// The Verse.Mod subclass that hosts the settings screen. Separate from the [StaticConstructorOnStartup]
// PatchAll entry point (CelestialLightingMod) on purpose: RimWorld instantiates exactly one Mod
// subclass per package to draw its Options → Mod Settings page, and that instantiation happens
// during mod loading — so it is also the natural, earliest place to load the persisted settings and
// expose them to the patches through a static accessor.
public class CelestialLightingSettingsMod : Mod
{
    // Static so the Harmony patches (which are static classes RimWorld constructs, not things we can
    // hand a reference to) can read the live settings. Assigned in the constructor below, which
    // RimWorld calls once at startup before any patch runs during gameplay.
    public static CelestialLightingSettings Settings { get; private set; }

    // Held so code with only the static Settings in hand (e.g. the hotkey GameComponent) can persist
    // a change: ModSettings are written back through the owning Mod's WriteSettings(), not the
    // settings object itself.
    private static CelestialLightingSettingsMod instance;

    public CelestialLightingSettingsMod(ModContentPack content) : base(content)
    {
        instance = this;
        Settings = GetSettings<CelestialLightingSettings>();
        // Push the persisted choices into the static flags the patches read, at startup — before any
        // patch runs during gameplay — so a saved "aurora off" (etc.) is in effect from the first frame.
        Settings.ApplyToRuntime();
    }

    // Persists the current settings to disk. Used by the in-game hotkey toggle so a floor flipped
    // mid-game survives a reload, matching what the settings window's own Close would do.
    public static void Save() => instance?.WriteSettings();

    // RimWorld calls this when the settings window closes. Re-apply so the final state reaches the
    // runtime flags even if something changed without the per-frame apply in DoSettingsWindowContents.
    public override void WriteSettings()
    {
        base.WriteSettings();
        Settings.ApplyToRuntime();
    }

    public override string SettingsCategory() => "Celestial Lighting";

    public override void DoSettingsWindowContents(Rect inRect)
    {
        var listing = new Listing_Standard();
        listing.Begin(inRect);

        DrawEffectToggles(listing);
        listing.GapLine();
        DrawPresetSection(listing);
        listing.GapLine();
        DrawAestheticKnobs(listing);
        listing.GapLine();
        DrawBrightnessFloorSection(listing);

        listing.End();

        // Push any change the player just made straight into the runtime flags, so effects toggle
        // live in-game while the window is open — no reload needed. Cheap (a handful of field copies).
        Settings.ApplyToRuntime();
    }

    // The per-effect on/off switches — the user-facing front end of the CelestialLightingFeatures
    // flags. Each is a plain checkbox bound directly to a persisted bool; ApplyToRuntime (run every
    // frame this window is open, and at startup) copies them into the static flags the patches read.
    private void DrawEffectToggles(Listing_Standard listing)
    {
        Text.Font = GameFont.Medium;
        listing.Label("Effects");
        Text.Font = GameFont.Small;
        listing.Label("Turn any individual celestial effect on or off. All on by default.");

        listing.CheckboxLabeled("Civil-twilight persistence", ref Settings.civilTwilightPersistence,
            "Linger the warm dusk tint through civil twilight after geometric sunset.");
        listing.CheckboxLabeled("Angular-size penumbra", ref Settings.penumbraContrast,
            "Soften shadow contrast toward the horizon as the sun's disc widens the penumbra.");
        listing.CheckboxLabeled("Moon-cast shadows", ref Settings.moonShadows,
            "A faint, phase- and altitude-scaled shadow cast by the moon at night.");
        listing.CheckboxLabeled("Night-sky radiance", ref Settings.nightRadiance,
            "Replace vanilla's flat night glow with a starlight + airglow + moonlight floor.");
        listing.CheckboxLabeled("Pitch-black nights", ref Settings.pitchBlackNights,
            "Darken the on-screen night overlay toward black as the night floor drops.");
        listing.CheckboxLabeled("Black unlit interiors", ref Settings.indoorSkyOcclusion,
            "Stop the sky lighting roofed cells. Vanilla always bleeds ~61% of the sky into every roofed tile, so a sealed cave never goes black; with this on, an unlit interior is lit by its lamps or not at all — day and night.");
        Settings.doorSkyLeak = LabeledSlider(listing, "  Light leaked through doors", Settings.doorSkyLeak, 0f, 0.5f);
        listing.CheckboxLabeled("Atmospheric night glow", ref Settings.atmosphericGlow,
            "The constant starlight + airglow floor. Off = only moonlight lights the night (true pitch-black on a moonless night).");
        Settings.minNightBrightness = LabeledSlider(listing, "  Minimum night brightness", Settings.minNightBrightness, 0f, 1f);
        listing.CheckboxLabeled("Low-light desaturation", ref Settings.lowLightDesaturation,
            "Drain colour toward a cool blue-grey as the sky darkens (the Purkinje shift).");
        listing.CheckboxLabeled("Sky colour-temperature", ref Settings.skyColorTemperature,
            "Warm the sky toward the horizon on a continuous, altitude-keyed curve.");
        listing.CheckboxLabeled("Aurora during solar flares", ref Settings.aurora,
            "Shift the night sky toward auroral greens/reds while a solar flare is active.");
        listing.CheckboxLabeled("Eclipse darkening", ref Settings.eclipseDarkening,
            "Reshape the vanilla eclipse's flat dim into a gradual fly-in / park / fly-out.");
        listing.CheckboxLabeled("Natural eclipse timing (opt-in)", ref Settings.naturalEclipse,
            "Also fire eclipses at their real, short astronomically-correct duration from the modeled moon. Changes WHEN a gameplay event occurs, so it is off by default.");
        listing.CheckboxLabeled("Blood-moon crimson (VRE – Sanguophage)", ref Settings.bloodMoon,
            "Recolour the moonlit night crimson while VRE – Sanguophage's blood-moon condition is active. Inert without that mod.");
    }

    private void DrawPresetSection(Listing_Standard listing)
    {
        Text.Font = GameFont.Medium;
        listing.Label("Preset");
        Text.Font = GameFont.Small;
        listing.Label("Pick a look and every correlated slider below is set for you. Nudge any slider and the preset becomes Custom.");

        // A named preset's radio applies its whole bundle; Custom's radio only records the state (it
        // must never stomp the knobs the player has tuned).
        DrawPresetRadio(listing, CelestialPreset.Realistic, "Realistic", "Physically-faithful shadows, genuinely black nights, colourless night vision.");
        DrawPresetRadio(listing, CelestialPreset.Cinematic, "Cinematic", "Longer softer shadows, a gentle night glow, more colour kept at night.");
        DrawPresetRadio(listing, CelestialPreset.Custom, "Custom", "Your own mix of the sliders below.");
    }

    private void DrawPresetRadio(Listing_Standard listing, CelestialPreset option, string label, string tooltip)
    {
        bool selected = Settings.preset == option;
        if (!listing.RadioButton(label, selected, tabIn: 8f, tooltip))
            return;

        // RadioButton returns true only on the click that selects it. Applying a named bundle vs.
        // just recording Custom is the one place these two cases diverge.
        if (Presets.IsOpinionated(option))
            Settings.ApplyPreset(option);
        else
            Settings.preset = option;
    }

    private void DrawAestheticKnobs(Listing_Standard listing)
    {
        Text.Font = GameFont.Medium;
        listing.Label("Look");
        Text.Font = GameFont.Small;
        listing.Label("These feed the shadow, night-darkness, and desaturation subsystems. (Not all are wired up yet.)");

        // Each slider marks the preset Custom only if the player actually moved it, so merely opening
        // the window never silently flips a chosen preset to Custom.
        Settings.shadowLengthScale = AestheticSlider(listing, "Shadow length", Settings.shadowLengthScale, 0.5f, 2.0f);
        Settings.shadowStrength = AestheticSlider(listing, "Shadow strength", Settings.shadowStrength, 0f, 1f);
        Settings.nightRadianceFloor = AestheticSlider(listing, "Night radiance floor", Settings.nightRadianceFloor, 0f, 0.3f);
        Settings.desaturation = AestheticSlider(listing, "Night desaturation", Settings.desaturation, 0f, 1f);
    }

    // An aesthetic-knob slider: on a real change it records that the knobs no longer match a named
    // preset. Only the four §1/§3/§7/§9 knobs use this — the accessibility floor is orthogonal to
    // the presets and uses the plain LabeledSlider so touching it never flips the preset to Custom.
    private float AestheticSlider(Listing_Standard listing, string label, float value, float min, float max)
    {
        float updated = LabeledSlider(listing, label, value, min, max);
        if (updated != value)
            Settings.MarkAestheticKnobsCustom();
        return updated;
    }

    // A plain labeled slider with no side effects on the preset selection.
    private float LabeledSlider(Listing_Standard listing, string label, float value, float min, float max)
    {
        listing.Label($"{label}: {value:0.00}");
        return listing.Slider(value, min, max);
    }

    private void DrawBrightnessFloorSection(Listing_Standard listing)
    {
        Text.Font = GameFont.Medium;
        listing.Label("Accessibility: minimum brightness");
        Text.Font = GameFont.Small;
        listing.Label("An opt-in floor on how dark nights can look. The complement to pitch-black nights: black for atmosphere, one keypress to legible when you need to see. Toggle it in-game with the \"Toggle minimum brightness\" hotkey.");

        listing.CheckboxLabeled("Enable minimum brightness floor", ref Settings.brightnessFloorEnabled);
        Settings.brightnessFloor = LabeledSlider(listing, "Minimum brightness", Settings.brightnessFloor, 0f, 1f);
    }
}
