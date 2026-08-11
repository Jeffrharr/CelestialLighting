using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file, the same discipline as
// CloudField.cs / CloudUnderlightMath.cs. Compiled into both Source (net481, inside RimWorld) and
// Tests (net8.0, standalone) via a linked <Compile Include>, so the exact code that ships is the
// exact code under test.
//
// Subsystem 26 (DESIGN.md §26, issue #140): THE TWILIGHT SWEEP — Earth's own shadow, drawn as a
// boundary that MOVES ACROSS THE MAP between sunset and the end of civil twilight.
//
// WHAT THIS IS FOR, and it is a different claim from every other sky subsystem here. §8, §13, §19,
// §19c, §22 and §23 are all ONE NUMBER OR ONE COLOUR PER FRAME, applied everywhere at once and
// changing only with the clock. §11a's aurora and §15b's eave shade are the only spatial effects,
// and both stand still. Nothing in this mod has ever MOVED ACROSS THE GROUND — and epic #103's
// standing worry is that RimWorld's fixed exposure and top-down camera make a brighter overlay read
// as washed out rather than luminous. Motion is the one visual channel that does not depend on
// exposure at all: an edge crossing the colony is legible at a fraction of the contrast a static
// wash needs, because the eye is asked to notice a CHANGE rather than judge a level.
//
// WHY THE LITERAL TERMINATOR CANNOT BE THE THING DRAWN. The day/night terminator sweeps the ground
// at ~266 m/s at latitude 55, so it crosses a 250-cell map in about a second of real-world time —
// and RimWorld's clock is compressed ~86x at 1x speed, so that is ~6 ms of wall clock. A
// physically-timed terminator is a single-frame flash. This is exactly the objection that killed
// §3's across-map shadow gradient (issues #11, #26): across 250 cells, real geometry has nothing to
// say. Anyone who proposes "just sweep the terminator" is proposing a flicker.
//
// WHAT IS DRAWN INSTEAD, AND WHY ITS TIMESCALE IS RIGHT BY CONSTRUCTION. After sunset, Earth's own
// shadow rises out of the ANTI-SOLAR horizon as a dusky band, with the pink Belt of Venus riding on
// top of it, and climbs the sky across the whole civil-twilight window — tens of minutes, not one
// second. §19c already owns the geometry (PurpleLightMath.ShadowHeightKm, h = R(sec θ − 1)) and §23
// already inverts it rather than introducing a second approximation of the same quantity. This file
// adds no new geometry at all: it reuses the window every other twilight subsystem already runs in,
// and only asks WHERE ALONG THE SUN AXIS the boundary has got to.
//
// THE PROJECTION FICTION, stated rather than glossed. A shadow rising up the SKY DOME is drawn here
// as a band crossing the MAP PLANE. That is not a projection of anything; it is the same conceit
// §11a's aurora curtain ships with, where a display 100 km up is drawn lying across the ground, and
// it was accepted there for the same reason it is accepted here — a top-down camera has nowhere else
// to put sky. What is honest about it is the TIMING and the ORDER: the boundary starts on the
// anti-solar side, moves toward the sun, and finishes exactly when §8's tint does.
public static class TwilightSweepMath
{
    // The window the sweep runs in, in degrees of solar elevation. Mirrors
    // SkyColorTemperature.NightFadeFloorDegrees rather than inventing a second twilight boundary —
    // this file is pure and cannot reference that adapter-side constant, so TwilightSweepMathTests
    // pins the two equal and fails if either moves. Choosing §8's own floor means the sweep finishes
    // at the instant §8's tint reaches zero, so the last frame of the sweep is also the last frame
    // with any colour in it, and nothing pops.
    public const float SweepFloorDegrees = -6f;

    // How far the drawn band extends AHEAD of the boundary, as a fraction of the map's sun axis.
    // This is the Belt of Venus: a real one is roughly 10-20 degrees of sky deep, i.e. a substantial
    // fraction of the visible dome rather than a line, and a band thinner than this reads as a drawn
    // stripe rather than as light.
    public const float BeltWidth = 0.34f;

    // How soft the trailing (shadow-side) edge is, as a fraction of the axis. Epic #103 asks for soft
    // edges as a first-class property of the shared pass; here the edge is a texture ramp rather than
    // inset rim geometry, so softness is one constant. A hard edge here reads as a rendering artifact
    // — a straight line of darkness creeping over the colony — which is issue #140's own named risk.
    public const float EdgeSoftness = 0.13f;

    // Peak additive strength, before the amplitude scale. Calibrated to land in the ΔE 3-6 band the
    // repo settled on after §11a's curtain read as distracting at ~9 (see CLAUDE.md's verification
    // bar), NOT at the strength that makes the mechanism most legible.
    public const float SweepAmplitude = 0.13f;

    // How the two lights in the band are weighted against each other, and this pair is a DESIGN
    // decision rather than a tuning one.
    //
    // The moving belt has to dominate the static horizon glow. Physically it is the other way round —
    // the sunset horizon is by far the brightest part of a twilight sky — but a §26 whose brightest
    // feature sits still at the sunward map edge is a worse version of what §8 already draws every
    // evening for free, and would leave the moving boundary as a faint secondary bump. The whole
    // hypothesis under test (epic #103: does MOTION read where brightness does not) needs the thing
    // that moves to be the thing you see.
    //
    // The glow is kept rather than dropped because zero would put a hard outer limit on the band:
    // beyond BeltWidth ahead of the boundary the field would fall to nothing, and a sunward half that
    // goes abruptly dark reads as a second edge travelling in front of the first. Its job is to fill,
    // not to lead. First measured at 1.0 / 0.35; the sweep that settled it is in DESIGN.md §26.
    public const float BeltWeight = 1f;

    public const float GlowWeight = 0.35f;

    // How warm the BELT's own light is, on the same 0 (§19c's anti-solar twilight hue) to 1 (§8's
    // reddened sunward tint) scale the drawn colour interpolates on.
    //
    // NOT ZERO, and the first cut's assumption that it should be is what the offline preview caught.
    // A Belt of Venus is not the anti-solar sky's colour: it is SUNLIGHT that has grazed a very long
    // low path, been reddened by it, and then been scattered back off the sky above Earth's shadow.
    // So its hue sits between the two ends rather than at one of them — which is exactly why a real
    // one is salmon-pink rather than lavender. With this at 0 the band baked out at §19c's normalised
    // hue of roughly (1.00, 0.81, 1.00), i.e. very pale magenta, and additively over a dark evening
    // ground that reads as a white streak with no colour in it at all.
    public const float BeltWarmth = 0.25f;

    // Where the shadow boundary has got to, in [0, 1] along the sun axis: 0 is the ANTI-SOLAR edge of
    // the map (where the shadow first appears, at sunset) and 1 is the SUNWARD edge (where it arrives
    // as the window closes).
    //
    // LINEAR IN ELEVATION, DELIBERATELY, and this is the one place the file trades physics for a
    // property worth more. The shadow's angular altitude is not linear in solar depression, but the
    // quantity being mapped onto it — "how far across a 250 m map" — is a fiction to begin with (see
    // the header), so a nonlinear ramp would add precision to an axis that has no units. What a
    // linear ramp buys is that the boundary moves at a CONSTANT SPEED, and constant speed is the
    // entire visual claim being tested: an edge that accelerated or stalled mid-crossing would read
    // as a stutter in the effect rather than as dusk.
    //
    // Above the horizon there is no shadow to draw and this returns 0 rather than clamping to
    // something the caller has to recognise; below the floor it is 1, i.e. fully crossed.
    public static float SweepPosition(float elevationDegrees, bool inVacuum)
    {
        // No atmosphere, no antitwilight arch — the band exists because air scatters light into the
        // sightline above the shadow, and vacuum has none. Same first-parameter-out-the-top shape as
        // every other vacuum gate here (see Vacuum.cs for why the flag is last and never defaulted).
        if (inVacuum)
            return 0f;

        if (elevationDegrees >= 0f)
            return 0f;

        return Clamp01(elevationDegrees / SweepFloorDegrees);
    }

    // The envelope over the whole window: 0 at sunset, 0 at the floor, peaking in between.
    //
    // ZERO AT BOTH ENDS IS WHY THERE IS NO SEAM, and it is §23's GlowPhase shape (4t(1-t)) reused
    // rather than re-derived. At sunset §8's own tint is at its most saturated and the sky needs no
    // help; at the floor §8 is already at zero and anything still drawn would be an additive layer
    // hanging in a dark sky with nothing to belong to. Fading in and out inside the window means the
    // feature can never produce a step, at either boundary, at any latitude — which matters more than
    // usual for a prototype that ships off, because a step is the failure mode a reviewer would
    // (correctly) refuse to merge and could easily mistake for the whole idea being wrong.
    public static float WindowEnvelope(float sweep)
    {
        float t = Clamp01(sweep);
        return 4f * t * (1f - t);
    }

    // The additive intensity at a point whose projection along the sun axis is `axisPosition` in
    // [0, 1] (0 = anti-solar edge, 1 = sunward edge), given the boundary at `sweep`.
    //
    // THREE REGIONS, and the middle one is the whole effect:
    //
    //   behind the boundary   in Earth's shadow. Exactly zero — no light is added, and the CONTRAST
    //                         against what is still lit is what the eye reads. An additive pass
    //                         cannot darken, so "shadow" here has to mean "nothing added" rather
    //                         than "something subtracted"; that is a real limitation of the lane
    //                         (epic #103) and the reason the envelope above matters so much.
    //
    //   the belt              the Belt of Venus, riding directly on the shadow's top edge. Peaks AT
    //                         the boundary and falls off ahead of it.
    //
    //   ahead of the belt     ordinary twilight sky, still lit, ramping up toward the sunward
    //                         horizon where the sunset itself is.
    //
    // The belt and the horizon glow are SUMMED rather than blended, because they are two different
    // lights arriving from two different parts of the sky — the same "sources in parallel add,
    // sources in series multiply" argument §19c makes at length about why the ozone notch and §8's
    // reddening compose as a sum. Getting that wrong is how you end up with one muddy lobe.
    public static float Intensity(float axisPosition, float sweep, float amplitude)
    {
        Components(axisPosition, sweep, out float edge, out float belt, out float glow);

        // Normalised by the weights rather than clamped, so the peak lands at `amplitude` exactly.
        // Clamping the sum instead would flatten the belt into a plateau precisely where the effect
        // is meant to be sharpest — the one part of the band a viewer's eye is tracking.
        const float TotalWeight = BeltWeight + GlowWeight;

        return amplitude * edge * (((BeltWeight * belt) + (GlowWeight * glow)) / TotalWeight);
    }

    // The colour at this point, on the same 0 (§19c's anti-solar twilight hue) to 1 (§8's reddened
    // sunward tint) scale the overlay interpolates its two endpoint colours on.
    //
    // THE COLOUR FOLLOWS THE SAME PARTITION THE INTENSITY DOES, which is the whole idea and is what
    // the first cut got wrong. The band is two lights, not one: a pink belt riding on the shadow's
    // edge and an orange glow at the sunward horizon. So the hue at any point is their
    // INTENSITY-WEIGHTED AVERAGE — wherever the belt dominates the light, the belt's colour dominates
    // the pixel, and where the horizon glow takes over it turns orange on its own. Nothing has to
    // decide where the crossover is; it falls out of which source is brighter there.
    //
    // The first cut instead ramped warmth linearly from the boundary to the map edge, which sounds
    // equivalent and is not: it put §19c's near-white magenta at the belt's peak and full orange out
    // where the alpha had fallen to almost nothing, so the only visible part of the band was
    // colourless and the only coloured part was invisible. Tools/SweepPreview is what showed that,
    // before any of it reached a screenshot.
    //
    // MEASURED FROM THE BOUNDARY, NOT FROM THE MAP EDGE, and that is what makes the colour move
    // rather than merely the brightness. A hue keyed on absolute position would leave the map with a
    // permanently pink east side all evening while only the alpha changed — a stain, not a sunset.
    public static float Warmth(float axisPosition, float sweep)
    {
        Components(axisPosition, sweep, out _, out float belt, out float glow);

        float beltLight = BeltWeight * belt;
        float glowLight = GlowWeight * glow;
        float total = beltLight + glowLight;

        // Where there is no light at all the hue is unobservable — the alpha is zero there by
        // construction — so any answer renders identically. Returning the belt's own warmth rather
        // than 0 or 1 keeps the table's two ends from being a discontinuity that a future reader
        // mistakes for a boundary condition worth preserving.
        if (total <= 1e-6f)
            return BeltWarmth;

        return Clamp01(((beltLight * BeltWarmth) + glowLight) / total);
    }

    // The three shape terms both public functions above are built from, evaluated once so the two can
    // never disagree about where the belt is.
    private static void Components(
        float axisPosition, float sweep, out float edge, out float belt, out float glow)
    {
        float p = Clamp01(axisPosition);
        float b = Clamp01(sweep);

        // Distance ahead of the boundary. Negative behind it, i.e. inside the shadow.
        float ahead = p - b;

        // The soft trailing edge. A smoothstep rather than a linear ramp so the derivative is zero
        // on both sides of the edge — a linear ramp's corner is visible as a Mach band on a flat
        // colony floor, which is exactly the "reads as a decal" failure §23b's own EdgeSoftness note
        // records for the same reason.
        edge = SmoothStep(-EdgeSoftness, EdgeSoftness, ahead);

        // The belt: peak at the boundary, gone by BeltWidth ahead of it. Squared falloff rather than
        // a true Gaussian — a Gaussian has no support boundary, so it would leave a faint haze over
        // the entire sunward half that reads as the map simply being brighter, which is the thing
        // the flat lanes already do and this one exists not to repeat.
        float beltFalloff = Clamp01(1f - (ahead / BeltWidth));
        belt = beltFalloff * beltFalloff;

        // The horizon glow, brightest at the sunward edge. Squared for the same reason a real one is
        // concentrated near the horizon: the light has come the long way round, so it is where the
        // path is longest, not spread evenly across the dome.
        glow = p * p;
    }

    // The boundary as seen by something at altitude, e.g. §25's cloud deck.
    //
    // WHY THE CLOUDS NEED THEIR OWN BOUNDARY — this is issue #140's depth question, and the answer is
    // already in the codebase rather than a new invention. Earth's shadow reaches a deck at height h
    // LATER than it reaches the ground, at the depression angle CloudUnderlightMath.
    // ShadowEntryDepressionDegrees(h) already computes for §23. So the deck behaves exactly as if the
    // sun were `shadowEntryDegrees` HIGHER than it is: its boundary lags the ground's, and it lags by
    // more for a higher deck.
    //
    // What that buys on screen is parallax. The ground's shadow edge and the cloud sheets' shadow
    // edge are in different places at the same instant, and the gap between them widens with the deck
    // altitude the weather already declares — so a high cirrus evening reads as clouds still catching
    // light long after the ground has gone dark, and a low stratus one reads as everything going out
    // together. That is the same "cloud altitude sets the timing" mechanism issue #88 is built around,
    // rendered spatially instead of as a strength curve, and it costs one addition.
    public static float DeckSweepPosition(
        float elevationDegrees, float shadowEntryDegrees, bool inVacuum) =>
        SweepPosition(elevationDegrees + Math.Abs(shadowEntryDegrees), inVacuum);

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float span = edge1 - edge0;
        if (MathF.Abs(span) < 1e-6f)
            return x < edge0 ? 0f : 1f;

        float t = Clamp01((x - edge0) / span);
        return t * t * (3f - 2f * t);
    }

    private static float Clamp01(float value)
    {
        if (value < 0f)
            return 0f;

        return value > 1f ? 1f : value;
    }
}
