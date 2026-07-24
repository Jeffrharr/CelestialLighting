using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// EXPERIMENTAL — see DESIGN.md "Shadow tilt across the map" for the full writeup, including why
// this might silently do nothing depending on how MatBases.SunShadow's shader is authored.
//
// Vanilla draws every section's sun-shadow mesh with the exact same shadow vector, because
// SkyManager.SetSunShadowVector sets it via Shader.SetGlobalVector — one value for the whole map,
// every draw call, every material. There is no vanilla mechanism for two sections on opposite
// sides of the same map to render different shadow lengths in the same frame.
//
// This patch replaces SectionLayer_SunShadows.DrawLayer() so each section draws its shadow mesh
// with its own MaterialPropertyBlock carrying a slightly rescaled shadow vector (same direction,
// length nudged up or down a few percent depending on where the section sits along the shadow
// axis). This relies on the shader's "_CastVect" being declared as a real per-material property
// (not only a global set via Shader.SetGlobalVector) so a MaterialPropertyBlock can override it
// per draw call — we can't inspect the compiled shader asset from decompiled C# to confirm that
// ahead of time. If it isn't a real per-material property, MaterialPropertyBlock.SetVector on an
// undeclared name is a silent no-op in Unity: every section still renders with the global vector,
// i.e. this degrades to exactly vanilla's uniform look, with no exception and no visual glitch.
// Verify in-game (see DESIGN.md) before assuming this looks any different from vanilla.
// SectionLayer_SunShadows is `internal`, so it can't be named directly (typeof(...) or as a
// parameter type) from this assembly — TargetMethod() looks it up by name instead, and the
// Prefix below takes its public base class SectionLayer_Dynamic, which Harmony accepts since the
// runtime instance is still the internal subclass.
[HarmonyPatch]
public static class Patch_ShadowTilt
{
    static MethodBase TargetMethod() =>
        AccessTools.Method(AccessTools.TypeByName("Verse.SectionLayer_SunShadows"), "DrawLayer");

    // How much the shadow vector's length can grow/shrink from the map's center to its edge.
    // 0.15 means the far edge (along the current shadow axis) casts shadows up to ~15% longer
    // than the center, and the opposite edge ~15% shorter — kept small deliberately so it reads
    // as "one side of the map looks a little different" rather than an obviously stretched
    // shadow near the map border.
    private const float MaxLengthVariation = 0.15f;

    // Reused across draw calls instead of allocated per-section per-frame; Clear() resets it
    // before each SetVector so state never leaks between sections.
    private static readonly MaterialPropertyBlock PropBlock = new MaterialPropertyBlock();

    static bool Prefix(SectionLayer_Dynamic __instance)
    {
        if (!__instance.Visible)
            return false;

        __instance.RefreshSubMeshBounds();

        Section section = SectionLayerAccess.GetSection(__instance);
        Map map = section.map;

        // Goes through the patched method (Patch_ShadowDirection's Postfix runs on every call, this
        // one included), so both vector and intensity already reflect our elevation-based model —
        // reusing lightInfo.intensity here instead of a second, independent GenCelestial.CurShadowStrength
        // call keeps this in agreement with Patch_ShadowDirection's existence/intensity decision
        // (e.g. zero at night) rather than falling back to vanilla's own separate curve.
        GenCelestial.LightInfo lightInfo = GenCelestial.GetLightSourceInfo(map, GenCelestial.LightType.Shadow);
        Vector2 shadowDir = lightInfo.vector;
        float lengthScale = ComputeLengthScale(map, section, shadowDir);
        float shadowStrength = lightInfo.intensity;

        List<LayerSubMesh> subMeshes = __instance.subMeshes;
        for (int i = 0; i < subMeshes.Count; i++)
        {
            LayerSubMesh subMesh = subMeshes[i];
            if (IsDrawable(subMesh))
                DrawSubMesh(subMesh, shadowDir, lengthScale, shadowStrength);
        }

        return false; // skip the original DrawLayer/base.DrawLayer entirely — we've done its job above
    }

    private static bool IsDrawable(LayerSubMesh subMesh) => subMesh.finalized && !subMesh.disabled;

    private static void DrawSubMesh(LayerSubMesh subMesh, Vector2 shadowDir, float lengthScale, float shadowStrength)
    {
        PropBlock.Clear();
        PropBlock.SetVector(ShaderPropertyIDs.MapSunLightDirection,
            new Vector4(shadowDir.x * lengthScale, 0f, shadowDir.y * lengthScale, shadowStrength));

        Graphics.DrawMesh(subMesh.mesh, Matrix4x4.identity, subMesh.material, subMesh.renderLayer,
            camera: null, submeshIndex: 0, properties: PropBlock);
    }

    // Impure boundary: pulls plain floats out of the live Map/Section/Vector2 and hands them to
    // Formulas, which does the actual projection/clamping math and is covered by offline unit
    // tests (degenerate zero-vector shadow direction, degenerate zero-extent map, clamping at the
    // map edges, sign relative to the shadow axis).
    private static float ComputeLengthScale(Map map, Section section, Vector2 shadowDir)
    {
        Vector3 mapCenter = new Vector3(map.Size.x / 2f, 0f, map.Size.z / 2f);
        Vector3 sectionCenter = section.CellRect.CenterVector3;

        float positionFraction = Formulas.ShadowLengthPositionFraction(
            offsetX: sectionCenter.x - mapCenter.x,
            offsetZ: sectionCenter.z - mapCenter.z,
            shadowDirX: shadowDir.x,
            shadowDirZ: shadowDir.y,
            mapSizeX: map.Size.x,
            mapSizeZ: map.Size.z);

        return Formulas.ShadowLengthScale(positionFraction, MaxLengthVariation);
    }
}
