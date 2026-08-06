using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file (same discipline as
// Formulas.cs and SkyColorTemperature.cs). It is linked into the test project via <Compile Include>
// so the exact code that ships is the exact code under test. If a function here ever needs
// Mathf/Color/Map, it belongs in the OzoneTwilight adapter instead — pass primitives in from there.
//
// Subsystem 19 (DESIGN.md §19): the deep blue cast of polar twilight.
//
// WHY IT IS BLUE. Not Rayleigh scattering — that is daytime blue, and it is §8's model, not ours.
// With the sun below the horizon the only light reaching the ground has crossed a near-horizontal
// path of tens to hundreds of km through the stratospheric ozone layer. Ozone's CHAPPUIS ABSORPTION
// BAND eats 450-780 nm, peaking at 603 nm — the orange/green middle of the visible spectrum. What
// survives is the short-wavelength tail. The effect is strong enough that Arctic reindeer evolved a
// seasonally-tuned tapetum for it.
//
// WHY THERE IS NO LATITUDE TERM, and this is the first question any reader will ask. The absorbing
// column is set by the sun's altitude relative to the ozone layer, not by where the observer
// stands: a sun at -7.2 degrees presents the same slant path at Svalbard and at Quito. What
// latitude changes is DWELL TIME — near the poles the diurnal circle is shallow, so the sun parks
// in the -6..-18 band for hours (in midwinter it never leaves it, which *is* polar night); at the
// equator it crosses the same band nearly vertically in ~25 minutes. So keying on elevation alone
// is not a simplification, it is the correct model, and it is strictly better than a latitude term:
// polar night emerges with no polar special case anywhere, the equator keeps its real brief blue
// hour instead of being artificially zeroed, and the whole thing tracks §14's sun clock and
// Realistic Axial Tilt for free because dwell is a property of SolarPosition.ElevationForMap rather
// than a constant we wrote down. Adding Formulas.LatitudeStrength here would double-count, since
// latitude already enters through elevation.
//
// WHY THERE IS NEVERTHELESS A LATITUDE-KEYED OZONE COLUMN, AND WHY THAT IS NOT THE PARAGRAPH ABOVE
// QUIETLY REVERSED (issue #82). Beer-Lambert is tau = sigma * N_column * airmass, three independent
// factors. The paragraph above is entirely an argument about AIRMASS — the GEOMETRY of the slant
// path — and it stands unchanged: the path length is set by the sun's altitude relative to the
// layer, so it is elevation-keyed and nothing else. N_column is a different factor. It is not a
// path length, it is HOW MUCH ABSORBER SITS ALONG THAT PATH, and on Earth that is emphatically not
// globally uniform: the Brewer-Dobson circulation lifts ozone in the tropics and transports it
// poleward, piling it up at high latitudes. Real total columns run ~260 DU over the tropics against
// ~380-420 DU at high latitudes in spring. Same slant path, more molecules per unit length.
//
// The distinction that keeps the two from colliding: scaling the AIRMASS by latitude would be
// double-counting, because latitude already reaches the airmass through elevation. Scaling the
// COLUMN by latitude is not, because nothing else in the file expresses it — before this it was one
// hardcoded number. sigma comes from published tables and airmass comes from the sun; N_column was
// the only one of the three still pinned to a constant, and this makes it as honest as the other
// two. Note what is deliberately NOT done here: no latitude threshold, no polar special case, and
// no change to BandStrength. The band still opens and closes on elevation alone at every latitude,
// so polar night still emerges from dwell time and the equator keeps its brief blue hour — it is
// simply a slightly shallower blue there, which is what the measurements say.
//
// WHY THIS IS NOT A COLOUR TEMPERATURE, and this is the decision the whole subsystem turns on. The
// obvious design is SkyColorTemperature.BlackbodyToRgb(20000) from the published ~20,000 K CCT
// measured at -7.2 degrees. It does not work, and the arithmetic says so before any code runs.
// MatBases.LightOverlay.color MULTIPLIES the scene (NightDesaturationMath's header records this as
// a measured dead end for §9, where a tint close to vanilla's night sky moved ground saturation by
// 0.001 and the effect was, measurably, not there). A multiply shifts hue only by ATTENUATING
// channels — and vanilla's Clear night sky is already almost exactly as blue as a 20,000 K
// blackbody:
//
//     vanilla Clear night sky (0.482, 0.603, 0.682)   R/B 0.707   G/B 0.884
//     Planckian 20,000 K      (0.669, 0.778, 1.000)   R/B 0.669   G/B 0.778
//
// There is nothing to move toward. Lerping the sky colour FULLY to that target attenuates red by
// only 5.3% — invisible, and §9's dead end repeating exactly.
//
// The reason is physical: polar twilight is not a blackbody. It is sunlight with a broad absorption
// notch cut out of it, and a correlated colour temperature is a poor single-number summary of a
// notched spectrum. So we model the notch directly, via Beer-Lambert transmission per channel.
// That gives five times the effect at half the blend, and it DEEPENS with elevation for free
// because the notch gets deeper as the slant path lengthens — a progression the blackbody model
// cannot express at all.
//
// ON THE 20,000 K FIGURE, since it is the number a reader will try to check us against. Our model
// is deliberately MORE saturated than that CCT: at -7.2 degrees it gives R/B 0.33 where a 20,000 K
// blackbody gives 0.67. That is not a discrepancy to fix. CCT is found by projecting a spectrum
// onto the nearest point of the Planckian locus, and a notched spectrum sits well off the locus
// toward higher saturation — so its CCT necessarily understates how coloured it looks. The measured
// CCT tells us the sky at -7.2 is blue and roughly how blue in HUE ANGLE terms; it does not bound
// the saturation, and reproducing it exactly would mean reproducing the very desaturation that made
// the blackbody model invisible. MaxSlantAirmass below is the honest calibration knob if the depth
// ever needs revisiting.
//
// Like §2, §8 and §9 this is a COLOUR-ONLY transform: the adapter blends WeatherWorker.CurSkyTarget's
// colours and never its .glow, so it does not disturb the brightness value other mods read (see
// DESIGN.md "Conflict risk"). The one brightness concession the user asked for is expressed
// separately, as a floor on §7a's overlay darkening — see OverlayFloor at the bottom of this file.
// Nothing here has any concept of gameplay light.
public static class OzoneTwilightMath
{
    // --- The absorption notch ---
    //
    // Published Chappuis cross-sections in cm^2/molecule (Brion et al. 1998, 350-830 nm; Burkholder
    // & Talukdar 1994; compiled in Atmos. Meas. Tech. 7, 609-624, 2014), sampled at the sRGB channel
    // centres. These are textbook tabulated physical constants, not mod-specific numbers — see
    // DESIGN.md "Clean-room provenance".
    //
    //   R ~600 nm sits essentially ON the 603 nm band peak (sigma 5.23e-21).
    //   G ~550 nm sits on the rising flank below the 575 nm hump (sigma 4.83e-21 at 575).
    //   B ~450 nm sits inside the Huggins-Chappuis minimum (350-470 nm), where the cross section
    //     falls below 1e-22 — ozone is effectively transparent there.
    //
    // That R > G >> B ordering IS the blue. Nothing else in this file creates the hue; the ramps
    // below only decide how deep the notch is cut and how much of it is applied.
    public const float CrossSectionRed = 5.23e-21f;
    public const float CrossSectionGreen = 3.80e-21f;
    public const float CrossSectionBlue = 0.10e-21f;

    // --- The absorber: how much ozone is actually up there (issue #82) ---
    //
    // Vertical ozone column at the PIVOT LATITUDE, molecules/cm^2 — 300 Dobson Units, the standard
    // global mean. At the Chappuis peak this is a vertical optical depth of only 0.042 (about 4%
    // absorbed straight overhead), which is exactly why the effect is invisible by day and dominant
    // at twilight: the slant path below multiplies it by tens.
    //
    // This stays the name and the number it always was, and OzoneColumnForLatitude reproduces it
    // exactly at the pivot, so the whole existing calibration survives as the MIDPOINT of the new
    // curve rather than being replaced by it. Everything below only moves away from this value.
    public const float OzoneColumn = 8.07e18f;

    // Where the global mean actually sits on the poleward gradient. 300 DU is the global mean AND
    // roughly the mid-latitude value, which is the happy accident that lets the curve be added
    // without recalibrating anything: at 45 degrees OzoneColumnForLatitude returns OzoneColumn
    // unchanged, so every number in DESIGN.md §19's transmission table still describes a
    // mid-latitude map exactly as measured.
    public const float ColumnPivotLatitudeDegrees = 45f;

    // The two ends of the gradient, as fractions of the 300 DU pivot so the pin above is structural
    // rather than a coincidence of three independently-rounded constants.
    //
    // 260 DU is the tropical value: the Brewer-Dobson circulation's rising branch is over the
    // tropics, so tropical air is young, ozone-poor and continually exported poleward.
    public const float TropicalColumnFraction = 260f / 300f;

    // 420 DU at the pole is NOT a third free parameter — it is FORCED by the other two plus the
    // shape function, and is worth reading as a prediction rather than a setting: with a sin^4
    // profile, 260 DU at the equator and 300 DU at 45 degrees (where sin^4 = 1/4) leaves
    // 260 + 4*(300-260) = 420 and no freedom at all. That it lands squarely inside the observed
    // Arctic-spring range (380-420 DU) is the check on the shape, not an input to it.
    public const float PolarColumnFraction = 420f / 300f;

    // Vertical ozone column over a given latitude, molecules/cm^2. Monotonically non-decreasing in
    // |latitude| by construction, which is the property the hue tests pin.
    //
    // WHY sin^4 RATHER THAN A RAMP IN |latitude|. Two things constrain the shape and between them
    // they leave little room. First, hemispheric symmetry: the column must be an EVEN function of
    // latitude, and smooth across the equator — a straight |latitude| ramp puts a cusp there, which
    // would read in game as the gradient reversing direction at a hard line no physical process
    // draws. Second, both ends are observationally flat: the tropical column barely moves between
    // 0 and 20 degrees, and the poleward pile-up saturates rather than spiking at the pole. sin^4
    // has zero derivative at both ends and puts its steepest gradient near 55 degrees, which is
    // where the real subtropical-to-midlatitude gradient actually lives. It is a fit to the shape
    // of the climatology, not a derivation from the circulation, and it is deliberately kept to one
    // line for that reason.
    public static float OzoneColumnForLatitude(float latitudeDegrees)
    {
        float poleward = PolewardColumnShape(latitudeDegrees);
        float fraction = TropicalColumnFraction + (PolarColumnFraction - TropicalColumnFraction) * poleward;
        return OzoneColumn * fraction;
    }

    // 0 at the equator rising to 1 at the pole. |latitude| is clamped to 90 before the sine so a
    // caller handing in an out-of-range or wrapped latitude gets the polar value rather than sin^4
    // folding back DOWN past 90 degrees and silently thinning the column at the pole.
    private static float PolewardColumnShape(float latitudeDegrees)
    {
        float sine = MathF.Sin(Min(Abs(latitudeDegrees), 90f) * RadiansPerDegree);
        float sineSquared = sine * sine;
        return sineSquared * sineSquared;
    }

    // --- The elevation band ---
    //
    // Where the blue starts. Deliberately ABOVE §8's NightFadeFloorDegrees (-6), so the blue begins
    // while the warm terms are still dying rather than after: real dusk genuinely has a warm band
    // low in the west under an already-blue vault. The 2-degree overlap is the effect, not a seam.
    public const float BlueOnsetDegrees = -4f;

    // The altitude the published ~20,000 K CCT measurement was taken at. Not a tuned number — it is
    // the model's anchor to its own reference point, and the test that reproduces that CCT samples
    // the airmass ramp here.
    public const float BluePeakDegrees = -7.2f;

    // End of nautical twilight, and where the slant path stops deepening. Between BluePeakDegrees
    // and here the band strength is HELD FLAT, and that plateau is the single most important shape
    // decision in the file: it is the mechanism by which dwell time becomes the visible variable. A
    // triangle peaking at one elevation would pulse the blue on and off as a shallow polar sun
    // oscillates around its peak; the plateau is what makes "the polar sky is simply blue for hours"
    // fall out of a function that never mentions latitude.
    public const float BluePlateauEndDegrees = -12f;

    // End of astronomical twilight. Past this no sunlight enters the ozone path at all, so the
    // effect must be exactly zero and §7's night radiance owns the sky outright.
    public const float BlueEndDegrees = -18f;

    // Slant airmass at the plateau — how many vertical ozone columns a near-horizontal ray crosses.
    // Tens is the right order for a tangent ray through a shell at ~20-25 km given Earth's radius,
    // and 45 lands the notch depth where the blue is clearly readable on screen: at the peak it
    // attenuates ground red by ~24% through the multiply overlay, against the ~5% a full-strength
    // blackbody tint managed (see the header, and GroundRedAttenuation_ExceedsVisibleThreshold).
    //
    // This is the PHYSICALLY-LABELLED knob for how deep the notch cuts, as opposed to the artistic
    // one, which is the adapter's blend strength. Prefer moving the blend for taste and this only
    // for "the absorption is wrong" — they are not interchangeable, because this one also changes
    // how fast the hue deepens with elevation and the blend does not.
    public const float MaxSlantAirmass = 45f;

    // --- The visual brightness floor (see OverlayFloor) ---
    //
    // Derived, not asserted. At -7.2 degrees with §7 running, composed glow is about 0.014, and
    // NightRadianceMath.OverlayBrightnessFactor divides by OverlayFullBrightGlow (0.19) and returns
    // about 0.076. Seven percent of vanilla overlay brightness is functionally black, and a blue you
    // cannot see is not a feature. 0.30 keeps the band clearly readable, sits well under the 1.0 a
    // full moon at zenith already reads, and sits under the Cinematic preset's minNightBrightness of
    // 0.50 — so the preset whose whole job is legibility is unaffected by construction.
    public const float DefaultOverlayFloor = 0.30f;

    // Can the sun reach the blue band AT ALL on this day, at this latitude? A cheap correctness-
    // preserving gate so the adapter can skip the transcendentals entirely.
    //
    // A pure LATITUDE threshold would be wrong, and it is worth spelling out why since "make it a
    // polar feature" is the obvious instinct: every latitude passes through -4..-18 twice a day, the
    // equator just does it in ~25 minutes. Gating on latitude alone would delete the blue hour
    // everywhere except the poles — the exact outcome the elevation-only model exists to avoid.
    //
    // But there are two states where the band genuinely cannot be reached, and both are latitude
    // COMBINED WITH season. Solar elevation over a day is continuous between its noon maximum and
    // its midnight minimum, and both extremes are closed-form with no trig at all:
    //
    //     maxElevation = 90 - |latitude - declination|      (local noon)
    //     minElevation = |latitude + declination| - 90      (local midnight)
    //
    // So the band is unreachable exactly when the sun never dips low enough (POLAR DAY / midnight
    // sun) or never climbs high enough (TRUE POLAR NIGHT above ~84.5 degrees, where the sun stays
    // under -18 all day and there is no sunlight in the ozone path to colour).
    //
    // Note what this deliberately does NOT skip: latitude 78 in midwinter has max -11.4 and min
    // -35.4, so it runs. That is civil polar night — the whole reason this subsystem exists.
    public static bool CanReachBandToday(float latitudeDegrees, float declinationDegrees)
    {
        float maxElevation = 90f - Abs(latitudeDegrees - declinationDegrees);
        float minElevation = Abs(latitudeDegrees + declinationDegrees) - 90f;

        bool sunNeverDipsLowEnough = minElevation > BlueOnsetDegrees;
        bool sunNeverClimbsHighEnough = maxElevation < BlueEndDegrees;

        return !sunNeverDipsLowEnough && !sunNeverClimbsHighEnough;
    }

    // How many vertical ozone columns the light crosses, as a function of how far the sun is below
    // the horizon. Zero at and above the horizon (a ray from an up-sun has no long slant path and
    // the daytime sky is §8's business), ramping linearly to MaxSlantAirmass by the plateau and held
    // there below it.
    //
    // Linear is a deliberate simplification of a genuinely messy geometry (the tangent point rises
    // through the ozone layer as the sun sinks, and the layer stops being illuminated from below at
    // some point). It is monotonic and correctly ordered, which is all the colour needs; the
    // alternative is a refraction-aware shell integral whose extra precision would be invisible
    // behind a 0.45 blend.
    public static float SlantAirmass(float elevationDegrees)
    {
        float depthBelowHorizon = InverseLerpClamped(0f, BluePlateauEndDegrees, elevationDegrees);
        return depthBelowHorizon * MaxSlantAirmass;
    }

    // Beer-Lambert transmission through the Chappuis band at this sun elevation and latitude,
    // normalised to a pure hue.
    //
    // The two arguments are the two independent halves of tau: elevation sets the PATH LENGTH
    // through the layer (SlantAirmass) and latitude sets the ABSORBER DENSITY along it
    // (OzoneColumnForLatitude). They are not interchangeable and neither substitutes for the other
    // — see the header's two latitude paragraphs, which exist so this signature does not read as a
    // reversal of a settled decision.
    //
    // NOTE WHAT IS NOT AN ARGUMENT HERE, because it is a standing invariant rather than an
    // oversight: site altitude and local air quality. The ozone layer sits at 20-30 km, above the
    // bulk of the atmosphere and far above the boundary layer where terrain height and aerosol
    // loading live, so a mountain map and a polluted map cross the SAME ozone column as a sea-level
    // clean one. §8's location terms and §19's must not bleed into each other, and the tests hold
    // that as a signature-level guard.
    //
    // NORMALISATION MATTERS. We divide by the maximum channel (always blue, since its cross section
    // is ~50x smaller than red's) so the result carries hue and nothing else. Without it the
    // transmission's absolute darkening would smuggle a brightness change into a patch we are
    // calling colour-only — and brightness belongs to OverlayFloor alone, because §7a runs later on
    // the composed material and lerps toward opaque black, so any brightness added here would be
    // multiplied away regardless. See DESIGN.md §19.
    public static SkyColorTemperature.Rgb ChappuisTransmission(float elevationDegrees, float latitudeDegrees)
    {
        float airmass = SlantAirmass(elevationDegrees);
        float column = OzoneColumnForLatitude(latitudeDegrees);

        float red = Transmission(CrossSectionRed, column, airmass);
        float green = Transmission(CrossSectionGreen, column, airmass);
        float blue = Transmission(CrossSectionBlue, column, airmass);

        // Blue is always the largest by construction (smallest cross section), but take the real max
        // rather than assuming it, so a future edit to the constants cannot silently produce a
        // channel above 1 and blow out the lerp in the adapter.
        float brightest = Max(red, Max(green, blue));
        return new SkyColorTemperature.Rgb(red / brightest, green / brightest, blue / brightest);
    }

    // The whole of Beer-Lambert, and the reason the three factors are three separate arguments: each
    // one is owned by a different part of the model (published tables, latitude, sun elevation) and
    // keeping them separate is what stopped N_column being invisible when it was a constant.
    private static float Transmission(float crossSection, float column, float airmass) =>
        MathF.Exp(-crossSection * column * airmass);

    // How much of the Chappuis hue applies right now, in [0, 1] — a trapezoid on elevation: 0 above
    // BlueOnsetDegrees, rising to 1 at BluePeakDegrees, HELD FLAT to BluePlateauEndDegrees, falling
    // back to 0 at BlueEndDegrees. Written as the min of a rising and a falling clamped ramp, the
    // same branch-free construction as Formulas.CivilTwilightPersistence, so a reader who knows one
    // knows this one.
    //
    // This envelope governs HOW MUCH LIGHT IS LEFT, not what colour it is — ChappuisTransmission
    // above owns the hue and keeps deepening independently as the slant path grows.
    //
    // inVacuum (DESIGN.md §18, shared gate in Vacuum.cs) is threaded in as a parameter rather than
    // early-returned in the adapter, per the house rule, so no caller can extract a nonzero shape
    // out of a vacuum. Ozone twilight has no vacuum analogue whatsoever — no ozone, no slant path,
    // no phenomenon — so unlike §8 (which still has an honest unreddened colour to pin) there is
    // simply nothing to report and the whole effect is zero.
    public static float BandStrength(float elevationDegrees, bool inVacuum)
    {
        if (inVacuum)
            return 0f;

        // Cheap guard before anything else: most of every day at most latitudes is outside the band
        // entirely, and this keeps that common case down to two comparisons.
        if (elevationDegrees >= BlueOnsetDegrees || elevationDegrees <= BlueEndDegrees)
            return 0f;

        float rising = InverseLerpClamped(BlueOnsetDegrees, BluePeakDegrees, elevationDegrees);
        float falling = InverseLerpClamped(BlueEndDegrees, BluePlateauEndDegrees, elevationDegrees);
        return Min(rising, falling);
    }

    // The visual-only brightness floor, expressed in the units Patch_PitchBlackOverlay (§7a) already
    // speaks: a minimum screen brightness, which it feeds to NightRadianceMath.OverlayBrightnessFactor.
    //
    // Real polar twilight is dim but emphatically not black — snow reads blue because scattered
    // skylight still lands on it — so without this the blue would be multiplied into darkness on any
    // preset with a low minNightBrightness. Routing it through §7a's existing argument rather than
    // writing a material or a glow value is what keeps it VISUAL-ONLY BY CONSTRUCTION:
    // OverlayBrightnessFactor feeds nothing but MatBases.LightOverlay.color and FogOfWar.color, so
    // GlowGrid, plant growth, solar output and Dub's Skylights never see it.
    //
    // Three properties, each load-bearing:
    //   * It only ever RAISES, so it can never darken a night the player's own setting made
    //     brighter. On the shipped Cinematic preset (0.50) it is inert by construction.
    //   * Scaling by tintStrength means sliding the strength to 0 restores the exact pre-feature
    //     baseline, which is what makes the harness A/B meaningful.
    //   * Scaling by bandStrength means the floor fades in and out with the blue, so there is no
    //     brightness step at -4 or -18.
    public static float OverlayFloor(float baseMinBrightness, float bandStrength, float tintStrength)
    {
        float polarFloor = DefaultOverlayFloor * bandStrength * tintStrength;
        return Max(baseMinBrightness, polarFloor);
    }

    private const float RadiansPerDegree = MathF.PI / 180f;

    private static float Abs(float v) => v < 0f ? -v : v;
    private static float Min(float a, float b) => a < b ? a : b;
    private static float Max(float a, float b) => a > b ? a : b;
    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    private static float InverseLerpClamped(float a, float b, float v) => Clamp01((v - a) / (b - a));
}
