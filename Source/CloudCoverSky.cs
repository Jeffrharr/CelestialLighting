namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file, same discipline as
// WeatherDimmingMath.cs. Compiled into both Source (net481) and Tests (net8.0) via a linked
// <Compile Include>, so the exact code that ships is the exact code under test.
//
// §22's colour contribution: how much a partly-cloudy Clear day should darken and desaturate the
// rendered sky, expressed as MULTIPLIERS so Patch_CloudCoverSky can compose with whatever earlier
// postfixes on WeatherWorker.CurSkyTarget already wrote (§2's twilight warmth, §8's colour
// temperature, §11's aurora tint) rather than overwriting them — same discipline as
// WeatherDimmingMath.SkyTintFactor, and Patch_LimbRefraction / Patch_TwilightColor's own
// read-then-multiply treatment of .colors.saturation.
//
// WHY LERP TOWARD VANILLA'S OWN OVERCAST RATIO RATHER THAN INVENT A NEW DARKENING CURVE. Vanilla
// already ships a "what does a cloud deck look like" answer for every weather that classifies as
// overcast: skyColorsDay drops from Clear's (1,1,1) / saturation 1.25 to the shared wet-weather
// palette's (0.8,0.8,0.8) / saturation 0.9 — WeatherDimmingMath.ClearSkyLuminance/OvercastSkyLuminance
// and ClearSaturation/OvercastSaturation are exactly those two anchors, reused here rather than
// duplicated. A cloud fraction of 1.0 during Clear therefore ends up looking like the SAME destination
// Overcast/Rain/Fog already look like, not a bespoke "cloudy" palette this file invents independently
// — that is what makes a half-cloudy Clear day read as "partway to Overcast" rather than as a
// CelestialLighting-specific effect layered on top of vanilla's own weather art.
//
// WHY THIS IS A SEPARATE FILE FROM WeatherDimmingMath RATHER THAN A NEW CASE INSIDE IT. Dimming
// classifies an ALREADY-CHOSEN weather as more or less cloudy from its own data (PaletteOpacity,
// PrecipitationEvidence) — it has nothing to say about Clear, which classifies to exactly 0 on both
// axes by construction. This file's input is never that classifier; it is CloudCoverDrift's fraction,
// a quantity vanilla's weather system has no notion of at all. The two happen to share a destination
// palette because both are describing "how covered is the sky", not because one is a variant of the
// other — see Patch_CloudCoverSky's header for why the two patches never fire on the same weather.
public static class CloudCoverSky
{
    // Multiplier for .colors.sky and .colors.overlay. 1 at cloudCover 0 (no change from whatever
    // Clear's palette and every earlier postfix already produced); WeatherDimmingMath's own
    // Overcast/Clear luminance ratio (0.8) at cloudCover 1.
    public static float SkyTintFactor(float cloudCover) =>
        Lerp(1f, WeatherDimmingMath.OvercastSkyLuminance / WeatherDimmingMath.ClearSkyLuminance, Clamp01(cloudCover));

    // Multiplier for .colors.saturation. Same shape, using WeatherDimmingMath's Overcast/Clear
    // saturation ratio (0.9 / 1.25 = 0.72) as the cloudCover-1 endpoint.
    public static float SaturationTintFactor(float cloudCover) =>
        Lerp(1f, WeatherDimmingMath.OvercastSaturation / WeatherDimmingMath.ClearSaturation, Clamp01(cloudCover));

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
