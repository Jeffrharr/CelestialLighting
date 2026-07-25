using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file (only System). Same rule
// and reason as Formulas.cs: this file is <Compile Include>-linked into both the mod (net481,
// runs inside RimWorld) and the test project (net8.0, `dotnet test` standalone), so the exact
// brightness->saturation falloff that ships is the exact code under test. Anything needing Mathf,
// Color, Map, or Find belongs in the patch/adapter (Patch_LowLightDesaturation.cs), which passes
// primitives in from here.
//
// Purkinje shift: as scene brightness falls, human vision hands over from cones (colour, photopic)
// to rods (achromatic, scotopic). Colour discrimination drains away and the scene drifts toward a
// dim blue-grey. This file models "how far into rod vision are we right now" as a single 0..1
// factor keyed on sky glow, plus the saturation falloff that factor drives — the branching/curve a
// human has to reason about lives here, tested; the patch just applies the resulting numbers to
// Unity colours.
//
// A separate own-file static class (rather than more functions inside Formulas.cs) keeps this
// feature's math from colliding with the other in-flight subsystems that also touch Formulas.cs.
public static class PurkinjeMath
{
    // Sky-glow thresholds, in GenCelestial/WeatherWorker glow units (the same 0..1 scale
    // CurCelestialSunGlow and SkyTarget.glow use). Above OnsetGlow, cones still dominate and the
    // scene keeps its full daytime colour (factor 0). Below FullGlow, we treat vision as fully
    // rod-dominated (factor 1). In between is the mesopic hand-over ramp.
    //
    // OnsetGlow is anchored on RimWorld's own definition of "fully lit" rather than on a taste
    // value. Verse.GlowGrid.GroundGlowAt caps ordinary artificial light at exactly 0.5
    // (`b = Mathf.Min(0.5f, b)`), and PlantProperties.growMinGlow is 0.51 — which is precisely why
    // sun lamps need GroundGlowAt's `accumulatedGlowAt.a == 1 -> return 1f` escape hatch to grow
    // anything at all. 0.5 is therefore the brightest an ordinary lamp-lit cell ever reads, and a
    // lamp-lit room has to render at FULL colour. Anything below it is, by the game's own measure,
    // less than fully lit, and that is where colour should begin to drain.
    //
    // FullGlow is a small nonzero value, not 0, so the effect is fully developed slightly before the
    // sky goes pitch black — otherwise the shift would only complete at a brightness too low to
    // actually see it.
    public const float OnsetGlow = 0.50f;
    public const float FullGlow = 0.05f;

    // The ramp between those anchors is deliberately NOT linear.
    //
    // Raising the onset from 0.30 to 0.50 stretches the ramp across the whole dusk band, and a
    // linear ramp would then drain ~20% of the scene's colour at glow 0.35 — exactly §2's twilight
    // peak, where §2 and §8 are actively warming the sky and golden hour is supposed to read warm
    // rather than grey. Easing in resolves that: the curve hugs zero through the top of its range
    // and only bites once the scene is genuinely dim.
    //
    // The exponent is derived from that constraint, not picked by eye. At glow 0.35 the normalised
    // position is (0.50 - 0.35) / (0.50 - 0.05) = 1/3, and (1/3)^2.75 ~= 0.05, so the twilight peak
    // keeps ~95% of its saturation — imperceptible — while full rod vision at FullGlow is still
    // exactly 1. Monotonicity and both endpoints are preserved for any exponent > 0; only the shape
    // in between changes.
    public const float RampExponent = 2.75f;

    // At full rod vision, how much of the scene's colour saturation is removed. Not 1.0 (a total
    // greyscale) on purpose: real scotopic vision isn't perfectly colourless, and leaving a sliver
    // of saturation keeps the night from reading as a flat black-and-white photo. The remaining
    // fraction is (1 - MaxSaturationDrop). This is a sensible default; the settings pass (issue #9
    // umbrella) is expected to expose it as a slider.
    public const float MaxSaturationDrop = 0.60f;

    // How far into rod (scotopic) vision the current glow puts us, in [0, 1]. 0 = full cone/colour
    // vision at or above OnsetGlow; 1 = full rod/achromatic vision at or below FullGlow; a linear
    // ramp between. Note the inputs to InverseLerp are ordered high-glow -> low-glow, so the factor
    // *rises* as it gets darker.
    //
    // Feeding this the night-radiance-adjusted, weather-attenuated glow (see the patch) is what
    // makes the shift "strongest on the darkest nights": a new-moon or overcast night lands further
    // up this ramp than a clear full-moon one, so the pure core needs no moon or cloud term of its
    // own.
    //
    // The moon half of that was always true — §7 writes moon phase straight into .glow. The cloud
    // half was NOT, despite an earlier comment here claiming it came for free from a
    // "weather-clamped" glow: WeatherDef.maxGlow defaults to 1.0 and is set exactly once across all
    // vanilla XML (Odyssey's Overcast, 0.95), which is inert at night where celestial glow is ~0
    // under every weather alike. Until §13 landed, a blizzard and a clear sky produced an identical
    // factor here. It is §13's ApparentGlow, applied by the patch before this call, that finally
    // makes the cloud half real — see DESIGN.md §13.
    public static float PurkinjeFactor(float sunGlow) =>
        Pow(InverseLerpClamped(OnsetGlow, FullGlow, sunGlow), RampExponent);

    // The scalar §9 USED to multiply SkyColorSet.saturation by, kept only as the reference curve for
    // the tint strength — nothing writes the global saturation any more.
    //
    // Why it stopped being applied: SkyColorSet.saturation is assigned to Find.CameraColor, which is
    // a ColorCorrectionCurves image effect operating on the finished frame. It desaturates every
    // pixel equally, so a campfire burning at night came out as grey as the dark ground around it —
    // the opposite of how scotopic vision works, where a bright source keeps its colour while dim
    // surroundings lose theirs. No per-cell fix was possible through that channel: desaturation is
    // lerp(colour, luminance(colour), t), which needs the pixel's own colour, while everything §9 can
    // reach is a multiply, and a multiply can only scale or shift hue. §9 now expresses the shift
    // entirely through the per-cell CoolNight tint on the lighting overlay instead.
    public static float SaturationMultiplier(float sunGlow) =>
        Lerp(1f, 1f - MaxSaturationDrop, PurkinjeFactor(sunGlow));

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float InverseLerpClamped(float a, float b, float v) => Clamp01((v - a) / (b - a));

    // MathF.Pow via float cast rather than Math.Pow's double round-trip, matching how the rest of
    // the pure cores stay in single precision (see NightRadianceMath's MathF.Sin). The 0 and 1
    // endpoints are exact for any positive exponent, so the plateaus stay exact.
    private static float Pow(float value, float exponent) => MathF.Pow(value, exponent);
}
