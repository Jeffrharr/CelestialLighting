using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file (System only), same discipline
// as Formulas.cs, EclipseMath.cs and VacuumRadianceMath.cs. Linked into both the shipped mod (net481)
// and the test project (net8.0) by a single <Compile Include>, so the model that runs in RimWorld is
// the model under test.
//
// Pure core of subsystem 18e (DESIGN.md §18e): how an eclipse should RESPOND on an Odyssey vacuum
// map. Note what this file does NOT contain — any geometry. §10a's transit geometry is untouched and
// keeps firing in orbit, which reverses the line in epic #8 that said natural eclipses should stand
// down on vacuum maps.
//
// WHY THE GEOMETRY SURVIVES AND ONLY THE RESPONSE CHANGES. That line was written assuming an orbital
// platform's sun motion is an orbital period. It is not: RimWorld's orbits are stationary
// (PlanetLayer.LongLatOf derives lat/long from a static tile centre, and nothing anywhere gives an
// orbit tile a period — see DESIGN.md §18). A platform hangs permanently over one lat/long and sees
// the same sky as the surface tile below it, so a new moon at a node transits the sun for the
// platform exactly as it does for the ground under it, at the same instant and for the same duration.
// EclipseMath, MoonMath.DefaultNodalPeriodDays and the cadence test are all correct in orbit as-is
// and are deliberately not touched by this file.
//
// WHAT IS WRONG IN VACUUM IS THE RESPONSE, and it is one physical fact with two consequences:
//
//   there is nothing left to scatter light into the umbra.
//
// At sea level, standing inside the moon's shadow, most of the sky you can see is NOT in that shadow,
// and it goes on scattering sunlight down onto you the whole time. That is why a total eclipse is a
// deep blue-grey gloom rather than night, and it is exactly what vanilla's own
// GameCondition_NoSunlight.EclipseSkyColors encodes — a wan (0.482, 0.603, 0.682) grey, not black.
// In vacuum there is no scattering medium at all, so:
//
//   1. TOTALITY GOES NEAR-BLACK. The umbra is lit by the night sources and by nothing else, so its
//      minimum is the §18b night floor — starlight, unextinguished, plus a planetshine term §18b
//      derives to be ~zero at night. UmbralGlow / UmbralSkyBrightness below.
//   2. INGRESS AND EGRESS HARDEN. With the scattered-light pedestal gone, the sky brightness has
//      almost nothing left in it that does not come straight through the uncovered part of the solar
//      disc — so brightness tracks the covered fraction far more literally than at sea level.
//      CoverageTrackingError below turns "far more literally" into a number.
//
// The second falls out of the first: there is no separate hardening knob, and deliberately so. A
// tuned steepening constant would be a look choice pretending to be physics, whereas removing the
// pedestal IS the physics, and the hardening is what removing it does.
//
// LUNAR PARALLAX IS STILL IGNORED, and this is the file that owes the argument. A 200 km platform is
// displaced from the ground observer by at most 200 km against a lunar distance of ~384,400 km, so
// the moon shifts by at most atan(200 / 384400) = 0.0298° — about 6% of a lunar disc diameter
// (2 × 0.274° = 0.548°). Not zero. But §6 already accepts a flat-ecliptic approximation whose own
// error dwarfs that, and the eclipse's impact parameter is set by the moon's ecliptic latitude at
// new moon, which the nodal model produces to nothing like 0.03° precision in the first place. A
// parallax term would therefore be false precision bolted onto an approximation two orders of
// magnitude coarser. The existing simplification carries over unchanged; it does not need a new
// argument, only this one written down.
//
// EXPLICITLY OUT OF SCOPE: the planet's own shadow. A platform crosses into the planet's shadow once
// per day. That is ORDINARY ORBITAL NIGHT — it belongs to the sun clock and #32's limb-refraction
// ramp, must never become a GameCondition, and must never feed the eclipse cadence. This file only
// ever describes the moon transiting the sun, which happens about once every few game years and is
// counted by the §10a cadence test that this change leaves untouched.
public static class VacuumEclipseMath
{
    // --- The umbral minimum ---

    // The glow the sky bottoms out at during totality.
    //
    // Sea level: vanilla's own umbral glow, passed straight through. We do not have an opinion about
    // the sea-level umbra here — §10's coverage ramp already reshapes how the sky GETS there, and
    // what it arrives at is vanilla's to say.
    //
    // Vacuum: the §18b night floor, and the floor ITSELF rather than any fraction of it. That
    // distinction is the whole point of the issue: our present minimum is daylight-derived (vanilla's
    // target lerped in by the covered fraction, so a partial's floor is a slice of the daylight it
    // started from), and in vacuum the correct answer is an absolute anchor instead — the umbra is
    // lit by the same sources night is lit by, so it is exactly as bright as night is.
    //
    // Callers get nightFloorGlow from NightRadiance.FloorGlowFor(map), the one shared read #30 owns
    // and #31 binds to from the shadow side, so the eclipse umbra and a cast shadow's darkest point
    // cannot disagree — they are the same function.
    //
    // Two consequences worth stating because they look like bugs otherwise. First, the vacuum umbra
    // is a touch BRIGHTER than vanilla's, whose target glow is a flat 0: totality in orbit is starlit,
    // not switched off, and 0 was never physical. Second, the floor tracks the moon, so an eclipse
    // under a full moon would bottom out higher than one under a new moon — except that a solar
    // eclipse happens at new moon by definition, so in practice the moonlight term is ~0 and the
    // vacuum umbra lands on unextinguished starlight. The moon blocking the sun is, correctly,
    // showing us its unlit face.
    //
    // `inVacuum` is LAST and required, and the vacuum branch returns before any sea-level term is
    // consulted, per the convention Vacuum.cs sets out for the whole §18 epic.
    public static float UmbralGlow(float atmosphericUmbralGlow, float nightFloorGlow, bool inVacuum)
    {
        if (inVacuum)
            return Clamp01(nightFloorGlow);

        return atmosphericUmbralGlow;
    }

    // Multiplier applied to the eclipse's own umbral sky COLOURS.
    //
    // The glow channel above drives gameplay light; this one drives what the player actually sees,
    // and it is where "totality goes near-black" is won or lost. Vanilla's umbral colour is that wan
    // (0.482, 0.603, 0.682) grey — a Rec. 709 luma of 0.583 — so at sea level a total eclipse renders
    // at 58% of full sky brightness no matter what the glow says. That grey is not an oversight, it
    // is the scattered light from the unshadowed sky, and at sea level it is right.
    //
    // Sea level: 1, i.e. an exact identity. The postfix that applies this is therefore a provable
    // no-op on every planet-surface map — it multiplies the vanilla colours by 1.0f — which is why
    // the discriminator can be threaded through the pure math instead of short-circuited in the
    // adapter.
    //
    // Vacuum: the brightness the night sky itself renders at, via §7a's own glow→screen curve
    // (NightRadianceMath.OverlayBrightnessFactor). Using §7a's curve rather than a new one is
    // deliberate — it means the umbra is drawn by literally the same mapping that draws every night
    // in this mod, so "in vacuum, totality looks like night" is enforced by construction rather than
    // by two constants that happen to match today. At §18b's new-moon vacuum floor (0.0317 glow) that
    // is 0.167, taking the rendered umbra from 58% of full sky down to 9.7%.
    //
    // The player's own MinNightBrightness clamp rides along inside OverlayBrightnessFactor, so
    // someone who has raised the night floor for playability gets the same relief inside an eclipse.
    public static float UmbralSkyBrightnessScale(float nightFloorGlow, float minNightBrightness, bool inVacuum)
    {
        if (inVacuum)
            return NightRadianceMath.OverlayBrightnessFactor(nightFloorGlow, minNightBrightness);

        return 1f;
    }

    // Absolute rendered brightness of the umbra, 0..1, as a fraction of an unobscured daytime sky:
    // the atmosphere's own umbral brightness scaled by the rule above. Split from the scale so the
    // adapter (which multiplies live Color channels) and the offline comparison (which reasons about
    // one scalar) drive the same arithmetic.
    public static float UmbralSkyBrightness(
        float atmosphericUmbralSkyBrightness, float nightFloorGlow, float minNightBrightness, bool inVacuum) =>
        Clamp01(atmosphericUmbralSkyBrightness)
        * UmbralSkyBrightnessScale(nightFloorGlow, minNightBrightness, inVacuum);

    // --- The response curve, and the quantitative form of "ingress and egress harden" ---

    // Rendered sky brightness at a given occulted fraction of the solar disc.
    //
    // This mirrors vanilla Verse.SkyColorSet.LerpDarken — Lerp(A, Min(A, B), t) — which is what
    // SkyManager.CurrentSkyTarget actually applies for an active GameCondition, with t being the
    // coverage ramp §10 writes into SkyTargetLerpFactor. Reproducing it here rather than only in the
    // patch is what lets the offline tests reason about the COMPOSED result instead of about our
    // half of it.
    public static float EclipsedSkyBrightness(float coverage, float ambientSkyBrightness, float umbralSkyBrightness)
    {
        float ambient = Clamp01(ambientSkyBrightness);
        float darkest = MathF.Min(ambient, Clamp01(umbralSkyBrightness));
        return ambient + (darkest - ambient) * Clamp01(coverage);
    }

    // How far the sky has been dimmed, as a fraction of its unobscured brightness. 0 = untouched,
    // 1 = fully dark. This is the quantity that would equal the covered fraction exactly if the sky
    // carried no light except what comes through the uncovered part of the disc.
    public static float NormalisedDimming(float coverage, float ambientSkyBrightness, float umbralSkyBrightness)
    {
        float ambient = Clamp01(ambientSkyBrightness);
        if (ambient <= 0f)
            return 0f;

        return (ambient - EclipsedSkyBrightness(coverage, ambient, umbralSkyBrightness)) / ambient;
    }

    // THE HARDENING METRIC. Mean absolute deviation of the normalised dimming from the covered
    // fraction, sampled uniformly over a whole transit of the given magnitude.
    //
    // A perfect disc-overlap response — brightness proportional to the uncovered fraction and to
    // nothing else — scores exactly 0. Anything the umbra is lit by that does NOT come through the
    // solar disc shows up as a positive score, and the sea-level scattered-light pedestal is by far
    // the largest such thing. So this is not a shape/steepness measure: it is "how much of the sky's
    // light is not coming from the sun right now", integrated over the event.
    //
    // Kept in the pure core, next to the model it makes a claim about, so the claim is defined in one
    // place and the offline test asserts it rather than restating it.
    public static float CoverageTrackingError(
        float ambientSkyBrightness, float umbralSkyBrightness, float magnitude, int samples)
    {
        if (samples <= 0)
            return 0f;

        float total = 0f;
        for (int i = 0; i < samples; i++)
        {
            // Midpoint sampling over event progress, so neither endpoint (where coverage is 0 and
            // every curve trivially agrees) is double-counted.
            double progress = (i + 0.5) / samples;
            float coverage = (float)EclipseMath.NaturalCoverageAtProgress(
                progress, magnitude, EclipseMath.DefaultMoonSunRadiusRatio);
            total += MathF.Abs(
                NormalisedDimming(coverage, ambientSkyBrightness, umbralSkyBrightness) - coverage);
        }

        return total / samples;
    }

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
}
