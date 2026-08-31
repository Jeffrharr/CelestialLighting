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
// Measures whether vanilla's weather mask still covers everything it is supposed to cover.
//
// WHY THIS EXISTS AS A NUMBER AND NOT AS A SCREENSHOT. "Rain falls through the ceiling" is the
// user-visible name for one thing: a cell that SectionLayer_IndoorMask decided to hide, with no quad
// baked over it, so the rain overlay's fragments are not z-rejected there. A screenshot answers that
// only where a frame happens to be pointing, only for the weather that happens to be running, and
// only when someone looks closely enough to tell a rain streak from concrete noise — the three ways
// the check quietly stops being made. These metrics ask the map instead, whole-map, per cell.
//
// The hidden set is VANILLA'S OWN ANSWER, pulled through reflection rather than reimplemented.
// SectionLayer_IndoorMask.HideCommon and HideRainFogOverlay are the two private statics that decide
// which cells get a quad; a probe that re-derived their rules could only ever agree with a formula
// this repo believes, which is worthless for catching the case where vanilla (or another mod) has
// changed what a hidden cell is. If either lookup goes missing after a Ludeon update the probe
// throws rather than reporting a comfortable zero — ApiCompatibilityTests pins both names so that
// surfaces as a failing offline test first.
//
// The gravship pair exists because Patch_IndoorMaskOverage is deliberately blind to that path now.
// AppendQuadToMesh is shared by the section masks and by BakeGravshipIndoorMesh, which the takeoff
// and landing cutscenes use for the free submeshes that fly with the ship; our overage clamp is
// gated to the section materials so those keep vanilla's 0.16. That narrowing is invisible in every
// frame this repo can capture — the harness cannot fly a gravship — so it is pinned here instead:
// bake through the real vanilla helper with the cutscene's own material and read the inflation back
// out. GravshipOverage staying at 0.16 while Overage reads 0 IS the statement that the clamp stops
// at the section mask, and it is checked against live vanilla code rather than against this comment.
public sealed class IndoorMaskProbe : IProbe
{
    public enum Metric
    {
        // Cells vanilla decided to hide that no mask quad covers. The defect metric: anything above
        // zero is rain (and fog, and sun shadow) reaching ground that is under a roof.
        UncoveredCells,

        // The largest inflation any section-mask quad carries beyond its own cell, in cells. Vanilla
        // bakes 0.16 for a cell with no impassable building on it; §15's seam fix takes that to 0.
        // Read beside UncoveredCells, never instead of it: a flush quad still covers its whole cell,
        // so this number moving is a change of MARGIN and only the other one is a change of COVER.
        Overage,

        // The same inflation measured through BakeGravshipIndoorMesh with the cutscene's own
        // material. Expected to stay at vanilla's 0.16 with eave_shadows either way.
        GravshipOverage,

        // Cells hidden by vanilla that the gravship bake left uncovered, over the same cell set.
        GravshipUncoveredCells,

        // DebugViewSettings.drawShadows, which is SectionLayer_IndoorMask.Visible verbatim. Switched
        // off, the mask is not regenerated or drawn at all and rain falls through every roof on the
        // map at once — the one state that reproduces the whole-colony version of this bug, and one
        // no other probe here can distinguish from a mask that baked correctly.
        LayerVisible,
    }

    // Vanilla's two private hide predicates. Resolved once: a Probe step runs a handful of times in a
    // scenario, but UncoveredCells calls both of these per cell over the whole map.
    private static readonly MethodInfo HideCommon =
        AccessTools.Method(typeof(SectionLayer_IndoorMask), "HideCommon",
            new[] { typeof(Map), typeof(IntVec3) });

    private static readonly MethodInfo HideRainFogOverlay =
        AccessTools.Method(typeof(SectionLayer_IndoorMask), "HideRainFogOverlay",
            new[] { typeof(Map), typeof(IntVec3) });

    // The material the takeoff/landing cutscene bakes its flying mask with, so the gravship metrics
    // exercise the path Patch_IndoorMaskOverage's material gate is supposed to leave alone. Private
    // static on WorldComponent_GravshipController. The fallback is any material that is NOT one of
    // the two the clamp admits — FilledMask is the mask family's own spare — because what the bake
    // needs from it is only that it is not a section mask; if this ever resolves to IndoorMask by
    // accident the probe would measure the clamped path and report a pass for the wrong path.
    private static readonly Material GravshipMaskMaterial =
        AccessTools.Field(typeof(WorldComponent_GravshipController), "IndoorMaskGravship")
            ?.GetValue(null) as Material
        ?? MatBases.FilledMask;

    private readonly Metric metric;

    public string Name { get; }

    public IndoorMaskProbe(string name, Metric metric)
    {
        Name = name;
        this.metric = metric;
    }

    public float Read(Map map)
    {
        switch (metric)
        {
            case Metric.LayerVisible:
                return DebugViewSettings.drawShadows ? 1f : 0f;
            case Metric.UncoveredCells:
                return SectionCoverage(map).Uncovered;
            case Metric.Overage:
                return SectionCoverage(map).MaxOverage;
            case Metric.GravshipUncoveredCells:
                return GravshipCoverage(map).Uncovered;
            case Metric.GravshipOverage:
                return GravshipCoverage(map).MaxOverage;
            default:
                throw new ArgumentOutOfRangeException(nameof(metric), metric, null);
        }
    }

    private readonly struct Coverage
    {
        public readonly int Uncovered;
        public readonly float MaxOverage;

        public Coverage(int uncovered, float maxOverage)
        {
            Uncovered = uncovered;
            MaxOverage = maxOverage;
        }
    }

    // What the section masks actually baked, read back off the meshes rather than predicted.
    //
    // The layer is regenerated first because a section only bakes when it is dirty AND on screen:
    // without this, a probe would report every off-camera roof as uncovered and the number would say
    // more about where the scenario left the camera than about the mask. RegenerateLayerNow is
    // vanilla's own whole-map regenerate, the same call WorldComponent_GravshipController makes.
    private Coverage SectionCoverage(Map map)
    {
        map.mapDrawer.RegenerateLayerNow(typeof(SectionLayer_IndoorMask));

        var covered = new HashSet<IntVec3>();
        float maxOverage = 0f;

        foreach (Section section in SectionsOf(map))
        {
            if (section.GetLayer(typeof(SectionLayer_IndoorMask)) is SectionLayer layer)
            {
                foreach (LayerSubMesh subMesh in layer.subMeshes)
                {
                    // Only the two flavours that hide weather. FilledMask and DebugOverlay hang off
                    // this layer too and neither is a weather clip, so counting them would let a
                    // debug overlay stand in for a missing mask.
                    if (HidesWeather(subMesh))
                    {
                        maxOverage = Mathf.Max(
                            maxOverage, ReadQuads(subMesh.verts, Vector3.zero, covered));
                    }
                }
            }
        }

        return new Coverage(CountUncovered(map, covered), maxOverage);
    }

    private static bool HidesWeather(LayerSubMesh subMesh) =>
        subMesh.material == MatBases.IndoorMask || subMesh.material == MatBases.RoofedOutdoorMask;

    // The same question asked of BakeGravshipIndoorMesh, over the cells vanilla wants hidden on this
    // map. Feeding it the hidden set rather than an arbitrary rectangle matters: the bake applies the
    // same HideCommon test its section sibling does, so a rectangle of open ground would bake nothing
    // and the probe would read a contented 0 uncovered out of 0 expected.
    //
    // The bake is offset by the cell set's own centre, exactly as the cutscene does it (the mesh has
    // to sit at the origin so the renderer can fly it), so the coverage check undoes that offset
    // rather than comparing ship-local coordinates against map cells.
    private Coverage GravshipCoverage(Map map)
    {
        List<IntVec3> hidden = HiddenCells(map);
        if (hidden.Count == 0)
            return new Coverage(0, 0f);

        Vector3 center = CenterOf(hidden);
        LayerSubMesh baked = SectionLayer_IndoorMask.BakeGravshipIndoorMesh(
            map, hidden, hidden.Count, GravshipMaskMaterial, center);

        var covered = new HashSet<IntVec3>();
        float maxOverage = ReadQuads(baked.verts, center, covered);

        int uncovered = 0;
        for (int i = 0; i < hidden.Count; i++)
        {
            if (!covered.Contains(hidden[i]))
                uncovered++;
        }

        return new Coverage(uncovered, maxOverage);
    }

    // Walks a mask mesh four verts at a time — AppendQuadToMesh's own stride — recording which cell
    // each quad covers and returning the largest inflation seen.
    //
    // A quad is only credited with covering its cell if it spans the WHOLE cell. That is the property
    // that stops rain: a quad shrunk to half a cell leaves the other half unclipped, and a coverage
    // check that only asked "is there a quad near this cell" would pass a mask with holes in it.
    private static float ReadQuads(List<Vector3> verts, Vector3 meshOffset, HashSet<IntVec3> covered)
    {
        float maxOverage = 0f;

        for (int i = 0; i + 3 < verts.Count; i += 4)
        {
            float minX = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue;

            for (int v = i; v < i + 4; v++)
            {
                Vector3 p = verts[v] + meshOffset;
                minX = Mathf.Min(minX, p.x);
                minZ = Mathf.Min(minZ, p.z);
                maxX = Mathf.Max(maxX, p.x);
                maxZ = Mathf.Max(maxZ, p.z);
            }

            // The quad spans [x - overage, x + 1 + overage] on both axes, so its centre lands on the
            // cell's centre whatever the overage is, and flooring that centre names the cell.
            var cell = new IntVec3(
                Mathf.FloorToInt((minX + maxX) * 0.5f), 0, Mathf.FloorToInt((minZ + maxZ) * 0.5f));

            float overage = Mathf.Max(cell.x - minX, cell.z - minZ);
            maxOverage = Mathf.Max(maxOverage, overage);

            // Tolerance is float-comparison slack only — a quad that misses its cell by a thousandth
            // of a cell is bit noise from the offset arithmetic, while the failure this is looking
            // for (a quad that never got built, or one built inside its cell) is off by 0.16 or by a
            // whole cell.
            const float Epsilon = 0.001f;
            bool spansCell = minX <= cell.x + Epsilon && maxX >= cell.x + 1f - Epsilon
                && minZ <= cell.z + Epsilon && maxZ >= cell.z + 1f - Epsilon;

            if (spansCell)
                covered.Add(cell);
        }

        return maxOverage;
    }

    private static int CountUncovered(Map map, HashSet<IntVec3> covered)
    {
        int uncovered = 0;

        foreach (IntVec3 cell in map.AllCells)
        {
            if (IsHidden(map, cell) && !covered.Contains(cell))
                uncovered++;
        }

        return uncovered;
    }

    private static List<IntVec3> HiddenCells(Map map)
    {
        var hidden = new List<IntVec3>();

        foreach (IntVec3 cell in map.AllCells)
        {
            if (IsHidden(map, cell))
                hidden.Add(cell);
        }

        return hidden;
    }

    // Vanilla's own disjunction, in vanilla's own order: GenerateSectionLayer bakes a quad when
    // either predicate is true and skips the cell when neither is.
    private static bool IsHidden(Map map, IntVec3 cell)
    {
        // Named explicitly rather than left to a NullReferenceException from the Invoke: a renamed
        // vanilla predicate is exactly the kind of change this probe exists to notice, and "probe
        // threw, here is which member moved" is a report someone can act on.
        if (HideCommon == null || HideRainFogOverlay == null)
        {
            throw new MissingMethodException(
                "SectionLayer_IndoorMask.HideCommon/HideRainFogOverlay no longer resolve — vanilla's "
                + "weather-mask predicates have moved, so no coverage number here means anything.");
        }

        return (bool)HideCommon.Invoke(null, new object[] { map, cell })
            || (bool)HideRainFogOverlay.Invoke(null, new object[] { map, cell });
    }

    private static Vector3 CenterOf(List<IntVec3> cells)
    {
        var bounds = CellRect.FromCellList(cells);
        return bounds.CenterVector3;
    }

    // Every section on the map, reached through the one public accessor MapDrawer offers. The
    // sections array itself is private, and stepping the map in Section.Size strides gets the same
    // set without a second FieldRef to keep in step with vanilla.
    private static IEnumerable<Section> SectionsOf(Map map)
    {
        var seen = new HashSet<Section>();

        for (int x = 0; x < map.Size.x; x += Section.Size)
        {
            for (int z = 0; z < map.Size.z; z += Section.Size)
            {
                Section section = map.mapDrawer.SectionAt(new IntVec3(x, 0, z));

                if (section != null && seen.Add(section))
                {
                    yield return section;
                }
            }
        }
    }
}
