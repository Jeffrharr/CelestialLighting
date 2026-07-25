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
        ProbeRegistry.Register(new SkyColorTemperatureProbe());
        ProbeRegistry.Register(new AuroraTintProbe());
        ProbeRegistry.Register(new EclipseCoverageProbe());
        ProbeRegistry.Register(new BloodMoonProbe());
        ProbeRegistry.Register(new BrightnessFloorProbe());

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
        FeatureRegistry.Register(
            "pitch_black_true",
            enabled => NightRadianceSettings.Current.MinNightBrightness =
                enabled ? 0f : NightRadianceMath.DefaultMinNightBrightness);
    }
}
