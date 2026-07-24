using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file, same discipline as
// Formulas.cs. It is compiled into both Source (net481, runs inside RimWorld) and Tests (net8.0,
// runs standalone via `dotnet test`) via a linked <Compile Include>, so the exact code that ships
// is the exact code under test. Anything that needs Mathf/Vector/Map/Find belongs in the adapter
// (Patch_NightRadiance / MoonSeam), not here.
//
// Subsystem 7 (DESIGN.md §7): night brightness is the *sum* of a few dim, physically-motivated
// light sources instead of a flat glow floor, so darkness is emergent — legible under a full moon,
// genuinely dark on a new moon, and true pitch-black once the atmospheric floors are toggled off.
public static class NightRadianceMath
{
    // --- Tunable floor defaults (the settings seam, NightRadianceSettings, overrides these at the
    //     adapter boundary; these are the DESIGN defaults so the feature works standalone) ---

    // Starlight: an open night sky is never truly zero. Real moonless-clear starlight is only
    // ~0.001 lux — far below anything legible — so this is a deliberately-raised faint floor in
    // RimWorld's 0..1 glow units, chosen for atmosphere/legibility rather than photometric accuracy.
    public const float DefaultStarlightGlow = 0.02f;

    // Airglow: faint atmospheric self-emission (upper-atmosphere chemiluminescence), a second small
    // constant floor independent of the moon. Same order of magnitude as the starlight floor.
    public const float DefaultAirglowGlow = 0.02f;

    // Full moon at the zenith, in glow units. A real full moon is ~0.25 lux (~1/400,000 of the noon
    // sun) yet reads as "bright enough to move around by": this maps that to a night glow clearly
    // above a new-moon night while staying unmistakably night, not dimmed day.
    public const float DefaultMaxMoonlightGlow = 0.15f;

    // Sun-elevation band (degrees) over which the night floor fades in. Above NightFloorStartElevation
    // the floor contributes nothing at all — daytime and twilight own the sky there, so this composes
    // *under* §2's twilight warm-tint rather than fighting it. At/below NightFloorFullElevation the
    // floor fully owns the glow.
    //
    // The two anchors are textbook: -0.83 degrees is the atmospheric-refraction horizon (where the
    // sun's disk actually disappears — the same constant the shadow simulator uses), and -18 degrees
    // is the end of astronomical twilight, past which scattered sunlight no longer lightens the sky
    // and all that remains *is* starlight + airglow + moonlight.
    public const float NightFloorStartElevation = -0.83f;
    public const float NightFloorFullElevation = -18f;

    // How high the moon sits scales its contribution: a low moon's light crosses more atmosphere and
    // lands more obliquely than one overhead. Uses sin(elevation) (the same projection factor the
    // sun's own illumination follows), clamped to 0 below the horizon — a set moon casts no light.
    // Not the full airmass law, but the same monotonic horizon->zenith ramp, which is all a glow
    // scalar needs.
    public static float MoonAltitudeFactor(float moonElevationDegrees)
    {
        float sinElevation = MathF.Sin(ToRadians(moonElevationDegrees));
        return sinElevation < 0f ? 0f : sinElevation;
    }

    // illuminatedFraction: 0 = new moon (no reflected light), 1 = full moon — the elongation->phase
    // term the moon-phase subsystem (§6) provides. Linear in both illuminated fraction and altitude
    // factor, so a new moon, or a moon below the horizon, contributes exactly nothing, and a full
    // moon at the zenith gives the full maxMoonlightGlow.
    public static float MoonlightGlow(float illuminatedFraction, float moonElevationDegrees, float maxMoonlightGlow) =>
        Clamp01(illuminatedFraction) * MoonAltitudeFactor(moonElevationDegrees) * maxMoonlightGlow;

    // The night floor is the *sum* (not the max) of the three sources, clamped to [0, 1]. Summing is
    // the whole point of the design: a clear full-moon night reads distinctly brighter than a
    // new-moon night because moonlight adds on top of the star/airglow floors rather than replacing
    // them. True pitch-black falls out with no special case — set both floors to 0 and, on a new
    // moon (or with the moon down), this is exactly 0.
    public static float NightSourceGlow(float starlightGlow, float airglowGlow, float moonlightGlow) =>
        Clamp01(starlightGlow + airglowGlow + moonlightGlow);

    // 0 above NightFloorStartElevation (day/twilight untouched), ramping to 1 at
    // NightFloorFullElevation. Because start > full (both below the horizon, descending), this is an
    // inverse-lerp on a descending range — InverseLerpClamped handles the reversed endpoints.
    public static float NightFloorWeight(float sunElevationDegrees) =>
        InverseLerpClamped(NightFloorStartElevation, NightFloorFullElevation, sunElevationDegrees);

    // Blends the vanilla glow toward our computed night floor by NightFloorWeight. Daytime (weight 0)
    // is returned unchanged, so this can never brighten or dim the day; deep night (weight 1) becomes
    // exactly nightGlow. The gradual blend (rather than a hard swap at the horizon) avoids a
    // single-frame brightness pop at sunset/sunrise, and — crucially — letting the floor cross
    // *below* vanilla's night glow is what makes true pitch-black possible: a plain Max() could only
    // ever brighten, never darken toward black.
    public static float ApplyNightFloor(float vanillaGlow, float sunElevationDegrees, float nightGlow) =>
        Lerp(vanillaGlow, nightGlow, NightFloorWeight(sunElevationDegrees));

    private static float ToRadians(float degrees) => degrees * MathF.PI / 180f;

    private static float Clamp01(float v) => Clamp(v, 0f, 1f);
    private static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float InverseLerpClamped(float a, float b, float v) => Clamp01((v - a) / (b - a));
}
