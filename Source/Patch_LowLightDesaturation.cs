using HarmonyLib;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// Low-light desaturation / Purkinje shift (DESIGN.md §9). As the sky darkens toward night, human
// vision loses colour discrimination and the world drifts toward a dim, cool blue-grey — rods
// taking over from cones. This reproduces that so our nights read as *night*, not as a uniformly
// dimmed day.
//
// Like §2 (Patch_TwilightColor) and the planned §8 colour-temperature curve, this is a COLOUR-ONLY
// blend on WeatherWorker.CurSkyTarget — it touches SkyColorSet (sky/overlay tint + the saturation
// multiplier) and deliberately never writes __result.glow. Keeping clear of glow is what lets it
// stack cleanly with those siblings and with anything downstream that reads brightness
// (SkyManager.CurSkyGlow, GlowGrid, Dub's Skylights): we change how the existing light *reads*, not
// how much of it there is.
//
// Multiple postfixes on CurSkyTarget coexist fine (Patch_TwilightColor already adds one); Harmony
// runs them in sequence and each nudges the same struct.
[HarmonyPatch(typeof(WeatherWorker), nameof(WeatherWorker.CurSkyTarget))]
public static class Patch_LowLightDesaturation
{
    // The colour rod-dominated night vision biases toward — a desaturated cool blue-grey. Real
    // scotopic vision is achromatic but perceptually reads slightly blue (the Purkinje blue shift),
    // so this is a low-saturation grey pulled a touch toward blue rather than a neutral grey.
    private static readonly Color CoolNight = new Color(0.55f, 0.60f, 0.72f);

    // Peak blend fractions toward CoolNight at full rod vision (PurkinjeFactor == 1). The sky plane
    // carries the shift more than the overlay so the tint reads without washing the whole scene
    // flat. Both are scaled down by the factor, so at dusk (factor near 0) the nudge is negligible
    // and only deep night gets the full drift. Deliberately moderate — subtle-but-present, and a
    // future settings slider (issue #9 umbrella) can scale it.
    private const float SkyBlendMax = 0.50f;
    private const float OverlayBlendMax = 0.35f;

    static void Postfix(Map map, ref SkyTarget __result)
    {
        // Read the sky target's OWN glow, not GenCelestial.CurCelestialSunGlow. This is the opposite
        // choice from Patch_TwilightColor (which recomputes celestial glow because it wants twilight
        // timing anchored to true sun position): here we *want* the actual displayed brightness,
        // including weather dimming, because a darker scene genuinely pushes vision further into rod
        // territory. __result.glow is already clamped by the active WeatherDef's maxGlow, so an
        // overcast night reads darker here and desaturates more — exactly the "strongest on the
        // darkest nights" behaviour the design asks for, with no separate weather term.
        //
        // TODO(integration): once the night-radiance subsystem (§7 / issue #4) merges, it will
        // raise/lower __result.glow by moon phase + star/airglow floors. As long as its postfix runs
        // before this one, this desaturation tracks it automatically — a full-moon night lands lower
        // on the ramp (less shift) than a new-moon one. If Harmony orders them the other way, add
        // [HarmonyAfter("<its patch id>")] here. Standalone, reading the vanilla weather-clamped glow
        // is a correct, self-contained default.
        float glow = __result.glow;

        float factor = PurkinjeMath.PurkinjeFactor(glow);
        if (factor <= 0f)
            return;

        // Drain colour saturation toward the rod-vision floor. SkyColorSet.saturation is a global
        // multiplier on the rendered scene's saturation, so this alone desaturates everything;
        // multiplying (rather than assigning) preserves whatever the active WeatherDef and §2's
        // twilight bump already set.
        __result.colors.saturation *= PurkinjeMath.SaturationMultiplier(glow);

        // Bias the sky/overlay tint toward the cool blue-grey. Lerp (never overwrite) so each
        // WeatherDef's palette still shows through — it's just pulled toward night-blue as colour
        // discrimination fades.
        __result.colors.sky = Color.Lerp(__result.colors.sky, CoolNight, factor * SkyBlendMax);
        __result.colors.overlay = Color.Lerp(__result.colors.overlay, CoolNight, factor * OverlayBlendMax);
    }
}
