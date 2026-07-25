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
    // OnsetGlow sits below vanilla's dusk threshold (0.6) and below §2's twilight peak (0.35): the
    // colour shift must not start eating into golden hour, when cones are still firing and the sky
    // is *supposed* to read warm. FullGlow is a small nonzero value, not 0, so the effect is fully
    // developed slightly before the sky goes pitch black — otherwise the shift would only complete
    // at a brightness too low to actually see it.
    public const float OnsetGlow = 0.30f;
    public const float FullGlow = 0.05f;

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
        InverseLerpClamped(OnsetGlow, FullGlow, sunGlow);

    // The scalar to multiply the scene's existing colour saturation by, in
    // [1 - MaxSaturationDrop, 1]. 1.0 in daylight (no change); down to 1 - MaxSaturationDrop at full
    // rod vision. This is the "brightness -> saturation falloff" the design calls for, expressed so
    // the patch can apply it with a single multiply and so its endpoints are directly unit-testable.
    public static float SaturationMultiplier(float sunGlow) =>
        Lerp(1f, 1f - MaxSaturationDrop, PurkinjeFactor(sunGlow));

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float InverseLerpClamped(float a, float b, float v) => Clamp01((v - a) / (b - a));
}
