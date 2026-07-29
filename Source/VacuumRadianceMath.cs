using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file (System only), same discipline
// as Formulas.cs, IlluminanceMath.cs and NightRadianceMath.cs. Linked into both the shipped mod
// (net481) and the test project (net8.0) by a single <Compile Include>, so the model that runs in
// RimWorld is the model under test.
//
// This is the pure core of subsystem 18b (DESIGN.md §18b): what the night-side light budget looks
// like on an Odyssey vacuum map. §7 sums starlight + airglow + moonlight; in orbit all three terms
// move, and — the part that is easy to get backwards — one of them moves UP.
//
//   airglow   -> 0        the emission layer is at ~90 km and the platform is at 200 km, so we are
//                         ABOVE it. From up here it is a band on the limb below, not ambient light
//                         landing on the deck.
//   starlight -> BRIGHTER there is no atmosphere left to extinguish it. The sea-level floor is an
//                         already-extinguished number; dividing the extinction back out is the whole
//                         of the vacuum starlight term.
//   moonlight -> the dominant reflected source is no longer the moon but the lit planet filling most
//                         of the sky below. That is planetshine, and see below for why it turns out
//                         to contribute nothing at all in the one regime this file exists to answer.
//
// THE LOAD-BEARING GEOMETRY (epic #8). RimWorld's orbits are stationary: PlanetLayer.LongLatOf
// derives lat/long from a static tile centre and nothing anywhere gives an orbit tile a period. A
// platform therefore hangs permanently over one lat/long and inherits that tile's day/night cycle
// exactly. So a platform in the planet's shadow is directly above the planet's OWN night side, which
// means planetshine is at its minimum precisely when fill light would matter. Orbital night is the
// darkest state this mod can produce, and it falls out of the geometry rather than being dialled in.
public static class VacuumRadianceMath
{
    // --- Named anchors ---
    //
    // These are the one-time physical anchors §18 is allowed to invent, in the sense the epic uses:
    // RimWorld has no planet radius and no orbital altitude beyond a display string, so a vacuum
    // model has to name the numbers it derives from rather than pretend they were read off a def.
    // #32 (limb refraction) needs the same two and should share these rather than pick its own.

    // Planet radius, km. Earth's mean radius. RimWorld gives no radius anywhere; this is the anchor.
    public const float PlanetRadiusKm = 6371f;

    // Orbit altitude, km. This one IS from the game: PlanetLayerDef for `Orbit` carries
    // elevationString "200km". It is a display string rather than a number the sim uses, but it is
    // the game stating its own intent, so we honour it instead of choosing an altitude.
    public const float OrbitAltitudeKm = 200f;

    // Bond albedo of the planet below. 0.30 is Earth's. Used only by the planetshine term.
    public const float PlanetAlbedo = 0.30f;

    // Sea-level clear-sky atmospheric extinction, magnitudes per airmass, in the visual band.
    // Published values run ~0.20 at a good high-altitude site to ~0.30 at sea level (Rayleigh
    // scattering ~0.15, aerosol ~0.10, ozone ~0.02); 0.28 is the usual sea-level clear figure.
    //
    // The result is not sensitive to the choice: over the whole 0.20-0.30 range the hemispheric
    // transmittance below runs 0.72 to 0.62, i.e. a vacuum starlight gain of 1.38x to 1.61x. Every
    // claim this file makes survives anywhere in that band, which is why a single figure is honest
    // rather than a knob. VacuumRadianceMathTests pins that insensitivity directly.
    public const float SeaLevelExtinctionMagPerAirmass = 0.28f;

    // --- Starlight: the term that goes UP ---

    // The fraction of the star field's illuminance that survives the atmosphere and reaches sea
    // level, for a horizontal surface under the whole sky.
    //
    // WHY AN INTEGRAL AND NOT sec(z). Starlight is not a point source: it arrives from the entire
    // hemisphere at once, and each direction is extinguished by its own airmass while contributing
    // by its own cosine projection. So the number that matters is the projection-weighted mean
    // transmittance over the hemisphere, not the zenith transmittance:
    //
    //     T = 2 * integral(0..1) 10^(-0.4 k / u) * u du,      u = cos(zenith angle)
    //
    // (the 2 normalises the same integral with no extinction, which is 1/2). At k = 0.28 that is
    // 0.641 — noticeably lower than the zenith-only 0.773, because the low-elevation sky is much
    // more heavily extinguished and there is a great deal of sky down there.
    //
    // Two deliberate simplifications, both stated rather than hidden: airmass is taken as plane-
    // parallel sec(z), which diverges at the horizon but is weighted to nothing by the same cosine;
    // and the light extinction removes is treated as lost rather than partly re-scattered back down,
    // which makes this a slight OVERestimate of the vacuum gain. Both are far inside the spread of
    // the extinction coefficient itself.
    public static readonly float StarlightTransmittance =
        HemisphericTransmittance(SeaLevelExtinctionMagPerAirmass);

    // Vacuum starlight, in glow units, from the sea-level floor §7 already ships.
    //
    // The claim being made here is about the RATIO, not the absolute value. §7's 0.02 is explicitly
    // a look-calibrated floor rather than a photometric one ("chosen for atmosphere/legibility"), so
    // asserting a photometric number in vacuum would be a category error. What we can assert is that
    // removing the atmosphere multiplies whatever that floor represents by 1/T — and applying that
    // ratio preserves the calibration while encoding the physics of the change.
    //
    // This is the term the issue warns is easy to get backwards. It is a DIVISION: the vacuum value
    // sits ABOVE the sea-level one (0.02 -> 0.031). VacuumStarlightIsBrighterThanSeaLevel pins it.
    public static float StarlightGlow(float seaLevelStarlightGlow) =>
        seaLevelStarlightGlow / StarlightTransmittance;

    // --- Airglow: the term that goes to zero ---

    // Airglow in vacuum, in glow units. Zero, unconditionally, and a named constant rather than a
    // bare 0 at the call site so the reason travels with the value.
    //
    // The emission layer sits at ~90 km. A 200 km platform is above it, so what was ambient light
    // from overhead becomes a thin band on the limb below — a thing you would SEE, and #32's job if
    // anyone renders it, but not light falling on the deck. There is no partial case to model: the
    // orbit layer is either at 200 km or the whole altitude model is different.
    public const float AirglowGlow = 0f;

    // --- Planetshine: the term that replaces moonlight as the dominant reflected source ---

    // Half-angle of the patch of ground the platform can see, in degrees of surface arc from the
    // sub-satellite point: acos(R / (R + h)) == 14.17 degrees at 200 km. Everything beyond it is
    // below the platform's horizon.
    //
    // This single number is the entire physical content of being in orbit rather than on the ground,
    // and it is what makes planetshine a real derived quantity rather than a restatement of
    // daylight: the platform sees ground that is up to 14.17 degrees of solar depression AHEAD of
    // its own, so it can still be lit by a sun the platform has long since lost.
    public static readonly float HorizonCapHalfAngleDegrees =
        ToDegrees(MathF.Acos(PlanetRadiusKm / (PlanetRadiusKm + OrbitAltitudeKm)));

    // Fraction of a full hemisphere the planet's disc fills, as seen by a downward-facing surface:
    // sin^2 of the disc's angular radius, which reduces to (R / (R + h))^2 == 0.940. At 200 km the
    // planet is 94% of the sky below you, which is exactly why it outranks the moon as a reflector.
    public static readonly float PlanetDiscFillFactor =
        (PlanetRadiusKm / (PlanetRadiusKm + OrbitAltitudeKm)) *
        (PlanetRadiusKm / (PlanetRadiusKm + OrbitAltitudeKm));

    // Illuminance planetshine puts on a downward-facing surface at the platform, in lux, for a given
    // solar elevation AT THE PLATFORM'S OWN LAT/LONG (which, orbits being stationary, is also the
    // solar elevation at the sub-satellite point).
    //
    // Integrates the visible cap: every ground point inside HorizonCapHalfAngleDegrees is a Lambertian
    // reflector of whatever sunlight reaches it, and its contribution is weighted by the solid angle
    // it subtends at the platform times the cosine of the platform's own view angle. The solar
    // elevation at a ground point displaced by great-circle arc g in bearing phi comes from exact
    // spherical trigonometry:
    //
    //     sin(elev') = sin(elev) cos(g) + cos(elev) sin(g) cos(phi)
    //
    // ONLY THE SOLAR PART OF THE GROUND'S BRIGHTNESS COUNTS. AmbientSkyLux clamps to a 0.001 lux
    // night floor which IS starlight and airglow — the very light the platform already receives
    // directly. Bouncing it off the ground and counting it again as planetshine would double-count
    // the starlight term this file just finished raising, so the night floor is subtracted out and
    // planetshine is sunlight only. That subtraction is what makes planetshine exactly zero once the
    // whole visible cap is past astronomical twilight, which is the design claim, derived rather
    // than asserted.
    public static float PlanetshineLux(float sunElevationDegrees)
    {
        float capRadians = ToRadians(HorizonCapHalfAngleDegrees);
        float sunRadians = ToRadians(sunElevationDegrees);
        float sinSun = MathF.Sin(sunRadians);
        float cosSun = MathF.Cos(sunRadians);

        float sum = 0f;
        float arcStep = capRadians / CapArcSamples;
        float bearingStep = 2f * MathF.PI / CapBearingSamples;

        for (int i = 0; i < CapArcSamples; i++)
        {
            float arc = (i + 0.5f) * arcStep;
            sum += ArcRingContribution(arc, arcStep, bearingStep, sinSun, cosSun);
        }

        // The albedo/pi turns ground illuminance into Lambertian radiance; the R^2 completes the
        // change of variables from surface area to the solid angle the ring subtends.
        return PlanetAlbedo / MathF.PI * PlanetRadiusKm * PlanetRadiusKm * sum;
    }

    // One ring of the visible cap at great-circle arc `arc` from the sub-satellite point, averaged
    // over bearing. Split out rather than nested two loops deep so the geometry (this function) and
    // the quadrature (its caller) can each be read on their own.
    private static float ArcRingContribution(
        float arc, float arcStep, float bearingStep, float sinSun, float cosSun)
    {
        float r = PlanetRadiusKm + OrbitAltitudeKm;
        float cosArc = MathF.Cos(arc);
        float distanceSquared =
            PlanetRadiusKm * PlanetRadiusKm + r * r - 2f * r * PlanetRadiusKm * cosArc;
        float distance = MathF.Sqrt(distanceSquared);

        // cosView: how far off nadir the ring sits, as seen from the platform.
        // cosEmission: how obliquely the platform sees that patch of ground. Goes negative past the
        // horizon, which cannot happen inside the cap — the guard is here so a caller that widens
        // the cap cannot silently start subtracting light.
        float cosView = (r - PlanetRadiusKm * cosArc) / distance;
        float cosEmission = (r * cosArc - PlanetRadiusKm) / distance;
        if (cosEmission <= 0f)
            return 0f;

        float ringWeight = cosView * cosEmission * MathF.Sin(arc) / distanceSquared * arcStep;

        float sum = 0f;
        for (int j = 0; j < CapBearingSamples; j++)
        {
            float bearing = (j + 0.5f) * bearingStep;
            float sinElevation = sinSun * cosArc + cosSun * MathF.Sin(arc) * MathF.Cos(bearing);
            float elevation = ToDegrees(MathF.Asin(Clamp(sinElevation, -1f, 1f)));
            sum += SolarOnlyGroundLux(elevation) * bearingStep;
        }

        return ringWeight * sum;
    }

    // Ground illuminance from the SUN alone: AmbientSkyLux with its starlight/airglow night floor
    // removed, so a ground point past astronomical twilight contributes exactly nothing. See
    // PlanetshineLux for why the floor must not be counted twice.
    private static float SolarOnlyGroundLux(float sunElevationDegrees)
    {
        float total = IlluminanceMath.AmbientSkyLux(sunElevationDegrees);
        float solar = total - IlluminanceMath.NightSkyFloorLux;
        return solar < 0f ? 0f : solar;
    }

    // Quadrature resolution for the cap integral. 90x90 converges to within ~0.03% of a 400x360 grid
    // (checked offline against the same model in Python), and the whole thing is evaluated once at
    // static init for PlanetshineFloorLux plus whatever the tests sweep — never per frame.
    private const int CapArcSamples = 90;
    private const int CapBearingSamples = 90;

    // The most planetshine the platform can ever receive while the §7 night floor fully owns the sky
    // — i.e. evaluated at NightFloorFullElevation (-18 degrees), the top of the full-night band and,
    // because planetshine falls monotonically as the sun sinks further, the supremum over it.
    //
    // It comes out at ~0.00085 lux: about 1/300th of a full moon, and three orders of magnitude
    // below the starlight floor it sits next to. THIS IS THE ANSWER TO THE DESIGN QUESTION, arrived
    // at by computing it rather than by asserting it — see DESIGN.md §18b. Orbital night gets no
    // meaningful fill light from the planet, because at orbital night the planet below is having a
    // night of its own.
    public static readonly float PlanetshineFloorLux =
        PlanetshineLux(NightRadianceMath.NightFloorFullElevation);

    // Converts any reflected-sunlight source from lux into the mod's glow units, using the moon as
    // the calibration.
    //
    // WHY THE MOON AND NOT A LUX->GLOW SCALE. There is no single one: §7's night floor (0.001 lux ->
    // 0.04 glow) and §7's full moon (0.267 lux -> 0.15 glow) imply scales 70x apart, because glow is
    // deliberately compressive rather than photometric. But glow units are SUMMED in §7's model, and
    // a compressive (logarithmic) mapping is not additive, so a log fit would double-count the
    // moment a second source appeared. Anchoring on the moon alone dodges both problems: it is a
    // linear ratio, and it is the RIGHT comparison class — planetshine and moonlight are the same
    // physical thing (sunlight bounced off a nearby rock) so the look-calibration the moon already
    // carries transfers to planetshine by their true photometric ratio and nothing new is invented.
    public static float ReflectedGlow(float lux, float maxMoonlightGlow) =>
        maxMoonlightGlow * lux / IlluminanceMath.FullMoonZenithLux;

    // Planetshine's contribution to the vacuum night floor, in glow units. Scales with the same
    // MaxMoonlightGlow the moon uses, so a player who turns reflected night light down turns both
    // down together.
    public static float PlanetshineFloorGlow(float maxMoonlightGlow) =>
        ReflectedGlow(PlanetshineFloorLux, maxMoonlightGlow);

    // --- The night floor in vacuum ---

    // The vacuum arm of NightRadianceMath.NightFloorGlow. Same shape as §7's sea-level sum — the
    // sources ADD, so darkness stays emergent — with the three §18b substitutions applied:
    // starlight divided by its extinction, airglow gone, planetshine added alongside a moon term
    // that survives unchanged (see DESIGN.md §18b for why the moon is not replaced).
    public static float NightFloorGlow(
        float seaLevelStarlightGlow, float moonlightGlow, float maxMoonlightGlow) =>
        Clamp(
            StarlightGlow(seaLevelStarlightGlow)
            + AirglowGlow
            + moonlightGlow
            + PlanetshineFloorGlow(maxMoonlightGlow),
            0f, 1f);

    // 2 * integral(0..1) 10^(-0.4 k / u) * u du by midpoint rule. The integrand is smooth on (0, 1]
    // and vanishes rapidly as u -> 0 (the exponent goes to minus infinity), so a midpoint rule never
    // evaluates the singular endpoint and converges quickly; 2000 samples is well past the point
    // where more digits stop moving.
    private static float HemisphericTransmittance(float extinctionMagPerAirmass)
    {
        const int samples = 2000;
        float sum = 0f;
        for (int i = 0; i < samples; i++)
        {
            float u = (i + 0.5f) / samples;
            sum += MathF.Pow(10f, -0.4f * extinctionMagPerAirmass / u) * u;
        }

        return 2f * sum / samples;
    }

    private static float ToRadians(float degrees) => degrees * MathF.PI / 180f;
    private static float ToDegrees(float radians) => radians * 180f / MathF.PI;
    private static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
}
