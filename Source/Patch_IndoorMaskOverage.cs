using HarmonyLib;
using Verse;

namespace CelestialLighting;

// The eave-seam fix (docs/EAVE-SEAM.md, issue #63's real cause).
//
// Vanilla clips sun shadows off roofed ground with an invisible depth pass: SectionLayer_IndoorMask
// bakes a quad per hidden cell at AltitudeLayer.MetaOverlays, queued at Transparent+160/165 —
// immediately before the sun-shadow pass at Transparent+170 — so shadow fragments under a roof are
// z-rejected rather than drawn. That design is sound, and §15's roofline shadows rely on it exactly
// the way vanilla's wall shadows do: the cast band ends where the mask begins.
//
// The seam comes from the two mask flavours disagreeing about where that is. A roofed cell in a
// ProperRoom bakes into MatBases.IndoorMask with `overage = 0.16f` — AppendQuadToMesh inflates the
// quad 0.16 cells on EVERY side — while the same roof over a not-proper room (a porch, or a room
// with any one wall cell missing) bakes into MatBases.RoofedOutdoorMask with overage 0, flush with
// the roofline. So precisely when a room becomes enclosed, the invisible clip region grows 0.16
// cells past the roofline and eats the roofline-adjacent 3-4 px of the cast band, leaving a lighter
// sliver between the band and the roof's own shade. Every earlier fix attempt failed because the
// defect was never in any geometry we author: identical shadow meshes were being clipped by a mask
// whose size depends on room enclosure.
//
// The fix clamps the overage to zero, making the enclosed-room mask flush like the porch mask
// vanilla already ships. Consequences, all bounded:
//   - The cast band now reaches the roofline in enclosed rooms — the seam closes. Nothing is drawn
//     twice: this removes a clip, it adds no darkening, so the no-double-darken rule §15b lives
//     under is satisfied by construction.
//   - Weather drawn above the mask's queue keeps being masked over exactly the roofed cells; the
//     0.16-cell margin by which rain used to stop OUTSIDE an enclosed roofline goes flush instead —
//     which is what porches, gapped rooms, and every RoofedOutdoorMask cell already looked like.
//     Coverage of a hidden cell is never reduced: a flush quad still spans its whole cell, and the
//     quads tile edge to edge. indoor_mask_uncovered pins that as a number rather than an argument.
//   - Fogged cells take the same flush edge, because they bake through the same indoor mesh.
//
// TWO NARROWINGS, BOTH ABOUT LIVING BESIDE OTHER MODS AND BESIDE FUTURE VANILLA.
//
// It clamps only the SECTION mask, by mesh identity. AppendQuadToMesh is public and static, and its
// other shipped caller is SectionLayer_IndoorMask.BakeGravshipIndoorMesh, which
// WorldComponent_GravshipController runs three times per takeoff/landing to bake the free submeshes
// that fly with the ship and curtain the terrain. Those cutscene meshes are drawn moving, over a
// composited capture, by code with no eave seam in it — there is nothing there for this fix to fix,
// so reaching them was only ever blast radius. The section path is exactly the meshes built from
// MatBases.IndoorMask (the 0.16 flavour) and MatBases.DebugOverlay (drawIndoorMask's mirror of it,
// which has to keep showing the real clip region or it lies to whoever switched it on); the gravship
// bakes pass their own free submeshes with the controller's private materials, so an identity check
// separates them with no scope to enter, leak, or have another mod's exception unwind out of.
//
// It moves only vanilla's OWN value. If the incoming overage is not the 0.16 this fix was derived
// against, some other mod has already decided what the mask edge should be and we leave it alone
// rather than winning by patch order — a seam is a cosmetic sliver, and silently overriding another
// mod's mask geometry is not a trade worth making for it. ApiCompatibilityTests pins that 0.16 is
// still the literal vanilla bakes with, so a Ludeon change to it fails a test rather than quietly
// switching this fix off.
//
// Gated on the §15 feature flag so eave_shadows OFF remains a bit-for-bit vanilla baseline for the
// harness A/Bs, per the discipline in EaveShadowGrid.
[HarmonyPatch(typeof(SectionLayer_IndoorMask), nameof(SectionLayer_IndoorMask.AppendQuadToMesh))]
public static class Patch_IndoorMaskOverage
{
    // Vanilla's inflation for a cell with no impassable building on it, mirrored from
    // SectionLayer_IndoorMask.GenerateSectionLayer. Compared exactly rather than with a tolerance:
    // this is a literal copied out of the same build, not a measurement, and a near-miss means the
    // value came from somewhere else — which is the case we want to decline.
    public const float VanillaOverage = 0.16f;

    static void Prefix(LayerSubMesh mesh, ref float overage)
    {
        if (!CelestialLightingFeatures.EaveShadows)
            return;

        if (overage != VanillaOverage)
            return;

        if (!IsSectionMask(mesh))
            return;

        overage = 0f;
    }

    // Null-safe because AppendQuadToMesh is itself null-safe on the mesh: GenerateSectionLayer hands
    // it a null debugMesh on every ordinary frame, and vanilla answers by doing nothing at all.
    private static bool IsSectionMask(LayerSubMesh mesh) =>
        mesh != null
        && (mesh.material == MatBases.IndoorMask || mesh.material == MatBases.DebugOverlay);
}
