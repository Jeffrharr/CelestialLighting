using System;
using System.Collections.Generic;
using RimWorld;
using RimWorldTestHarness.Mod.Probes;
using UnityEngine;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead — the
// shipped mod must never take a hard reference on RimWorldTestHarness, a dev-only tool.
//
// WHAT THIS MEASURES. The extruded shadow length, in cells, of a like caster at each end of the
// shadow axis. §3 used to bake a few percent of across-map length variation into vertex alpha, and
// this probe existed to prove that rendered (issue #11). That feature is gone, so the probe's job
// has inverted: a ratio of exactly 1.00 is now the PASS condition, and it guards two things at
// once — that no per-section length gradient survives anywhere in the bake path, and that the bake
// path still runs at all, since a dead path would report one of the sentinels below rather than two
// plausible cell counts.
//
// The shadow a cell casts is exactly
//     |extrusion| = (vertex alpha / 255) * |_CastVect.xz|
// because the shipped "Custom/Sun shadow" vertex program is
//     position.xyz = in_COLOR0.www * _CastVect.xyz + in_POSITION0.xyz
// (read off resources.assets in #17; DESIGN.md §3 keeps that scan). So this probe reads the live
// global vector RimWorld pushed this frame, reads the alpha this mod baked into two sections at
// opposite ends of the shadow axis, and reports the resulting shadow lengths in CELLS.
//
// WHY NOT PIXELS. A screenshot centroid measures whatever else is on screen too — that has inverted
// the sign of a result here before. The alpha is the last value the CPU owns before the GPU applies
// it, and the multiplication the GPU does to it is one line of shader that has been read directly.
//
// WHY IT REGENERATES THE SECTIONS IT SAMPLES. Only sections the camera has visited have a built
// mesh (Section.TryUpdate defers the rest), and the two ends of a 250-cell map cannot both be on
// screen at a useful zoom. Regenerate() is idempotent and is exactly the call TryUpdate makes — the
// same argument SectionRegenerateTimingProbe's header sets out — so the probe rebuilds the two
// sections it needs and reads them, which also proves the bake path itself runs.
public sealed class ShadowExtrusionProbe : IProbe
{
    public enum Metric
    {
        // Shadow length, in cells, of a full-height caster in the section furthest ALONG the shadow
        // axis (the map edge the shadows point toward).
        FarEdgeCells,

        // The same for the section furthest against the axis.
        NearEdgeCells,

        // Far / near. This is the number that decides pass/fail, and 1.0 is now what it should
        // read: no across-map gradient survives. Anything else means one has come back.
        FarOverNear,
    }

    // Only sections holding a caster at least this tall are compared, so the ratio is between two
    // like casters rather than between a wall and a sandbag. 1.0 is what walls, natural rock, coolers
    // and vents declare; the rest of the shipped range is {0.15, 0.17, 0.2, 0.3, 0.35, 0.5}.
    private const float MinCasterHeight = 0.99f;

    // Sentinels rather than exceptions, so a bad setup reads as an obviously wrong number in the
    // report instead of failing the run at an unrelated step.
    private const float NoShadowVector = -1f;
    private const float NoCastersFound = -2f;
    private const float LayerMissing = -3f;

    private readonly Metric metric;

    public string Name { get; }

    public ShadowExtrusionProbe(string name, Metric metric)
    {
        Name = name;
        this.metric = metric;
    }

    public float Read(Map map)
    {
        Vector4 castVect = Shader.GetGlobalVector(ShaderPropertyIDs.MapSunLightDirection);
        Vector2 axis = new Vector2(castVect.x, castVect.z);
        if (axis.magnitude <= 0.0001f)
            return NoShadowVector;

        Type layerType = GenTypes.GetTypeInAnyAssembly("Verse.SectionLayer_SunShadows");
        if (layerType == null)
            return LayerMissing;

        Section? far = null;
        Section? near = null;
        float farProjection = float.MinValue;
        float nearProjection = float.MaxValue;

        foreach (Section section in TallCasterSections(map))
        {
            float projection = ProjectionAlongAxis(map, section, axis);
            if (projection > farProjection)
            {
                farProjection = projection;
                far = section;
            }

            if (projection < nearProjection)
            {
                nearProjection = projection;
                near = section;
            }
        }

        if (far == null || near == null)
            return NoCastersFound;

        float farCells = ShadowCells(far, layerType, axis.magnitude);
        float nearCells = ShadowCells(near, layerType, axis.magnitude);

        if (metric == Metric.FarEdgeCells)
            return farCells;
        if (metric == Metric.NearEdgeCells)
            return nearCells;

        return nearCells <= 0.0001f ? NoCastersFound : farCells / nearCells;
    }

    // Longest shadow the section's rebuilt submesh will extrude, in cells. Reads the maximum baked
    // alpha rather than an average because a section holds a whole perimeter, most of whose vertices
    // are the ground-level (alpha 0) end of each quad.
    private static float ShadowCells(Section section, Type layerType, float castLength)
    {
        SectionLayer? layer = section.GetLayer(layerType);
        if (layer == null)
            return LayerMissing;

        layer.Regenerate();

        byte peak = 0;
        List<LayerSubMesh> subMeshes = layer.subMeshes;
        for (int i = 0; i < subMeshes.Count; i++)
        {
            if (subMeshes[i].material == MatBases.SunShadow)
                peak = PeakAlpha(subMeshes[i].colors, peak);
        }

        return peak / 255f * castLength;
    }

    private static byte PeakAlpha(List<Color32> colors, byte seed)
    {
        byte peak = seed;
        for (int i = 0; i < colors.Count; i++)
        {
            if (colors[i].a > peak)
                peak = colors[i].a;
        }

        return peak;
    }

    // Sections holding at least one full-height shadow caster, found from the edifice grid rather
    // than from meshes — a section the camera has never visited has no mesh yet, and building one
    // for every section on a 250x250 map just to find the two ends would cost more than the whole
    // scenario.
    private static IEnumerable<Section> TallCasterSections(Map map)
    {
        Building[] edifices = map.edificeGrid.InnerArray;
        CellIndices indices = map.cellIndices;
        MapDrawer drawer = map.mapDrawer;

        for (int sectionX = 0; sectionX * 17 < map.Size.x; sectionX++)
        {
            for (int sectionZ = 0; sectionZ * 17 < map.Size.z; sectionZ++)
            {
                Section section = drawer.SectionAt(new IntVec3(sectionX * 17, 0, sectionZ * 17));
                if (HasTallCaster(section, edifices, indices))
                    yield return section;
            }
        }
    }

    private static bool HasTallCaster(Section section, Building[] edifices, CellIndices indices)
    {
        foreach (IntVec3 cell in section.CellRect)
        {
            Building edifice = edifices[indices.CellToIndex(cell)];
            if (edifice != null && edifice.def.staticSunShadowHeight >= MinCasterHeight)
                return true;
        }

        return false;
    }

    // Signed distance, in cells, of a section's centre from the map centre along the shadow axis.
    // Only the ORDER matters — the probe uses it to pick the two extreme sections and nothing else —
    // so this deliberately skips the half-extent normalisation and the [-1, 1] clamp that §3's
    // deleted Formulas helper applied, both of which could tie two corner sections at the extremes.
    private static float ProjectionAlongAxis(Map map, Section section, Vector2 axis)
    {
        Vector3 center = section.CellRect.CenterVector3;
        Vector2 dir = axis.normalized;

        return (center.x - map.Size.x / 2f) * dir.x + (center.z - map.Size.z / 2f) * dir.y;
    }
}
