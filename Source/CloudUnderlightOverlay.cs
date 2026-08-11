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

    // The baked field. RGB carries §8's tint and alpha carries the residual — see
    // CloudUnderlightField.WriteRgba for why the colour is baked into the pixels rather than set as
    // the material's colour.
    private static readonly Texture2D FieldTex = NewFieldTexture();

    private static readonly float[] Intensity =
        new float[CloudUnderlightField.Resolution * CloudUnderlightField.Resolution];

    private static readonly byte[] Pixels =
        new byte[CloudUnderlightField.Resolution * CloudUnderlightField.Resolution * 4];

    // What the currently-baked texture was baked FROM. All four together are the cache key: a bake is
    // reused only when every input that shaped it still holds.
    //
    // The map id is part of it because two maps can be loaded at once and their fields are seeded by
    // different tiles. Only the visible map draws, so switching between a colony and a caravan map
    // costs one rebake on the frame after the switch rather than one per frame.
    private static int _bakedMapId = -1;
    private static int _bakedFractionStep = int.MinValue;
    private static int _bakedTintKey = int.MinValue;
    private static float _bakedMean;

    // Cloud fraction is quantised before it is compared, so §22's continuously drifting value does not
    // force a rebake every frame for a change no pixel could show. 1/128 is finer than one byte of
    // alpha, so the quantisation cannot be the thing a viewer sees.
    private const float FractionSteps = 128f;

    // Same idea for the tint, one axis per channel packed into an int. 64 steps per channel is half a
    // byte of colour resolution, well under what a texture upload can express.
    private const float TintSteps = 64f;

    // The last alpha written to the material, cached for the same reason SheetMaterial exists:
    // Material.color is a native round trip in both directions, so the last written value is
    // remembered here rather than read back. NaN so the first draw always writes — 0 is a legitimate
    // steady-state value and seeding with it would let a genuine first frame at 0 skip its write.
    private static float _lastAlpha = float.NaN;

    // Draws this frame's underlit-cloud layer over `map`, or nothing at all if there is none. Called
    // once per frame from Patch_CloudUnderlightDraw, for the currently-visible map only.
    public static void Draw(Map map)
    {
        float strength = CloudUnderlightLayer.StrengthFor(map);

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

        Rebake(map);

        if (strength != _lastAlpha)
        {
            LayerMat.color = new Color(1f, 1f, 1f, strength);
            _lastAlpha = strength;
        }

        // The tile is scaled to CellsPerRepeat map cells and panned by the absolute tick. Both work on
        // top of MAP-SPACE UVs — the mask charts 0..1 across the whole map, matching
        // MeshPool.wholeMapPlane, so scaling here is in units of "tiles per map" and the same numbers
        // are correct whichever of the two meshes came back.
        int absTick = Find.TickManager?.TicksAbs ?? 0;
        LayerMat.mainTextureScale = new Vector2(
            map.Size.x / CloudUnderlightField.CellsPerRepeat,
            map.Size.z / CloudUnderlightField.CellsPerRepeat);
        LayerMat.mainTextureOffset = new Vector2(
            CloudUnderlightField.DriftOffsetU(absTick), CloudUnderlightField.DriftOffsetV(absTick));

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

    // Re-walks the noise only when the cloud fraction has actually moved, and re-writes the bytes only
    // when either that or the tint has. See this file's header for why those two cadences are split.
    private static void Rebake(Map map)
    {
        int mapId = map.uniqueID;
        float fraction = CloudUnderlightLayer.CloudFractionFor(map);
        int fractionStep = (int)(fraction * FractionSteps + 0.5f);

        SkyColorTemperature.Rgb hot = CloudUnderlightLayer.HotTintFor(map);
        SkyColorTemperature.Rgb cool = CloudUnderlightLayer.CoolTintFor(map);
        CloudUnderlightLayer.GradientAxisFor(map, out int axisU, out int axisV);

        // The axis joins the two colours in the cache key. It changes at most eight times a day (it
        // is the sun's bearing rounded to the eight tiling directions), but when it does change every
        // texel's colour moves, so a key that ignored it would leave the gradient pointing the old
        // way until the sun's colour happened to move a quantum.
        int tintKey = TintKey(hot) ^ (TintKey(cool) * 31) ^ ((axisU + 2) << 26) ^ ((axisV + 2) << 29);

        bool structureStale = mapId != _bakedMapId || fractionStep != _bakedFractionStep;
        if (!structureStale && tintKey == _bakedTintKey)
            return;

        if (structureStale)
        {
            _bakedMean = CloudUnderlightField.FillIntensity(
                Intensity,
                CloudUnderlightField.Resolution,
                CloudUnderlightField.Resolution,
                fractionStep / FractionSteps,
                map.Tile.tileId);

            _bakedMapId = mapId;
            _bakedFractionStep = fractionStep;
        }

        CloudUnderlightField.WriteRgba(
            Pixels, Intensity,
            CloudUnderlightField.Resolution, CloudUnderlightField.Resolution,
            _bakedMean,
            hot.R, hot.G, hot.B,
            cool.R, cool.G, cool.B,
            axisU, axisV);
        _bakedTintKey = tintKey;

        // LoadRawTextureData rather than SetPixels32, the same choice AuroraCurtainOverlay made and
        // for the same reason: the pure core already writes bytes in exactly the layout
        // TextureFormat.RGBA32 wants, so this is a memcpy with no per-pixel Color32 marshalling in
        // between. Apply(false) skips mip regeneration the texture does not have.
        FieldTex.LoadRawTextureData(Pixels);
        FieldTex.Apply(false);
    }

    private static int TintKey(SkyColorTemperature.Rgb tint) =>
        (Step(tint.R) << 16) | (Step(tint.G) << 8) | Step(tint.B);

    private static int Step(float channel)
    {
        int step = (int)(channel * TintSteps + 0.5f);
        return step < 0 ? 0 : (step > 255 ? 255 : step);
    }

    private static Texture2D NewFieldTexture()
    {
        Texture2D texture = new Texture2D(
            CloudUnderlightField.Resolution, CloudUnderlightField.Resolution,
            TextureFormat.RGBA32, mipChain: false)
        {
            name = "CelestialLighting_CloudUnderlight",
            // Repeat is what makes the drift a pan rather than a rebake — the field is tileable by
            // construction (AuroraNoise wraps on an integer lattice), so a seam can never sweep
            // across the colony.
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear,
        };

        LayerMat.mainTexture = texture;
        return texture;
    }
}
