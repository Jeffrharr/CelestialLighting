using System;

namespace CelestialLighting;

// Pure math only — System-only, no UnityEngine or Verse types anywhere in this file, exactly like
// Formulas.cs. It is linked into the test project the same way Formulas.cs is (see
// CelestialLighting.Tests.csproj), so the code that ships and the code under test are the same file,
// never a hand-copied reimplementation that could drift.
//
// --- Why this exists (angular-size penumbra) ---
//
// The shadow simulator in Formulas.cs treats the Sun as a point source: it produces a shadow with a
// perfectly hard edge whose overall opacity is faded by ShadowIntensityFromElevation. Real sunlight
// comes from a disk ~0.53 degrees across, so every shadow has a *penumbra* — a soft transition band
// at its edge where the Sun is only partially occluded. That band is narrow when the Sun is high but
// widens sharply as the Sun nears the horizon, because the same angular spread of the solar disk
// projects across a rapidly-lengthening shadow. Near sunrise/sunset real shadows visibly blur and
// lose contrast; a point-source model keeps them crisp all the way down, which reads wrong.
//
// We cannot blur the shadow-mesh edge itself without a custom shader (ruled out — see DESIGN.md's
// clean-room provenance note), and MatBases.SunShadow's compiled shader exposes no edge-softness
// uniform to drive instead: scanning the shipped resources.assets for issue #11 found that
// "Custom/Sun shadow" declares only _CastVect and _Color, and that "_PenumbraSoftness" appears
// nowhere in the game's assets at all. So this file computes two things from one physical model:
//
//   1. A dimensionless penumbra *softness* in [0, 1] (PenumbraSoftness) — the dimensionless form of
//      the physics, which PenumbraContrastFactor below is a fixed function of and which PenumbraProbe
//      pins live. It is not pushed at any shader; there is no uniform to push it at.
//   2. A shadow-opacity *contrast factor* in [1 - MaxContrastLoss, 1] (PenumbraContrastFactor) — a
//      guaranteed-visible 2D approximation of the same effect that needs no shader property. As the
//      penumbra widens, more of the shadow footprint is only partially shaded, so the shadow's
//      average darkness (its contrast against the lit ground) drops. Multiplying the existing shadow
//      opacity by this factor washes low-Sun shadows out toward the horizon exactly as a widening
//      penumbra would, using the opacity channel the shader already reads today.
public static class PenumbraMath
{
    // The Sun's mean angular radius as seen from Earth: the solar disk is ~0.533 degrees across, so
    // its radius is ~0.2665 degrees. Textbook value (solar angular diameter), not a tuned knob — it
    // is the physical cause of the penumbra. A caster's edge is lit by rays spanning the elevation
    // band [elevation - alpha, elevation + alpha]; the two limbs' shadow edges bracket the penumbra.
    public const float SunAngularRadiusDegrees = 0.2665f;

    // The lower solar limb's ray defines the far (fully-unshadowed) edge of the penumbra. As the Sun
    // sinks, that limb approaches and then crosses the horizon, at which point its shadow edge runs
    // to infinity and the penumbra is effectively unbounded. Flooring the limb elevation at a small
    // positive angle caps the modelled penumbra width at a large finite value (cot of this angle)
    // instead of dividing by ~0 or going negative below the horizon — the softness curve then just
    // saturates to ~1, which is the intended "fully soft" reading there.
    private const float MinLimbElevationDegrees = 0.1f;

    // The penumbra-width-per-caster-height at which PenumbraSoftness reads exactly 0.5. This sets
    // where along the descent the softening becomes pronounced: width == 1 corresponds to a Sun
    // elevation of ~5.6 degrees, so softness climbs through its middle range across the last handful
    // of degrees before the horizon and saturates near it — matching how real penumbrae only widen
    // conspicuously very close to sunrise/sunset.
    private const float SoftnessHalfWidth = 1.0f;

    // The most opacity a fully-soft (horizon) shadow loses relative to a hard high-Sun one. Capped
    // below 1 deliberately: even a maximally-soft shadow stays partly visible (retains
    // 1 - MaxContrastLoss == 0.4 of its opacity) rather than vanishing — outright disappearance near
    // the horizon is the separate job of Formulas.ShadowIntensityFromElevation's existence ramp, not
    // this contrast term. The two compose by multiplication in the draw path.
    public const float MaxContrastLoss = 0.6f;

    // Penumbra width on the ground, per unit of caster height, from the two solar-limb shadow edges:
    // width/height = cot(elevation - alpha) - cot(elevation + alpha). Monotonically increasing as
    // elevation falls (tiny near the zenith, growing without bound toward the horizon), unlike the
    // width-relative-to-shadow-length ratio, which degenerates at the zenith where the shadow length
    // itself goes to zero. Returns 0 for a Sun straight overhead and saturates to a large value once
    // the lower limb reaches the horizon floor.
    public static float PenumbraWidthPerHeight(float elevationDegrees)
    {
        float lowerLimb = elevationDegrees - SunAngularRadiusDegrees;
        float upperLimb = elevationDegrees + SunAngularRadiusDegrees;

        // Below the (floored) horizon the lower-limb edge is undefined/infinite; clamp so the width
        // caps at a large finite value rather than diverging or flipping sign.
        float flooredLowerLimb = lowerLimb < MinLimbElevationDegrees ? MinLimbElevationDegrees : lowerLimb;

        float width = CotDegrees(flooredLowerLimb) - CotDegrees(upperLimb);
        return width < 0f ? 0f : width;
    }

    // Maps the unbounded penumbra width to a smooth, bounded softness in [0, 1) via w / (w + k):
    // 0 when the Sun is overhead (no penumbra), 0.5 at width == SoftnessHalfWidth, approaching 1 as
    // the penumbra grows without bound near the horizon. No hard cliff — a saturating curve rather
    // than a linear ramp against a reference width, so there is no discontinuity to tune around.
    // Consumed by PenumbraContrastFactor below and pinned live by PenumbraProbe; no shader reads it
    // (RimWorld's sun-shadow shader has no edge-softness uniform — see the file header).
    public static float PenumbraSoftness(float elevationDegrees)
    {
        float width = PenumbraWidthPerHeight(elevationDegrees);
        return width / (width + SoftnessHalfWidth);
    }

    // Multiplier applied to a shadow's opacity: 1 (no change) when the Sun is high and the shadow
    // edge is effectively hard, falling toward 1 - MaxContrastLoss as the penumbra widens near the
    // horizon. This is the shader-property-free approximation — a wider penumbra means a larger
    // partially-shaded fraction of the footprint, i.e. a lower-contrast (washed-out) shadow — applied
    // to the opacity channel the sun-shadow shader already reads.
    public static float PenumbraContrastFactor(float elevationDegrees) =>
        1f - MaxContrastLoss * PenumbraSoftness(elevationDegrees);

    // cot(x) with x in degrees. Callers guarantee x stays away from 0 (via MinLimbElevationDegrees)
    // so sin(x) is never ~0 here; no divide-by-zero guard is needed at this layer.
    private static float CotDegrees(float degrees)
    {
        float radians = degrees * MathF.PI / 180f;
        return MathF.Cos(radians) / MathF.Sin(radians);
    }
}
