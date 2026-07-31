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
// WHAT THIS MEASURES. Two of the mod's section layers carry a map-wide alpha in their material rather
// than in the mesh, so each spends half of every day at alpha 0: §9's night wash is transparent for
// the whole of daylight, §15b's eave shade for the whole of night. Vanilla's MapDrawLayer.DrawLayer
// gates only on Visible and subMesh.disabled and knows nothing about the material, so before the
// DrawLayer overrides those layers submitted one Graphics.DrawMesh per on-screen section per frame
// for a mesh that blends nothing.
//
// This COUNTS submissions rather than timing them, for the reason GeometryEvalCountProbe states about
// its own subject: the claim is "these submissions stop happening", which is a count. The cost they
// carry is mostly GPU fill — a viewport-sized transparent pass that writes no pixels — and a CPU
// stopwatch around DrawMesh would measure the submission and miss the overdraw entirely, reporting a
// small number for a real saving. A count is the honest instrument for the claim actually being made.
//
// WHY IT PATCHES THE BASE METHOD. The two layers' overrides call base.DrawLayer() only when their
// material is drawing, so MapDrawLayer.DrawLayer is exactly the point where a submission does or does
// not happen. It is also what the layers' vtable slots resolved to BEFORE the overrides existed, so
// the same instrument reads both builds and the before/after is one number moving — the two-level
// trick GeometryEvalCountProbe uses to stay comparable across the change it measures.
//
// The prefix therefore runs for every layer of every visible section (~10 x ~30 a frame) and costs two
// type checks each. That is fine in a probe build and would not be in a shipped one, which is the
// other reason this lives here.
public sealed class SectionLayerDrawCountProbe : IProbe
{
    public enum Metric
    {
        WashDrawsMean,
        WashDrawsMax,
        ShadeDrawsMean,
        ShadeDrawsMax,
        SectionsDrawnMean,
        FramesCounted,
    }

    private readonly Metric metric;

    public string Name { get; }

    public SectionLayerDrawCountProbe(string name, Metric metric)
    {
        Name = name;
        this.metric = metric;
    }

    public float Read(Map map) => SectionLayerDrawCounters.Read(metric);
}

// The counting itself, plus the Harmony patch that feeds it. Static because Harmony prefixes must be,
// and because there is one count per game process however many probes read it.
public static class SectionLayerDrawCounters
{
    // Four seconds of frames at 60fps, matching GeometryEvalCounters: long enough for a scenario to
    // Wait out a steady state and have all of it in the window, short enough that a window opened by a
    // reset does not silently include pre-reset frames once it wraps.
    private const int WindowFrames = 512;

    private readonly struct Sample
    {
        public readonly int WashDraws;
        public readonly int ShadeDraws;
        public readonly int SectionsDrawn;

        public Sample(int washDraws, int shadeDraws, int sectionsDrawn)
        {
            WashDraws = washDraws;
            ShadeDraws = shadeDraws;
            SectionsDrawn = sectionsDrawn;
        }
    }

    private static readonly List<Sample> Completed = new List<Sample>(WindowFrames);

    private static int currentFrame;
    private static bool frameOpen;
    private static int washDraws;
    private static int shadeDraws;
    private static int sectionsDrawn;

    private static bool installed;

    // Called once from ProbeRegistration's static constructor, the same place and for the same reason
    // GeometryEvalCounters.Install is: it lands before any section has drawn a frame.
    public static void Install()
    {
        if (installed)
            return;

        installed = true;
        Harmony harmony = new Harmony("celestiallighting.probes.sectionlayerdrawcount");

        MethodInfo drawLayer = AccessTools.Method(typeof(MapDrawLayer), nameof(MapDrawLayer.DrawLayer));
        if (drawLayer == null)
            throw new InvalidOperationException(
                "SectionLayerDrawCountProbe could not resolve MapDrawLayer.DrawLayer. Failing loudly "
                + "rather than reporting a zero that would read as a perfect saving.");

        harmony.Patch(drawLayer, prefix: new HarmonyMethod(
            AccessTools.Method(typeof(SectionLayerDrawCounters), nameof(NoteDraw))));
    }

    // One entry into the base draw = one submission that actually reaches Graphics.DrawMesh (modulo
    // vanilla's own finalized/disabled test inside, which neither layer trips in a steady state).
    //
    // SectionsDrawn counts every layer's entry, ours included, purely so a reading of zero wash draws
    // can be distinguished from a reading taken while nothing was drawing at all — a scenario that
    // probes on a frame with the camera off the map would otherwise report a spectacular saving.
    public static void NoteDraw(MapDrawLayer __instance)
    {
        Advance();
        sectionsDrawn++;

        if (__instance is SectionLayer_NightDesaturation)
            washDraws++;
        else if (__instance is SectionLayer_EaveShade)
            shadeDraws++;
    }

    // Banks the frame that just ended, if any, and opens the new one. Frames in which nothing drew at
    // all are never banked: with a map on screen every frame draws something, so a gap means the game
    // was not rendering a map, not that a frame was free.
    private static void Advance()
    {
        int frame = Time.frameCount;
        if (frame == currentFrame && frameOpen)
            return;

        if (frameOpen)
            Bank();

        currentFrame = frame;
        frameOpen = true;
        washDraws = 0;
        shadeDraws = 0;
        sectionsDrawn = 0;
    }

    private static void Bank()
    {
        if (Completed.Count == WindowFrames)
            Completed.RemoveAt(0);

        Completed.Add(new Sample(washDraws, shadeDraws, sectionsDrawn));
    }

    // Bridged to the harness as a SetFeature toggle so a scenario can open a fresh window immediately
    // before the segment it wants to characterize — otherwise a reading taken after a SetTime is a mean
    // over both sides of the clock change and shows neither.
    public static void Reset()
    {
        Completed.Clear();
        frameOpen = false;
        washDraws = 0;
        shadeDraws = 0;
        sectionsDrawn = 0;
    }

    public static float Read(SectionLayerDrawCountProbe.Metric metric)
    {
        switch (metric)
        {
            case SectionLayerDrawCountProbe.Metric.WashDrawsMean:
                return Mean(sample => sample.WashDraws);
            case SectionLayerDrawCountProbe.Metric.WashDrawsMax:
                return Max(sample => sample.WashDraws);
            case SectionLayerDrawCountProbe.Metric.ShadeDrawsMean:
                return Mean(sample => sample.ShadeDraws);
            case SectionLayerDrawCountProbe.Metric.ShadeDrawsMax:
                return Max(sample => sample.ShadeDraws);
            case SectionLayerDrawCountProbe.Metric.SectionsDrawnMean:
                return Mean(sample => sample.SectionsDrawn);
            case SectionLayerDrawCountProbe.Metric.FramesCounted:
                return Completed.Count;
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
