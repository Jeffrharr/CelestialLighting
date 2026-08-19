using UnityEngine;
using Verse;

namespace CelestialLighting;

// §25 (DESIGN.md §25, issue #138): draws the cloud deck itself — several bounded cloud sheets moving
// across the map, rather than one field stretched over it.
//
// THE TILED VERSION SHIPPED FIRST AND WAS MEASURED, WHICH IS WHY THIS ONE EXISTS. Stretching one
// tiling noise texture over the map read as mottled haze at partial cover and — a tiling field at full
// coverage being uniform — as a flat grey veil at full cover, ΔE 13.99 with every pixel changed. See
// CloudSheetLayout's header for what discrete sheets buy over that; the short version is that a repeat
// is a rhythm and a sky does not have one.
//
// The arrangement is §11a's: a fixed set of slot materials, each drawn as one bounded quad with its
// own transform. What is new is that the quads MOVE — an aurora's sheets stand still and shimmer,
// where a cloud's whole character is that it goes somewhere.
//
// THE SHAPE IS BAKED ONCE, EVER. A bounded sheet's shape does not depend on how cloudy it is, where it
// has drifted to, or what colour the sun is — coverage became a count of sheets, drift became a
// transform, colour became a per-sheet material tint. So the atlas is filled in this static
// constructor during load and never touched again, which deletes the 7 ms main-thread bake the tiled
// version paid whenever the cloud fraction moved.
//
// [StaticConstructorOnStartup] is mandatory: `new Material(...)`, `new Texture2D(...)` and the bake
// itself must happen on Unity's main thread.
[StaticConstructorOnStartup]
public static class CloudSheetOverlay
{
    // 3x3 blobs of 128: ONE ROW PER DECK (§25b), three shapes each.
    //
    // This was 2x2 and one cloud TYPE, with variety coming only from shape, mirroring, size and
    // speed — a sky of twelve of the same thing. The row is now the cloud's KIND, shaped by its own
    // deck's curve in CloudField.FillBlobAtlas, so a cirrus cell is thin and streaky everywhere in
    // row 2 however its column is picked. That is why the count of decks and the atlas's axis are
    // the same number and have to stay so; CloudSheetLayout.BlobFor is the one place that maps
    // between them.
    //
    // A SECOND ATLAS WAS THE OTHER OPTION AND IS THE WRONG ONE. §23b and §23c must draw the SAME
    // shapes this does — they are the light this cloud adds and the light it blocks — so a per-type
    // atlas would mean three textures that all three lanes had to agree on picking from, i.e. three
    // chances for the sky and the ground to be showing different clouds. One atlas, rows for types,
    // keeps the agreement structural.
    public const int AtlasCells = 3;

    // 384 = 3 x 128, so a blob keeps the 128 px it had at 2x2 rather than being squeezed to 85 to
    // fit an extra row into the old 256. The bake is 2.25x the texels it was, which is paid ONCE in
    // the static constructor below and never again — see FillBlobAtlas's "baked once, ever" note.
    //
    // PUBLIC because §25c's volume has to be baked at exactly this size, from exactly this seed, or
    // the 3-D shape the shader marches and the 2-D silhouette everything else draws are two
    // different clouds. All three cloud lanes share one set of shapes; that invariant is the reason
    // these are constants somewhere other subsystems can read rather than local literals.
    public const int AtlasSize = 384;

    public const int AtlasSeed = 20260810;

    public static readonly Texture2D Atlas = BuildAtlas(1f);

    // §25d's atlas: the same shapes with the alpha curve applied (issue #144).
    //
    // A SECOND TEXTURE RATHER THAN A CURVE AT DRAW TIME, for one structural reason: this atlas is
    // baked in a static constructor at load, and the feature flags it would have to consult are set
    // afterwards — by the settings screen, or mid-scenario by the harness. Baking both costs 590 KB
    // and a few milliseconds once, and it is what lets the flag be flipped between two frames of one
    // live A/B, which is the only way the two get compared under identical everything else.
    public static readonly Texture2D PresentAtlas = BuildAtlas(CloudSheetMath.PresenceAlphaGamma);

    // One material per sheet slot, allocated up front. Materials cannot be re-tinted between
    // Graphics.DrawMesh calls in a frame — the call is deferred, so a later colour write would reach
    // every quad already queued — which is exactly why AuroraCurtainOverlay keeps an array too.
    private static readonly Material[] SheetMats = BuildSheetMaterials();

    // The alpha last written to each slot, cached rather than read back: Material.color is a native
    // round trip in both directions. NaN so the first draw always writes.
    private static readonly float[] LastAlpha = NewNaNArray(CloudSheetLayout.MaxSheets);

    public static void Draw(Map map)
    {
        float alpha = CloudLayers.SheetAlphaFor(map);
        if (alpha <= 0f)
            return;

        // Shared with §23b and §23c: one layout, three passes, so a bright patch of ground is under
        // a gap by construction rather than by two subsystems being tuned to look alike.
        int count = CloudSheetDraw.PlaceSheets(map, out CloudSheetLayout.Placement[] Placements);
        if (count <= 0)
            return;

        // The colour ends and the gradient axis are read once per frame, not once per sheet: they are
        // properties of the sun, and every sheet is under the same one.
        SkyColorTemperature.Rgb hot = CloudLayers.HotTintFor(map);
        SkyColorTemperature.Rgb cool = CloudLayers.CoolTintFor(map);
        CloudLayers.GradientAxisFor(map, out int axisU, out int axisV);

        // ELEVATION AND SKY GLOW ONCE, ILLUMINATION PER SHEET. Both readings are properties of the
        // map; how much of a given cloud's light is coming from beneath, and therefore how bright and
        // how opaque that cloud is, is a property of the DECK it sits on (§25b), because a deck loses
        // the sun at its own altitude's depression angle. Hoisting either out of the loop the way
        // this used to is what made the whole sky recolour and go out together — see
        // CloudSheetMath.UnderlitFraction and DeckIllumination.
        float elevation = SolarPosition.ElevationForMap(map);
        float skyGlow = map.skyManager.CurSkyGlow;

        float altitude = AltitudeLayer.FogOfWar.AltitudeFor() + Altitudes.AltInc;

        // §25c: whether this frame raymarches the cloud volume per pixel or draws §25b's baked
        // atlas. Decided ONCE, not per sheet — a frame with some sheets marched and some baked would
        // show two different cloud models side by side in one sky.
        //
        // `Available` is checked ahead of the flag rather than after it, so a player who turns the
        // feature on where the shader cannot run gets the baked cloud instead of an empty sky. That
        // is not a defensive habit: only Linux bundles are built today.
        bool volumetric = CelestialLightingFeatures.CloudVolume && CloudVolumeShader.Available;

        // §25d, read once per frame for the same reason the tints are: it is a property of the build,
        // not of a sheet. Off leaves every term exactly where §25b put it.
        bool sunlitOpacity = CelestialLightingFeatures.CloudPresence;
        float directCeiling = sunlitOpacity
            ? CloudSheetMath.SunlitDeckCeiling
            : CloudSheetMath.UnderlitDeckFloor;

        // The sun's own geometry, read once per frame for the same reason the tints are: every sheet
        // is under one sun. Only the volumetric path uses it, and SolarPosition is memoised per
        // frame anyway (issue #12), but reading it here keeps the per-sheet body free of live state.
        float azimuth = volumetric ? CloudLayers.SunAzimuthFor(map) : 0f;

        for (int i = 0; i < count; i++)
        {
            CloudSheetLayout.Placement placement = Placements[i];

            // Off-map sheets are placed but not drawn. They still count toward everyone else's
            // overlap depth, which is right — a sheet half over the edge is genuinely stacked on the
            // one beside it — but there is no reason to issue a draw call for a quad nobody can see.
            if (!CloudSheetLayout.OnScreen(placement, map.Size.x, map.Size.z))
                continue;

            // Overlapping sheets read as thicker cloud rather than as one slightly more opaque one —
            // capped, or a busy sky becomes a white slab.
            float boost = CloudSheetMath.OverlapBoost(
                CloudSheetLayout.OverlapDepth(Placements, count, i));

            float underlit = CloudSheetMath.UnderlitFraction(
                elevation, CloudDeckMath.ShadowEntryDegrees(placement.Deck));

            // Whichever is brighter, the ambient light everything gets or the direct light only this
            // deck is getting. It scales the alpha as well as the colour, so a shadowed sheet is both
            // darker AND sheerer (a dark sheet at full alpha would black the colony out wholesale,
            // which is a lighting change rather than a cloud) — and, the other way round, a deck still
            // catching the sun stays opaque enough to read while everything under it fades.
            //
            // §25d splits the two apart: see CloudSheetMath.DeckOpacity for why a cloud's opacity is
            // a fact about the cloud and not about the light on it, and for the measurement that says
            // the coupling — not the renderer — is what makes a sunset cloud invisible.
            float illumination = CloudSheetMath.DeckIllumination(skyGlow, underlit, directCeiling);
            float opacity = sunlitOpacity
                ? CloudSheetMath.DeckOpacity(skyGlow, underlit)
                : illumination;

            Color colour = SheetColour(
                placement, hot, cool, axisU, axisV, underlit,
                Mathf.Min(illumination * boost, 1f),
                Mathf.Min(alpha * opacity * placement.Alpha * boost, 1f), map);

            // The two renderers take the SAME colour and the SAME alpha. That is what makes the
            // feature flag an honest A/B: everything upstream of the renderer — placement, deck,
            // overlap, illumination, opacity — is identical between the two frames, and the only
            // difference is whether that colour was multiplied into a baked texture or interpolated
            // toward at every step of a march.
            Material material = volumetric
                ? VolumeSheet(i, placement, colour, hot, cool, azimuth, elevation)
                : BakedSheet(i, placement, colour, sunlitOpacity);

            // NO OPEN-SKY MASK, deliberately. §23b and §23c mask to unroofed cells because they are
            // light reaching the GROUND, which a roof stops. A cloud is above the roof and should draw
            // over the whole map, buildings included — masking it would carve a cloud-shaped hole out
            // of the sky wherever somebody had built a barn.
            Graphics.DrawMesh(
                MeshPool.plane10,
                Matrix4x4.TRS(
                    new Vector3(placement.CenterX, altitude, placement.CenterZ),
                    Quaternion.identity,
                    new Vector3(placement.Size, 1f, placement.Size)),
                material,
                0);
        }
    }

    // §25b's baked path, unchanged: the atlas cell, the mirroring and the tint go onto the slot's
    // Transparent material and it draws.
    private static Material BakedSheet(
        int slot, in CloudSheetLayout.Placement placement, Color colour, bool present)
    {
        Material material = SheetMats[slot];

        // Which of the two atlases this frame wears. Written every frame rather than cached: it is
        // one reference assignment against a colour write that already happens here, and caching it
        // would mean a flag flipped between two frames of an A/B did not take until the next reload.
        Texture2D atlas = present ? PresentAtlas : Atlas;
        if (!ReferenceEquals(material.mainTexture, atlas))
            material.mainTexture = atlas;

        // Which of the atlas's blobs this sheet is wearing: its deck picks the row and its ShapeSeed
        // the column. The seed changes once per crossing — so a sheet keeps its silhouette for the
        // whole time it is on screen and wears a different one next time round. A slot-fixed shape
        // (what this did first) meant a long-running colony saw the same clouds on repeat forever.
        int blob = CloudSheetLayout.BlobFor(placement, AtlasCells);

        // The sheet's own quad already spans exactly the sheet, so the UV transform here only has to
        // pick the atlas cell and apply the mirroring — no map-space placement, unlike the two
        // illumination lanes which draw the mask's geometry instead (see CloudSheetDraw).
        float cell = 1f / AtlasCells;
        float scaleU = placement.FlipU ? -cell : cell;
        float scaleV = placement.FlipV ? -cell : cell;
        material.mainTextureScale = new Vector2(scaleU, scaleV);
        material.mainTextureOffset = new Vector2(
            ((blob % AtlasCells) + (placement.FlipU ? 1 : 0)) * cell,
            ((blob / AtlasCells) + (placement.FlipV ? 1 : 0)) * cell);

        // Colour carries the alpha, so a single write covers both.
        material.color = colour;
        LastAlpha[slot] = colour.a;
        return material;
    }

    // §25c's volumetric path: the same placement and the same colour, handed to the raymarch shader
    // along with the colour a part of this cloud the sun never reaches should be.
    //
    // THE SHADOW COLOUR IS AN ABSOLUTE RADIANCE, NOT A HUE, and getting that wrong is the trap this
    // subsystem has already paid for once. §19c's ComposedHue — `cool` here — is normalised to a
    // peak channel of 1 because it exists to carry hue and nothing else. Handed over raw it makes
    // every shadow/lit channel ratio clamp at 1, so the shadows come out as flat dimming with no
    // colour shift at all. Scaling it by AmbientSkyFraction first is what gives a shaded flank the
    // blue of the sky above it instead of a grey of its own.
    private static Material VolumeSheet(
        int slot, in CloudSheetLayout.Placement placement, Color colour,
        in SkyColorTemperature.Rgb hot, in SkyColorTemperature.Rgb cool,
        float azimuth, float elevation)
    {
        Color shadow = new Color(
            colour.r * ShadowRatio(hot.R, cool.R),
            colour.g * ShadowRatio(hot.G, cool.G),
            colour.b * ShadowRatio(hot.B, cool.B),
            colour.a);

        return CloudVolumeShader.Configure(
            slot, placement, AtlasCells, AtlasSize, colour, shadow, azimuth, elevation);
    }

    // The shaded side's brightness as a fraction of the lit side's, per channel. Guarded against a
    // lit channel that has gone to nearly zero, which §8's blue genuinely does at the horizon, and
    // capped at 1 because a shadow that is brighter than the light is not a shadow.
    private static float ShadowRatio(float lit, float hue)
    {
        if (lit <= 1e-4f)
            return 1f;

        return Mathf.Min(1f, hue * CloudVolumeMath.AmbientSkyFraction / lit);
    }

    // ONE COLOUR PER SHEET, sampled from the map-space gradient at that sheet's own centre. The
    // tiled version baked this per texel; a bounded sheet is one place, so it takes one colour —
    // which is cheaper AND is what makes a sky of sheets show its warm side and its cool side at
    // once, since the sheets are in different places.
    private static Color SheetColour(
        in CloudSheetLayout.Placement placement,
        in SkyColorTemperature.Rgb hot, in SkyColorTemperature.Rgb cool,
        int axisU, int axisV, float underlit, float brightness, float alpha, Map map)
    {
        float warmth = CloudField.GradientWarmth(
            placement.CenterX / map.Size.x, placement.CenterZ / map.Size.z, axisU, axisV);

        float sunsetR = Mix(cool.R, hot.R, warmth);
        float sunsetG = Mix(cool.G, hot.G, warmth);
        float sunsetB = Mix(cool.B, hot.B, warmth);

        // THE DAYLIGHT COLOUR IS LIT BY THE SKY, not a fixed grey. A cloud is a white-ish diffuser: it
        // has almost no colour of its own and shows whatever is illuminating it, so a neutral constant
        // was wrong in a way that only shows once the sun is off zenith — the clouds stayed the same
        // dull grey while §8 turned the whole map golden around them.
        //
        // Multiplying the neutral base by §8's own target colour (`hot`, i.e.
        // SkyColorForElevation at this elevation) is the smallest correct fix and adds no new colour
        // authority: at a high sun that colour is near-white and this is a no-op, and as the sun drops
        // the cloud tops warm with the light warming them. Rescaled by the brightest channel so the
        // multiply changes the HUE without darkening — §8's colour is normalised for blending, not for
        // use as a light, and using it raw would dim every cloud as the sun set on top of the
        // brightness term already doing that job.
        float peak = Mathf.Max(hot.R, Mathf.Max(hot.G, hot.B));
        float scale = peak > 0.001f ? 1f / peak : 1f;

        Color day = CelestialLightingFeatures.CloudPresence ? PresentDayColour : DayColour;

        float dayR = day.r * hot.R * scale;
        float dayG = day.g * hot.G * scale;
        float dayB = day.b * hot.B * scale;

        return new Color(
            Mix(dayR, sunsetR, underlit) * brightness,
            Mix(dayG, sunsetG, underlit) * brightness,
            Mix(dayB, sunsetB, underlit) * brightness,
            alpha);
    }

    // Daylight cloud colour: a bright neutral grey, not white. White reads as snow or as a blown-out
    // highlight on a top-down map; real cloud tops under direct sun are close to neutral and read as
    // bright because of their surroundings — a contrast this pass gets for free by being drawn over a
    // lit colony.
    private static readonly Color DayColour = new Color(0.86f, 0.87f, 0.90f);

    // §25d's daylight cloud colour (issue #144), and the single biggest reason the shipped one could
    // not be seen.
    //
    // 0.86 grey is very close to what lit terrain already is — RimWorld's desert sand sits near 0.78,
    // temperate soil and stone not far below it — so an alpha blend toward it converges on the
    // ground's own brightness and the cloud reads as a faint desaturating haze rather than as an
    // object. Raising the ALPHA does not fix that: a mid-grey at high opacity is still mid-grey, and
    // it costs the player the ability to see their base through it.
    //
    // A real cloud top under direct sun is close to white and slightly BLUE-white — it is lit by the
    // whole sky as well as by the sun — and near-white is what separates it from every terrain in the
    // game except snow. Under snow it correctly nearly vanishes, which is what happens when you look
    // down at cloud over an ice sheet.
    //
    // Still not 1.0: the multiply by §8's normalised sun colour happens after this, and a cloud
    // pinned at pure white would clip that hue away at exactly the hours §25b's deck windows exist to
    // colour.
    private static readonly Color PresentDayColour = new Color(0.96f, 0.97f, 1.00f);

    private static float Mix(float a, float b, float t) => a + (b - a) * t;

    private static Texture2D BuildAtlas(float alphaGamma)
    {
        float[] intensity = new float[AtlasSize * AtlasSize];
        byte[] pixels = new byte[AtlasSize * AtlasSize * 4];

        // The per-row shaping is §25b's deck table: row 0 keeps the single curve this atlas baked
        // before decks existed, so a sky that draws only low cloud draws exactly what §25 drew.
        CloudField.FillBlobAtlas(
            intensity, AtlasSize, AtlasCells, seed: 20260810, octaves: CloudField.SheetOctaves,
            // §25d fills the two thin decks in — see CloudDeckMath.PresentShapeCuts. Keyed off the
            // same gamma that already distinguishes the two bakes, so one thing decides which atlas
            // this is rather than two that could disagree.
            rowCut: alphaGamma == 1f
                ? CloudDeckMath.ShapeCuts()
                : CloudDeckMath.PresentShapeCuts(),
            rowGain: CloudDeckMath.ShapeGains(),
            rowFrequencyU: CloudDeckMath.FrequenciesU(),
            rowFrequencyV: CloudDeckMath.FrequenciesV(),
            // §25d narrows the falloff band so a cloud has a BORDER. See
            // CloudField.PresentBlobCoreFraction: at the shipped 0.35 two thirds of every blob is a
            // smooth ramp, which reads fine at noon on brightness alone and fails at sunset, when the
            // cloud and the ground are lit by the same warm light and an edge is the only thing left
            // that can separate them.
            coreFraction: alphaGamma == 1f
                ? CloudField.BlobCoreFraction
                : CloudField.PresentBlobCoreFraction,
            rimBite: alphaGamma == 1f ? 0f : CloudField.PresentRimBite);
        CloudField.WriteBlobRgba(pixels, intensity, intensity.Length, alphaGamma);

        Texture2D texture = new Texture2D(AtlasSize, AtlasSize, TextureFormat.RGBA32, mipChain: false)
        {
            name = alphaGamma == 1f
                ? "CelestialLighting_CloudSheetAtlas"
                : "CelestialLighting_CloudSheetAtlasPresent",
            // CLAMP, not Repeat — the opposite of the tiled version and the whole point. Each blob's
            // alpha already reaches zero inside its own cell, so nothing wraps and nothing repeats.
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };

        texture.LoadRawTextureData(pixels);
        texture.Apply(false);
        return texture;
    }

    private static Material[] BuildSheetMaterials()
    {
        Material[] materials = new Material[CloudSheetLayout.MaxSheets];
        for (int i = 0; i < materials.Length; i++)
        {
            // Alpha-blended rather than additive: cloud OCCLUDES. MoteGlow could only ever brighten
            // what is behind it, which is the one thing a cloud never does to the ground below it.
            materials[i] = new Material(ShaderDatabase.Transparent) { mainTexture = Atlas };
        }

        return materials;
    }

    private static float[] NewNaNArray(int length)
    {
        float[] values = new float[length];
        for (int i = 0; i < values.Length; i++)
            values[i] = float.NaN;

        return values;
    }
}
