using RimWorld;
using Verse;

namespace CelestialLighting;

// Thin adapter for §13: pulls the primitives WeatherDimmingMath needs off live Map/WeatherManager
// state. Shared by Patch_WeatherDimming (sky tint), Patch_ShadowStrength (shadow softening),
// Patch_LowLightDesaturation (§9's apparent brightness) and WeatherDimmingProbe, so all four always
// agree about how heavy the weather is instead of risking four independently-derived values
// disagreeing — the same discipline SolarPosition enforces for sun elevation.
//
// WHY map.weatherManager AND NOT WeatherWorker's own `def`. Patch_WeatherDimming postfixes
// WeatherWorker.CurSkyTarget, so the "obvious" read is the def belonging to the worker being
// patched. That field is private (pinned by ApiCompatibilityTests.WeatherWorker_DefFieldIsNotPublic),
// so it would need FieldRefAccess — and it would buy nothing, because reading the manager is exactly
// equivalent, not merely close. SkyManager.CurrentSkyTarget calls CurSkyTarget on BOTH the current
// and the last weather's worker and lerps the two results by TransitionLerpFactor. A uniform
// map-level multiply therefore factors straight back out of that lerp:
//
//     Lerp(a*k, b*k, t) == k * Lerp(a, b, t)
//
// so blending the two defs' opacities here and applying one scalar gives bit-identical output to
// applying each def's own scalar inside its own worker call — with no reflection and no fragile
// private-field binding.
public static class WeatherDimming
{
    // How much of a cloud deck is overhead right now, in [0,1], blended across any in-flight weather
    // transition. 0 when the feature is off, when there is no weather manager (pocket maps during
    // generation), or under any clear / non-weather weather.
    public static float CloudOpacityFor(Map map)
    {
        if (!CelestialLightingFeatures.WeatherDimming)
            return 0f;

        WeatherManager weather = map?.weatherManager;
        if (weather == null)
            return 0f;

        return WeatherDimmingMath.BlendOpacity(
            OpacityOf(weather.lastWeather),
            OpacityOf(weather.curWeather),
            weather.TransitionLerpFactor);
    }

    // The 0..1 fraction by which the rendered sky is currently darkened. 0 whenever CloudOpacityFor
    // is 0, so the feature gate and the clear-sky fast path are both inherited from it.
    public static float DimmingFor(Map map)
    {
        float opacity = CloudOpacityFor(map);
        if (opacity <= 0f)
            return 0f;

        // Vanilla already lerps all three rates across the weather transition (WeatherManager.RainRate
        // and friends are Mathf.Lerp(last, cur, TransitionLerpFactor)), so we deliberately do not lerp
        // them again here — only the palette-derived opacity needs our own blend. SandRate returns 0
        // without Odyssey, so reading it unconditionally is safe.
        WeatherManager weather = map.weatherManager;
        return WeatherDimmingMath.DimmingFraction(
            opacity,
            weather.RainRate,
            weather.SnowRate,
            weather.SandRate,
            WeatherDimmingSettings.MaxDimming);
    }

    // Classifies a single WeatherDef from the day palette it already ships. No def-name list and no
    // registration step, so a modded weather is classified by the same data it declares for
    // rendering — see WeatherDimmingMath.CloudOpacity for why the product of the two deficits is
    // what keeps caves, the metal hell and orbit at exactly 0.
    private static float OpacityOf(WeatherDef def)
    {
        if (def == null)
            return 0f;

        SkyColorSet colors = def.skyColorsDay;
        return WeatherDimmingMath.CloudOpacity(
            colors.sky.r, colors.sky.g, colors.sky.b, colors.saturation);
    }
}
