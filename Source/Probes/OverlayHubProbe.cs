using System;
using RimWorldTestHarness.Mod.Probes;
using UnityEngine;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead.
//
// Reads the lighting overlay's baked vertex COLOUR at one cell and reports how far the cell's centre
// vertex sits from the mean of its four corners. Vanilla writes every centre as exactly that mean
// (SectionLayer_LightingOverlay), and the mesh fans four triangles out of the centre, so any pass of
// ours that leaves the centre disagreeing with its corners renders as a diamond-with-diagonals inside
// the tile -- the "black square with an X fading inside it" a player reported on wall tiles. A
// screenshot shows the X only when the tile is bright enough to see it and the camera close enough;
// this reads the number the X is made of, so a scenario can pin it at zero per arm and learn which
// subsystem wrote it.
//
// Luma is the plain channel mean, not a perceptual weight: the overlay's RGB is artificial light and
// the question is "does the centre equal the corner mean", which is per-channel arithmetic.
//
// Vertex layout is vanilla's own (MakeBaseGeometry), read the way SkyCoverVertexProbe reads it.
public sealed class OverlayHubProbe : IProbe
{
    public enum Metric
    {
        // Mean of the four corner vertices' RGB luma, 0..255.
        CornerMeanLuma,

        // The cell's centre vertex RGB luma, 0..255.
        CentreLuma,

        // CornerMeanLuma - CentreLuma. Positive means the centre is darker than its corners (a dark
        // X); negative means brighter (a light X). Zero is what vanilla's own bake gives.
        Hub,

        // The same difference on the alpha channel (sky cover), for the passes that write alpha.
        AlphaHub,
    }

    public const float Unavailable = -999f;

    private readonly IntVec3 cellOffset;
    private readonly Metric metric;

    public string Name { get; }

    public OverlayHubProbe(string name, IntVec3 cellOffset, Metric metric)
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

        int stride = rect.Width + 1;
        int localX = cell.x - rect.minX;
        int localZ = cell.z - rect.minZ;
        int botLeft = localZ * stride + localX;
        int centre = (rect.Width + 1) * (rect.Height + 1) + localZ * rect.Width + localX;

        if (centre >= colors.Length || botLeft + stride + 1 >= colors.Length)
            return Unavailable;

        Color32 c0 = colors[botLeft];
        Color32 c1 = colors[botLeft + 1];
        Color32 c2 = colors[botLeft + stride];
        Color32 c3 = colors[botLeft + stride + 1];
        Color32 hub = colors[centre];

        float cornerLuma = (Luma(c0) + Luma(c1) + Luma(c2) + Luma(c3)) / 4f;
        float cornerAlpha = (c0.a + c1.a + c2.a + c3.a) / 4f;

        switch (metric)
        {
            case Metric.CornerMeanLuma:
                return cornerLuma;
            case Metric.CentreLuma:
                return Luma(hub);
            case Metric.Hub:
                return cornerLuma - Luma(hub);
            case Metric.AlphaHub:
                return cornerAlpha - hub.a;
            default:
                throw new ArgumentOutOfRangeException(nameof(metric), metric, null);
        }
    }

    private static float Luma(Color32 c) => (c.r + c.g + c.b) / 3f;

    private static Color32[] LightingOverlayColors(Map map, IntVec3 cell, out CellRect rect)
    {
        rect = default;

        Section section = map.mapDrawer?.SectionAt(cell);
        if (section == null)
            return null;

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
}
