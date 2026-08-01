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
//   - Fogged-cell and gravship bakes flow through the same helper and get the same flush edge.
//
// Gated on the §15 feature flag so eave_shadows OFF remains a bit-for-bit vanilla baseline for the
// harness A/Bs, per the discipline in EaveShadowGrid.
[HarmonyPatch(typeof(SectionLayer_IndoorMask), nameof(SectionLayer_IndoorMask.AppendQuadToMesh))]
public static class Patch_IndoorMaskOverage
{
    static void Prefix(ref float overage)
    {
        if (CelestialLightingFeatures.EaveShadows)
            overage = 0f;
    }
}
