using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file (same discipline as
// Formulas.cs). It is linked into the test project via <Compile Include> so the exact code that
// ships is the exact code under test. If a function here ever needs Mathf/Color/Map, it belongs in
// the Patch_SkyColorTemperature adapter instead — pass primitives in from there.
//
// Subsystem 8 (DESIGN.md §8): a continuous colour-temperature curve keyed on sun ALTITUDE. The sky
// shifts from a warm, low-colour-temperature glow near the horizon (~2000 K) up to a neutral
// daylight white near the zenith (~5772 K, the Sun's real effective temperature), passing through
// golden-hour warmth on the way. This generalizes §2's single fixed twilight hue: because day
// length and peak sun altitude already vary with latitude/season (vanilla GenCelestial + our own
// SolarPosition simulator), a high-latitude winter day whose sun never climbs far *stays* warm all
// day for free — the effect is a function of altitude alone, so low sun == warm sky wherever and
// whenever that happens.
//
// Critically this is a COLOUR-ONLY transform: the adapter blends WeatherWorker.CurSkyTarget's
// colours and never its .glow, so it does not disturb the brightness value other mods read (see
// DESIGN.md "Conflict risk"). Nothing in this file has any concept of brightness/glow at all.
public static class SkyColorTemperature
{
    // Endpoints of the altitude → colour-temperature curve, in Kelvin.
    //   HorizonKelvin: a warm ~2000 K (deep sunrise/sunset orange) at/below the horizon.
    //   ZenithKelvin:  5772 K, the Sun's actual effective (photospheric) temperature — the neutral
    //                  daylight white we ramp toward as the sun climbs. Not a round number by
    //                  accident: it's the real physical anchor the whole curve is built around.
    public const float HorizonKelvin = 2000f;
    public const float ZenithKelvin = 5772f;

    // At/above this solar elevation the sky has reached full daylight temperature and no further
    // warming is applied. 60° is high enough that only tropical/summer midday sun exceeds it, so
    // temperate and high-latitude skies stay perceptibly warm through most of their (lower) day —
    // which is exactly the "seasonal twilight stays warm all day up north" behaviour §8 wants.
    public const float DaylightAltitudeDegrees = 60f;

    // Standard atmospheric refraction at the horizon (matches Formulas.AtmosphericRefractionDegrees,
    // referenced directly so there is a single source of truth for "where the sun sets"): the sun's
    // last light lingers a fraction of a degree below geometric zero, so the warm tint should too.
    private const float HorizonElevationDegrees = Formulas.AtmosphericRefractionDegrees;

    // Below this the sun is far enough down that there is no direct sunlight left to colour, so the
    // tint fades out entirely and night (subsystem 7's domain) takes over. -6° is the end of civil
    // twilight — a standard, physically-meaningful choice, not a tuned magic number.
    private const float NightFadeFloorDegrees = -6f;

    public readonly struct Rgb
    {
        public readonly float R;
        public readonly float G;
        public readonly float B;

        public Rgb(float r, float g, float b)
        {
            R = r;
            G = g;
            B = b;
        }
    }

    // Linear ramp from HorizonKelvin (at/below the horizon) to ZenithKelvin (at/above
    // DaylightAltitudeDegrees), clamped flat past both ends. Monotonic in elevation, so a lower sun
    // is always at least as warm as a higher one — the property the whole subsystem depends on.
    public static float ColorTemperatureKelvin(float elevationDegrees)
    {
        float t = InverseLerpClamped(0f, DaylightAltitudeDegrees, elevationDegrees);
        return Lerp(HorizonKelvin, ZenithKelvin, t);
    }

    // How strongly the warm tint should be applied at a given sun altitude, in [0, 1]. It is the
    // product of two ramps:
    //   * lowSunRamp — 1 at the horizon, falling to 0 by DaylightAltitudeDegrees: the tint is
    //     strongest when the sun is low and vanishes once it's high (where the sky is neutral
    //     anyway, so blending toward ~5772 K would do almost nothing regardless).
    //   * daylightGate — 1 while the sun is up, ramping to 0 between the refraction-adjusted horizon
    //     and the end of civil twilight: below the horizon there is no direct sunlight left to
    //     colour, so we hand off to the night-radiance subsystem instead of tinting darkness warm.
    // The adapter multiplies this geometric factor by its own per-channel blend strengths, exactly
    // the way Patch_TwilightColor multiplies Formulas.TwilightFactor by 0.35/0.25.
    public static float TintStrength(float elevationDegrees)
    {
        float lowSunRamp = InverseLerpClamped(DaylightAltitudeDegrees, 0f, elevationDegrees);
        float daylightGate = InverseLerpClamped(NightFadeFloorDegrees, HorizonElevationDegrees, elevationDegrees);
        return lowSunRamp * daylightGate;
    }

    // Convenience composition used by both the adapter and the live probe so they can never derive a
    // different colour from the same elevation: elevation → colour temperature → RGB.
    public static Rgb SkyColorForElevation(float elevationDegrees) =>
        BlackbodyToRgb(ColorTemperatureKelvin(elevationDegrees));

    // Blackbody colour temperature → linear-ish sRGB in [0, 1] per channel, via the widely published
    // (public-domain) Tanner Helland approximation of the Planckian locus. This is a standard
    // tabulated/curve-fit conversion used across countless colour tools — textbook, not mod-specific
    // (see DESIGN.md "Clean-room provenance"). Valid roughly 1000–40000 K; our curve only ever feeds
    // it HorizonKelvin..ZenithKelvin.
    //
    // Each channel is its own small named function (below) rather than one deeply-nested branch, so
    // the piecewise structure reads top-to-bottom instead of forcing the reader to hold the other
    // two channels' cases in mind.
    public static Rgb BlackbodyToRgb(float kelvin)
    {
        float temp = kelvin / 100f;
        return new Rgb(RedChannel(temp), GreenChannel(temp), BlueChannel(temp));
    }

    // temp is kelvin/100. Below ~6600 K the red channel is fully saturated; above it, it rolls off.
    private static float RedChannel(float temp)
    {
        if (temp <= 66f)
            return 1f;
        return Clamp01(329.698727446f * MathF.Pow(temp - 60f, -0.1332047592f) / 255f);
    }

    private static float GreenChannel(float temp)
    {
        if (temp <= 66f)
            return Clamp01((99.4708025861f * MathF.Log(temp) - 161.1195681661f) / 255f);
        return Clamp01(288.1221695283f * MathF.Pow(temp - 60f, -0.0755148492f) / 255f);
    }

    private static float BlueChannel(float temp)
    {
        if (temp >= 66f)
            return 1f;
        if (temp <= 19f)
            return 0f;
        return Clamp01((138.5177312231f * MathF.Log(temp - 10f) - 305.0447927307f) / 255f);
    }

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float InverseLerpClamped(float a, float b, float v) => Clamp01((v - a) / (b - a));
}
