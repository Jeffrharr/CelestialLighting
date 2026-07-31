using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead.
//
// ================================================================================================
// WHAT THIS MEASURES THAT aurora_curtain_cost CANNOT
//
// aurora_curtain_cost drives AuroraCurtainHemRays.FillRows itself, in a loop, and reports how long
// ONE amortised bake slice takes. That is a per-CALL number and it is blind to the only question
// issue #60 is actually asking, which is a per-FRAME one: how often is each stage entered, and what
// does the whole postfix cost on a frame where nothing bakes at all?
//
// So this probe measures the SHIPPED path in place rather than a re-driven copy of it. Every stage
// of Patch_AuroraCurtainDraw's call tree is Harmony-timed where it really runs, with its call count
// alongside, and the probe reports microseconds PER FRAME — directly comparable to the 0.451 ms/frame
// Dubs Performance Analyzer attributed to the postfix.
//
// The patches are installed from OUT HERE, exactly as GeometryEvalCountProbe's are, so the shipped
// assembly never carries stopwatch instrumentation it would only use during a measurement pass.
//
// ================================================================================================
// INCLUSIVE, NOT EXCLUSIVE
//
// Each stage's number includes its children (advance contains bake, place and upload; total contains
// everything). Subtracting is left to the reader rather than done here, because the alternative — a
// per-thread stack that charges each tick to the innermost open stage — is more instrumentation than
// the thing being instrumented, and the tree here is shallow enough to subtract by hand.
//
// ================================================================================================
// READ aurora_path_overhead_us BEFORE READING ANYTHING ELSE
//
// Harmony's own prefix/postfix dispatch plus two Stopwatch reads is not free, and the cheap stages
// here (SetSheet, DrawSheet) are individually smaller than a microsecond. The calibration stage is a
// genuinely empty method patched by the same machinery and called in a loop, so
// `stage_us - overhead_us * stage_calls_per_frame` is the honest correction, and a stage whose
// reported cost is near its call count times the overhead measured nothing but this probe.
public static class AuroraPathTimers
{
    // Stage names double as the dictionary key, because Harmony hands the postfix the original
    // MethodBase and every method timed here has a distinct name. Keying on the name rather than on
    // MethodBase identity avoids depending on reflection handing back the same MethodInfo instance
    // the patch was installed against.
    public const string Total = "Postfix";
    // The arithmetic half of the strength calculation. Was CurrentCurtainStrength, which the draw
    // path no longer calls: it resolves the driver itself now and hands it down, so the lookup and
    // the arithmetic are separately timed rather than nested.
    public const string Strength = "CurtainStrengthFor";
    public const string Driver = "ActiveTintDriver";
    public const string Advance = "Advance";
    public const string Bake = "Regenerate";
    public const string Upload = "Upload";
    public const string Place = "PlaceSheets";
    public const string Draw = "DrawOverlay";
    public const string SetSheet = "SetSheet";
    public const string DrawSheet = "DrawSheet";

    // The two halves of a bake, timed separately because they run on different schedules: FillRows
    // on every baked tick, BuildColumnTable only when the cursor returns to the bottom. Any spike
    // that is one and not the other is the difference between a hot loop and a once-per-sweep table.
    public const string Table = "BuildColumnTable";
    public const string FillRows = "FillRows";

    public const string Calibration = "CalibrationNoOp";

    private static readonly Dictionary<string, int> Index = new Dictionary<string, int>();
    private static readonly List<string> Order = new List<string>();

    private static long[] ticks = new long[0];
    private static int[] calls = new int[0];

    // Worst single call per stage. The mean says what the aurora costs; this says what it does to
    // ONE frame, which is the half of a stutter a player actually notices and the half Dubs reports
    // as "Max For Frame".
    private static long[] maxTicks = new long[0];

    private static bool installed;

    // Stages whose target method could not be resolved. Reported rather than silently zero: a stage
    // that was never patched and a stage that was never entered are different findings, and only one
    // of them is a fact about the aurora.
    private static readonly List<string> Missing = new List<string>();

    public static void Install()
    {
        if (installed)
            return;

        installed = true;
        Harmony harmony = new Harmony("celestiallighting.probes.aurorapathtiming");

        Type overlay = typeof(AuroraCurtainOverlay);
        Type conditions = typeof(AuroraConditions);
        Type draw = AccessTools.TypeByName("CelestialLighting.Patch_AuroraCurtainDraw");

        Time(harmony, Total, AccessTools.Method(draw, "Postfix"));
        Time(harmony, Strength, AccessTools.Method(conditions, "CurtainStrengthFor"));
        Time(harmony, Driver, AccessTools.Method(conditions, "ActiveTintDriver"));
        Time(harmony, Advance, AccessTools.Method(overlay, "Advance"));
        Time(harmony, Bake, AccessTools.Method(overlay, "Regenerate"));
        Time(harmony, Upload, AccessTools.Method(overlay, "Upload"));
        Time(harmony, Place, AccessTools.Method(overlay, "PlaceSheets"));
        Time(harmony, Draw, AccessTools.Method(overlay, "DrawOverlay"));
        Time(harmony, SetSheet, AccessTools.Method(overlay, "SetSheet"));
        Time(harmony, DrawSheet, AccessTools.Method(overlay, "DrawSheet"));
        // FillRows is overloaded — one takes a prebuilt ColumnTable, one builds the columns itself —
        // and only the table overload is on the shipping path. Selected by signature rather than by
        // name so a build that reordered the two cannot silently time the wrong one.
        Time(harmony, Table, AccessTools.Method(typeof(AuroraCurtainHemRays), "BuildColumnTable"));
        Time(harmony, FillRows, TableFillRows());

        Time(harmony, Calibration, AccessTools.Method(typeof(AuroraPathTimers), nameof(CalibrationNoOp)));

        ticks = new long[Order.Count];
        calls = new int[Order.Count];
        maxTicks = new long[Order.Count];
    }

    private static MethodBase TableFillRows()
    {
        foreach (MethodInfo m in typeof(AuroraCurtainHemRays).GetMethods(
                     BindingFlags.Public | BindingFlags.Static))
        {
            ParameterInfo[] ps = m.GetParameters();
            if (m.Name == FillRows && ps.Length > 1
                && ps[1].ParameterType == typeof(AuroraCurtainHemRays.ColumnTable))
                return m;
        }

        return null;
    }

    private static void Time(Harmony harmony, string stage, MethodBase target)
    {
        Index[stage] = Order.Count;
        Order.Add(stage);

        if (target == null)
        {
            Missing.Add(stage);
            return;
        }

        harmony.Patch(
            target,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(AuroraPathTimers), nameof(Pre))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(AuroraPathTimers), nameof(Post))));
    }

    public static void Pre(out long __state) => __state = Stopwatch.GetTimestamp();

    public static void Post(long __state, MethodBase __originalMethod)
    {
        long elapsed = Stopwatch.GetTimestamp() - __state;

        if (!Index.TryGetValue(__originalMethod.Name, out int i) || i >= ticks.Length)
            return;

        ticks[i] += elapsed;
        calls[i]++;

        if (elapsed > maxTicks[i])
            maxTicks[i] = elapsed;
    }

    // Patched by the same machinery as everything else and does nothing, so timing it in a loop
    // measures the instrumentation and only the instrumentation.
    public static void CalibrationNoOp()
    {
    }

    public static void Reset()
    {
        for (int i = 0; i < ticks.Length; i++)
        {
            ticks[i] = 0;
            calls[i] = 0;
            maxTicks[i] = 0;
        }
    }

    public static double MaxMicrosFor(string stage) =>
        Index.TryGetValue(stage, out int i) && i < maxTicks.Length
            ? maxTicks[i] * 1_000_000.0 / Stopwatch.Frequency
            : 0.0;

    public static double MicrosFor(string stage) => TicksFor(stage) * 1_000_000.0 / Stopwatch.Frequency;

    public static long TicksFor(string stage) =>
        Index.TryGetValue(stage, out int i) && i < ticks.Length ? ticks[i] : 0L;

    public static int CallsFor(string stage) =>
        Index.TryGetValue(stage, out int i) && i < calls.Length ? calls[i] : 0;

    // Removes work this probe's own calibration loop charged to a stage, so measuring the
    // instrumentation does not become part of the window being measured.
    public static void Discount(string stage, long elapsedTicks, int elapsedCalls)
    {
        if (!Index.TryGetValue(stage, out int i) || i >= ticks.Length)
            return;

        ticks[i] -= elapsedTicks;
        calls[i] -= elapsedCalls;
    }

    public static int MissingCount => Missing.Count;

    // Frames on which the postfix did real work. Advance is entered exactly once per drawn frame on
    // the map's own manager — the second, world-level call bails before reaching it — so this is the
    // frame count every per-frame figure is divided by, and it is counted rather than assumed.
    public static int Frames => CallsFor(Advance);
}

// One metric per registered probe name, because the harness's Probe step reads a single float.
public sealed class AuroraPathTimingProbe : IProbe
{
    public enum Metric
    {
        // Wall-clock microseconds per drawn frame, for one stage. The headline numbers.
        TotalUsPerFrame,
        StrengthUsPerFrame,
        DriverUsPerFrame,
        AdvanceUsPerFrame,
        BakeUsPerFrame,
        UploadUsPerFrame,
        PlaceUsPerFrame,
        DrawUsPerFrame,
        SetSheetUsPerFrame,
        DrawSheetUsPerFrame,

        // How often each stage is entered per drawn frame. This is the half aurora_curtain_cost
        // structurally cannot see, and the half issue #60 says decides everything.
        DriverCallsPerFrame,
        BakeCallsPerFrame,
        UploadCallsPerFrame,
        SetSheetCallsPerFrame,
        DrawSheetCallsPerFrame,

        // Microseconds for ONE bake, so the per-frame figure above can be read as
        // "cost of a bake x how often we bake" rather than as an opaque product.
        BakeUsPerCall,

        // The bake's two halves, separately. FillRows runs on every baked tick; BuildColumnTable
        // only on the 1-in-32 tick where the refresh cursor wraps. Which of them dominates the worst
        // frame is the whole question behind issue #60's "Max For Frame" paragraph.
        TableUsPerCall,
        TableCallsPerFrame,
        FillRowsUsPerCall,
        UploadUsPerCall,

        // Worst SINGLE call seen since the reset — the stutter, not the average. TotalUsMax is the
        // direct analogue of Dubs' "Max For Frame" column.
        TotalUsMax,
        BakeUsMax,
        TableUsMax,
        UploadUsMax,

        // Frames accumulated since the last reset. A sanity denominator: every figure above is
        // meaningless if this is small, and it also reveals whether the harness step being used
        // actually renders frames.
        Frames,

        // The instrumentation's own per-call cost, and the number of stages that failed to resolve.
        OverheadUs,
        MissingStages,

        // Side-effecting: zeroes every accumulator and returns 0, so a scenario can exclude warmup
        // and JIT from the window it reports on.
        Reset,
    }

    private const int CalibrationRuns = 2000;

    private readonly Metric metric;

    public string Name { get; }

    public AuroraPathTimingProbe(string name, Metric metric)
    {
        Name = name;
        this.metric = metric;
    }

    public float Read(Map map)
    {
        if (metric == Metric.Reset)
        {
            AuroraPathTimers.Reset();
            return 0f;
        }

        if (metric == Metric.MissingStages)
            return AuroraPathTimers.MissingCount;

        if (metric == Metric.OverheadUs)
            return (float)MeasureOverhead();

        int frames = AuroraPathTimers.Frames;

        if (metric == Metric.Frames)
            return frames;

        switch (metric)
        {
            case Metric.BakeUsPerCall: return PerCall(AuroraPathTimers.Bake);
            case Metric.TableUsPerCall: return PerCall(AuroraPathTimers.Table);
            case Metric.FillRowsUsPerCall: return PerCall(AuroraPathTimers.FillRows);
            case Metric.UploadUsPerCall: return PerCall(AuroraPathTimers.Upload);
            case Metric.TotalUsMax: return (float)AuroraPathTimers.MaxMicrosFor(AuroraPathTimers.Total);
            case Metric.BakeUsMax: return (float)AuroraPathTimers.MaxMicrosFor(AuroraPathTimers.Bake);
            case Metric.TableUsMax: return (float)AuroraPathTimers.MaxMicrosFor(AuroraPathTimers.Table);
            case Metric.UploadUsMax: return (float)AuroraPathTimers.MaxMicrosFor(AuroraPathTimers.Upload);
        }

        if (frames <= 0)
            return 0f;

        switch (metric)
        {
            case Metric.TotalUsPerFrame: return PerFrame(AuroraPathTimers.Total, frames);
            case Metric.StrengthUsPerFrame: return PerFrame(AuroraPathTimers.Strength, frames);
            case Metric.DriverUsPerFrame: return PerFrame(AuroraPathTimers.Driver, frames);
            case Metric.AdvanceUsPerFrame: return PerFrame(AuroraPathTimers.Advance, frames);
            case Metric.BakeUsPerFrame: return PerFrame(AuroraPathTimers.Bake, frames);
            case Metric.UploadUsPerFrame: return PerFrame(AuroraPathTimers.Upload, frames);
            case Metric.PlaceUsPerFrame: return PerFrame(AuroraPathTimers.Place, frames);
            case Metric.DrawUsPerFrame: return PerFrame(AuroraPathTimers.Draw, frames);
            case Metric.SetSheetUsPerFrame: return PerFrame(AuroraPathTimers.SetSheet, frames);
            case Metric.DrawSheetUsPerFrame: return PerFrame(AuroraPathTimers.DrawSheet, frames);
            case Metric.DriverCallsPerFrame: return CallsPerFrame(AuroraPathTimers.Driver, frames);
            case Metric.BakeCallsPerFrame: return CallsPerFrame(AuroraPathTimers.Bake, frames);
            case Metric.UploadCallsPerFrame: return CallsPerFrame(AuroraPathTimers.Upload, frames);
            case Metric.SetSheetCallsPerFrame: return CallsPerFrame(AuroraPathTimers.SetSheet, frames);
            case Metric.DrawSheetCallsPerFrame: return CallsPerFrame(AuroraPathTimers.DrawSheet, frames);
            case Metric.TableCallsPerFrame: return CallsPerFrame(AuroraPathTimers.Table, frames);
            default: return 0f;
        }
    }

    private static float PerFrame(string stage, int frames) =>
        (float)(AuroraPathTimers.MicrosFor(stage) / frames);

    private static float CallsPerFrame(string stage, int frames) =>
        AuroraPathTimers.CallsFor(stage) / (float)frames;

    private static float PerCall(string stage)
    {
        int n = AuroraPathTimers.CallsFor(stage);
        return n <= 0 ? 0f : (float)(AuroraPathTimers.MicrosFor(stage) / n);
    }

    // Times the patched empty method through the same prefix/postfix pair every other stage goes
    // through, then subtracts its accumulator back out so the calibration loop does not pollute a
    // window the scenario is still measuring.
    private static double MeasureOverhead()
    {
        long before = AuroraPathTimers.TicksFor(AuroraPathTimers.Calibration);

        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < CalibrationRuns; i++)
            AuroraPathTimers.CalibrationNoOp();
        watch.Stop();

        double micros = watch.ElapsedTicks * 1_000_000.0 / Stopwatch.Frequency / CalibrationRuns;

        // The calibration stage is timed like any other, so its own loop would otherwise show up in
        // whatever window the scenario is accumulating. Only the delta this call added is removed.
        long after = AuroraPathTimers.TicksFor(AuroraPathTimers.Calibration);
        AuroraPathTimers.Discount(AuroraPathTimers.Calibration, after - before, CalibrationRuns);

        return micros;
    }
}
