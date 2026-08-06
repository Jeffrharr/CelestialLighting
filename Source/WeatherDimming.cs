using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// Thin adapter for §13: pulls the primitives WeatherDimmingMath needs off live Map/WeatherManager
// state. Shared by Patch_WeatherDimming (sky tint), Patch_ShadowStrength (shadow softening),
// Patch_LowLightDesaturation (§9's apparent brightness) and WeatherDimmingProbe, so all four always
// agree about how heavy the weather is instead of risking four independently-derived values
// disagreeing — the same discipline SolarPosition enforces for sun elevation.
//
// This is also where the two live-state questions the pure classifier cannot answer are asked: does
// this map have a sky at all (HasSky), and has the def declared its own answer (WeatherCloudDeck).
// Both exist because §13's original palette-only classifier was tuned against a vanilla-only census
// and misread modded cave environments as overcast — see DESIGN.md §13.
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
    // generation), when the map has no sky over it, or under any clear / non-weather weather.
    public static float CloudOpacityFor(Map map)
    {
        if (!CelestialLightingFeatures.WeatherDimming)
            return 0f;

        WeatherManager weather = map?.weatherManager;
        if (weather == null)
            return 0f;

        if (!MapSky.HasSky(map))
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
        float dimming = WeatherDimmingMath.DimmingFraction(
            opacity,
            weather.RainRate,
            weather.SnowRate,
            weather.SandRate,
            WeatherDimmingSettings.MaxDimming);

        // §21: the DAYTIME half of the surface-cloud cavity. The same deck this function is dimming
        // for also bounces the ground's light back down, and over snow it hands most of the dimming
        // back. Not a contradiction and not a sign error — a cloud blocks the sun AND reflects from
        // its base, for the same reason it is a cloud. Over bare ground the gain is exactly 1 and
        // this line returns `dimming` unchanged, so every non-snowy map is bit-identical to pre-§21.
        //
        // WHY HERE RATHER THAN IN Patch_WeatherDimming. DimmingFor is the shared read, and all three
        // of its consumers want the recovered value: the sky tint (§13), §9's ApparentGlow and §9's
        // per-cell night-wash strength. A snowy overcast that renders brighter must also desaturate
        // less, and it does so here for free — which is exactly why §21 writes no saturation term of
        // its own (DESIGN.md §21, §9).
        //
        // WHAT IS DELIBERATELY LEFT ALONE: Patch_ShadowStrength, which reads CloudOpacityFor rather
        // than this, so the deck still softens shadows by the full amount. Brightness comes back,
        // contrast does not. That asymmetry is the whiteout.
        //
        // The opacity is passed rather than re-read: CavityGainFor would otherwise walk MapSky's
        // uncached biome/condition gates a second time on a path SkyManager runs twice per map per
        // frame.
        return AlbedoCavityMath.RecoveredDimming(dimming, SurfaceBuildup.CavityGainFor(map, opacity));
    }

    // §13's STRUCTURAL GUARD, and the half of the problem the pure classifier cannot reach. "Is this
    // palette a cloud deck?" is a question about a WeatherDef; "is there any sky here?" is a question
    // about the map, and asking it rather than trying to infer it from a palette is what closes the
    // entire cave / pocket-map / orbit class in one cheap check. §13 shipped without it on the
    // strength of a vanilla-only census; Biomes! Caverns and MultiFloors both ship cave environments
    // with overcast-shaped palettes, which the palette rule alone rates 1.00 and 0.71.
    //
    // The rule itself now lives in MapSky, because Biomes! Caverns showed the question was never
    // specific to weather: sunset warmth, colour temperature, aurora, blood moon, eclipses and a
    // night floor lifted by MOONLIGHT are all equally meaningless under a rock ceiling, and all of
    // them were applying themselves to sealed caves. This delegation is a pure move — the rule and
    // the counting are unchanged, which is what keeps weather_dimming_skyless.json passing untouched
    // and makes that scenario the regression proving the move was behaviour-preserving.

    // Classifies a single WeatherDef from data it already ships — its day palette and its
    // precipitation rates. No def-name list and no registration step; see
    // WeatherDimmingMath.CloudOpacity for what each line of evidence buys and why neither alone is
    // enough.
    private static float OpacityOf(WeatherDef def)
    {
        if (def == null)
            return 0f;

        // An explicit statement by the def beats anything we can infer from it. Checked first so the
        // escape hatch is unconditional: a mod author (or an XML patch) who says "this is not a cloud
        // deck" is not overruled by a palette that happens to look like one.
        WeatherCloudDeck declared = def.GetModExtension<WeatherCloudDeck>();
        if (declared != null && declared.OverridesOpacity)
            return Mathf.Clamp01(declared.opacity);

        SkyColorSet colors = def.skyColorsDay;
        return WeatherDimmingMath.CloudOpacity(
            colors.sky.r, colors.sky.g, colors.sky.b, colors.saturation,
            def.rainRate, def.snowRate, def.sandRate);
    }
}
