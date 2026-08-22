using Verse;

namespace CelestialLighting;

// The one place that answers "how much of vanilla's overlay brightness is §7a currently keeping on this
// map" — the `keep` factor Patch_PitchBlackOverlay lerps MatBases.LightOverlay toward black by.
//
// Extracted because it now has a SECOND consumer. §7b's baked sky cover divides the indoor floor by this
// same factor (IndoorOcclusionMath.EffectiveIndoorFloor) so the two brightness floors stop compounding,
// and a compensation computed from a *different* keep than the one actually applied to the material would
// not cancel — it would leave a residue that varies with the sun and reads as the interior breathing
// against the sky. Two copies of this chain would have drifted the first time §19 or Anomaly touched one
// of them, which is exactly what the ozone floor and the UnnaturalDarkness clamp below are.
//
// Deliberately NOT a memo. It is a handful of float operations plus two field reads, called once per
// SkyManagerUpdate and once per section regenerate rather than per cell; GeometryMemo/FrameStamp exist for
// things far more expensive than this. A memo here would also have to key on map AND frame to stay correct
// across the two callers, which costs more than it saves.
public static class NightOverlayKeep
{
    // Fraction of vanilla overlay brightness §7a leaves on screen for `map`, in [0, 1]. 1 means "nothing
    // is being darkened", which is both the daylight answer and the answer whenever the feature pair is
    // off — so a caller never needs to check the flags itself, and a caller that multiplies or divides by
    // this value is a no-op in every case where §7a is not acting.
    public static float For(Map map) =>
        map?.skyManager == null ? 1f : For(map, map.skyManager.CurSkyGlow);

    // Same, with the glow supplied by the caller. Patch_PitchBlackOverlay uses this overload because it
    // already holds the SkyManager whose Update it is postfixing, and reading CurSkyGlow back off the map
    // would be the same number by a longer route.
    public static float For(Map map, float curSkyGlow)
    {
        // Both gates are §7a's own: the darkening is defined relative to §7's night floor, so with either
        // feature off nothing is darkened and there is nothing to compensate for. These stay in sync with
        // Patch_PitchBlackOverlay by being the thing it calls.
        if (!CelestialLightingFeatures.NightRadiance || !CelestialLightingFeatures.PitchBlackNights)
            return 1f;

        if (map == null)
            return 1f;

        // disableSkyLighting biomes are the case where vanilla switches the overlay off entirely and §7a
        // stands down rather than re-enabling it. Nothing is darkened there either, so the same 1.
        if (map.Biome != null && map.Biome.disableSkyLighting)
            return 1f;

        // §19 may RAISE the floor while the sun sits in the ozone twilight band; OverlayFloorFor returns
        // the configured value untouched whenever that band is inactive.
        float configuredMinBrightness =
            OzoneTwilight.OverlayFloorFor(map, NightRadianceSettings.Current.MinNightBrightness);

        // NOT the raw CurSkyGlow: an eclipse drives that to a flat 0 at any hour, and VisualGlowFor floors
        // it at the night floor for visual purposes only. Patch_PitchBlackOverlay's header carries the
        // measured ΔE 8.61 that reading it raw cost.
        float visualGlow = NightRadiance.VisualGlowFor(map, curSkyGlow);

        float rawBrightness = NightRadianceMath.RawOverlayBrightnessFactor(visualGlow);
        float minBrightness = NightRadianceMath.EffectiveMinNightBrightness(
            MapSky.UnnaturalDarknessActive(map), configuredMinBrightness, rawBrightness);

        return NightRadianceMath.OverlayBrightnessFactor(visualGlow, minBrightness);
    }
}
