using UnityEngine;
using Verse;

namespace CelestialLighting;

// §23b (issue #88 option 2): draws the additive underlit-cloud layer — one textured quad per frame
// over the map's open sky, through the pass §24 built and the mask it shares (epic #103).
//
// WHY A TEXTURE RATHER THAN GEOMETRY, which is the choice this file is. The structure being drawn is
// a smooth 2-D field, and there are two ways to put one on screen here: a mesh whose vertex colours
// carry it, or a texture the GPU interpolates. §15b's eave shade proves vertex colours work through
// ShaderDatabase.Transparent — but this pass has to be ADDITIVE (MoteGlow), and nothing in this
// codebase has ever asked MoteGlow to honour a vertex colour. §11a's aurora, which needed exactly
// this and got it working, uses a texture. Following the path already known to work here costs one
// 64x64 RGBA texture and buys bilinear filtering, free soft interpolation between texels, and drift
// as a UV pan rather than a rebake.
//
// THE BAKE IS SPLIT AT THE CADENCE BOUNDARY. The field's SHAPE depends only on how cloudy it is,
// which moves a few times an in-game hour; its COLOUR is §8's sky target, which moves every frame as
// the sun sets. So the noise walk runs on the fraction and the byte write runs on the colour, and
// neither pays the other's rate. Without that split a subsystem whose whole life is the ten minutes
// around sunset would be re-walking 4,096 noise samples per frame during exactly those ten minutes.
//
// [StaticConstructorOnStartup] is mandatory, same as AuroraCurtainOverlay / SnowGlareOverlay: `new
// Material(...)` and `new Texture2D(...)` must happen on Unity's main thread, and the attribute is
// what guarantees the static initialiser runs there (at startup, after ShaderDatabase has loaded)
// rather than on whichever thread first touches the type.
[StaticConstructorOnStartup]
public static class CloudUnderlightOverlay
{
    // Additive, and for the reason that is the whole point of epic #103: SkyColorSet.sky is a MULTIPLY
    // into MatBases.LightOverlay.color whose brightest palette is already (1,1,1), so a multiplicative
    // lane has no headroom to make one part of the map brighter than another. MoteGlow adds. Same
    // shader as §11a and §24, same reason.
    //
    // This composites rendered pixels only. SkyTarget.glow, GlowGrid, plant growth, solar output and
    // pawn vision are all untouched — §23b stays inside the mod's colour-only lane exactly as §23 does.
    private static readonly Material[] LayerMats = BuildMaterials();

    private static Material[] BuildMaterials()
    {
        Material[] materials = new Material[CloudSheetLayout.MaxSheets];
        for (int i = 0; i < materials.Length; i++)
            materials[i] = new Material(ShaderDatabase.MoteGlow);

        return materials;
    }


    // Draws this frame's underlit-cloud layer over `map`, or nothing at all if there is none.
    //
    // ONE DRAW PER SHEET, through the open-sky mask, sharing CloudSheetLayout's placements with §23c
    // and §25 — see CloudSheetDraw for the whole arrangement, and for why a bounded shape that has to
    // respect roofs is drawn as the mask's geometry with the shape moved by texture coordinates.
    //
    // The colour is sampled per SHEET rather than per texel: a sheet is one place, so it takes one
    // colour off the sunward/anti-solar gradient, and a sky of sheets shows its warm side and its cool
    // side at once because the sheets are in different places.
    public static void Draw(Map map)
    {
        float strength = CloudLayers.StrengthFor(map);

        // The overwhelmingly common case: the sun is not inside the glow window, or there is no cloud,
        // or the feature is off. Returning before touching a material or a draw call is what makes
        // "§23b costs nothing outside the minutes it can draw" literally true.
        if (strength <= 0f)
            return;

        int count = CloudSheetDraw.PlaceSheets(map, out CloudSheetLayout.Placement[] placements);
        if (count <= 0)
            return;

        SkyColorTemperature.Rgb hot = CloudLayers.HotTintFor(map);
        SkyColorTemperature.Rgb cool = CloudLayers.CoolTintFor(map);
        CloudLayers.GradientAxisFor(map, out int axisU, out int axisV);

        float altitude = AltitudeLayer.VisEffects.AltitudeFor();
        int atlasCells = CloudSheetOverlay.AtlasCells;

        for (int i = 0; i < count; i++)
        {
            CloudSheetLayout.Placement placement = placements[i];
            if (!CloudSheetLayout.OnScreen(placement, map.Size.x, map.Size.z))
                continue;

            // Stacked cloud bounces more light down, capped — the same OverlapBoost §25 uses for its
            // opacity and §23c for its shadow depth, so all three read one number about one sky.
            float boost = CloudSheetMath.OverlapBoost(
                CloudSheetLayout.OverlapDepth(placements, count, i));

            Material material = LayerMats[i];
            material.mainTexture = CloudSheetOverlay.Atlas;
            CloudSheetDraw.ApplySheetUvs(
                material, placement, map, atlasCells,
                placement.ShapeSeed % (atlasCells * atlasCells));

            // Warm toward the sun, magenta away from it — §8's target colour and §19c's composed
            // twilight hue, sampled at this sheet's own centre.
            float warmth = CloudField.GradientWarmth(
                placement.CenterX / map.Size.x, placement.CenterZ / map.Size.z, axisU, axisV);

            material.color = new Color(
                Mix(cool.R, hot.R, warmth),
                Mix(cool.G, hot.G, warmth),
                Mix(cool.B, hot.B, warmth),
                Mathf.Min(strength * placement.Alpha * boost, 1f));

            // ALTITUDE: VisEffects (33), above LightingOverlay (32) and below FogOfWar — the pair
            // §11a's curtain and §24's glare both need. AltitudeLayer.Weather (31), where vanilla's
            // own weather overlays live, would put this pass BELOW the multiply it exists to exceed;
            // §24 measured what that costs rather than asserting it. Below FogOfWar because this is
            // light landing on GROUND, so unexplored ground having none of it is correct.
            if (!CloudSheetDraw.DrawThroughMask(map, material, altitude))
                return;
        }
    }

    private static float Mix(float a, float b, float t) => a + (b - a) * t;
}
