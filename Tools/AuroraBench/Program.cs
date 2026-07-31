using System;
using System.Diagnostics;
using CelestialLighting;

namespace CelestialLighting.Tools;

// Splits §11a's per-frame cost into its PURE parts and times each one separately, so issue #60's
// "it is not bake" can be checked against a number rather than against an A/B that moved by 2 us.
//
// Deliberately NOT the same measurement as AuroraCurtainCostProbe: that probe times one amortised
// slice of the bake, in-game, under Mono. This times every pure stage of the frame path separately,
// offline, under the .NET 8 JIT. The absolute numbers are therefore optimistic (Mono is materially
// slower) but the SHARES are what the issue is asking about, and a stage that is 2 us here cannot be
// 400 us there.
internal static class Program
{
    private const int Warmup = 2000;
    private const int Runs = 20000;

    private static void Main()
    {
        AuroraFieldSpec spec = AuroraFieldRegistry.Active;
        int w = spec.ResolutionX;
        int h = spec.ResolutionY;
        int rows = spec.RefreshRows;
        int slices = (h + rows - 1) / rows;

        Console.WriteLine($"field={spec.Name} {w}x{h} refreshRows={rows} slices/sweep={slices} " +
                          $"pixels={spec.PixelCount / 1024} KiB");
        Console.WriteLine();

        byte[] pixels = new byte[spec.PixelCount];
        AuroraCurtainHemRays.ColumnTable table =
            AuroraCurtainHemRays.BuildColumnTable(null, w, 0f);

        // --- bake: the per-sweep column table -----------------------------------------------
        double tableUs = Time(Runs / 20, Warmup / 20, i =>
            table = AuroraCurtainHemRays.BuildColumnTable(table, w, i * 6f));

        // --- bake: one slice of rows from that table ----------------------------------------
        double sliceUs = Time(Runs, Warmup, i =>
            AuroraCurtainHemRays.FillRows(
                pixels, table, w, h, i % slices * rows, rows,
                0.2f, 0.9f, 0.3f, spec.TintWeight));

        // --- bake: a whole sweep, table included, exactly as the adapter amortises it -------
        double bakePerTickUs = sliceUs + tableUs / slices;

        // --- per-frame: the display schedule ------------------------------------------------
        AuroraDisplay[] live = new AuroraDisplay[AuroraDisplays.MaxLive];
        int liveTotal = 0;
        double resolveUs = Time(Runs, Warmup, i =>
            liveTotal += AuroraDisplays.Resolve(12345, i * 7, live));

        // --- per-frame: placement math for every live display -------------------------------
        int n = AuroraDisplays.Resolve(12345, 5000, live);
        double placeUs = Time(Runs, Warmup, i =>
        {
            float drift = AuroraCurtainHemRays.Oscillate(i * AuroraCurtainHemRays.DriftRate);
            for (int s = 0; s < n; s++)
            {
                AuroraSheetPlacement home = AuroraSheetLayout.RandomPlacement(
                    live[s].Seed, 250, 250, live[s].Slot, AuroraDisplays.MaxLive);
                AuroraSheetPlacement p = AuroraSheetLayout.WithDrift(home, drift * home.UScale);
                Sink += p.CenterX + p.Alpha;
            }
        });

        // Placement in the shipping path is cached per display (HomeFor), so the steady-state cost
        // is WithDrift only. Timed separately because that is the number the frame actually pays.
        double driftOnlyUs = Time(Runs, Warmup, i =>
        {
            float drift = AuroraCurtainHemRays.Oscillate(i * AuroraCurtainHemRays.DriftRate);
            for (int s = 0; s < n; s++)
            {
                AuroraSheetPlacement p = AuroraSheetLayout.WithDrift(live[s].Slot == 0
                    ? Home0 : Home0, drift);
                Sink += p.CenterX;
            }
        });

        Console.WriteLine($"BAKE (once per TICK, gated by _lastBakedTick)");
        Console.WriteLine($"  BuildColumnTable (once per {slices}-slice sweep) : {tableUs,8:F2} us");
        Console.WriteLine($"  FillRows, {rows} rows x {w} px                     : {sliceUs,8:F2} us");
        Console.WriteLine($"  => amortised bake per tick                     : {bakePerTickUs,8:F2} us");
        Console.WriteLine($"  => a whole sweep ({slices} slices + 1 table)        : " +
                          $"{sliceUs * slices + tableUs,8:F2} us");
        Console.WriteLine();
        Console.WriteLine($"PER FRAME (ungated, runs every rendered frame)");
        Console.WriteLine($"  AuroraDisplays.Resolve                         : {resolveUs,8:F2} us " +
                          $"({(double)liveTotal / Runs:F2} live avg)");
        Console.WriteLine($"  placement, cold  (RandomPlacement + WithDrift) : {placeUs,8:F2} us " +
                          $"for {n} displays");
        Console.WriteLine($"  placement, warm  (WithDrift only, the real path): {driftOnlyUs,8:F2} us " +
                          $"for {n} displays");
        Console.WriteLine();
        Console.WriteLine($"  => total PURE per-frame work                   : " +
                          $"{resolveUs + driftOnlyUs,8:F2} us");
        Console.WriteLine($"  sink {Sink:F3}");
    }

    private static readonly AuroraSheetPlacement Home0 =
        AuroraSheetLayout.RandomPlacement(1, 250, 250, 0, AuroraDisplays.MaxLive);

    public static double Sink;

    private static double Time(int runs, int warmup, Action<int> body)
    {
        for (int i = 0; i < warmup; i++)
            body(i);

        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < runs; i++)
            body(i);
        watch.Stop();

        return watch.ElapsedTicks * 1_000_000.0 / Stopwatch.Frequency / runs;
    }
}
