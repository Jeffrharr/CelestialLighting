using HarmonyLib;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// Drives the map-wide half of §15b's eave shade: one material-colour write per frame, taken from the
// sun-shadow tint SkyManager has just finished composing.
//
// Sits on SkyManagerUpdate, and specifically as a Postfix of it, because that is where
// MatBases.SunShadow.color is assigned — `Color.Lerp(Color.white, curSky.colors.shadow,
// GenCelestial.CurShadowStrength(map))`, with our own Patch_ShadowStrength already folded into that
// last term. Reading the finished material rather than re-deriving the depth from elevation is the
// whole point: every shadow feature we own (§1's elevation ramp, the angular-size penumbra, §13's
// weather softening, the moon's night handoff) reaches the screen through that one value, so an eave
// can never drift from the shadow it is being matched to, including for features not written yet.
[HarmonyPatch(typeof(SkyManager), nameof(SkyManager.SkyManagerUpdate))]
public static class Patch_EaveShade
{
    static void Postfix(SkyManager __instance)
    {
        // The shade material is one global object while SkyManagerUpdate runs for every loaded map
        // each frame — the same trap Patch_PitchBlackOverlay and Patch_NightDesaturationStrength
        // document. Without this guard a second colony or quest map would overwrite the visible map's
        // shadow depth with its own. Comparing skyManager identity keeps this on public API
        // (SkyManager.map is private), and it is also what makes the read below correct: vanilla only
        // writes MatBases.SunShadow for `map == Find.CurrentMap`, so on any other map that colour is
        // some other map's, not this one's.
        Map current = Find.CurrentMap;
        if (current == null || current.skyManager != __instance)
            return;

        if (!CelestialLightingFeatures.EaveShade)
        {
            EaveShadeOverlay.Clear();
            return;
        }

        // Biomes that disable shadows outright (SectionLayer_SunShadows.Visible checks the same flag)
        // have no cast band for an eave to match, so there is nothing to reconcile with and shading
        // the porch alone would invent the very mismatch this exists to remove.
        if (!MapSky.DrawsShadows(current))
        {
            EaveShadeOverlay.Clear();
            return;
        }

        EaveShadeOverlay.SetShadowTint(MatBases.SunShadow.color);
    }
}
