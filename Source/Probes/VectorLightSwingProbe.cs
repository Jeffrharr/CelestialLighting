using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorldTestHarness.Mod.Probes;
using UnityEngine;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// THE INSTRUMENT ISSUE #218 DID NOT HAVE. That defect is transient: on the frame a door moves, a
// section bakes vanilla's fresh glow against our stale coverage and the region renders darker than
// either end of the swing, then the next frame puts it right. Every scenario in this repo captures
// after a settle, so all of them read the correct final value and none of them can see it. It was
// found by a human watching a run.
//
// WHY A PER-FRAME HOOK RATHER THAN A ROW OF Probe STEPS. The harness runs one step per frame, so a
// column of consecutive Probe steps does sample consecutive frames — but a door swing needs real
// ticks (GameComponent_DoorAperture drives it from GameComponentTick, and AdvanceTicks is a clock
// JUMP that runs no ticks at all), which means the clock has to be running and the tick-to-frame
// alignment then varies with frame time. The defective frame lands on a different step index between
// runs, and a survey that misses it reports a clean build. Sampling from inside the render loop sees
// EVERY frame regardless of where the steps fell, which is the difference between an instrument and
// a lottery.
//
// WHY A REGION AND NOT A CELL. Picking the one cell the overshoot lands on requires knowing where it
// lands, and a wrong guess reports a confident zero that is indistinguishable from a fixed build.
// The sampler folds a box and answers the worst cell in it, plus where that cell was — so the
// scenario states a neighbourhood rather than a bet.
//
// WHAT IT READS: the lighting overlay's baked vertex colour, the same quantity RenderedLightCellProbe
// reads and for the same reason — it is what the mask writes during a section regenerate, so a stale
// bake is visible in it directly rather than inferred from pixels with the sky multiply in between.
public static class VectorLightSwingSampler
{
    // Sentinel matching RenderedLightCellProbe.Unavailable, and the value SwingExcursionMath.Trace
    // refuses rather than folds. Kept as its own constant rather than referenced across, because the
    // two probes walk the mesh independently on purpose (see LightingOverlayColors below).
    public const float Unreadable = -1f;

    // The armed box, as offsets from map centre — the same convention every scene-setup step and
    // every §27 probe uses, so a scenario that moves its room moves this with it.
    private static IntVec3 centreOffset;
    private static int radius = -1;

    // One trace per cell of the armed box, row-major in z then x. Allocated on arming and reused for
    // the whole window, so the per-frame cost is the mesh read and nothing else.
    private static SwingExcursionMath.Trace[] traces = Array.Empty<SwingExcursionMath.Trace>();

    // Frames sampled since arming. Its own counter rather than a trace's Count, because a frame in
    // which the mesh could not be read still happened — and a scenario reading zero frames has
    // measured nothing, which must not look like a monotone swing.
    public static int Frames;

    // Per-frame scratch for the section-to-colours lookup. A box a few cells across spans at most a
    // handful of sections, so a linear scan over two parallel lists beats a dictionary and, more to
    // the point, allocates nothing per frame beyond the colour arrays Unity hands back.
    private static readonly List<Section> ScratchSections = new List<Section>();
    private static readonly List<Color32[]> ScratchColors = new List<Color32[]>();
    private static readonly List<CellRect> ScratchRects = new List<CellRect>();

    private static bool installed;

    public static bool Armed => radius >= 0;

    // Called once from ProbeRegistration's static constructor, the same place and for the same reason
    // SectionLayerDrawCounters.Install and GeometryEvalCounters.Install are: it lands before any frame
    // has drawn.
    //
    // HOOKED ON DrawMapMesh, NOT ON MapMeshDrawerUpdate_First, and the choice is not cosmetic. The
    // fix for #218 is a prefix on MapMeshDrawerUpdate_First; an instrument sharing that method with
    // the thing it measures invites exactly the argument a measurement exists to settle. DrawMapMesh
    // is the next call in Map.MapUpdate, runs once per frame on the drawing map, and is the moment
    // the meshes are handed to the renderer — so what it reads is what the frame shows, which is the
    // claim being made.
    public static void Install()
    {
        if (installed)
            return;

        installed = true;
        Harmony harmony = new Harmony("celestiallighting.probes.vectorlightswing");

        MethodInfo drawMapMesh = AccessTools.Method(typeof(MapDrawer), nameof(MapDrawer.DrawMapMesh));

        if (drawMapMesh == null)
        {
            throw new InvalidOperationException(
                "VectorLightSwingSampler could not resolve MapDrawer.DrawMapMesh. Failing loudly "
                + "rather than sampling nothing and reporting a monotone swing.");
        }

        harmony.Patch(drawMapMesh, postfix: new HarmonyMethod(
            AccessTools.Method(typeof(VectorLightSwingSampler), nameof(NoteFrame))));
    }

    // Arm the sampler over a box and throw away whatever the last window recorded.
    //
    // ARMING IS ALSO THE RESET. A scenario runs two arms in one boot and the second must not inherit
    // the first's minimum — that would carry one arm's defect into the other's reading and report the
    // fix as not working.
    public static void Arm(IntVec3 offset, int cellRadius)
    {
        centreOffset = offset;
        radius = cellRadius < 0 ? 0 : cellRadius;

        int span = (radius * 2) + 1;

        if (traces.Length != span * span)
        {
            traces = new SwingExcursionMath.Trace[span * span];
        }

        Array.Clear(traces, 0, traces.Length);
        Frames = 0;
    }

    public static void Disarm()
    {
        radius = -1;
    }

    // One rendered frame. Reads every cell of the armed box out of the lighting overlay meshes and
    // folds each into its own trace.
    private static void NoteFrame(MapDrawer __instance)
    {
        if (!Armed)
            return;

        Map map = Find.CurrentMap;

        if (map == null || map.mapDrawer != __instance)
            return;

        Frames++;

        ScratchSections.Clear();
        ScratchColors.Clear();
        ScratchRects.Clear();

        IntVec3 centre = map.Center + centreOffset;
        int span = (radius * 2) + 1;

        for (int dz = -radius; dz <= radius; dz++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                int slot = ((dz + radius) * span) + (dx + radius);
                IntVec3 cell = new IntVec3(centre.x + dx, 0, centre.z + dz);

                traces[slot] = traces[slot].Add(Sample(map, cell));
            }
        }
    }

    // What one cell renders at, as vanilla's own summary of a glow colour.
    //
    // THE MAX CHANNEL, matching RenderedLightCellProbe.Metric.Level and for its reason: ColorInt
    // normalises the three channels against their shared peak, so an individual channel is not
    // monotone in the light added even in vanilla, and monotonicity is the entire property under
    // test here. Only the peak is, and it is also what GlowGrid.GroundGlowAt reads.
    private static float Sample(Map map, IntVec3 cell)
    {
        if (!cell.InBounds(map))
            return Unreadable;

        Color32[] colors = ColorsFor(map, cell, out CellRect rect);

        if (colors == null)
            return Unreadable;

        int index = ((rect.Width + 1) * (rect.Height + 1))
            + ((cell.z - rect.minZ) * rect.Width) + (cell.x - rect.minX);

        if (index < 0 || index >= colors.Length)
            return Unreadable;

        Color32 colour = colors[index];

        return Math.Max(colour.r, Math.Max(colour.g, colour.b));
    }

    // The section's colour array, fetched once per section per frame rather than once per cell.
    //
    // WHY THE CACHE EXISTS AT ALL. Mesh.colors32 ALLOCATES a fresh managed array on every read, so
    // the obvious implementation — construct a RenderedLightCellProbe per cell and call Read — would
    // allocate one array per cell per frame. On a box a few cells across across a ninety-frame swing
    // that is thousands of arrays of several hundred entries, which on this subsystem's own
    // measurement budget is a garbage collector in the middle of the window being measured.
    private static Color32[] ColorsFor(Map map, IntVec3 cell, out CellRect rect)
    {
        rect = default;

        Section section = map.mapDrawer?.SectionAt(cell);

        if (section == null)
            return null;

        int known = ScratchSections.IndexOf(section);

        if (known >= 0)
        {
            rect = ScratchRects[known];
            return ScratchColors[known];
        }

        // Same walk RenderedLightCellProbe and SkyCoverVertexProbe each keep their own copy of, and
        // duplicated for the reason those two record: a shared helper would let a layout change made
        // for one subsystem's probe silently rewrite another's readings mid-investigation.
        SectionLayer layer = section.GetLayer(typeof(SectionLayer_LightingOverlay));
        Mesh mesh = layer?.GetSubMesh(MatBases.LightOverlay)?.mesh;

        CellRect bounds = new CellRect(section.botLeft.x, section.botLeft.z, Section.Size, Section.Size);
        bounds.ClipInsideMap(map);

        Color32[] colors = mesh?.colors32;
        int expected = ((bounds.Width + 1) * (bounds.Height + 1)) + (bounds.Width * bounds.Height);

        if (colors != null && colors.Length != expected)
        {
            colors = null;
        }

        // Cached even when null, so a section whose mesh is missing costs one lookup per frame rather
        // than one per cell — and so the rejected count stays a count of cells rather than of retries.
        ScratchSections.Add(section);
        ScratchColors.Add(colors);
        ScratchRects.Add(bounds);

        rect = bounds;

        return colors;
    }

    // The worst cell in the box, and the answer the scenario pins. Zero means every cell moved
    // monotonically from where it started to where it ended.
    public static float WorstExcursion()
    {
        float worst = 0f;

        for (int i = 0; i < traces.Length; i++)
        {
            float excursion = traces[i].Excursion;
            worst = excursion > worst ? excursion : worst;
        }

        return worst;
    }

    // Where the worst cell was, as an offset from the armed centre. Reported so a non-zero reading
    // names a place on the map instead of being a number to argue about — and so a defect that moves
    // to a different part of the doorway is visible as having moved.
    public static float WorstExcursionAxis(bool wantX)
    {
        float worst = 0f;
        int worstSlot = -1;

        for (int i = 0; i < traces.Length; i++)
        {
            float excursion = traces[i].Excursion;

            if (excursion > worst)
            {
                worst = excursion;
                worstSlot = i;
            }
        }

        if (worstSlot < 0)
            return 0f;

        int span = (radius * 2) + 1;

        return wantX ? (worstSlot % span) - radius : (worstSlot / span) - radius;
    }

    // The largest end-to-end change any cell in the box underwent.
    //
    // THE GUARD AGAINST A CONFIDENT ZERO, which is this instrument's own worst failure mode: a run
    // where the door never swung, the scene never lit, or the box sat somewhere nothing happened
    // reports a perfectly monotone window. A scenario pins this ABOVE a floor alongside pinning the
    // excursion at zero, so "nothing moved" fails instead of passing.
    public static float WidestSpan()
    {
        float widest = 0f;

        for (int i = 0; i < traces.Length; i++)
        {
            SwingExcursionMath.Trace trace = traces[i];

            // A cell sampled once has no end-to-end change to report; only a cell with two samples
            // has travelled anywhere.
            if (trace.Count >= 2)
            {
                float span = Math.Abs(trace.Last - trace.First);
                widest = span > widest ? span : widest;
            }
        }

        return widest;
    }

    // Cells the mesh walk refused, summed over the window. Non-zero means the instrument spent frames
    // unable to read its subject, which makes any excursion reading a lower bound rather than an
    // answer — so it is a probe of its own rather than a silent drop.
    public static float RejectedSamples()
    {
        int rejected = 0;

        for (int i = 0; i < traces.Length; i++)
        {
            rejected += traces[i].Rejected;
        }

        return rejected;
    }
}

// The harness face of the sampler above. One metric per probe, because a scenario pins numbers one
// at a time and a packed value would have to be unpacked in JSON.
public sealed class VectorLightSwingProbe : IProbe
{
    public enum Metric
    {
        // How far the worst cell in the armed box left the band between where it started and where it
        // ended. The number issue #218 is about: zero is a monotone swing.
        Excursion,

        // Where that cell was, relative to the armed centre.
        ExcursionX,
        ExcursionZ,

        // The largest end-to-end change in the box, so "nothing happened" cannot pass as "nothing
        // went wrong".
        Span,

        // Frames sampled, and cells the mesh walk refused. Both exist so a zero excursion can be
        // shown to be a measurement rather than an absence of one.
        Frames,
        Rejected,
    }

    private readonly Metric metric;

    public string Name { get; }

    public VectorLightSwingProbe(string name, Metric metric)
    {
        Name = name;
        this.metric = metric;
    }

    public float Read(Map map)
    {
        switch (metric)
        {
            case Metric.Excursion:
                return VectorLightSwingSampler.WorstExcursion();
            case Metric.ExcursionX:
                return VectorLightSwingSampler.WorstExcursionAxis(wantX: true);
            case Metric.ExcursionZ:
                return VectorLightSwingSampler.WorstExcursionAxis(wantX: false);
            case Metric.Span:
                return VectorLightSwingSampler.WidestSpan();
            case Metric.Frames:
                return VectorLightSwingSampler.Frames;
            case Metric.Rejected:
                return VectorLightSwingSampler.RejectedSamples();
            default:
                throw new ArgumentOutOfRangeException(nameof(metric), metric, null);
        }
    }
}
