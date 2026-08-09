using System;
using RimWorldTestHarness.Mod.Probes;
using UnityEngine;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// Reads §7b's actual OUTPUT: the baked per-vertex sky-cover alpha in the lighting overlay mesh, which
// is the number the shader samples to decide how much sky colour a vertex takes. Every other way of
// checking this subsystem re-runs IndoorOcclusionMath and therefore proves only that the arithmetic
// is self-consistent; this reads the mesh Patch_IndoorSkyOcclusion actually wrote, so it catches the
// failure mode where the classification is right but the bake never landed (stale mesh, a section
// that was never dirtied, another mod's postfix overwriting ours).
//
// WHY A CORNER VERTEX IS THE INTERESTING ONE. A leak is invisible at cell centres by construction:
// IndoorOcclusionMath.CentreOcclusion forces an interior cell's centre to a flat 1.0 no matter how
// open its corners are, so a probe reading centres would report 255 either side of the toggle and
// conclude — wrongly — that nothing happened. The whole effect lives in the lattice corners, where
// CornerOcclusion caps a corner that touches a leaky boundary. Both metrics are exposed here
// precisely so a scenario can pin that contrast rather than assert one number in isolation: corner
// moves, centre does not, and a run where BOTH moved would mean something else changed too.
//
// Vertex layout is vanilla's own (MakeBaseGeometry): (Width+1)*(Height+1) corner vertices in
// row-major z-then-x order, then Width*Height cell-centre vertices in the same order. Recomputed the
// way Patch_IndoorSkyOcclusion recomputes it rather than reflecting Section's private cache, and the
// probe returns a negative sentinel rather than throwing if the layout does not match, so a mesh
// change upstream reads as a failed pin instead of an exception mid-scenario.
public sealed class SkyCoverVertexProbe : IProbe
{
    public enum Metric
    {
        // The lattice point at the south-west corner of the addressed cell — shared by that cell and
        // its three neighbours to the south and west. This is where a glass wall's leak shows up: for
        // the cell one step in from a south-facing glass wall, this corner touches the glass.
        CornerAlpha,

        // The addressed cell's own centre vertex. Pinned alongside the corner as the control.
        CentreAlpha,
    }

    // Sentinel for "the mesh was not in the layout we expect" (no map, no section, no submesh, or a
    // vertex count that does not match vanilla's geometry). Negative so it can never be confused with
    // a real alpha, all of which are 0..255.
    public const float Unavailable = -1f;

    private readonly IntVec3 cellOffset;
    private readonly Metric metric;

    public string Name { get; }

    // cellOffset is relative to map.Center, matching how every scene-setup step in the scenarios
    // addresses cells. The probe is handed a Map, not the scene plan that built it, so a scenario edit
    // that moves the room must move the registered offsets with it.
    public SkyCoverVertexProbe(string name, IntVec3 cellOffset, Metric metric)
    {
        Name = name;
        this.cellOffset = cellOffset;
        this.metric = metric;
    }

    public float Read(Map map)
    {
        IntVec3 cell = map.Center + cellOffset;
        if (!cell.InBounds(map))
            return Unavailable;

        Color32[] colors = LightingOverlayColors(map, cell, out CellRect rect);
        if (colors == null)
            return Unavailable;

        int index = VertexIndex(rect, cell, metric);
        return index >= 0 && index < colors.Length ? colors[index].a : Unavailable;
    }

    private static Color32[] LightingOverlayColors(Map map, IntVec3 cell, out CellRect rect)
    {
        rect = default;

        Section section = map.mapDrawer?.SectionAt(cell);
        if (section == null)
            return null;

        // Section.GetLayer(Type) is vanilla's own public accessor, so this cannot drift from however
        // Section chooses to store its layers.
        SectionLayer layer = section.GetLayer(typeof(SectionLayer_LightingOverlay));
        Mesh mesh = layer?.GetSubMesh(MatBases.LightOverlay)?.mesh;
        if (mesh == null)
            return null;

        rect = new CellRect(section.botLeft.x, section.botLeft.z, Section.Size, Section.Size);
        rect.ClipInsideMap(map);

        Color32[] colors = mesh.colors32;
        int expected = (rect.Width + 1) * (rect.Height + 1) + rect.Width * rect.Height;
        return colors != null && colors.Length == expected ? colors : null;
    }

    private static int VertexIndex(CellRect rect, IntVec3 cell, Metric metric)
    {
        int stride = rect.Width + 1;
        int localX = cell.x - rect.minX;
        int localZ = cell.z - rect.minZ;

        switch (metric)
        {
            case Metric.CornerAlpha:
                return localZ * stride + localX;
            case Metric.CentreAlpha:
                return (rect.Width + 1) * (rect.Height + 1) + localZ * rect.Width + localX;
            default:
                throw new ArgumentOutOfRangeException(nameof(metric), metric, null);
        }
    }
}
