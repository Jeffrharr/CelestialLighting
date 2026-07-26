namespace CelestialLighting;

// The single source of truth for the two shadow "Look" sliders, in the same shape as
// WeatherDimmingSettings and EclipseSettings: one tiny static the patches read, backed by
// CelestialLightingSettings via ApplyToRuntime so the choice survives a restart.
//
// Two patches own a shadow between them — Patch_ShadowDirection writes the vector and
// LightInfo.intensity, Patch_ShadowStrength writes GenCelestial.CurShadowStrength, which is the
// alpha the shader actually renders with — and both must apply the same knob or the mod ships a
// shadow whose length and opacity disagree about which preset is active. Reading one static from
// both is the same discipline SolarPosition already enforces for the sun position itself.
//
// Both default to 1.0 (the identity), so a fresh install with no settings blob, and any code path
// that runs before ApplyToRuntime, renders exactly the physical model with no knob applied.
public static class ShadowSettings
{
    // Multiplier on shadow LENGTH, matching the "Shadow length" slider (0.5 - 2.0). Applied via
    // Formulas.ScaleShadowVector, which re-clamps to vanilla's own MaxShadowLength.
    public static float LengthScale = 1f;

    // Multiplier on shadow OPACITY, matching the "Shadow strength" slider (0 - 1). Applied via
    // Formulas.ScaleShadowStrength. At 0 the mod draws no cast shadow at all, which is the honest
    // meaning of a strength slider at its floor.
    public static float Strength = 1f;
}
