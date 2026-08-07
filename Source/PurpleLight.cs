using System;
using Verse;

namespace CelestialLighting;

// The impure boundary for §19c (the twilight purple light): it lifts sun elevation, latitude, the
// map's air column and the §18 vacuum flag off live game state and hands them to the pure
// PurpleLightMath layer.
//
// Extracted for the same reason OzoneTwilight is, even though §19c currently has only ONE consumer
// (Patch_PurpleLight) where §19 has two. The gates are the point: this subsystem has to agree with
// §8 and §19 about whether there is a sky at all, and re-deriving three gates inside a patch is
// exactly how the three drift apart. It also gives the live probe something to read that is not
// WeatherWorker.CurSkyTarget — see PurpleLightProbe for why that matters.
//
// One deliberate difference from OzoneTwilight: no per-frame memo. §19 memoises because two
// independent consumers each paid for three exp() calls per frame; here there is one consumer, the
// window is only two degrees wide, and WindowStrength early-outs on two comparisons everywhere
// else. A memo would be pure overhead for a dictionary hit's worth of saving.
public static class PurpleLight
{
    // How strongly the composed purple applies right now, in [0, 1]. Zero means the effect is
    // entirely absent — feature off, no sky overhead, sun outside the -6..-4 window, or in vacuum.
    public static float WindowStrengthFor(Map map)
    {
        if (!CelestialLightingFeatures.PurpleLight)
            return 0f;

        if (HasNoSky(map))
            return 0f;

        // §18 vacuum gate (Vacuum.cs), threaded into the pure layer as a parameter rather than
        // early-returned here, per §18a's rule — the "no atmosphere, no superposition of two
        // atmospheric scattering sources" decision belongs in the function it flattens.
        bool inVacuum = Vacuum.InVacuumForMap(map);
        float elevation = SolarPosition.ElevationForMap(map);

        return PurpleLightMath.WindowStrength(elevation, inVacuum) * PurpleLightSettings.TintStrength;
    }

    // The composed hue for the current tile and tick, normalised to a maximum channel of 1 so it
    // carries hue and nothing else. Callers must gate on WindowStrengthFor first; this deliberately
    // does not repeat the gates, because outside the window the value is meaningless rather than
    // zero and returning a "safe" colour would invite someone to use it there.
    public static SkyColorTemperature.Rgb ComposedHueFor(Map map)
    {
        // All five inputs come from the same memoised (map, frame) reads every other sun-driven
        // patch funnels through, so on any frame where §8 or §19 has already run this costs
        // dictionary hits and nothing else. Latitude comes off SolarPosition.Inputs rather than a
        // second Find.WorldGrid.LongLatOf for the reason Patch_PolarNightBlue spells out: an
        // independently-read latitude is the sort of thing that drifts from the elevation it is
        // supposed to pair with.
        SolarPosition.Inputs sun = SolarPosition.InputsForMap(map);
        float elevation = SolarPosition.ElevationForMap(map);

        // The same three atmospheric readings §8 itself reads (via Patch_SkyColorTemperature), so
        // the warm half of the superposition is the identical spectrum §8 is blending toward on this
        // tile rather than a sea-level stand-in. §19's half takes no altitude or aerosol input at all
        // and must never grow one — the ozone layer sits above the boundary layer, and
        // OzoneTwilightMath's signature guard enforces it.
        float pressureFraction = SiteAltitude.PressureFractionForMap(map);
        float aerosolFraction = SiteAltitude.AerosolFractionForMap(map);
        float angstromExponent = SiteAltitude.AngstromExponentForMap(map);
        bool inVacuum = Vacuum.InVacuumForMap(map);

        return PurpleLightMath.ComposedHue(
            elevation, sun.Latitude, pressureFraction, aerosolFraction, angstromExponent, inVacuum);
    }

    // §17's enclosed-map gate plus issue #35's blacked-out sky, mirrored from the other
    // CurSkyTarget postfixes. A cavern has no sky to scatter through, and an overhead opaque sulfur
    // cloud means no sunlight is reaching either of the two sources this superposes.
    private static bool HasNoSky(Map map) => MapSky.IsEnclosed(map) || MapSky.SkyBlackedOut(map);
}
