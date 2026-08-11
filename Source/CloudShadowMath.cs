using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file, same discipline as
// CloudUnderlightMath.cs, and linked into the offline tests so the shipped code is the tested code.
//
// Subsystem 23c (DESIGN.md §23c): DAYLIGHT CLOUD SHADOWS — the other thing a broken deck does to the
// light reaching the ground, and the one everybody's eye already knows.
//
// WHY THIS EXISTS AT ALL, AND WHY IT ARRIVED LAST. §23b draws the light a deck bounces DOWN after
// sunset. Watching it, the honest reaction was that it looks like the sun being shaded by clouds —
// which is the wrong reading of §23b (inside its window there is no direct sun left to shade) but is
// a completely right description of what a broken deck does for the other twelve hours of the day.
// The eye reaches for that reading because it is the common case, and the mod did not have it. So the
// two are the same phenomenon at opposite ends of the day, sharing one field:
//
//   sun below the horizon   §23b   deck is the SOURCE      add warm light where the cloud is
//   sun above the horizon   §23c   deck is the OCCLUDER    subtract light where the cloud is
//
// THE PARTITION IS THE SAME ONE, and that is what keeps this from double-counting §13/§22. Those
// already darken the whole sky by the MEAN cloud amount; what a flat colour cannot express is that
// the darkening is uneven. So this lane draws the field's residual above its own mean, exactly as
// §23b does, and the mean stays where it already was.
public static class CloudShadowMath
{
    // Peak alpha of the black wash, at a high sun over a half-covered sky. A starting guess in the
    // same sense §23b's LayerAmplitude was, and deliberately lower than it: subtracting light from a
    // DAYLIT map is far more visible than adding it to a twilit one, because the eye is comparing
    // against a bright surround rather than a near-black one. The live A/B is what settles it.
    public const float ShadowAmplitude = 0.18f;

    // Below this elevation the shadow fades out entirely. Not zero, and not §8's civil-twilight floor
    // either: a cloud shadow needs a DIRECT BEAM to block, and near the horizon almost all the light
    // reaching the ground is already diffuse skylight the deck does not occlude sharply. Ten degrees
    // is where the direct beam stops dominating in the standard clear-sky split — and it also hands
    // over cleanly to §23b, which only starts once the sun is below the horizon, leaving a wide
    // no-man's land in between where neither lane draws and the sky is simply changing colour.
    public const float DirectBeamFadeDegrees = 10f;

    // How much of the light reaching the ground is the direct beam the deck can block, in [0, 1].
    //
    // sin(elevation) is the airmass-free geometric term — the beam's illuminance on a horizontal
    // surface goes as the sine of its altitude — and the fade below DirectBeamFadeDegrees stands in
    // for the part this model does not carry: at low sun the beam has crossed a long path, is heavily
    // attenuated, and the diffuse fraction rises steeply. Squaring the fade rather than ramping it
    // linearly keeps the last couple of degrees from putting a visible shadow on a scene that is
    // mostly skylight by then.
    public static float DirectBeamFraction(float elevationDegrees)
    {
        if (elevationDegrees <= 0f)
            return 0f;

        float geometric = MathF.Sin(elevationDegrees * (MathF.PI / 180f));

        if (elevationDegrees >= DirectBeamFadeDegrees)
            return geometric;

        float t = elevationDegrees / DirectBeamFadeDegrees;
        return geometric * t * t;
    }

    // The alpha the black wash is drawn at. Zero is a true no-op — the overlay returns before its draw
    // call rather than drawing a transparent quad.
    //
    // inVacuum takes the Vacuum.cs convention: last parameter, required, early-returning before any of
    // the arithmetic. No air means no cloud to cast anything.
    public static float ShadowAlpha(float elevationDegrees, float cloudFraction, bool inVacuum) =>
        ShadowAlphaWithAmplitude(elevationDegrees, cloudFraction, ShadowAmplitude, inVacuum);

    // The amplitude spelled out, so the offline invariants do not stop meaning anything when the live
    // A/B retunes ShadowAmplitude — the same reason CloudCoverDrift.FractionWithAmplitude and
    // CloudUnderlightMath.LayerStrengthWithAmplitude exist, and the seam the harness sweep drives.
    public static float ShadowAlphaWithAmplitude(
        float elevationDegrees, float cloudFraction, float amplitude, bool inVacuum)
    {
        if (inVacuum)
            return 0f;

        if (!(amplitude > 0f))
            return 0f;

        float fraction = Clamp01(cloudFraction);

        // BOTH ENDS DRAW NOTHING, for the same reason §23b's do — and the overcast end is the one
        // worth stating, because "more cloud is more shadow" is the intuition a later change would
        // follow. A solid deck casts no PATCHES: it is uniform, so the whole map is equally shaded and
        // that is precisely what §13's flat dimming already renders. What this lane draws is the
        // unevenness, and an unbroken deck has none.
        if (fraction <= 0f || fraction >= 1f)
            return 0f;

        return amplitude * DirectBeamFraction(elevationDegrees);
    }

    private static float Clamp01(float v)
    {
        if (float.IsNaN(v))
            return 0f;

        return v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
