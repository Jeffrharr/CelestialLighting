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
// are bounded by [0, 1], and CloudUnderlightFieldTests pins the whole curve rather than its endpoints.
public static class CloudUnderlightField
{
    // Baked texture size per axis. Deliberately small: the structure being drawn is cloud-scale
    // (tens of map cells across), the texture is bilinearly filtered by the GPU on the way to the
    // screen, and a full re-bake walks every texel. 64 keeps a whole rebake at ~4k noise samples, so
    // it can happen inside one frame without the rolling row-slice machinery §11a needed.
    public const int Resolution = 64;

    // How many map cells one texture tile spans. One tile is a little smaller than a large (250x250)
    // map, so a colony sees roughly one cloud field's worth of structure at a time rather than a
    // repeating wallpaper — with the base lattice below, that puts a single patch at ~60 cells,
    // about the size of a built-up colony core.
    public const float CellsPerRepeat = 240f;

    // Lattice cells per texture tile per axis, i.e. the base frequency of the noise. Kept as a
    // separate constant from the resolution so the two can be reasoned about independently: this is
    // the SHAPE of the field, the resolution is only how finely it is sampled.
    public const int LatticeCells = 4;

    // Three octaves, matching §22's CloudCoverDrift rather than §11a's aurora. Cloud edges are ragged
    // at every scale; two octaves reads as smooth blobs and four starts putting detail below one
    // texel, where bilinear filtering throws it away and the bake pays for it anyway.
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
    public static float Coverage(float u, float v, int seed) =>
        AuroraNoise.Fbm(u * LatticeCells, v * LatticeCells, LatticeCells, LatticeCells, seed, Octaves);

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
    public static float FillIntensity(float[] intensity, int width, int height, float cloudFraction, int seed)
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
                intensity[row + x] = Coverage((x + 0.5f) * uScale, v, seed);
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

    // Writes the RGBA32 bytes the texture wants: the tint in RGB, the residual in alpha.
    //
    // THE TINT IS BAKED INTO THE TEXTURE RATHER THAN SET AS THE MATERIAL'S COLOUR, which looks like
    // the long way round and is not. Both §11a's aurora and §24's glare set Material.color to
    // (1, 1, 1, alpha) — white — and neither has ever asked ShaderDatabase.MoteGlow to multiply a
    // COLOURED material through a texture. SheetMaterial's own header records why this codebase does
    // not guess about that shader's behaviour: it is not ours, and being wrong renders something
    // plausible rather than nothing. The aurora bakes its driver colour into its pixels for the same
    // reason, so this file follows the one path that is already known to work here.
    //
    // The cost of that choice is bounded by the split above: re-tinting walks the bytes but not the
    // noise, so a colour that moves every frame costs a memory write per texel, not a field rebake.
    public static void WriteRgba(
        byte[] rgba, float[] intensity, int count, float mean, float tintR, float tintG, float tintB)
    {
        if (rgba == null || intensity == null)
            return;

        byte r = ToByte(tintR);
        byte g = ToByte(tintG);
        byte b = ToByte(tintB);

        for (int i = 0; i < count; i++)
        {
            int o = i * 4;
            rgba[o + 0] = r;
            rgba[o + 1] = g;
            rgba[o + 2] = b;
            rgba[o + 3] = ToByte(Residual(intensity[i], mean));
        }
    }

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
