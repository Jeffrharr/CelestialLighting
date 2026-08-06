using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// Subsystem 19c (DESIGN.md §19c): the twilight PURPLE LIGHT — the lavender the western sky turns
// roughly 15-25 minutes after sunset. Like §2, §8 and §19 this is a Postfix on
// WeatherWorker.CurSkyTarget that NUDGES (never replaces) the returned colours, here toward the
// superposition of the reddened horizon band with the ozone-crossed vault. Blending rather than
// overwriting preserves each WeatherDef's own palette, so rain and fog stay distinct.
//
// COLOUR ONLY, NEVER .glow — the same low-risk lane as §2, §8 and §19: we touch only
// __result.colors, so we do not disturb the brightness value Dub's Skylights and other mods read.
// See DESIGN.md "Conflict risk". Unlike §19 there is no brightness arm at all: the purple light sits
// in civil twilight where there is still plenty of light, so §19's argument for an overlay floor
// simply does not arise here.
//
// WHY THIS IS A THIRD PATCH RATHER THAN AN EDIT TO §8 OR §19. Purple inverts BOTH of §8's tested
// invariants (monotonic warmth, and R >= G >= B) and §19's hue ordering as well (§19 is B > G > R,
// purple is green-minimum). It cannot live inside either file without deleting the invariant that
// file exists to hold, which is the same argument DESIGN.md §19 already makes for why §19 is not an
// extension of §8. A composition above both is the only place it fits.
//
// ORDERING. This must run AFTER §8 and §19, because it is a correction applied to what those two
// left behind rather than an independent tint. Intra-assembly Harmony order cannot be expressed —
// all our patches share one owner ID, so [HarmonyAfter] does not apply — so ordering is secured
// structurally instead: the window envelope is exactly zero at both boundaries, and the nudge is a
// lerp toward a hue rescaled to whatever colour it finds. If it ran first, §8 and §19 would simply
// blend it back down; the effect would be weaker, never wrong or discontinuous. That is a much
// weaker dependency than a priority attribute would be papering over.
[HarmonyPatch(typeof(WeatherWorker), nameof(WeatherWorker.CurSkyTarget))]
public static class Patch_PurpleLight
{
    // Per-channel blend strengths, DERIVED from §8's and §19's rather than chosen.
    //
    // The whole diagnosis behind this subsystem is that the -6..-4 handoff models the two sources as
    // SUBSTITUTING for one another — §8 fading out as §19 fades in — when physically both are fully
    // present in the sky at once. Their strength product peaks at 0.048 at -5 degrees, which is why
    // the window reads as a muddy neutral: it is the one place both subsystems are nearly off.
    //
    // If both sources are fully present, the composed displacement from the vanilla palette is the
    // one two full-strength sequential lerps would have produced — their union, 1 - (1 - a)(1 - b).
    // That is 0.6425 for the sky and 0.4750 for the overlay, and neither is a number anybody picked.
    // Reading §8's and §19's constants rather than copying them is what keeps the three in step if
    // either is ever retuned.
    private const float SkyBlend =
        1f - (1f - Patch_SkyColorTemperature.SkyBlend) * (1f - Patch_PolarNightBlue.SkyBlend);

    private const float OverlayBlend =
        1f - (1f - Patch_SkyColorTemperature.OverlayBlend) * (1f - Patch_PolarNightBlue.OverlayBlend);

    private static void Postfix(Map map, ref SkyTarget __result)
    {
        // One shared adapter call carrying every gate — the feature toggle, the enclosed/blacked-out
        // sky, the window envelope, §18's vacuum flag and the user's strength slider. Outside the
        // two-degree window this returns exactly 0, which is what makes every sunset the mod already
        // ships bit-identical: the patch is a no-op everywhere except between -6 and -4.
        float window = PurpleLight.WindowStrengthFor(map);
        if (window <= 0f)
            return;

        SkyColorTemperature.Rgb hue = PurpleLight.ComposedHueFor(map);

        __result.colors.sky = BlendTowardHue(__result.colors.sky, hue, window * SkyBlend);
        __result.colors.overlay = BlendTowardHue(__result.colors.overlay, hue, window * OverlayBlend);
        // Deliberately leave __result.colors.saturation and __result.glow untouched, exactly as §8
        // and §19 do: saturation shaping is Patch_TwilightColor's job and glow is off-limits to the
        // whole colour-only lane. Purple is a HUE claim and nothing else — turning saturation up to
        // sell it would be a different subsystem making a different claim.
    }

    // Lerp toward the hue rescaled to the source colour's own brightest channel, so only the RATIO
    // between channels moves. Identical in form to Patch_PolarNightBlue's, and identical for the
    // same reason: PurpleLightMath.ComposedHue is normalised to a maximum channel of 1, and blending
    // toward that raw would drag every channel UP toward white and smuggle a brightness rise into a
    // patch documented as colour-only.
    private static Color BlendTowardHue(Color from, SkyColorTemperature.Rgb hue, float t)
    {
        float brightest = Mathf.Max(from.r, Mathf.Max(from.g, from.b));
        Color target = new Color(hue.R * brightest, hue.G * brightest, hue.B * brightest, from.a);
        return Color.Lerp(from, target, t);
    }
}
