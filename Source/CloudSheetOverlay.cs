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
    // 2x2 blobs of 128, so four distinct cloud shapes. One cloud TYPE — same noise character
    // throughout — with variety coming from shape, rotation, size and speed. A second type (thin
    // high cirrus against fat cumulus, say) would be a different shaping curve in
    // CloudField.FillBlobAtlas plus a second atlas, and is deliberately not attempted yet.
    // Public so §23b and §23c draw the SAME shapes — they are the light this cloud adds and the
    // light it blocks, so a second atlas would be two clouds claiming to be one.
    public const int AtlasCells = 2;

    private const int AtlasSize = 256;

    public static readonly Texture2D Atlas = BuildAtlas();

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

        float underlit = CloudSheetMath.UnderlitFraction(SolarPosition.ElevationForMap(map));
        float brightness = CloudSheetMath.SheetBrightness(map.skyManager.CurSkyGlow);

        // §26's DECK boundary, when the twilight sweep is drawing (issue #140's depth half). Read once
        // per frame for the same reason the colour ends above are: it is a property of the sun and the
        // deck, so every sheet is under the same one.
        //
        // WHAT THIS CHANGES AND WHY IT IS THE HONEST VERSION. Without it every sheet takes its colour
        // from CloudField.GradientWarmth, a STATIC map-space gradient — so the whole deck turns warm
        // together and the sky reads as flat, which is the "no depth" complaint. Earth's shadow
        // reaches a deck at height h LATER than it reaches the ground, at the angle §23 already
        // computes, so the deck has its own boundary that lags the ground's and lags further for a
        // higher deck. Shading each sheet against THAT gives sheets on the sunward side still catching
        // light while sheets behind the boundary have gone out — parallax that falls out of the
        // altitude the weather already declares rather than a tuned offset.
        //
        // Inert when §26 is off: DeckShadingFor returns false and the gradient below is untouched.
        bool deckShading = TwilightSweep.DeckShadingFor(
            map, out float deckSweep, out float sweepAxisU, out float sweepAxisV);

        float altitude = AltitudeLayer.FogOfWar.AltitudeFor() + Altitudes.AltInc;

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

            // §26's per-sheet lit fraction: 1 where this sheet still catches light, 0 where the
            // deck's own shadow boundary has passed it. Multiplying the frame-wide `underlit` by it
            // is what turns a deck that all warms together into one that goes out in order.
            float sheetUnderlit = underlit;
            float sheetWarmth = float.NaN;

            if (deckShading)
            {
                float axisPosition = TwilightSweepField.AxisPosition(
                    placement.CenterX / map.Size.x, placement.CenterZ / map.Size.z,
                    sweepAxisU, sweepAxisV);

                sheetUnderlit = underlit * TwilightSweepMath.LitFraction(axisPosition, deckSweep);
                sheetWarmth = TwilightSweepMath.Warmth(axisPosition, deckSweep);
            }

            SetSheet(i, placement, hot, cool, axisU, axisV, sheetUnderlit,
                Mathf.Min(brightness * boost, 1f), Mathf.Min(alpha * placement.Alpha * boost, 1f),
                map, sheetWarmth);

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
                SheetMats[i],
                0);
        }
    }

    // `sweepWarmth` is §26's hue for this sheet against the DECK's boundary, or NaN when §26 is not
    // drawing. NaN rather than a bool-plus-value pair because there is exactly one caller and the
    // sentinel keeps the parameter list from growing a second flag that can disagree with the first —
    // and because a NaN that leaked through would produce a visibly broken sheet rather than a
    // plausible wrong colour, which is the failure mode worth having.
    private static void SetSheet(
        int slot, in CloudSheetLayout.Placement placement,
        in SkyColorTemperature.Rgb hot, in SkyColorTemperature.Rgb cool,
        int axisU, int axisV, float underlit, float brightness, float alpha, Map map,
        float sweepWarmth)
    {
        Material material = SheetMats[slot];

        // Which of the atlas's blobs this sheet is wearing. Taken from the placement's own ShapeSeed,
        // which changes once per crossing — so a sheet keeps its silhouette for the whole time it is
        // on screen and wears a different one next time round. A slot-fixed shape (what this did
        // first) meant a long-running colony saw four clouds on repeat forever.
        int blob = placement.ShapeSeed % (AtlasCells * AtlasCells);

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

        // ONE COLOUR PER SHEET, sampled from the map-space gradient at that sheet's own centre. The
        // tiled version baked this per texel; a bounded sheet is one place, so it takes one colour —
        // which is cheaper AND is what makes a sky of sheets show its warm side and its cool side at
        // once, since the sheets are in different places.
        //
        // §26 REPLACES THAT GRADIENT WHEN IT IS DRAWING, rather than blending with it, and the choice
        // is deliberate. Both are answers to "which way is the sun from this sheet", and running two
        // at once would put two crossovers on one sky. §26's is the better answer while it is
        // available: it is keyed to the true azimuth rather than to the eight lattice directions
        // CloudField must round to (issue #139), and it is anchored to the deck's own moving shadow
        // boundary rather than to a phase that rides with the field's pan. Outside §26's window there
        // is no boundary to anchor to, so the static gradient is the only answer there is.
        float warmth = float.IsNaN(sweepWarmth)
            ? CloudField.GradientWarmth(
                placement.CenterX / map.Size.x, placement.CenterZ / map.Size.z, axisU, axisV)
            : sweepWarmth;

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

        float dayR = DayColour.r * hot.R * scale;
        float dayG = DayColour.g * hot.G * scale;
        float dayB = DayColour.b * hot.B * scale;

        Color colour = new Color(
            Mix(dayR, sunsetR, underlit) * brightness,
            Mix(dayG, sunsetG, underlit) * brightness,
            Mix(dayB, sunsetB, underlit) * brightness,
            alpha);

        // Colour carries the alpha, so a single equality check covers both — but only the alpha is
        // cached, because it is the one that is usually still. A colour that has moved with a
        // stationary alpha still needs the write, which is why the comparison is not the gate for it.
        if (alpha != LastAlpha[slot] || true)
        {
            material.color = colour;
            LastAlpha[slot] = alpha;
        }
    }

    // Daylight cloud colour: a bright neutral grey, not white. White reads as snow or as a blown-out
    // highlight on a top-down map; real cloud tops under direct sun are close to neutral and read as
    // bright because of their surroundings — a contrast this pass gets for free by being drawn over a
    // lit colony.
    private static readonly Color DayColour = new Color(0.86f, 0.87f, 0.90f);

    private static float Mix(float a, float b, float t) => a + (b - a) * t;

    private static Texture2D BuildAtlas()
    {
        float[] intensity = new float[AtlasSize * AtlasSize];
        byte[] pixels = new byte[AtlasSize * AtlasSize * 4];

        CloudField.FillBlobAtlas(intensity, AtlasSize, AtlasCells, seed: 20260810, octaves: CloudField.SheetOctaves);
        CloudField.WriteBlobRgba(pixels, intensity, intensity.Length);

        Texture2D texture = new Texture2D(AtlasSize, AtlasSize, TextureFormat.RGBA32, mipChain: false)
        {
            name = "CelestialLighting_CloudSheetAtlas",
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
