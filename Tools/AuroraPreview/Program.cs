using System;
using System.Diagnostics;
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
// FAITHFULNESS IS THE WHOLE VALUE. This links the shipped generators as source and reads their layer
// geometry (feature sizes, pan rates, layer alphas) from them, which is exactly why those constants live
// in the pure core. A previewer that restated them would drift from the thing it previews, and a tool
// you cannot trust is worse than no tool.
//
// TWO FIELDS, ONE TOOL. Issue #42 has AuroraCurtain's contour field competing against
// AuroraCurtainHemRays' authored hem-and-rays silhouette, so every output is produced for both from the
// same code at the same instant, plus a stacked side-by-side. Which one is better is a question about
// two pictures next to each other, and any difference in how they were rendered would poison it.
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

    // One drawn plane: how large the tile is on the map in each axis, how fast it pans, and its share of
    // the additive sum. Anisotropic on purpose — AuroraCurtain uses one number for both axes and
    // AuroraCurtainHemRays uses two, and the composite has to be able to express either.
    private sealed class Layer
    {
        public float CellsPerRepeatX;
        public float CellsPerRepeatY;
        public float PanU;
        public float PanV;
        public float Alpha;
    }

    // A field generator plus everything the composite needs to lay it over the map.
    private sealed class FieldSpec
    {
        public string Name;
        public int Resolution;
        public float TintWeight;
        public Action<byte[], int, int, int, int, float, float, float, float, float> Fill;
        public Layer[] Layers;
        public string SamplesPerPixelNote;
    }

    public static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "preview";
        Directory.CreateDirectory(outDir);

        // Peak strength, i.e. deep night mid-event — the state worth looking at.
        float strength = AuroraMath.MaxCurtainStrength;

        // A green palette entry, as vanilla's Aurora event would be cycling through.
        float[] tint = { 0.2f, 0.9f, 0.3f };

        FieldSpec contour = ContourSpec();
        FieldSpec hemRays = HemRaysSpec();

        byte[] contourComposite = Render(outDir, contour, tint, strength);
        byte[] hemRaysComposite = Render(outDir, hemRays, tint, strength);

        WriteStacked(Path.Combine(outDir, "sidebyside.png"), contourComposite, hemRaysComposite);
        Console.WriteLine();
        Console.WriteLine("sidebyside.png            contour on top, hem-rays below, same instant and ground");

        return 0;
    }

    // ---- The two fields under comparison -------------------------------------------------------

    private static FieldSpec ContourSpec() => new FieldSpec
    {
        Name = "contour",
        Resolution = AuroraCurtain.Resolution,
        TintWeight = AuroraCurtain.DriverTintWeight,
        Fill = AuroraCurtain.FillRows,
        SamplesPerPixelNote = "~9 2D noise samples/pixel (warp 1 + 2x2-octave ribbons 4 + envelope 2 + hue 2)",
        Layers = new[]
        {
            new Layer
            {
                CellsPerRepeatX = AuroraCurtain.Layer1CellsPerRepeat,
                CellsPerRepeatY = AuroraCurtain.Layer1CellsPerRepeat,
                PanU = AuroraCurtain.Layer1PanU,
                PanV = AuroraCurtain.Layer1PanV,
                Alpha = 1f,
            },
            new Layer
            {
                CellsPerRepeatX = AuroraCurtain.Layer2CellsPerRepeat,
                CellsPerRepeatY = AuroraCurtain.Layer2CellsPerRepeat,
                PanU = AuroraCurtain.Layer2PanU,
                PanV = AuroraCurtain.Layer2PanV,
                Alpha = AuroraCurtain.Layer2Alpha,
            },
        },
    };

    // One layer, not two. The hem-rays field already contains three stacked curtains inside a single
    // tile, so a second plane at another scale would put a second set of hems on screen at a size that
    // contradicts the first — the contour field needs two layers because one contour is not much of a
    // sky, and this one does not.
    private static FieldSpec HemRaysSpec() => new FieldSpec
    {
        Name = "hemrays",
        Resolution = AuroraCurtainHemRays.Resolution,
        TintWeight = AuroraCurtainHemRays.DriverTintWeight,
        Fill = AuroraCurtainHemRays.FillRows,
        SamplesPerPixelNote =
            "19 1D noise samples/COLUMN (3 curtains x [2-octave hem + ray + clump + length + envelope]"
            + " + hue wobble); 0 per pixel",
        Layers = new[]
        {
            new Layer
            {
                CellsPerRepeatX = AuroraCurtainHemRays.CellsPerRepeatX,
                CellsPerRepeatY = AuroraCurtainHemRays.CellsPerRepeatY,
                PanU = AuroraCurtainHemRays.PanU,
                PanV = AuroraCurtainHemRays.PanV,
                Alpha = 1f,
            },
        },
    };

    // ---- Output ---------------------------------------------------------------------------------

    private static byte[] Render(string outDir, FieldSpec spec, float[] tint, float strength)
    {
        byte[] field = BakeField(spec, 0f, tint);

        WriteFieldTexture(Path.Combine(outDir, spec.Name + "_field.png"), spec, field);
        Console.WriteLine($"{spec.Name}_field.png      {spec.Resolution}^2 baked texture, 4x nearest zoom");

        byte[] composite = Composite(spec, field, ticks: 0, strength, ViewCellsY);
        Png.Write(Path.Combine(outDir, spec.Name + "_composite.png"),
            ViewCellsX * PixelsPerCell, ViewCellsY * PixelsPerCell, composite);
        Console.WriteLine($"{spec.Name}_composite.png  {ViewCellsX}x{ViewCellsY} cells, additive over night ground");

        WriteFilmstrip(Path.Combine(outDir, spec.Name + "_motion.png"), spec, tint, strength);
        Console.WriteLine($"{spec.Name}_motion.png     6 frames, 1 in-game hour apart, to judge drift and undulation");

        ReportCoverage(spec, field);
        ReportCost(spec, tint);
        Console.WriteLine();

        return composite;
    }

    private static byte[] BakeField(FieldSpec spec, float ticks, float[] tint)
    {
        int side = spec.Resolution;
        byte[] rgba = new byte[side * side * 4];
        spec.Fill(rgba, side, side, 0, side, ticks, tint[0], tint[1], tint[2], spec.TintWeight);
        return rgba;
    }

    // The baked texture itself, nearest-neighbour zoomed, alpha shown as brightness against black. This is
    // the field with nothing else on top — the view that makes "wiry filament" vs "soft curtain"
    // immediately obvious.
    private static void WriteFieldTexture(string path, FieldSpec spec, byte[] field)
    {
        const int zoom = 4;
        int side = spec.Resolution;
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

    // Every layer composited additively over the night ground, at its real feature size and pan offset.
    // This reproduces the game's own arithmetic:
    //
    //   uv_mesh  = cell * PlaneRepeatsPerCell        (wholeMapPlane's pre-multiplied UVs)
    //   uv_tex   = uv_mesh * materialScale + offset  (Unity's scale-then-offset)
    //   pixel   += texRGB * texA * layerAlpha * strength   (MoteGlow's additive blend)
    //
    // materialScale is 1/(CellsPerRepeat * PlaneRepeatsPerCell), so uv_tex reduces to
    // cell/CellsPerRepeat + offset — one repeat per CellsPerRepeat cells, which is the whole intent.
    //
    // Rows are counted DOWNWARD from the top of the image so that +v in the texture is up on screen.
    // Irrelevant for the contour field, which has no preferred direction; load-bearing for the hem-rays
    // field, whose entire premise is that rays point up. Getting it wrong would render it upside down and
    // the comparison would be meaningless.
    private static byte[] Composite(FieldSpec spec, byte[] field, int ticks, float strength, int cellsY)
    {
        int width = ViewCellsX * PixelsPerCell;
        int height = cellsY * PixelsPerCell;
        byte[] image = new byte[width * height * 4];

        for (int py = 0; py < height; py++)
        {
            float cellY = (float)(height - 1 - py) / PixelsPerCell;

            for (int px = 0; px < width; px++)
            {
                float cellX = (float)px / PixelsPerCell;

                float r = NightGround[0];
                float g = NightGround[1];
                float b = NightGround[2];

                for (int l = 0; l < spec.Layers.Length; l++)
                {
                    Layer layer = spec.Layers[l];
                    float u = cellX / layer.CellsPerRepeatX + ticks * layer.PanU % 1f;
                    float v = cellY / layer.CellsPerRepeatY + ticks * layer.PanV % 1f;
                    Add(spec, field, u, v, strength * layer.Alpha, ref r, ref g, ref b);
                }

                int dst = (py * width + px) * 4;
                image[dst] = ToByte(r);
                image[dst + 1] = ToByte(g);
                image[dst + 2] = ToByte(b);
                image[dst + 3] = 255;
            }
        }

        return image;
    }

    // Six composites an in-game hour apart, stacked vertically with a divider. A still cannot show drift,
    // and this is the cheap substitute for the harness's timelapse video: if consecutive strips look
    // identical the motion is too slow, and if they share no structure it is too fast.
    private static void WriteFilmstrip(string path, FieldSpec spec, float[] tint, float strength)
    {
        const int frames = 6;
        const int ticksPerHour = 2500;
        const int stripCells = 30;
        int stripHeight = stripCells * PixelsPerCell;
        int width = ViewCellsX * PixelsPerCell;
        int height = frames * (stripHeight + 2);
        byte[] image = new byte[width * height * 4];

        for (int f = 0; f < frames; f++)
        {
            int ticks = f * ticksPerHour;
            byte[] field = BakeField(spec, ticks, tint);
            byte[] strip = Composite(spec, field, ticks, strength, stripCells);

            Array.Copy(strip, 0, image, f * (stripHeight + 2) * width * 4, strip.Length);
        }

        Png.Write(path, width, height, image);
    }

    // The two composites one above the other with a divider, which is the only view that actually answers
    // "which of these looks more like an aurora".
    private static void WriteStacked(string path, byte[] top, byte[] bottom)
    {
        int width = ViewCellsX * PixelsPerCell;
        int panel = ViewCellsY * PixelsPerCell;
        int height = panel * 2 + 3;
        byte[] image = new byte[width * height * 4];

        Array.Copy(top, 0, image, 0, top.Length);
        Array.Copy(bottom, 0, image, (panel + 3) * width * 4, bottom.Length);

        Png.Write(path, width, height, image);
    }

    // Bilinear sample of the tileable field, then MoteGlow's additive contribution. Bilinear rather than
    // nearest because the material is set to FilterMode.Bilinear — sampling differently here would make
    // the preview crisper than the game and hide exactly the softness being tuned.
    private static void Add(FieldSpec spec, byte[] field, float u, float v, float weight,
        ref float r, ref float g, ref float b)
    {
        int side = spec.Resolution;

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
    private static void ReportCoverage(FieldSpec spec, byte[] field)
    {
        int side = spec.Resolution;
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
        Console.WriteLine($"  coverage: bright(>0.4) {100.0 * bright / total:F1}%   " +
                          $"dark(<0.05) {100.0 * dim / total:F1}%   " +
                          $"mean alpha {alphaSum / (double)total / 255.0:F3}");
        Console.WriteLine("            a curtain wants a low bright% with plenty of genuinely dark sky between bands");
    }

    // Wall-clock cost of baking one whole tile, and of baking it the way the adapter actually does (a
    // six-row slice at a time, rebuilding whatever per-call state the field needs). The second number is
    // the one that matters for the hem-rays field, whose saving comes from amortising work across a
    // column and is therefore diluted by slicing.
    //
    // This is a .NET 8 JIT number on a desktop CPU. The live harness measured Mono at ~6x slower for the
    // contour field, so multiply accordingly before comparing against a frame budget — the RATIO between
    // the two fields here is the trustworthy part, not the absolute.
    private static void ReportCost(FieldSpec spec, float[] tint)
    {
        const int rowsPerUpdate = 6;
        int side = spec.Resolution;
        byte[] rgba = new byte[side * side * 4];

        double whole = TimeBakes(spec, rgba, side, tint, side);
        double slice = TimeBakes(spec, rgba, side, tint, rowsPerUpdate);

        Console.WriteLine($"  cost:     whole tile {whole:F0} us" +
                          $"   per frame at {rowsPerUpdate} rows/update {slice:F0} us" +
                          $"   ({side / rowsPerUpdate} frames per full refresh)   (.NET 8, x1)");
        Console.WriteLine($"  samples:  {spec.SamplesPerPixelNote}");
    }

    // Times one bake of `rows` rows, averaged, after a warm-up pass so the JIT is not being measured.
    private static double TimeBakes(FieldSpec spec, byte[] rgba, int side, float[] tint, int rows)
    {
        const int reps = 200;

        spec.Fill(rgba, side, side, 0, rows, 0f, tint[0], tint[1], tint[2], spec.TintWeight);

        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < reps; i++)
            spec.Fill(rgba, side, side, 0, rows, i, tint[0], tint[1], tint[2], spec.TintWeight);
        sw.Stop();

        return sw.Elapsed.TotalMilliseconds * 1000.0 / reps;
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
