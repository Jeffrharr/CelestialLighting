using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool. See
// RimWorldTestHarness/DESIGN.md's "Where probe tests live" for the full reasoning.
//
// Reads back PenumbraMath.PenumbraSoftness for the live map's current Sun elevation — the exact pure
// value Patch_ShadowStrength uses to soften shadow opacity (via PenumbraContrastFactor, which is a
// fixed function of this softness). Feeding SolarPosition.ElevationForMap through PenumbraSoftness
// here mirrors the patch's own computation, so a scenario can pin the angular-size penumbra end-to-end
// against a real running game rather than only in the offline PenumbraMathTests.
public sealed class PenumbraProbe : IProbe
{
    public string Name => "penumbra_softness";

    public float Read(Map map)
    {
        float elevation = SolarPosition.ElevationForMap(map);
        return PenumbraMath.PenumbraSoftness(elevation);
    }
}
