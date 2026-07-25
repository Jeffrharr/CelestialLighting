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
    }
}
