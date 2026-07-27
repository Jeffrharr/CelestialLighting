using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// See DESIGN.md "Shadow tilt across the map" for the full writeup, including the shader evidence
// summarised below.
//
// Vanilla draws every section's sun-shadow mesh with the exact same shadow vector, because
// SkyManager.SetSunShadowVector sets it via Shader.SetGlobalVector — one value for the whole map,
// every draw call, every material. There is no vanilla mechanism for two sections on opposite
// sides of the same map to render different shadow lengths in the same frame.
//
// This patch replaces SectionLayer_SunShadows.DrawLayer() so each section draws its shadow mesh
// with its own MaterialPropertyBlock carrying a slightly rescaled shadow vector (same direction,
// length nudged up or down a few percent depending on where the section sits along the shadow
// axis).
//
// SectionLayer_SunShadows is `internal`, so it can't be named directly (typeof(...) or as a
// parameter type) from this assembly — TargetMethod() looks it up by name instead, and the
// Prefix below takes its public base class SectionLayer_Dynamic, which Harmony accepts since the
// runtime instance is still the internal subclass.
//
// --- SHADER EVIDENCE (issue #11, 2026-07) — this used to be guesswork; it is now read off the
// shipped asset. Scanning RimWorldLinux_Data/resources.assets:
//
//   * The material MatBases.SunShadow loads is bound to the shader "Custom/Sun shadow", whose
//     Properties {} block does not declare _CastVect at all. The literal string "_CastVect" occurs
//     exactly five times in the whole asset file: twice in the SunShadow / SunShadowFade materials'
//     serialized m_SavedProperties, and three times inside compiled shader blobs (as a member of the
//     "$Globals" constant buffer). It never occurs as a SerializedProperty name/description pair
//     ("_SwayHead" + "Sway head", "_Rotation" + "Rotation", ...), which is how a real Properties {}
//     entry is stored. So _CastVect is a plain program uniform fed by Shader.SetGlobalVector.
//
//   * The vertex program amounts to
//         position.xyz = in_COLOR0.www * _CastVect.xyz + in_POSITION0.xyz
//     — _CastVect.xyz *is* the extrusion vector: direction and length together, scaled per vertex by
//     the caster-height alpha SectionLayer_SunShadows.Regenerate() bakes into the mesh colours.
//     Rescaling that vector is precisely the knob this patch wants, and it is genuinely consumed.
//
//   * _CastVect.w is read by nothing. The fragment program emits _Color. That — not an inert
//     property block — is the entire explanation for the earlier live A/B
//     (Tests/Scenarios/penumbra_lowsun), where folding shadow opacity into a per-section _CastVect.w
//     produced *zero* visible change: opacity lives in the material colour SkyManager lerps, so .w
//     was always a dead channel. That experiment therefore proves nothing about whether the property
//     block binds. Opacity now lives in Patch_ShadowStrength, which is the correct home for it.
//
//   * "_PenumbraSoftness" does not occur anywhere in the game's assets. The forward hook that used to
//     push it here was provably a silent no-op and has been deleted rather than left to mislead.
//
// --- RESIDUAL UNCERTAINTY, stated plainly. Whether a MaterialPropertyBlock can override a $Globals
// uniform the shader never declared in Properties {} is engine behaviour, not something readable out
// of the asset file. The evidence leans strongly toward "yes": the per-draw constant buffer must be
// filled by resolving every $Globals member *by name* against (property block -> material sheet ->
// global sheet), because that is the only way engine-supplied uniforms such as unity_MatrixVP and
// unity_FogColor — likewise never in a Properties {} block — get any value at all. The apparent
// counter-example is that the shipped SunShadow material carries a baked _CastVect of
// (-3.145, 0, -1.147, 0) which demonstrably does *not* freeze in-game shadows; that is explained by a
// Material's own sheet being reconciled against its shader's declared property list when it loads,
// a reconciliation a MaterialPropertyBlock (which is bound to no shader) never undergoes.
// The decisive check is still a live edge-vs-centre shadow-length diff at low sun. Until someone runs
// it this stays, rather than being deleted on an inference. The failure mode remains safe either way:
// if the override does not bind, every section renders with the global vector — exactly vanilla's
// uniform look, no exception and no visual glitch.
//
// --- COST. The per-draw property block defeats Unity's draw batching for shadow sections: roughly
// 6 (close zoom) to 30 (zoomed out) shadow draws per frame stop merging, against RimWorld's existing
// several-hundred-draw baseline. Unprofiled, estimated well under 0.2 ms/frame. That is the price of
// the effect; if the live diff ever shows the override does not bind, delete this whole file and the
// cost goes with it.
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

        // Shadow OPACITY — including the angular-size penumbra attenuation near the horizon — is
        // handled globally in Patch_ShadowStrength (GenCelestial.CurShadowStrength), which is the
        // value SkyManager lerps MatBases.SunShadow.color by and is what actually darkens the ground.
        // The shader never reads _CastVect.w at all (see the shader-evidence note above), so this
        // just carries lightInfo.intensity through unchanged — matching the .w vanilla itself writes,
        // so overriding the vector cannot accidentally disagree with the global on that channel.
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
