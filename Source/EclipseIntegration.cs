using System;
using Verse;

namespace CelestialLighting;

// Self-contained seam for the OPT-IN astronomically-triggered eclipse mode described in the
// GitHub issue comment and DESIGN.md §10 ("Optional: astronomically-triggered eclipses"). That
// mode fires the vanilla Eclipse GameCondition only when the modeled moon actually transits the
// sun (and suppresses the random Eclipse incident so the two don't double-fire). Because it changes
// *when* a gameplay event (solar-power loss, colonist mood) occurs, it steps one notch outside this
// mod's visual-only remit and is therefore opt-in and OFF BY DEFAULT — the cosmetic darkening in
// Patch_EclipseDarkening stays the pure-visual default and does not depend on any of this.
//
// The trigger inherently needs the modeled moon's ecliptic position — orbital inclination + nodes —
// which is subsystem #3 (moon position), built in a parallel branch that is NOT merged yet. Rather
// than take a hard dependency on unmerged code, this file defines the minimal seam the trigger will
// plug into and keeps the whole path inert until both (a) the setting is switched on and (b) #3 has
// supplied a moon-separation provider. Standalone, this file has no effect at runtime.
public static class EclipseIntegration
{
    // Opt-in master switch. Defaults OFF: with this false the astronomical trigger never runs and
    // the mod is purely cosmetic.
    //
    // TODO(integration): once the mod gains its ModSettings screen (see DESIGN.md "Settings,
    // presets, brightness floor"), back this with a persisted user toggle instead of a plain static
    // field so the choice survives a restart. Keeping it a static for now avoids inventing a
    // settings-persistence layer inside this feature branch and keeps the default unambiguously off.
    public static bool AstronomicalTriggerEnabled = false;

    // Provider for the current angular separation (in degrees) between the modeled moon and the sun
    // on a given map, plus their apparent angular radii. Null until subsystem #3 (moon position)
    // is merged and registers a real implementation — while null the trigger cannot fire, which is
    // the intended standalone behavior.
    //
    // TODO(integration): #3 owns the moon's true ecliptic position (inclination + nodes). When it
    // lands, have it assign MoonSunGeometryProvider from its own startup so ShouldEclipseBeActive
    // below starts returning real answers. The flat "moon always on the ecliptic" approximation is
    // deliberately NOT used here — it would report a transit at every new moon and make eclipses
    // fire far too often (this is exactly the "requires orbital inclination + nodes" caveat in the
    // issue).
    public static Func<Map, MoonSunGeometry?> MoonSunGeometryProvider = null;

    // Apparent geometry of the moon relative to the sun at an instant, as seen from a map's tile.
    // Angles in degrees. Supplied by #3; consumed only by the pure EclipseMath.IsGeometricTransit
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

    // Whether a real eclipse should be active on this map right now, per the opt-in trigger. Returns
    // false whenever the mode is off or #3 has not supplied geometry, so the standalone mod never
    // asserts a transit. When both are present the decision defers to the pure geometry check.
    //
    // TODO(integration): the caller that will actually use this (fire GameConditionDefOf.Eclipse
    // when it flips true, and suppress the random Eclipse IncidentDef while the mode is on) belongs
    // in the branch that merges #3, since only then is there real geometry to drive it. It is left
    // unwired here on purpose: with the trigger off by default and no provider, wiring it now would
    // be dead code that could only misbehave once #3 arrives.
    public static bool ShouldEclipseBeActive(Map map)
    {
        if (!AstronomicalTriggerEnabled || MoonSunGeometryProvider == null)
            return false;

        MoonSunGeometry? geometry = MoonSunGeometryProvider(map);
        if (!geometry.HasValue)
            return false;

        MoonSunGeometry g = geometry.Value;
        return EclipseMath.IsGeometricTransit(
            g.SeparationDegrees, g.SunAngularRadiusDegrees, g.MoonAngularRadiusDegrees);
    }
}
