using System;
using System.IO;
using CelestialLighting;

namespace CelestialLighting.Tools;

// Renders §11a's aurora field to PNGs, offline, in about a second.
//
// WHY THIS EXISTS. The field generator is pure, so the only thing a RimWorld boot adds to a look-and-feel
// question is three minutes of latency and a shared-DLL race. Tuning Smoothness or the feature size
// through the live harness meant one blind guess per boot. This closes the loop to roughly a second, and
// it caught two real defects the offline unit tests could not: the field being tiled ~25 times across the
// map (wholeMapPlane's UVs are pre-multiplied by 200), and the ribbons rendering as thin wiry filaments
// rather than curtains. Both were obvious on sight and invisible to an assertion.
//
// It is NOT a replacement for the live run. It cannot show compositing against real terrain, the
// interaction with §7a's night darkening, or the Mono frame cost. What it does is make the live run a
// confirmation rather than an exploration.
//
// FAITHFULNESS IS THE WHOLE VALUE. This links the shipped AuroraCurtain/AuroraNoise as source and reads
// the shipped layer geometry (feature sizes, pan rates, second-layer alpha) from AuroraCurtain, which is
// exactly why those constants live in the pure core. A previewer that restated them would drift from the
// thing it previews, and a tool you cannot trust is worse than no tool.
public static class Program
{
    // A plausible dimmed night ground colour, sampled from the live harness's own aurora_curtain_off.png.
    // The point of compositing over something rather than over black: this effect is ADDITIVE, so its
    // apparent strength depends on what is underneath, and judging it against black flatters it.
    private static readonly float[] NightGround = { 0.115f, 0.098f, 0.090f };

    // Roughly what the harness screenshots frame, in cells, so the previews are directly comparable to
    // the live PNGs rather than to some other zoom level.
    private const int ViewCellsX = 120;
    private const int ViewCellsY = 68;
    private const int PixelsPerCell = 8;

    public static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "preview";
        Directory.CreateDirectory(outDir);

        // Peak strength, i.e. deep night mid-event — the state worth looking at.
        float strength = AuroraMath.MaxCurtainStrength;

        // A green palette entry, as vanilla's Aurora event would be cycling through.
        float[] tint = { 0.2f, 0.9f, 0.3f };

        byte[] field = BakeField(0f, tint);
        WriteFieldTexture(Path.Combine(outDir, "field.png"), field);
        Console.WriteLine($"field.png          {AuroraCurtain.Resolution}^2 baked texture, 4x nearest zoom");

        WriteComposite(Path.Combine(outDir, "composite.png"), field, ticks: 0, strength);
        Console.WriteLine($"composite.png      {ViewCellsX}x{ViewCellsY} cells, both layers additive over night ground");

        WriteFilmstrip(Path.Combine(outDir, "motion.png"), tint, strength);
        Console.WriteLine("motion.png         6 frames, 1 in-game hour apart, to judge drift and undulation");

        ReportCoverage(field);
        return 0;
    }

    private static byte[] BakeField(float ticks, float[] tint)
    {
        int side = AuroraCurtain.Resolution;
        byte[] rgba = new byte[side * side * 4];
        AuroraCurtain.FillRows(
            rgba, side, side, 0, side, ticks,
            tint[0], tint[1], tint[2], AuroraCurtain.DriverTintWeight);
        return rgba;
    }

    // The baked texture itself, nearest-neighbour zoomed, alpha shown as brightness against black. This is
    // the ribbon field with nothing else on top — the view that makes "wiry filament" vs "soft curtain"
    // immediately obvious.
    private static void WriteFieldTexture(string path, byte[] field)
    {
        const int zoom = 4;
        int side = AuroraCurtain.Resolution;
        int outSide = side * zoom;
        byte[] image = new byte[outSide * outSide * 4];

        for (int y = 0; y < outSide; y++)
        {
            for (int x = 0; x < outSide; x++)
            {
                int src = ((y / zoom) * side + x / zoom) * 4;
                float a = field[src + 3] / 255f;

                int dst = (y * outSide + x) * 4;
                image[dst] = (byte)(field[src] * a);
                image[dst + 1] = (byte)(field[src + 1] * a);
                image[dst + 2] = (byte)(field[src + 2] * a);
                image[dst + 3] = 255;
            }
        }

        Png.Write(path, outSide, outSide, image);
    }

    // Both layers composited additively over the night ground, at their real feature sizes and pan
    // offsets. This reproduces the game's own arithmetic:
    //
    //   uv_mesh  = cell * PlaneRepeatsPerCell        (wholeMapPlane's pre-multiplied UVs)
    //   uv_tex   = uv_mesh * materialScale + offset  (Unity's scale-then-offset)
    //   pixel   += texRGB * texA * layerAlpha * strength   (MoteGlow's additive blend)
    //
    // materialScale is 1/(CellsPerRepeat * PlaneRepeatsPerCell), so uv_tex reduces to
    // cell/CellsPerRepeat + offset — one repeat per CellsPerRepeat cells, which is the whole intent.
    private static void WriteComposite(string path, byte[] field, int ticks, float strength)
    {
        int width = ViewCellsX * PixelsPerCell;
        int height = ViewCellsY * PixelsPerCell;
        byte[] image = new byte[width * height * 4];

        float pan1U = ticks * AuroraCurtain.Layer1PanU % 1f;
        float pan1V = ticks * AuroraCurtain.Layer1PanV % 1f;
        float pan2U = ticks * AuroraCurtain.Layer2PanU % 1f;
        float pan2V = ticks * AuroraCurtain.Layer2PanV % 1f;

        for (int py = 0; py < height; py++)
        {
            float cellY = (float)py / PixelsPerCell;

            for (int px = 0; px < width; px++)
            {
                float cellX = (float)px / PixelsPerCell;

                float r = NightGround[0];
                float g = NightGround[1];
                float b = NightGround[2];

                Add(field, cellX / AuroraCurtain.Layer1CellsPerRepeat + pan1U,
                    cellY / AuroraCurtain.Layer1CellsPerRepeat + pan1V, strength, ref r, ref g, ref b);

                Add(field, cellX / AuroraCurtain.Layer2CellsPerRepeat + pan2U,
                    cellY / AuroraCurtain.Layer2CellsPerRepeat + pan2V,
                    strength * AuroraCurtain.Layer2Alpha, ref r, ref g, ref b);

                int dst = (py * width + px) * 4;
                image[dst] = ToByte(r);
                image[dst + 1] = ToByte(g);
                image[dst + 2] = ToByte(b);
                image[dst + 3] = 255;
            }
        }

        Png.Write(path, width, height, image);
    }

    // Six composites an in-game hour apart, stacked vertically with a divider. A still cannot show drift,
    // and this is the cheap substitute for the harness's timelapse video: if consecutive strips look
    // identical the motion is too slow, and if they share no structure it is too fast.
    private static void WriteFilmstrip(string path, float[] tint, float strength)
    {
        const int frames = 6;
        const int ticksPerHour = 2500;
        int stripHeight = 26 * PixelsPerCell;
        int width = ViewCellsX * PixelsPerCell;
        int height = frames * (stripHeight + 2);
        byte[] image = new byte[width * height * 4];

        for (int f = 0; f < frames; f++)
        {
            int ticks = f * ticksPerHour;
            byte[] field = BakeField(ticks, tint);

            float pan1U = ticks * AuroraCurtain.Layer1PanU % 1f;
            float pan1V = ticks * AuroraCurtain.Layer1PanV % 1f;
            float pan2U = ticks * AuroraCurtain.Layer2PanU % 1f;
            float pan2V = ticks * AuroraCurtain.Layer2PanV % 1f;

            for (int y = 0; y < stripHeight; y++)
            {
                float cellY = (float)y / PixelsPerCell;

                for (int px = 0; px < width; px++)
                {
                    float cellX = (float)px / PixelsPerCell;

                    float r = NightGround[0];
                    float g = NightGround[1];
                    float b = NightGround[2];

                    Add(field, cellX / AuroraCurtain.Layer1CellsPerRepeat + pan1U,
                        cellY / AuroraCurtain.Layer1CellsPerRepeat + pan1V, strength, ref r, ref g, ref b);

                    Add(field, cellX / AuroraCurtain.Layer2CellsPerRepeat + pan2U,
                        cellY / AuroraCurtain.Layer2CellsPerRepeat + pan2V,
                        strength * AuroraCurtain.Layer2Alpha, ref r, ref g, ref b);

                    int dst = ((f * (stripHeight + 2) + y) * width + px) * 4;
                    image[dst] = ToByte(r);
                    image[dst + 1] = ToByte(g);
                    image[dst + 2] = ToByte(b);
                    image[dst + 3] = 255;
                }
            }
        }

        Png.Write(path, width, height, image);
    }

    // Bilinear sample of the tileable field, then MoteGlow's additive contribution. Bilinear rather than
    // nearest because the material is set to FilterMode.Bilinear — sampling differently here would make
    // the preview crisper than the game and hide exactly the softness being tuned.
    private static void Add(byte[] field, float u, float v, float weight,
        ref float r, ref float g, ref float b)
    {
        int side = AuroraCurtain.Resolution;

        float fx = Wrap01(u) * side - 0.5f;
        float fy = Wrap01(v) * side - 0.5f;

        int x0 = (int)Math.Floor(fx);
        int y0 = (int)Math.Floor(fy);
        float tx = fx - x0;
        float ty = fy - y0;

        int x1 = WrapIndex(x0 + 1, side);
        int y1 = WrapIndex(y0 + 1, side);
        x0 = WrapIndex(x0, side);
        y0 = WrapIndex(y0, side);

        Sample(field, side, x0, y0, out float r00, out float g00, out float b00, out float a00);
        Sample(field, side, x1, y0, out float r10, out float g10, out float b10, out float a10);
        Sample(field, side, x0, y1, out float r01, out float g01, out float b01, out float a01);
        Sample(field, side, x1, y1, out float r11, out float g11, out float b11, out float a11);

        float sr = Lerp(Lerp(r00, r10, tx), Lerp(r01, r11, tx), ty);
        float sg = Lerp(Lerp(g00, g10, tx), Lerp(g01, g11, tx), ty);
        float sb = Lerp(Lerp(b00, b10, tx), Lerp(b01, b11, tx), ty);
        float sa = Lerp(Lerp(a00, a10, tx), Lerp(a01, a11, tx), ty);

        float k = sa * weight;
        r += sr * k;
        g += sg * k;
        b += sb * k;
    }

    private static void Sample(byte[] field, int side, int x, int y,
        out float r, out float g, out float b, out float a)
    {
        int i = (y * side + x) * 4;
        r = field[i] / 255f;
        g = field[i + 1] / 255f;
        b = field[i + 2] / 255f;
        a = field[i + 3] / 255f;
    }

    // Numbers alongside the pictures, because the eye is unreliable about coverage and the repo rule is
    // to measure rather than eyeball. These are the same properties AuroraCurtainTests asserts; printing
    // them here says how close to the edge of those assertions the current tuning actually sits.
    private static void ReportCoverage(byte[] field)
    {
        int side = AuroraCurtain.Resolution;
        int bright = 0;
        int dim = 0;
        long alphaSum = 0;

        for (int i = 0; i < side * side; i++)
        {
            int a = field[i * 4 + 3];
            alphaSum += a;
            if (a > 102)
                bright++;
            if (a < 13)
                dim++;
        }

        int total = side * side;
        Console.WriteLine();
        Console.WriteLine($"coverage: bright(>0.4) {100.0 * bright / total:F1}%   " +
                          $"dark(<0.05) {100.0 * dim / total:F1}%   " +
                          $"mean alpha {alphaSum / (double)total / 255.0:F3}");
        Console.WriteLine("  a curtain wants a low bright% with plenty of genuinely dark sky between bands;");
        Console.WriteLine("  bright% climbing toward 50 means it is becoming the flat wash 11a replaced.");
    }

    private static float Wrap01(float v)
    {
        float f = v % 1f;
        return f < 0f ? f + 1f : f;
    }

    private static int WrapIndex(int v, int period)
    {
        int m = v % period;
        return m < 0 ? m + period : m;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static byte ToByte(float v)
    {
        float c = v < 0f ? 0f : (v > 1f ? 1f : v);
        return (byte)(c * 255f + 0.5f);
    }
}
