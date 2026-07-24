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

        Building[] innerArray = map.edificeGrid.InnerArray;
        float y = AltitudeLayer.Shadows.AltitudeFor();
        CellRect cellRect = section.CellRect;
        cellRect.ClipInsideMap(map);

        LayerSubMesh subMesh = __instance.GetSubMesh(MatBases.SunShadow);
        subMesh.Clear(MeshParts.All);
        subMesh.verts.Capacity = cellRect.Area * 2;
        subMesh.tris.Capacity = cellRect.Area * 4;
        subMesh.colors.Capacity = cellRect.Area * 2;

        CellIndices cellIndices = map.cellIndices;

        for (int i = cellRect.minX; i <= cellRect.maxX; i++)
        {
            for (int j = cellRect.minZ; j <= cellRect.maxZ; j++)
            {
                Building building = innerArray[cellIndices.CellToIndex(i, j)];
                if (building == null || !(building.def.staticSunShadowHeight > 0f))
                    continue;

                float staticSunShadowHeight = building.def.staticSunShadowHeight;
                Color32 item = new Color32(0, 0, 0, (byte)(255f * staticSunShadowHeight));

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

                if (i > 0)
                {
                    building = innerArray[cellIndices.CellToIndex(i - 1, j)];
                    if (building == null || building.def.staticSunShadowHeight < staticSunShadowHeight)
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
                }
                if (i < map.Size.x - 1)
                {
                    building = innerArray[cellIndices.CellToIndex(i + 1, j)];
                    if (building == null || building.def.staticSunShadowHeight < staticSunShadowHeight)
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
                }
                if (j > 0)
                {
                    building = innerArray[cellIndices.CellToIndex(i, j - 1)];
                    if (building == null || building.def.staticSunShadowHeight < staticSunShadowHeight)
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
                }
                // The added fourth face — vanilla has no equivalent block for this direction.
                if (j < map.Size.z - 1)
                {
                    building = innerArray[cellIndices.CellToIndex(i, j + 1)];
                    if (building == null || building.def.staticSunShadowHeight < staticSunShadowHeight)
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
