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
    private static readonly Material LayerMat = new Material(ShaderDatabase.MoteGlow);

    // The last alpha written to the material, cached for the same reason SheetMaterial exists:
    // Material.color is a native round trip in both directions, so the last written value is
    // remembered here rather than read back. NaN so the first draw always writes — 0 is a legitimate
    // steady-state value and seeding with it would let a genuine first frame at 0 skip its write.
    private static float _lastAlpha = float.NaN;

    // Draws this frame's underlit-cloud layer over `map`, or nothing at all if there is none. Called
    // once per frame from Patch_CloudLayersDraw, for the currently-visible map only.
    public static void Draw(Map map)
    {
        float strength = CloudLayers.StrengthFor(map);

        // The overwhelmingly common case: the sun is not inside the glow window, or there is no cloud,
        // or the feature is off. Returning before touching the texture, the material or the draw call
        // is what makes "§23b costs nothing outside the minutes it can draw" literally true rather
        // than approximately true.
        if (strength <= 0f)
            return;

        // Roofed cells are masked out by GEOMETRY, because they cannot be masked by altitude:
        // SectionLayer_IndoorMask draws below VisEffects, so vanilla's own roof masking does not reach
        // this pass. See OpenSkyMaskMath's header. A null mesh means the map is entirely roofed and
        // there is nothing to draw at all.
        Mesh mesh = OpenSkyMask.MeshFor(map);
        if (mesh == null)
            return;

        // The shared bake. §23c draws this identical texture through a black Transparent material,
        // which is what makes "the light a deck adds" and "the light it blocks" the same pixels rather
        // than two fields that have to be kept in step — see CloudFieldTexture.
        LayerMat.mainTexture = CloudFieldTexture.For(map);
        CloudFieldTexture.ApplyTiling(LayerMat, map);

        if (strength != _lastAlpha)
        {
            LayerMat.color = new Color(1f, 1f, 1f, strength);
            _lastAlpha = strength;
        }

        // ALTITUDE: VisEffects (33), above LightingOverlay (32) and below FogOfWar — the same pair
        // §11a's curtain and §24's glare both need, and load-bearing for the same reason. Drawing at
        // AltitudeLayer.Weather (31), where vanilla's own weather overlays live, would put this pass
        // BELOW the multiply it exists to exceed. §24 measured what that costs rather than asserting
        // it: an overcast palette passes most of an addition through, so the failure is conditional
        // and backwards (the effect fades as the deck thickens) rather than total.
        //
        // Below FogOfWar is right here for the same reason it is right for glare: this is light
        // landing on GROUND, so unexplored ground having none of it is correct rather than a
        // limitation. An aurora is sky, which is why §11a is the one that clears the fog.
        if (mesh == MeshPool.wholeMapPlane)
        {
            SkyOverlay.DrawWorldOverlay(map, LayerMat, AltitudeLayer.VisEffects.AltitudeFor());
            return;
        }

        // The two meshes need different origins: vanilla's shared plane is centred on map.Center and
        // placed by SkyOverlay.DrawWorldOverlay, while our masked mesh is built in absolute cell
        // coordinates and draws at the origin. Branching on which came back keeps that difference in
        // one visible place rather than hidden inside the mask — the same shape SnowGlareOverlay uses.
        Vector3 position = new Vector3(0f, AltitudeLayer.VisEffects.AltitudeFor(), 0f);
        Graphics.DrawMesh(mesh, position, Quaternion.identity, LayerMat, 0);
    }

}
