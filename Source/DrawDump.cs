using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// TEMPORARY DIAGNOSTIC for docs/EAVE-SEAM.md — deleted once the eave seam is understood.
//
// The tint pass (seam_tint) proved the seam rows are drawn — and in the enclosed room NOT drawn —
// by geometry carrying MatBases.SunShadow, the material our Regenerate Prefix writes. Yet the
// AUTHORED vertex lists are byte-identical between the enclosed room and the one-cell-gap room, and
// the submission is a bare Graphics.DrawMesh(mesh, identity, material): identical baked meshes
// cannot produce different pixels. So the remaining suspect is the BAKED Mesh object at draw time —
// this logs it at the only moment that matters, inside DrawLayer on the frame the screenshot is
// taken, instead of trusting the build-side lists a bake supposedly copied in.
//
// Per SunShadow submesh of any section near the two test rooms it logs: a content hash of
// mesh.vertices + mesh.colors32 alpha (comparable across the two rooms' same-phase sections), the
// counts, the finalized/disabled flags, and every baked vertex in the roofline band rows so the two
// rooms can be diffed vert-for-vert on exactly what the GPU was handed.
public static class DrawDump
{
    public static bool Enabled;

    // Both same-phase rooms plus margin. Section rects, not cells, are tested against this, so the
    // window errs wide: rooms span x 108..153, roofline z 169..171.
    private const int MinX = 90;
    private const int MaxX = 175;
    private const int MinZ = 150;
    private const int MaxZ = 190;

    // Baked-vert dump rows: the roofline band and one row either side.
    private const float VertMinZ = 160f;
    private const float VertMaxZ = 190f;

    public static void Layer(SectionLayer layer, string path)
    {
        if (!Enabled)
            return;

        Section section = SectionLayerAccess.GetSection(layer);
        CellRect rect = section.CellRect;
        if (rect.maxX < MinX || rect.minX > MaxX || rect.maxZ < MinZ || rect.minZ > MaxZ)
            return;

        for (int i = 0; i < layer.subMeshes.Count; i++)
            Submesh(section, layer.subMeshes[i], i, path);
    }

    // FNV-1a over raw float bits — an order-sensitive content hash, so two meshes hash equal only
    // when their vertex streams are equal element for element.
    private static uint Fnv(uint h, float f) =>
        unchecked((h ^ (uint)f.GetHashCode()) * 16777619u);

    private static void Submesh(Section section, LayerSubMesh sm, int index, string path)
    {
        // mesh.vertices / colors32 allocate copies per call — acceptable for a flag-gated
        // diagnostic that lives for a handful of frames.
        Vector3[] vs = sm.mesh.vertices;
        Color32[] cs = sm.mesh.colors32;
        bool alphas = cs != null && cs.Length == vs.Length;

        uint h = 2166136261u;
        for (int k = 0; k < vs.Length; k++)
        {
            h = Fnv(h, vs[k].x);
            h = Fnv(h, vs[k].y);
            h = Fnv(h, vs[k].z);
            if (alphas)
                h = unchecked((h ^ cs[k].a) * 16777619u);
        }

        // The index buffer is hashed as well as the vertex stream: every earlier "identical mesh"
        // comparison in this investigation (SeamDump.Mesh included) diffed verts and alphas only.
        // Two meshes with equal vertex streams but different TRIANGLES render differently while
        // passing all of those diffs — the one remaining way the day-2 paradox can be real.
        int[] tris = sm.mesh.triangles;
        uint th = 2166136261u;
        for (int k = 0; k < tris.Length; k++)
            th = unchecked((th ^ (uint)tris[k]) * 16777619u);

        Log.Message(
            $"SEAMDRAW f{Time.frameCount} {path} sect({section.botLeft.x},{section.botLeft.z}) "
            + $"sm{index} mat={(sm.material == null ? "null" : sm.material.name)} "
            + $"fin={sm.finalized} dis={sm.disabled} verts={vs.Length} hash={h:X8} "
            + $"tris={tris.Length / 3} trihash={th:X8}");

        if (sm.material != MatBases.SunShadow)
            return;

        for (int k = 0; k < vs.Length; k++)
        {
            Vector3 v = vs[k];
            bool inBand = v.z >= VertMinZ && v.z <= VertMaxZ && v.x >= MinX && v.x <= MaxX;
            if (inBand)
            {
                byte a = alphas ? cs[k].a : (byte)0;
                Log.Message(
                    $"SEAMDRAWVERT f{Time.frameCount} sect({section.botLeft.x},{section.botLeft.z}) "
                    + $"#{k} pos=({v.x:F2},{v.y:F4},{v.z:F2}) a={a}");
            }
        }

        // Every triangle with at least one vertex in the band rows, as positions (not indices, which
        // are not comparable across two independently-authored meshes). This is the actual geometry
        // the GPU rasterises at the roofline — the thing no diff so far has looked at.
        for (int k = 0; k + 2 < tris.Length; k += 3)
        {
            Vector3 a = vs[tris[k]];
            Vector3 b = vs[tris[k + 1]];
            Vector3 c = vs[tris[k + 2]];
            bool touches =
                (a.z >= VertMinZ && a.z <= VertMaxZ && a.x >= MinX && a.x <= MaxX)
                || (b.z >= VertMinZ && b.z <= VertMaxZ && b.x >= MinX && b.x <= MaxX)
                || (c.z >= VertMinZ && c.z <= VertMaxZ && c.x >= MinX && c.x <= MaxX);
            if (touches)
            {
                byte aa = alphas ? cs[tris[k]].a : (byte)0;
                byte ab = alphas ? cs[tris[k + 1]].a : (byte)0;
                byte ac = alphas ? cs[tris[k + 2]].a : (byte)0;
                Log.Message(
                    $"SEAMDRAWTRI f{Time.frameCount} sect({section.botLeft.x},{section.botLeft.z}) "
                    + $"({a.x:F2},{a.z:F2},{aa})-({b.x:F2},{b.z:F2},{ab})-({c.x:F2},{c.z:F2},{ac})");
            }
        }
    }

    // Once per logged frame: the two per-frame inputs the shader combines with the mesh — the cast
    // vector and the material tint. Identical for both rooms by construction (shared material), but
    // logging them costs nothing and retires the question.
    private static int lastGlobalsFrame = -1;

    public static void Globals()
    {
        if (!Enabled || Time.frameCount == lastGlobalsFrame)
            return;

        lastGlobalsFrame = Time.frameCount;
        Vector4 cast = Shader.GetGlobalVector(ShaderPropertyIDs.MapSunLightDirection);
        Color c = MatBases.SunShadow.color;
        Log.Message(
            $"SEAMDRAWGLOBAL f{Time.frameCount} castVect=({cast.x:F4},{cast.y:F4},{cast.z:F4},{cast.w:F4}) "
            + $"color=({c.r:F3},{c.g:F3},{c.b:F3},{c.a:F3}) queue={MatBases.SunShadow.renderQueue}");
    }
}

// A postfix on the layer's own DrawLayer sees every submission regardless of which caller invoked
// it (MapDrawer.DrawMapMesh routes in-view sections through Section.DrawSection -> every layer, and
// out-of-view ones through Section.DrawDynamicSections -> only layers passing ShouldDrawDynamic).
// Both test rooms are fully in view, so the path split is not distinguished here — a missing
// SEAMDRAW line for a window section on a logged frame would itself be the finding.
[HarmonyPatch]
public static class Patch_DrawDump_SunShadowsDrawLayer
{
    static MethodBase TargetMethod() =>
        AccessTools.Method(AccessTools.TypeByName("Verse.SectionLayer_SunShadows"), "DrawLayer");

    static void Postfix(SectionLayer __instance)
    {
        DrawDump.Globals();
        DrawDump.Layer(__instance, "draw");
    }
}
