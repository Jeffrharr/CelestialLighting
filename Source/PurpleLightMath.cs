using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file (same discipline as
// Formulas.cs, SkyColorTemperature.cs and OzoneTwilightMath.cs). It is linked into the test project
// via <Compile Include> so the exact code that ships is the exact code under test. If a function
// here ever needs Mathf/Color/Map, it belongs in the Patch_PurpleLight adapter instead — pass
// primitives in from there.
//
// Subsystem 19c (DESIGN.md §19c): the PURPLE LIGHT — the lavender/magenta that fills the western sky
// roughly 15-25 minutes after sunset, and the single most striking thing a clear sky does.
//
// WHAT PURPLE IS, AS A CONSTRAINT ON THE CODE. Purple is not a colour temperature. There is no point
// on the Planckian locus that is purple, which is why §8's blackbody ramp can never reach it however
// it is tuned. In channel terms purple is a GREEN MINIMUM: red and blue both high, green pushed
// below both. Every function below exists to produce that one ordering, and the tests pin the
// ordering rather than any particular colour.
//
// ============================================================================================
// WHY THE OBVIOUS CONSTRUCTION — MULTIPLY §8's REDDENING BY §19's OZONE NOTCH — CANNOT WORK
// ============================================================================================
//
// Issue #85 proposed exactly that: sunlight takes a long low path through the troposphere (reddened,
// §8) and the already-reddened light then crosses the stratospheric ozone layer (green notched out,
// §19), so the two transmissions should MULTIPLY inside the 2-degree window where both subsystems
// are live. Red-rich input minus green equals purple. It is a good story and it is wrong twice over.
// Both refutations are kept under test (SeriesGreenNotchIsReachable, and the geometry constant
// below) specifically so nobody re-derives the idea and re-implements it.
//
// FIRST REFUTATION — THE GEOMETRY. The two media are not in series along one ray. At solar
// depression theta the Earth's shadow rises to h = R_earth * (sec(theta) - 1), which across this
// subsystem's own window is 15.6 km at -4 degrees and 35.1 km at -6. The ENTIRE TROPOSPHERE IS
// ALREADY IN SHADOW there. So light that crosses the ozone layer does not afterwards cross a long
// tropospheric slant path — it drops nearly vertically through perhaps one airmass to the observer —
// and the reddened light that produces the warm band low in the west arrives along a completely
// different, near-horizontal sightline hundreds of km toward the still-lit sunset point, which never
// enters the ozone slant path. They are two different rays into two different parts of the sky.
// Sources in SERIES multiply; sources in PARALLEL add. The correct composition is a sum.
//
// SECOND REFUTATION — THE COLORIMETRY, and it holds even if you grant the series reading. Composing
// filters adds optical depths, so the composed depth is s * TroposphericDepth + m * OzoneDepth for
// some pair of weights. A green MINIMUM in transmission is a green MAXIMUM in depth, which needs
// both (depth_G - depth_R) > 0 and (depth_G - depth_B) > 0. Ozone attenuates red harder than green
// (its cross section peaks at 603 nm, essentially on the red channel centre) so it pushes the wrong
// way on the first; §8's blackbody attenuates blue vastly harder than green so it pushes the wrong
// way on the second. Each needs the other to rescue it, and the arithmetic says neither can: the
// feasible band of s/m is EMPTY at §8's sea-level horizon endpoint. See SeriesGreenNotchIsReachable
// for the closed form and the numbers. This is the same family of result PR #98 established for a
// monotone lambda^-alpha filter — a monotone filter cannot cut a mid-band notch — extended to the
// composition of two monotone filters of opposite slope, which can only do it when their slope
// ratios bracket, and these miss by a factor of about 1.5.
//
// A THIRD THING THAT TURNED OUT NOT TO BE TRUE EITHER. Issue #85 also diagnosed the current
// behaviour as "two independent additive tints toward the same sky" that "blends the purple away
// into a muddy neutral". Successive Color.Lerps toward FIXED targets are algebraically ONE lerp
// toward the weight-averaged target — with W = 1 - (1 - t8)(1 - t19) and the obvious weights the two
// forms agree to float precision, which SequentialLerps_AreOneCompositeLerp pins. Nothing is being
// averaged away that a single composed lerp would have preserved. The structure was never the
// problem.
//
// ============================================================================================
// WHAT THE PROBLEM ACTUALLY IS, AND THE FIX
// ============================================================================================
//
// AMPLITUDE, NOT STRUCTURE. Today's composition ALREADY puts green at a minimum between about -4.5
// and -5.0 degrees — but by only 3-6/255, which is why it reads as a muddy neutral rather than as
// purple. The reason is the handoff itself: §8 fades out by -6 while §19 fades in from -4, so the
// window where both are live is precisely the window where both are WEAKEST. Their strength product
// peaks at 0.048 at -5 degrees. The cross-fade models the two sources as SUBSTITUTING for one
// another, and physically they do not: at -5 degrees the reddened horizon band and the ozone-crossed
// vault are both fully present in the sky at once. Cross-fading them is the error.
//
// So this file adds a THIRD superposition term rather than rewriting either subsystem. It composes
// the two source spectra at a weight derived from the spectra themselves (BalancedBlueFraction), and
// the adapter applies the result as one further luminance-neutral nudge whose strength is a hump
// over the window, exactly zero at both edges. Zero outside the window is what makes every
// already-shipped sunset bit-identical; zero AT the edges is what keeps the seam continuous, so
// there is no step at -4 or -6.
//
// Like §2, §8, §9 and §19 this is a COLOUR-ONLY transform: the adapter blends
// WeatherWorker.CurSkyTarget's colours and never its .glow, so it does not disturb the brightness
// value other mods read (see DESIGN.md "Conflict risk"). Unlike §19 it adds no brightness arm at
// all — the purple light sits in civil twilight where there is still plenty of light, so §19's
// argument for an overlay floor does not apply here.
public static class PurpleLightMath
{
    // --- The window ---
    //
    // The purple light's window is exactly the 2-degree overlap DESIGN.md §8 and §19 already
    // describe, and both ends are references rather than fresh constants on purpose: this subsystem
    // is DEFINED as "where those two overlap", so if either boundary ever moves, this must move with
    // it rather than silently detaching into a third independently-tuned band.
    //
    // Real purple light peaks around -4 to -6 degrees solar elevation, so the window the two
    // subsystems already agreed on happens to be the physically right one. That is a check on the
    // existing boundaries, not a coincidence to lean on: issue #85 explicitly allowed widening the
    // window if a live A/B wanted it, and it did not.
    public const float WindowUpperDegrees = OzoneTwilightMath.BlueOnsetDegrees;      // -4
    public const float WindowLowerDegrees = SkyColorTemperature.NightFadeFloorDegrees; // -6

    // Earth's mean radius in km, used only by ShadowHeightKm below.
    private const float EarthRadiusKm = 6371f;

    // How high the Earth's shadow has risen when the sun sits this far below the horizon:
    // h = R * (sec(theta) - 1). This is the FIRST REFUTATION in the header expressed as code, and it
    // is here rather than in a comment so a test can assert the number instead of trusting prose.
    //
    // Across this window it returns 15.6 km (-4) to 35.1 km (-6). The troposphere tops out near
    // 10-12 km and the ozone layer sits at 20-30, so within the window the shadow has swept the
    // whole troposphere and is climbing through the ozone layer. That is the statement "the ozone
    // path and the tropospheric path are not the same ray", and it is what makes the composition
    // below a sum rather than a product.
    //
    // elevationDegrees is signed as everywhere else in the mod (negative == below the horizon), so
    // this takes the absolute value; at or above the horizon there is no shadow height to report and
    // it returns 0.
    public static float ShadowHeightKm(float elevationDegrees)
    {
        if (elevationDegrees >= 0f)
            return 0f;

        float depressionRadians = Abs(elevationDegrees) * RadiansPerDegree;
        return EarthRadiusKm * (1f / MathF.Cos(depressionRadians) - 1f);
    }

    // How much of the composed purple applies right now, in [0, 1]: a symmetric hump across the
    // window, exactly 0 at and outside both edges and 1 at the midpoint (-5 degrees).
    //
    // The shape is 4 * r * (1 - r) — the simplest function that is zero at both ends, peaks at 1 in
    // the middle, and has no free parameters to tune. Every property this subsystem's invariants
    // need comes from the two zeros:
    //   * Zero OUTSIDE the window is what makes every sunset the mod already shipped bit-identical,
    //     which is the regression pin issue #85 asked for.
    //   * Zero AT the edges is what removes the seam. §8 is still at ~0.39 strength at -4 and §19 is
    //     at ~0.63 at -6, so an envelope that switched on abruptly at either boundary would put a
    //     visible step in the middle of a live sunset.
    // A trapezoid with a plateau was considered and rejected: §19's plateau exists to express dwell
    // time at polar latitudes, and there is no equivalent argument here — the purple light is a
    // genuinely transient thing, and a hump says that.
    //
    // inVacuum (DESIGN.md §18, the shared gate in Vacuum.cs) is threaded in as a parameter rather
    // than early-returned in the adapter, per §18a's rule, so no caller can extract a nonzero shape
    // out of a vacuum. The purple light has no vacuum analogue whatsoever — it is a superposition of
    // two atmospheric scattering sources, neither of which exists — so like §19 the whole effect is
    // simply zero rather than pinned to some honest airless value.
    public static float WindowStrength(float elevationDegrees, bool inVacuum)
    {
        if (inVacuum)
            return 0f;

        if (elevationDegrees >= WindowUpperDegrees || elevationDegrees <= WindowLowerDegrees)
            return 0f;

        float span = WindowUpperDegrees - WindowLowerDegrees;
        float fromLower = (elevationDegrees - WindowLowerDegrees) / span;
        return 4f * fromLower * (1f - fromLower);
    }

    // --- The composition ---
    //
    // What fraction of the composed spectrum comes from the ozone-blued vault, against the reddened
    // horizon band. This is DERIVED, not tuned, and the derivation is the whole reason the result is
    // purple rather than merely "somewhere between warm and blue".
    //
    // The warm source is red-dominant (its red channel is 1 after normalisation) and the blue source
    // is blue-dominant (its blue channel is 1). Mix them at weight w and the composed red and blue
    // peaks are (1 - w) + w * blue.R and (1 - w) * warm.B + w. Setting those equal — NEITHER SOURCE
    // DOMINATES THE OTHER — has one solution:
    //
    //     w = (1 - warm.B) / ((1 - warm.B) + (1 - blue.R))
    //
    // and that solution is also, to within a rounding of the sampled elevations, the mix that
    // MAXIMISES the green trough. That is not luck. Green is the one channel neither source carries:
    // the warm source is green-poor because Rayleigh reddening has walked it down the blackbody
    // curve, and the blue source is green-poor because the Chappuis band eats 500-700 nm. Red and
    // blue each arrive from one source only, so any mix that favours one source dims the other's
    // peak toward green's level and fills the trough in; the balanced mix is where both peaks are as
    // far above green as they can simultaneously be.
    //
    // The physical reading is the honest one: the purple light IS the moment neither the reddened
    // band nor the blue vault dominates the western sky, which is exactly why it is a narrow window
    // rather than a phase of dusk.
    //
    // Guard: if both sources are already unsaturated (both headrooms ~0) there is no purple to
    // find and the weight is meaningless, so return an even split rather than dividing by zero. In
    // practice this is unreachable while either subsystem is live — it would need a 5772 K horizon
    // and a zero-airmass ozone path at once.
    public static float BalancedBlueFraction(SkyColorTemperature.Rgb warm, SkyColorTemperature.Rgb blue)
    {
        float warmHeadroom = 1f - warm.B;
        float blueHeadroom = 1f - blue.R;
        float total = warmHeadroom + blueHeadroom;

        if (total <= 1e-6f)
            return 0.5f;

        return warmHeadroom / total;
    }

    // The composed purple hue: the two source spectra superposed at the balanced weight, normalised
    // to a maximum channel of 1 so the result carries HUE AND NOTHING ELSE.
    //
    // Normalising here for the same reason OzoneTwilightMath.ChappuisTransmission does: the adapter
    // rescales this to the colour it is blending FROM, so only channel ratios move and no brightness
    // change is smuggled into a patch documented as colour-only.
    //
    // Both inputs are themselves normalised first. §8's blackbody is not — BlackbodyToRgb returns a
    // colour whose blue channel is near zero at the horizon endpoint, and its overall level carries
    // §8's own darkening — so feeding it raw would weight the mix by an accident of the Helland fit
    // rather than by the balance argument above.
    //
    // The saturation of the result is bounded by the MORE SELECTIVE of its two inputs automatically,
    // because a normalised convex combination cannot leave the hull of its endpoints. Issue #85 asked
    // for that as a sanity bound against a multiply running away; here it is structural rather than
    // asserted, and ComposedHue_IsNeverMoreSelectiveThanItsInputs pins it anyway.
    public static SkyColorTemperature.Rgb ComposedHue(
        float elevationDegrees,
        float latitudeDegrees,
        float pressureFraction,
        float aerosolFraction,
        float angstromExponent,
        bool inVacuum)
    {
        SkyColorTemperature.Rgb warm = Normalised(SkyColorTemperature.SkyColorForElevation(
            elevationDegrees, pressureFraction, aerosolFraction, angstromExponent, inVacuum));
        SkyColorTemperature.Rgb blue = OzoneTwilightMath.ChappuisTransmission(elevationDegrees, latitudeDegrees);

        float w = BalancedBlueFraction(warm, blue);

        return Normalised(new SkyColorTemperature.Rgb(
            Mix(warm.R, blue.R, w),
            Mix(warm.G, blue.G, w),
            Mix(warm.B, blue.B, w)));
    }

    // ============================================================================================
    // The refuted construction, kept executable so the refutation is a test rather than a paragraph
    // ============================================================================================

    // §8's blackbody colour read as a TRANSMISSION and converted to optical depth per unit of §8
    // strength. The blackbody at `horizonKelvin` divided by the unreddened ZenithKelvin anchor is
    // what §8's curve claims the tropospheric path did to sunlight, so -ln of that (normalised so
    // the least-attenuated channel is 0) is its optical-depth shape.
    //
    // At the sea-level 2000 K endpoint this is (0, 0.571, 2.808): the troposphere is claimed to eat
    // blue about five times harder than green, in log terms. That ratio is the whole problem.
    public static SkyColorTemperature.Rgb TroposphericDepth(float horizonKelvin)
    {
        SkyColorTemperature.Rgb reddened = SkyColorTemperature.BlackbodyToRgb(horizonKelvin);
        SkyColorTemperature.Rgb anchor = SkyColorTemperature.BlackbodyToRgb(SkyColorTemperature.ZenithKelvin);

        SkyColorTemperature.Rgb transmission = Normalised(new SkyColorTemperature.Rgb(
            reddened.R / anchor.R,
            reddened.G / anchor.G,
            reddened.B / anchor.B));

        return new SkyColorTemperature.Rgb(
            Depth(transmission.R), Depth(transmission.G), Depth(transmission.B));
    }

    // §19's Chappuis optical depth per unit of slant airmass, at a given latitude. Simply
    // sigma * N_column, the two Beer-Lambert factors that do not depend on the sun.
    //
    // At the pivot latitude this is (0.0422, 0.0307, 0.0008): ozone eats RED about 38% harder than
    // green, which is the other half of the problem — it pushes the green trough the wrong way.
    public static SkyColorTemperature.Rgb OzoneDepthPerAirmass(float latitudeDegrees)
    {
        float column = OzoneTwilightMath.OzoneColumnForLatitude(latitudeDegrees);
        return new SkyColorTemperature.Rgb(
            OzoneTwilightMath.CrossSectionRed * column,
            OzoneTwilightMath.CrossSectionGreen * column,
            OzoneTwilightMath.CrossSectionBlue * column);
    }

    // Can a SERIES composition of the two — the construction issue #85 proposed — produce a green
    // minimum at ANY pair of strengths? The answer is a closed form with no search in it.
    //
    // Composed depth is D = s * tropo + m * ozone. A green minimum in transmission is a green
    // maximum in depth, so both of these must hold:
    //
    //     D_G > D_R   <=>   s * (tropo_G - tropo_R) > m * (ozone_R - ozone_G)
    //     D_G > D_B   <=>   m * (ozone_G - ozone_B) > s * (tropo_B - tropo_G)
    //
    // tropo_R is 0 and ozone_B is nearly 0, so both differences on the right are positive and the
    // pair reduces to a band on the single ratio s/m:
    //
    //     (ozone_R - ozone_G)/(tropo_G - tropo_R)  <  s/m  <  (ozone_G - ozone_B)/(tropo_B - tropo_G)
    //
    // Note what has dropped out: the ABSOLUTE strengths. The question is purely one of SHAPE, so no
    // amount of turning either subsystem up or down can change the answer — which is why this is a
    // refutation rather than a tuning note. At §8's sea-level 2000 K endpoint the band is
    // (0.0202, 0.0134) — lower bound above upper bound, empty, impossible.
    //
    // It does open above a horizon endpoint of about 2181 K, which §20's site-altitude term reaches
    // above roughly 1150 m. That is a real hole in the refutation and it closes on its own: the
    // required s is then 0.35 to 0.71, against a §8 TintStrength that never exceeds 0.32 anywhere in
    // the window and that §20 scales DOWN by the very pressureFraction that opened the band. The two
    // conditions pull in opposite directions, so there is no map anywhere on which the series
    // construction produces purple.
    public static bool SeriesGreenNotchIsReachable(float horizonKelvin, float latitudeDegrees)
    {
        SeriesGreenNotchBand(horizonKelvin, latitudeDegrees, out float lower, out float upper);
        return lower < upper;
    }

    // The band itself, exposed so the tests can pin the numbers rather than only the verdict — a
    // bool alone would still pass if both bounds silently collapsed to zero.
    public static void SeriesGreenNotchBand(
        float horizonKelvin, float latitudeDegrees, out float lower, out float upper)
    {
        SkyColorTemperature.Rgb tropo = TroposphericDepth(horizonKelvin);
        SkyColorTemperature.Rgb ozone = OzoneDepthPerAirmass(latitudeDegrees);

        lower = (ozone.R - ozone.G) / (tropo.G - tropo.R);
        upper = (ozone.G - ozone.B) / (tropo.B - tropo.G);
    }

    // --- Small shared helpers ---

    // Rescale so the brightest channel is exactly 1, i.e. strip everything but hue. A colour that is
    // entirely black has no hue to preserve, so it passes through rather than dividing by zero.
    private static SkyColorTemperature.Rgb Normalised(SkyColorTemperature.Rgb c)
    {
        float brightest = Max(c.R, Max(c.G, c.B));
        if (brightest <= 0f)
            return c;

        return new SkyColorTemperature.Rgb(c.R / brightest, c.G / brightest, c.B / brightest);
    }

    // -ln(transmission), floored so a fully-extinguished channel gives a large finite depth instead
    // of an infinity that would poison every comparison downstream.
    private static float Depth(float transmission) => -MathF.Log(Max(transmission, 1e-12f));

    private const float RadiansPerDegree = MathF.PI / 180f;

    private static float Mix(float a, float b, float t) => a + (b - a) * t;
    private static float Abs(float v) => v < 0f ? -v : v;
    private static float Max(float a, float b) => a > b ? a : b;
}
