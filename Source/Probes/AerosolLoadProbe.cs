using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj —
// the shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// The raw aerosol column fraction the sky patch is actually using — §20b's pollution term and §20e's
// rainfall-keyed background, already summed, already through §20c's day-to-day drift. Read through
// the same SiteAltitude accessor Patch_SkyColorTemperature calls, per §18's rule that a probe reads
// the value its patch reads.
//
// This is issue #92's headline claim made numeric rather than only visual: before §20e this probe
// would have read exactly 0 on every pollution-0 tile, which is most of them. Pair it with
// sky_red_blue_ratio (the colour that fraction produces) the same way aerosol_angstrom_exponent pairs
// with it for §20d — the two aerosol subsystems fail independently, and a scenario needs a way to
// tell "the load never arrived" apart from "the load arrived but the colour it produced is wrong".
public sealed class AerosolLoadProbe : IProbe
{
    public string Name => "aerosol_load_fraction";

    public float Read(Map map) => SiteAltitude.AerosolFractionForMap(map);
}
