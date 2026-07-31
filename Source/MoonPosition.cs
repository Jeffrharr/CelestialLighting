using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// Thin per-map adapter for the moon, the moon-side twin of SolarPosition. It pulls the primitives
// MoonMath needs off live Map/Find/GameComponent state (tile lat/long, day-of-year, day-percent, and
// the game-wide cycle position) and hands them to the pure core, which does all the trigonometry.
//
// Both consumers of moon geometry go through this one adapter so they can never derive a different
// moon from each other — the same discipline SolarPosition already enforces for the sun across the
// two shadow patches. If the moon component is absent (no game loaded, or on the main menu), every
// accessor degrades to "no moon" (elevation far below the horizon, zero shadow, zero moonlight)
// rather than throwing on the render path.
//
// SkyForMap is memoized per (map, frame, cycle position) — see GeometryMemo.cs. As with the sun,
// keep it the only place moon geometry is derived: everything below (ShadowForMap,
// MoonlightBrightnessForMap) reads through it, which is what makes one cached evaluation per frame
// serve the whole render path.
public static class MoonPosition
{
    public readonly struct Sky
    {
        public readonly float ElevationDegrees;
        public readonly float AzimuthDegrees;
        public readonly float IlluminatedFraction;

        // Carried alongside the two angles it produced, purely so the moon's declination is
        // observable where the sun's already is (SolarPosition.Inputs.Declination). Nothing in the
        // render path reads it. It exists because the declination is the one number the Realistic
        // Axial Tilt seam actually changes, and a probe that recomputed it instead of reading what
        // this struct was built from could report our model while the game drew theirs — the exact
        // blindness AxialTiltDeclinationProbe's header describes for the sun.
        public readonly float DeclinationDegrees;

        public Sky(
            float elevationDegrees, float azimuthDegrees, float illuminatedFraction, float declinationDegrees)
        {
            ElevationDegrees = elevationDegrees;
            AzimuthDegrees = azimuthDegrees;
            IlluminatedFraction = illuminatedFraction;
            DeclinationDegrees = declinationDegrees;
        }
    }

    // A far-below-horizon, unilluminated moon used when there is no live moon component to read.
    // Every downstream helper treats this as "no moon up", so callers need no separate null path.
    private static readonly Sky NoMoon = new Sky(-90f, 0f, 0f, 0f);

    // Dev-only escape hatch, always true in a real game — exactly the SunClockAdapter.WarpEnabled
    // pattern and there for the same reason. Setting it false leaves the moon on the raw day percent
    // while the sun stays warped, which reproduces the shipped-§14 artifact this file's SkyForMap
    // comment describes, so the live harness can capture it as the BEFORE half of an A/B. Flipping
    // SunClockAdapter.WarpEnabled instead would not do: that reverts the SUN too, giving the pre-§14
    // single-clock world rather than the bug. Nothing in the shipped mod ever writes this — see
    // ProbeRegistration's moon_clock_warp bridge.
    public static bool WarpMoonClock = true;

    // Per-frame memo (issue #12; rationale in GeometryMemo.cs). SkyForMap is the moon's twin of
    // SolarPosition.InputsForMap: ShadowForMap, MoonlightBrightnessForMap, the HUD readout and
    // Patch_MoonShadowColor's CurSkyTarget postfix all land here — six evaluations per frame on the
    // current map, and each uncached one paid for a *second* WorldGrid.LongLatOf on top of the one
    // inside InputsForMap.
    private static readonly GeometryMemo<Sky> SkyMemo = new GeometryMemo<Sky>();

    // Static delegate over a value tuple rather than a closure over `map`, so the memoized path
    // allocates nothing per frame.
    private static readonly Func<(Map map, float cyclePosition), Sky> ComputeSky = ComputeSkyForMap;

    public static Sky SkyForMap(Map map)
    {
        GameComponent_MoonPhase moon = GameComponent_MoonPhase.Current;
        if (moon == null)
            return NoMoon;

        // Read (cheaply, from the tick) and folded into the memo key rather than left implicit,
        // because it is the one input here that is NOT a pure function of (map, tick): the harness's
        // eclipse staging slides the whole cycle mid-run by writing debugSynodicShiftTicks. Keying on
        // the resulting cycle position means a staged eclipse can never be served a pre-stage moon.
        float cyclePosition = moon.CyclePosition;

        return SkyMemo.Get(
            map.uniqueID,
            FrameStamp.Current().WithScalar(cyclePosition),
            (map, cyclePosition),
            ComputeSky);
    }

    private static Sky ComputeSkyForMap((Map map, float cyclePosition) args)
    {
        Map map = args.map;
        float cyclePosition = args.cyclePosition;

        // The moon's day percent is NOT independent of the sun's — MoonMath.MoonDayPercent exists to
        // produce moonHourAngle == sunHourAngle - elongation, so whatever clock the sun is on, the
        // moon must lag THAT clock. So take the day percent from SolarPosition.InputsForMap rather
        // than reading GenLocalDate.DayPercent again: in §14's default locked mode the sun's percent
        // is warped onto vanilla's day, and the moon has to be warped with it.
        //
        // Reading the raw percent here instead is what §14 originally shipped, and it silently broke
        // every sun-moon relationship the model is built on. The moon lagged the wrong sun by
        // (vanillaHalfDay - physicalHalfDay) * 24 — 1.5 to 3.1 h within +/-60 degrees, 7.7 h at
        // latitude 70 in winter. Measured against the shipped build, with the pre-§14 single-clock
        // numbers in brackets as the baseline this restores:
        //   - a full moon rose 2.2-3.3 h before sunset, 4.97 h worst case  [0.11-0.37 h],
        //   - it hung in a lit sky 3.5-6.5 h a day, 13.2 h at latitude 70  [0.22-1.36 h],
        //   - a new moon sat 12-35 degrees of elevation off the sun        [0 — it IS the sun's
        //     position at elongation 0], and
        //   - the dusk shadow handoff popped. Both shadow patches switch from sun to moon at the
        //     sun's horizon crossing, where ShadowIntensityFromElevation has ramped the sun shadow to
        //     0; the moon shadow then starts at whatever the moon's elevation implies, over a ramp
        //     only 3 degrees wide. On the raw clock the moon was already 10-36 degrees up there, so
        //     the shadow snapped straight to full MoonShadowMaxStrength (0.28) pointing somewhere
        //     unrelated, every clear night around full moon.
        //
        // That last one is worth stating precisely, because the fix does not make it zero. Refraction
        // enters both sunrise equations with the same sign while the full moon's declination is the
        // sun's REFLECTED, so the two windows are not exact complements: at sunset a full moon sits
        // ~0.8 degrees up, for a handoff step of 0.155. That step is inherent to the moon model and
        // predates §14 — what this restores is the baseline, not perfection.
        //
        // Latitude comes from the same Inputs for the same reason SolarPosition centralizes it: two
        // adapters deriving their own tile latitude is how the sun and moon drift apart.
        SolarPosition.Inputs sun = SolarPosition.InputsForMap(map);
        float dayPercent = WarpMoonClock ? sun.DayPercent : GenLocalDate.DayPercent(map);

        // Day-of-year still comes straight off the tick: it selects the moon's place on the ecliptic
        // (its declination), which is an orbital fact and has nothing to do with the time-of-day warp.
        Vector2 longLat = Find.WorldGrid.LongLatOf(map.Tile);
        int dayOfYear = GenDate.DayOfYear(Find.TickManager.TicksAbs, longLat.x);

        // Resolved through the same interop seam as the sun's declination, so the two bodies can
        // never end up on different models of the year. With Realistic Axial Tilt installed this is
        // their moon, inclined orbit and all, evaluated at OUR cycle position; without it (or against
        // a RAT predating their lunar geometry) it degrades to our own model at the moon's equivalent
        // sun-day, which is that same moon with the inclination set to zero. See
        // AxialTiltCompat.MoonDeclinationDegrees for why the phase stays ours while the orbit is
        // theirs, and MoonMath.MoonEquivalentSunDayOfYear for the fallback.
        float declination = AxialTiltCompat.MoonDeclinationDegrees(dayOfYear, cyclePosition);
        float moonDayPercent = MoonMath.MoonDayPercent(dayPercent, cyclePosition);

        // Reuse Formulas' own solar-position equations for the moon, feeding the moon's declination
        // and lagged day-percent — the moon is just another body on the ecliptic.
        //
        // We take only the declination from RAT and not their LunarElevationDegrees/LunarAzimuthDegrees,
        // even though those exist, and the reason is not distrust: their hour angle is (dayPercent -
        // cyclePosition), the same lag MoonMath.MoonDayPercent produces, so with the same declination
        // the two agree. What differs is what feeds them. Our day percent is §14-warped onto vanilla's
        // day (see above), theirs is not; and their azimuth returns 0 where azimuth is undefined,
        // which our shadow vector would silently read as "due north" rather than "no direction".
        // Keeping both bodies on one trig path is also what makes the sun/moon invariants above
        // testable offline, where no RAT exists.
        float elevation = Formulas.SolarElevationDegrees(sun.Latitude, declination, moonDayPercent);
        float azimuth = Formulas.SolarAzimuthDegrees(sun.Latitude, declination, elevation, moonDayPercent);
        return new Sky(elevation, azimuth, MoonMath.IlluminatedFraction(cyclePosition), declination);
    }

    // Moon-cast shadow for this map, or null when the moon is down / new (so no shadow should render).
    // Vector points directly away from the moon, exactly like the sun's shadow vector, with a length
    // from the same cot(elevation) curve; strength is the faint, phase-and-altitude-scaled moon alpha.
    public static (Vector2 vector, float strength)? ShadowForMap(Map map)
    {
        // Feature gate (default on): when off, report no moon shadow so both shadow patches fall
        // back to a shadowless night — the faithful pre-moon baseline. See MoonShadows in
        // CelestialLightingFeatures for why this single choke point is where the toggle lives.
        if (!CelestialLightingFeatures.MoonShadows)
            return null;

        Sky sky = SkyForMap(map);

        // §6b: how bright the sky already is decides whether the moon's shadow can be seen at all, so
        // the sun's elevation is an input here rather than the gate it used to be. Read from the same
        // SolarPosition adapter both shadow patches read, so all three agree on one sun — the same
        // discipline that keeps this file the single source of the moon.
        float sunElevation = SolarPosition.ElevationForMap(map);

        // Returns 0 (and so null, below) once the shadow would render under the perceptible floor —
        // which now includes all of daylight, where the ratio is nonzero but four orders of magnitude
        // too small to see. That is what removes the old sunset pop: there is no longer a moment where
        // this switches from "no shadow" to "full shadow".
        float strength = MoonMath.MoonShadowStrength(sky.IlluminatedFraction, sky.ElevationDegrees, sunElevation);
        if (strength <= 0f)
            return null;

        Formulas.ShadowVector shadow = Formulas.ShadowVectorFromSunPosition(sky.ElevationDegrees, sky.AzimuthDegrees);
        return (new Vector2(shadow.X, shadow.Y), strength);
    }

    // Normalized 0..1 moonlight contribution for the current map — the seam the night-radiance
    // subsystem (§7) consumes.
    // TODO(integration): §7 night-radiance should read this and sum it with its starlight/airglow
    // floors (scaled by its own moonlight slider) rather than recomputing the moon itself.
    public static float MoonlightBrightnessForMap(Map map)
    {
        Sky sky = SkyForMap(map);
        return MoonMath.MoonlightBrightness(sky.IlluminatedFraction, sky.ElevationDegrees);
    }
}
