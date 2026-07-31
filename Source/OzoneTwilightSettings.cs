namespace CelestialLighting;

// How strong §19's polar-night blue is, in one tiny static — same shape as PurkinjeSettings,
// EclipseSettings and WeatherDimmingSettings, so the patch and any probe read one value and cannot
// disagree.
//
// This is what the "Polar blue strength" slider drives. It scales BOTH arms of the subsystem: the
// colour nudge in Patch_PolarNightBlue and the visual brightness floor threaded into §7a through
// OzoneTwilightMath.OverlayFloor. Scaling both from one knob is what makes 0 a true no-op rather
// than "no tint but the nights are still lifted", which is the property the harness A/B depends on.
//
// 1 == the shipped look. 0 == no tint and no floor, i.e. equivalent to turning the feature off.
public static class OzoneTwilightSettings
{
    public static float TintStrength = 1f;
}
