using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file, exactly like Formulas.cs.
// That's deliberate: this file is compiled into both Source (net481, runs inside RimWorld) and
// Tests (net8.0, runs standalone via `dotnet test`) via a linked <Compile Include>, so the exact
// code that ships is the exact code under test. If a function here ever needs Mathf/Color/Map, it
// belongs in the thin adapter (Patch_AuroraTint / AuroraConditions) instead — pass primitives in.
//
// This backs subsystem 11 (DESIGN.md §11): while an auroral-driver condition (a solar flare) is
// active, tint the *night* sky toward auroral greens/reds. Visual only — nothing here reads or
// writes the sky's glow or any gameplay value; it produces a colour and a blend strength that a
// postfix lerps into WeatherWorker.CurSkyTarget's colours, never its .glow (see §11 conflict-risk
// lane: colour-only keeps us clear of the brightness value other mods read).
public static class AuroraMath
{
    // A plain RGB triple so this file can describe auroral colours without pulling in
    // UnityEngine.Color (which would break the "no Unity in the pure core" rule and stop the tests
    // from compiling standalone). The adapter converts this to a UnityEngine.Color at the boundary.
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

    // The two dominant auroral emission lines, expressed as approximate sRGB renderings of their
    // monochromatic wavelengths — physical constants, not values lifted from any mod:
    //   * atomic oxygen 557.7 nm — the familiar bright auroral green (most common),
    //   * atomic oxygen 630.0 nm — the high-altitude red seen during stronger geomagnetic storms.
    // A single spectral line has no exact sRGB triple (the gamut can't reproduce a pure wavelength),
    // so these are representative in-gamut approximations of each line's hue, chosen to read
    // unmistakably as "auroral green" and "auroral red" rather than to be colourimetrically exact.
    public static readonly Rgb OxygenGreen = new Rgb(0.16f, 1f, 0.36f);
    public static readonly Rgb OxygenRed = new Rgb(1f, 0.15f, 0.18f);

    // Auroras are only visible against a dark sky, so the tint must fade out as the sky brightens.
    // These two thresholds are in GenCelestial.CurCelestialSunGlow units. The upper bound reuses
    // vanilla's own GameCondition_Aurora.MaxSunGlow (0.5) — the glow above which vanilla stops
    // showing its aurora at all — so our flare-driven tint disappears at the same brightness
    // vanilla's own aurora does, rather than lingering into a visibly-lit sky.
    public const float MaxVisibleGlow = 0.5f;
    public const float FullVisibilityGlow = 0.1f;

    // How far to blend the sky/overlay colours toward the auroral hue at peak (night, mid-condition).
    // Overlay is nudged less than the sky so the vignette/overlay layer stays subordinate. Both are
    // colour-only; neither touches glow.
    //
    // These were 0.35/0.15, argued up from vanilla's 0.075/0.025 on the reasoning that we ship no
    // shimmering overlay *texture*, only a flat colour, so we need more colour to read as an aurora.
    // Rendered, that argument fails in the other direction: because the tint is flat and covers the
    // whole map at once, 0.35 does not read as "a strong aurora" but as a green filter over the
    // world — the flat-neon failure the original comment was trying to avoid. Measured on the
    // aurora_event frame it moved mean frame RGB by (-13.8, +9.2, -14.1) out of 255, about 25%.
    //
    // A flat wash and a textured aurora are not the same effect at different strengths, so matching
    // vanilla's *visible* intensity is the wrong target; the right one is a tint you notice without
    // it reading as a colour grade. Roughly half, ~2.4x vanilla, lands there. Getting genuinely
    // vivid auroras needs spatial structure and movement rather than a bigger number here — that is
    // issue #42, and if it ships these two drop back toward vanilla's values as the base layer
    // underneath it.
    public const float MaxSkyTintStrength = 0.18f;
    public const float MaxOverlayTintStrength = 0.08f;

    // The tint eases in over the condition's first FadeTicks and out over its last FadeTicks so a
    // solar flare doesn't snap a fully-green sky in and out at its start/end. 2500 ticks ≈ one
    // in-game hour (GenDate.TicksPerHour), a natural, unobtrusive ramp against a ~1-day flare.
    public const float DefaultFadeTicks = 2500f;

    // One full green→red→green shimmer cycle. Long enough (≈ several in-game hours) that the colour
    // drifts slowly rather than strobing; the actual max amount of red mixed in is capped by
    // MaxRedMix below so the aurora stays predominantly green (as real ones mostly are).
    public const float ShimmerPeriodTicks = 9000f;

    // Cap on how far toward red the shimmer ever pushes: real aurorae are dominated by the 557.7 nm
    // green line, with red appearing only intermittently and at high altitude, so we stay mostly
    // green and only lean partway toward red at the peak of each shimmer cycle.
    public const float MaxRedMix = 0.4f;

    // 0 when the sky is at/above MaxVisibleGlow (too bright to see an aurora), ramping to 1 once it
    // is dark enough (at/below FullVisibilityGlow). Linear between. This is what makes the flare
    // tint a *night* effect: a daytime solar flare produces no visible sky colour, exactly as a real
    // aurora is washed out by daylight.
    public static float NightVisibility(float sunGlow) =>
        InverseLerpClamped(MaxVisibleGlow, FullVisibilityGlow, sunGlow);

    // Eases 0→1 over the first fadeTicks of the condition and 1→0 over its last fadeTicks, holding
    // at 1 in between; the two ends are combined with Min so a very short condition (shorter than
    // 2*fadeTicks) simply peaks lower instead of ever exceeding 1. Permanent conditions pass a huge
    // ticksLeft (see the adapter), so their fade-out term stays clamped at 1 and they hold full
    // strength indefinitely.
    public static float ConditionRampFactor(float ticksPassed, float ticksLeft, float fadeTicks)
    {
        if (fadeTicks <= 0f)
            return 1f;

        float fadeIn = Clamp01(ticksPassed / fadeTicks);
        float fadeOut = Clamp01(ticksLeft / fadeTicks);
        return Min(fadeIn, fadeOut);
    }

    // Combined blend strength for the sky colour: how dark it is (NightVisibility) times how far
    // into/through the condition we are (ramp) times the peak tint. Returns 0 whenever either factor
    // is 0 (bright sky, or a condition that has fully faded), so the adapter can early-out.
    public static float SkyTintStrength(float sunGlow, float ramp) =>
        NightVisibility(sunGlow) * Clamp01(ramp) * MaxSkyTintStrength;

    // Same as SkyTintStrength but for the overlay layer, kept a fixed fraction weaker so the overlay
    // never out-saturates the sky itself.
    public static float OverlayTintStrength(float sunGlow, float ramp) =>
        NightVisibility(sunGlow) * Clamp01(ramp) * MaxOverlayTintStrength;

    // Wraps a tick count into a [0, 1) phase over the given period; the adapter feeds Find.TickManager
    // .TicksGame so the shimmer advances with game time. Guards a non-positive period (returns 0)
    // rather than dividing by zero.
    public static float PhaseFromTicks(long ticks, float periodTicks)
    {
        if (periodTicks <= 0f)
            return 0f;

        float wrapped = (float)(ticks % (long)periodTicks);
        float phase = wrapped / periodTicks;
        return phase < 0f ? phase + 1f : phase;
    }

    // A gentle green↔red↔green oscillation in [0, MaxRedMix], peaking (most red) at phase 0.5 and
    // returning to pure green at the ends. Cosine-shaped so the transition eases rather than moving
    // linearly, which reads as a slow auroral undulation rather than a mechanical fade.
    public static float ShimmerRedMix(float phase01)
    {
        float eased = 0.5f * (1f - MathF.Cos(Clamp01(phase01) * MathF.PI * 2f));
        return eased * MaxRedMix;
    }

    // Lerps from the green line (redMix == 0) to the red line (redMix == 1). Callers pass
    // ShimmerRedMix's output, which is capped at MaxRedMix, so in practice this stays green-dominant.
    public static Rgb AuroralColor(float redMix)
    {
        float t = Clamp01(redMix);
        return new Rgb(
            Lerp(OxygenGreen.R, OxygenRed.R, t),
            Lerp(OxygenGreen.G, OxygenRed.G, t),
            Lerp(OxygenGreen.B, OxygenRed.B, t));
    }

    // Convenience: the auroral colour for a given shimmer phase, i.e. AuroralColor(ShimmerRedMix(p)).
    public static Rgb AuroralColorAtPhase(float phase01) => AuroralColor(ShimmerRedMix(phase01));

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float Min(float a, float b) => a < b ? a : b;
    private static float InverseLerpClamped(float a, float b, float v) => Clamp01((v - a) / (b - a));
}
