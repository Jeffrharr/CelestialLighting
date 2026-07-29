using Verse;

namespace CelestialLighting;

// Thin adapter for §7 / §18b: pulls the primitives NightRadianceMath needs off live Map state and
// answers one question — how dark can the sky over this map get right now.
//
// SHARED ON PURPOSE, and this is the point of the file existing at all. Three subsystems need the
// same number and would otherwise each derive their own:
//
//   §7   Patch_NightRadiance blends the night sky toward it (this is the value the floor IS)
//   #31  vacuum shadow contrast — with no skylight to fill the umbra, a shadow bottoms out here
//   #33  vacuum eclipse response — totality falls to here rather than to a tuned fraction
//
// Same value reached from three directions. That is the same discipline SolarPosition enforces for
// sun elevation and WeatherDimming for cloud cover, and it is deliberately a SHARED READ rather than
// one patch stashing a value for another to pick up later: a read has no ordering to get wrong, no
// staleness across frames, and no silent dependency on which Harmony patch ran first.
//
// WHAT IT DOES NOT DO. It reports the floor, not the current sky — no sun-elevation ramp is applied
// (NightRadianceMath.ApplyNightFloor owns that) and no weather multiply (§13 owns that, on the colour
// channel). Callers that want "the darkest this can get" want exactly this; callers that want "how
// bright is it now" want SkyManager.CurSkyGlow.
//
// It is also deliberately NOT gated on CelestialLightingFeatures.NightRadiance. That flag turns §7's
// own effect off, restoring vanilla's flat night glow — a value this mod does not own and cannot
// report. Making the shared floor jump whenever an unrelated toggle flipped would be worse than
// useless to #31 and #33, so each consumer gates its own effect and this reports the model.
public static class NightRadiance
{
    // The night floor for this map, in RimWorld's 0..1 glow units: the sum of every dim night-side
    // source the model knows about, with the §18b vacuum substitutions applied when the map has no
    // atmosphere. Includes moonlight, so it tracks the lunar cycle — a full-moon night genuinely has
    // a higher floor than a new-moon one, which is what makes an eclipse or an umbra under a full
    // moon bottom out higher than the same event on a new moon.
    public static float FloorGlowFor(Map map)
    {
        NightRadianceSettings settings = NightRadianceSettings.Current;

        // The atmospheric floors are the "true pitch-black" switch: with them off, starlight and
        // airglow drop to zero and only the reflected sources remain. Read here rather than in the
        // patch so every consumer of the floor sees the same answer to that toggle.
        float starlight = settings.AtmosphericGlowEnabled ? settings.StarlightGlow : 0f;
        float airglow = settings.AtmosphericGlowEnabled ? settings.AirglowGlow : 0f;

        MoonState moon = MoonSeam.Provider(map);
        float moonlight = NightRadianceMath.MoonlightGlow(
            moon.IlluminatedFraction, moon.ElevationDegrees, settings.MaxMoonlightGlow);

        return NightRadianceMath.NightFloorGlow(
            starlight, airglow, moonlight, settings.MaxMoonlightGlow, Vacuum.InVacuumForMap(map));
    }
}
