using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file, same discipline as
// Formulas.cs / NightRadianceMath.cs / BloodMoonMath.cs. Linked into both Source (net481, inside
// RimWorld) and Tests (net8.0, standalone under `dotnet test`) via a <Compile Include>, so the exact
// code that ships is the exact code under test. Anything needing Mathf/Color/Map belongs in
// Patch_LimbRefraction instead — pass primitives in from there.
//
// Subsystem 18d (DESIGN.md §18d): the limb-refraction flash — what an orbital platform sees at
// sunset instead of ground twilight.
//
// THE PROBLEM. On an Odyssey vacuum map vanilla runs the full ground sky cycle 200 km up
// (SkyManagerUpdate -> WeatherWorker.CurSkyTarget -> GenCelestial.CurCelestialSunGlow; the Space
// biome sets neither disableSkyLighting nor disableShadows), so the platform gets a sea-level dusk
// it has no atmosphere to make. §18a strips that tint back out and deliberately leaves a hard step
// at the terminator. This is what physically replaces it, and it is the only vacuum change in the
// epic that ADDS a look rather than removing one.
//
// THE PHYSICS. Nothing is between the platform and the sun, so the sun does not dim at all as it
// descends — right up until the planet itself gets in the way. What happens next is the only
// atmospheric optics an orbital observer ever sees: for the last couple of degrees the sunlight
// reaching the platform has grazed the planet's atmospheric limb, taking the longest possible path
// through air, and Rayleigh scattering has stripped everything but the red end out of it. Same
// geometry that makes a totally eclipsed moon copper — the platform is briefly inside the planet's
// own penumbra, lit only by light bent through its ring of sunset. Then the sun goes behind the
// solid limb and there is nothing left but planetshine.
//
// Three consequences, all three falling out of the geometry rather than being dialled in:
//
//   1. THE HORIZON IS DEPRESSED. From altitude h the tangent line to the surface is
//      acos(R / (R + h)) below local horizontal, so the platform stays lit ~14 degrees of solar
//      travel past the ground beneath it — the better part of an hour at both ends of the day.
//      Expressed here as a shift of the sun-clock's own elevation->glow curve, which is what makes
//      this "a curve swap in a ramp we already own" rather than a new brightness model.
//
//   2. THE RAMP IS SHORT. Only the lower atmosphere refracts. From 200 km the limb is 1609 km away,
//      so a 50 km shell is worth under two degrees of solar depression. Add the sun's own disc and
//      the whole red phase is ~2.4 degrees against a sea-level twilight that runs to -18. About
//      one-seventh the angular width.
//
//   3. THE RAMP IS RED, THEN IT STOPS. Extinction along the limb path is exponential in the ray's
//      tangent altitude, so the colour barely moves for the first half of the band and then dives
//      into deep red over the last few tenths of a degree, at which point the solid limb cuts the
//      disc off. The "step" to the night floor is not a discontinuity we inserted; it is what an
//      exponential looks like when you run out of band.
//
// WHAT THIS IS NOT. This is the daily planet-occultation — ordinary orbital night, once per game
// day, driven entirely by the sun clock. It is not a GameCondition and there is no event. Eclipses
// (moon transits the sun, once every few game years) are §10a's and they stay there. The boundary is
// exact: platform crosses into the planet's shadow == daily == here; moon crosses the sun == rare ==
// there.
//
// ALSO DELIBERATELY ABSENT: the green/blue flash real astronauts report at the top edge of the band.
// It is real, but it is a POINTING phenomenon — you see it because you are looking along the limb at
// one spot on the planet's edge. Our lighting is full-map ambient with no view direction at all, so
// there is no honest way to present it here, and a green tint on the whole sky would be decoration
// pretending to be physics. Left out on purpose; see DESIGN.md §18d.
public static class LimbRefractionMath
{
    // ------------------------------------------------------------------
    // THE ANCHORS. Everything below is derived; these five numbers are not.
    // ------------------------------------------------------------------
    //
    // RimWorld gives us no planet radius, no atmospheric depth and no scale height, and there is no
    // way to derive them from anything the game exposes. So we pick Earth-like values ONCE, name them
    // here and in DESIGN.md §18d as the anchor, and derive the dip angle, the band width, the ramp
    // endpoints and the colour from them. Same contract the rest of the mod runs on: the constants
    // are named and few, and nothing downstream is hand-tuned to make a screenshot look right.

    // ANCHOR 1 — planet radius. Earth's mean radius. RimWorld's planet has no stated size; the world
    // sphere's PlanetLayerSettings.radius is 100 for the surface layer and 130 for orbit, which are
    // render-space units for drawing the globe, not kilometres.
    public const float PlanetRadiusKm = 6371f;

    // ANCHOR 2 — platform altitude, from PlanetLayerDef.elevationString on Odyssey's OrbitLayer,
    // which reads "200km".
    //
    // That field is `[MustTranslate] public string elevationString = "{0}m"` — a DISPLAY STRING for
    // the world-map UI, not a simulation quantity. Nothing in the game parses a number back out of
    // it. So this is an anchor, not a lookup, and it is labelled as one.
    //
    // The other candidate is a trap and is rejected here explicitly: PlanetLayerSettings
    // .extraCameraAltitude is a CAMERA parameter. It sits in the same IExposable struct as `origin`,
    // `viewAngle`, `subdivisions` and `backgroundWorldCameraOffset`, and Odyssey's OrbitLayer
    // settings set it to 300 against a sphere `radius` of 130 — more than two planetary radii of
    // pull-back. Read as a physical altitude that would be ~15000 km, not 200. It is where the camera
    // sits to frame the layer, and nothing else.
    public const float OrbitAltitudeKm = 200f;

    // ANCHOR 3 — depth of the refracting shell. The part of the atmosphere thick enough to bend and
    // redden a grazing ray: troposphere plus stratosphere, call it 50 km. Cross-check against ANCHOR 4
    // below: 50 km is 6.25 scale heights, where density has fallen to e^-6.25 (about 0.2%) of sea
    // level and there is nothing left to scatter. The two anchors are consistent rather than
    // independent, but both are stated because neither is derived from the other here.
    public const float RefractingShellDepthKm = 50f;

    // ANCHOR 4 — atmospheric scale height. Earth's ~8 km. This is what makes the ramp's SHAPE, not
    // just its width: density (and so optical depth along the limb path) falls as exp(-altitude / H),
    // which is why the colour is nearly unchanged for the first half of the band and then collapses
    // into red at the end. Consequence 3 is this constant.
    public const float ScaleHeightKm = 8f;

    // ANCHOR 5 — the sun's angular diameter, Earth's 0.53 degrees. The sun is not a point, so both
    // ends of the band are smeared by the disc: first contact with the shell happens half a disc
    // early, and total occultation half a disc late.
    public const float SolarAngularDiameterDegrees = 0.53f;

    // The sun's apparent travel rate, used only to express the band as a duration: 360 degrees per
    // 24 h. This is the equatorial rate; at higher latitudes the sun descends obliquely and the band
    // lasts longer, so anything derived from this is a LOWER bound on duration.
    public const float SunTravelDegreesPerHour = 15f;

    // ------------------------------------------------------------------
    // Rayleigh scattering coefficients
    // ------------------------------------------------------------------
    //
    // Zenith optical depth at sea level follows tau(lambda) = 0.0088 * lambda^-4.15 with lambda in
    // microns — the standard Rayleigh fit. The -4.15 exponent (rather than a textbook -4) folds in
    // the dispersion of air's refractive index. Evaluated at representative RGB wavelengths, this is
    // what makes the band red rather than "red because we said so": along the grazing path blue is
    // attenuated by e^-17.1 while red is only attenuated by e^-3.7.
    public const float RedWavelengthMicrons = 0.65f;
    public const float GreenWavelengthMicrons = 0.55f;
    public const float BlueWavelengthMicrons = 0.45f;

    private const float RayleighCoefficient = 0.0088f;
    private const float RayleighExponent = -4.15f;

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

    // ------------------------------------------------------------------
    // Derived geometry. Declaration order is dependency order — C# initialises static fields in
    // source order, so moving one of these above what it reads would silently zero it.
    // ------------------------------------------------------------------

    // Distance from planet centre to platform.
    public static readonly float OrbitRadiusKm = PlanetRadiusKm + OrbitAltitudeKm;

    // How far below local horizontal the SOLID limb sits: acos(R / (R + h)). 14.172 degrees for the
    // anchors above. This is also exactly the depression at which the sun's centre is cut off — see
    // the impact-parameter argument on TangentAltitudeKm.
    public static readonly float HorizonDipDegrees =
        ToDegrees(MathF.Acos(PlanetRadiusKm / OrbitRadiusKm));

    // Same construction for the top of the refracting shell: acos((R + d) / (R + h)). 12.266 degrees.
    // A ray grazing at this depression just skims the top of the air.
    public static readonly float ShellTopDipDegrees =
        ToDegrees(MathF.Acos((PlanetRadiusKm + RefractingShellDepthKm) / OrbitRadiusKm));

    // Slant range from platform to the tangent point on the surface: sqrt((R + h)^2 - R^2). 1608.9
    // km. Used only to state the linearised cross-check (in tests and DESIGN.md); the band width
    // below is computed exactly instead.
    public static readonly float LimbDistanceKm =
        MathF.Sqrt(OrbitRadiusKm * OrbitRadiusKm - PlanetRadiusKm * PlanetRadiusKm);

    // Degrees of solar depression between skimming the shell top and touching the solid limb: 1.907.
    //
    // NOTE ON THE EXACT vs LINEARISED FORM. The back-of-envelope version is
    // atan(shellDepth / limbDistance) = 1.780 degrees — treat the shell as a bar of known length held
    // perpendicular at the tangent point. That is the first-order term of the exact expression: the
    // impact parameter is (R + h) cos(delta), so d(impact)/d(delta) = -(R + h) sin(dip), and
    // (R + h) sin(dip) IS LimbDistanceKm. The linearisation runs 7% low because sin grows across the
    // band. We take the exact difference of tangent lines because it costs nothing and because
    // "derive it, don't tune it" should mean the real geometry rather than the estimate that
    // motivated it. LimbGeometry_LinearisedShellArc_MatchesExactFormToWithinAnEighthOfADegree pins
    // the relationship, so the estimate stays a documented cross-check rather than a forgotten
    // discrepancy.
    public static readonly float ShellArcDegrees = HorizonDipDegrees - ShellTopDipDegrees;

    public static readonly float SolarAngularRadiusDegrees = SolarAngularDiameterDegrees * 0.5f;

    // The band, in map-facing terms: sun ELEVATION (negative = below local horizontal), because that
    // is what SolarPosition.ElevationForMap hands every other subsystem.
    //
    // Top of the band is where the sun's LOWER edge first touches the shell top; bottom is where its
    // UPPER edge disappears behind the solid limb. -12.001 to -14.437 degrees.
    //
    // Worth being explicit, because the issue's prose reads the other way round: the 14.17-degree dip
    // is where the light STOPS, not where the ramp starts. The dip is the solid limb by construction
    // (it is acos(R / (R + h)), with R the surface), so the refraction band necessarily sits ABOVE it
    // and the step to the planetshine floor happens AT it. Full sun until -12.0, red ramp down to
    // -14.4, then nothing. Symmetric on sunrise, since elevation is symmetric about solar noon and
    // nothing here reads the time of day.
    public static readonly float BandTopElevationDegrees = -(ShellTopDipDegrees - SolarAngularRadiusDegrees);
    public static readonly float BandBottomElevationDegrees = -(HorizonDipDegrees + SolarAngularRadiusDegrees);

    // 2.437 degrees — the shell arc plus one whole solar disc.
    public static readonly float BandWidthDegrees = BandTopElevationDegrees - BandBottomElevationDegrees;

    // 9.75 minutes at the equatorial rate.
    public static readonly float BandDurationMinutes = BandWidthDegrees / SunTravelDegreesPerHour * 60f;

    // Consequence 1 as a number: how much further the sun must fall past the GROUND's sunset before
    // the platform loses it. Formulas.AtmosphericRefractionDegrees (-0.83) is the sea-level horizon
    // the rest of the mod already uses, so this is measured against the same reference the surface
    // subsystems do rather than against a bare 0.
    public static readonly float SunlitOvershootDegrees =
        Formulas.AtmosphericRefractionDegrees - BandBottomElevationDegrees;

    // 54.4 minutes at the equatorial rate — "roughly an hour past the ground below it", falling out
    // of the geometry instead of being asserted.
    public static readonly float SunlitOvershootMinutes =
        SunlitOvershootDegrees / SunTravelDegreesPerHour * 60f;

    // Limb-path amplification: how much more air a ray grazing the surface crosses than one coming
    // straight down. For an exponential atmosphere the grazing slant column is sqrt(2 * pi * R / H)
    // times the zenith column — about 70.7x for the anchors here. This single factor is why the band
    // goes copper rather than merely warm, and it is the same factor that reddens a totally eclipsed
    // moon (§12's crimson, arrived at from the other side).
    public static readonly float LimbPathAmplification =
        MathF.Sqrt(2f * MathF.PI * PlanetRadiusKm / ScaleHeightKm);

    // Optical depth of the full grazing path at zero tangent altitude, per channel: red 3.72,
    // green 7.44, blue 17.11 — transmissions of 2.4%, 0.06% and 4e-8 respectively.
    public static readonly float GrazingOpticalDepthRed = GrazingOpticalDepth(RedWavelengthMicrons);
    public static readonly float GrazingOpticalDepthGreen = GrazingOpticalDepth(GreenWavelengthMicrons);
    public static readonly float GrazingOpticalDepthBlue = GrazingOpticalDepth(BlueWavelengthMicrons);

    public static float ZenithOpticalDepth(float wavelengthMicrons) =>
        RayleighCoefficient * MathF.Pow(wavelengthMicrons, RayleighExponent);

    private static float GrazingOpticalDepth(float wavelengthMicrons) =>
        ZenithOpticalDepth(wavelengthMicrons) * LimbPathAmplification;

    // ------------------------------------------------------------------
    // Shape helpers — deliberately UNGATED
    // ------------------------------------------------------------------
    //
    // These three describe the geometry of a limb sightline and nothing else; they are the direct
    // analogue of §18a leaving Formulas.TwilightBandWidth / TwilightPeakHeight ungated while gating
    // TwilightFactor. Gating a shape parameter would let a caller pull a "vacuum shape" out of a
    // surface map, which is backwards: the gate belongs on the EFFECT, where a wrong answer is
    // visible. The four effect-level entry points below all carry it.

    // How high above the surface the ray from the sun's centre to the platform passes, in km.
    //
    // The sun is far enough away to treat its rays as parallel, so the ray is a straight line through
    // the platform in the sun's direction. The perpendicular distance from the planet's centre to
    // that line — the impact parameter — is (R + h) * cos(depression): the platform sits at radius
    // (R + h), and the angle between its up-vector and the line is 90 degrees + depression, whose
    // sine is cos(depression). Subtract R and that is the tangent altitude. Note this makes the dip
    // fall straight out: the impact parameter equals R exactly when depression == acos(R / (R + h)).
    //
    // Clamped at 0: once the line passes below the surface the sun is simply occulted and there is no
    // deeper path to compute. Above the shell top it returns altitudes greater than
    // RefractingShellDepthKm, which is correct and harmless — the exponential has already made the
    // optical depth negligible there, so no separate "outside the shell" branch is needed.
    public static float TangentAltitudeKm(float sunElevationDegrees)
    {
        float depressionDegrees = -sunElevationDegrees;
        float altitude = OrbitRadiusKm * MathF.Cos(ToRadians(depressionDegrees)) - PlanetRadiusKm;
        return altitude < 0f ? 0f : altitude;
    }

    // Per-channel transmission along the limb path, in [0, 1].
    //
    // tau(z) = tau_grazing * exp(-z / H): for an exponential atmosphere the column density along a
    // grazing path scales with the local density at the tangent point, so the whole altitude
    // dependence is one exponential and the grazing constants above carry all the geometry.
    // Beer-Lambert from there.
    //
    // Evaluated at the tangent altitude of the disc CENTRE, not integrated across the disc. That is a
    // deliberate simplification: a real limb sunset has the disc's lower half redder than its upper
    // half, which is exactly the razor-thin gradient astronauts photograph — and it is a pointing
    // phenomenon we have no way to show in a full-map ambient tint anyway (same reasoning as the
    // green flash, see the file header). Integrating would move the mean colour by far less than the
    // anchors' own uncertainty.
    public static Rgb LimbTransmission(float sunElevationDegrees)
    {
        float density = MathF.Exp(-TangentAltitudeKm(sunElevationDegrees) / ScaleHeightKm);
        return new Rgb(
            MathF.Exp(-GrazingOpticalDepthRed * density),
            MathF.Exp(-GrazingOpticalDepthGreen * density),
            MathF.Exp(-GrazingOpticalDepthBlue * density));
    }

    // Fraction of the solar disc still clear of the solid limb, in [0, 1].
    //
    // The limb is a straight edge at HorizonDipDegrees (its curvature across half a degree of sun is
    // negligible from 1609 km), so this is the classic circular-segment area: for a chord at signed
    // distance c from a unit disc's centre, the area on the far side is acos(c) - c * sqrt(1 - c^2)
    // over a total of pi. c = -1 (edge below the disc) gives 1; c = +1 gives 0. This is what turns
    // the last 0.53 degrees into a genuine cutoff rather than an asymptote.
    public static float SolarDiscVisibleFraction(float sunElevationDegrees)
    {
        float depressionDegrees = -sunElevationDegrees;
        float c = Clamp((depressionDegrees - HorizonDipDegrees) / SolarAngularRadiusDegrees, -1f, 1f);
        return (MathF.Acos(c) - c * MathF.Sqrt(Max(0f, 1f - c * c))) / MathF.PI;
    }

    // ------------------------------------------------------------------
    // Effect-level entry points — all four carry the §18 gate
    // ------------------------------------------------------------------
    //
    // THE GATE, per Vacuum.cs's convention: `bool inVacuum` last, required, never defaulted, branched
    // on before any of the model runs. Note this subsystem inverts §18a's polarity — there the vacuum
    // branch is the suppression and the body is the real effect; here the vacuum branch IS the effect
    // and the sea-level branch is the inert one. The convention still holds exactly as written, and
    // for the same reason: a call site cannot silently opt out, and every test pins the pair.

    // How much of the sun's light is reaching the platform: 1 in full sun, 0 once the disc is gone.
    //
    // Two independent factors, because they are two different physical things — how much of the sun
    // is still visible (geometry) times how much of its light survives the air it is shining through
    // (extinction). The spectrum is collapsed to one brightness via the same Rec. 601 luma weights
    // §12 uses, so "how bright" and "what colour" come out of one model rather than two that could
    // drift apart.
    //
    // Sea level: 1 at every elevation. Not because a surface map has no extinction, but because it
    // has an entirely different one that §2/§7/§8 already own end to end — this function has nothing
    // to say about a sky seen from inside the atmosphere, and returning 1 makes it a strict no-op
    // rather than a second opinion.
    //
    // In vacuum, above the band this is exactly 1: nothing dims an orbital sun, so the platform runs
    // full daylight right up to the moment the planet starts to get in the way. Consequence 1 needs
    // no code beyond that early return.
    public static float SunlightFraction(float sunElevationDegrees, bool inVacuum)
    {
        if (!inVacuum)
            return 1f;

        if (sunElevationDegrees >= BandTopElevationDegrees)
            return 1f;
        if (sunElevationDegrees <= BandBottomElevationDegrees)
            return 0f;

        Rgb transmission = LimbTransmission(sunElevationDegrees);
        float luminous = BloodMoonMath.Luma(transmission.R, transmission.G, transmission.B);
        return luminous * SolarDiscVisibleFraction(sunElevationDegrees);
    }

    // The band's colour as a DIRECTION in colour space — the transmission triple renormalised so its
    // brightest channel is 1. Brightness is SunlightFraction's job; this carries only hue and
    // saturation, the same split BloodMoonMath.CrimsonTint uses so that a tint can never double as a
    // dimmer.
    //
    // Near-white at the top of the band (1, 0.997, 0.988) and effectively monochromatic red at the
    // bottom (1, 0.024, 4e-8). The near-white top is what lets this compose without a seam: §18a pins
    // the vacuum sky colour temperature flat at ZenithKelvin (5772 K, the unreddened photospheric
    // anchor) and zeroes its tint strength, so at the instant the band opens we are departing from a
    // sky that is exactly the sun's own unreddened colour — and departing from it by nothing.
    //
    // Sea level: white, i.e. no rotation of the sky's hue at all. Paired with a TintStrength of 0
    // this is doubly inert, which is deliberate — either one alone already reduces to a no-op, so a
    // future caller reaching for only one of them still cannot leak an orbital colour onto the
    // ground.
    public static Rgb LimbTint(float sunElevationDegrees, bool inVacuum)
    {
        if (!inVacuum)
            return new Rgb(1f, 1f, 1f);

        Rgb transmission = LimbTransmission(sunElevationDegrees);
        float brightest = Max(transmission.R, Max(transmission.G, transmission.B));

        // Defensive, and unreachable with the shipped anchors: red transmission bottoms out at 0.024
        // rather than at 0, so `brightest` is always positive. A deeper shell or a smaller scale
        // height would underflow every channel, and the limit of the normalised triple as the path
        // deepens is pure red — so that is what it returns. The alternative (0, 0, 0) would read as a
        // "black tint" and dim rather than colour.
        if (brightest <= 0f)
            return new Rgb(1f, 0f, 0f);

        return new Rgb(transmission.R / brightest, transmission.G / brightest, transmission.B / brightest);
    }

    // How strongly to apply that tint, in [0, 1]. A SPIKE, not a ramp: 0 at the top of the band,
    // peaking at 0.789 around -13.95 degrees, back to 0 at the bottom.
    //
    // Two factors, and the second one is the important one:
    //
    //   How far the spectrum has shifted — 1 - (normalised green transmission), so the tint asserts
    //   itself exactly as fast as the air actually removes the middle of the spectrum. Green is the
    //   channel to key on because it is the luma-dominant one, so this tracks the visible change
    //   rather than the blue channel's much earlier and much less visible collapse.
    //
    //   How much of the reddened source is still there to do the colouring — SolarDiscVisibleFraction.
    //   Without it the strength would still be ~0.98 the moment the disc vanished behind the solid
    //   limb, and since colour and glow are separate fields, the tint would then sit on the sky for
    //   the whole of orbital night: a blood-red darkness held over from a sun that had already set.
    //   With it, the tint reaches zero exactly where the sunlight does, and what colour the night
    //   actually is stays entirely §18b's planetshine question.
    //
    // This is NOT double-counting the disc against SunlightFraction. That one answers "how much light
    // is there"; this one answers "how much of the light there is, is limb light" — the mixing weight
    // against the floor. They share a factor because they share a cause.
    //
    // The spike shape is also the honest one. The phenomenon is a flash: the red band appears, deepens
    // as the path lengthens, and is then cut off mid-deepening by the solid planet. A monotone ramp to
    // 1 would describe a sunset that fades, which is the sea-level behaviour this subsystem exists to
    // replace.
    //
    // Sea level: 0. Ground dusk is §2's, and it is a different colour arriving by a different path.
    public static float TintStrength(float sunElevationDegrees, bool inVacuum)
    {
        if (!inVacuum)
            return 0f;

        if (sunElevationDegrees >= BandTopElevationDegrees)
            return 0f;
        if (sunElevationDegrees <= BandBottomElevationDegrees)
            return 0f;

        float spectralShift = Clamp01(1f - LimbTint(sunElevationDegrees, inVacuum).G);
        return spectralShift * SolarDiscVisibleFraction(sunElevationDegrees);
    }

    // The composed sky glow for a vacuum map: consequence 1 (a sun that stays up ~14 degrees longer)
    // and consequences 2-3 (the short red ramp) in one value.
    //
    // WHY THIS BORROWS §14's CURVE. The issue calls this "a curve swap in the sun-clock ramp we
    // already own", and that is literally what the first line is. SunClockMath.GlowFromElevation is
    // the mod's one calibrated statement of "how bright is a sky with the sun this high" — three
    // anchors fitted against vanilla's own day length (DESIGN.md §14). Evaluating it at
    // `elevation + HorizonDipDegrees` re-references it from the ground's horizon to the PLATFORM's
    // horizon, and consequence 1 is then the entire content of that one addition. Nothing new is
    // introduced and no second brightness model appears. Note this borrows the CURVE only, not §14's
    // mode: the locked/realistic switch decides whose clock the sun runs on, and is orthogonal to
    // which horizon a vacuum map measures its own sun against.
    //
    // Illumination still falls off as the sun nears the platform's own horizon even though there is
    // no air to attenuate it, and that is correct rather than an oversight: the map is rendered as a
    // deck seen from above, and a grazing sun lights a deck weakly for purely projective reasons.
    //
    // planetshineFloor is INJECTED, not defined here. It belongs to the vacuum night-light budget
    // (§18b) — airglow to zero, starlight unextinguished, planetshine standing in for the moon as the
    // dominant reflector. This subsystem's job ends at "the sun is gone"; what is left when it is gone
    // is somebody else's number, and taking it as a parameter is what keeps the two models from
    // redefining each other's constants. The adapter supplies it from NightRadiance.FloorGlowFor.
    //
    // max() rather than a blend, for the same reason NightRadianceMath.ApplyNightFloor uses one: a
    // floor is a floor. While any sunlight survives it dominates by orders of magnitude, so the
    // handover happens on its own wherever the exponential crosses the floor — moving the floor moves
    // the crossing, and nothing here has to know where that is. That is worth more than it looks,
    // because §18b's floor is moon-dependent — 0.0317 on a new moon and materially higher under a full
    // one, since moonlight is a term in it — so the handover elevation
    // slides up and down the band with the lunar cycle without a single constant here changing, and
    // under a bright enough moon the last of the limb light is legitimately outshone before it reaches
    // its reddest.
    //
    // Sea level: seaLevelGlow, untouched. Vanilla plus §7's night floor own the surface sky and this
    // must not add a second opinion on top of them.
    public static float VacuumSkyGlow(
        float sunElevationDegrees, float seaLevelGlow, float planetshineFloor, bool inVacuum)
    {
        if (!inVacuum)
            return seaLevelGlow;

        float platformElevation = sunElevationDegrees + HorizonDipDegrees;
        float unobstructed = SunClockMath.GlowFromElevation(platformElevation);
        return Max(unobstructed * SunlightFraction(sunElevationDegrees, inVacuum), planetshineFloor);
    }

    private static float ToRadians(float degrees) => degrees * MathF.PI / 180f;
    private static float ToDegrees(float radians) => radians * 180f / MathF.PI;
    private static float Max(float a, float b) => a > b ? a : b;
    private static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
    private static float Clamp01(float v) => Clamp(v, 0f, 1f);
}
