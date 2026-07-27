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
// The one further change since: the per-cell `Building` lookups now go through EaveShadowGrid,
// which resolves an effective caster HEIGHT per cell rather than an edifice, so §15's eaves (roofed
// cells that are not enclosed — porches, overhangs) can cast a roofline shadow. That substitution
// is exactly equivalent to vanilla's own tests whenever §15 is off: vanilla's `building == null`
// branches are indistinguishable from "height 0" here, because every neighbour test only runs once
// the centre cell's own height is already > 0, so a null neighbour's 0 satisfies `< centreHeight`
// for precisely the reason `building == null` did.
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

        float y = AltitudeLayer.Shadows.AltitudeFor();
        CellRect cellRect = section.CellRect;
        cellRect.ClipInsideMap(map);

        EaveShadowGrid casters =
            EaveShadowGrid.Build(map, cellRect, CelestialLightingFeatures.EaveShadows);

        // Section 3's shadow tilt, baked. One multiplier for the whole section — it is a function of
        // where the section sits along the shadow axis, not of the cell — so it is resolved once
        // here and multiplied into every caster's vertex alpha below. The axis it is computed
        // against is the one MapComponent_SunShadowAxis has stored, NOT the live shadow vector, so
        // that every section on the map agrees no matter what order they were last rebuilt in; that
        // component is also what rebuilds them once the axis has drifted far enough to matter.
        float lengthScale = MapComponent_SunShadowAxis.LengthScaleFor(map, cellRect);

        LayerSubMesh subMesh = __instance.GetSubMesh(MatBases.SunShadow);
        subMesh.Clear(MeshParts.All);
        subMesh.verts.Capacity = cellRect.Area * 2;
        subMesh.tris.Capacity = cellRect.Area * 4;
        subMesh.colors.Capacity = cellRect.Area * 2;

        for (int i = cellRect.minX; i <= cellRect.maxX; i++)
        {
            for (int j = cellRect.minZ; j <= cellRect.maxZ; j++)
            {
                float staticSunShadowHeight = casters.At(i, j);
                if (!(staticSunShadowHeight > 0f))
                    continue;

                // The alpha the "Custom/Sun shadow" vertex program multiplies the global extrusion
                // vector by, so it is both "how tall is this caster" and — since §3's tilt is baked
                // rather than pushed per draw — "how far along the shadow axis is this section".
                // Formulas.ShadowCasterAlphaByte owns the clamp: an unchecked float->byte cast of an
                // over-1.0 product would WRAP, turning a wall's shadow into a stub.
                Color32 item = new Color32(
                    0, 0, 0, Formulas.ShadowCasterAlphaByte(staticSunShadowHeight, lengthScale));

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

                if (i > 0 && casters.At(i - 1, j) < staticSunShadowHeight)
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
                if (i < map.Size.x - 1 && casters.At(i + 1, j) < staticSunShadowHeight)
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
                if (j > 0 && casters.At(i, j - 1) < staticSunShadowHeight)
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
                if (j < map.Size.z - 1 && casters.At(i, j + 1) < staticSunShadowHeight)
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

        if (subMesh.verts.Count > 0)
        {
            subMesh.FinalizeMesh(MeshParts.Verts | MeshParts.Tris | MeshParts.Colors);
            // Matches vanilla's own (fixed, not distance-derived) mesh bounds inflation.
            subMesh.mesh.bounds = new Bounds(Vector3.zero, new Vector3(1000f, 1000f, 1000f));
        }

        return false;
    }
}
