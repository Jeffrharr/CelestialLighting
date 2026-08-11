using UnityEngine;
using Verse;

namespace CelestialLighting;

// §25 (DESIGN.md §25, issue #138): draws the cloud deck itself — a sheet of actual cloud over the map,
// rather than only the light it adds or blocks.
//
// ITS OWN TEXTURE, unlike §23b and §23c which share one. Two things differ and both matter: the alpha
// is the field's INTENSITY rather than its residual (a drawn cloud is the object, not an adjustment to
// a flat approximation of it — see CloudSheetMath.SheetAlpha), and it is sampled at
// CloudField.SheetResolution with CloudField.SheetOctaves, four times the texel density and one more
// octave, because this is drawing cloud edges rather than the blurred light they cast. It is still the
// SAME FIELD at the same seed and lattice, so it cannot disagree with the illumination lanes about
// where the clouds are — see CloudField.Coverage's octave overload.
//
// ABOVE FogOfWar, which is the one altitude decision here and the opposite of §23b/§23c's. Those draw
// light landing on ground, so unexplored ground correctly has none. A cloud is SKY: it sits between
// the camera and the map, and hiding it over unexplored terrain would say the player's ignorance of
// the ground somehow reaches the atmosphere. §11a's aurora made the same call for the same reason and
// is the precedent this copies, down to the `+ Altitudes.AltInc`.
//
// [StaticConstructorOnStartup] is mandatory: `new Material(...)` and `new Texture2D(...)` must happen
// on Unity's main thread.
[StaticConstructorOnStartup]
public static class CloudSheetOverlay
{
    // Alpha-blended rather than additive: cloud OCCLUDES. MoteGlow could only ever brighten what is
    // behind it, which is the one thing a cloud never does to the ground below it.
    private static readonly Material SheetMat = new Material(ShaderDatabase.Transparent);

    private static readonly Texture2D SheetTex = NewSheetTexture();

    private static readonly float[] Intensity =
        new float[CloudField.SheetResolution * CloudField.SheetResolution];

    private static readonly byte[] Pixels =
        new byte[CloudField.SheetResolution * CloudField.SheetResolution * 4];

    // Daylight cloud colour: a bright neutral grey, not white. White reads as snow or as a blown-out
    // highlight on a top-down map; real cloud tops under direct sun are close to neutral and the eye
    // reads them as bright because of their surroundings, which is a contrast this pass gets for free
    // by being drawn over a lit colony.
    private static readonly SkyColorTemperature.Rgb DayColour =
        new SkyColorTemperature.Rgb(0.86f, 0.87f, 0.90f);

    private static int _bakedMapId = -1;
    private static int _bakedFractionStep = int.MinValue;
    private static int _bakedColourKey = int.MinValue;

    private const float FractionSteps = 64f;
    private const float ColourSteps = 32f;

    private static float _lastAlpha = float.NaN;

    public static void Draw(Map map)
    {
        float alpha = CloudLayers.SheetAlphaFor(map);
        if (alpha <= 0f)
            return;

        Rebake(map);

        if (alpha != _lastAlpha)
        {
            // White material colour: the sheet's own colour is baked into the texture (it varies
            // across the field with the sunset gradient), so the material carries only the alpha.
            SheetMat.color = new Color(1f, 1f, 1f, alpha);
            _lastAlpha = alpha;
        }

        CloudFieldTexture.ApplyTiling(SheetMat, map);

        // NO OPEN-SKY MASK, and that is deliberate rather than an omission. §23b and §23c mask to
        // unroofed cells because they are light reaching the GROUND, which a roof stops. Cloud is
        // above the roof: it should draw over the whole map including the colony's own buildings,
        // exactly as it would look from a camera this high. Masking it would carve a cloud-shaped hole
        // out of the sky wherever somebody had built a barn.
        SkyOverlay.DrawWorldOverlay(
            map, SheetMat, AltitudeLayer.FogOfWar.AltitudeFor() + Altitudes.AltInc);
    }

    private static void Rebake(Map map)
    {
        int mapId = map.uniqueID;
        float fraction = CloudLayers.CloudFractionFor(map);
        int fractionStep = (int)(fraction * FractionSteps + 0.5f);

        SkyColorTemperature.Rgb hot = CloudLayers.HotTintFor(map);
        SkyColorTemperature.Rgb cool = CloudLayers.CoolTintFor(map);
        CloudLayers.GradientAxisFor(map, out int axisU, out int axisV);

        float underlit = CloudSheetMath.UnderlitFraction(SolarPosition.ElevationForMap(map));
        float brightness = CloudSheetMath.SheetBrightness(map.skyManager.CurSkyGlow);

        int colourKey = ColourKey(hot) ^ (ColourKey(cool) * 31)
            ^ ((int)(underlit * ColourSteps) << 12) ^ ((int)(brightness * ColourSteps) << 18)
            ^ ((axisU + 2) << 26) ^ ((axisV + 2) << 29);

        bool structureStale = mapId != _bakedMapId || fractionStep != _bakedFractionStep;
        if (!structureStale && colourKey == _bakedColourKey)
            return;

        if (structureStale)
        {
            // THE EXPENSIVE ONE. 192x192 at five octaves is ~37k samples, an order of magnitude past
            // the illumination field's bake, and it runs on the main thread. It is affordable only
            // because of what triggers it: the cloud FRACTION, which §22 caches hourly and §13 moves
            // only across a weather transition. Quantised to 1/64 rather than the illumination lane's
            // 1/128 for exactly that reason — halving the steps halves the rebakes during a transition,
            // and at this resolution one texel of coverage is far below what an eye tracks.
            CloudField.FillIntensity(
                Intensity,
                CloudField.SheetResolution,
                CloudField.SheetResolution,
                fractionStep / FractionSteps,
                map.Tile.tileId,
                CloudField.SheetOctaves);

            _bakedMapId = mapId;
            _bakedFractionStep = fractionStep;
        }

        CloudField.WriteSheetRgba(
            Pixels, Intensity,
            CloudField.SheetResolution, CloudField.SheetResolution,
            hot.R, hot.G, hot.B,
            cool.R, cool.G, cool.B,
            DayColour.R, DayColour.G, DayColour.B,
            underlit, brightness,
            axisU, axisV);
        _bakedColourKey = colourKey;

        SheetTex.LoadRawTextureData(Pixels);
        SheetTex.Apply(false);
    }

    private static int ColourKey(SkyColorTemperature.Rgb colour)
    {
        int r = Step(colour.R);
        int g = Step(colour.G);
        int b = Step(colour.B);
        return (r << 16) | (g << 8) | b;
    }

    private static int Step(float channel)
    {
        int step = (int)(channel * ColourSteps + 0.5f);
        return step < 0 ? 0 : (step > 255 ? 255 : step);
    }

    private static Texture2D NewSheetTexture()
    {
        Texture2D texture = new Texture2D(
            CloudField.SheetResolution, CloudField.SheetResolution,
            TextureFormat.RGBA32, mipChain: false)
        {
            name = "CelestialLighting_CloudSheet",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear,
        };

        SheetMat.mainTexture = texture;
        return texture;
    }
}
