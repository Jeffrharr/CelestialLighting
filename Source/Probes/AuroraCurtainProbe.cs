using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// Reads back exactly the curtain alpha Patch_AuroraCurtainDraw hands the overlay, by calling the same
// shared AuroraConditions.CurrentCurtainStrength the patch uses — so a live scenario can pin §11a
// end-to-end against a real running game (0 with no driver, 0 in daylight, 0 with the feature off,
// ramping up during a night-time aurora) instead of only the offline AuroraCurtainTests. Mirrors
// AuroraTintProbe, which does the same job for §11's flat tint.
public sealed class AuroraCurtainProbe : IProbe
{
    public string Name => "aurora_curtain";

    public float Read(Map map) => AuroraConditions.CurrentCurtainStrength(map);
}
