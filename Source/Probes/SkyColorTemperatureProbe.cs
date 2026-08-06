using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj —
// the shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// Reads back the §8 colour-temperature the patch keys the sky tint off, in Kelvin, for the live
// tile/tick: the same sun elevation via the shared SolarPosition simulator Patch_SkyColorTemperature
// uses, fed through the same SkyColorTemperature.ColorTemperatureKelvin ramp. So a scenario can pin
// the altitude -> temperature curve end-to-end against a running game (warm ~2000 K near the horizon,
// neutral ~5772 K with the sun high) rather than only via the offline SkyColorTemperatureTests.
public sealed class SkyColorTemperatureProbe : IProbe
{
    public string Name => "sky_color_temperature";

    public float Read(Map map)
    {
        float elevation = SolarPosition.ElevationForMap(map);
        // Every input the patch feeds the curve, read the same way through the same helpers, so a
        // scenario can never observe a temperature the sky is not actually being tinted toward:
        //   * the §20 site air column, so a mountain scenario probes the whiter horizon endpoint
        //     (~3416 K at 4000 m) rather than the sea-level 2000 K;
        //   * the §20b aerosol load, so a polluted-lowland scenario probes the warmer endpoint the
        //     haze pushes it to (1500 K at pollution 1.0 on a sea-level tile) — and, on a mountain,
        //     probes the fact that the aerosol column has already fallen away beneath it;
        //   * the §18 vacuum gate, so an orbital scenario probes the flat unreddened anchor
        //     (5772 K at every elevation) rather than the ground ramp.
        return SkyColorTemperature.ColorTemperatureKelvin(
            elevation,
            SiteAltitude.PressureFractionForMap(map),
            SiteAltitude.AerosolFractionForMap(map),
            Vacuum.InVacuumForMap(map));
    }
}
