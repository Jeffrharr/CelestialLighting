using UnityEngine;
using Verse;

namespace CelestialLighting;

// §23c (DESIGN.md §23c): draws daylight cloud shadows — the same field §23b adds light through,
// subtracted instead of added, over the map's open sky.
//
// ONE ATLAS, THREE LANES, AND THE SHADER IS THE ONLY DIFFERENCE. §25 draws a sheet as cloud, this
// draws the same sheet as the dark patch under it, and §23b draws it as the warm light it bounces
// down at dusk. All three read the same CloudSheetLayout placements and sample the same blob atlas —
// so the shadow is under the cloud by construction rather than by two subsystems being tuned to look
// alike. That was NOT true for a while: the sheets were bounded while these two were still keyed on a
// tiled field, which put two different cloud patterns on one screen.
//
// The two lanes are also mutually exclusive in time — §23b needs the sun below the horizon, this needs
// it above — so on any given frame at most one of them is drawing.
//
// [StaticConstructorOnStartup] for the usual reason: `new Material(...)` must be on Unity's main
// thread. See EaveShadeOverlay's header, which records what happens without it.
[StaticConstructorOnStartup]
public static class CloudShadowOverlay
{
    // ShaderDatabase.Transparent is ordinary alpha blending, and blending BLACK at alpha `a` is
    // `scene * (1 - a)` — a multiply, which is exactly what a shadow is. Same shader and same
    // reasoning as §15b's eave shade; deliberately NOT MoteGlow, which can only add.
    //
    // MatBases.SunShadow is emphatically not reusable here either, for the reason EaveShadeOverlay
    // records: its shader displaces every vertex by the shadow vector scaled by that vertex's alpha.
    private static readonly Material[] ShadowMats = BuildMaterials();

    private static Material[] BuildMaterials()
    {
        Material[] materials = new Material[CloudSheetLayout.MaxSheets];
        for (int i = 0; i < materials.Length; i++)
            materials[i] = new Material(ShaderDatabase.Transparent);

        return materials;
    }

    // Draws this frame's cloud shadows over `map`, or nothing at all if there are none.
    //
    // ONE DRAW PER SHEET, through the open-sky mask — see CloudSheetDraw for why a bounded shape that
    // has to respect roofs is drawn as the mask's geometry with the shape moved by texture coordinates
    // rather than as its own quad. Materials cannot be re-tinted between deferred draw calls, so each
    // slot owns one.
    public static void Draw(Map map)
    {
        float alpha = CloudLayers.ShadowAlphaFor(map);

        // The common case on most frames of most saves: no cloud, no sun, or the feature off.
        if (alpha <= 0f)
            return;

        int count = CloudSheetDraw.PlaceSheets(map, out CloudSheetLayout.Placement[] placements);
        if (count <= 0)
            return;

        // Built with the placements, once a tick, rather than by this lane for itself every
        // frame -- see CloudSheetDraw.OverlapDepths. All three lanes read the same array, which
        // is the same reason they read the same placements.
        float[] depths = CloudSheetDraw.OverlapDepths;

        float altitude = AltitudeLayer.VisEffects.AltitudeFor();

        for (int i = 0; i < count; i++)
        {
            CloudSheetLayout.Placement placement = placements[i];
            if (!CloudSheetLayout.OnScreen(placement, map.Size.x, map.Size.z))
                continue;

            // Stacked cloud casts a deeper shadow, capped — the same OverlapBoost §25 uses for its own
            // opacity, so a thick patch of sky and the dark patch under it come from one number.
            float boost = CloudSheetMath.OverlapBoost(depths[i]);

            Material material = ShadowMats[i];
            material.mainTexture = CloudSheetOverlay.Atlas;
            // The same atlas cell §25 draws the cloud from, resolved through the one function that
            // knows the row-is-deck convention — so this shadow is cast by that cloud rather than by
            // one with the same seed on a different deck.
            CloudSheetDraw.ApplySheetUvs(
                material, placement, map, CloudSheetOverlay.AtlasCells,
                CloudSheetLayout.BlobFor(placement, CloudSheetOverlay.AtlasCells));

            // Black, so the blend is a pure multiply. The alpha is the whole signal.
            material.color = new Color(0f, 0f, 0f, Mathf.Min(alpha * placement.Alpha * boost, 1f));

            // ALTITUDE: VisEffects, the same as §23b and §24, and for a reason worth stating because a
            // shadow's natural home looks like it should be lower. Vanilla's own sun shadows draw far
            // below the lighting overlay as part of the map mesh — but they are cast BY things ON the
            // map, so they belong under the light. A cloud shadow is cast by something ABOVE everything
            // on the map, so it must darken pawns, buildings and roofs alike, which means drawing after
            // them. Below FogOfWar for the same reason §23b is: this is a change to the light landing
            // on ground, and unexplored ground has none of it either way.
            if (!CloudSheetDraw.DrawThroughMask(map, material, altitude))
                return;
        }
    }
}
