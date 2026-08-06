using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj —
// the shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// The §20c Angstrom exponent this map's tile resolves to: how wavelength-selective its aerosol is,
// i.e. what size the particles are. Read through the same SiteAltitude accessor
// Patch_SkyColorTemperature calls, per §18's rule that a probe reads the value its patch reads.
//
// This is the INPUT half of §20c. It is worth probing separately from the resulting colour because
// the two can fail independently and a scenario needs to tell them apart: a desert map reading 0.2
// here with an unchanged sky says the keying works and the spectrum is not being applied, while a
// desert map reading 1.3 says worldgen handed us a rainfall we did not expect. Pair it with
// sky_red_blue_ratio, which reads the output half.
public sealed class AerosolAngstromProbe : IProbe
{
    public string Name => "aerosol_angstrom_exponent";

    public float Read(Map map) => SiteAltitude.AngstromExponentForMap(map);
}
