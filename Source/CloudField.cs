using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file, same discipline as
// AuroraCurtain.cs / CloudUnderlightMath.cs. Compiled into both Source (net481, inside RimWorld) and
// Tests (net8.0, standalone) via a linked <Compile Include>, so the exact code that ships is the
// exact code under test.
//
// Subsystem 23b (DESIGN.md §23b, issue #88 OPTION 2): the spatial half of cloud-base underlighting.
//
// WHAT §23 COULD NOT DO. §23 (option 1) modulates the STRENGTH of §8's single flat sky colour by how
// much of the cloud deck is still catching direct sunlight from beneath. That gets the timing and the
// intensity of the mechanism right, and issue #88 is explicit that it cannot get the thing people
// actually mean by a dramatic sunset: warm underlit cloud standing against a cool vault is a
// difference between two places on screen, and one colour has no way to be two colours. Averaging
// them produces a neutral mud, which is worse than not trying. This file is the other half — the
// structure — drawn additively through the pass §24 built (epic #103) instead of through the sky
// palette's multiply.
//
// THE PARTITION: THE FLAT LANE CARRIES THE MEAN, THIS LANE CARRIES WHAT IS ABOVE IT. The obvious
// implementation — draw warm light proportional to how underlit the cloud is — would render a second
// time what §23 already renders through §8's tint, exactly the double-count SnowGlareMath.
// UndrawableExcess exists to avoid one subsystem over. So `Residual` below subtracts the field's own
// areal mean: what is drawn at a point is how much MORE underlit cloud sits there than the map
// average, and the average itself is left to §23. The two lanes partition one quantity, and neither
// needs to know the other exists at draw time.
//
// That subtraction is also what makes the two degenerate skies degenerate:
//
//   cloud fraction 0   — no cloud, no structure, nothing drawn (and §23 has nothing to modulate)
//   cloud fraction 1   — a solid overcast is uniform, so there is no structure either: an unbroken
//                        deck is exactly the sky a single flat colour describes perfectly, and this
//                        lane correctly stands down and leaves all of it to §23
//   cloud fraction 0.5 — half lit deck, half open vault, the largest contrast a sky can show
//
// The middle case peaking is not tuning. It falls out of subtracting a mean from a field whose values
// are bounded by [0, 1], and CloudFieldTests pins the whole curve rather than its endpoints.
public static class CloudField
{
    // Baked texture size per axis. Deliberately small: a full re-bake walks every texel (CloudPreview
    // prints the timing), and there is nothing fine-grained in what is being drawn.
    //
    // AND "NOTHING FINE-GRAINED" IS THE SCOPE, NOT A LIMITATION — it is the single most important
    // sentence in this file. §23b draws the light a cloud base BOUNCES BACK DOWN ONTO THE GROUND. It
    // is not a picture of clouds. Bounced light off a deck is diffuse by construction: every point on
    // the ground sees a large solid angle of lit cloud, so the illumination varies smoothly over tens
    // of cells and has no edges of its own to render. A sharper field here would not look more like
    // clouds, it would look like cloud SHADOWS painted on the terrain, which is a different (and
    // wrong) claim about where the light is coming from.
    //
    // An actual drawn cloud layer — §11a-style, in the sky, with shapes — is a separate subsystem and
    // has its own ticket. This one deliberately stops at the illumination.
    public const int Resolution = 64;

    // How many map cells one texture tile spans. One tile is a little smaller than a large (250x250)
    // map, so a colony sees roughly one field's worth of variation at a time rather than a repeating
    // wallpaper — with the base lattice below, that puts a single bright region at ~60 cells, about
    // the size of a built-up colony core.
    public const float CellsPerRepeat = 240f;

    // Lattice cells per texture tile per axis, i.e. the base frequency of the noise. Kept as a
    // separate constant from the resolution so the two can be reasoned about independently: this is
    // the SHAPE of the field, the resolution is only how finely it is sampled.
    public const int LatticeCells = 4;

    // Three octaves, matching §22's CloudCoverDrift rather than §11a's aurora. Two reads as a single
    // smooth blob; more than three puts detail below one texel, where bilinear filtering throws it
    // away and the bake pays for it anyway — and, per the scope note above, detail is not what this
    // field is for.
    public const int Octaves = 3;

    // Bins used to find the coverage threshold from the tile's own histogram. See ThresholdFor.
    public const int HistogramBins = 256;

    // Width of the soft edge, in noise units. A cloud does not have a coastline: the underlit patch
    // has to fade out or the additive quad shows a hard contour that reads as a decal on the ground
    // rather than as light. Epic #103 asks for soft edges as a first-class property of the shared
    // pass; here they are free, because the field is a texture and the fade is a smoothstep in the
    // bake rather than inset rim geometry.
    public const float EdgeSoftness = 0.16f;

    // Ticks for the field to drift exactly one full tile, i.e. CellsPerRepeat map cells. 7200 puts a
    // patch at roughly two map cells per real-time second at 1x speed — a cloud shadow crosses a
    // colony in a couple of minutes of watching, and moves ~80 cells over the hour a sunset lasts.
    // Physical cloud speeds (10 m/s, i.e. ~14 cells per TICK once RimWorld's 1.44 in-game seconds per
    // tick are accounted for) are not usable here: they would smear the field into flicker.
    public const int DriftTileTicks = 7200;

    // Drift direction. Not axis-aligned, because a field panning due east reads as the camera moving
    // rather than as weather moving — the same observation AuroraFieldRegistry.Contour records about
    // rigid translation, solved there with counter-panning layers and here with one oblique one.
    public const float DriftU = 1f;
    public const float DriftV = 0.35f;

    // The coverage field itself, in [0, 1]: how much cloud sits over the point (u, v) of the tile,
    // before any threshold. Tileable by construction — AuroraNoise wraps on an integer lattice — which
    // is what lets the drift below be a UV pan rather than a re-bake, and what keeps a seam from
    // sweeping across the colony once per drift cycle.
    //
    // The seed is the map tile id at the call site, same as §22's cloud fraction and §20c's aerosol
    // drift: two colonies on one planet get unrelated skies, and one colony's sky is stable across
    // save and load without anything being persisted.
    public static float Coverage(float u, float v, int seed) => Coverage(u, v, seed, Octaves);

    // The same field sampled at a chosen octave count, which is how §25's drawn sheet gets cloud-like
    // detail out of the field §23b/§23c deliberately keep soft.
    //
    // THE TWO AGREE WHERE IT MATTERS, and that is why this is an octave count rather than a second
    // field. Adding octaves layers finer detail ON TOP of the same lattice at the same seed, so the
    // large-scale structure — which patch is cloudy — is identical; only the edges gain detail. The
    // illumination lanes want the blurred version because bounced and blocked light genuinely are
    // blurred versions of the cloud pattern, and the sheet wants the detailed one because it is
    // drawing the cloud itself. Same field, two levels of detail, no possibility of them disagreeing
    // about where the clouds are.
    //
    // (Not bit-identical at large scale, strictly: fBm normalises by the summed amplitude, so extra
    // octaves shift every value slightly. The per-tile quantile threshold absorbs that — covered area
    // still comes out as the fraction asked for — which is one more thing ThresholdFor buys.)
    public static float Coverage(float u, float v, int seed, int octaves) =>
        AuroraNoise.Fbm(u * LatticeCells, v * LatticeCells, LatticeCells, LatticeCells, seed, octaves);

    // How underlit-cloud-covered a point is, in [0, 1], given the coverage value above which the deck
    // counts as cloud. A smoothstep band of width EdgeSoftness straddles the threshold — half above,
    // half below, so the 50% contour sits exactly on it and the softening does not bias the covered
    // area. Smoothstep rather than a linear ramp so the patch edge has no visible crease where it
    // meets flat 0 or flat 1, the same reason AuroraNoise fades its lattice with smootherstep.
    public static float PatchIntensity(float coverage, float threshold)
    {
        float lower = threshold - EdgeSoftness * 0.5f;
        float t = Clamp01((coverage - lower) / EdgeSoftness);
        return t * t * (3f - 2f * t);
    }

    // The coverage value above which this tile counts as cloud, chosen so that the covered AREA comes
    // out as the fraction asked for.
    //
    // A FIXED THRESHOLD DOES NOT WORK HERE, and finding out why is what this method is. Fractal value
    // noise is strongly peaked around its mean, so a fixed cut lands in the steep part of the
    // histogram where a small move in the cut is a large move in area. Worse, the tile is a SMALL
    // SAMPLE — LatticeCells is 4, so one tile holds 16 base lattice cells — and its median therefore
    // varies substantially from seed to seed. Measured across three tile seeds, one fixed cut gave
    // covered areas of 0.41, 0.78 and 0.70 for the same requested 0.5, i.e. the number the rest of the
    // subsystem treats as "how cloudy is it" would have meant something different on every colony.
    //
    // Reading the threshold off the tile's own histogram removes both problems at once and removes
    // the two tuning constants that were standing in for it. Cost is one extra pass over the field
    // during a bake that already walks it twice.
    //
    // The two ends are handled explicitly rather than left to the histogram, because the endpoints are
    // a CLAIM (see this file's header: a uniform sky has no structure and must draw nothing), and a
    // quantile of a discrete histogram would land a hair inside them.
    public static float ThresholdFor(float[] coverage, int count, float cloudFraction)
    {
        float fraction = Clamp01(cloudFraction);

        // Above every possible coverage value, plus the band, so nothing is cloud.
        if (fraction <= 0f)
            return 1f + EdgeSoftness;

        // Below every possible value, likewise, so all of it is.
        if (fraction >= 1f)
            return -EdgeSoftness;

        int[] bins = new int[HistogramBins];
        for (int i = 0; i < count; i++)
        {
            int bin = (int)(Clamp01(coverage[i]) * (HistogramBins - 1));
            bins[bin]++;
        }

        // Walk down from the top until the requested area has been accounted for. Descending rather
        // than ascending because the quantity being asked for is "the cloudiest `fraction` of the
        // tile", and accumulating from the end it is defined from avoids an off-by-one at fraction 1.
        float wanted = fraction * count;
        float accumulated = 0f;

        for (int bin = HistogramBins - 1; bin >= 0; bin--)
        {
            float next = accumulated + bins[bin];
            if (next >= wanted)
            {
                // Interpolate inside the bin, so a slowly drifting cloud fraction moves the threshold
                // smoothly instead of stepping once per bin — a step here would read on screen as the
                // whole cloud field flickering a size larger every few minutes.
                float within = bins[bin] > 0 ? (next - wanted) / bins[bin] : 0f;
                return (bin + within) / (HistogramBins - 1);
            }

            accumulated = next;
        }

        return 0f;
    }

    // Fills `intensity` (length width*height) with PatchIntensity over the whole tile and returns its
    // areal mean — the quantity §23's flat lane is already rendering, and therefore the quantity this
    // lane has to subtract before drawing anything.
    //
    // Returning the mean rather than subtracting it in place is deliberate: the caller re-writes the
    // texture bytes whenever the SUN's colour moves (every few frames) but only re-runs this noise
    // walk when the cloud FRACTION moves (a few times an in-game hour), so the two stages are split at
    // exactly the boundary where their cadences differ. See CloudUnderlightLayer.
    public static float FillIntensity(
        float[] intensity, int width, int height, float cloudFraction, int seed) =>
        FillIntensity(intensity, width, height, cloudFraction, seed, Octaves);

    public static float FillIntensity(
        float[] intensity, int width, int height, float cloudFraction, int seed, int octaves)
    {
        if (intensity == null || width <= 0 || height <= 0)
            return 0f;

        int count = width * height;
        float uScale = 1f / width;
        float vScale = 1f / height;

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            // Sampled at texel CENTRES (+0.5), not corners. A corner-sampled tile repeats its first
            // column when it wraps, which shows up as a one-texel-wide stationary stripe in an
            // otherwise drifting field — the classic tiling-noise off-by-one.
            float v = (y + 0.5f) * vScale;

            for (int x = 0; x < width; x++)
                intensity[row + x] = Coverage((x + 0.5f) * uScale, v, seed, octaves);
        }

        // Raw coverage in hand, the threshold is a property of THIS tile (see ThresholdFor), so the
        // second pass turns coverage into intensity in place.
        float threshold = ThresholdFor(intensity, count, cloudFraction);

        double sum = 0.0;
        for (int i = 0; i < count; i++)
        {
            float value = PatchIntensity(intensity[i], threshold);
            intensity[i] = value;
            sum += value;
        }

        return (float)(sum / count);
    }

    // How much MORE underlit cloud sits at a point than the map average — the quantity actually drawn.
    // Floored at zero because an additive pass can only add: a point with LESS cloud than average is
    // not a place to subtract light, it is simply a place this lane leaves to the flat one.
    public static float Residual(float intensity, float mean)
    {
        float residual = intensity - mean;
        return residual < 0f ? 0f : residual;
    }

    // Writes the RGBA32 bytes the texture wants: the colour in RGB, the residual in alpha.
    //
    // THE COLOUR IS NOT ONE COLOUR, AND THAT IS WHERE THE DRAMA IS. A single flat tint across the
    // whole field adds warm light everywhere and reads as the map being turned up — which is exactly
    // what §23's flat lane already does, one lane down, and is the thing this one exists to escape.
    // What makes a real sunset dramatic is that the light arriving at the ground is a DIFFERENT
    // COLOUR in different directions: the deck toward the sun is lit through the longest, reddest
    // path and bounces deep orange down, while the deck away from it is lit by the anti-solar
    // twilight sky and bounces pink/magenta. This blends between those two ends across the field.
    //
    // Both ends are existing authorities rather than a palette invented here — the same discipline
    // §23 kept by modulating §8's tint instead of introducing a colour target. `hot` is §8's own
    // SkyColorForElevation, the colour it is already blending the sky toward; `cool` is §19c's
    // ComposedHue, this codebase's answer for twilight purple. §23b's novelty stays spatial.
    //
    // THE AXIS IS QUANTISED TO THE EIGHT DIRECTIONS THAT TILE, which is a real compromise and worth
    // stating plainly. The texture repeats and pans (that is how drift is free), so anything baked
    // into it must be periodic over the tile or a seam sweeps across the colony. A gradient along an
    // arbitrary sun azimuth is not periodic; a cosine along an INTEGER lattice direction is. So
    // GradientAxis rounds the sun's direction to the nearest of the eight, and the colour lobes lie
    // along that. The axis therefore tracks the sun to within 22.5 degrees, and the lobe's phase
    // rides with the field rather than being pinned to the sun — a player cannot see the sun from a
    // top-down camera, so what reads is that the light differs across the map, which is the point.
    //
    // THE BYTES ARE BAKED RATHER THAN SET AS THE MATERIAL'S COLOUR, and that was true before the
    // gradient existed. Both §11a's aurora and §24's glare set Material.color to (1, 1, 1, alpha) —
    // white — and neither has ever asked ShaderDatabase.MoteGlow to multiply a COLOURED material
    // through a texture. SheetMaterial's own header records why this codebase does not guess about
    // that shader: it is not ours, and being wrong renders something plausible rather than nothing.
    // The gradient makes the choice moot anyway, since a material colour could not express it.
    //
    // Cost stays on the cheap side of the cadence split: this walks the bytes, not the noise, so a
    // colour (and an axis) that move every frame cost a memory write per texel, not a field rebake.
    public static void WriteUnderlightRgba(
        byte[] rgba, float[] intensity, int width, int height, float mean,
        float hotR, float hotG, float hotB, float coolR, float coolG, float coolB,
        int axisU, int axisV)
    {
        if (rgba == null || intensity == null || width <= 0 || height <= 0)
            return;

        float uScale = 1f / width;
        float vScale = 1f / height;

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            float v = (y + 0.5f) * vScale;

            for (int x = 0; x < width; x++)
            {
                // 0.5 + 0.5 cos(2 pi s) with s an integer combination of u and v: periodic over the
                // tile in both axes by construction, so it wraps exactly wherever the pan puts it.
                // 1 at the hot end of the lobe, 0 at the cool end.
                float s = axisU * ((x + 0.5f) * uScale) + axisV * v;
                float warmth = 0.5f + 0.5f * MathF.Cos(2f * MathF.PI * s);

                int o = (row + x) * 4;
                rgba[o + 0] = ToByte(Mix(coolR, hotR, warmth));
                rgba[o + 1] = ToByte(Mix(coolG, hotG, warmth));
                rgba[o + 2] = ToByte(Mix(coolB, hotB, warmth));
                rgba[o + 3] = ToByte(Residual(intensity[row + x], mean));
            }
        }
    }

    // §25's sheet resolution and octave count. Denser and deeper than the illumination lanes, because
    // this one is drawing the cloud rather than the light it casts — see Coverage's overload for why
    // more octaves is the same field rather than a second one.
    //
    // BOUNDED BY THE BAKE, NOT BY TASTE, and the first numbers picked were not affordable. 192 at five
    // octaves measured **28.43 ms** in Tools/CloudPreview — nearly two dropped frames at 60 fps, on
    // the main thread, every time the cloud fraction moves a quantum. 128 at four measures about a
    // third of that. It is still a hitch rather than free, and the honest fix is the one §11a already
    // uses (bake in row slices across frames, uploading only on completion); it is not done here
    // because §25 is a prototype whose flag ships off, and because the rolling bake needs the
    // threshold pass restructured to work off a subsample. Named rather than hidden — see DESIGN.md
    // §25's performance note for the measured maxMsPerFrame.
    public const int SheetResolution = 128;

    public const int SheetOctaves = 4;

    // How far out from a blob's centre its alpha starts falling off, as a fraction of the half-width.
    // The rim between this and 1.0 is what makes a bounded sheet have an EDGE rather than a seam: the
    // alpha reaches exactly zero before the quad does, so nothing about the quad is visible.
    public const float BlobCoreFraction = 0.35f;

    // Fills a square atlas of `blobsPerAxis` x `blobsPerAxis` cloud blobs, each one noise shaped by a
    // radial falloff to zero at its own edge.
    //
    // BAKED ONCE, EVER — which is the whole reason §25 moved from a tiling field to bounded sheets.
    // The old arrangement re-walked the noise whenever the cloud fraction moved, at 7 ms a time on the
    // main thread (28 ms before the resolution came down). A bounded sheet's SHAPE does not depend on
    // how cloudy it is, on where it has drifted to, or on what colour the sun is: coverage became a
    // count of sheets, drift became a transform, and colour became a per-sheet material tint. So this
    // runs once in a static constructor during load and never again.
    //
    // An ATLAS rather than one blob, because sheets are drawn several at a time and rotation alone does
    // not hide that they are the same object. Four shapes times a free rotation times a size variation
    // is enough that a sky of a dozen reads as a dozen clouds.
    public static void FillBlobAtlas(float[] intensity, int atlasSize, int blobsPerAxis, int seed, int octaves)
    {
        if (intensity == null || atlasSize <= 0 || blobsPerAxis <= 0)
            return;

        int blobSize = atlasSize / blobsPerAxis;
        float half = blobSize * 0.5f;

        for (int y = 0; y < atlasSize; y++)
        {
            int blobY = y / blobSize;
            int row = y * atlasSize;

            for (int x = 0; x < atlasSize; x++)
            {
                int blobX = x / blobSize;

                // Local coordinates within this blob, in [-1, 1] from its own centre.
                float localX = ((x % blobSize) - half + 0.5f) / half;
                float localY = ((y % blobSize) - half + 0.5f) / half;
                float radius = MathF.Sqrt(localX * localX + localY * localY);

                // Radial falloff, smoothstepped so the rim has no crease. Zero at and beyond the
                // inscribed circle, which is what keeps the quad's corners empty and its edges
                // invisible.
                float fade = 1f - InverseLerpClamped(BlobCoreFraction, 1f, radius);
                fade = fade * fade * (3f - 2f * fade);

                if (fade <= 0f)
                {
                    intensity[row + x] = 0f;
                    continue;
                }

                // Each blob in the atlas gets its own noise seed, so the four are different clouds
                // rather than four crops of one. Sampled on the blob's own local coordinates rather
                // than the atlas's, so the noise scale is the same in every cell of the atlas.
                int blobSeed = seed + (blobY * blobsPerAxis + blobX) * 7919;
                float coverage = Coverage(
                    (x % blobSize) / (float)blobSize, (y % blobSize) / (float)blobSize, blobSeed, octaves);

                // Stretch the noise's own narrow range across [0, 1] so a blob has solid middles and
                // ragged edges rather than being uniformly half-transparent. Value noise clusters
                // around 0.5, so this cut and gain are what turn "grey mush" into "cloud".
                float shaped = Clamp01((coverage - 0.34f) * 3.2f);

                intensity[row + x] = fade * shaped;
            }
        }
    }

    // The blob atlas's bytes: white in RGB, the blob's own shape in alpha. Colour is a per-sheet
    // material tint rather than baked, because every sheet on screen wants a different one (they sit
    // at different points of the sunset's colour gradient) and because a tint that changes every frame
    // must not imply a re-bake.
    public static void WriteBlobRgba(byte[] rgba, float[] intensity, int count)
    {
        if (rgba == null || intensity == null)
            return;

        for (int i = 0; i < count; i++)
        {
            int o = i * 4;
            rgba[o + 0] = 255;
            rgba[o + 1] = 255;
            rgba[o + 2] = 255;
            rgba[o + 3] = ToByte(intensity[i]);
        }
    }

    // How warm the colour gradient is at a point of the MAP, in [0, 1] — 1 at the sunward lobe, 0 at
    // the anti-solar one. The same cosine WriteUnderlightRgba bakes per texel, evaluated instead at a
    // single point, which is all a bounded sheet needs: one sheet is one place, so it takes one colour.
    //
    // Being periodic no longer matters here (nothing tiles), but it is kept rather than replaced with a
    // plain ramp so that §23b's ground patches and §25's sheets are warm in the same places. Two
    // different gradient shapes would have the sky and the ground disagreeing about where the sun is.
    public static float GradientWarmth(float u, float v, int axisU, int axisV)
    {
        float s = axisU * u + axisV * v;
        return 0.5f + 0.5f * MathF.Cos(2f * MathF.PI * s);
    }

    // §25's bytes: the sheet's colour in RGB, its own coverage in alpha.
    //
    // NOT A RESIDUAL, unlike the two illumination writers — see CloudSheetMath.SheetAlpha for why. A
    // drawn cloud is the object, not an adjustment to a flat approximation of it, so alpha is the
    // field's intensity outright and an overcast sky comes out covered rather than uniform-and-blank.
    //
    // The colour blends the same two ends WriteUnderlightRgba uses, along the same tiling axis, but
    // toward `dayR/G/B` as the sun rises — so a lit-from-above deck reads white-grey and a lit-from-
    // beneath one carries the sunset's own gradient. `underlit` is CloudSheetMath.UnderlitFraction,
    // computed once by the caller rather than per texel.
    public static void WriteSheetRgba(
        byte[] rgba, float[] intensity, int width, int height,
        float hotR, float hotG, float hotB, float coolR, float coolG, float coolB,
        float dayR, float dayG, float dayB, float underlit, float brightness,
        int axisU, int axisV)
    {
        if (rgba == null || intensity == null || width <= 0 || height <= 0)
            return;

        float uScale = 1f / width;
        float vScale = 1f / height;
        float mix = Clamp01(underlit);
        float lit = Clamp01(brightness);

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            float v = (y + 0.5f) * vScale;

            for (int x = 0; x < width; x++)
            {
                float s = axisU * ((x + 0.5f) * uScale) + axisV * v;
                float warmth = 0.5f + 0.5f * MathF.Cos(2f * MathF.PI * s);

                // The underlit colour first (the sunset gradient), then blended toward the daylight
                // colour by how much of the deck's light is coming from above.
                float r = Mix(Mix(dayR, Mix(coolR, hotR, warmth), mix), 0f, 1f - lit);
                float g = Mix(Mix(dayG, Mix(coolG, hotG, warmth), mix), 0f, 1f - lit);
                float b = Mix(Mix(dayB, Mix(coolB, hotB, warmth), mix), 0f, 1f - lit);

                int o = (row + x) * 4;
                rgba[o + 0] = ToByte(r);
                rgba[o + 1] = ToByte(g);
                rgba[o + 2] = ToByte(b);
                rgba[o + 3] = ToByte(intensity[row + x]);
            }
        }
    }

    // The tiling direction nearest the sun's horizontal bearing, as the integer (u, v) pair
    // WriteRgba's cosine runs along. See that method for why an arbitrary direction cannot be used.
    //
    // Azimuth is degrees clockwise from north, matching Formulas.SolarAzimuthDegrees, and the map's
    // axes are x = east and z = north, so the sun's direction is (sin, cos). Each component rounds to
    // -1, 0 or +1 at cos(67.5 degrees) = 0.3827, the halfway point between the axis-aligned and
    // diagonal directions — which is what makes all eight equally likely rather than the diagonals
    // swallowing the axes.
    public static void GradientAxis(float sunAzimuthDegrees, out int axisU, out int axisV)
    {
        float radians = sunAzimuthDegrees * (MathF.PI / 180f);
        axisU = RoundToUnit(MathF.Sin(radians));
        axisV = RoundToUnit(MathF.Cos(radians));

        // Both components can only round to zero if the sine and cosine are both under 0.3827, which
        // no angle satisfies — but a NaN azimuth would land here, and a (0, 0) axis makes the cosine
        // constant, i.e. a flat tint with no gradient at all. Falling back to due north keeps the
        // gradient present rather than silently reverting to the thing this exists to escape.
        if (axisU == 0 && axisV == 0)
            axisV = 1;
    }

    private static int RoundToUnit(float component)
    {
        const float DiagonalThreshold = 0.3827f;
        if (component > DiagonalThreshold)
            return 1;

        return component < -DiagonalThreshold ? -1 : 0;
    }

    private static float Mix(float a, float b, float t) => a + (b - a) * t;

    private static float InverseLerpClamped(float a, float b, float v) =>
        MathF.Abs(b - a) < 1e-6f ? 0f : Clamp01((v - a) / (b - a));

    // The drift offset for a tick, in UV. Computed from the absolute tick rather than accumulated per
    // frame, for the reason AuroraSheetSpec.PanU records: an accumulated pan depends on how many
    // frames have been drawn, which makes it unreproducible between two harness runs of the same
    // scenario and therefore unscreenshotable. Wrapping in ticks (rather than letting the float grow)
    // keeps the arithmetic exact for as long as a colony can last.
    public static float DriftOffsetU(int absoluteTicks) => Frac(DriftU * Phase(absoluteTicks));

    public static float DriftOffsetV(int absoluteTicks) => Frac(DriftV * Phase(absoluteTicks));

    private static float Phase(int absoluteTicks)
    {
        int wrapped = absoluteTicks % DriftTileTicks;
        if (wrapped < 0)
            wrapped += DriftTileTicks;

        return wrapped / (float)DriftTileTicks;
    }

    // Fractional part, always positive. DriftV scales the phase past 1 for some ticks, and a negative
    // or >1 offset would be silently accepted by Unity's Repeat wrap while making the two axes
    // disagree about where the tile starts.
    private static float Frac(float v)
    {
        float f = v - (int)v;
        return f < 0f ? f + 1f : f;
    }

    private static byte ToByte(float v)
    {
        float scaled = v * 255f;
        if (scaled <= 0f || float.IsNaN(scaled))
            return 0;

        return scaled >= 255f ? (byte)255 : (byte)(scaled + 0.5f);
    }

    private static float Clamp01(float v)
    {
        if (float.IsNaN(v))
            return 0f;

        return v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
