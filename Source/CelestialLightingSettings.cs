using Verse;

namespace CelestialLighting;

// Persisted, user-tunable state for the whole mod. Thin Verse adapter over the pure
// CelestialSettingsMath: the aesthetic knob fields mirror PresetKnobs one-for-one, and ApplyPreset
// just copies a resolved bundle into them, so — per DESIGN.md — a preset is never a separate code
// path, only "a bundle of the same values".
//
// Field defaults describe the out-of-box experience: the mod ships on the Cinematic preset, so a
// first night looks good and stays readable (its two brightness floors sit at 0.50). Realistic is
// one click away for genuinely black nights.
public class CelestialLightingSettings : ModSettings
{
    // Which named bundle (if any) the aesthetic knobs currently reflect. Goes to Custom the instant
    // a player moves an individual slider, so the UI can show the aesthetic knobs as "no longer a
    // preset" without a separate dirty flag.
    public CelestialPreset preset = CelestialPreset.Cinematic;

    // --- Aesthetic knobs (mirror PresetKnobs; placeholders until §1/§7/§9 land) ---
    public float shadowLengthScale = Presets.Cinematic.ShadowLengthScale;
    public float shadowStrength = Presets.Cinematic.ShadowStrength;
    public float desaturation = Presets.Cinematic.Desaturation;
    public float weatherDimmingStrength = Presets.Cinematic.WeatherDimming;

    // --- Per-effect on/off toggles (drive the CelestialLightingFeatures flags each patch reads;
    //     default true == the shipped, everything-on behaviour). These make the settings screen the
    //     user-facing front-end of the same flags the dev harness flips via its SetFeature step. ---
    public bool civilTwilightPersistence = true;
    public bool penumbraContrast = true;
    public bool moonShadows = true;
    public bool nightRadiance = true;
    public bool lowLightDesaturation = true;
    public bool skyColorTemperature = true;
    public bool polarNightBlue = true;
    public bool purpleLight = true;
    public bool aurora = true;

    // §11a's ribbon curtain, a sub-toggle of `aurora` above. Separate because it is the one part of the
    // aurora with a per-frame render cost, so a player on a weak machine (or one who simply prefers the
    // plain tint) can drop it without losing auroras entirely — and because turning it off restores
    // §11's stronger solo tint rather than leaving a weaker sky. See CelestialLightingFeatures
    // .AuroraCurtain.
    public bool auroraCurtain = true;

    public bool eclipseDarkening = true;
    public bool bloodMoon = true;
    public bool pitchBlackNights = true;
    public bool indoorSkyOcclusion = true;
    public bool weatherDimming = true;
    public bool cloudCover = true;

    // §22's weather-label "- N% cloudy" suffix, a sub-toggle of cloudCover above — same relationship
    // as auroraCurtain to aurora. See CelestialLightingFeatures.CloudCoverLabel for why this covers
    // only the UI readout, not the sky tint.
    public bool cloudCoverLabel = true;

    // §25's drawn cloud sheets — the clouds themselves, moving over the map, as opposed to the sky
    // colour the master toggle above shifts. The other sub-toggle of cloudCover, and the same
    // relationship: cloudCover says whether this mod has an opinion about cloud at all, this says
    // whether that opinion is drawn as objects. It is also the one toggle here that changes what is
    // ON the map rather than what colour it is, which is why it gets its own switch rather than
    // riding the master. See CelestialLightingFeatures.CloudSheet.
    public bool cloudSheet = true;

    // §25c: the volumetric renderer for the sheets above. Default true — see
    // CelestialLightingFeatures.CloudVolume for why it ships on with the baked path as the
    // performance option rather than the other way round.
    public bool cloudVolume = true;

    public bool eaveShadows = true;

    // §27 vector light sources. SHIPS OFF: it is the most opinionated thing in the mod, because
    // light vanilla delivered around a corner no longer arrives at all and indirectly lit rooms are
    // genuinely darker. That is the feature working, and it is still a large enough taste call to be
    // opt-in until it has been lived with.
    //
    // The composition underneath is deliberately NOT exposed. Mask and beam are what §27 is designed
    // around, and the crossfade survives only as the fallback the code picks for itself when the
    // per-emitter glow arrays cannot be read — a player has no way to judge that choice and no
    // reason to be asked about it.
    public bool vectorLights = false;

    // The one part of §27 that draws a new OBJECT rather than recolouring an existing one, which is
    // why it gets its own switch rather than riding the master — the same reasoning as §25's visible
    // clouds. Inert while vectorLights is off.
    public bool vectorLightPawnShadows = true;

    // --- Night-radiance tunables (drive NightRadianceSettings.Current) ---
    // The atmospheric starlight+airglow floor ("true pitch-black" when off), and the pitch-black
    // overlay's minimum-brightness clamp (0 == genuinely black nights; raise it for playability).
    // minNightBrightness is part of the preset bundle, so its default tracks the shipped preset
    // rather than NightRadianceMath.DefaultMinNightBrightness — the math-layer constant is still the
    // subsystem's own neutral value, it is just no longer what we ship.
    public bool atmosphericGlow = true;
    public float minNightBrightness = Presets.Cinematic.MinNightBrightness;

    // How strong §19's polar-night blue is. Scales BOTH arms — the colour nudge and the visual
    // brightness floor — so 0 is a true no-op rather than "no tint but the nights are still lifted".
    // Deliberately NOT in PresetKnobs: this is a per-effect intensity like purpleLightStrength, not one
    // of the taste axes the Realistic/Cinematic bundle correlates along. (The FLOOR does interact with
    // presets, in the good direction: inert under Cinematic's 0.50 minNightBrightness, load-bearing
    // under Realistic's 0.)
    public float polarNightBlueStrength = 1f;

    // §19c. Unlike polarNightBlueStrength this scales a single arm — §19c has no brightness half —
    // so 0 is exactly "feature off" and needs no preset-specific caveat.
    public float purpleLightStrength = 1f;

    // --- Indoor sky-occlusion tunables (drive IndoorOcclusionSettings.Current) ---
    // How much sky a roofed cell keeps regardless of how sealed it is. 0 = sealed rooms go fully
    // black; raise it to keep interiors readable without disabling the effect or brightening the
    // outdoors via the map-wide accessibility floor. Also part of the preset bundle (see
    // minNightBrightness above), so it defaults to the shipped preset's value, not the math layer's.
    public float minIndoorBrightness = Presets.Cinematic.MinIndoorBrightness;

    // --- §7c native sky falloff tunables (drive NativeSkyFalloffSettings.Current). Only visible in
    // practice while the Ambient Light workshop mod is absent — SkyFalloffSource defers to that mod
    // outright when present, so these two never feed a cell it already answered. Part of the preset
    // bundle (see minIndoorBrightness above) rather than one global default: live playtesting settled
    // on two different values per preset, not one — Cinematic's 0.50 minIndoorBrightness floor already
    // lifts every roofed cell most of the way up, so it needs a much stronger PassThroughPercent for the
    // opening to still read as brighter than the rest of the room, where Realistic's floor of 0 leaves
    // the same job to a much smaller push. See DESIGN.md §7c for the measured values (Realistic 35%/8
    // cells, Cinematic 80%/10 cells). NativeSkyFalloffMath.DefaultPassThroughPercent/DefaultMaxDepth
    // remain the math layer's own neutral fallback (used only by NativeSkyFalloffSettings.Defaults and
    // its offline tests) — not what either preset ships. ---
    public float nativeSkyFalloffPassThroughPercent = Presets.Cinematic.NativeSkyFalloffPassThroughPercent;
    public int nativeSkyFalloffMaxDepth = Presets.Cinematic.NativeSkyFalloffMaxDepth;

    // §7d door-strength weighting (DoorLeakMath) — how much a stronger-than-wood door dims the native
    // flood as it crosses, not one of the taste axes above: an all-wood-door game must read identically
    // regardless of preset, so this is a plain global default rather than a PresetKnobs field, the same
    // reasoning polarNightBlueStrength/purpleLightStrength already apply to their own per-effect knobs.
    public float doorStrengthSensitivity = DoorLeakMath.DefaultSensitivity;

    // --- Eclipse (drives EclipseSettings.Mode) — which eclipse flavour(s) are live. Defaults to
    //     UnnaturalOnly: reshape the darkening of the storyteller's own eclipse, fire nothing of our
    //     own, because natural eclipses are real events and no default here may alter gameplay. The
    //     other two flavours are opt-in. See EclipseSettings for the full rationale. ---
    public EclipseMode eclipseMode = EclipseMode.UnnaturalOnly;

    // --- §14 sun clock. Locked (default) keeps vanilla's day length exactly; realistic makes vanilla
    //     follow our physical sun, which is a gameplay change (growing hours, solar output). ---
    public SunClockMode sunClock = SunClockMode.LockedToVanilla;

    // Copies a named preset's bundle into the aesthetic knob fields. Kept trivial and delegating to
    // the pure resolver so the correlation between knobs lives in exactly one tested place.
    public void ApplyPreset(CelestialPreset chosen)
    {
        PresetKnobs knobs = Presets.Resolve(chosen);
        shadowLengthScale = knobs.ShadowLengthScale;
        shadowStrength = knobs.ShadowStrength;
        desaturation = knobs.Desaturation;
        weatherDimmingStrength = knobs.WeatherDimming;
        minNightBrightness = knobs.MinNightBrightness;
        minIndoorBrightness = knobs.MinIndoorBrightness;
        nativeSkyFalloffPassThroughPercent = knobs.NativeSkyFalloffPassThroughPercent;
        nativeSkyFalloffMaxDepth = knobs.NativeSkyFalloffMaxDepth;
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
        // Retires every cached per-frame geometry answer, because the values written just below are
        // inputs to several of them. Unconditional and first, so nothing can read a value derived
        // from the settings this call is in the middle of replacing. A no-op at today's one-frame
        // memo span — see SettingsGeneration for why it is wired up anyway, and why it is not
        // change-detected the way the redraw hooks are.
        SettingsGeneration.Bump();

        CelestialLightingFeatures.CivilTwilightPersistence = civilTwilightPersistence;
        CelestialLightingFeatures.PenumbraContrast = penumbraContrast;
        CelestialLightingFeatures.MoonShadows = moonShadows;
        CelestialLightingFeatures.NightRadiance = nightRadiance;
        CelestialLightingFeatures.LowLightDesaturation = lowLightDesaturation;
        CelestialLightingFeatures.SkyColorTemperature = skyColorTemperature;
        CelestialLightingFeatures.PolarNightBlue = polarNightBlue;
        OzoneTwilightSettings.TintStrength = polarNightBlueStrength;
        CelestialLightingFeatures.PurpleLight = purpleLight;
        PurpleLightSettings.TintStrength = purpleLightStrength;
        CelestialLightingFeatures.Aurora = aurora;
        CelestialLightingFeatures.AuroraCurtain = auroraCurtain;
        CelestialLightingFeatures.EclipseDarkening = eclipseDarkening;
        CelestialLightingFeatures.BloodMoon = bloodMoon;
        CelestialLightingFeatures.PitchBlackNights = pitchBlackNights;
        CelestialLightingFeatures.IndoorSkyOcclusion = indoorSkyOcclusion;
        CelestialLightingFeatures.WeatherDimming = weatherDimming;
        CelestialLightingFeatures.CloudCover = cloudCover;
        CelestialLightingFeatures.CloudCoverLabel = cloudCoverLabel;
        CelestialLightingFeatures.CloudSheet = cloudSheet;
        CelestialLightingFeatures.CloudVolume = cloudVolume;

        // SyncTo rather than a plain assignment, and the order matters: half of §27 is BAKED into
        // the lighting overlay's vertex colours during a section regenerate, so flipping the switch
        // changes what should be on screen without changing anything that would provoke a rebake.
        // The map would keep rendering the previous answer until the player happened to build
        // something — which for a settings screen means the toggle looks broken.
        CelestialLightingFeatures.VectorLightPawnShadows = vectorLightPawnShadows;
        VectorLightRedraw.SyncTo(vectorLights);
        CelestialLightingFeatures.VectorLights = vectorLights;
        CelestialLightingFeatures.EaveShadows = eaveShadows;
        // One player-facing switch drives both halves of §15 — the split flag exists only so the
        // harness can isolate them (see CelestialLightingFeatures.EaveShade). A shipped game must
        // never see the caster on with the shade off: that combination is exactly the bright-lip
        // artifact §15b was written to remove.
        CelestialLightingFeatures.EaveShade = eaveShadows;

        // §9's tint strength. This slider was persisted and then ignored by every earlier version of
        // this method; it scales the per-cell night tint now that §9 no longer touches the global
        // saturation post-process and the tint is the whole effect.
        PurkinjeSettings.TintStrength = desaturation;

        // §1's two Look sliders. Read by Patch_ShadowDirection (vector length + LightInfo
        // intensity) and Patch_ShadowStrength (the shader alpha) through one shared static, so the
        // length and the opacity can never end up on different presets.
        ShadowSettings.LengthScale = shadowLengthScale;
        ShadowSettings.Strength = shadowStrength;

        NightRadianceSettings.Current.AtmosphericGlowEnabled = atmosphericGlow;
        NightRadianceSettings.Current.MinNightBrightness = minNightBrightness;

        WeatherDimmingSettings.MaxDimming = weatherDimmingStrength;

        EclipseSettings.Mode = eclipseMode;
        CelestialLightingFeatures.SunClock = sunClock;

        // Roofed cells never take sky glow (GlowGrid.GroundGlowAt returns early for them), so the
        // only way to hold an interior above black is this floor, applied as a cap on occlusion.
        IndoorOcclusionSettings.Current.MinIndoorBrightness = minIndoorBrightness;

        // §7c's own two knobs — see SkyFalloffSource for why they only matter without Ambient Light.
        NativeSkyFalloffSettings.Current.MaxDepth = nativeSkyFalloffMaxDepth;
        NativeSkyFalloffSettings.Current.PassThroughPercent = nativeSkyFalloffPassThroughPercent;

        // §7d — only ever multiplies the native flood's strength, so it is a no-op whenever the
        // Ambient Light passthrough is answering instead (see DoorLeakMath's own header).
        NativeSkyFalloffSettings.Current.DoorStrengthSensitivity = doorStrengthSensitivity;

        // §7b's alphas live in baked section meshes, not in a per-frame material, so a change here is
        // invisible until the meshes are rebuilt. Must run after the assignments above.
        IndoorOcclusionRedraw.SyncTo(
            indoorSkyOcclusion, IndoorOcclusionSettings.Current.MinIndoorBrightness,
            NativeSkyFalloffSettings.Current.MaxDepth, NativeSkyFalloffSettings.Current.PassThroughPercent,
            NativeSkyFalloffSettings.Current.DoorStrengthSensitivity);

        // §15's caster heights are baked into the sun-shadow section meshes for the same reason, so
        // the eave toggle needs its own rebuild. Separate from the call above because the two write
        // different mesh layers and dirty different flags.
        EaveShadowRedraw.SyncTo(eaveShadows);

        // §9's per-cell wash joined the same club when SectionLayer_NightDesaturation started skipping
        // its bake while the feature is off: turning the box back on has to dirty the map, or the wash
        // stays missing until the next lamp or roof edit happens to rebuild the sections.
        NightDesaturationRedraw.SyncTo(lowLightDesaturation);
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref preset, "preset", CelestialPreset.Cinematic);
        Scribe_Values.Look(ref civilTwilightPersistence, "civilTwilightPersistence", true);
        Scribe_Values.Look(ref penumbraContrast, "penumbraContrast", true);
        Scribe_Values.Look(ref moonShadows, "moonShadows", true);
        Scribe_Values.Look(ref nightRadiance, "nightRadiance", true);
        Scribe_Values.Look(ref lowLightDesaturation, "lowLightDesaturation", true);
        Scribe_Values.Look(ref weatherDimming, "weatherDimming", true);
        Scribe_Values.Look(ref cloudCover, "cloudCover", true);
        Scribe_Values.Look(ref cloudCoverLabel, "cloudCoverLabel", true);
        Scribe_Values.Look(ref cloudSheet, "cloudSheet", true);
        Scribe_Values.Look(ref cloudVolume, "cloudVolume", true);
        Scribe_Values.Look(ref vectorLights, "vectorLights", false);
        Scribe_Values.Look(ref vectorLightPawnShadows, "vectorLightPawnShadows", true);
        Scribe_Values.Look(ref skyColorTemperature, "skyColorTemperature", true);
        Scribe_Values.Look(ref polarNightBlue, "polarNightBlue", true);
        Scribe_Values.Look(ref polarNightBlueStrength, "polarNightBlueStrength", 1f);
        Scribe_Values.Look(ref purpleLight, "purpleLight", true);
        Scribe_Values.Look(ref purpleLightStrength, "purpleLightStrength", 1f);
        Scribe_Values.Look(ref aurora, "aurora", true);
        Scribe_Values.Look(ref auroraCurtain, "auroraCurtain", true);
        Scribe_Values.Look(ref eclipseDarkening, "eclipseDarkening", true);
        Scribe_Values.Look(ref bloodMoon, "bloodMoon", true);
        Scribe_Values.Look(ref pitchBlackNights, "pitchBlackNights", true);
        Scribe_Values.Look(ref indoorSkyOcclusion, "indoorSkyOcclusion", true);
        Scribe_Values.Look(ref eaveShadows, "eaveShadows", true);
        Scribe_Values.Look(ref minIndoorBrightness, "minIndoorBrightness", Presets.Cinematic.MinIndoorBrightness);
        Scribe_Values.Look(ref nativeSkyFalloffPassThroughPercent, "nativeSkyFalloffPassThroughPercent",
            Presets.Cinematic.NativeSkyFalloffPassThroughPercent);
        Scribe_Values.Look(ref nativeSkyFalloffMaxDepth, "nativeSkyFalloffMaxDepth",
            Presets.Cinematic.NativeSkyFalloffMaxDepth);
        Scribe_Values.Look(ref doorStrengthSensitivity, "doorStrengthSensitivity", DoorLeakMath.DefaultSensitivity);
        Scribe_Values.Look(ref atmosphericGlow, "atmosphericGlow", true);
        Scribe_Values.Look(ref minNightBrightness, "minNightBrightness", Presets.Cinematic.MinNightBrightness);
        // The default moved from Both to UnnaturalOnly. Scribe_Values omits a value equal to its
        // default, so a config written before the change has no eclipseMode node and reads back as
        // UnnaturalOnly — the intended migration, and free of the usual "silently reset a deliberate
        // choice" cost because the mod was still Workshop-private when this changed.
        Scribe_Values.Look(ref eclipseMode, "eclipseMode", EclipseMode.UnnaturalOnly);
        Scribe_Values.Look(ref sunClock, "sunClock", SunClockMode.LockedToVanilla);
        Scribe_Values.Look(ref shadowLengthScale, "shadowLengthScale", Presets.Cinematic.ShadowLengthScale);
        Scribe_Values.Look(ref shadowStrength, "shadowStrength", Presets.Cinematic.ShadowStrength);
        Scribe_Values.Look(ref desaturation, "desaturation", Presets.Cinematic.Desaturation);
        Scribe_Values.Look(ref weatherDimmingStrength, "weatherDimmingStrength",
            Presets.Cinematic.WeatherDimming);
    }
}
