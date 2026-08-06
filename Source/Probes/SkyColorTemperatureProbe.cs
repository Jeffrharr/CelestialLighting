using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj —
// the shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// Reads back the §8 CLEAN-AIR colour temperature the patch derives the sky tint from, in Kelvin, for
// the live tile/tick: the same sun elevation via the shared SolarPosition simulator
// Patch_SkyColorTemperature uses, fed through the same SkyColorTemperature.ColorTemperatureKelvin
// ramp. So a scenario can pin the altitude -> temperature curve end-to-end against a running game
// (warm ~2000 K near the horizon, neutral ~5772 K with the sun high) rather than only via the offline
// SkyColorTemperatureTests.
//
// WHY THIS NO LONGER SEES POLLUTION, which reads like a regression and is not. Until §20d the aerosol
// load moved this ramp's warm endpoint, so a Kelvin reading carried it. §20d retired that endpoint:
// aerosol's colour effect is a spectral SHAPE and no single colour temperature can carry one, so it
// is now applied per channel afterwards. A probe that folded it back into a Kelvin would be inventing
// a number the sky is not being tinted toward, which is the exact failure §18's "a probe reads the
// same value its patch does" rule exists to prevent. The aerosol half is probed honestly instead, as
// the two numbers that actually describe it: aerosol_angstrom_exponent and sky_red_blue_ratio.
public sealed class SkyColorTemperatureProbe : IProbe
{
    public string Name => "sky_color_temperature";

    public float Read(Map map)
    {
        float elevation = SolarPosition.ElevationForMap(map);
        // Every input this half of the curve takes, read the same way through the same helpers, so a
        // scenario can never observe a temperature the sky is not actually being tinted toward:
        //   * the §20 site air column, so a mountain scenario probes the whiter horizon endpoint
        //     (~3416 K at 4000 m) rather than the sea-level 2000 K;
        //   * the §18 vacuum gate, so an orbital scenario probes the flat unreddened anchor
        //     (5772 K at every elevation) rather than the ground ramp.
        return SkyColorTemperature.ColorTemperatureKelvin(
            elevation,
            SiteAltitude.PressureFractionForMap(map),
            Vacuum.InVacuumForMap(map));
    }
}
