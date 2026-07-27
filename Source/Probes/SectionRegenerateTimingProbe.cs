using System;
using System.Collections.Generic;
using System.Diagnostics;
using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see <Compile Remove> in CelestialLighting.csproj)
// and compiled into TestMod/CelestialLighting.Probes.csproj instead — the shipped mod must never take
// a hard reference on RimWorldTestHarness, which is a dev-only tool.
//
// Times ONE section layer's Regenerate() on a live map, in microseconds. This exists because issue
// #10 asked what our section-layer fan-out actually costs (DESIGN.md §16) and the only honest answer
// was "nobody has measured it": the dominant terms — the glow-grid and room-grid reads, and the
// Unity mesh upload inside FinalizeMesh — cannot be reproduced offline, because there is no Map and
// no GPU outside a running game. So the measurement is taken here, where all three are real.
//
// WHY IT CALLS Regenerate() DIRECTLY rather than dirtying a flag and waiting a frame. Verse.Section's
// TryUpdate is what turns a dirty flag into work, and it neither reports what it did nor separates
// one layer from another; timing it would give a single number that mixes every subscriber. Calling
// each layer's own Regenerate() is exactly the call TryUpdate makes, one layer at a time, so summing
// the per-layer numbers over a flag's subscriber list reconstructs the flag's cost with the parts
// still visible. Regenerate is idempotent — it rebuilds a mesh from current map state, which is what
// vanilla does dozens of times a second — so calling it repeatedly perturbs nothing.
//
// WHY IT IGNORES Visible. TryUpdate does too (decompiled 1.6: RegenerateAllLayers and
// RegenerateDirtyLayers check layer.Visible, TryUpdate does not), so a layer whose feature toggle is
// off still pays this cost on every relevant dirty flag. Measuring through Visible would report zero
// for a cost the player is really paying. That asymmetry is half of what §16 exists to record.
//
// The number is a mean over repeated regenerates of the same sections, so caches and the JIT are
// warm — a lower bound on a cold first edit, and the right basis for comparing layers against each
// other, which is the question being asked.
public sealed class SectionRegenerateTimingProbe : IProbe
{
    // How many distinct sections to time, and how many regenerates each. Warmup runs first so
    // neither the JIT nor the one-off MakeBaseGeometry allocation lands inside the timed window.
    private const int MaxSections = 4;
    private const int WarmupRuns = 2;
    private const int TimedRuns = 10;

    // Sentinels, returned rather than thrown so a missing layer shows up as an obviously wrong probe
    // value in the report instead of failing the whole scenario at an unrelated step.
    private const float TypeNotFound = -1f;
    private const float NoSectionsSampled = -2f;
    private const float LayerNotOnSection = -3f;

    private readonly string layerTypeName;

    public string Name { get; }

    // layerTypeName is the fully-qualified name because two of the interesting layers
    // (SectionLayer_SunShadows, SectionLayer_Darkness) are internal to Assembly-CSharp and cannot be
    // named in source at all — the same constraint Patch_ShadowRoofInvalidation works around.
    public SectionRegenerateTimingProbe(string name, string layerTypeName)
    {
        Name = name;
        this.layerTypeName = layerTypeName;
    }

    public float Read(Map map)
    {
        Type layerType = GenTypes.GetTypeInAnyAssembly(layerTypeName);
        if (layerType == null)
            return TypeNotFound;

        List<Section> sections = SampleSections(map);
        if (sections.Count == 0)
            return NoSectionsSampled;

        long ticks = 0;
        int runs = 0;
        foreach (Section section in sections)
        {
            SectionLayer layer = section.GetLayer(layerType);
            if (layer == null)
                return LayerNotOnSection;

            ticks += TimeLayer(layer);
            runs += TimedRuns;
        }

        // Stopwatch ticks are not microseconds on every platform, hence the explicit frequency
        // conversion rather than a hardcoded divisor.
        double microsPerRun = ticks * 1_000_000.0 / Stopwatch.Frequency / runs;
        return (float)microsPerRun;
    }

    private static long TimeLayer(SectionLayer layer)
    {
        for (int i = 0; i < WarmupRuns; i++)
            layer.Regenerate();

        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < TimedRuns; i++)
            layer.Regenerate();
        watch.Stop();
        return watch.ElapsedTicks;
    }

    // Prefers sections that actually contain roof, because that is where roof edits happen and
    // because a roofed cell is the only one that reaches the expensive half of EaveCells.IsEave (the
    // room query — an unroofed cell short-circuits before it). Timing only bare ground would flatter
    // the eave layer for the exact reason it is cheap there. Falls back to the first sections in
    // row-major order so the probe still returns something on a map with no roof at all.
    private static List<Section> SampleSections(Map map)
    {
        List<Section> roofed = new List<Section>();
        List<Section> any = new List<Section>();

        for (int z = 0; z < map.Size.z; z += Section.Size)
        {
            for (int x = 0; x < map.Size.x; x += Section.Size)
            {
                Section section = map.mapDrawer.SectionAt(new IntVec3(x, 0, z));
                if (any.Count < MaxSections)
                    any.Add(section);

                if (roofed.Count < MaxSections && ContainsRoof(map, section))
                    roofed.Add(section);
            }
        }

        return roofed.Count > 0 ? roofed : any;
    }

    private static bool ContainsRoof(Map map, Section section)
    {
        foreach (IntVec3 cell in section.CellRect)
        {
            if (map.roofGrid.Roofed(cell))
                return true;
        }

        return false;
    }
}
