using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj —
// the shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool. See
// RimWorldTestHarness/DESIGN.md's "Where probe tests live" for the full reasoning.
//
// Reads back §21's cavity gain for the live map — the same number SurfaceBuildup.CavityGainFor
// hands to both the night floor (§7) and the daytime dimming recovery (§13). Screenshots alone
// can't distinguish "the ramp produced no lift" from "the lift is real but too small for CIELAB
// ΔE to clear noise at this map's areal dilution" — this probe answers that directly, in the same
// units DESIGN.md §21 quotes (1.0 = no lift, matching pre-§21 behaviour bit-for-bit).
public sealed class SurfaceCavityGainProbe : IProbe
{
    public string Name => "surface_cavity_gain";

    public float Read(Map map) => SurfaceBuildup.CavityGainFor(map);
}
