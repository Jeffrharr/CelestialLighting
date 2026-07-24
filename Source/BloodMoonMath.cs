using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file, same discipline as
// Formulas.cs. It is linked into both Source (net481, inside RimWorld) and Tests (net8.0,
// standalone under `dotnet test`) via a <Compile Include>, so the exact code that ships is the
// exact code under test. If a function here ever needs Mathf/Color/Map, it belongs in the
// BloodMoon adapter or Patch_BloodMoon instead — pass primitives in from there.
//
// This is the recolour core for the "blood moon" render (DESIGN.md §12): a lunar eclipse turns a
// full moon coppery-crimson, so when a third-party blood-moon condition is active we want the
// moonlit night to read red instead of the usual silver-blue. The two nontrivial pieces —
// "how strongly to tint given how dark it is" (NightFactor) and "shift a colour toward crimson
// without dimming it" (CrimsonTint) — live here with offline [TestCase] coverage.
public static class BloodMoonMath
{
    // Rec. 601 luma coefficients — the standard perceptual weighting for collapsing RGB to a
    // single brightness value. We use it so the crimson recolour keeps each colour roughly as
    // bright as it already was: a blood moon is a *full* moon (red, but still a moonlit night, not
    // darkness — see the issue), so the tint must not double as a dimmer.
    public const float LumaR = 0.299f;
    public const float LumaG = 0.587f;
    public const float LumaB = 0.114f;

    // Deep coppery crimson — the hue a real lunar eclipse turns the Moon (long-wavelength sunlight
    // refracted through the planet's atmosphere into its umbra). Treated only as a *direction* in
    // colour space: CrimsonTint scales it to the base colour's own brightness before blending, so
    // these absolute magnitudes set the hue/saturation of the target, not its lightness.
    public const float CrimsonR = 0.72f;
    public const float CrimsonG = 0.09f;
    public const float CrimsonB = 0.06f;

    // Night ramp, in GenCelestial.CurCelestialSunGlow units (1 = full day, 0 = full night). The
    // tint is a night effect: it fades out through dusk so a blood moon that lingers past sunrise
    // doesn't paint the daytime sky red. Full crimson at/below NightFullGlow (matches vanilla's
    // nightEdge threshold of 0.1), zero at/above NightStartGlow.
    public const float NightStartGlow = 0.5f;
    public const float NightFullGlow = 0.1f;

    private const float Epsilon = 0.0001f;

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

    public static float Luma(float r, float g, float b) => LumaR * r + LumaG * g + LumaB * b;

    // 0 in daylight, ramping up to 1 as the sky darkens past NightStartGlow down to NightFullGlow.
    // Note the ramp endpoints are descending (start > full) because brighter sky = smaller effect;
    // InverseLerpClamped handles a > b fine (the divisor is negative on both sides, so it cancels).
    public static float NightFactor(float sunGlow) => InverseLerpClamped(NightStartGlow, NightFullGlow, sunGlow);

    // The crimson blend strength to apply on a map right now: how dark it is (NightFactor) scaled
    // by the caller's configured maximum. Kept separate from NightFactor so the ramp and the cap
    // are each unit-tested independently.
    public static float TintStrength(float sunGlow, float maxTint) => NightFactor(sunGlow) * Clamp01(maxTint);

    // Shifts (r, g, b) toward crimson by `strength` (0 = unchanged, 1 = full crimson), preserving
    // the input's brightness. Rather than blending toward a fixed red — which could be brighter or
    // darker than the night already is — we first rescale the crimson hue to the *input's* own
    // luma, so the target is "this exact colour, but red". Consequences that fall out for free and
    // are pinned by tests:
    //   - A black input (luma 0) stays black: no light means no red, so an unlit corner of a
    //     new-moon-dark sky isn't spuriously lit up crimson.
    //   - For a realistically dim night colour the blend preserves luma almost exactly; only when
    //     the base is bright enough that a same-luma crimson would need R > 1 does the clamp bite
    //     and the result come out a little dimmer (a blood-moon night is dim anyway, so this edge
    //     rarely matters in practice).
    public static Rgb CrimsonTint(float r, float g, float b, float strength)
    {
        strength = Clamp01(strength);
        float baseLuma = Luma(r, g, b);
        float crimsonLuma = Luma(CrimsonR, CrimsonG, CrimsonB);

        // Scale the crimson direction so its brightness matches the base colour. crimsonLuma is a
        // fixed nonzero constant, but guard the division anyway so this stays total for any future
        // retune of the crimson constants toward zero.
        float scale = crimsonLuma > Epsilon ? baseLuma / crimsonLuma : 0f;
        float targetR = Clamp01(CrimsonR * scale);
        float targetG = Clamp01(CrimsonG * scale);
        float targetB = Clamp01(CrimsonB * scale);

        return new Rgb(
            Lerp(r, targetR, strength),
            Lerp(g, targetG, strength),
            Lerp(b, targetB, strength));
    }

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float InverseLerpClamped(float a, float b, float v) => Clamp01((v - a) / (b - a));
}
