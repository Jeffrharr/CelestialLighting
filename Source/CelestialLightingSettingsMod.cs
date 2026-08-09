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

    // Scroll state for the settings page. The content (a dozen-plus effect toggles, two radio groups
    // and six sliders) is taller than the settings window at any reasonable resolution, and a
    // Listing_Standard does not scroll on its own — it just keeps drawing past the bottom edge, so
    // everything below the fold was unreachable.
    private Vector2 scrollPosition;

    // Measured content height, filled in from Listing.CurHeight after the first pass. Seeded at 0 and
    // floored against the visible height below, so the first frame draws without a scrollbar rather
    // than with a wrong one, and self-corrects from the second frame on.
    private float contentHeight;

    private const float ScrollBarWidth = 20f;

    public override void DoSettingsWindowContents(Rect inRect)
    {
        // The view rect is the *content* rect: as tall as the content actually needs (never shorter
        // than the window, or Unity would show a scrollbar with nothing to scroll), and narrower than
        // the window by the scrollbar so the rightmost slider handles aren't hidden under it.
        Rect viewRect = new Rect(0f, 0f, inRect.width - ScrollBarWidth, Mathf.Max(contentHeight, inRect.height));

        Widgets.BeginScrollView(inRect, ref scrollPosition, viewRect);

        var listing = new Listing_Standard();
        // Without this, Listing_Standard silently breaks into a *second column* the moment the
        // content passes listingRect.height (Listing.NewColumnIfNeeded), which defeats the scroll
        // view twice over: the overflow is drawn off to the right instead of below, and CurHeight —
        // the current column's cursor — never exceeds the visible height, so contentHeight stays
        // pinned at inRect.height and there is never anything to scroll.
        listing.maxOneColumn = true;
        listing.Begin(viewRect);

        // Presets first: picking a look sets the sliders further down, so the thing that overwrites
        // other controls should be seen before them, not after.
        DrawPresetSection(listing);
        listing.GapLine();
        DrawEffectToggles(listing);
        listing.GapLine();
        DrawAestheticKnobs(listing);

        // Read the height BEFORE End() — CurHeight tracks the listing's running cursor, and End()
        // is free to reset it.
        contentHeight = listing.CurHeight;
        listing.End();

        Widgets.EndScrollView();

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
        listing.CheckboxLabeled("Eave shadows", ref Settings.eaveShadows,
            "Let roofs cast shadows where they are not enclosing anything — porches, overhangs and the eaves that oversail a wall. Vanilla only ever casts shadows from buildings, so a porch roof throws nothing. Replaces the Perspective: Eaves mod, which is incompatible with this one.");
        // Read-only, deliberately. When a world-geometry mod is installed the planet's obliquity is
        // ITS setting, not ours, and offering a switch here would create a second source of truth for
        // one number — which is precisely the biome/sky disagreement these interops exist to remove.
        // A player who wants a different tilt changes it where it is defined. So this reports rather
        // than asks, and it is the only line on this screen that does.
        //
        // The same rule already governed Realistic Axial Tilt, which never had an opt-out; this is
        // what makes Planetsmith match it instead of being the one geometry source we second-guess.
        ShowObliquitySource(listing);

        listing.CheckboxLabeled("Night-sky radiance", ref Settings.nightRadiance,
            "Replace vanilla's flat night glow with a starlight + airglow + moonlight floor.");
        listing.CheckboxLabeled("Pitch-black nights", ref Settings.pitchBlackNights,
            "Darken the on-screen night overlay toward black as the night floor drops.");
        listing.CheckboxLabeled("Black unlit interiors", ref Settings.indoorSkyOcclusion,
            "Stop the sky lighting roofed cells. Vanilla always bleeds ~61% of the sky into every roofed tile, so a sealed cave never goes black; with this on, an unlit interior is lit by its lamps or not at all — day and night.");
        Settings.doorSkyLeak = LabeledSlider(listing, "  Light leaked through doors", Settings.doorSkyLeak, 0f, 0.5f);
        Settings.minIndoorBrightness = AestheticSlider(listing, "  Minimum indoor brightness", Settings.minIndoorBrightness, 0f, 1f);
        Settings.nativeSkyFalloffPassThroughPercent = AestheticSlider(listing, "  Sky brightness at an opening (no Ambient Light)",
            Settings.nativeSkyFalloffPassThroughPercent, 0f, 100f,
            "How bright a roofed cell right next to a door or window gap gets, before it tapers off with distance. Only used when the Ambient Light workshop mod is not installed — with it installed, that mod's own value is used instead.");
        Settings.nativeSkyFalloffMaxDepth = AestheticIntSlider(listing, "  How far the glow reaches (cells, no Ambient Light)",
            Settings.nativeSkyFalloffMaxDepth, 1, 24,
            "How many cells deep the gradient above reaches before fading to nothing. Higher = the glow carries further into a room. Only used when the Ambient Light workshop mod is not installed.");
        listing.CheckboxLabeled("Atmospheric night glow", ref Settings.atmosphericGlow,
            "The constant starlight + airglow floor. Off = only moonlight lights the night (true pitch-black on a moonless night).");
        Settings.minNightBrightness = AestheticSlider(listing, "  Minimum night brightness", Settings.minNightBrightness, 0f, 1f);
        listing.CheckboxLabeled("Low-light desaturation", ref Settings.lowLightDesaturation,
            "Drain colour toward a cool blue-grey as the sky darkens (the Purkinje shift).");
        listing.CheckboxLabeled("Weather dimming", ref Settings.weatherDimming,
            "Let clouds, rain and storms darken the sky and soften shadows, scaled by how hard it is coming down. Vanilla changes the sky's colour with the weather but never its brightness. Visual only — plant growth and solar panel output are unaffected.");
        listing.CheckboxLabeled("Partial cloud cover", ref Settings.cloudCover,
            "Let a Clear day drift toward an overcast look as cloud cover builds and clears, keyed on the biome's own season and rainfall so wetter places and wetter seasons see it more. Slow and continuous — a full hour-to-hour lattice, not a coin flip — and the weather label gains a \"- N% cloudy\" suffix to match. Visual only, and only while the weather itself reads Clear; it never changes which weather is rolled or how long it lasts.");
        listing.CheckboxLabeled("    \"- N% cloudy\" weather label", ref Settings.cloudCoverLabel,
            "Show the cloud percentage next to the weather name (e.g. \"Clear - 20% cloudy\"), including a reading of 0%. Off keeps the sky effect above but leaves the weather panel reading plain \"Clear\", same as vanilla.");
        listing.CheckboxLabeled("Sky colour-temperature", ref Settings.skyColorTemperature,
            "Warm the sky toward the horizon on a continuous, altitude-keyed curve.");
        listing.CheckboxLabeled("Polar night blue", ref Settings.polarNightBlue,
            "Tint the sky deep blue while the sun sits just below the horizon, the way real polar twilight reads. Sunlight arriving at that angle has crossed a long path through the ozone layer, which absorbs the orange and green out of it. Keyed on sun height alone, so high latitudes get it for hours or for whole winter days while the equator gets a brief blue hour after dusk. It also stops the deepest twilight crushing to black so the colour is actually visible — visual only, gameplay brightness is unaffected.");
        // LabeledSlider, not AestheticSlider: this is a per-effect intensity, not one of the taste
        // axes the preset bundle owns, so moving it must NOT flip the preset radio to Custom.
        Settings.polarNightBlueStrength = LabeledSlider(listing, "  Polar blue strength", Settings.polarNightBlueStrength, 0f, 1f);
        listing.CheckboxLabeled("Twilight purple light", ref Settings.purpleLight,
            "Turn the sky lavender for the couple of degrees of dusk where the reddened band low in the west and the ozone-blued vault overhead are both fully lit. Both of those sources are short of green — one because a long air path reddened it, the other because ozone absorbs the middle of the spectrum — so where they overlap the sky loses its green and goes purple. Roughly 15-25 minutes after sunset, and the same again before dawn. Colour only; it changes nothing outside that narrow window.");
        Settings.purpleLightStrength = LabeledSlider(listing, "  Purple light strength", Settings.purpleLightStrength, 0f, 1f);
        listing.CheckboxLabeled("Auroral sky tint", ref Settings.aurora,
            "Shift the night sky toward auroral colours during a solar flare or an aurora event, and at no other time. A flare gets a slow green/red shimmer; an aurora event borrows the colour vanilla is already cycling through, which its own sky render is too bright to show.");
        // The cost is in the LABEL, not only the tooltip. This is the one setting in the mod with a
        // real per-frame price, and a player who never hovers should still be told before they turn it
        // on — particularly since it is on by default.
        listing.CheckboxLabeled("    Auroral curtain (performance cost)", ref Settings.auroraCurtain,
            "Draw a drifting auroral curtain over the map instead of tinting the whole sky one flat colour — a bright wandering hem with vertical rays standing on it, several colours at once, folding and undulating.\n\nThis is the only part of the mod with a per-frame render cost. Measured on this machine's Mono runtime it is roughly 0.3-0.4 ms per frame of field regeneration — about 2% of a 60fps frame — plus one extra draw call. That is paid ONLY while an aurora or solar flare is actually running, which is rare and short; the rest of the time this subsystem is a single null check and allocates nothing.\n\nIf your framerate is already marginal, or a profiler points at CelestialLighting during an aurora, this is the switch. Turning it off falls back to the flat sky tint at its full solo strength, so you lose the curtain, not the aurora.");
        listing.CheckboxLabeled("Eclipse effects", ref Settings.eclipseDarkening,
            "Master toggle for CelestialLighting's eclipse handling. Off = vanilla eclipses (flat dim, storyteller timing) and none of the modes below. On = reshaped darkening plus the eclipse mode selected below.");
        DrawEclipseModeRadio(listing);
        DrawSunClockRadio(listing);
        listing.CheckboxLabeled("Blood-moon crimson (VRE – Sanguophage)", ref Settings.bloodMoon,
            "Recolour the moonlit night crimson while VRE – Sanguophage's blood-moon condition is active. Inert without that mod.");
    }

    // The three eclipse flavours (DESIGN.md §10). Only meaningful while "Eclipse effects" above is on
    // (the master); each option's tooltip spells out what fires and how it renders. Mirrors the preset
    // radio pattern below — RadioButton returns true only on the click that selects an option.
    // Reports which mod owns this planet's obliquity, and what it says, when one of them does.
    //
    // Silent otherwise: with neither installed the number is our own constant, there is nothing to
    // defer to and nothing a player could act on, so a line saying "23.44° (Celestial Lighting)"
    // would be noise on a screen that is otherwise all decisions.
    //
    // Reads AxialTiltCompat.ObliquityDegrees rather than either mod's field, so it reports the value
    // actually in force and therefore shows the RAT-wins precedence rather than restating it — with
    // both installed this reads RAT's tilt, which is the honest answer to "what is my sky using".
    private void ShowObliquitySource(Listing_Standard listing)
    {
        string source = ObliquitySourceName();
        if (source == null)
            return;

        listing.Label(
            $"Axial tilt: {AxialTiltCompat.ObliquityDegrees:0.#}°  (set by {source})",
            tooltip: $"This world's obliquity comes from {source}, so the sky matches the planet it "
                + "generated rather than Earth's 23.4°. Change it there — a second switch here would "
                + "just let the sky and the biomes disagree again.\n\nDay length is unaffected unless "
                + "you also pick realistic day length below."
                + SeasonPhaseNote());
    }

    // Realistic Planets 2 supplies a seasonal phase as well as a tilt, and that phase is a quarter of
    // a year ahead of the one RimWorld runs its temperatures and growing season on. The sun peaking a
    // fortnight before the warmest day is the most noticeable thing about following RP2's planet, so
    // it is said here rather than left to be discovered — it reads as a bug otherwise.
    //
    // Silent for every other source, because there is nothing to warn about: RAT moves the seasons
    // themselves, and Planetsmith supplies no phase at all.
    private string SeasonPhaseNote() =>
        !AxialTiltCompat.Active && RealisticPlanetsCompat.Active
            ? "\n\nRealistic Planets 2 also reckons the year a quarter earlier than RimWorld does, so "
                + "your longest day lands about 15 days before the warmest one. That is its calendar, "
                + "not a fault in the sky."
            : "";

    // RAT first, matching the precedence in AxialTiltCompat.SolarDeclinationDegrees: it owns the
    // running year's phase, tilt and moon together, so with both installed it is the one answering.
    private string ObliquitySourceName()
    {
        if (AxialTiltCompat.Active)
            return "Realistic Axial Tilt";

        if (RealisticPlanetsCompat.Active)
            return "Realistic Planets 2";

        return PlanetsmithCompat.Active ? "Planetsmith" : null;
    }

    // §14. Phrased around the consequence rather than the mechanism: "day length" is what a player
    // actually notices, and the realistic option's tooltip leads with the fact that it moves growing
    // hours, because that is the part that can surprise someone mid-colony.
    private void DrawSunClockRadio(Listing_Standard listing)
    {
        Text.Font = GameFont.Medium;
        listing.Label("Sun clock");
        Text.Font = GameFont.Small;
        listing.Label("Which sun decides when day starts and ends.");

        DrawSunClockOption(listing, SunClockMode.LockedToVanilla, "Vanilla day length  (default)",
            "Shadows, twilight and night darkness follow a real solar model, but the sun is warped to rise and set exactly when vanilla says. Day length, growing hours and solar power are untouched.");
        DrawSunClockOption(listing, SunClockMode.Realistic, "Realistic day length  (changes gameplay)",
            "Vanilla's daylight follows our physical sun instead. Real polar summers and winters, correct equinoxes at the poles, and a working southern hemisphere — but day length moves by about 1.5 hours on average, which shifts growing seasons and solar output.");
    }

    private void DrawSunClockOption(Listing_Standard listing, SunClockMode option, string label, string tooltip)
    {
        if (listing.RadioButton(label, Settings.sunClock == option, tabIn: 8f, tooltip))
            Settings.sunClock = option;
    }

    private void DrawEclipseModeRadio(Listing_Standard listing)
    {
        DrawEclipseModeOption(listing, EclipseMode.UnnaturalOnly, "Unnatural eclipse event only  (default)",
            "Purely cosmetic — no extra events. Only the storyteller's own random eclipse occurs, reshaped from vanilla's flat dim into a gradual fly-in / park / fly-out.");
        DrawEclipseModeOption(listing, EclipseMode.Both, "Natural + unnatural eclipse  (changes gameplay)",
            "Both at once: real geometric eclipses fire at astronomically-correct times (rendered with the natural transit ramp), AND the storyteller's random eclipses still occur (rendered with the fly-in / park / fly-out darkening). The geometric ones are extra real eclipses — rare, but they cost solar power and mood like any other.");
        DrawEclipseModeOption(listing, EclipseMode.NaturalOnly, "Natural eclipse only  (changes gameplay)",
            "Only real geometric eclipses, fired from the modeled moon at their short astronomically-correct duration (~one every few game years). The storyteller's random eclipse is suppressed so the two never double-fire, so eclipses become rarer and shorter than vanilla's.");
    }

    private void DrawEclipseModeOption(Listing_Standard listing, EclipseMode option, string label, string tooltip)
    {
        if (listing.RadioButton(label, Settings.eclipseMode == option, tabIn: 8f, tooltip))
            Settings.eclipseMode = option;
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
        listing.Label("These feed the shadow, night-darkness, and desaturation subsystems.");

        // Each slider marks the preset Custom only if the player actually moved it, so merely opening
        // the window never silently flips a chosen preset to Custom.
        Settings.shadowLengthScale = AestheticSlider(listing, "Shadow length", Settings.shadowLengthScale, 0.5f, 2.0f);
        Settings.shadowStrength = AestheticSlider(listing, "Shadow strength", Settings.shadowStrength, 0f, 1f);
        Settings.desaturation = AestheticSlider(listing, "Night desaturation", Settings.desaturation, 0f, 1f);
        Settings.weatherDimmingStrength = AestheticSlider(listing, "Weather dimming", Settings.weatherDimmingStrength, 0f, 0.5f);
    }

    // An aesthetic-knob slider: on a real change it records that the knobs no longer match a named
    // preset. Every slider backed by a PresetKnobs field uses this — including the two minimum-
    // brightness floors drawn up in the effects section, since a preset now sets those too. The
    // accessibility floor and the door leak are orthogonal to the presets and keep the plain
    // LabeledSlider, so touching them never flips the preset to Custom.
    private float AestheticSlider(Listing_Standard listing, string label, float value, float min, float max,
        string tooltip = null)
    {
        float updated = LabeledSlider(listing, label, value, min, max, tooltip);
        if (updated != value)
            Settings.MarkAestheticKnobsCustom();
        return updated;
    }

    // Int-backed counterpart to AestheticSlider, for the one PresetKnobs field that isn't a float
    // (NativeSkyFalloffMaxDepth is a cell count). Same Custom-flip-on-real-change behaviour as its
    // float sibling, built on LabeledIntSlider the same way AestheticSlider is built on LabeledSlider.
    private int AestheticIntSlider(Listing_Standard listing, string label, int value, int min, int max,
        string tooltip = null)
    {
        int updated = LabeledIntSlider(listing, label, value, min, max, tooltip);
        if (updated != value)
            Settings.MarkAestheticKnobsCustom();
        return updated;
    }

    // A plain labeled slider with no side effects on the preset selection. tooltip is optional so every
    // existing call site (none of which passed one) keeps compiling unchanged.
    private float LabeledSlider(Listing_Standard listing, string label, float value, float min, float max,
        string tooltip = null)
    {
        listing.Label($"{label}: {value:0.00}", tooltip: tooltip);
        return listing.Slider(value, min, max);
    }

    // Listing_Standard.Slider is float-only; this rounds for display and for the value handed back, so
    // an int-backed setting (NativeSkyFalloffSettings.MaxDepth is a cell count, not a fraction) still
    // gets a slider instead of the free-typed Verse.Dialog_Input this repo has no other use for.
    private int LabeledIntSlider(Listing_Standard listing, string label, int value, int min, int max,
        string tooltip = null)
    {
        listing.Label($"{label}: {value}", tooltip: tooltip);
        return Mathf.RoundToInt(listing.Slider(value, min, max));
    }
}
