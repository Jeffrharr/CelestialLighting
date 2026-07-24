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
    public static float TwilightFactor(float sunGlow, float strength)
    {
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
    public static float TwilightWarmthFactor(float sunGlow, float elevationDegrees, float strength)
    {
        float glowBand = TwilightFactor(sunGlow, strength);
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
    public const float AxialTiltDegrees = 23.44f;

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

    public static float SolarDeclinationDegrees(float dayOfYear) => AxialTiltDegrees * DeclinationSign(dayOfYear);

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
    // NOTE: which literal world axis (+X vs -X) this maps to "compass east" hasn't been confirmed
    // against RimWorld's actual world-space convention from decompiled code alone — verify in-game
    // that shadows sweep the expected way (e.g. morning shadows pointing toward the map's west
    // edge) and flip the sign in ShadowVectorFromSunPosition below if it's mirrored.
    public static float SolarAzimuthDegrees(float latitudeDegrees, float declinationDegrees, float elevationDegrees, float dayPercent)
    {
        float latRad = ToRadians(latitudeDegrees);
        float declRad = ToRadians(declinationDegrees);
        float elRad = ToRadians(elevationDegrees);
        float hourAngleRad = ToRadians(HourAngleDegrees(dayPercent));

        float cosLat = MathF.Cos(latRad);
        float cosEl = MathF.Cos(elRad);
        if (Abs(cosLat) < 0.0001f || Abs(cosEl) < 0.0001f)
            return NormalizeDegrees(HourAngleDegrees(dayPercent));

        float sinAz = MathF.Cos(declRad) * MathF.Sin(hourAngleRad) / cosEl;
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

    private static float ToRadians(float degrees) => degrees * MathF.PI / 180f;
    private static float ToDegrees(float radians) => radians * 180f / MathF.PI;

    private static float NormalizeDegrees(float degrees)
    {
        float wrapped = degrees % 360f;
        return wrapped < 0f ? wrapped + 360f : wrapped;
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
