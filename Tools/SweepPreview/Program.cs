using System;
using System.Diagnostics;
using System.IO;

namespace CelestialLighting.Tools;

// Offline preview for §26's twilight sweep (DESIGN.md §26, issue #140). Renders the shipped field —
// linked as source, never reimplemented — across the whole twilight window, so the band's shape, its
// edge softness and the belt/glow balance can be judged in about a second instead of a RimWorld boot.
//
// WHY §26 NEEDS THIS MORE THAN ITS NEIGHBOURS DID. CloudPreview answers "what does this field look
// like", a question about one still. §26's subject is MOTION, and the honest failure mode issue #140
// names — "a boundary creeping over the colony reads as a rendering artifact rather than as dusk" —
// is about the sequence, not any one frame. A live run shows one evening at whatever hours the
// scenario picked; this shows every step of the window at once, which is the only cheap way to see
// whether the boundary moves at a sane speed and whether the belt stays welded to it.
//
// WHAT IT DELIBERATELY CANNOT ANSWER. It composites over a flat grey, so it says nothing about how
// the band reads over an actual colony, at an actual zoom, against §8's own tint and §9's
// desaturation — which is the question the feature lives or dies on. The live A/B is not optional
// because of this tool; this tool exists so the live A/B is spent on the question only it can answer.
public static class Program
{
    // The frames of the window to render. 13 steps from the horizon to §8's fade floor: enough to see
    // the boundary move without the strip becoming unreadable.
    private const int Steps = 13;

    private const int FrameSize = 128;

    // A dim evening colony floor to composite onto. Not black: an additive pass over black shows its
    // own colour at full saturation, which flatters it enormously and is nothing like the case that
    // matters. Not mid-grey either — by the time §26 draws, the sun is down and §8 has already taken
    // the ground most of the way to dark.
    private static readonly float[] Ground = { 0.20f, 0.19f, 0.21f };

    // Roughly WSW, i.e. an ordinary sunset bearing, and deliberately NOT axis-aligned: a diagonal sun
    // is the case TwilightSweepField.AxisPosition's (|u| + |v|) normalisation exists for, so previewing
    // on a cardinal bearing would hide the exact bug that normalisation prevents.
    private const float SunAzimuthDegrees = 252f;

    public static void Main(string[] args)
    {
        string outputDir = args.Length > 0 ? args[0] : "sweep_preview";
        Directory.CreateDirectory(outputDir);

        TwilightSweepField.SunwardAxis(SunAzimuthDegrees, out float axisU, out float axisV);
        Console.WriteLine(
            $"sun azimuth {SunAzimuthDegrees:0.#} deg -> axis ({axisU:0.000}, {axisV:0.000})");
        Console.WriteLine();

        WriteWindowStrip(outputDir, axisU, axisV);
        WriteAmplitudeLadder(outputDir, axisU, axisV);
        WriteDeckLag(outputDir, axisU, axisV);
        ReportBakeCost();
    }

    // The window, frame by frame: what a whole evening looks like, laid out left to right.
    private static void WriteWindowStrip(string outputDir, float axisU, float axisV)
    {
        // The two endpoint colours are printed alongside the geometry because "the band looks grey"
        // has two completely different causes — a weak alpha, or two endpoints that are nearly the
        // same colour — and only the numbers separate them.
        Console.WriteLine("elevation  sweep   envelope  alpha   hot RGB              cool RGB");

        byte[] strip = new byte[Steps * FrameSize * FrameSize * 4];
        string frameDir = Path.Combine(outputDir, "frames");
        Directory.CreateDirectory(frameDir);

        for (int step = 0; step < Steps; step++)
        {
            // Inclusive of both ends so the strip shows the envelope going to zero at each, which is
            // the property that makes a step at either boundary impossible.
            float elevation = TwilightSweepMath.SweepFloorDegrees * step / (Steps - 1f);

            float sweep = TwilightSweepMath.SweepPosition(elevation, inVacuum: false);
            float envelope = TwilightSweepMath.WindowEnvelope(sweep);
            float alpha = envelope * TwilightSweepMath.SweepAmplitude;

            SkyColorTemperature.Rgb hot = HotTint(elevation);
            SkyColorTemperature.Rgb cool = CoolTint(elevation);

            Console.WriteLine(
                $"{elevation,8:0.00}  {sweep,6:0.000}  {envelope,8:0.000}  {alpha,6:0.000}  " +
                $"({hot.R:0.00},{hot.G:0.00},{hot.B:0.00})     ({cool.R:0.00},{cool.G:0.00},{cool.B:0.00})");

            byte[] frame = RenderFrame(axisU, axisV, sweep, alpha, elevation);
            Png.Write(Path.Combine(frameDir, $"sweep_{step:00}.png"), FrameSize, FrameSize, frame);
            BlitColumn(strip, frame, step);
        }

        Png.Write(Path.Combine(outputDir, "sweep_window.png"), Steps * FrameSize, FrameSize, strip);
        Console.WriteLine();
        Console.WriteLine($"wrote sweep_window.png ({Steps} frames) and frames/sweep_NN.png");
        Console.WriteLine();
    }

    // The same instant at several strengths, which exists to separate two questions the shipped
    // strength cannot answer at once.
    //
    // At the calibrated amplitude the band is deliberately restrained — the repo's own history is that
    // ΔE ~9 read as distracting and the target is 3-6 — and at that strength it is genuinely hard to
    // tell "the shape is right and the effect is quiet" from "the shape is wrong and I cannot see it".
    // The overdriven columns answer the first question so the live A/B only has to answer the second.
    // NONE OF THESE EXCEPT THE 1.0x COLUMN IS A SHIPPING VALUE, and the multiplier is printed on the
    // console rather than baked into a filename so nobody quotes one of them as a measurement.
    private static void WriteAmplitudeLadder(string outputDir, float axisU, float axisV)
    {
        // Mid-window, where the envelope is at its peak and the band is fully on the map.
        const float elevation = -3f;
        float[] multipliers = { 1f, 2f, 4f, 8f };

        float sweep = TwilightSweepMath.SweepPosition(elevation, inVacuum: false);
        float baseAlpha = TwilightSweepMath.WindowEnvelope(sweep) * TwilightSweepMath.SweepAmplitude;

        byte[] strip = new byte[multipliers.Length * FrameSize * FrameSize * 4];

        Console.WriteLine($"amplitude ladder at elevation {elevation:0.0} deg (shipped = 1.0x)");
        Console.WriteLine("multiplier  alpha");

        for (int i = 0; i < multipliers.Length; i++)
        {
            Console.WriteLine($"{multipliers[i],9:0.0}x  {baseAlpha * multipliers[i],6:0.000}");
            BlitColumn(
                strip, RenderFrame(axisU, axisV, sweep, baseAlpha * multipliers[i], elevation), i);
        }

        Png.Write(
            Path.Combine(outputDir, "sweep_amplitude.png"),
            multipliers.Length * FrameSize, FrameSize, strip);
        Console.WriteLine();
        Console.WriteLine("wrote sweep_amplitude.png");
        Console.WriteLine();
    }

    // The depth half of issue #140: the ground's boundary against three decks' boundaries at one
    // instant. A high deck is still catching light where the ground has already gone out, and the gap
    // between the two edges is what reads as parallax rather than as a flat wash.
    private static void WriteDeckLag(string outputDir, float axisU, float axisV)
    {
        // Mid-window, where the envelope is near its peak and both boundaries are comfortably on the
        // map — at either end of the window one of them is off the edge and the comparison shows
        // nothing.
        const float elevation = -3f;

        (string name, float metres)[] decks =
        {
            ("ground", 0f),
            ("stratus_1km", 1000f),
            ("altocumulus_4km", 4000f),
            ("cirrus_10km", 10000f),
        };

        byte[] strip = new byte[decks.Length * FrameSize * FrameSize * 4];

        Console.WriteLine($"deck lag at elevation {elevation:0.0} deg");
        Console.WriteLine("deck              entry(deg)  sweep");

        for (int i = 0; i < decks.Length; i++)
        {
            float entry = CloudUnderlightMath.ShadowEntryDepressionDegrees(decks[i].metres);
            float sweep = TwilightSweepMath.DeckSweepPosition(elevation, entry, inVacuum: false);

            // The GROUND's envelope throughout, deliberately. The deck's boundary lags, but the
            // evening is as far along as it is — scaling each row by its own envelope would make the
            // high deck look dimmer as well as later, conflating two different claims.
            float alpha = TwilightSweepMath.WindowEnvelope(
                TwilightSweepMath.SweepPosition(elevation, inVacuum: false))
                * TwilightSweepMath.SweepAmplitude;

            Console.WriteLine($"{decks[i].name,-16}  {entry,10:0.000}  {sweep,5:0.000}");

            BlitColumn(strip, RenderFrame(axisU, axisV, sweep, alpha, elevation), i);
        }

        Png.Write(
            Path.Combine(outputDir, "sweep_deck_lag.png"), decks.Length * FrameSize, FrameSize, strip);
        Console.WriteLine();
        Console.WriteLine("wrote sweep_deck_lag.png");
        Console.WriteLine();
    }

    // One frame: bake the shipped field, then composite it additively over the evening ground exactly
    // as ShaderDatabase.MoteGlow does in game.
    private static byte[] RenderFrame(
        float axisU, float axisV, float sweep, float alpha, float elevation)
    {
        int n = TwilightSweepField.Resolution;
        byte[] field = new byte[n * n * 4];

        SkyColorTemperature.Rgb hot = HotTint(elevation);
        SkyColorTemperature.Rgb cool = CoolTint(elevation);

        TwilightSweepField.WriteRgba(
            field, n, n, axisU, axisV, sweep, alpha,
            hot.R, hot.G, hot.B, cool.R, cool.G, cool.B);

        byte[] frame = new byte[FrameSize * FrameSize * 4];
        for (int y = 0; y < FrameSize; y++)
        {
            for (int x = 0; x < FrameSize; x++)
            {
                // Nearest-neighbour upscale from the baked 64x64. The game gets bilinear filtering
                // from the GPU, so this preview shows the field slightly harder-edged than it renders
                // — the safe direction to be wrong in when the question is whether an edge reads as a
                // seam.
                int sx = x * n / FrameSize;
                int sy = y * n / FrameSize;
                int src = ((sy * n) + sx) * 4;
                int dst = ((y * FrameSize) + x) * 4;

                float a = field[src + 3] / 255f;
                frame[dst + 0] = Add(Ground[0], field[src + 0] / 255f, a);
                frame[dst + 1] = Add(Ground[1], field[src + 1] / 255f, a);
                frame[dst + 2] = Add(Ground[2], field[src + 2] / 255f, a);
                frame[dst + 3] = 255;
            }
        }

        return frame;
    }

    // What the bake actually costs, because §26 rebakes every frame it draws — the one lane here with
    // no slow half to cache — and "is that affordable" should be a measured number in the PR rather
    // than an assurance.
    private static void ReportBakeCost()
    {
        int n = TwilightSweepField.Resolution;
        byte[] field = new byte[n * n * 4];

        TwilightSweepField.SunwardAxis(SunAzimuthDegrees, out float axisU, out float axisV);

        // Warm the JIT before timing, or the first call's compilation dominates the measurement.
        for (int i = 0; i < 100; i++)
            TwilightSweepField.WriteRgba(field, n, n, axisU, axisV, 0.5f, 0.13f, 1f, .6f, .3f, .4f, .2f, .6f);

        const int Iterations = 10000;
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < Iterations; i++)
            TwilightSweepField.WriteRgba(field, n, n, axisU, axisV, 0.5f, 0.13f, 1f, .6f, .3f, .4f, .2f, .6f);

        watch.Stop();
        double microseconds = watch.Elapsed.TotalMilliseconds * 1000.0 / Iterations;
        Console.WriteLine($"bake: {n}x{n} in {microseconds:0.0} us per frame ({Iterations} iterations)");
    }

    // The two endpoints, at a sea-level site and latitude 45 — the same calls TwilightSweep's adapter
    // makes, so what this previews is the shipped colour rather than a stand-in.
    private static SkyColorTemperature.Rgb HotTint(float elevation) =>
        SkyColorTemperature.SkyColorForElevation(
            elevation, 1f, 1f, AerosolSpectrum.ReferenceAngstromExponent, inVacuum: false);

    private static SkyColorTemperature.Rgb CoolTint(float elevation) =>
        PurpleLightMath.ComposedHue(
            elevation, 45f, 1f, 1f, AerosolSpectrum.ReferenceAngstromExponent, inVacuum: false);

    private static byte Add(float ground, float light, float alpha)
    {
        float value = ground + (light * alpha);
        if (value < 0f)
            value = 0f;

        return (byte)((value > 1f ? 1f : value) * 255f + 0.5f);
    }

    // Copies one square frame into column `index` of a horizontal strip.
    private static void BlitColumn(byte[] strip, byte[] frame, int index)
    {
        int stripWidth = strip.Length / (FrameSize * 4);

        for (int y = 0; y < FrameSize; y++)
        {
            int srcRow = y * FrameSize * 4;
            int dstRow = ((y * stripWidth) + (index * FrameSize)) * 4;
            Array.Copy(frame, srcRow, strip, dstRow, FrameSize * 4);
        }
    }
}
