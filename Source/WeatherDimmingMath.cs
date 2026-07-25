using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file, same discipline as
// Formulas.cs / NightRadianceMath.cs. Compiled into both Source (net481, inside RimWorld) and
// Tests (net8.0, standalone) via a linked <Compile Include>, so the exact code that ships is the
// exact code under test. Anything needing Mathf/Color/Map belongs in Patch_WeatherDimming.
//
// Subsystem 13 (DESIGN.md §13): make weather actually darken the sky and soften shadows.
//
// WHY THIS EXISTS AT ALL. Vanilla does not dim sky glow by weather. WeatherWorker.CurSkyTarget
// computes `result.glow = Math.Min(CurCelestialSunGlow(map), def.maxGlow)`, and `maxGlow` defaults
// to 1.0 and is set exactly ONCE across all vanilla XML — Odyssey's `Overcast`, at 0.95. Rain, Fog,
// Blizzard, thunderstorms, Sandstorm and BlindFog all leave it at the default. So a blizzard is,
// as far as light is concerned, identical to a clear noon. §9's low-light desaturation was written
// believing otherwise ("an overcast night desaturates more than a clear one for free"); it never
// did, because at night celestial glow is ~0 under every weather alike. This subsystem is what
// finally makes that true.
//
// WHICH CHANNEL WE WRITE — the single most important decision here. SkyTarget carries two
// independent outputs, and SkyManagerUpdate consumes them separately:
//
//   .glow            -> curSkyGlowInt -> GlowGrid.GroundGlowAt  == GAMEPLAY
//                       (PlantProperties.growMinGlow 0.51, CompPowerPlantSolar, pawn psych-glow)
//   .colors.sky /    -> MatBases.LightOverlay.color, MatBases.FogOfWar,
//   .overlay /          Find.CameraColor.saturation                == PURE RENDER
//   .saturation
//
// We write the colour channel and never touch .glow. That is not a workaround — it is the channel
// vanilla already uses for weather: clear skies push skyColorsDay.sky = (1,1,1), every overcast or
// wet weather pushes (0.8,0.8,0.8). A real 20% visual darkening that never reaches GroundGlowAt.
// We deepen it and make it scale with storm intensity. This keeps CLAUDE.md's "scope is
// visual/atmospheric only" intact with no amendment: plant growth, solar output and pawn vision
// stay bit-for-bit vanilla under every weather.
public static class WeatherDimmingMath
{
    // --- Classification anchors, read off the vanilla WeatherDef census (see CloudOpacity) ---

    // Rec. 709 luma of a clear sky palette (1,1,1) and of the shared overcast/wet palette
    // (0.8,0.8,0.8). These bracket the luminance ramp: at or above clear, no cloud; at or below
    // overcast, a full deck.
    public const float ClearSkyLuminance = 1.0f;
    public const float OvercastSkyLuminance = 0.8f;

    // Saturation of the same two palettes. Vanilla's clear-family saturation is 1.25 and the
    // overcast/wet family is 0.9.
    public const float ClearSaturation = 1.25f;
    public const float OvercastSaturation = 0.9f;

    // The heaviest obscuration rate in vanilla — Odyssey's Sandstorm, sandRate 1.6. Blizzard's
    // snowRate 1.5 and every rainRate of 1.0 sit below it, so dividing by this maps vanilla's whole
    // range into [0,1] with the worst storm at exactly 1.
    public const float ObscurationReference = 1.6f;

    // Share of the maximum dimming that a *dry* deck (fog, overcast, dry thunderstorm — cloud but
    // no falling precipitation) already gets. The remaining 40% is bought by precipitation
    // intensity. Not 0: an overcast sky is meaningfully darker than a clear one even with no rain,
    // which is the single most common case players will see.
    public const float DryDeckShare = 0.6f;

    // Default peak sky darkening, as a fraction of the rendered sky brightness. 0.30 at the very
    // worst vanilla storm. Deliberately modest: this multiplies the *light overlay colour*, which
    // is already a fairly dark multiply at dusk/night, so large values crush the picture to mud
    // rather than reading as weather.
    public const float DefaultMaxDimming = 0.30f;

    // How much of the shadow's contrast a full cloud deck removes. Under heavy overcast direct
    // sunlight is replaced almost entirely by diffuse skylight and cast shadows genuinely vanish;
    // 0.85 leaves a faint 15% so the scene keeps some grounding rather than looking unlit.
    public const float MaxShadowSoftening = 0.85f;

    // --- Cloud classification ---

    // Rec. 709 luma. Not a naive channel average: a modded palette that is chromatic rather than
    // grey should be judged the way an eye weighs it, so green counts far more than blue.
    public static float Luminance(float r, float g, float b) =>
        0.2126f * r + 0.7152f * g + 0.0722f * b;

    // 0 at a clear sky's luminance, 1 at or below the overcast palette's. Descending range, which
    // InverseLerpClamped handles; the clamp means a modded palette darker than vanilla's overcast
    // plateaus at 1 instead of running away.
    public static float LuminanceDeficit(float luminance) =>
        InverseLerpClamped(ClearSkyLuminance, OvercastSkyLuminance, luminance);

    // 0 at the clear-family saturation, 1 at or below the overcast family's. Same descending shape.
    public static float SaturationDeficit(float saturation) =>
        InverseLerpClamped(ClearSaturation, OvercastSaturation, saturation);

    // How much of a cloud deck this weather's own day palette implies, in [0,1].
    //
    // THE PRODUCT IS BOTH CLASSIFIER AND GUARD, and that is the whole design. A weather must be
    // BOTH greyer AND flatter than clear to count as cloud. Taking the product rather than a max
    // or a sum is what lets this subsystem skip every explicit guard — no roof check, no biome
    // check, no defName list — because of a happy regularity in the vanilla data:
    //
    //   family                      sky            saturation   lumDef  satDef  opacity
    //   Clear, Windy, Orbit         (1,1,1)        1.25         0       0       0
    //   Underground, Undercave      (0.3,0.4,0.4)  1.25         1       0       0
    //   MetalHell                   (0.4,0.5,0.5)  1.25         1       0       0
    //   UnnaturalDarkness           (.482,.603,.682) 1.25       1       0       0
    //   Fog/Rain/Blizzard/Sandstorm (0.8,0.8,0.8)  0.9          1       1       1
    //   GrayPall / UnnaturalFog     (.482,.603,.682) 0.75/0.5   1       1       1
    //
    // Every dark-palette NON-weather (the underground set, plus Anomaly's UnnaturalDarkness) keeps
    // the clear family's saturation of 1.25, so its saturation deficit is exactly 0 and the product
    // zeroes it structurally. Luminance alone would misfire on all four. Orbit is safe under either
    // rule only because its palette is byte-identical to Clear's — that is luck, and the product
    // does not depend on it.
    //
    // A modded weather is classified by the same data it already ships, with no registration step.
    public static float CloudOpacity(float r, float g, float b, float saturation) =>
        LuminanceDeficit(Luminance(r, g, b)) * SaturationDeficit(saturation);

    // Blends the outgoing and incoming weather's opacity across a weather transition, so a front
    // rolling in eases rather than snapping. Mirrors exactly how vanilla lerps RainRate/SnowRate:
    // WeatherManager.TransitionLerpFactor runs 0 -> 1 over WeatherManager.TransitionTicks (4000).
    public static float BlendOpacity(float lastOpacity, float curOpacity, float transitionLerpFactor) =>
        Lerp(lastOpacity, curOpacity, Clamp01(transitionLerpFactor));

    // Precipitation intensity in [0,1], normalised against the heaviest vanilla storm. Max (not sum)
    // of the three rates because they are alternatives, not additives — no vanilla weather both
    // rains and sandstorms, and a modded one that did should read as one heavy storm, not a
    // double-strength one.
    public static float ObscurationIntensity(float rainRate, float snowRate, float sandRate)
    {
        float heaviest = Max3(rainRate, snowRate, sandRate);
        return Clamp01(heaviest / ObscurationReference);
    }

    // The final 0..1 fraction by which the rendered sky is darkened.
    //
    // Cloud opacity decides WHETHER (and how much of) a deck is overhead; precipitation decides how
    // hard it is coming down, moving the result across a band from DryDeckShare of maxDimming up to
    // the whole of it. maxDimming is the user's strength slider, so setting it to 0 makes every term
    // vanish and the whole subsystem a true no-op.
    public static float DimmingFraction(
        float cloudOpacity, float rainRate, float snowRate, float sandRate, float maxDimming)
    {
        float floor = DryDeckShare * maxDimming;
        float band = Lerp(floor, maxDimming, ObscurationIntensity(rainRate, snowRate, sandRate));
        return Clamp01(Clamp01(cloudOpacity) * band);
    }

    // --- Consumers ---

    // The multiplier Patch_WeatherDimming applies to SkyColorSet.sky and .overlay. Multiplying
    // (never assigning) is what lets this stack with §2's twilight warmth, §8's colour temperature
    // and §11's aurora tint instead of overwriting whichever ran first.
    public static float SkyTintFactor(float dimming) => Clamp01(1f - Clamp01(dimming));

    // Perceived brightness for §9's Purkinje ramp: what the sky LOOKS like once the deck is
    // overhead, as opposed to SkyTarget.glow, which is what the game's lighting grid still reads.
    //
    // This is the seam that finally delivers §9's original promise. §9 keys its rod-vision ramp on
    // brightness, and it wants the *apparent* brightness — a rainy night genuinely looks darker and
    // so should desaturate further. Feeding it this instead of raw .glow gets that without writing
    // a single gameplay-visible value.
    public static float ApparentGlow(float glow, float dimming) =>
        Clamp01(glow) * SkyTintFactor(dimming);

    // The multiplier Patch_ShadowStrength applies to cast-shadow alpha. Under a deck, direct sun is
    // replaced by diffuse skylight and shadows lose their edge; at full opacity only
    // (1 - MaxShadowSoftening) of the contrast survives.
    //
    // Scaled by the user's strength slider relative to the shipped default so that sliding to 0 is a
    // no-op here too, and so the one knob governs the whole subsystem coherently. Clamped at 1 so
    // pushing the slider past the default deepens the sky without ever driving shadows negative.
    //
    // Unlike PenumbraMath.PenumbraContrastFactor — which models the SUN's angular disk and so
    // applies only while the sun is up — this applies to the moon's shadow as well: clouds hide the
    // moon exactly as they hide the sun.
    public static float ShadowContrastFactor(float cloudOpacity, float maxDimming)
    {
        float strength = Clamp01(maxDimming / DefaultMaxDimming);
        return Clamp01(1f - Clamp01(cloudOpacity) * strength * MaxShadowSoftening);
    }

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float InverseLerpClamped(float a, float b, float v) => Clamp01((v - a) / (b - a));

    private static float Max3(float a, float b, float c)
    {
        float ab = a > b ? a : b;
        return ab > c ? ab : c;
    }
}
