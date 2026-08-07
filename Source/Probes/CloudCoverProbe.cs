using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// The raw §22 cloud fraction, read through the exact same CloudCoverClock accessor both
// Patch_CloudCoverSky and Patch_CloudCoverLabel call, per §18's rule that a probe reads the value its
// patch reads. Unconditional — CloudCoverClock has no opinion on the current weather, so a scenario
// pairs this with SetWeather ("Clear") to see the value the patches actually act on, and can also read
// it under a non-Clear weather to confirm the number keeps drifting underneath even while nothing
// currently consumes it.
//
// Deliberately reports the FRACTION, not the rendered sky colour. Patch_CloudCoverSky's colour is the
// product of §2, §8, §11 and §12 all lerping the same struct on top of it, so asserting on the colour
// would couple this scenario to every other subsystem's tuning; the fraction is §22's own contribution
// alone, the same reasoning WeatherDimmingProbe gives for its own fraction-not-colour choice.
public sealed class CloudCoverProbe : IProbe
{
    public string Name => "cloud_cover_fraction";

    public float Read(Map map) => CloudCoverClock.FractionForMap(map);
}
