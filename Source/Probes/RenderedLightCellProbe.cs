using System;
using RimWorldTestHarness.Mod.Probes;
using UnityEngine;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// What one cell actually RENDERS at: the composed artificial-light colour in the lighting overlay's
// mesh, after vanilla's flood, §27's mask and §27's max have all had their say.
//
// WHY NOTHING ELSE ANSWERS THIS. `door_inside_ground_glow` and friends read GlowGrid.GroundGlowAt,
// which is the GAMEPLAY light — plant growth, work speed, StatPart_Glow — and §27 deliberately never
// touches it. That is the subsystem's central promise, and it makes every glow-grid probe blind to
// the entire feature by construction: a scenario pinning them would read the same number with §27 on
// and off and conclude nothing had happened. SkyCoverVertexProbe reads the same mesh but the ALPHA
// channel, which is §7b's sky cover and is likewise left alone here. The RGB of that mesh is the one
// number that moves, and this reads it.
//
// PIXELS ARE THE WRONG INSTRUMENT FOR THE SAME QUESTION. A screenshot of two rooms is the whole point
// of the comparison, but a per-pixel ΔE cannot say WHY they differ — the sky multiply, the desatur-
// ation layer and every other lane in the mod sit between this value and the frame. Reading the
// vertex the shader consumes separates "the two rooms are composed differently" from "the two rooms
// are composed alike and something downstream is treating them differently", which on an
// indoor-versus-outdoor comparison is exactly the ambiguity worth resolving.
//
// Vertex layout is vanilla's own (MakeBaseGeometry): (Width+1)*(Height+1) corner vertices in
// row-major z-then-x order, then Width*Height cell-centre vertices in the same order — the same
// layout SkyCoverVertexProbe walks, and a negative sentinel rather than an exception if it ever
// stops matching.
public sealed class RenderedLightCellProbe : IProbe
{
    public enum Metric
    {
        // The cell's own centre vertex, per channel. The centre is vanilla's average of the four
        // corners around it, so it is what the middle of the cell renders at and the number a
        // screenshot of that cell is a picture of.
        Red,
        Green,
        Blue,

        // Rec. 709 luminance of the same vertex, which is the right single number for "are these two
        // rooms lit alike" — a torch is warm, so comparing red alone flatters whichever room the
        // torch reaches more directly.
        Luminance,

        // The max channel: vanilla's OWN summary of a glow colour, and the only one the monotonicity
        // property of §27 phase 5b can be stated on.
        //
        // WHY NOT LUMINANCE, WHICH IS THE BETTER PERCEPTUAL NUMBER. Because the claim under test is
        // "adding a lamp never lowers this", and that claim is FALSE PER CHANNEL for vanilla itself:
        // ColorInt.ProjectToColor32 normalises the three channels against their shared peak, so a
        // green lamp added to a red-lit cell genuinely lowers the red channel, and any weighted mix
        // of the three inherits that. Only the peak is monotone, and it is also exactly what
        // GlowGrid.GroundGlowAt reads — `Mathf.Max(Mathf.Max(r, g), b) / 255f * 3.6f`. Measuring the
        // property on anything else would fail the oracle before it ever reached our arithmetic.
        Level,
    }

    // Sentinel for "the mesh was not in the layout we expect". Negative so it can never be confused
    // with a real channel, all of which are 0..255.
    public const float Unavailable = -1f;

    private readonly IntVec3 cellOffset;
    private readonly Metric metric;

    public string Name { get; }

    // cellOffset is relative to map.Center, matching how every scene-setup step in the scenarios
    // addresses cells — so a scenario edit that moves a room must move the registered offsets with it.
    public RenderedLightCellProbe(string name, IntVec3 cellOffset, Metric metric)
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

        int index = (rect.Width + 1) * (rect.Height + 1)
            + (cell.z - rect.minZ) * rect.Width + (cell.x - rect.minX);

        if (index < 0 || index >= colors.Length)
            return Unavailable;

        Color32 colour = colors[index];

        switch (metric)
        {
            case Metric.Red:
                return colour.r;
            case Metric.Green:
                return colour.g;
            case Metric.Blue:
                return colour.b;
            case Metric.Luminance:
                return 0.2126f * colour.r + 0.7152f * colour.g + 0.0722f * colour.b;
            case Metric.Level:
                return Math.Max(colour.r, Math.Max(colour.g, colour.b));
            default:
                throw new ArgumentOutOfRangeException(nameof(metric), metric, null);
        }
    }

    // Same walk as SkyCoverVertexProbe's, deliberately duplicated rather than shared: that probe is
    // the §7b instrument and this is the §27 one, and a shared helper would make a layout change in
    // one subsystem's probe silently rewrite the other's readings mid-investigation.
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
