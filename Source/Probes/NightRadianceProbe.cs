using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj —
// the shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool. See
// RimWorldTestHarness/DESIGN.md's "Where probe tests live" for the full reasoning.
//
// Reads back the night-floor value Patch_NightRadiance blends the sky toward for the live tile/tick,
// honoring the live NightRadianceSettings (so a scenario can flip AtmosphericGlowEnabled off and
// assert the floor collapses toward pitch black). This is what lets a live scenario pin the emergent
// night glow end-to-end against a real running game rather than only the offline unit tests.
//
// IT CALLS THE SHARED ADAPTER, and that is load-bearing rather than tidiness. This probe used to
// re-derive the floor itself — same settings reads, same moon-seam read, same source-sum, written out
// a second time. That was already a latent bug (a probe that agrees with the patch only for as long
// as someone maintains two copies) and §18b made it an actual one: the vacuum substitutions live
// inside NightRadiance.FloorGlowFor, so a hand-rolled copy would have gone on cheerfully reporting
// the sea-level 0.04 on an orbital platform and the live scenario would have "confirmed" a floor the
// game was not using. Measuring through the same function the game measures through is the entire
// value of a probe.
public sealed class NightRadianceProbe : IProbe
{
    public string Name => "night_radiance";

    public float Read(Map map) => NightRadiance.FloorGlowFor(map);
}
