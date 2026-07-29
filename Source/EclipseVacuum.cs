using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// Thin Verse adapter for §18e: pulls the primitives VacuumEclipseMath needs off live Map state and
// rebuilds the eclipse's own SkyTarget with them. All the reasoning lives in the pure core; this file
// only knows how to read a map and how to multiply a Color.
//
// It follows the §18 gate convention (Vacuum.cs) exactly: Vacuum.InVacuumForMap(map) is called ONCE
// here, and the resulting bool is passed down as the last argument of every pure function. Nothing
// branches on the atmosphere in this file — which is also why Patch_EclipseVacuumSky can run
// unconditionally and still be a provable no-op on planet-surface maps: the sea-level arm of
// UmbralSkyBrightnessScale is 1, so every channel is multiplied by exactly 1.0f.
public static class EclipseVacuum
{
    // The eclipse's umbral sky target for this map, given whatever vanilla (or an earlier postfix)
    // produced. Rebuilds two things and deliberately leaves the rest alone:
    //
    //   glow          -> VacuumEclipseMath.UmbralGlow, i.e. the §18b night floor in vacuum.
    //   colors.sky    -> scaled toward black by the night sky's own rendered brightness.
    //   colors.overlay-> same scale; it is the weather-overlay tint and tracks the sky.
    //
    // NOT touched:
    //
    //   colors.shadow     — how a cast shadow is tinted in vacuum is #31's subsystem (§18c), and
    //                       having two branches write it would be exactly the drift #30's shared
    //                       floor exists to prevent. Vanilla's eclipse shadow colour stands here.
    //   colors.saturation — colour handling on vacuum maps belongs to #29 (§18a). Scaling brightness
    //                       is a statement about how much light there is; saturation is not.
    //   lightsourceShine* — the sun's on-screen bloom. Vanilla already drives it to 0 for an eclipse,
    //                       and there is no less than none.
    public static SkyTarget UmbralTargetFor(Map map, SkyTarget atmosphericTarget)
    {
        // The single gate read for this subsystem, per Vacuum.cs's convention.
        bool inVacuum = Vacuum.InVacuumForMap(map);

        // #30's shared night floor — the same function §7 blends the night sky toward and #31 bottoms
        // its shadows out at. Read rather than stashed, so there is no patch ordering to get wrong.
        float nightFloorGlow = NightRadiance.FloorGlowFor(map);
        float minNightBrightness = NightRadianceSettings.Current.MinNightBrightness;

        float glow = VacuumEclipseMath.UmbralGlow(atmosphericTarget.glow, nightFloorGlow, inVacuum);
        float scale = VacuumEclipseMath.UmbralSkyBrightnessScale(nightFloorGlow, minNightBrightness, inVacuum);

        SkyColorSet colors = atmosphericTarget.colors;
        colors.sky = Darken(colors.sky, scale);
        colors.overlay = Darken(colors.overlay, scale);

        return new SkyTarget(
            glow, colors, atmosphericTarget.lightsourceShineSize, atmosphericTarget.lightsourceShineIntensity);
    }

    // Scales a sky colour's brightness while PRESERVING ALPHA. Multiplying a Color by a float scales
    // alpha too, and that would invert the whole effect: MatBases.LightOverlay is an overlay material
    // whose alpha controls how much of it lands on the scene, so fading alpha toward 0 makes the map
    // brighter, not darker. The eclipse colour set ships at alpha 1 and must stay there.
    private static Color Darken(Color color, float scale) =>
        new Color(color.r * scale, color.g * scale, color.b * scale, color.a);
}
