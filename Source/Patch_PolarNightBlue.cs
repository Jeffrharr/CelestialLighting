using HarmonyLib;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// Subsystem 19 (DESIGN.md §19): the deep blue cast of polar twilight, from ozone's Chappuis
// absorption band. Like Patch_TwilightColor (§2) and Patch_SkyColorTemperature (§8) this is a
// Postfix on WeatherWorker.CurSkyTarget that NUDGES (never replaces) the returned colours — here
// toward the transmitted spectrum of sunlight that has crossed a long near-horizontal slant path
// through the ozone layer. Blending rather than overwriting preserves each WeatherDef's own palette,
// so rain and fog stay distinct, just blued by however deep the sun is under the horizon.
//
// COLOUR ONLY, NEVER .glow — this stays in the exact same low-risk lane as §2 and §8: we only touch
// __result.colors, so we do not disturb the brightness value (glow) that Dub's Skylights and other
// mods read. See DESIGN.md "Conflict risk". The one brightness concession §19 makes is expressed
// entirely in §7a's terms (OzoneTwilight.OverlayFloorFor), which is visual-only by construction.
//
// Composition with §2 and §8: all three warm/cool the sky around dusk, and they overlap on purpose
// in a 2-degree window. §8 dies at its NightFadeFloorDegrees (-6) and §2's civil-twilight
// persistence dies at the same -6, while our blue starts at -4 — so for those two degrees a warm
// band low in the west sits under an already-blue vault, which is what real dusk looks like. No
// HarmonyPriority: successive Color.Lerps are not commutative, but the error is bounded by
// a*b*(B - A) per channel, which WarmAndBlue_AreNeverBothAtFullStrength holds under 0.10 across the
// whole overlap. (Intra-assembly Harmony order cannot be expressed anyway — all our patches share
// one owner ID, so [HarmonyAfter] does not apply.)
//
// Composition with §9: §19 stacks with the Purkinje cool-grey tint deliberately, with no
// cross-subsystem suppression. They model different things — §9 is the eye losing colour
// discrimination as rods take over, §19 is the sky genuinely being blue — and a real polar twilight
// is both at once.
[HarmonyPatch(typeof(WeatherWorker), nameof(WeatherWorker.CurSkyTarget))]
public static class Patch_PolarNightBlue
{
    // Per-channel maximum blend strengths. Higher than §8's 0.35/0.25 for a measured reason:
    // vanilla's night palette is already cool (0.482, 0.603, 0.682), so the hue delta to the
    // transmitted spectrum is smaller than a warm nudge's and a §8-sized blend would read as
    // nothing — which is exactly how §9's first two attempts died (see NightDesaturationMath's
    // header). At 0.45 the multiply overlay attenuates ground red by ~24% at the band's peak; the
    // offline GroundRedAttenuation_ExceedsVisibleThreshold test is what keeps that honest.
    private const float SkyBlend = 0.45f;
    private const float OverlayBlend = 0.30f;

    private static void Postfix(Map map, ref SkyTarget __result)
    {
        // One shared adapter call. Every gate — the feature toggle, the enclosed/blacked-out sky,
        // the reachability gate, the band envelope and §18's vacuum flag — lives behind this, so the
        // colour arm and §7a's floor arm can never disagree about whether §19 is active.
        float band = OzoneTwilight.BandStrengthFor(map);
        float tint = band * OzoneTwilightSettings.TintStrength;
        if (tint <= 0f)
            return;

        SkyColorTemperature.Rgb hue = OzoneTwilightMath.ChappuisTransmission(SolarPosition.ElevationForMap(map));

        // Scale the pure hue against the colour we are blending FROM, so the nudge stays
        // luminance-neutral. ChappuisTransmission is normalised to a maximum channel of 1, which
        // without this rescale would drag every channel UP toward white and smuggle a brightness
        // rise into a patch documented as colour-only. It would also be pointless: §7a runs later on
        // the composed material and lerps toward opaque black, so brightness added here is
        // multiplied away regardless. Brightness is OverlayFloorFor's job alone. See DESIGN.md §19.
        __result.colors.sky = BlendTowardHue(__result.colors.sky, hue, tint * SkyBlend);
        __result.colors.overlay = BlendTowardHue(__result.colors.overlay, hue, tint * OverlayBlend);
        // Deliberately leave __result.colors.saturation and __result.glow untouched: saturation
        // shaping is Patch_TwilightColor's job (and §9 documents why the global saturation
        // post-process is the wrong lane for a per-cell effect), and glow is off-limits to the whole
        // colour-only lane.
    }

    // Lerp toward the hue rescaled to the source colour's own brightest channel, so only the RATIO
    // between channels moves. Pulled out as its own function rather than inlined twice because the
    // rescale is the subtle half of this patch and reads better named than repeated.
    private static Color BlendTowardHue(Color from, SkyColorTemperature.Rgb hue, float t)
    {
        float brightest = Mathf.Max(from.r, Mathf.Max(from.g, from.b));
        Color target = new Color(hue.R * brightest, hue.G * brightest, hue.B * brightest, from.a);
        return Color.Lerp(from, target, t);
    }
}
