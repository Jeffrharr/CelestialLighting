using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj —
// the shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool. See
// RimWorldTestHarness/DESIGN.md's "Where probe tests live" for the full reasoning.
//
// Reads back §26's boundary position for the live map, through the same TwilightSweep.PositionFor
// call the overlay uses, so the probe cannot report a sweep the screen did not get.
//
// THIS IS THE PROBE THAT MAKES A FILMED SWEEP FALSIFIABLE, which is a stronger claim than most
// probes here can make. §26's thesis is about MOTION, and motion is exactly what a still frame cannot
// show and what a video makes very easy to believe on no evidence: a clip of an evening getting
// darker looks like a sweep whether or not anything swept. Pinning this at two hours inside the
// window is what separates "the boundary crossed the map" from "the sky dimmed while a static
// gradient sat there" — and CLAUDE.md's own note about pixel centroids lying (a pawn in frame once
// inverted a whole sign) applies with full force to a moving edge.
public sealed class TwilightSweepProbe : IProbe
{
    public string Name => "twilight_sweep_position";

    public float Read(Map map) => TwilightSweep.PositionFor(map);
}
