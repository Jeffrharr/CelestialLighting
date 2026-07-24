using System;
using RimWorld;
using Verse;

namespace CelestialLighting;

// Adapter + seam for the NATURAL eclipse (DESIGN.md §10a): the opt-in mode that fires the vanilla
// Eclipse GameCondition only when the modeled moon actually transits the sun, with the correct SHORT
// real-eclipse duration, and suppresses the random Eclipse incident so the two don't double-fire.
// Because it changes *when* and *how long* a gameplay event (solar-power loss, colonist mood) occurs,
// it steps one notch outside this mod's visual-only remit and is gated behind EclipseSettings
// .NaturalEclipseEnabled, which defaults OFF — the unnatural (§10b) cosmetic darkening in
// Patch_EclipseDarkening stays the pure-visual default and depends on none of this.
//
// The trigger inherently needs the modeled moon's ecliptic position — orbital inclination + nodes —
// which is the moon-position subsystem (DESIGN.md §6), built in a separate branch that is NOT merged
// yet. Rather than take a hard dependency on unmerged code, this file defines the minimal seam the
// trigger plugs into and keeps the whole path inert until both (a) NaturalEclipseEnabled is on and
// (b) §6 has supplied a real moon-geometry provider. Standalone, with the default no-transit
// provider, this file has no runtime effect.
public static class EclipseIntegration
{
    // Provider for the current apparent geometry of the modeled moon relative to the sun on a given
    // map. Defaults to a no-transit stub (a sensible default: with no moon model present we assert no
    // eclipse), so ShouldEclipseBeActive is always safe to call even standalone.
    //
    // TODO(integration): the moon-position subsystem (DESIGN.md §6) owns the moon's true ecliptic
    // position (inclination + nodes). When it lands, have it assign MoonSunGeometryProvider from its
    // own startup so ShouldEclipseBeActive starts returning real answers. The flat "moon always on the
    // ecliptic" approximation is deliberately NOT used here — it would report a transit at every new
    // moon and make eclipses fire far too often (the "requires orbital inclination + nodes" caveat in
    // §6's scope note and §10a).
    public static Func<Map, MoonSunGeometry?> MoonSunGeometryProvider = DefaultNoTransitProvider;

    // Sensible default until §6 lands: no moon geometry is known, so report none (no transit). Kept as
    // a named method rather than a null so the provider is always callable and the "no eclipse" answer
    // is explicit rather than a null special-case.
    private static MoonSunGeometry? DefaultNoTransitProvider(Map map) => null;

    // Apparent geometry of the moon relative to the sun at an instant, as seen from a map's tile.
    // Angles in degrees. Supplied by §6; consumed only by the pure EclipseMath.IsGeometricTransit
    // check so the decision itself stays testable and free of live game state.
    public readonly struct MoonSunGeometry
    {
        public readonly double SeparationDegrees;
        public readonly double SunAngularRadiusDegrees;
        public readonly double MoonAngularRadiusDegrees;

        public MoonSunGeometry(
            double separationDegrees, double sunAngularRadiusDegrees, double moonAngularRadiusDegrees)
        {
            SeparationDegrees = separationDegrees;
            SunAngularRadiusDegrees = sunAngularRadiusDegrees;
            MoonAngularRadiusDegrees = moonAngularRadiusDegrees;
        }
    }

    // Whether a real (§10a) eclipse should be active on this map right now, per the opt-in trigger.
    // Returns false whenever the mode is off or the provider reports no geometry, so the standalone
    // mod never asserts a transit. When both are present the decision defers to the pure geometry
    // check, keeping it testable.
    //
    // TODO(integration): the caller that will actually use this — a GameComponent that, while
    // NaturalEclipseEnabled is on, fires GameConditionDefOf.Eclipse with a duration from
    // EclipseMath.NaturalEclipseDurationTicks when this flips true, ends it when the discs part, and
    // suppresses the random Eclipse IncidentDef so they don't double-fire — belongs in the branch that
    // merges §6, since only then is there real moon geometry (and a real relative angular speed) to
    // drive it. It is left unwired here on purpose: with the trigger off by default and only the
    // no-transit provider, wiring it now would be dead code that could only misbehave once §6 arrives.
    public static bool ShouldEclipseBeActive(Map map)
    {
        if (!EclipseSettings.NaturalEclipseEnabled)
            return false;

        MoonSunGeometry? geometry = MoonSunGeometryProvider?.Invoke(map);
        if (!geometry.HasValue)
            return false;

        MoonSunGeometry g = geometry.Value;
        return EclipseMath.IsGeometricTransit(
            g.SeparationDegrees, g.SunAngularRadiusDegrees, g.MoonAngularRadiusDegrees);
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
