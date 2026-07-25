namespace CelestialLighting;

// How strong §9's night tint is, in one tiny static — the same shape as EclipseSettings and
// WeatherDimmingSettings, so the patch and any probe read one value and cannot disagree.
//
// This is what the "Night desaturation" slider drives. That setting was persisted from the day the
// settings screen landed but never applied to anything (CelestialLightingSettings.ApplyToRuntime
// simply did not mention it, and the UI admitted as much with "Not all are wired up yet"), because
// the strength it would logically have controlled — PurkinjeMath.MaxSaturationDrop — fed the global
// SkyColorSet.saturation multiply. Now that §9 works entirely through the per-cell tint, the slider
// has something real and local to scale, so it is wired here.
//
// 1 == the shipped look. 0 == no tint at all, i.e. equivalent to turning the feature off.
public static class PurkinjeSettings
{
    public static float TintStrength = 1f;
}
