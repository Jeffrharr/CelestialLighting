using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace CelestialLighting;

// Adapter + seam for the NATURAL eclipse (DESIGN.md §10a): the opt-in mode that fires the vanilla
// Eclipse GameCondition only when the modeled moon actually transits the sun, with the correct SHORT
// real-eclipse duration, and suppresses the random Eclipse incident so the two don't double-fire.
// Because it changes *when* and *how long* a gameplay event (solar-power loss, colonist mood) occurs,
// it is one of the flavours selected by EclipseSettings.Mode (the three-way eclipse-mode radio): it
// runs in NaturalOnly and in the default Both, and stands down in UnnaturalOnly, where only the
// storyteller's random eclipse gets the §10b cosmetic reshape in Patch_EclipseDarkening.
//
// The trigger needs the modeled moon's true ecliptic position — orbital inclination + nodes — which
// the moon-position subsystem (DESIGN.md §6) now provides: GameComponent_MoonPhase exposes both the
// synodic cycle (elongation) and the nodal cycle, and MoonMath turns them into a sun-moon angular
// separation. This file is the thin Verse-facing adapter that reads that live state and hands
// primitives to the pure EclipseMath/MoonMath cores; the orchestration (when to fire/end/suppress an
// Eclipse) lives in GameComponent_NaturalEclipse. The MoonSunGeometryProvider indirection is kept so
// the geometry source stays swappable and testable, but it now defaults to the real live moon.
public static class EclipseIntegration
{
    // Provider for the current apparent geometry of the modeled moon relative to the sun on a given
    // map. Defaults to the real live moon (LiveMoonSunGeometry); kept assignable so tests can swap it.
    // Returns null when there is no moon component (off-game/main menu), so ShouldEclipseBeActive is
    // always safe to call.
    public static Func<Map, MoonSunGeometry?> MoonSunGeometryProvider = LiveMoonSunGeometry;

    // Magnitude (0 = grazing partial, 1 = dead-central total) of the natural eclipse currently being
    // driven, published by GameComponent_NaturalEclipse when it fires one so the darkening patch and
    // the dev probe can scale the ramp to how much of the sun is actually covered. Resets to 1 (the
    // central default) so the unnatural mode and any not-yet-computed state read a full park.
    public static double ActiveNaturalMagnitude = 1.0;

    // The Eclipse conditions WE fired from geometry, so the darkening patch/probe can tell a natural
    // eclipse from the storyteller's when Both mode has both kinds. Reference identity, pruned of
    // expired entries on each fire so it can't grow without bound over a long game. (Not persisted:
    // if a save is taken mid-eclipse in Both mode, that one condition renders unnatural for its
    // remainder after load — cosmetic-only, and eclipses are short and rare.)
    private static readonly HashSet<GameCondition> naturalConditions = new HashSet<GameCondition>();

    // Record a condition as one of ours (called by GameComponent_NaturalEclipse right after it fires).
    public static void MarkNaturalCondition(GameCondition condition)
    {
        naturalConditions.RemoveWhere(c => c == null || c.Expired);
        naturalConditions.Add(condition);
    }

    // Which darkening ramp this active eclipse should use, per the current mode (and, in Both mode,
    // whether we fired it). The single choke point both the live patch and the offline probe call so
    // they render identically.
    public static bool RendersNatural(GameCondition condition) =>
        EclipseModeRules.RendersNatural(EclipseSettings.Mode, naturalConditions.Contains(condition));

    // The live geometry provider: reads the game-wide modeled moon (shared across all maps) and the
    // planet's orbital day-of-year, and turns them into the sun-moon separation and the eclipse's
    // impact parameter (closest approach ≈ the moon's ecliptic latitude at the new moon). The transit
    // is a global celestial fact, so the Map argument is only a hook for the seam — the separation
    // itself doesn't depend on which map. Returns null with no moon component present.
    public static MoonSunGeometry? LiveMoonSunGeometry(Map map)
    {
        GameComponent_MoonPhase moon = GameComponent_MoonPhase.Current;
        if (moon == null)
            return null;

        float cyclePosition = moon.CyclePosition;
        float nodalPosition = moon.NodalCyclePosition;
        float dayOfYear = OrbitalDayOfYear();

        float separation = MoonMath.SunMoonSeparationDegrees(dayOfYear, cyclePosition, nodalPosition);
        // Closest approach ≈ |ecliptic latitude| at the new moon: near new moon the longitude gap is
        // ~0, so the moon's latitude is essentially the impact parameter that sets how central (how
        // dark) the eclipse is. Computed from the same primitives so it can never disagree.
        float impact = Math.Abs(MoonMath.MoonEclipticLatitudeDegrees(dayOfYear, cyclePosition, nodalPosition));

        return new MoonSunGeometry(
            separation, EclipseMath.SunAngularRadiusDegrees, EclipseMath.MoonAngularRadiusDegrees, impact);
    }

    // Continuous day-of-year (fractional) driving the sun's ecliptic longitude, from the absolute tick
    // at full resolution. Kept longitude-independent: the sun-moon separation is a frame-invariant
    // angle, and any fixed hemisphere offset would just re-anchor the (already arbitrary) node epoch —
    // so a plain global orbital phase is both correct and the simplest thing that agrees with the pure
    // EclipseStaging math used to film the event.
    private static float OrbitalDayOfYear() =>
        (float)((double)Find.TickManager.TicksAbs / GenDate.TicksPerDay % Formulas.DaysPerYear);

    // Apparent geometry of the moon relative to the sun at an instant. Angles in degrees. Consumed by
    // the pure EclipseMath.IsGeometricTransit / TransitMagnitude checks so the decisions stay testable
    // and free of live game state.
    public readonly struct MoonSunGeometry
    {
        public readonly double SeparationDegrees;
        public readonly double SunAngularRadiusDegrees;
        public readonly double MoonAngularRadiusDegrees;
        // Closest sun-moon approach over the passage (the eclipse's impact parameter). Sets magnitude.
        public readonly double ClosestApproachDegrees;

        public MoonSunGeometry(
            double separationDegrees, double sunAngularRadiusDegrees, double moonAngularRadiusDegrees,
            double closestApproachDegrees)
        {
            SeparationDegrees = separationDegrees;
            SunAngularRadiusDegrees = sunAngularRadiusDegrees;
            MoonAngularRadiusDegrees = moonAngularRadiusDegrees;
            ClosestApproachDegrees = closestApproachDegrees;
        }
    }

    // Whether a real (§10a) eclipse should be active right now, per the opt-in trigger. Returns false
    // whenever the mode is off or there is no moon geometry, so the mod never asserts a transit unless
    // asked. When both are present the decision defers to the pure geometry check, keeping it testable.
    public static bool ShouldEclipseBeActive(Map map)
    {
        if (!EclipseModeRules.NaturalTriggerActive(EclipseSettings.Mode))
            return false;

        MoonSunGeometry? geometry = MoonSunGeometryProvider?.Invoke(map);
        if (!geometry.HasValue)
            return false;

        MoonSunGeometry g = geometry.Value;
        return EclipseMath.IsGeometricTransit(
            g.SeparationDegrees, g.SunAngularRadiusDegrees, g.MoonAngularRadiusDegrees);
    }

    // Central-ness of the transit happening right now, in [0, 1], from the live impact parameter — 1
    // for a bullseye total, near 0 for a grazing partial. GameComponent_NaturalEclipse reads this at
    // fire time to size both the darkening (via ActiveNaturalMagnitude) and the duration. Returns 0
    // when there is no geometry (caller should not be firing then anyway).
    public static double CurrentTransitMagnitude(Map map)
    {
        MoonSunGeometry? geometry = MoonSunGeometryProvider?.Invoke(map);
        if (!geometry.HasValue)
            return 0.0;

        MoonSunGeometry g = geometry.Value;
        return EclipseMath.TransitMagnitude(
            g.ClosestApproachDegrees, g.SunAngularRadiusDegrees, g.MoonAngularRadiusDegrees);
    }

    // Correct SHORT real-eclipse duration in ticks for a transit of the given magnitude, from the
    // moon's relative angular speed (elongation sweeps a full 360° per synodic month). Falls back to 0
    // — caller then leaves the event alone — if there is no moon component or a degenerate period.
    public static int CurrentEclipseDurationTicks(double magnitude)
    {
        GameComponent_MoonPhase moon = GameComponent_MoonPhase.Current;
        if (moon == null || moon.synodicPeriodDays <= 0f)
            return 0;

        // Moon-relative-to-sun angular speed: 360° over one synodic month, expressed per hour.
        double relativeSpeedDegPerHour = 360.0 / (moon.synodicPeriodDays * GenDate.HoursPerDay);
        double ticks = EclipseMath.NaturalEclipseDurationTicks(
            relativeSpeedDegPerHour,
            EclipseMath.SunAngularRadiusDegrees,
            EclipseMath.MoonAngularRadiusDegrees,
            magnitude,
            GenDate.TicksPerHour);
        return ticks <= 0.0 ? 0 : (int)Math.Round(ticks);
    }

    // Fraction of the eclipse elapsed, in [0, 1]. TicksPassed + TicksLeft is the full duration; the
    // degenerate zero-duration case (nothing elapsed yet) reports 0 so the sky stays at its normal
    // daytime value rather than dividing by zero. Shared by Patch_EclipseDarkening and the dev-only
    // EclipseCoverageProbe so the live darkening and the probe read the exact same progress.
    public static float ProgressOf(GameCondition condition)
    {
        int passed = condition.TicksPassed;
        int total = passed + condition.TicksLeft;
        if (total <= 0)
            return 0f;

        return (float)passed / total;
    }
}
