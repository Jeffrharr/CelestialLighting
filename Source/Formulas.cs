using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file. That's deliberate: this
// file is compiled into both Source (net481, runs inside RimWorld) and Tests (net8.0, runs
// standalone via `dotnet test`) via a linked <Compile Include>, so the exact code that ships is
// the exact code under test, not a hand-copied reimplementation that could silently drift.
// If a function here ever needs Mathf, Vector2, Map, or Find, it belongs in a patch/adapter file
// instead — pass primitives in from there.
public static class Formulas
{
    public readonly struct LatitudeContext
    {
        public readonly float Latitude;
        public readonly float Strength; // 0 at the equator, 1 by FullStrengthLatitude degrees
        public readonly float DeclinationSign; // which pole the sun currently favors, in [-1, 1]
        public readonly float HemisphereSign; // -1 south, +1 north (and at exactly the equator)
        public readonly float Lean; // HemisphereSign * DeclinationSign * Strength, in [-1, 1]

        public LatitudeContext(float latitude, float strength, float declinationSign, float hemisphereSign)
        {
            Latitude = latitude;
            Strength = strength;
            DeclinationSign = declinationSign;
            HemisphereSign = hemisphereSign;
            Lean = hemisphereSign * declinationSign * strength;
        }
    }

    // Matches RimWorld.GenDate.DaysPerYear, not an arbitrary guess — reused here so our seasonal
    // cycle stays in lockstep with vanilla's if that constant ever changes (covered by an
    // ApiCompatibilityTests assertion on the constant's value, since a silent change here would
    // desync the seasons without throwing anything).
    public const float DaysPerYear = 60f;

    // Below this latitude the effect fades to (near) nothing — real-world shadows near the
    // equator stay close to a fixed east-west lean year-round, so vanilla's unmodified behavior
    // is already a reasonable approximation there.
    public const float FullStrengthLatitude = 60f;

    // Twilight band center, in GenCelestial.CurCelestialSunGlow units — sits inside vanilla's
    // dusk (0.6) / nightEdge (0.1) thresholds.
    public const float TwilightPeakGlow = 0.35f;

    public static LatitudeContext ComputeLatitudeContext(float latitude, float dayOfYear)
    {
        float strength = LatitudeStrength(latitude);
        float declinationSign = DeclinationSign(dayOfYear);
        float hemisphereSign = Sign(latitude);
        return new LatitudeContext(latitude, strength, declinationSign, hemisphereSign);
    }

    public static float LatitudeStrength(float latitude) =>
        InverseLerpClamped(0f, FullStrengthLatitude, Abs(latitude));

    // Same one-line sinusoidal day-of-year term already present in vanilla's
    // GenCelestial.SunPositionUnmodified — reused for consistency, not copied as a substantial
    // expression.
    public static float DeclinationSign(float dayOfYear) =>
        -MathF.Cos(dayOfYear / DaysPerYear * MathF.PI * 2f);

    // Interpolates the SIGN of y toward lean's sign, not toward y's literal negation. An earlier
    // version did `Lerp(y, -y, InverseLerp(-1, 1, lean))`, which collapses y to exactly 0
    // whenever lean == 0 — not just at the equator (arguably fine) but at every equinox, at every
    // latitude including 60+ degrees, flattening shadows everywhere twice a year. This sign-blend
    // makes lean == 0 a true no-op, while still reaching a full flip continuously as |lean|
    // approaches 1 — no discontinuity at the equator or equinox, no equinox-flattening artifact.
    public static float ApplyShadowLean(float y, float lean)
    {
        float targetSign = Sign(lean);
        return Lerp(y, Abs(y) * targetSign, Abs(lean));
    }

    public static float TwilightBandWidth(float strength) => Lerp(0.12f, 0.35f, strength);

    // Peaks at sunGlow == TwilightPeakGlow, falls off linearly to 0 across bandWidth on either
    // side, then scales the peak height by latitude strength. Note this has a nonzero floor even
    // at strength == 0 (Lerp(0.15f, 0.55f, 0f) == 0.15f, not 0f) — a small warm nudge survives
    // exactly at golden hour even on the equator, by design (real equatorial sunsets are a little
    // warm too, just not as extended as high-latitude ones). Callers that want a hard zero at the
    // equator should gate on strength <= 0 themselves, as Patch_TwilightColor does.
    public static float TwilightFactor(float sunGlow, float strength)
    {
        float bandWidth = TwilightBandWidth(strength);
        return Clamp01(1f - Abs(sunGlow - TwilightPeakGlow) / bandWidth) * Lerp(0.15f, 0.55f, strength);
    }

    // Projects a section's offset from map center onto the (normalized) shadow axis, scaled to
    // [-1, 1] by how far that axis reaches toward the map edge. Returns 0 (neutral, no crash) if
    // shadowDir is degenerate (zero length) or the map has zero extent along the shadow axis.
    public static float ShadowLengthPositionFraction(
        float offsetX, float offsetZ, float shadowDirX, float shadowDirZ, float mapSizeX, float mapSizeZ)
    {
        float shadowMagnitude = MathF.Sqrt(shadowDirX * shadowDirX + shadowDirZ * shadowDirZ);
        if (shadowMagnitude <= 0.0001f)
            return 0f;

        float dirX = shadowDirX / shadowMagnitude;
        float dirZ = shadowDirZ / shadowMagnitude;

        // Half-extent of the map along the shadow axis (not a hardcoded guess) so this scales
        // correctly whether the map is small (200x200) or large (300x300+).
        float halfExtent = Abs(mapSizeX / 2f * dirX) + Abs(mapSizeZ / 2f * dirZ);
        if (halfExtent <= 0.0001f)
            return 0f;

        float positionFraction = (offsetX * dirX + offsetZ * dirZ) / halfExtent;
        return Clamp(positionFraction, -1f, 1f);
    }

    // positionFraction is expected pre-clamped to [-1, 1] (see ShadowLengthPositionFraction) but
    // this clamps again defensively so a caller passing a raw, unclamped value still can't push
    // the result outside [1 - maxVariation, 1 + maxVariation].
    public static float ShadowLengthScale(float positionFraction, float maxVariation) =>
        1f + Clamp(positionFraction, -1f, 1f) * maxVariation;

    private static float Abs(float v) => v < 0f ? -v : v;
    private static float Clamp01(float v) => Clamp(v, 0f, 1f);
    private static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float InverseLerpClamped(float a, float b, float v) => Clamp01((v - a) / (b - a));

    // Matches UnityEngine.Mathf.Sign's convention: exactly 0 returns +1, not 0. Kept explicit here
    // rather than relying on System.Math.Sign (which returns 0 for 0) so this function's behavior
    // doesn't silently depend on which sign convention some future edit assumes.
    private static float Sign(float v) => v < 0f ? -1f : 1f;
}
