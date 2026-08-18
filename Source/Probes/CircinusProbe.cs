using System;
using System.Reflection;
using HarmonyLib;
using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Performance measured through Circinus (Workshop 3773680130, `astryl.Circinus`) rather than through
// Dubs Performance Analyzer, for one reason: it reports CALL COUNTS, and a call count is the number
// the Dubs windows could not give us.
//
// WHAT WENT WRONG WITHOUT IT. §27 phase 3 does all of its work inside a section regenerate. The Dubs
// `mask` window reported 0.3165 ms/frame against the gated window's 1.1273 — a threefold win — while
// `Patch_VectorLightSuppress:Postfix` was simply ABSENT from that window's table, as was
// Patch_IndoorSkyOcclusion on the same method. Two unrelated regenerate patches both missing means
// no regenerate ran, so the window timed the feature doing nothing and reported it as fast. Only two
// rows were prefix-filtered out of thirty-six, so the absence was real rather than a filter artifact
// — but establishing that took reading RowsBeforeFilter against RowsMatched, which is exactly the
// forensics a call count makes unnecessary.
//
// So the metric that matters here is Calls, and the timings are only meaningful once it is non-zero.
// A scenario pinning AvgMs without also pinning Calls can still be measuring an idle window.
//
// ARMED BY HAND, NOT BY CIRCINUS'S CURATED LIST. Circinus instruments ~35 curated targets by default
// and `SectionLayer_LightingOverlay.Regenerate` is not among them, which is correct for its own
// purpose and useless for ours. Instrumenter.ArmMethod takes any MethodBase, so we name the method we
// care about instead of hoping it is on somebody's list.
//
// WHY THE VANILLA METHOD AND NOT OUR POSTFIX. Arming `Regenerate` measures the whole bake including
// our postfix, so the cost of the mask is the DIFFERENCE between the same row with the feature on and
// off. That is the ratio-between-builds comparison Dubs' own report notes recommend, and it avoids
// asking a profiler to instrument a Harmony patch method, which is a stranger thing to do than it
// sounds.
//
// ENTIRELY OPTIONAL. Circinus is not a dependency and is not in About.xml — with the mod absent every
// type lookup returns null, Available reads 0, and the timing metrics read 0 rather than throwing.
// A scenario that wants these numbers pins circinus_available at 1 first, exactly as the shader and
// per-emitter-glow probes do, so a run without the mod fails loudly instead of reporting zeros.
public sealed class CircinusProbe : IProbe
{
    public enum Metric
    {
        // Whether Circinus is loaded and its profiler API resolved. Pin this at 1 in any arm that
        // claims to measure performance through it.
        Available,

        // How many measurement cycles the profiler has seen. Circinus's ring holds 2000; a window
        // that never filled is a window whose percentages mean less than they look like they do.
        Cycles,

        // How many times the armed method was entered. THE ONE THAT MATTERS: everything else here
        // is meaningless while this is zero, and zero is what the Dubs run reported without saying so.
        Calls,

        // Mean and worst milliseconds per cycle across the window.
        AvgMs,
        MaxMs,

        // Total milliseconds spent inside the method across the window. THE ONE TO COMPARE ARMS ON:
        // a tick budget does not produce the same number of regenerates twice — FastForward overruns
        // by up to about a tenth — so AvgMs per cycle silently mixes "cheaper per call" with "called
        // fewer times". TotalMs divided by Calls is a per-call cost that survives that.
        TotalMs,

        // Peak entries in a single cycle — the shape behind the mean, and what says whether a bake
        // is one big spike or a steady drip.
        MaxCallsPerCycle,

        // Whether the target is still instrumented. Circinus sheds instrumentation on its own
        // schedule — ArmingPlan, CorpusPreShed and ProfilerRegistry.DisarmedBelowFloor all exist to
        // take methods back out — so an arm measured after a shed reads zero calls and looks exactly
        // like a window in which nothing happened. Pin this at 1 next to any timing.
        Patched,

        // Zeroes the armed method's counters and reads back 0.
        //
        // A PROBE USED AS AN ACTION, which is not what probes are for and is done anyway because the
        // scenario language has no other lever. CollectStatistics accumulates across Circinus's whole
        // 2000-cycle ring, so a scenario measuring two arms in one run would report the second arm's
        // numbers with the first arm's still inside them — the mask would inherit the crossfade's
        // bake and the comparison would be meaningless in the direction that flatters whichever arm
        // ran second. A Probe step on this between arms is the barrier that stops it.
        Reset,
    }

    private static readonly Type InstrumenterType =
        AccessTools.TypeByName("Circinus.Profiling.Instrumenter");

    private static readonly Type RegistryType =
        AccessTools.TypeByName("Circinus.Profiling.ProfilerRegistry");

    private static readonly MethodInfo ArmMethodInfo =
        InstrumenterType == null ? null : AccessTools.Method(InstrumenterType, "ArmMethod");

    private static readonly MethodInfo DrainArmsInfo =
        InstrumenterType == null ? null : AccessTools.Method(InstrumenterType, "DrainArms");

    private static readonly MethodInfo IsPatchedInfo =
        InstrumenterType == null ? null : AccessTools.Method(InstrumenterType, "IsPatched");

    private static readonly MethodInfo FindInfo =
        RegistryType == null ? null : AccessTools.Method(RegistryType, "Find");

    private static readonly FieldInfo EnabledField =
        RegistryType == null ? null : AccessTools.Field(RegistryType, "Enabled");

    private static readonly FieldInfo RecordingField =
        RegistryType == null ? null : AccessTools.Field(RegistryType, "Recording");

    private static readonly PropertyInfo CycleCountProp =
        RegistryType == null ? null : AccessTools.Property(RegistryType, "CycleCount");

    public static bool Available =>
        ArmMethodInfo != null && FindInfo != null && EnabledField != null && RecordingField != null;

    private readonly Metric metric;
    private readonly string typeName;
    private readonly string methodName;

    private MethodBase target;
    private bool armed;

    public string Name { get; }

    public CircinusProbe(string name, Metric metric, string typeName = null, string methodName = null)
    {
        Name = name;
        this.metric = metric;
        this.typeName = typeName;
        this.methodName = methodName;
    }

    public float Read(Map map)
    {
        if (metric == Metric.Available)
            return Available ? 1f : 0f;

        if (!Available)
            return 0f;

        // Armed on first read rather than at registration. Circinus resolves its own types during
        // startup and arming transpiles the target, neither of which should happen while the mod
        // list is still loading — and a probe's first read is the earliest moment a scenario has
        // definitely finished setting the world up.
        EnsureArmed();

        if (metric == Metric.Cycles)
            return CycleCount();

        // Re-armed on every read rather than once. Circinus takes instrumentation back out on its
        // own schedule, and a silently shed method reports zero calls — the same reading as an idle
        // window, which is the confusion this whole probe exists to end.
        Rearm();

        if (metric == Metric.Patched)
            return IsPatched() ? 1f : 0f;

        if (metric == Metric.Reset)
            return ResetCounters();

        return Statistic();
    }

    private void EnsureArmed()
    {
        if (armed)
            return;

        armed = true;
        target = AccessTools.Method(AccessTools.TypeByName(typeName), methodName);

        if (target == null)
        {
            Log.Warning(
                "[CelestialLighting] Circinus probe could not resolve " + typeName + "." + methodName
                + "; its timings will read zero.");
            return;
        }

        try
        {
            // Recording before arming, so the first cycle after the transpile is already counted.
            EnabledField.SetValue(null, true);
            RecordingField.SetValue(null, true);

            ArmMethodInfo.Invoke(null, new object[] { target, "celestiallighting" });

            // Arms are queued and applied on Circinus's own update; draining here means the very
            // next frame is instrumented rather than whenever it next gets round to it.
            DrainArmsInfo?.Invoke(null, null);
        }
        catch (Exception ex)
        {
            Log.Warning("[CelestialLighting] Circinus probe could not arm " + methodName + ": " + ex.Message);
            target = null;
        }
    }

    private bool IsPatched()
    {
        if (target == null || IsPatchedInfo == null)
            return false;

        return Convert.ToBoolean(IsPatchedInfo.Invoke(null, new object[] { target }));
    }

    private void Rearm()
    {
        if (target == null || IsPatched())
            return;

        try
        {
            EnabledField.SetValue(null, true);
            RecordingField.SetValue(null, true);
            ArmMethodInfo.Invoke(null, new object[] { target, "celestiallighting" });
            DrainArmsInfo?.Invoke(null, null);
        }
        catch (Exception)
        {
            // Left to the Patched metric to report rather than logged per read.
        }
    }

    private float ResetCounters()
    {
        if (target == null)
            return 0f;

        object profiler = FindInfo.Invoke(null, new object[] { target });
        AccessTools.Method(profiler?.GetType(), "Reset")?.Invoke(profiler, null);
        return 0f;
    }

    private float CycleCount()
    {
        object value = CycleCountProp?.GetValue(null);
        return value == null ? 0f : Convert.ToSingle(value);
    }

    private float Statistic()
    {
        if (target == null)
            return 0f;

        object profiler = FindInfo.Invoke(null, new object[] { target });

        // Null rather than zero-valued when the method has been armed but never entered, which is
        // the case this whole probe exists to make visible.
        if (profiler == null)
            return 0f;

        MethodInfo collect = AccessTools.Method(profiler.GetType(), "CollectStatistics");

        if (collect == null)
            return 0f;

        // CollectStatistics(int cycles, out double average, out double max, out double total,
        //                   out long calls, out long maxCalls)
        object[] args = { (int)CycleCount(), 0.0, 0.0, 0.0, 0L, 0L };
        collect.Invoke(profiler, args);

        switch (metric)
        {
            case Metric.Calls:
                return Convert.ToSingle(args[4]);
            case Metric.MaxCallsPerCycle:
                return Convert.ToSingle(args[5]);
            case Metric.MaxMs:
                return Convert.ToSingle(args[2]);
            case Metric.TotalMs:
                return Convert.ToSingle(args[3]);
            default:
                return Convert.ToSingle(args[1]);
        }
    }
}
