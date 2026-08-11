using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types. §26's texel bake: turns TwilightSweepMath's
// one-dimensional band into the RGBA bytes the overlay uploads each frame.
//
// WHY A TEXTURE RATHER THAN VERTEX COLOURS. A whole-map quad has four vertices, and four vertex
// colours interpolate to a LINEAR ramp along any axis — which would be enough for a gradient but not
// for a BAND with a soft trailing edge and a peak in the middle, which is what §26 actually draws.
// The alternative is a strip of subdivided quads, i.e. building geometry to express a 1-D function;
// §23b already established that this pass's shader (MoteGlow) is known to honour a texture and has
// never been asked to honour a vertex colour, so the texture is both the cheaper and the
// better-evidenced route.
//
// WHY THE WHOLE 2-D FIELD IS BAKED FOR WHAT IS A 1-D FUNCTION — this looks wasteful and is the
// deliberate choice. The band runs along the SOLAR AZIMUTH, which is an arbitrary angle that moves
// through the evening. A material's texture transform can translate, scale and mirror, but it CANNOT
// ROTATE (the same limitation CloudSheetLayout.FlipU exists to work around), so a 1-D ramp panned by
// UV could only ever run along U or V. Baking the projection into a 2-D field is what buys a
// continuously-rotating axis.
//
// AND THAT IS PRECISELY WHY §26 DODGES ISSUE #139. §23b's gradient is stuck at 22.5-degree axis
// steps with an unpinned phase because its texture TILES and PANS, so anything baked into it must be
// periodic over the tile or a seam sweeps the colony. §26's texture tiles nothing — one quad, one
// map, clamped — so the axis can be the true azimuth, continuously, with no rounding and no seam.
// The cost of that freedom is this bake.
//
// THE BAKE IS PER FRAME, AND THAT IS AFFORDABLE FOR ONE REASON: the window. §26 can only draw during
// the few minutes a day the sun spends between the horizon and §8's fade floor. 64x64 is 4,096 texels
// of a handful of flops and four byte writes, plus a 16 KB upload — a rounding error on a frame, and
// one paid on well under 1% of the frames in a game year. A subsystem that drew all day would have to
// split its cadence the way §23b's does; this one does not.
public static class TwilightSweepField
{
    // Texels per axis. Matches CloudField.Resolution, and for the same reason: the field being drawn
    // is smooth by construction — a band with a soft edge, nothing finer — so resolution controls how
    // finely a smooth thing is sampled, not how much detail it has. Bilinear filtering covers the
    // rest.
    public const int Resolution = 64;

    // Projects a texel onto the sun axis, returning [0, 1] where 0 is the ANTI-SOLAR corner and 1 the
    // SUNWARD one.
    //
    // NORMALISED BY (|axisU| + |axisV|), which is the extent of the unit square projected onto that
    // direction. Without it a diagonal axis would run out of range before reaching the far corner and
    // the band would finish its crossing early — visible as the sweep completing while a wedge of map
    // is still lit, and only on diagonal azimuths, i.e. exactly the kind of bug that survives a test
    // written on a north-south sun.
    public static float AxisPosition(float u, float v, float axisU, float axisV) =>
        Clamp01(RawAxisPosition(u, v, axisU, axisV));

    // The same projection without the clamp. WriteRgba walks it INCREMENTALLY — the projection is
    // linear in u and v, so the whole field is two additions per texel — and an incremented value
    // that had been clamped at each step would stick at the map edge instead of carrying on.
    private static float RawAxisPosition(float u, float v, float axisU, float axisV)
    {
        float extent = MathF.Abs(axisU) + MathF.Abs(axisV);
        if (extent < 1e-6f)
            return 0.5f;

        return 0.5f + ((((u - 0.5f) * axisU) + ((v - 0.5f) * axisV)) / extent);
    }

    // The sunward unit vector in UV space for a solar azimuth in degrees.
    //
    // Same convention as CloudField.GradientAxis — u follows +x (east), v follows +z (north), azimuth
    // measured clockwise from north — but WITHOUT its rounding to the eight lattice directions. That
    // rounding exists there only because the tiled field must stay periodic; see this file's header.
    public static void SunwardAxis(float azimuthDegrees, out float axisU, out float axisV)
    {
        float radians = azimuthDegrees * (MathF.PI / 180f);
        axisU = MathF.Sin(radians);
        axisV = MathF.Cos(radians);

        // A NaN azimuth would leave both components NaN, and a NaN axis bakes a field of zero bytes —
        // i.e. the feature silently absent rather than obviously wrong. Falling back to due north
        // keeps the band on screen where it can be seen to be wrong.
        if (float.IsNaN(axisU) || float.IsNaN(axisV))
        {
            axisU = 0f;
            axisV = 1f;
        }
    }

    // Fills `rgba` (width * height * 4 bytes, row-major, RGBA32) with this frame's band.
    //
    // COLOUR AND ALPHA COME FROM THE SAME AXIS POSITION, which is what keeps the pink edge welded to
    // the shadow it rides on. The two ends are borrowed rather than invented — §8's reddened sunward
    // tint and §19c's composed twilight hue — for the reason CloudLayers.CoolTintFor sets out at
    // length: a private colour target here would be a second opinion about what twilight looks like,
    // and this codebase has one answer per question by policy.
    public static void WriteRgba(
        byte[] rgba, int width, int height,
        float axisU, float axisV, float sweep, float amplitude,
        float hotR, float hotG, float hotB, float coolR, float coolG, float coolB)
    {
        if (rgba == null || width <= 0 || height <= 0)
            return;

        if (rgba.Length < width * height * 4)
            return;

        // THE FIELD IS ONE-DIMENSIONAL, SO THE MATHS IS DONE ONCE PER STEP OF THE AXIS AND NOT ONCE
        // PER TEXEL. Every texel's colour and alpha depend on nothing but its projection onto the sun
        // axis, so the band is baked into a small lookup table first and the texel loop degrades to
        // an index and a four-byte copy.
        //
        // MEASURED, because the first cut did it the obvious way and the number was not a rounding
        // error: evaluating Intensity and Warmth per texel cost 91 us per bake, against a whole-mod
        // budget of ~0.46 ms/frame (DESIGN.md §3's profile history). Paid only inside the twilight
        // window, but "only during sunset" is exactly when a player is watching, so it is the worst
        // available moment to spend a fifth of the mod on one quad. The table costs 256 evaluations
        // instead of 4,096 and the loop below is memory-bound after that; SweepPreview reports the
        // current figure, and the PR quotes it.
        BuildBandTable(sweep, amplitude, hotR, hotG, hotB, coolR, coolG, coolB);

        // The projection is linear in u and v, so it can be walked with one addition per texel rather
        // than recomputed. Deliberately derived from RawAxisPosition at the origin rather than from a
        // second copy of the formula, so the incremental walk and the reference projection cannot
        // drift apart — a drift here would tilt the band slightly off the sun with nothing to catch it.
        float extent = MathF.Abs(axisU) + MathF.Abs(axisV);
        if (extent < 1e-6f)
            extent = 1f;

        float stepU = axisU / (extent * width);
        float stepV = axisV / (extent * height);
        float rowStart = RawAxisPosition(0.5f / width, 0.5f / height, axisU, axisV);

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            float p = rowStart;

            for (int x = 0; x < width; x++)
            {
                int entry = TableIndex(p) * 4;
                int o = (row + x) * 4;

                rgba[o + 0] = Table[entry + 0];
                rgba[o + 1] = Table[entry + 1];
                rgba[o + 2] = Table[entry + 2];
                rgba[o + 3] = Table[entry + 3];

                p += stepU;
            }

            rowStart += stepV;
        }
    }

    // How finely the band is sampled along the axis. 256 over a 64-texel span is four table entries
    // per texel, so the table is never the thing quantising the edge — the texture resolution and the
    // GPU's bilinear filter are, exactly as they would be without it.
    private const int TableSize = 256;

    // Static and reused. The overlay bakes on the draw path inside Map.MapUpdate, so a per-frame
    // allocation here would be garbage generated during precisely the minutes the player is watching
    // the screen — the same reasoning TwilightSweepOverlay's own buffer records.
    //
    // NOT THREAD SAFE, and that is fine for the reason it is fine for every other draw-path buffer in
    // this mod: Unity calls MapUpdate on the main thread only. Worth stating rather than assuming,
    // because a static mutable buffer is the kind of thing a future parallel bake would quietly break.
    private static readonly byte[] Table = new byte[TableSize * 4];

    private static void BuildBandTable(
        float sweep, float amplitude,
        float hotR, float hotG, float hotB, float coolR, float coolG, float coolB)
    {
        for (int i = 0; i < TableSize; i++)
        {
            float p = i / (TableSize - 1f);

            float warmth = TwilightSweepMath.Warmth(p, sweep);
            float alpha = TwilightSweepMath.Intensity(p, sweep, amplitude);

            int o = i * 4;
            Table[o + 0] = ToByte(Mix(coolR, hotR, warmth));
            Table[o + 1] = ToByte(Mix(coolG, hotG, warmth));
            Table[o + 2] = ToByte(Mix(coolB, hotB, warmth));
            Table[o + 3] = ToByte(alpha);
        }
    }

    // Clamps here rather than in the walk, which is what lets the projection be incremented: a texel
    // whose projection runs past either end of the axis takes the end entry, which is the same answer
    // AxisPosition's own clamp gives.
    private static int TableIndex(float p)
    {
        int index = (int)((p * (TableSize - 1)) + 0.5f);
        if (index < 0)
            return 0;

        return index >= TableSize ? TableSize - 1 : index;
    }

    private static byte ToByte(float value) => (byte)(Clamp01(value) * 255f + 0.5f);

    private static float Mix(float a, float b, float t) => a + (b - a) * t;

    private static float Clamp01(float value)
    {
        if (value < 0f)
            return 0f;

        return value > 1f ? 1f : value;
    }
}
