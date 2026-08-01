using HarmonyLib;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// TEMPORARY DIAGNOSTIC for docs/EAVE-SEAM.md — deleted once the eave seam is understood.
//
// Every build-side measurement says the sun-shadow submesh we author is identical between the
// enclosed room and the one-cell-gap room, and editing its triangles does not move a pixel. So the
// question is no longer "what did we build" but "who actually draws the seam rows". This answers it
// empirically: while the `seam_tint` feature flag is on, every candidate layer's material is forced
// to a distinct, unmistakable colour each frame:
//
//   MatBases.SunShadow      -> RED     (the submesh our Regenerate Prefix authors)
//   MatBases.SunShadowFade  -> YELLOW  (vanilla's OTHER shadow material — Graphic_Shadow /
//                                       Printer_Shadow printed thing shadows. Walls print these
//                                       independently of the perimeter mesh, so if the band's
//                                       missing tail was ever drawn by wall shadowData rather than
//                                       by our skirts, it shows up yellow.)
//   MatBases.EdgeShadow     -> GREEN   (vanilla SectionLayer_EdgeShadows — an enclosed room has
//                                       more wall edge, and its quads land in the same rows)
//   EaveShadeOverlay        -> BLUE    (§15b's own shade layer)
//
// The lighting overlay is deliberately left untinted: it multiplies the whole frame, so tinting it
// would recolour everything and drown the reading; its contribution is already characterised by the
// casters-off frames.
//
// The writes happen in a Priority.Last postfix on SkyManagerUpdate so they land after both
// vanilla's per-frame tint writes and our own SetShadowTint-style postfixes — whoever writes these
// colours during the update, the tint wins the frame. Originals are captured once and restored on
// disable, because EdgeShadow's colour is NOT rewritten per frame by anything, and a leaked green
// would contaminate every scenario after this one in a suite.
public static class SeamTint
{
    public static bool Enabled;

    private static bool captured;
    private static Color sunShadow, sunShadowFade, edgeShadow, eaveShade;

    public static void Apply()
    {
        if (Enabled)
        {
            if (!captured)
            {
                sunShadow = MatBases.SunShadow.color;
                sunShadowFade = MatBases.SunShadowFade.color;
                edgeShadow = MatBases.EdgeShadow.color;
                eaveShade = EaveShadeOverlay.Material.color;
                captured = true;
            }
            // Alpha 0.8 everywhere: strong enough that ownership is unmistakable in a screenshot,
            // below 1.0 so overlapping layers still show as a blend rather than pure occlusion.
            MatBases.SunShadow.color = new Color(1f, 0f, 0f, 0.8f);
            MatBases.SunShadowFade.color = new Color(1f, 1f, 0f, 0.8f);
            MatBases.EdgeShadow.color = new Color(0f, 1f, 0f, 0.8f);
            EaveShadeOverlay.Material.color = new Color(0f, 0f, 1f, 0.8f);
        }
        else if (captured)
        {
            MatBases.SunShadow.color = sunShadow;
            MatBases.SunShadowFade.color = sunShadowFade;
            MatBases.EdgeShadow.color = edgeShadow;
            EaveShadeOverlay.Material.color = eaveShade;
            captured = false;
        }
    }
}

// Separate class so the [HarmonyPatch] scan doesn't try to read patch metadata off the flag holder.
[HarmonyPatch(typeof(SkyManager), nameof(SkyManager.SkyManagerUpdate))]
[HarmonyPriority(Priority.Last)]
public static class Patch_SeamTint
{
    static void Postfix() => SeamTint.Apply();
}
