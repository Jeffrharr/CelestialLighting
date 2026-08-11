using UnityEngine;
using Verse;

namespace CelestialLighting;

// The one baked cloud field every lane draws through, and the reason there is only one.
//
// THREE LANES, ONE FIELD. §23b adds the light a deck bounces down (additive, at dusk), §23c subtracts
// the light a deck blocks (alpha-blended black, by day), and §25 draws the deck itself. They are three
// statements about the SAME clouds, so they have to agree about where those clouds are — two
// independently seeded fields would put the bright ground somewhere other than under the gaps, which
// is worse than either lane alone. Sharing the bake makes that agreement structural rather than a
// convention two files have to keep.
//
// §23b AND §23c SHARE THIS TEXTURE OUTRIGHT, not just the field behind it. Both want alpha = the
// field's residual above its own mean; they differ only in what they do with it — MoteGlow with a
// white material adds the baked colour, ShaderDatabase.Transparent with a BLACK material blends
// toward black and ignores the baked RGB entirely (blending toward `tex.rgb * mat.rgb` with a black
// material colour is black whatever the texture says). So one bake serves both, and the two lanes
// cannot drift apart even in principle. §25's sheet needs different content (alpha is the cloud
// itself, not its residual, and it is sampled far more finely), so it bakes its own — see
// CloudSheetOverlay.
//
// [StaticConstructorOnStartup] is mandatory for the usual reason: `new Texture2D(...)` must happen on
// Unity's main thread. See AuroraCurtainOverlay's header.
[StaticConstructorOnStartup]
public static class CloudFieldTexture
{
    private static readonly Texture2D Texture = NewFieldTexture();

    private static readonly float[] Intensity =
        new float[CloudField.Resolution * CloudField.Resolution];

    private static readonly byte[] Pixels =
        new byte[CloudField.Resolution * CloudField.Resolution * 4];

    // What the currently-baked texture was baked FROM. Together these are the cache key: a bake is
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

    // The field's areal mean at the last bake — the quantity §23's flat lane is already rendering, and
    // therefore the one both additive and subtractive lanes subtract before drawing anything. Exposed
    // so a probe can read the same number the pixels were built from.
    public static float BakedMean => _bakedMean;

    // The texture for this map right now, re-baking first if anything it depends on has moved.
    //
    // THE BAKE IS SPLIT AT THE CADENCE BOUNDARY. The field's SHAPE depends only on how cloudy it is,
    // which moves a few times an in-game hour; its COLOUR is §8's sky target and §19c's hue, which
    // move every frame as the sun sets. So the noise walk runs on the fraction and the byte write runs
    // on the colour, and neither pays the other's rate. Without that split, a subsystem whose whole
    // life is the ten minutes around sunset would re-walk 4,096 noise samples per frame during exactly
    // those ten minutes.
    public static Texture2D For(Map map)
    {
        int mapId = map.uniqueID;
        float fraction = CloudLayers.CloudFractionFor(map);
        int fractionStep = (int)(fraction * FractionSteps + 0.5f);

        SkyColorTemperature.Rgb hot = CloudLayers.HotTintFor(map);
        SkyColorTemperature.Rgb cool = CloudLayers.CoolTintFor(map);
        CloudLayers.GradientAxisFor(map, out int axisU, out int axisV);

        // The axis joins the two colours in the cache key. It changes at most eight times a day (it is
        // the sun's bearing rounded to the eight tiling directions), but when it does change every
        // texel's colour moves, so a key that ignored it would leave the gradient pointing the old way
        // until the sun's colour happened to move a quantum.
        int tintKey = TintKey(hot) ^ (TintKey(cool) * 31) ^ ((axisU + 2) << 26) ^ ((axisV + 2) << 29);

        bool structureStale = mapId != _bakedMapId || fractionStep != _bakedFractionStep;
        if (!structureStale && tintKey == _bakedTintKey)
            return Texture;

        if (structureStale)
        {
            _bakedMean = CloudField.FillIntensity(
                Intensity,
                CloudField.Resolution,
                CloudField.Resolution,
                fractionStep / FractionSteps,
                map.Tile.tileId);

            _bakedMapId = mapId;
            _bakedFractionStep = fractionStep;
        }

        CloudField.WriteUnderlightRgba(
            Pixels, Intensity,
            CloudField.Resolution, CloudField.Resolution,
            _bakedMean,
            hot.R, hot.G, hot.B,
            cool.R, cool.G, cool.B,
            axisU, axisV);
        _bakedTintKey = tintKey;

        // LoadRawTextureData rather than SetPixels32, the same choice AuroraCurtainOverlay made and
        // for the same reason: the pure core already writes bytes in exactly the layout
        // TextureFormat.RGBA32 wants, so this is a memcpy with no per-pixel Color32 marshalling in
        // between. Apply(false) skips mip regeneration the texture does not have.
        Texture.LoadRawTextureData(Pixels);
        Texture.Apply(false);
        return Texture;
    }

    // The tile scale and pan every lane draws this texture with, so the three cannot disagree about
    // where the field sits on the map. Map-space UVs (OpenSkyMask charts 0..1 across the map, matching
    // MeshPool.wholeMapPlane) mean the scale is in units of "tiles per map".
    public static void ApplyTiling(Material material, Map map)
    {
        int absTick = Find.TickManager?.TicksAbs ?? 0;
        material.mainTextureScale = new Vector2(
            map.Size.x / CloudField.CellsPerRepeat,
            map.Size.z / CloudField.CellsPerRepeat);
        material.mainTextureOffset = new Vector2(
            CloudField.DriftOffsetU(absTick), CloudField.DriftOffsetV(absTick));
    }

    private static int TintKey(SkyColorTemperature.Rgb tint) =>
        (Step(tint.R) << 16) | (Step(tint.G) << 8) | Step(tint.B);

    private static int Step(float channel)
    {
        int step = (int)(channel * TintSteps + 0.5f);
        return step < 0 ? 0 : (step > 255 ? 255 : step);
    }

    private static Texture2D NewFieldTexture() =>
        new Texture2D(CloudField.Resolution, CloudField.Resolution, TextureFormat.RGBA32, mipChain: false)
        {
            name = "CelestialLighting_CloudField",
            // Repeat is what makes the drift a pan rather than a rebake — the field is tileable by
            // construction (AuroraNoise wraps on an integer lattice), so a seam can never sweep across
            // the colony.
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear,
        };
}
