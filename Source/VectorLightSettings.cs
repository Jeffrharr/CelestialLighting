namespace CelestialLighting;

// §27's runtime tunables, in the same shape as ShadowSettings, CloudSheetSettings and
// IndoorOcclusionSettings: a plain static the render path reads, written once by
// CelestialLightingSettings.ApplyToRuntime, so neither the field nor the pure core takes a
// dependency on a ModSettings instance existing.
//
// NOT IN CelestialLightingFeatures, and the boundary is worth keeping straight. That file holds a
// BOOL and a string key per effect, because its keys are what the harness's SetFeature step drives
// and SetFeature can flip a bool and nothing else. A float has no business there — see
// Probes/VectorLightReachOverride for how a float knob gets moved inside one boot instead.
public static class VectorLightSettings
{
    // How far past its own glowRadius a lamp is drawn, as a multiplier. See VectorLightReachMath —
    // in particular why the resting value is exactly 1 and why that has to stay exact.
    public static float Reach = VectorLightReachMath.NoReach;
}
