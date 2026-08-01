using System;
using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// DIAGNOSTIC ONLY — for docs/EAVE-SEAM.md. Not part of any shipped behaviour.
//
// The seam investigation reached a point where every inference from pixels ran out: enclosing a roof
// strip halves the northward reach of its own cast band (0.29 tile open, 0.14 tile walled) while the
// eave/caster counts differ only at the strip's two end columns, which cannot explain a seam running
// the full length of the roofline. Displacement in the sun-shadow shader is `alpha * _CastVect` with
// _CastVect a per-map global, so a halved reach means the emitting caster's HEIGHT differs — and the
// only way to settle that is to read the grid the mesh builder actually reads, rather than to keep
// deducing it from luminance.
//
// Reports counts over the whole map, mirroring EaveShadowGrid.CellHeight exactly (it is the same
// EavesMath call), so a disagreement between this and the renderer is impossible by construction.
public sealed class SkirtProbe : IProbe
{
    public enum Metric
    {
        // Cells with any effective caster height at all — edifice OR roofline. Strictly a superset of
        // roof_shadow_cells, and the difference between them is the map's wall/tree count.
        Casters,

        // Cells that would emit a NORTH skirt: they cast, and the cell to their north is shorter. This
        // is EmitCell's own condition, and it is the one the seam turns on — the band north of a
        // roofline is exactly the union of these.
        NorthSkirts,

        // Total effective caster height x 100, summed over the map. Catches a height that is present
        // but WRONG (0.5 where 1.0 was expected), which a count cannot see and which is precisely what
        // a halved shadow reach would imply.
        HeightSum,
    }

    private readonly Metric metric;

    public string Name { get; }

    public SkirtProbe(string name, Metric metric)
    {
        Name = name;
        this.metric = metric;
    }

    public float Read(Map map)
    {
        if (map == null)
            return 0f;

        float total = 0f;
        foreach (IntVec3 cell in map.AllCells)
            total += Contribution(map, cell);

        return total;
    }

    private float Contribution(Map map, IntVec3 cell)
    {
        float here = Height(map, cell);

        switch (metric)
        {
            case Metric.Casters:
                return here > 0f ? 1f : 0f;
            case Metric.HeightSum:
                return here * 100f;
            case Metric.NorthSkirts:
                return EmitsNorthSkirt(map, cell, here) ? 1f : 0f;
            default:
                throw new ArgumentOutOfRangeException(nameof(metric), metric, null);
        }
    }

    // EmitCell's own north test, transcribed: the cell casts, it is not on the map's north edge, and
    // its northern neighbour is shorter than it is.
    private static bool EmitsNorthSkirt(Map map, IntVec3 cell, float here)
    {
        if (!(here > 0f) || cell.z >= map.Size.z - 1)
            return false;

        return Height(map, new IntVec3(cell.x, 0, cell.z + 1)) < here;
    }

    // The same resolution EaveShadowGrid.CellHeight performs, via the same EavesMath entry point.
    private static float Height(Map map, IntVec3 cell)
    {
        Building edifice = map.edificeGrid[cell];
        float edificeHeight = edifice == null ? 0f : edifice.def.staticSunShadowHeight;

        return EavesMath.CasterHeight(edificeHeight, EaveCells.CastsRoofShadow(map, cell));
    }
}
