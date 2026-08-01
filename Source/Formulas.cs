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
    // Matches RimWorld.GenDate.DaysPerYear, not an arbitrary guess — reused here so our seasonal
    // cycle stays in lockstep with vanilla's if that constant ever changes (covered by an
    // ApiCompatibilityTests assertion on the constant's value, since a silent change here would
    // desync the seasons without throwing anything).
    public const float DaysPerYear = 60f;

    // Below this latitude the twilight-color effect fades to (near) nothing — real-world twilight
    // near the equator is short and roughly latitude-independent, so vanilla's unmodified behavior
    // is already a reasonable approximation there.
    public const float FullStrengthLatitude = 60f;

    // Twilight band center, in GenCelestial.CurCelestialSunGlow units — sits inside vanilla's
    // dusk (0.6) / nightEdge (0.1) thresholds.
    public const float TwilightPeakGlow = 0.35f;

    public static float LatitudeStrength(float latitude) =>
        InverseLerpClamped(0f, FullStrengthLatitude, Abs(latitude));

    // Same one-line sinusoidal day-of-year term already present in vanilla's
    // GenCelestial.SunPositionUnmodified — reused for consistency, not copied as a substantial
    // expression. Returns which pole the sun currently favors, in [-1, 1]; SolarDeclinationDegrees
    // scales this by the real axial tilt to get an actual declination angle.
    public static float DeclinationSign(float dayOfYear) =>
        -MathF.Cos(dayOfYear / DaysPerYear * MathF.PI * 2f);

    public static float TwilightBandWidth(float strength) => Lerp(0.12f, 0.35f, strength);

    // Peak warm-nudge height at a given latitude strength. Shared by both the above-horizon
    // glow-keyed band (TwilightFactor) and the below-horizon civil-twilight persistence
    // (TwilightWarmthFactor) so the two are on the same intensity footing and meet smoothly at the
    // horizon. Nonzero floor at strength == 0 (== 0.15f) — see TwilightFactor's note.
    public static float TwilightPeakHeight(float strength) => Lerp(0.15f, 0.55f, strength);

    // Peaks at sunGlow == TwilightPeakGlow, falls off linearly to 0 across bandWidth on either
    // side, then scales the peak height by latitude strength. Note this has a nonzero floor even
    // at strength == 0 (TwilightPeakHeight(0) == 0.15f, not 0f) — a small warm nudge survives
    // exactly at golden hour even on the equator, by design (real equatorial sunsets are a little
    // warm too, just not as extended as high-latitude ones). Callers that want a hard zero at the
    // equator should gate on strength <= 0 themselves, as Patch_TwilightColor does.
    //
    // inVacuum (DESIGN.md §18, the shared gate in Vacuum.cs): twilight IS scattering — sunlight
    // lighting up air that the ground can no longer see the sun through. With no air there is no
    // twilight at any latitude, at any sun height, so the whole factor is zero rather than a
    // narrowed band. The band-width/peak-height helpers above stay ungated: they are shape
    // parameters, and gating them would leave a caller able to get a nonzero shape out of a vacuum.
    public static float TwilightFactor(float sunGlow, float strength, bool inVacuum)
    {
        if (inVacuum)
            return 0f;

        float bandWidth = TwilightBandWidth(strength);
        return Clamp01(1f - Abs(sunGlow - TwilightPeakGlow) / bandWidth) * TwilightPeakHeight(strength);
    }

    // --- Civil-twilight persistence (linger after geometric sunset) ---
    //
    // Vanilla's CurCelestialSunGlow is Clamp01(InverseLerp(0, 0.7, sin(elevation))), so it pins to
    // exactly 0 the instant the sun's geometric elevation crosses the horizon and stays 0 for the
    // whole night — it carries no information about how far below the horizon the sun is. Because
    // TwilightFactor above is keyed purely on that glow value, the warm dusk tint necessarily
    // collapsed to nothing at the geometric-sunset instant, cutting the sky's colour to full night
    // abruptly. In reality scattered sunlight keeps the sky usefully lit and warm through *civil
    // twilight* — the sun from 0 down to ~-6 degrees below the horizon, roughly 20-30 min after
    // sunset at mid-latitudes — so the warmth should linger and fade, not snap off.
    //
    // We can't recover "how far below the horizon" from glow, but our own solar-position simulator
    // computes true elevation directly (SolarElevationDegrees / SolarPosition.ElevationForMap), so
    // this term is keyed on elevation instead. Deliberately colour-only: it feeds the same warm
    // colour nudge in Patch_TwilightColor and never touches SkyManager's glow value — night stays
    // exactly as dark as vanilla makes it (which keeps glow-reading mods like Dub's Skylights
    // seeing an unmodified brightness), we only warm the hue of that darkening sky.

    // End of civil twilight: sun 6 degrees below the horizon (standard astronomical definition).
    // Past this, the sky is dark enough that no warm lingering light remains.
    public const float CivilTwilightEndDegrees = -6f;

    // Where the lingering warmth peaks, a couple degrees below the horizon — the deep-orange/red
    // part of dusk sits just under the horizon, not at the exact crossing. Between 0 and this the
    // warmth ramps up (it is 0 at the horizon so it meets the fading glow-keyed band there without
    // a jump); between this and CivilTwilightEndDegrees it fades back to 0 into full night.
    public const float CivilTwilightPeakDegrees = -2f;

    // A 0..1 triangular pulse over the civil-twilight band: 0 at and above the geometric horizon
    // (elevation >= 0), rising to 1 at CivilTwilightPeakDegrees, falling to 0 at
    // CivilTwilightEndDegrees, and 0 again below that. Written as the clamped min of a rising and a
    // falling ramp so there is no branching — above the horizon the rising ramp goes negative and
    // clamps out; below the civil-twilight floor the falling ramp goes negative and clamps out.
    public static float CivilTwilightPersistence(float elevationDegrees)
    {
        float rising = elevationDegrees / CivilTwilightPeakDegrees; // 0 at horizon, 1 at peak, >1 below peak
        float falling = (elevationDegrees - CivilTwilightEndDegrees)
            / (CivilTwilightPeakDegrees - CivilTwilightEndDegrees); // 0 at floor, 1 at peak, >1 above peak
        return Clamp01(Min(rising, falling));
    }

    // The full warm-tint factor Patch_TwilightColor applies: the larger of the above-horizon
    // glow-keyed band and the below-horizon civil-twilight persistence, both scaled to the same
    // latitude-dependent peak height so they meet smoothly at the horizon (where both are ~0).
    // max (not sum) avoids double-warming in any overlap and keeps the factor bounded by a single
    // peak height. All colour-only — the caller only ever writes SkyTarget colours, never glow.
    //
    // inVacuum zeroes BOTH pieces, not just the glow band. The civil-twilight persistence term is if
    // anything the more atmospheric of the two: it exists precisely because scattered light keeps
    // lighting the sky after the sun is geometrically down, which is the one thing a vacuum cannot
    // do. Shipping this leaves a hard colour step at the orbital terminator; the deep-red limb
    // refraction that physically replaces it is #32's job, not a reason to soften this to a ramp.
    public static float TwilightWarmthFactor(float sunGlow, float elevationDegrees, float strength, bool inVacuum)
    {
        if (inVacuum)
            return 0f;

        float glowBand = TwilightFactor(sunGlow, strength, inVacuum);
        float persistence = CivilTwilightPersistence(elevationDegrees) * TwilightPeakHeight(strength);
        return Max(glowBand, persistence);
    }

    // --- Solar-position shadow simulator ---
    //
    // Vanilla's own shadow model (GenCelestial.GetLightSourceInfo, LightType.Shadow) isn't derived
    // from real sun position at all: length comes from a plain linear lerp across the day
    // fraction, and CurCelestialSunGlow — the only public signal for "how far below the horizon is
    // the sun" — clamps to exactly 0 the moment the sun dips under, throwing away how far below it
    // actually is. That's fine for ambient glow, but it means vanilla has no way to tell "just set"
    // from "polar midnight", and its near-pole behavior (SunOffsetFractionFromLatitudeCurve) is a
    // two-point curve tuned only for 70-75 degrees latitude, not a real declination model.
    //
    // This replaces that with standard (simplified — no atmospheric refraction, no equation of
    // time) solar-position trigonometry: real axial tilt, a proper latitude/declination/hour-angle
    // elevation formula, and shadow length from cot(elevation) so shadows naturally get dramatic
    // near the horizon and vanish outright once the sun truly sets — no vanilla curve-patching
    // required. At the poles this falls out for free: elevation stays constant (== declination)
    // across the whole day, correctly producing continuous midnight sun in summer and total polar
    // night in winter, because the hour-angle term's coefficient (cos(latitude)) goes to zero there.

    // Earth's real axial tilt, not vanilla's fudge — reused everywhere so the "north pole in
    // summer" case is a real geometric consequence, not a curve that only kicks in past 70 degrees.
    // This is the obliquity we assume when nothing else on the mod list has an opinion; a world-gen
    // mod that lets the player pick one feeds it in through SolarDeclinationDegrees' overload below.
    public const float AxialTiltDegrees = 23.44f;

    // An obliquity of 90 degrees lies the planet on its side: the sun passes overhead at the pole at
    // the solstice and the whole hemisphere is in day or night for half the year. Past that the
    // geometry is not "more extreme", it is a planet spinning retrograde, which the -cos phase term
    // below cannot express — so this is the ceiling rather than a taste limit, and it happens to be
    // the top of Planetsmith's own 0-90 slider too.
    public const float MaxObliquityDegrees = 90f;

    // Below this, cot(elevation) blows up toward infinity — clamp the input angle before dividing,
    // not the output length, so the clamp reads as "treat near-zero elevation as this floor" rather
    // than a length-space magic number.
    private const float MinElevationForLengthDegrees = 0.5f;

    // Matches GenCelestial.ShadowMaxLengthDay/Night so the shader/mesh scale downstream (tuned for
    // vanilla's own -15..15 vector range) doesn't need retuning.
    public const float MaxShadowLength = 15f;

    // Standard atmospheric refraction at the horizon: the sun is still visible (and still casts a
    // shadow) for a few minutes after its true geometric elevation crosses 0, because the
    // atmosphere bends light near the horizon. Real sunrise/sunset happens at geometric elevation
    // ~-0.83 degrees, not 0 — gating shadow existence on elevation <= 0 (the original version of
    // this simulator) cut shadows off a little early. This is the one deliberately "real-world"
    // fudge in an otherwise simplified model (no equation of time, no per-altitude refraction curve
    // beyond this single constant).
    public const float AtmosphericRefractionDegrees = -0.83f;

    // How many degrees above the refraction-adjusted horizon (AtmosphericRefractionDegrees) it
    // takes for the shadow to reach full opacity. A hard cut at the gate (instead of this short
    // ramp) would pop a max-length shadow in or out over a single frame right at sunrise/sunset.
    private const float ShadowIntensityRampDegrees = 3f;

    public readonly struct ShadowVector
    {
        public readonly float X; // world east-west
        public readonly float Y; // world north-south

        public ShadowVector(float x, float y)
        {
            X = x;
            Y = y;
        }
    }

    public static float SolarDeclinationDegrees(float dayOfYear) =>
        SolarDeclinationDegrees(dayOfYear, AxialTiltDegrees);

    // The same declination, on a planet whose obliquity someone else chose.
    //
    // Note what this overload does NOT take: a seasonal phase. The tilt scales the swing, while
    // DeclinationSign decides which pole the sun favours on a given day, and only the tilt is a
    // property of the planet — the phase is a property of where the year is reckoned from, which
    // stays ours. That split is exactly why Planetsmith can be consumed through this overload and
    // Realistic Axial Tilt cannot: RAT reckons the year from a different point (sin where we use
    // -cos, a quarter-year apart) and so has to supply the whole declination, not just its scale.
    // See PlanetsmithCompat for the long version.
    public static float SolarDeclinationDegrees(float dayOfYear, float obliquityDegrees) =>
        SanitizeObliquityDegrees(obliquityDegrees) * DeclinationSign(dayOfYear);

    // Bound-check an obliquity that came from another mod's settings before we build a sky out of it.
    //
    // Not defensive habit: the value arrives from a slider we do not own, through reflection that
    // cannot type-check it, out of a save file a player may have hand-edited or that an older
    // version of that mod wrote. A NaN here does not throw — it propagates silently through every
    // trig call downstream and lands as an invisible sun and shadowless noon, which reads to a
    // player as our bug. Falling back to Earth's tilt costs one wrong-looking planet; NaN costs the
    // whole sky.
    public static float SanitizeObliquityDegrees(float obliquityDegrees) =>
        float.IsNaN(obliquityDegrees)
            ? AxialTiltDegrees
            : Clamp(obliquityDegrees, 0f, MaxObliquityDegrees);

    // 0 at solar noon (dayPercent == 0.5), negative in the morning, positive in the afternoon —
    // same convention vanilla's own SunPositionUnmodified uses for its day/night rotation.
    public static float HourAngleDegrees(float dayPercent) => (dayPercent - 0.5f) * 360f;

    // Standard solar-elevation formula: sin(elevation) = sin(lat)*sin(decl) + cos(lat)*cos(decl)*cos(hourAngle).
    public static float SolarElevationDegrees(float latitudeDegrees, float declinationDegrees, float dayPercent)
    {
        float latRad = ToRadians(latitudeDegrees);
        float declRad = ToRadians(declinationDegrees);
        float hourAngleRad = ToRadians(HourAngleDegrees(dayPercent));

        float sinElevation = MathF.Sin(latRad) * MathF.Sin(declRad)
            + MathF.Cos(latRad) * MathF.Cos(declRad) * MathF.Cos(hourAngleRad);
        return ToDegrees(MathF.Asin(Clamp(sinElevation, -1f, 1f)));
    }

    // Standard solar-azimuth formula (from north, clockwise). Degenerate exactly at the poles
    // (cos(latitude) == 0, every azimuth is "away from the pole") and exactly when the sun sits at
    // zenith/nadir (cos(elevation) == 0, azimuth is undefined) — both fall back to tracking the
    // hour angle directly, which still sweeps smoothly through the day instead of holding still or
    // dividing by zero.
    //
    // HANDEDNESS (settled — this was an open question until it showed up on screen): +X is compass
    // east, and the sun must rise there. The leading minus on sinAz is what makes that true and is
    // NOT optional: HourAngleDegrees is negative before noon, so without it sin(H) < 0 drags the
    // morning azimuth to 270 (west) and the sun runs backwards across the sky all day. We shipped
    // that mirrored for a while — nothing downstream flips the axis back (ShadowVectorFromSunPosition
    // negates both components equally, and Patch_ShadowDirection passes X straight to info.vector.x),
    // so a sign here is a sign on screen. SolarAzimuth_SunRisesInTheEast pins it.
    public static float SolarAzimuthDegrees(float latitudeDegrees, float declinationDegrees, float elevationDegrees, float dayPercent)
    {
        float latRad = ToRadians(latitudeDegrees);
        float declRad = ToRadians(declinationDegrees);
        float elRad = ToRadians(elevationDegrees);
        float hourAngleRad = ToRadians(HourAngleDegrees(dayPercent));

        float cosLat = MathF.Cos(latRad);
        float cosEl = MathF.Cos(elRad);
        // Negated for the same reason sinAz is: the fallback has to sweep the same direction as the
        // formula it stands in for, or the shadow reverses course at exactly the latitudes where the
        // sun circles the horizon all day and the sweep is the only motion there is to see.
        if (Abs(cosLat) < 0.0001f || Abs(cosEl) < 0.0001f)
            return NormalizeDegrees(-HourAngleDegrees(dayPercent));

        float sinAz = -MathF.Cos(declRad) * MathF.Sin(hourAngleRad) / cosEl;
        float cosAz = (MathF.Sin(declRad) - MathF.Sin(latRad) * MathF.Sin(elRad)) / (cosLat * cosEl);
        return ToDegrees(MathF.Atan2(sinAz, cosAz));
    }

    // cot(elevation), clamped both against the near-zero blow-up and against vanilla's own max
    // shadow length. Callers are expected to gate on elevation > 0 (sun above horizon) themselves —
    // this returns MaxShadowLength, not 0, for elevation <= 0, since "no shadow" there is an
    // existence question (see ShadowIntensityFromElevation), not a length one.
    public static float ShadowLengthFromElevation(float elevationDegrees)
    {
        float clampedElevation = elevationDegrees < MinElevationForLengthDegrees ? MinElevationForLengthDegrees : elevationDegrees;
        float length = MathF.Cos(ToRadians(clampedElevation)) / MathF.Sin(ToRadians(clampedElevation));
        return Clamp(length, 0f, MaxShadowLength);
    }

    // 0 at or below AtmosphericRefractionDegrees, ramping to 1 over ShadowIntensityRampDegrees above
    // that. Unlike vanilla's CurShadowStrength (which dips to 0 exactly at the day/night threshold
    // and ramps back up through the night), this stays at full strength for essentially the whole
    // time the sun is up — the dramatic-at-sunset look now comes entirely from
    // ShadowLengthFromElevation growing, not from intensity fading in and out. The ramp starts
    // below true 0 rather than at it so the last sliver of refraction-lingering light (see
    // AtmosphericRefractionDegrees) still casts a faint shadow instead of popping straight to zero.
    public static float ShadowIntensityFromElevation(float elevationDegrees) =>
        InverseLerpClamped(AtmosphericRefractionDegrees, AtmosphericRefractionDegrees + ShadowIntensityRampDegrees, elevationDegrees);

    // Sun azimuth is measured clockwise from north (0 = N, 90 = E), so its horizontal direction is
    // (sin(az), cos(az)) in (east, north); the shadow falls directly away from the sun.
    public static ShadowVector ShadowVectorFromSunPosition(float elevationDegrees, float azimuthDegrees)
    {
        float length = ShadowLengthFromElevation(elevationDegrees);
        float azRad = ToRadians(azimuthDegrees);
        float sunEast = MathF.Sin(azRad);
        float sunNorth = MathF.Cos(azRad);
        return new ShadowVector(-sunEast * length, -sunNorth * length);
    }

    // --- The two user-facing shadow knobs (the "Look" sliders), as pure functions ---
    //
    // Both are applied at the two patch sites that own a shadow (direction and strength) rather than
    // inside the geometry above, so the physical model stays the physical model and the knobs stay a
    // deliberate, visible multiply on top of it.

    // Scales a shadow vector's LENGTH by the user's slider, re-clamping to MaxShadowLength.
    //
    // The clamp is what keeps a >1 scale safe: vanilla's shadow mesh/shader is tuned for a -15..15
    // vector range (ShadowMaxLengthDay/Night), so scaling an already-clamped near-horizon shadow by
    // 1.4 would push the geometry outside the range vanilla's own renderer expects. Scaling first and
    // re-clamping means the knob lengthens mid-sky shadows — where the visible drama actually is —
    // and leaves the near-horizon ones pinned at vanilla's own maximum.
    public static ShadowVector ScaleShadowVector(float x, float y, float lengthScale)
    {
        float magnitude = MathF.Sqrt(x * x + y * y);
        if (magnitude <= 0.0001f || lengthScale <= 0f)
            return new ShadowVector(0f, 0f);

        float scaled = Clamp(magnitude * lengthScale, 0f, MaxShadowLength);
        float factor = scaled / magnitude;
        return new ShadowVector(x * factor, y * factor);
    }

    // Scales a shadow's opacity by the user's slider. Clamped at both ends: the slider is a 0..1
    // control, and a shadow alpha outside [0,1] is meaningless to the shader either way.
    public static float ScaleShadowStrength(float intensity, float strengthScale) =>
        Clamp01(intensity) * Clamp01(strengthScale);

    private static float ToRadians(float degrees) => degrees * MathF.PI / 180f;
    private static float ToDegrees(float radians) => radians * 180f / MathF.PI;

    private static float NormalizeDegrees(float degrees)
    {
        float wrapped = degrees % 360f;
        return wrapped < 0f ? wrapped + 360f : wrapped;
    }

    // The byte the mesh stores as a caster's vertex alpha, which the "Custom/Sun shadow" vertex
    // program multiplies the whole-map extrusion vector by (see DESIGN.md section 3 for the scan of
    // the shipped shader that established that). So this is purely "how tall is this caster".
    //
    // It exists rather than inlining vanilla's own `(byte)(255f * height)` because that cast is
    // UNCHECKED and staticSunShadowHeight is an unvalidated ThingDef float. Vanilla's own defs top
    // out at 1.0, but a modded def declaring 1.2 wraps 306 to 50 and collapses that building's
    // shadow into a stub; NaN is undefined outright. Patch_ShadowMeshPerimeter replaces the whole of
    // Regenerate() for every mod in the load order, so that clamp is ours to provide.
    //
    // Rounds rather than truncates, which halves the worst-case quantization error to half a level.
    // On quantization generally — the thing issue #11 asked us to check rather than assume — one
    // alpha level is 1/255 of the GLOBAL extrusion vector, so its error in world units is
    // |_CastVect| / 255, independent of caster height: short casters do not band worse than tall
    // ones, they simply have proportionally fewer levels to spend on a proportionally shorter
    // shadow. At Formulas.MaxShadowLength (15 cells) that is 0.059 cells, roughly one screen pixel
    // at default zoom.
    public static byte ShadowCasterAlphaByte(float casterHeight)
    {
        float alpha = 255f * casterHeight;
        if (!(alpha > 0f))
            return 0;
        if (alpha >= 254.5f)
            return 255;
        return (byte)(alpha + 0.5f);
    }

    private static float Abs(float v) => v < 0f ? -v : v;
    private static float Min(float a, float b) => a < b ? a : b;
    private static float Max(float a, float b) => a > b ? a : b;
    private static float Clamp01(float v) => Clamp(v, 0f, 1f);
    private static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float InverseLerpClamped(float a, float b, float v) => Clamp01((v - a) / (b - a));

    // Matches UnityEngine.Mathf.Sign's convention: exactly 0 returns +1, not 0. Kept explicit here
    // rather than relying on System.Math.Sign (which returns 0 for 0) so this function's behavior
    // doesn't silently depend on which sign convention some future edit assumes.
    private static float Sign(float v) => v < 0f ? -1f : 1f;
}
