using HarmonyLib;
using RimWorld;
using Verse;

namespace CelestialLighting;

// GenCelestial.CurShadowStrength(Map) is what actually drives the visible shadow: SkyManager
// reads it directly as the alpha channel of the shader global it sets every frame
// (SetSunShadowVector writes Vector4(vector.x, 0, vector.y, CurShadowStrength(map)) to
// MapSunLightDirection) and to tint MatBases.SunShadow/SunShadowFade — none of that reads
// GetLightSourceInfo's LightInfo.intensity, which Patch_ShadowDirection patches separately.
// Vanilla's CurShadowStrength (Clamp01(Abs(CurCelestialSunGlow(map) - 0.6f) / 0.15f)) stays near
// full strength through the night (its "moonlight" reading), so patching only
// Patch_ShadowDirection was not enough to actually suppress moon shadows in-game — the shader was
// still being told to render at near-full opacity regardless of what LightInfo.intensity said.
//
// This Postfix overrides the result with the same elevation-based intensity Patch_ShadowDirection
// uses (via the shared SolarPosition.ElevationForMap), so the two can never disagree: 0 once the
// sun drops past Formulas.AtmosphericRefractionDegrees, ramping to full strength over the same
// window above it.
[HarmonyPatch(typeof(GenCelestial), nameof(GenCelestial.CurShadowStrength))]
public static class Patch_ShadowStrength
{
    static void Postfix(Map map, ref float __result)
    {
        float elevation = SolarPosition.ElevationForMap(map);
        float strength = Formulas.ShadowIntensityFromElevation(elevation);

        // Angular-size penumbra (feature-gated): attenuate the shadow strength toward the horizon,
        // where the solar-disk penumbra widens and shadows lose contrast. CurShadowStrength is the
        // CORRECT lever for this — it is exactly what SkyManager lerps MatBases.SunShadow.color by
        // (the material colour that actually darkens the ground) and writes into the _CastVect
        // global. An earlier attempt folded this into Patch_ShadowTilt's per-section _CastVect.w
        // MaterialPropertyBlock override, which a live A/B proved inert: visible opacity is the
        // global material colour, not a per-draw _CastVect.w. Penumbra is a map-wide function of sun
        // elevation, so this global point is also the natural home for it — no per-section term.
        if (CelestialLightingFeatures.PenumbraContrast)
            strength *= PenumbraMath.PenumbraContrastFactor(elevation);

        __result = strength;
    }
}
