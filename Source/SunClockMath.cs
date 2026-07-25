using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types, same discipline as Formulas.cs / NightRadianceMath.cs.
// Linked into both Source (net481) and Tests (net8.0) so the shipped code is the tested code.
//
// Subsystem 14 (DESIGN.md §14): reconciling our physical sun with vanilla's day/night clock.
//
// THE PROBLEM. Every visual subsystem keys on SolarPosition.ElevationForMap — a real
// latitude/declination/hour-angle model. Vanilla's brightness keys on GenCelestial's own sun, which is
// not a physical model: it lifts the sun bodily toward the surface normal (19 degrees scaled by
// SunPeekAroundDegreesFactorCurve, plus another 17 that fades out from the equator to 60), and damps
// the seasonal term to a flat 0.2 below 70 degrees latitude. So the two disagree, and the disagreement
// is not small — measured against the shipped build:
//
//     hours per day with vanilla's sky lit but our sun below the horizon ("bright but shadowless")
//       lat  0 : 4.8 h      lat 45 : 3.1 - 6.1 h      lat 70 : up to 15.3 h
//       lat 30 : 3.4-5.2 h  lat 60 : 5.2 - 8.6 h
//
// Two ways out, and the mod supports both because they trade against each other:
//
//   LOCKED (default) — warp our sun's clock so it rises and sets exactly when vanilla's sky does.
//     Day length error is 0 by construction at every latitude and season, nothing gameplay-facing
//     changes, and the "lighting mod only" contract holds. Cost: we inherit vanilla's quirks (its
//     5-degree polar cliff at 70-75, and a southern hemisphere that never gets a true polar day
//     because both of its latitude curves are evaluated on SIGNED latitude and clamp below 70).
//
//   REALISTIC (opt-in) — drive vanilla's glow from our sun instead, via GlowFromElevation below.
//     True polar behaviour, real arctic summers, correct equinoxes at the poles. Cost: day length
//     moves ~1.5 h on average within +/-50 degrees (worst 2.75 h), which shifts growing hours and
//     solar output. That is a gameplay change, hence opt-in.
//
// Both run through one choke point, so the toggle needs no restart and nothing is baked.
public static class SunClockMath
{
    // --- LOCKED mode: time warp ---
    //
    // Both suns are symmetric about solar noon: vanilla rotates its sun by (dayPercent - 0.5) * 360
    // and takes a dot product (even in that angle), and our own HourAngleDegrees uses the identical
    // convention. So a whole day/night window collapses to ONE number — the half-day fraction h, with
    // sunrise at 0.5 - h and sunset at 0.5 + h. That symmetry is what makes this a two-line remap
    // instead of a piecewise search over sunrise/noon/sunset anchors.
    //
    // h == 0 means the sun never rises (polar night); h == 0.5 means it never sets (polar day).

    // Our physical half-day, solved analytically from the standard sunrise equation
    // cos(H) = (sin(h0) - sin(lat)sin(decl)) / (cos(lat)cos(decl)), with h0 the refraction horizon
    // Formulas already uses. Returns 0 for polar night and 0.5 for polar day, so the caller never has
    // to special-case the poles — they fall out of the same formula.
    public static float PhysicalHalfDay(float latitudeDegrees, float declinationDegrees)
    {
        float lat = ToRadians(latitudeDegrees);
        float decl = ToRadians(declinationDegrees);
        float horizon = ToRadians(Formulas.AtmosphericRefractionDegrees);

        float denominator = MathF.Cos(lat) * MathF.Cos(decl);
        if (MathF.Abs(denominator) < 1e-9f)
        {
            // Exactly at a pole (or a degenerate declination): the hour-angle term vanishes and
            // elevation is constant all day, so it is either up the whole time or down the whole time.
            float constantElevation = MathF.Sin(lat) * MathF.Sin(decl);
            return constantElevation > MathF.Sin(horizon) ? 0.5f : 0f;
        }

        float cosHourAngle = (MathF.Sin(horizon) - MathF.Sin(lat) * MathF.Sin(decl)) / denominator;
        if (cosHourAngle <= -1f)
            return 0.5f;
        if (cosHourAngle >= 1f)
            return 0f;

        // acos gives the half-day in radians of hour angle; 180 degrees of hour angle == half a day.
        return MathF.Acos(cosHourAngle) / MathF.PI * 0.5f;
    }

    // Smallest half-day we will warp to when vanilla says the sun rises but our physical sun never
    // does (or vice versa) — the two models disagree about whether there is a day at all, which
    // happens in a narrow band near the poles around the equinoxes. Without this, the target window
    // collapses to zero, the remap sends the entire day to a single instant, and the sun freezes at
    // one elevation for 24 hours. Clamping to a sliver of a day keeps a real (if very short) arc, so
    // the sky still moves and the shadow still sweeps.
    public const float MinWarpHalfDay = 0.02f;

    // Remaps a dayPercent so that our sun hits the horizon exactly when vanilla's sky does.
    //
    // Daytime maps onto daytime and night onto night, each linearly, both anchored at noon and
    // midnight — so the warp is continuous, strictly increasing, and never reorders events. Sunrise
    // lands on sunrise by construction, which is the whole point: the day-length error is not "small",
    // it is exactly zero.
    //
    // vanillaHalfDay is measured off the live game (SunClock bisects GenCelestial's own glow), NOT
    // re-derived here — re-implementing vanilla's curve would silently rot the first time Ludeon
    // retunes it.
    public static float WarpDayPercent(float dayPercent, float vanillaHalfDay, float physicalHalfDay)
    {
        float hv = Clamp(vanillaHalfDay, 0f, 0.5f);
        float hp = Clamp(physicalHalfDay, 0f, 0.5f);

        // Only clamp the target when the two models disagree about whether a day exists at all;
        // genuine polar day/night on BOTH sides must stay exact, or midnight sun picks up a fake dip.
        bool vanillaHasDay = hv > 0f && hv < 0.5f;
        if (vanillaHasDay)
            hp = Clamp(hp, MinWarpHalfDay, 0.5f - MinWarpHalfDay);

        // Offset from noon, in [-0.5, 0.5). Working in this space is what makes the symmetry usable.
        float d = dayPercent - 0.5f;
        d -= MathF.Floor(d + 0.5f);

        float magnitude = MathF.Abs(d);
        float sign = d < 0f ? -1f : 1f;

        // Daytime branch: |d| <= hv. Guarded against hv == 0 (vanilla polar night), where every
        // instant belongs to the night branch instead.
        if (hv > 0f && magnitude <= hv)
            return 0.5f + sign * magnitude * (hp / hv);

        // Night branch: map the leftover (0.5 - hv) onto the leftover (0.5 - hp). At hv == 0.5
        // (vanilla polar day) there is no night to map and the daytime branch above has already
        // covered the whole day.
        float vanillaNight = 0.5f - hv;
        float physicalNight = 0.5f - hp;
        if (vanillaNight <= 0f)
            return 0.5f + sign * hp;

        float intoNight = magnitude - hv;
        return 0.5f + sign * (hp + intoNight * (physicalNight / vanillaNight));
    }

    // --- REALISTIC mode: elevation -> glow ---
    //
    // Three anchors, because day length and dusk length are independent concerns and a single scale
    // factor cannot serve both. Vanilla's own mapping is glow = sin(elevation) / 0.7, which puts its
    // "daytime" bar (glow > 0.6) at a sun 25 degrees up — only reachable because vanilla lifts its
    // sun. Feed a physical sun through that same mapping and daytime collapses by 5+ hours at the
    // equator and polar summer registers as permanent night, since a polar sun never clears 25.
    //
    // Fitting one scale factor to day length instead gives K = 6.3 degrees, which matches day length
    // well (mean 1.75 h over +/-70) but saturates glow by 6 degrees of elevation — collapsing dusk
    // from vanilla's ~240 min to 37 min. For a lighting mod that is a worse regression than the thing
    // being fixed, and it would squeeze §2 twilight, §8 colour temperature, §11 aurora and §12 blood
    // moon (all glow-keyed) into a sliver.
    //
    // Splitting the anchors fixes both at once, because day length depends only on where glow crosses
    // 0.6 and the dusk ramp only on where it reaches 1.0:
    //     measured at lat 45 in summer — vanilla 239.9 min of dusk, three-anchor 267.3 min, K-fit 37.2

    // Glow 0.6 is vanilla's IsDaytime bar, so this elevation IS the day-length knob. 3.8 degrees was
    // fitted against vanilla's own day length across +/-70 latitude over a full year.
    public const float DaytimeElevationDegrees = 3.8f;

    // Where glow saturates. Matched to vanilla's effective saturation so the dusk ramp keeps its
    // shape; raising or lowering this changes how fast full daylight arrives and nothing else.
    public const float SaturationElevationDegrees = 45f;

    public const float DaytimeGlow = 0.6f;

    // Piecewise-linear in elevation between the three anchors: 0 at the refraction horizon, 0.6 at
    // DaytimeElevationDegrees, 1 at SaturationElevationDegrees.
    public static float GlowFromElevation(float elevationDegrees)
    {
        if (elevationDegrees <= Formulas.AtmosphericRefractionDegrees)
            return 0f;
        if (elevationDegrees >= SaturationElevationDegrees)
            return 1f;

        if (elevationDegrees <= DaytimeElevationDegrees)
        {
            float t = (elevationDegrees - Formulas.AtmosphericRefractionDegrees)
                / (DaytimeElevationDegrees - Formulas.AtmosphericRefractionDegrees);
            return t * DaytimeGlow;
        }

        float u = (elevationDegrees - DaytimeElevationDegrees)
            / (SaturationElevationDegrees - DaytimeElevationDegrees);
        return DaytimeGlow + u * (1f - DaytimeGlow);
    }

    private static float ToRadians(float degrees) => degrees * MathF.PI / 180f;
    private static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
}
