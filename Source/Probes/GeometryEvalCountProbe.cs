using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorldTestHarness.Mod.Probes;
using UnityEngine;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see <Compile Remove> in CelestialLighting.csproj)
// and compiled into TestMod/CelestialLighting.Probes.csproj instead — the shipped mod must never take
// a hard reference on RimWorldTestHarness, which is a dev-only tool.
//
// WHAT THIS MEASURES. Issue #12 / PR #18 claims solar and lunar geometry collapse to *one evaluation
// per map per frame*. That claim is a count, not a duration, so this counts rather than times. The
// per-call cost of SolarPosition.InputsForMap is a WorldGrid.LongLatOf plus a few dozen flops — small
// enough that a stopwatch around it would be reporting its own overhead, and a whole-frame timer
// would bury it under the mesh work SectionRegenerateTimingProbe already measures. Counting is the
// direct test of what the PR actually asserts; a timing number here would be a less honest answer to
// a more impressive-sounding question.
//
// WHY IT PATCHES TWO LEVELS. A "call" is an entry into the public accessor — the fan-out the memo is
// meant to absorb, and the number that must NOT change (the memo is not allowed to make callers stop
// asking). An "evaluation" is the geometry actually being derived. On a build with the memo those are
// two different methods; without it they are the same method, so the un-memoized build counts every
// call as an evaluation and the two series coincide. That is the point: same instrument, both builds,
// and the before/after is visible as calls and evaluations separating.
//
// WHY IT PATCHES THE PRIVATE Compute* METHODS RATHER THAN A LEAF FORMULA. The obvious alternative —
// counting a pure function only the compute path calls, e.g. SunClockAdapter.EffectiveDayPercent — is
// vulnerable to Mono inlining it at the one call site, which would silently report zero evaluations
// and look like a spectacular win. The Compute* methods are invoked through a held delegate, so they
// cannot be inlined away, which makes them the sturdier target despite being private.
//
// WHY IT REPORTS OVER A WINDOW OF COMPLETED FRAMES. The probe itself runs inside a frame, so the
// frame it is read on is still accumulating. Counts are therefore banked per frame and reported over
// frames that have already ended. The mean says what a steady frame costs; the max says what the
// worst frame in the window cost, which is where the per-visible-section term (Patch_ShadowTilt) and
// any tick-boundary double-evaluation would show up. Both are reported because either alone can
// mislead: a mean hides spikes, a max hides that the spike was one frame in two hundred.
public sealed class GeometryEvalCountProbe : IProbe
{
    public enum Metric
    {
        SolarCallsMean,
        SolarCallsMax,
        SolarEvalsMean,
        SolarEvalsMax,
        MoonCallsMean,
        MoonCallsMax,
        MoonEvalsMean,
        MoonEvalsMax,
        FramesCounted,
        MemoPresent,
        MapsLoaded,
    }

    private readonly Metric metric;

    public string Name { get; }

    public GeometryEvalCountProbe(string name, Metric metric)
    {
        Name = name;
        this.metric = metric;
    }

    public float Read(Map map) => GeometryEvalCounters.Read(metric);
}

// The counting itself, plus the Harmony patches that feed it. Static because Harmony prefixes must
// be, and because there is exactly one count per game process no matter how many probes read it.
public static class GeometryEvalCounters
{
    // Four seconds of frames at 60fps. Long enough that a scenario can Wait out a steady state and
    // still have every frame of it in the window; short enough that a window opened by a reset does
    // not silently include frames from before the reset once it wraps.
    private const int WindowFrames = 512;

    private readonly struct Sample
    {
        public readonly int SolarCalls;
        public readonly int SolarEvals;
        public readonly int MoonCalls;
        public readonly int MoonEvals;

        public Sample(int solarCalls, int solarEvals, int moonCalls, int moonEvals)
        {
            SolarCalls = solarCalls;
            SolarEvals = solarEvals;
            MoonCalls = moonCalls;
            MoonEvals = moonEvals;
        }
    }

    private static readonly List<Sample> Completed = new List<Sample>(WindowFrames);

    private static int currentFrame;
    private static bool frameOpen;
    private static int solarCalls;
    private static int solarEvals;
    private static int moonCalls;
    private static int moonEvals;

    // True when the build under test has the memo, i.e. when the private compute methods exist and
    // were patched separately from the public accessors. Reported as its own probe so a report can
    // never be read as "the memo build evaluated once" when it was really the un-memoized build
    // being asked a question it cannot answer.
    private static bool memoPresent;

    private static bool installed;

    // Called once from ProbeRegistration's static constructor. Patching here rather than at the
    // first probe read matters: it lands before SolarPosition/MoonPosition are first touched, so the
    // static delegates those classes hold over their own compute methods are created against the
    // already-patched methods.
    public static void Install()
    {
        if (installed)
            return;

        installed = true;
        Harmony harmony = new Harmony("celestiallighting.probes.geometryevalcount");

        PatchCounter(harmony, AccessTools.Method(typeof(SolarPosition), "InputsForMap"), nameof(NoteSolarCall));
        PatchCounter(harmony, AccessTools.Method(typeof(MoonPosition), "SkyForMap"), nameof(NoteMoonCall));

        // Absent on a build without the memo — that is the "before" case, not an error, so it is a
        // null check rather than the hard failure PatchCounter raises.
        MethodInfo? solarCompute = AccessTools.Method(typeof(SolarPosition), "ComputeInputsForMap");
        MethodInfo? moonCompute = AccessTools.Method(typeof(MoonPosition), "ComputeSkyForMap");

        memoPresent = solarCompute != null && moonCompute != null;
        if (!memoPresent)
            return;

        PatchCounter(harmony, solarCompute, nameof(NoteSolarEval));
        PatchCounter(harmony, moonCompute, nameof(NoteMoonEval));
    }

    private static void PatchCounter(Harmony harmony, MethodInfo? target, string counterName)
    {
        if (target == null)
            throw new InvalidOperationException(
                $"GeometryEvalCountProbe could not resolve the method for counter '{counterName}'. "
                + "Failing loudly rather than reporting a zero that would read as a perfect memo.");

        harmony.Patch(target, prefix: new HarmonyMethod(
            AccessTools.Method(typeof(GeometryEvalCounters), counterName)));
    }

    // On a build without the memo there is no separate evaluation path, so an entry into the public
    // accessor IS an evaluation. Counting it as both here (rather than leaving evaluations at zero
    // and explaining it in prose) means the two builds' reports are directly comparable columns.
    public static void NoteSolarCall()
    {
        Advance();
        solarCalls++;
        if (!memoPresent)
            solarEvals++;
    }

    public static void NoteMoonCall()
    {
        Advance();
        moonCalls++;
        if (!memoPresent)
            moonEvals++;
    }

    public static void NoteSolarEval()
    {
        Advance();
        solarEvals++;
    }

    public static void NoteMoonEval()
    {
        Advance();
        moonEvals++;
    }

    // Banks the frame that just ended, if any, and opens the new one. Frames in which nothing was
    // counted at all are never banked — with a map loaded every frame reads geometry, so a gap means
    // the game was not running a map, not that a frame was free.
    private static void Advance()
    {
        int frame = Time.frameCount;
        if (frame == currentFrame && frameOpen)
            return;

        if (frameOpen)
            Bank();

        currentFrame = frame;
        frameOpen = true;
        solarCalls = 0;
        solarEvals = 0;
        moonCalls = 0;
        moonEvals = 0;
    }

    private static void Bank()
    {
        if (Completed.Count == WindowFrames)
            Completed.RemoveAt(0);

        Completed.Add(new Sample(solarCalls, solarEvals, moonCalls, moonEvals));
    }

    // Bridged to the harness as a SetFeature toggle so a scenario can open a fresh window
    // immediately before the segment it wants to characterize. Without it every reading would be a
    // mean over whatever the scenario happened to do earlier, and the steady-state and
    // section-regenerate segments could not be told apart.
    public static void Reset()
    {
        Completed.Clear();
        frameOpen = false;
        solarCalls = 0;
        solarEvals = 0;
        moonCalls = 0;
        moonEvals = 0;
    }

    public static float Read(GeometryEvalCountProbe.Metric metric)
    {
        switch (metric)
        {
            case GeometryEvalCountProbe.Metric.SolarCallsMean:
                return Mean(sample => sample.SolarCalls);
            case GeometryEvalCountProbe.Metric.SolarCallsMax:
                return Max(sample => sample.SolarCalls);
            case GeometryEvalCountProbe.Metric.SolarEvalsMean:
                return Mean(sample => sample.SolarEvals);
            case GeometryEvalCountProbe.Metric.SolarEvalsMax:
                return Max(sample => sample.SolarEvals);
            case GeometryEvalCountProbe.Metric.MoonCallsMean:
                return Mean(sample => sample.MoonCalls);
            case GeometryEvalCountProbe.Metric.MoonCallsMax:
                return Max(sample => sample.MoonCalls);
            case GeometryEvalCountProbe.Metric.MoonEvalsMean:
                return Mean(sample => sample.MoonEvals);
            case GeometryEvalCountProbe.Metric.MoonEvalsMax:
                return Max(sample => sample.MoonEvals);
            case GeometryEvalCountProbe.Metric.FramesCounted:
                return Completed.Count;
            case GeometryEvalCountProbe.Metric.MemoPresent:
                return memoPresent ? 1f : 0f;
            case GeometryEvalCountProbe.Metric.MapsLoaded:
                // Reported so "per map per frame" is a measured claim rather than an assumption
                // about the fixture: every count above is game-wide, and only equals a per-map count
                // while this reads 1.
                return Find.Maps == null ? -1f : Find.Maps.Count;
            default:
                return -1f;
        }
    }

    private static float Mean(Func<Sample, int> select)
    {
        if (Completed.Count == 0)
            return -1f;

        long total = 0;
        foreach (Sample sample in Completed)
            total += select(sample);

        return (float)total / Completed.Count;
    }

    private static float Max(Func<Sample, int> select)
    {
        if (Completed.Count == 0)
            return -1f;

        int max = 0;
        foreach (Sample sample in Completed)
            max = Math.Max(max, select(sample));

        return max;
    }
}
