using System;
using RimWorldTestHarness.Mod.Probes;
using UnityEngine;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// Reads §9's actual OUTPUT: the baked per-vertex wash alpha in SectionLayer_NightDesaturation's mesh.
// NightDesaturationProbe reads the *material* alpha, which is the map-wide strength and says nothing
// about any individual cell — its own header records that the per-cell half was verified only by
// screenshots. This is the per-cell half as a number.
//
// WHY THE CENTRE-MINUS-CORNERS DIFFERENCE IS THE METRIC THAT MATTERS, and it is not a stylistic
// preference between three readings. SectionLayerGeometryMaker_Solid emits nine vertices per cell and
// fans four triangles out of the ninth, at the cell's exact middle. The eight around it are averages
// over the cells they touch; the centre has one cell and nothing to average. So the centre is the one
// vertex that can disagree with its own neighbourhood, and when it does the cell does not render as a
// slightly-wrong tile — it renders as a diamond radiating from the middle of it, creased along the
// four triangle diagonals. CentreExcess is the size of that diamond in alpha, signed: positive is a
// dark hub, negative a bright one, zero is a tile that shades flat.
//
// Reading the centre alone cannot see it (a wall reads 255 with the defect and 255 again in a room
// that is simply dark), and reading a corner alone cannot either. The difference is the defect.
//
// Vertex layout is vanilla's own (SectionLayerGeometryMaker_Solid.MakeBaseGeometry): nine vertices per
// cell in x-outer, z-inner order — bottom-left, left, top-left, top, top-right, right, bottom-right,
// bottom, centre — which is the order SectionLayer_NightDesaturation.AddCellColors appends its colours
// in. The probe returns a negative sentinel rather than throwing if the layout does not match, so a
// mesh change upstream reads as a failed pin instead of an exception mid-scenario.
public sealed class NightWashVertexProbe : IProbe
{
    public enum Metric
    {
        // The addressed cell's own centre vertex — the hub of the fan.
        CentreAlpha,

        // The mean of the cell's four corner vertices, which is what the centre would carry if the
        // wash were reconstructed smoothly across the tile.
        CornerMeanAlpha,

        // CentreAlpha - CornerMeanAlpha: the diamond, in alpha. This is the number to pin.
        CentreExcess,
    }

    // Sentinel for "the mesh was not in the layout we expect" (no map, no section, no submesh, or a
    // vertex count that does not match the geometry maker's). -1000 rather than -1 because
    // CentreExcess is a signed difference and can legitimately reach -255.
    public const float Unavailable = -1000f;

    private const int VerticesPerCell = 9;
    private const int CentreVertex = 8;

    // Indices of the four corner vertices within a cell's block of nine.
    private static readonly int[] CornerVertices = { 0, 2, 4, 6 };

    private readonly IntVec3 cellOffset;
    private readonly Metric metric;

    public string Name { get; }

    // cellOffset is relative to map.Center, matching how every scene-setup step in the scenarios
    // addresses cells. The probe is handed a Map, not the scene plan that built it, so a scenario edit
    // that moves the wall must move the registered offsets with it.
    public NightWashVertexProbe(string name, IntVec3 cellOffset, Metric metric)
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

        Color32[] colors = WashColors(map, cell, out CellRect rect);
        if (colors == null)
            return Unavailable;

        // x outer, z inner — the order MakeBaseGeometry walks and the order AddCellColors appends in.
        int block = ((cell.x - rect.minX) * rect.Height + (cell.z - rect.minZ)) * VerticesPerCell;
        if (block < 0 || block + VerticesPerCell > colors.Length)
            return Unavailable;

        float centre = colors[block + CentreVertex].a;
        float cornerMean = 0f;
        for (int i = 0; i < CornerVertices.Length; i++)
            cornerMean += colors[block + CornerVertices[i]].a;

        cornerMean /= CornerVertices.Length;

        switch (metric)
        {
            case Metric.CentreAlpha:
                return centre;
            case Metric.CornerMeanAlpha:
                return cornerMean;
            case Metric.CentreExcess:
                return centre - cornerMean;
            default:
                throw new ArgumentOutOfRangeException(nameof(metric), metric, null);
        }
    }

    private static Color32[] WashColors(Map map, IntVec3 cell, out CellRect rect)
    {
        rect = default;

        Section section = map.mapDrawer?.SectionAt(cell);
        if (section == null)
            return null;

        // Section.GetLayer(Type) is vanilla's own public accessor, so this cannot drift from however
        // Section chooses to store its layers.
        SectionLayer layer = section.GetLayer(typeof(SectionLayer_NightDesaturation));
        Mesh mesh = layer?.GetSubMesh(NightDesaturationOverlay.Material)?.mesh;
        if (mesh == null)
            return null;

        // Section.CellRect is what Regenerate itself walks, so the block index below is computed off
        // the same rectangle the colours were appended against.
        rect = section.CellRect;

        Color32[] colors = mesh.colors32;
        return colors != null && colors.Length == rect.Area * VerticesPerCell ? colors : null;
    }
}
