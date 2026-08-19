using System;

namespace CelestialLighting;

// §25c: the volumetric half of the drawn cloud sheet (DESIGN.md §25c, issue #144).
//
// THE PROBLEM THIS EXISTS TO SOLVE. §25 draws bounded cloud sheets and §25b gives each deck its own
// sunset window, and both are arithmetically correct — at −3.70° the probes read underlit_low 0.000
// against underlit_high 1.000, exactly the "cirrus is the last thing burning" sequence they were
// built for. The frame renders nothing: p90 ΔE 0.00, 1.1% of pixels changed. A flat quad tinted ONE
// colour has no mechanism to express what makes a sunset cloud worth looking at, because the thing
// that makes it worth looking at is that a single cloud is NOT one colour — the sun grazes its top
// while its own bulk keeps the light off everything below that.
//
// So this file computes, per atlas texel, how lit that part of the cloud is. Three terms, in
// descending order of how much they contribute:
//
//   1. SELF-SHADOWING. March toward the sun across the blob's own height field. If any column along
//      the way stands above the light ray, this texel is in that column's shadow. At a grazing sun
//      the ray barely climbs (and below the horizon it DESCENDS), so shadows run long and only the
//      highest peaks stay lit. This is the effect in one sentence.
//   2. LAMBERT on the height gradient, which brightens the sunward flank of every dome.
//   3. RIM GLOW, where the cloud is thin enough to transmit. This is the silver lining, and #144
//      names it the single biggest contributor to "vivid".
//
// WHY THIS IS NOT A RAYMARCHER, AND WHY §11a's REJECTION DOES NOT APPLY. §11a dropped raymarching
// for the aurora because there is no VIEW ray to smear along under a fixed top-down orthographic
// camera and no parallax to sell. That argument is about the view. Self-shadowing is about the
// LIGHT, and a light ray at a grazing angle is fully visible from directly overhead — it is the
// reason a satellite photo taken at the terminator looks nothing like one taken at noon. The march
// here is along the sun direction across a 2-D height field, not along the view direction through a
// 3-D volume, which is why it costs a texture bake rather than a shader.
//
// THE VERTICAL SCALE IS DELIBERATELY NOT PHYSICAL. A sheet is 0.66 of the map's shorter axis, so on
// a 250-cell map it stands in for a cloud about 165 m across while claiming to BE a cumulus a
// kilometre wide. §25 already lives with that fiction. If the height field were then given its real
// 2 km of vertical extent the aspect ratio would be absurd — every texel would shadow every other
// texel at any sun below ~40°, and the effect would be a binary "all dark" rather than a shape. So
// height is expressed as a fraction of the BLOB RADIUS (MaxHeightFraction), and a deck's real
// thickness in metres enters only as a RATIO against ThicknessReferenceMetres. That keeps the part
// that carries meaning — cumulus is deep and self-shadows hard, cirrus is a flat sheet and barely
// does — while dropping the part that would only produce a degenerate picture.
//
// WHAT THIS WRITES, AND WHY IT IS A MODULATION RATHER THAN A COLOUR. The shipped draw path already
// computes one colour per sheet and puts it in `material.color`; `ShaderDatabase.Transparent`
// multiplies that by the texel's RGB. Baking an absolute colour here would therefore apply the
// sunset twice. So this writes a MODULATION about the sheet's own colour, normalised so the
// brightest texel is 1.0 — the material colour is what the brightest part of the cloud looks like,
// and everything else is that colour scaled down and (per channel) cooled. The corollary is that
// this lane can only ever DARKEN: an 8-bit texture has no headroom above 1, and neither does
// `Transparent`. Making a cloud brighter than its own material colour is epic #103's problem, not
// this file's, and the rim glow is expressed as "the rim keeps the full colour while the lit top
// falls slightly below it" rather than as a boost past it.
public static class CloudVolumeMath
{
    // How tall a blob's densest texel stands, as a fraction of the blob's RADIUS, at a deck of
    // ThicknessReferenceMetres. Set by eye against Tools/CloudPreview: below about 0.2 the shadows
    // are too short to read as anything but noise, and above about 0.5 a blob shadows itself end to
    // end at any sun low enough to matter, which reads as a dirty smear rather than a lit top.
    public const float MaxHeightFraction = 0.35f;

    // The deck thickness that maps to MaxHeightFraction. 2 km is a cumulus congestus — the deep,
    // towering fair-weather cloud that gives the most dramatic sunset, which is the case this
    // subsystem exists for.
    public const float ThicknessReferenceMetres = 2000f;

    // Density is in [0, 1] and height is density raised to this. Below 1 the surface rises FAST off
    // the ragged edge and then flattens across the core, which is the shape of a cumulus dome. At
    // exactly 1 the height field is just the density field and every blob reads as a cone.
    public const float DomeExponent = 0.6f;

    // How far the shadow march walks, in samples. The step length is derived (see ShadeBlobAtlas) so
    // this is a quality knob rather than a distance one: 24 puts a sample roughly every 1-2 texels
    // over a 128-texel blob at the sun elevations that matter, which is finer than the noise's own
    // finest octave and therefore cannot miss an occluder that the eye could see.
    public const int MarchSteps = 24;

    // Ambient wrap: how much of the direct term survives where the surface faces fully away from the
    // sun. Real cloud is a multiple-scattering medium, so its shadowed side is never black — it is
    // lit by its own neighbours and by the sky. Zero here makes blobs read as lit STONES.
    public const float AmbientWrap = 0.35f;

    // The deepest the modulation goes. A fully self-shadowed texel keeps this fraction of the
    // sheet's colour. Also never zero, for the same multiple-scattering reason.
    public const float ShadowFloor = 0.30f;

    // How much the thin rim outglows the lit interior. Applied to 4d(1-d), which peaks at exactly
    // the half-density band — that is the cloud's edge, for free, without an edge detector.
    public const float RimGain = 0.28f;

    // How bright the sky's own light is against direct sunlight, as seen by a cloud face turned away
    // from the sun.
    //
    // THIS CONSTANT IS WHY THE SHADOWS HAVE A COLOUR AT ALL, and getting it wrong is a trap worth
    // naming because the first cut fell straight into it. `ShadeBlobAtlas` takes the shaded side's
    // colour as an absolute RADIANCE, not as a normalised hue — and §19c's ComposedHue, the obvious
    // thing to hand it, is normalised to a maximum channel of 1 because it is built to carry hue and
    // nothing else. Passing it raw makes every shadow/lit channel ratio clamp at 1, so the shadows
    // come out as pure dimming with no colour shift, which is precisely the "averaged neutral mud"
    // failure epic #103 describes. Multiply the hue by this first.
    //
    // 0.30 is the usual daylight ratio of diffuse sky to direct beam on a clear day. It is low enough
    // that a shaded flank reads as genuinely shaded and high enough that it does not read as black.
    public const float AmbientSkyFraction = 0.30f;

    // Per-deck vertical extent, indexed by CloudDeckMath's deck constants. These are the physical
    // numbers; MaxHeightFraction is what turns them into pixels.
    //
    // DELIBERATELY NOT A FIELD ON CloudDeckMath.Deck, though that is where it belongs. §25b (PR #143)
    // is still open, and adding a column to its table would mean rebasing its unit tests and its
    // documented deck table on every change here. Move it there once #143 lands.
    private static readonly float[] DeckThicknessMetres =
    {
        2000f,  // low  — cumulus / stratocumulus: deep, and the whole reason this subsystem exists
        800f,   // mid  — altocumulus: a layer with real but modest relief
        300f,   // high — cirrus: ice crystals in a sheet, essentially flat, so it barely self-shadows
    };

    public static float ThicknessMetres(int deck) =>
        deck >= 0 && deck < DeckThicknessMetres.Length
            ? DeckThicknessMetres[deck]
            : ThicknessReferenceMetres;

    // The sun's horizontal direction in atlas UV space.
    //
    // Azimuth is degrees clockwise from north, matching Formulas.SolarAzimuthDegrees, so the sun's
    // horizontal direction in (east, north) is (sin az, cos az). The atlas's u runs with map +x
    // (east) and v with map +z (north) — OpenSkyMask.BuildMesh writes each vertex's UV as its own
    // map cell coordinate, and MeshPool.plane10's UVs agree — so (east, north) IS (u, v) with no
    // further transform.
    //
    // UNLIKE §23b's gradient axis this is NOT quantised to the 8 lattice directions. That
    // quantisation exists because the illumination field TILES and a cosine along a non-integer
    // lattice direction is not periodic. The blob atlas is TextureWrapMode.Clamp and nothing in it
    // repeats, so the exact azimuth is free here.
    public static void SunDirection(float azimuthDegrees, out float dirU, out float dirV)
    {
        float radians = azimuthDegrees * (MathF.PI / 180f);
        dirU = MathF.Sin(radians);
        dirV = MathF.Cos(radians);
    }

    // Turns the density atlas into a height field, in TEXELS above the deck's base, per blob row.
    //
    // Height is per-row because the atlas is one row per deck (§25b's layout) and thickness is the
    // one thing that differs between decks here. `rowThicknessMetres` may be null, in which case
    // every row is treated as the reference thickness.
    public static void FillHeightField(
        float[] height, float[] density, int atlasSize, int blobsPerAxis, float[] rowThicknessMetres)
    {
        if (height == null || density == null || atlasSize <= 0 || blobsPerAxis <= 0)
            return;

        int blobSize = atlasSize / blobsPerAxis;
        float radiusTexels = blobSize * 0.5f;

        for (int y = 0; y < atlasSize; y++)
        {
            int blobY = y / blobSize;
            float thickness = rowThicknessMetres != null && blobY < rowThicknessMetres.Length
                ? rowThicknessMetres[blobY]
                : ThicknessReferenceMetres;

            // The deck's thickness enters only as a ratio, and only as a scale on the peak height.
            // A cirrus row at 300 m therefore stands 0.15 as tall as the cumulus row and casts
            // shadows 0.15 as long, which is the whole of "cirrus does not self-shadow".
            float peakTexels = radiusTexels * MaxHeightFraction * (thickness / ThicknessReferenceMetres);
            int row = y * atlasSize;

            for (int x = 0; x < atlasSize; x++)
            {
                float d = density[row + x];
                height[row + x] = d <= 0f ? 0f : peakTexels * MathF.Pow(d, DomeExponent);
            }
        }
    }

    // Writes the per-texel lighting modulation into `rgba`'s RGB, leaving alpha as the density the
    // shipped atlas already carries.
    //
    // `litR/G/B` is the colour of direct sunlight at this elevation (§8's SkyColorForElevation) and
    // `shadowR/G/B` the light the sky puts on the shaded side — §19c's ComposedHue scaled by
    // AmbientSkyFraction, and see that constant for why the scaling is not optional. Both are used
    // only as a RATIO — see the file header on why this is a modulation and not a colour.
    //
    // `strength` at 0 must write flat white, which is byte-for-byte what §25b's WriteBlobRgba
    // produces. That is what makes the feature flag a real A/B baseline rather than a picture of the
    // clouds being absent.
    public static void ShadeBlobAtlas(
        byte[] rgba, float[] density, float[] height, int atlasSize, int blobsPerAxis,
        float sunAzimuthDegrees, float sunElevationDegrees,
        float litR, float litG, float litB,
        float shadowR, float shadowG, float shadowB,
        float strength, bool inVacuum)
    {
        if (rgba == null || density == null || height == null || atlasSize <= 0 || blobsPerAxis <= 0)
            return;

        // A vacuum map has no cloud and no atmosphere to light one. Early return before any
        // arithmetic, per the Vacuum.cs convention, writing the pre-feature value.
        if (inVacuum || strength <= 0f)
        {
            WriteFlat(rgba, density, atlasSize * atlasSize);
            return;
        }

        SunDirection(sunAzimuthDegrees, out float dirU, out float dirV);

        float elevationRadians = sunElevationDegrees * (MathF.PI / 180f);
        float sinE = MathF.Sin(elevationRadians);
        float cosE = MathF.Cos(elevationRadians);

        // The ray's climb per texel travelled. Floored in MAGNITUDE rather than clamped to a sign,
        // because the sign is load-bearing: below the horizon tan is NEGATIVE, the ray descends as
        // it travels, and everything except the highest peaks falls into shadow. That is the sunset,
        // and it falls out of the same expression as noon rather than needing a branch.
        float tanE = MathF.Tan(elevationRadians);
        float climbPerTexel = MathF.Abs(tanE) < 1e-3f ? (tanE < 0f ? -1e-3f : 1e-3f) : tanE;

        int blobSize = atlasSize / blobsPerAxis;
        float radiusTexels = blobSize * 0.5f;
        float peakTexels = radiusTexels * MaxHeightFraction;

        // How far a shadow can reach before it has left the blob entirely. Beyond the blob there is
        // no cloud to be shadowed BY, so marching further only costs time.
        float reachTexels = MathF.Min(blobSize, peakTexels / MathF.Abs(climbPerTexel));
        float stepTexels = reachTexels / MarchSteps;

        // The brightest value the modulation can reach, used to normalise so that the material
        // colour means "the brightest part of this cloud" (see header).
        float peakModulation = 1f + RimGain;

        for (int y = 0; y < atlasSize; y++)
        {
            int blobY = y / blobSize;
            int row = y * atlasSize;

            for (int x = 0; x < atlasSize; x++)
            {
                int i = row + x;
                int o = i * 4;
                float d = density[i];

                // Fully transparent texels are never composited, so their colour cannot be seen and
                // computing it would be 24 march samples spent on nothing. The alpha still has to be
                // written, because this buffer is what gets uploaded.
                if (d <= 0f)
                {
                    rgba[o + 0] = 255;
                    rgba[o + 1] = 255;
                    rgba[o + 2] = 255;
                    rgba[o + 3] = 0;
                }
                else
                {
                    int blobX = x / blobSize;
                    float lit = LitFraction(
                        height, atlasSize, blobSize, blobX, blobY, x, y,
                        dirU, dirV, climbPerTexel, stepTexels);

                    float lambert = LambertTerm(
                        height, atlasSize, blobSize, blobX, blobY, x, y, dirU, dirV, cosE, sinE);

                    // Direct illumination of this patch of cloud top: how much sun reaches it, times
                    // how squarely it faces the sun, wrapped so a fully turned-away face is dim
                    // rather than black.
                    float direct = lit * (AmbientWrap + (1f - AmbientWrap) * lambert);

                    // 4d(1-d) peaks at half density, which is exactly the ragged transition band at
                    // a blob's edge. Gated on `lit` because an edge in shadow does not glow.
                    float rim = RimGain * (4f * d * (1f - d)) * lit;

                    float brightness = (ShadowFloor + (1f - ShadowFloor) * direct + rim) / peakModulation;

                    // Per channel, the shaded side is the sky's colour and the lit side the sun's,
                    // expressed as a ratio because all this can do is scale the sheet's own colour.
                    // Where the sky is BLUER than the sun — which at sunset it always is — the blue
                    // ratio saturates at 1 while the red one falls, and the shadow cools by the red
                    // dropping rather than by the blue rising. That is the only direction a
                    // multiply-only path can cool in, and it is the correct one.
                    rgba[o + 0] = ToByte(ChannelModulation(litR, shadowR, direct) * brightness, strength);
                    rgba[o + 1] = ToByte(ChannelModulation(litG, shadowG, direct) * brightness, strength);
                    rgba[o + 2] = ToByte(ChannelModulation(litB, shadowB, direct) * brightness, strength);
                    rgba[o + 3] = DensityToByte(d);
                }
            }
        }
    }

    // What §25b's WriteBlobRgba writes: flat white, alpha from the shape. Kept here so the
    // strength-0 and vacuum paths produce the pre-feature atlas from this file rather than by
    // arranging for the caller to skip it.
    private static void WriteFlat(byte[] rgba, float[] density, int count)
    {
        for (int i = 0; i < count; i++)
        {
            int o = i * 4;
            rgba[o + 0] = 255;
            rgba[o + 1] = 255;
            rgba[o + 2] = 255;
            rgba[o + 3] = DensityToByte(density[i]);
        }
    }

    // Marches from this texel toward the sun, returning 1 where nothing blocks the light and 0 where
    // something fully does.
    //
    // The march is clamped to the texel's OWN blob cell. Neighbouring cells in the atlas are
    // different clouds that happen to be stored side by side — letting one shadow the next would put
    // a hard vertical seam down the middle of a sheet at the cell boundary.
    private static float LitFraction(
        float[] height, int atlasSize, int blobSize, int blobX, int blobY, int x, int y,
        float dirU, float dirV, float climbPerTexel, float stepTexels)
    {
        float startHeight = height[y * atlasSize + x];
        int minX = blobX * blobSize;
        int minY = blobY * blobSize;
        int maxX = minX + blobSize - 1;
        int maxY = minY + blobSize - 1;

        // The deepest any occluder rises above the ray, which is what sets how HARD the shadow is.
        // Taking the maximum rather than counting blocked samples gives a shadow that deepens as the
        // occluder grows instead of one that switches on the moment a single sample is blocked —
        // the latter reads as aliasing crawling across the cloud as the sun moves.
        float deepest = 0f;

        for (int step = 1; step <= MarchSteps; step++)
        {
            float t = step * stepTexels;
            int sampleX = (int)MathF.Round(x + dirU * t);
            int sampleY = (int)MathF.Round(y + dirV * t);

            bool insideBlob = sampleX >= minX && sampleX <= maxX && sampleY >= minY && sampleY <= maxY;
            if (insideBlob)
            {
                float rayHeight = startHeight + climbPerTexel * t;
                float over = height[sampleY * atlasSize + sampleX] - rayHeight;
                if (over > deepest)
                    deepest = over;
            }
        }

        // Normalised against the blob's peak height so "fully shadowed" means "something a whole
        // cloud-height above the ray", and softened so the shadow has a penumbra rather than an edge.
        float shadow = Clamp01(deepest / MathF.Max(1e-4f, blobSize * 0.5f * MaxHeightFraction));
        return 1f - (shadow * shadow * (3f - 2f * shadow));
    }

    // Lambert on the height field's own gradient. The surface normal of a height field h(u, v) is
    // (-dh/du, -dh/dv, 1) before normalisation; dotting it with the light direction is what makes the
    // sunward flank of every dome brighter than its far side, independently of whether anything
    // shadows it.
    private static float LambertTerm(
        float[] height, int atlasSize, int blobSize, int blobX, int blobY, int x, int y,
        float dirU, float dirV, float cosE, float sinE)
    {
        int minX = blobX * blobSize;
        int minY = blobY * blobSize;
        int maxX = minX + blobSize - 1;
        int maxY = minY + blobSize - 1;

        float left = height[y * atlasSize + Clamp(x - 1, minX, maxX)];
        float right = height[y * atlasSize + Clamp(x + 1, minX, maxX)];
        float down = height[Clamp(y - 1, minY, maxY) * atlasSize + x];
        float up = height[Clamp(y + 1, minY, maxY) * atlasSize + x];

        float dhdu = (right - left) * 0.5f;
        float dhdv = (up - down) * 0.5f;

        float dot = -dhdu * cosE * dirU - dhdv * cosE * dirV + sinE;
        float length = MathF.Sqrt(dhdu * dhdu + dhdv * dhdv + 1f);
        return Clamp01(dot / length);
    }

    // The per-channel ratio between the shaded and lit colours, at this much direct light. Guarded
    // against a zero lit channel, which §8's colour can genuinely approach in blue at the horizon.
    private static float ChannelModulation(float lit, float shadow, float direct)
    {
        float ratio = lit <= 1e-4f ? 1f : Clamp01(shadow / lit);
        return ratio + (1f - ratio) * direct;
    }

    private static byte ToByte(float modulation, float strength)
    {
        // Blend toward flat white by strength, so 0 reproduces the pre-feature atlas exactly and the
        // knob is a straight line between the two rather than a separate code path.
        float value = 1f + (Clamp01(modulation) - 1f) * strength;
        int scaled = (int)(Clamp01(value) * 255f + 0.5f);
        return (byte)scaled;
    }

    private static byte DensityToByte(float density)
    {
        int scaled = (int)(Clamp01(density) * 255f + 0.5f);
        return (byte)scaled;
    }

    private static int Clamp(int value, int min, int max) =>
        value < min ? min : (value > max ? max : value);

    private static float Clamp01(float value) =>
        value < 0f ? 0f : (value > 1f ? 1f : value);

    // ---------------------------------------------------------------------------------------------
    // The 3-D volume (issue #144, second attempt)
    // ---------------------------------------------------------------------------------------------
    //
    // WHY THE HEIGHT FIELD ABOVE WAS NOT ENOUGH. It reads FLAT, and the reason is structural rather
    // than a tuning miss. `CloudField.FillBlobAtlas` contrast-stretches its noise —
    // `clamp01((coverage - cut) * gain)` with a gain of 3.2 — so the middle of every blob CLIPS at 1.
    // A height field derived from that is a mesa: a plateau at exactly peak height across the whole
    // core, with all of its relief crowded into the rim. Self-shadowing then only ever happens at the
    // edges, which is precisely "levels off too flat". No exponent fixes it, because the information
    // was destroyed by the clamp before the height field ever saw it.
    //
    // So this bakes a real third axis. Two things follow that the height field could not do at all:
    // light PASSES THROUGH thin cloud instead of being blocked by a surface, which is where a silver
    // lining actually comes from; and a column's opacity becomes its own optical depth rather than a
    // 2-D density that averaged about 0.3 and left every sunset sheer.
    //
    // WHAT IT COSTS, AND WHY THAT IS AFFORDABLE. The volume is a function of the noise alone — NOT of
    // the sun — so it is baked once at load and never rebuilt. Only the shading below depends on the
    // sun. That split is the whole reason a 3-D field is affordable on the CPU here: the expensive
    // half never runs again, and the half that does run is array lookups rather than noise.
    //
    // It is also, deliberately, the exact input a shader raymarch would want. If this is ever moved
    // to the GPU the volume is uploaded as a 3-D texture unchanged and only `ShadeBlobVolume` is
    // replaced — the bake, the shaping and the deck table all survive.
    public const int VolumeLayers = 20;

    // Lattice cells through the deck's whole depth. Far coarser than the horizontal lattice on
    // purpose: a deck is kilometres wide and hundreds of metres deep, so matching the horizontal
    // frequency would put the same number of features through the thickness as across the sky, and
    // the result reads as static rather than as cloud.
    public const int VerticalLattice = 2;

    public const int DetailOctaves = 3;

    // How much the 3-D noise eats into the shape carved by the 2-D coverage. This is what turns a
    // smooth lens into cauliflower; at 0 the volume is a solid of revolution and looks moulded.
    // Lowered from a first cut of 0.55, which ate so far into the shape that the volume's silhouette
    // no longer matched the 2-D atlas's. That matters more than it looks: all three cloud lanes draw
    // the SAME placements from the SAME shapes, so a drawn cloud whose outline disagrees with the
    // shadow it casts is a worse artefact than a slightly smoother cloud.
    // Raised from 0.38 to give the top surface LUMPS to catch light on.
    //
    // The whole point of this constant is that the 3-D noise SUBTRACTS rather than multiplies, so it
    // bites chunks out of the surface instead of fading it uniformly — which is what turns a smooth
    // lens into cauliflower. At 0.38 the bites are shallow and the top reads as a dome; a dome lit
    // from one side has a light half and a dark half and no peaks.
    //
    // 0.48 rather than the 0.55 a first cut used, which is the ceiling and is there for a reason
    // recorded above: past it the volume's silhouette drifts off the 2-D atlas, and all three cloud
    // lanes draw the same placements from the same shapes, so a cloud whose outline disagrees with
    // its own shadow is a worse artefact than a smoother cloud.
    public const float ErosionStrength = 0.48f;

    // Optical depth per unit density per texel of path. Sets how fast light dies inside cloud, so it
    // controls both how opaque a column is and how dark its own shadow is — one constant for both,
    // because physically it IS one constant.
    public const float ExtinctionPerTexel = 0.16f;

    // Where the visible top surface is taken to be: the depth at which a downward view has
    // accumulated this much optical depth. Not the first non-zero voxel — that would put the surface
    // on the outermost wisp and make every cloud look like a soap bubble.
    public const float SurfaceOpticalDepth = 0.55f;

    // Indexing is COLUMN-CONTIGUOUS: all of a column's layers sit together, because both the surface
    // walk and the opacity integral run down a column and that is the access pattern worth making
    // sequential. The light march jumps between columns and cannot be helped either way.
    public static int VolumeIndex(int x, int y, int layer, int atlasSize, int layers) =>
        ((y * atlasSize) + x) * layers + layer;

    // Bakes the density volume. `rowThicknessMetres` scales each deck row's vertical extent exactly
    // as FillHeightField does, so a cirrus row is a thin sheet and a cumulus row is deep.
    public static void FillBlobVolume(
        byte[] volume, int atlasSize, int blobsPerAxis, int layers, int seed, int octaves,
        float[] rowCut, float[] rowGain, float[] rowFrequencyU, float[] rowFrequencyV) =>
        FillBlobVolume(volume, atlasSize, blobsPerAxis, layers, seed, octaves,
            rowCut, rowGain, rowFrequencyU, rowFrequencyV, BlobCoreFraction, 0f);

    // The same with §25d's silhouette shaping, which the volume needs for the same reason the 2-D
    // atlas does and could not get from it (issue #144).
    //
    // WHY THIS HAD TO BE DUPLICATED HERE AT ALL. §25d's fixes — the narrowed falloff that gives a
    // cloud a border, the rim bite that keeps that border ragged instead of circular, the filled-in
    // thin decks — all live in the 2-D atlas, and the raymarch does not read the atlas: its alpha is
    // the optical depth of THIS volume. So a session with the shader on showed none of them and
    // measured back at the pre-§25d baseline, which reads as "the renderer does nothing" and is
    // really "the renderer was never told".
    //
    // The parameters match CloudField.FillBlobAtlas term for term on purpose. All three cloud lanes
    // share one set of shapes, and a volume whose silhouette disagreed with the atlas would put a
    // cloud over a shadow of a different shape — the failure CloudSheetDraw's header records.
    public static void FillBlobVolume(
        byte[] volume, int atlasSize, int blobsPerAxis, int layers, int seed, int octaves,
        float[] rowCut, float[] rowGain, float[] rowFrequencyU, float[] rowFrequencyV,
        float coreFraction, float rimBite) =>
        FillBlobVolume(volume, atlasSize, blobsPerAxis, layers, seed, octaves,
            rowCut, rowGain, rowFrequencyU, rowFrequencyV, coreFraction, rimBite, 1f);

    // ...and with §25d's density gamma, which is what stops the raymarch drawing a thinner cloud
    // than the bake beside it (issue #144).
    //
    // ONE MECHANISM, NOT TWO. The 2-D atlas lifts its thin end with a gamma
    // (CloudField.WriteBlobRgba) because a blob is mostly wisp and scaling the whole thing clamps
    // the cores instead. The volume needs exactly the same correction for exactly the same reason —
    // measured, its high deck marched to 0.48 of the atlas's alpha, so the deck §25b calls "the one
    // that lingers longest" was the one you could least see through the shader.
    //
    // Applying the SAME constant to voxel density rather than normalising extinction per deck, which
    // was the other candidate and is worse: extinction sets how far the sun reaches as well as how
    // opaque a column is, so scaling it up for a thin deck to fix its alpha also drives its light
    // march to full occlusion — a cirrus sheet pinned at the ambient floor, which is the precise
    // opposite of the sunset this subsystem exists to draw.
    public static void FillBlobVolume(
        byte[] volume, int atlasSize, int blobsPerAxis, int layers, int seed, int octaves,
        float[] rowCut, float[] rowGain, float[] rowFrequencyU, float[] rowFrequencyV,
        float coreFraction, float rimBite, float densityGamma)
    {
        if (volume == null || atlasSize <= 0 || blobsPerAxis <= 0 || layers <= 0)
            return;

        int blobSize = atlasSize / blobsPerAxis;
        float half = blobSize * 0.5f;

        for (int y = 0; y < atlasSize; y++)
        {
            int blobY = y / blobSize;
            float cut = RowValue(rowCut, blobY, CloudField.DefaultShapeCut);
            float gain = RowValue(rowGain, blobY, CloudField.DefaultShapeGain);
            float frequencyU = PositiveRowValue(rowFrequencyU, blobY);
            float frequencyV = PositiveRowValue(rowFrequencyV, blobY);

            for (int x = 0; x < atlasSize; x++)
            {
                int blobX = x / blobSize;

                float localX = ((x % blobSize) - half + 0.5f) / half;
                float localY = ((y % blobSize) - half + 0.5f) / half;
                float radius = MathF.Sqrt(localX * localX + localY * localY);

                float fade = 1f - InverseLerpClamped(coreFraction, 1f, radius);
                fade = fade * fade * (3f - 2f * fade);

                int blobSeed = seed + (blobY * blobsPerAxis + blobX) * 7919;
                float u = (x % blobSize) / (float)blobSize;
                float v = (y % blobSize) / (float)blobSize;

                // THE COVERAGE IS TAKEN UNSTRETCHED, which is the whole correction over the height
                // field. `CloudField.Coverage` is the raw fBm before the cut-and-gain clamp, so it
                // still varies across the core instead of saturating there, and it is that variation
                // which becomes the cloud's top profile.
                float coverage = CloudField.Coverage(u * frequencyU, v * frequencyV, blobSeed, octaves);

                // Rim bite raises the CUT with distance rather than scaling the result, so the
                // boundary is a noise contour instead of a circle — see CloudField.PresentRimBite,
                // where tightening the falloff without this drew three visible discs per row.
                float shaped = rimBite > 0f
                    ? (1f - InverseLerpClamped(0.92f, 1f, radius))
                        * Clamp01((coverage - (cut + (1f - fade) * rimBite)) * gain + 0.35f)
                    : fade * Clamp01((coverage - cut) * gain + 0.35f);

                for (int layer = 0; layer < layers; layer++)
                {
                    int index = VolumeIndex(x, y, layer, atlasSize, layers);

                    if (shaped <= 0f)
                    {
                        volume[index] = 0;
                    }
                    else
                    {
                        float heightFraction = (layer + 0.5f) / layers;

                        // The vertical profile of a convective cloud: a firm flat base, a body, and a
                        // top that tapers away. Both ends are ramps rather than steps or the volume
                        // would show as a slab with a hard lid.
                        float baseRamp = InverseLerpClamped(0f, 0.18f, heightFraction);
                        float topTaper = 1f - InverseLerpClamped(shaped * 0.55f, shaped, heightFraction);
                        float profile = baseRamp * Clamp01(topTaper);

                        // The 3-D noise ERODES the profile rather than multiplying it. Multiplying
                        // makes the whole volume fade uniformly and keeps its silhouette; subtracting
                        // bites chunks out of the surface, which is what gives a cloud its lumps and
                        // its overhangs.
                        // THREE octaves, not the atlas's four. The fourth octave of a 3-D fBm costs
                        // eight more hashes per voxel across 3.5 million voxels and lands at a scale
                        // finer than one texel of the atlas it is eroding, so it is invisible detail
                        // paid for at the highest per-sample price in the subsystem.
                        float detail = AuroraNoise.Fbm(
                            u * frequencyU * 4f, v * frequencyV * 4f, heightFraction,
                            4, 4, VerticalLattice, blobSeed + 31, DetailOctaves);

                        float density = Clamp01(
                            shaped * profile - (1f - detail) * ErosionStrength);

                        volume[index] = DensityToByte(
                            densityGamma == 1f || density <= 0f
                                ? density
                                : MathF.Pow(density, densityGamma));
                    }
                }
            }
        }
    }

    // Shades the volume for one sun position, writing the same RGB-modulation-plus-alpha layout
    // ShadeBlobAtlas writes, so the two are drop-in alternatives for the same draw path.
    //
    // Two marches per texel, and they answer different questions:
    //   DOWN the column, to find where the visible surface is and how opaque the column is overall.
    //   TOWARD THE SUN from that surface, through the volume, accumulating optical depth. Because
    //   this passes THROUGH cloud rather than being stopped by it, thin cloud transmits and lights up
    //   while a deep core goes dark — the silver lining is not a special case here, it is what the
    //   integral does at low optical depth.
    public static void ShadeBlobVolume(
        byte[] rgba, byte[] volume, int atlasSize, int blobsPerAxis, int layers,
        float sunAzimuthDegrees, float sunElevationDegrees,
        float litR, float litG, float litB,
        float shadowR, float shadowG, float shadowB,
        float strength, bool inVacuum)
    {
        if (rgba == null || volume == null || atlasSize <= 0 || blobsPerAxis <= 0 || layers <= 0)
            return;

        SunDirection(sunAzimuthDegrees, out float dirU, out float dirV);

        float elevationRadians = sunElevationDegrees * (MathF.PI / 180f);
        float tanE = MathF.Tan(elevationRadians);
        float climbPerTexel = MathF.Abs(tanE) < 1e-3f ? (tanE < 0f ? -1e-3f : 1e-3f) : tanE;

        int blobSize = atlasSize / blobsPerAxis;
        float peakTexels = blobSize * 0.5f * MaxHeightFraction;
        float layerTexels = peakTexels / layers;
        float stepTexels = MathF.Max(1f, MathF.Min(blobSize, peakTexels / MathF.Abs(climbPerTexel)) / MarchSteps);

        for (int y = 0; y < atlasSize; y++)
        {
            int blobY = y / blobSize;

            for (int x = 0; x < atlasSize; x++)
            {
                int blobX = x / blobSize;
                int o = (y * atlasSize + x) * 4;

                // Down the column: total optical depth, and the layer where the surface sits.
                float columnTau = 0f;
                int surfaceLayer = -1;
                for (int layer = layers - 1; layer >= 0; layer--)
                {
                    float d = volume[VolumeIndex(x, y, layer, atlasSize, layers)] * (1f / 255f);
                    columnTau += d * ExtinctionPerTexel * layerTexels;
                    if (surfaceLayer < 0 && columnTau >= SurfaceOpticalDepth)
                        surfaceLayer = layer;
                }

                float alpha = 1f - MathF.Exp(-columnTau);

                if (alpha <= 0.002f)
                {
                    rgba[o + 0] = 255;
                    rgba[o + 1] = 255;
                    rgba[o + 2] = 255;
                    rgba[o + 3] = 0;
                }
                else
                {
                    // A column too thin ever to reach the surface threshold is lit at its densest
                    // point rather than not at all — that is the wisp, and wisps are exactly what
                    // should glow when the sun is behind them.
                    if (surfaceLayer < 0)
                        surfaceLayer = layers / 2;

                    float startHeight = (surfaceLayer + 0.5f) * layerTexels;
                    float tau = LightMarch(
                        volume, atlasSize, layers, blobSize, blobX, blobY,
                        x, y, startHeight, dirU, dirV, climbPerTexel, stepTexels, layerTexels);

                    float transmittance = MathF.Exp(-tau);

                    // Ambient wrap again, for the same multiple-scattering reason as the height-field
                    // path: a cloud's shaded side is lit by its neighbours and by the sky.
                    float direct = AmbientWrap + (1f - AmbientWrap) * transmittance;

                    float brightness = ShadowFloor + (1f - ShadowFloor) * direct;

                    rgba[o + 0] = ToByte(ChannelModulation(litR, shadowR, direct) * brightness, strength);
                    rgba[o + 1] = ToByte(ChannelModulation(litG, shadowG, direct) * brightness, strength);
                    rgba[o + 2] = ToByte(ChannelModulation(litB, shadowB, direct) * brightness, strength);
                    rgba[o + 3] = DensityToByte(alpha);
                }
            }
        }
    }

    // Optical depth accumulated between a surface point and the sun. Clamped to the texel's own blob
    // for the same reason the height-field march is: neighbouring atlas cells are different clouds.
    private static float LightMarch(
        byte[] volume, int atlasSize, int layers, int blobSize, int blobX, int blobY,
        int x, int y, float startHeight, float dirU, float dirV,
        float climbPerTexel, float stepTexels, float layerTexels)
    {
        int minX = blobX * blobSize;
        int minY = blobY * blobSize;
        int maxX = minX + blobSize - 1;
        int maxY = minY + blobSize - 1;

        float tau = 0f;

        for (int step = 1; step <= MarchSteps; step++)
        {
            float t = step * stepTexels;
            int sampleX = (int)MathF.Round(x + dirU * t);
            int sampleY = (int)MathF.Round(y + dirV * t);
            float sampleHeight = startHeight + climbPerTexel * t;
            int layer = (int)(sampleHeight / layerTexels);

            bool inside = sampleX >= minX && sampleX <= maxX && sampleY >= minY && sampleY <= maxY
                && layer >= 0 && layer < layers;

            if (inside)
            {
                float d = volume[VolumeIndex(sampleX, sampleY, layer, atlasSize, layers)] * (1f / 255f);
                tau += d * ExtinctionPerTexel * stepTexels;
            }
        }

        return tau;
    }

    private static float RowValue(float[] values, int row, float fallback) =>
        values != null && row >= 0 && row < values.Length ? values[row] : fallback;

    private static float PositiveRowValue(float[] values, int row)
    {
        float value = RowValue(values, row, 1f);
        return value > 0f ? value : 1f;
    }

    // Matches CloudField's own blob falloff so the volume's silhouette and the 2-D atlas's agree —
    // the three cloud lanes share one set of shapes and that invariant outranks this file.
    private const float BlobCoreFraction = 0.35f;

    private static float InverseLerpClamped(float a, float b, float value)
    {
        if (MathF.Abs(b - a) < 1e-6f)
            return value < a ? 0f : 1f;

        return Clamp01((value - a) / (b - a));
    }
}
