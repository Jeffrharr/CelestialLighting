namespace CelestialLighting;

// The single source of truth for how strong §13 weather dimming is. Pulled out into its own tiny
// static — the same shape as EclipseSettings — so the sky-tint patch, the shadow patch, §9's
// desaturation and the dev probe all read one value and can never disagree about how heavy the
// weather currently is.
//
// The on/off decision lives in CelestialLightingFeatures.WeatherDimming; this is only the magnitude.
// Both are independently a full no-op: the feature off early-returns, and MaxDimming at 0 makes
// every term in WeatherDimmingMath vanish. Backed by CelestialLightingSettings.weatherDimmingStrength
// via ApplyToRuntime, so the choice survives a restart while this stays the field everything reads.
public static class WeatherDimmingSettings
{
    public static float MaxDimming = WeatherDimmingMath.DefaultMaxDimming;
}
