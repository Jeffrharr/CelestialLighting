using RimWorld;
using RimWorldTestHarness.Mod.Features;
using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// The one place the harness and the shipped mod are bridged. This dev-only assembly is the only one
// that references BOTH RimWorldTestHarness and CelestialLighting, so it is where probes get exposed
// to the harness's Probe step and feature flags get exposed to its SetFeature step. Neither the
// shipped CelestialLighting.dll nor RimWorldTestHarness.dll references the other — see
// RimWorldTestHarness/DESIGN.md's "Where probe tests live".
[StaticConstructorOnStartup]
public static class ProbeRegistration
{
    static ProbeRegistration()
    {
        ProbeRegistry.Register(new ShadowLeanProbe());
        ProbeRegistry.Register(new CivilTwilightProbe());
        ProbeRegistry.Register(new PenumbraProbe());
        ProbeRegistry.Register(new MoonIlluminationProbe());
        ProbeRegistry.Register(new NightRadianceProbe());
        ProbeRegistry.Register(new PurkinjeProbe());
        // §9's applied strength, added after two versions shipped whose rod-vision factor was right
        // and whose effect on screen was nil. PurkinjeProbe says how dark the sky is; this says how
        // much desaturation that actually turned into.
        ProbeRegistry.Register(new NightDesaturationProbe());
        ProbeRegistry.Register(new SkyColorTemperatureProbe());
        ProbeRegistry.Register(new AuroraTintProbe());
        ProbeRegistry.Register(new EclipseCoverageProbe());
        ProbeRegistry.Register(new BloodMoonProbe());
        ProbeRegistry.Register(new BrightnessFloorProbe());
        // §6a's two instruments. moon_shadow_render measures the composed shadow colour the shader
        // actually uses, which is the only reliable way to test a moon shadow — a screenshot A/B of a
        // night scene moves pixels by 1-3/255 and cannot be told apart from weather and pawn motion.
        // moon_elevation exists so a scenario can prove the moon was actually in the sky, since
        // moon_illumination reports phase alone and a full moon can be below the horizon.
        ProbeRegistry.Register(new MoonShadowRenderProbe());
        ProbeRegistry.Register(new MoonElevationProbe());
        ProbeRegistry.Register(new WeatherDimmingProbe());
        // Raw gameplay glow, so the weather_dimming scenario can assert §13's central negative: the
        // sky visibly darkens under a storm while this value does not move at all.
        ProbeRegistry.Register(new SkyGlowProbe());
        // §14: one number that says whether vanilla's sky and our sun agree about day/night.
        ProbeRegistry.Register(new SunClockDisagreementProbe());
        ProbeRegistry.Register(new SunElevationProbe());

        // Expose CelestialLighting's runtime feature flags to the harness's SetFeature step so a
        // scenario can screenshot an effect off then on. The setter just writes the shipped mod's
        // static flag; in production nothing calls it and the flag stays at its default (on).
        FeatureRegistry.Register(
            CelestialLightingFeatures.CivilTwilightPersistenceKey,
            enabled => CelestialLightingFeatures.CivilTwilightPersistence = enabled);
        FeatureRegistry.Register(
            CelestialLightingFeatures.PenumbraContrastKey,
            enabled => CelestialLightingFeatures.PenumbraContrast = enabled);
        FeatureRegistry.Register(
            CelestialLightingFeatures.MoonShadowsKey,
            enabled => CelestialLightingFeatures.MoonShadows = enabled);
        FeatureRegistry.Register(
            CelestialLightingFeatures.NightRadianceKey,
            enabled => CelestialLightingFeatures.NightRadiance = enabled);
        FeatureRegistry.Register(
            CelestialLightingFeatures.WeatherDimmingKey,
            enabled => CelestialLightingFeatures.WeatherDimming = enabled);
        // Not a CelestialLightingFeatures flag: this bridges the "true pitch-black" atmospheric-floor
        // switch that lives on NightRadianceSettings, so a probe scenario can drop the constant
        // starlight+airglow floor out of the night_radiance sum and watch only moonlight remain.
        FeatureRegistry.Register(
            CelestialLightingFeatures.NightAtmosphericGlowKey,
            enabled => NightRadianceSettings.Current.AtmosphericGlowEnabled = enabled);
        FeatureRegistry.Register(
            CelestialLightingFeatures.LowLightDesaturationKey,
            enabled => CelestialLightingFeatures.LowLightDesaturation = enabled);
        FeatureRegistry.Register(
            CelestialLightingFeatures.SkyColorTemperatureKey,
            enabled => CelestialLightingFeatures.SkyColorTemperature = enabled);
        FeatureRegistry.Register(
            CelestialLightingFeatures.AuroraKey,
            enabled => CelestialLightingFeatures.Aurora = enabled);
        FeatureRegistry.Register(
            CelestialLightingFeatures.EclipseDarkeningKey,
            enabled => CelestialLightingFeatures.EclipseDarkening = enabled);
        FeatureRegistry.Register(
            CelestialLightingFeatures.BloodMoonKey,
            enabled => CelestialLightingFeatures.BloodMoon = enabled);
        // Not a CelestialLightingFeatures flag: the accessibility minimum-brightness floor lives on
        // CelestialLightingSettings. Bridged so a scenario can flip it and pin the floor end-to-end.
        FeatureRegistry.Register(
            "brightness_floor",
            enabled =>
            {
                CelestialLightingSettings settings = CelestialLightingSettingsMod.Settings;
                if (settings != null)
                {
                    settings.brightnessFloorEnabled = enabled;
                    // Push through to the runtime statics: the floor also reaches sealed interiors via
                    // §7b's occlusion cap (roofed cells never take sky glow, so lifting CurSkyGlow alone
                    // cannot brighten them), and that path needs the baked meshes rebuilt.
                    settings.ApplyToRuntime();
                }
            });
        FeatureRegistry.Register(
            CelestialLightingFeatures.PitchBlackNightsKey,
            enabled => CelestialLightingFeatures.PitchBlackNights = enabled);
        // Not a CelestialLightingFeatures flag: the eclipse mode lives on EclipseSettings (mirrors the
        // mod's own eclipse-mode radio). Bridged as a bool for the harness's SetFeature step: enabled
        // => NaturalOnly (pure geometric eclipses, so a scenario films/validates the §10a trigger in
        // isolation), disabled => Both (the shipped default).
        FeatureRegistry.Register(
            "natural_eclipse",
            enabled => EclipseSettings.Mode = enabled ? EclipseMode.NaturalOnly : EclipseMode.Both);
        // Dev-only staging for the natural-eclipse trigger validation: a real eclipse only happens
        // once every few game years, so this phase-slides the modeled moon (via the pure EclipseStaging
        // math) onto a genuine new-moon-at-node alignment one pre-roll ahead of "now", after which the
        // real trigger detects the transit from real geometry and fires a real Eclipse. Disabling it
        // clears the shifts so the moon returns to its true phase. Never touched by the shipped mod.
        FeatureRegistry.Register(
            "eclipse_stage_alignment",
            enabled =>
            {
                GameComponent_MoonPhase moon = GameComponent_MoonPhase.Current;
                if (moon == null)
                    return;

                if (!enabled)
                {
                    moon.debugSynodicShiftTicks = 0L;
                    moon.debugNodalShiftTicks = 0L;
                    return;
                }

                EclipseStaging.AlignmentShifts shifts = EclipseStaging.ComputeAlignmentShifts(
                    Find.TickManager.TicksAbs,
                    (long)(moon.synodicPeriodDays * GenDate.TicksPerDay),
                    (long)(moon.nodalPeriodDays * GenDate.TicksPerDay),
                    GenDate.TicksPerDay,
                    Formulas.DaysPerYear,
                    EclipseStaging.DefaultPreRollTicks);
                moon.debugSynodicShiftTicks = shifts.SynodicShiftTicks;
                moon.debugNodalShiftTicks = shifts.NodalShiftTicks;
            });
        // Not a CelestialLightingFeatures flag: bridges the minimum-brightness clamp so a visual
        // scenario can force a genuinely pitch-black night (MinNightBrightness -> 0) instead of the
        // shipped playable floor. "enabled" == true means clamp to 0 (true black); false restores the
        // default playable floor.
        // §7b indoor sky occlusion. The flag write alone is not enough: unlike every other effect here,
        // §7b's output lives in baked section meshes rather than in a per-frame material, so a scenario
        // toggling it must also force those meshes to regenerate or both A/B screenshots show the same
        // pre-toggle bake.
        FeatureRegistry.Register(
            CelestialLightingFeatures.IndoorSkyOcclusionKey,
            enabled =>
            {
                CelestialLightingFeatures.IndoorSkyOcclusion = enabled;
                IndoorOcclusionRedraw.ForceRebuild();
            });
        // Not a CelestialLightingFeatures flag: bridges §7b's minimum-indoor-brightness slider so a
        // visual scenario can A/B a sealed room at full black against one held above it. "enabled" ==
        // true means raise the floor to a clearly-visible 0.25; false restores the shipped 0 (black).
        // Rebuilds the baked meshes for the same reason the occlusion toggle does.
        FeatureRegistry.Register(
            "indoor_min_brightness",
            enabled =>
            {
                IndoorOcclusionSettings.Current.MinIndoorBrightness =
                    enabled ? 0.25f : IndoorOcclusionMath.DefaultMinIndoorBrightness;
                IndoorOcclusionRedraw.ForceRebuild();
            });
        // Not a CelestialLightingFeatures flag: flips §14's warp so a scenario can capture the
        // pre-§14 behaviour as the BEFORE half of an A/B. "enabled" false == no warp == the artifact.
        FeatureRegistry.Register(
            "sun_clock_warp",
            enabled => SunClockAdapter.WarpEnabled = enabled);
        FeatureRegistry.Register(
            "pitch_black_true",
            enabled => NightRadianceSettings.Current.MinNightBrightness =
                enabled ? 0f : NightRadianceMath.DefaultMinNightBrightness);
    }
}
