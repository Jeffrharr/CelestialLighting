using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj —
// the shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// Reads back exactly the sky-tint strength Patch_AuroraTint applies, by calling the same shared
// AuroraConditions.CurrentSkyTintStrength the patch uses — so a live scenario can pin the aurora
// tint end-to-end against a real running game (0 with no flare / in daylight, ramping up during a
// night-time solar flare) instead of only the offline AuroraMathTests. This mirrors how
// ShadowLeanProbe re-derives Formulas.SolarDeclinationDegrees for the live clock.
public sealed class AuroraTintProbe : IProbe
{
    public string Name => "aurora_tint";

    public float Read(Map map) => AuroraConditions.CurrentSkyTintStrength(map);
}
