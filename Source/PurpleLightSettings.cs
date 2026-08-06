namespace CelestialLighting;

// How strong §19c's twilight purple light is, in one tiny static — the same shape as
// OzoneTwilightSettings, PurkinjeSettings and WeatherDimmingSettings, so the patch and any probe
// read one value and cannot disagree.
//
// This is what the "Purple light strength" slider drives. §19c has only one arm (the colour nudge;
// there is no brightness half the way §19 has an overlay floor), so scaling it is simply scaling
// the effect, and 0 is a true no-op identical to turning the feature off — the property the harness
// A/B depends on.
//
// 1 == the shipped look. 0 == no purple at all.
public static class PurpleLightSettings
{
    public static float TintStrength = 1f;
}
