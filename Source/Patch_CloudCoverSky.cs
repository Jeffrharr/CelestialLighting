using RimWorld;
using Verse;

namespace CelestialLighting;

// §22's sky contribution: darkens and desaturates a Clear-weather sky in proportion to how cloudy
// CloudCoverClock says it is right now. Colour-only, same discipline and same reason as
// Patch_WeatherDimming — see that file's header for the full .glow/.colors split; the short version is
// that SkyTarget.glow feeds GlowGrid.GroundGlowAt (gameplay: plant growth, solar output, pawn
// psych-glow) and .colors is pure render, and this mod's scope is visual/atmospheric only (parent
// CLAUDE.md).
//
// GATED STRICTLY ON curWeather == Clear — not lastWeather, not a transition blend. §13's own
// WeatherDimming blends across a transition because it is describing a REAL weather change that
// vanilla itself already takes ~4000 ticks to roll in (WeatherManager.TransitionLerpFactor);
// partial cloud cover is inventing an axis vanilla's weather state machine has no notion of at all,
// and cross-fading it against a transition into or out of an entirely different weather would only
// make the two features' interaction harder to reason about for very little visible benefit —
// CloudCoverDrift's own hourly wobble is already continuous and slow-moving. A boolean gate means the
// effect can only ever step by at most one hour's worth of cloud cover at the instant the weather
// actually changes, which is not the case this design spent its complexity budget on.
//
// WHY THIS NEVER FIGHTS Patch_WeatherDimming FOR THE SAME PIXELS, WITH NO ORDERING DECLARED. Both
// patches target this exact method, but their gates are mutually exclusive by construction:
// WeatherDimmingMath classifies Clear as opacity 0 on both its axes (palette identical to the
// clear-family anchors, zero rainRate/snowRate/sandRate), so Patch_WeatherDimming's own early return
// always fires while curWeather is Clear, and this patch's early return always fires whenever it is
// not. Neither stage's output depends on whether the other ran, so wherever Patch_SkyTargetComposite
// happens to sequence the two is safe.
public static class Patch_CloudCoverSky
{
    internal static void Apply(Map map, ref SkyTarget target)
    {
        if (map.weatherManager.curWeather != WeatherDefOf.Clear)
            return;

        // The feature gate lives inside CloudCoverClock/CloudCoverDrift's own amplitude, same as
        // Patch_WeatherDimming's dimming<=0 early return: no cloud this hour means no-op, leaving
        // Clear's palette exactly as vanilla renders it.
        float cloudCover = CloudCoverClock.FractionForMap(map);
        if (cloudCover <= 0f)
            return;

        // Multiply, never assign — see Patch_WeatherDimming's own header for why: the sky may already
        // carry §2's twilight warmth, §8's colour temperature or §11's aurora tint by the time this
        // postfix runs, and scaling preserves all of them rather than overwriting whichever ran first.
        float skyTint = CloudCoverSky.SkyTintFactor(cloudCover);
        target.colors.sky = ScaleRgb(target.colors.sky, skyTint);
        target.colors.overlay = ScaleRgb(target.colors.overlay, skyTint);

        // Read-then-multiply, matching Patch_LimbRefraction's and Patch_TwilightColor's own treatment
        // of this same field, rather than assigning an absolute saturation value.
        target.colors.saturation *= CloudCoverSky.SaturationTintFactor(cloudCover);

        // target.glow is deliberately NOT touched — see the header.
    }

    // Scales RGB and leaves ALPHA ALONE — see Patch_WeatherDimming.ScaleRgb's header for why: Unity's
    // Color * float scales all four channels, and alpha is how much of the lighting overlay renders
    // at all, not a brightness.
    private static UnityEngine.Color ScaleRgb(UnityEngine.Color color, float factor) =>
        new UnityEngine.Color(color.r * factor, color.g * factor, color.b * factor, color.a);
}
