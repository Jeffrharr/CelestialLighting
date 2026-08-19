using HarmonyLib;
using Verse;

namespace CelestialLighting;

// Drives the map-wide half of §9's per-cell wash: one material-colour write per frame, from the same
// rod-vision factor Patch_LowLightDesaturation keys its tint off.
//
// Sits on SkyManagerUpdate rather than on WeatherWorker.CurSkyTarget for the same reason
// Patch_PitchBlackOverlay does: this needs the FINAL composed brightness, after §7's night floor and
// every other CurSkyTarget postfix have had their say, and SkyManagerUpdate is the single point per
// frame where that is settled (curSkyGlowInt = curSky.glow, from the patched CurSkyTarget).
//
// It re-derives the factor from CurSkyGlow rather than having Patch_LowLightDesaturation stash it,
// because a stashed value would be a Harmony ordering dependency between two patches that share the
// one "celestiallighting" owner — an order [HarmonyAfter] cannot express, since that attribute takes
// owner IDs. Two reads of the same public value cannot disagree; two patches passing state can.
[HarmonyPatch(typeof(SkyManager), nameof(SkyManager.SkyManagerUpdate))]
public static class Patch_NightDesaturationStrength
{
    static void Postfix(SkyManager __instance)
    {
        // The wash material is one global object while SkyManagerUpdate runs for every loaded map
        // each frame — the same trap Patch_PitchBlackOverlay documents. Without this guard a second
        // colony or quest map would overwrite the visible map's strength with its own night.
        // Comparing skyManager identity keeps this on public API (SkyManager.map is private).
        Map current = Find.CurrentMap;
        if (current == null || current.skyManager != __instance)
            return;

        // Feature off: hold the wash at zero rather than leaving whatever the last enabled frame
        // wrote. The layer stops drawing too (SectionLayer_NightDesaturation.Visible), so this is
        // belt-and-braces — but it means a toggle mid-game is a true no-op either way round, which
        // is exactly what the harness's SetFeature A/B asserts.
        if (!CelestialLightingFeatures.LowLightDesaturation)
        {
            NightDesaturationOverlay.SetMapWash(0f, 0f);
            return;
        }

        // Through NightRadiance.VisualGlowFor rather than off CurSkyGlow directly, and for exactly the
        // reason §7a does it: vanilla's eclipse drives glow to a flat 0 whatever the hour, so an
        // eclipse at NIGHT was draining colour toward rod vision as though the sky had just gone from
        // moonlit to black — when the light level had not changed at all, because the sun was already
        // down. During DAYLIGHT the wash is untouched and colour still drains through a totality, which
        // is the real effect this subsystem is about.
        //
        // The floor is visual-only and never reaches SkyTarget.glow, so an eclipse costs vanilla's
        // solar output and vanilla's plant growth regardless of what this reads.
        float visualGlow = NightRadiance.VisualGlowFor(current, __instance.CurSkyGlow);

        float glow = WeatherDimmingMath.ApparentGlow(
            visualGlow, WeatherDimming.DimmingFor(current));

        NightDesaturationOverlay.SetMapWash(
            PurkinjeMath.PurkinjeFactor(glow), PurkinjeSettings.TintStrength);
    }
}
