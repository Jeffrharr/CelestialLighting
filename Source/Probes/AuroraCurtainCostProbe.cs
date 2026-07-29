using System.Diagnostics;
using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead.
//
// Measures what one frame's worth of §11a curtain regeneration actually costs, in microseconds, under
// the real Mono runtime — which is the whole point. Every performance number in AuroraCurtainOverlay's
// comments came from a .NET 8 benchmark, and RimWorld runs a materially slower runtime, so those figures
// establish the shape of the curve but not the budget. This closes that gap with a measurement.
//
// Times AuroraCurtain.FillRows directly, following SectionRegenerateTimingProbe's pattern: the probe
// drives the work itself rather than the shipped assembly carrying stopwatch instrumentation it would
// otherwise never use. It reads Resolution and RowsPerUpdate off AuroraCurtainOverlay rather than
// restating them, so it can only ever report the cost of the slice size actually shipped.
//
// Note this is deliberately the cost DURING an aurora. The far more important number — what §11a costs
// the other 99% of the time — is structural rather than measurable: Patch_AuroraCurtainDraw early-outs on
// a null driver before anything is allocated, so with no aurora running the answer is one null check.
public sealed class AuroraCurtainCostProbe : IProbe
{
    private const int WarmupRuns = 8;
    private const int TimedRuns = 200;

    public string Name => "aurora_curtain_cost";

    public float Read(Map map)
    {
        int side = AuroraCurtainOverlay.Resolution;
        int rows = AuroraCurtainOverlay.RowsPerUpdate;
        byte[] buffer = new byte[side * side * 4];

        // Advance both the time and the row cursor across runs exactly as the live refresh does. Holding
        // either fixed would let the JIT and the data cache flatter the measurement in ways the real
        // rolling refresh never enjoys.
        for (int i = 0; i < WarmupRuns; i++)
            Fill(buffer, side, rows, i);

        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < TimedRuns; i++)
            Fill(buffer, side, rows, WarmupRuns + i);
        watch.Stop();

        // Stopwatch ticks are not microseconds on every platform, hence the explicit frequency
        // conversion rather than a hardcoded divisor (same reason SectionRegenerateTimingProbe does it).
        double microsPerFrame = watch.ElapsedTicks * 1_000_000.0 / Stopwatch.Frequency / TimedRuns;
        return (float)microsPerFrame;
    }

    private static void Fill(byte[] buffer, int side, int rows, int iteration)
    {
        AuroraCurtain.FillRows(
            buffer, side, side,
            firstRow: iteration * rows % side,
            rowCount: rows,
            time: iteration * 60f,
            tintR: 0.2f, tintG: 0.9f, tintB: 0.3f,
            tintWeight: AuroraCurtain.DriverTintWeight);
    }
}
