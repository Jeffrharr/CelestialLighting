using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// Vanilla's SectionLayer_SunShadows.Regenerate() builds a "wall" shadow quad on a building's
// exposed west, east, and south edges (whichever neighbor cell is empty or has a shorter
// building), but there is no fourth `if` block for the north edge (the `j + 1` neighbor) at all —
// confirmed by decompiling the method in full. This went unnoticed under vanilla's own narrow
// day/night shadow-angle range, but Patch_ShadowDirection's full elevation/azimuth simulator
// sweeps shadows through every compass direction across the day and season, which makes the
// missing north face visible: a wall whose north side should be throwing a shadow (sun somewhere
// south of it) renders with a gap instead of a quad.
//
// This Prefix skips the original entirely and reimplements it verbatim, with a fourth block added
// for the `j + 1` neighbor. That block mirrors the existing south-facing block's triangle-winding
// pattern but traverses the shared edge in the opposite direction (matching how the existing
// west/east blocks mirror each other for their opposite sides) so the added quad's outward normal
// points north instead of south. Everything else here is copied from vanilla's Regenerate() —
// this is not a redesign, just the one missing case filled in.
//
// Two further changes since. The first: the per-cell `Building` lookups now go through EaveShadowGrid,
// which resolves an effective caster HEIGHT per cell rather than an edifice, so §15's eaves (roofed
// cells that are not enclosed — porches, overhangs) can cast a roofline shadow. That substitution
// is exactly equivalent to vanilla's own tests whenever §15 is off: vanilla's `building == null`
// branches are indistinguishable from "height 0" here, because every neighbour test only runs once
// the centre cell's own height is already > 0, so a null neighbour's 0 satisfies `< centreHeight`
// for precisely the reason `building == null` did.
//
// The second: a caster cell with no shorter neighbour emits nothing at all, where vanilla still
// emits its (invisible, zero-alpha) footprint quad. See EmitCell — this is what keeps admitting a
// mountain's whole interior as a caster from costing vertices for geometry that cannot draw.
//
// Because this Prefix replaces the whole method body, any OTHER mod's transpiler on Regenerate is
// skipped — including Perspective: Eaves, which is why §15 exists at all and why About.xml declares
// that mod incompatible rather than merely load-ordered. See DESIGN.md §15.
[HarmonyPatch]
public static class Patch_ShadowMeshPerimeter
{
    private static readonly Color32 LowVertexColor = new Color32(0, 0, 0, 0);

    static MethodBase TargetMethod() =>
        AccessTools.Method(AccessTools.TypeByName("Verse.SectionLayer_SunShadows"), "Regenerate");

    static bool Prefix(SectionLayer_Dynamic __instance)
    {
        if (!MatBases.SunShadow.shader.isSupported)
            return false;

        Section section = SectionLayerAccess.GetSection(__instance);
        Map map = section.map;

        // Shadowless map (Biomes! Caverns' cavern biomes, or any biome setting vanilla's
        // disableShadows). Vanilla's own SectionLayer_SunShadows.Visible reads the same flag, so the
        // mesh we are about to build could never be drawn; Biomes! Caverns additionally Prefix-skips
        // DrawLayer on its caverns. Returning false keeps the vanilla body skipped — this Prefix
        // REPLACES Regenerate rather than augmenting it, so falling through to the original would
        // build vanilla's mesh instead of none at all, which is the opposite of what is wanted.
        //
        // Worth more here than at the other Gate B sites: this is the whole per-section mesh build,
        // and on a cavern map every section was paying for it every time the shadow axis moved.
        if (!MapSky.DrawsShadows(map))
            return false;

        float y = AltitudeLayer.Shadows.AltitudeFor();
        CellRect cellRect = section.CellRect;
        cellRect.ClipInsideMap(map);

        EaveShadowGrid casters =
            EaveShadowGrid.Build(map, cellRect, CelestialLightingFeatures.EaveShadows);

        LayerSubMesh subMesh = __instance.GetSubMesh(MatBases.SunShadow);
        subMesh.Clear(MeshParts.All);
        subMesh.verts.Capacity = cellRect.Area * 2;
        subMesh.tris.Capacity = cellRect.Area * 4;
        subMesh.colors.Capacity = cellRect.Area * 2;

        for (int i = cellRect.minX; i <= cellRect.maxX; i++)
        {
            for (int j = cellRect.minZ; j <= cellRect.maxZ; j++)
                EmitCell(subMesh, casters, map, i, j, y);
        }

        if (subMesh.verts.Count > 0)
        {
            subMesh.FinalizeMesh(MeshParts.Verts | MeshParts.Tris | MeshParts.Colors);
            // Matches vanilla's own (fixed, not distance-derived) mesh bounds inflation.
            subMesh.mesh.bounds = new Bounds(Vector3.zero, new Vector3(1000f, 1000f, 1000f));
        }

        return false;
    }

    // One cell's contribution: a flat footprint quad plus a skirt on each of the four edges whose
    // neighbour is shorter. Extracted from the loop above only so the two early exits can be plain
    // returns — vanilla's body is otherwise transcribed unchanged.
    private static void EmitCell(
        LayerSubMesh subMesh, EaveShadowGrid casters, Map map, int i, int j, float y)
    {
        float staticSunShadowHeight = casters.At(i, j);

        // TEMPORARY DIAGNOSTIC (docs/EAVE-SEAM.md) — removed once the eave seam is understood.
        if (SeamDump.Covers(i, j))
            SeamDump.Cell(i, j, staticSunShadowHeight, casters, map);

        if (!(staticSunShadowHeight > 0f))
            return;

        // Which edges this cell is actually on the outside of. Vanilla asks these four questions
        // inline, one per block, after the footprint quad is already committed; asking them up front
        // is what lets the whole cell be skipped below.
        bool west = i > 0 && casters.At(i - 1, j) < staticSunShadowHeight;
        bool east = i < map.Size.x - 1 && casters.At(i + 1, j) < staticSunShadowHeight;
        bool south = j > 0 && casters.At(i, j - 1) < staticSunShadowHeight;
        bool north = j < map.Size.z - 1 && casters.At(i, j + 1) < staticSunShadowHeight;

        // A cell buried inside a caster blob — every neighbour at least as tall — draws nothing: no
        // skirt block below can fire, and the footprint quad carries zero alpha so the shader never
        // extrudes it. Vanilla emits it anyway, which is free enough on a colony's few hundred wall
        // cells but is not free now that a whole cave's interior classifies as a caster: a 60x60
        // mined-out cavern would otherwise pay 14,400 vertices per regenerate to render nothing.
        //
        // The four skirt blocks index back into this quad (count+1 .. count+3), so skipping it is
        // only sound while none of them fires — which is exactly the condition tested here.
        if (!west && !east && !south && !north)
            return;

        // The alpha the "Custom/Sun shadow" vertex program multiplies the global extrusion
        // vector by — i.e. "how tall is this caster", and nothing else, exactly as vanilla
        // intends it (see DESIGN.md §3 for the shader scan that established this).
        // Formulas.ShadowCasterAlphaByte owns the clamp vanilla's own plain
        // `(byte)(255f * height)` lacks: that cast is unchecked, and staticSunShadowHeight
        // is an unvalidated ThingDef float, so any modded def declaring more than 1.0 WRAPS
        // (1.2 -> 306 -> 50) and collapses that building's shadow to a stub. Vanilla's own
        // defs never exceed 1.0, but this Prefix replaces the whole method for every mod in
        // the load order, so the clamp is ours to provide.
        Color32 item = new Color32(
            0, 0, 0, Formulas.ShadowCasterAlphaByte(staticSunShadowHeight));

        // Flat footprint quad (unmoved by the shader — LowVertexColor's zero alpha).
        int count = subMesh.verts.Count;
        subMesh.verts.Add(new Vector3(i, y, j));
        subMesh.verts.Add(new Vector3(i, y, j + 1));
        subMesh.verts.Add(new Vector3(i + 1, y, j + 1));
        subMesh.verts.Add(new Vector3(i + 1, y, j));
        subMesh.colors.Add(LowVertexColor);
        subMesh.colors.Add(LowVertexColor);
        subMesh.colors.Add(LowVertexColor);
        subMesh.colors.Add(LowVertexColor);

        int count2 = subMesh.verts.Count;
        subMesh.tris.Add(count2 - 4);
        subMesh.tris.Add(count2 - 3);
        subMesh.tris.Add(count2 - 2);
        subMesh.tris.Add(count2 - 4);
        subMesh.tris.Add(count2 - 2);
        subMesh.tris.Add(count2 - 1);

        if (west)
        {
            int count3 = subMesh.verts.Count;
            subMesh.verts.Add(new Vector3(i, y, j));
            subMesh.verts.Add(new Vector3(i, y, j + 1));
            subMesh.colors.Add(item);
            subMesh.colors.Add(item);
            subMesh.tris.Add(count + 1);
            subMesh.tris.Add(count);
            subMesh.tris.Add(count3);
            subMesh.tris.Add(count3);
            subMesh.tris.Add(count3 + 1);
            subMesh.tris.Add(count + 1);
        }
        if (east)
        {
            int count4 = subMesh.verts.Count;
            subMesh.verts.Add(new Vector3(i + 1, y, j + 1));
            subMesh.verts.Add(new Vector3(i + 1, y, j));
            subMesh.colors.Add(item);
            subMesh.colors.Add(item);
            subMesh.tris.Add(count + 2);
            subMesh.tris.Add(count4);
            subMesh.tris.Add(count4 + 1);
            subMesh.tris.Add(count4 + 1);
            subMesh.tris.Add(count + 3);
            subMesh.tris.Add(count + 2);
        }
        if (south)
        {
            int count5 = subMesh.verts.Count;
            subMesh.verts.Add(new Vector3(i, y, j));
            subMesh.verts.Add(new Vector3(i + 1, y, j));
            subMesh.colors.Add(item);
            subMesh.colors.Add(item);
            subMesh.tris.Add(count);
            subMesh.tris.Add(count + 3);
            subMesh.tris.Add(count5);
            subMesh.tris.Add(count + 3);
            subMesh.tris.Add(count5 + 1);
            subMesh.tris.Add(count5);
        }
        // The added fourth face — vanilla has no equivalent block for this direction.
        if (north)
        {
            int count6 = subMesh.verts.Count;
            subMesh.verts.Add(new Vector3(i + 1, y, j + 1));
            subMesh.verts.Add(new Vector3(i, y, j + 1));
            subMesh.colors.Add(item);
            subMesh.colors.Add(item);
            subMesh.tris.Add(count + 2);
            subMesh.tris.Add(count + 1);
            subMesh.tris.Add(count6);
            subMesh.tris.Add(count + 1);
            subMesh.tris.Add(count6 + 1);
            subMesh.tris.Add(count6);
        }
    }
}
