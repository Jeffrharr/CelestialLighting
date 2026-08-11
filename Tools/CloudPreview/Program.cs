using System;
using System.Diagnostics;
using CelestialLighting;

namespace CelestialLighting.Tools;

// Renders §23b's underlit-cloud field (DESIGN.md §23b, issue #88 option 2) to PNGs, and times a bake.
//
// WHY THIS EXISTS. "Does it look like clouds" is a question about the field's SHAPE, and answering it
// through the live harness costs a full RimWorld boot per iteration — ten minutes to see whether an
// octave count helped. It is also the wrong instrument: the game composites the field over terrain at
// low alpha in near-darkness, so a shape problem and a strength problem are hard to tell apart on
// screen. This renders the field alone, at full contrast, in about a second.
//
// The bake timing is here for the same reason. The field is re-walked whenever the cloud fraction
// moves, on the main thread, so the resolution/octave trade has a per-frame cost attached to it and
// this is where that cost is measured rather than guessed at.
//
// Usage: dotnet run --project Tools/CloudPreview [outputDirectory]
public static class Program
{
    private static readonly float[] Fractions = { 0.15f, 0.35f, 0.5f, 0.75f };
    private static readonly int[] Seeds = { 1234, 77, 9001 };

    public static void Main(string[] args)
    {
        string outputDir = args.Length > 0 ? args[0] : ".";
        int n = CloudField.Resolution;
        float[] intensity = new float[n * n];

        Console.WriteLine(
            $"resolution {n}  cellsPerRepeat {CloudField.CellsPerRepeat}  " +
            $"lattice {CloudField.LatticeCells}  octaves {CloudField.Octaves}  " +
            $"edgeSoftness {CloudField.EdgeSoftness}");
        Console.WriteLine(
            $"one texel spans {CloudField.CellsPerRepeat / n:F2} map cells; " +
            $"base feature {CloudField.CellsPerRepeat / CloudField.LatticeCells:F0} " +
            $"cells; finest octave " +
            $"{CloudField.CellsPerRepeat / (CloudField.LatticeCells * (1 << (CloudField.Octaves - 1))):F1} cells");
        Console.WriteLine();

        foreach (int seed in Seeds)
        {
            foreach (float fraction in Fractions)
            {
                float mean = CloudField.FillIntensity(intensity, n, n, fraction, seed);

                // Two renders per case, because they answer different questions. The RESIDUAL is what
                // is actually drawn; the raw INTENSITY is what the field looks like before the mean
                // comes out, which is the one to look at when judging shape, since the residual's
                // contrast depends on how much of it §23's flat lane is carrying.
                Png.Write($"{outputDir}/cloudfield_s{seed}_f{fraction:0.00}_intensity.png", n, n,
                    Grey(intensity, n * n, v => v));
                Png.Write($"{outputDir}/cloudfield_s{seed}_f{fraction:0.00}_residual.png", n, n,
                    Grey(intensity, n * n, v => CloudField.Residual(v, mean)));

                float covered = 0f;
                float peak = 0f;
                for (int i = 0; i < intensity.Length; i++)
                {
                    covered += intensity[i];
                    float residual = CloudField.Residual(intensity[i], mean);
                    if (residual > peak)
                        peak = residual;
                }

                Console.WriteLine(
                    $"seed {seed,5}  fraction {fraction:0.00}  covered {covered / intensity.Length:0.000}  " +
                    $"mean {mean:0.000}  peak residual {peak:0.000}");
            }
        }

        Console.WriteLine();

        // One bake, timed on its own after a warm-up, because this is the number that decides whether
        // a resolution or octave bump is affordable on the main thread.
        CloudField.FillIntensity(intensity, n, n, 0.4f, 1);
        Stopwatch watch = Stopwatch.StartNew();
        const int Bakes = 20;
        for (int i = 0; i < Bakes; i++)
            CloudField.FillIntensity(intensity, n, n, 0.4f, i);

        watch.Stop();
        Console.WriteLine($"illumination bake {watch.Elapsed.TotalMilliseconds / Bakes:0.00} ms " +
                          $"({n * n} texels x {CloudField.Octaves} octaves)");

        // §25's sheet bake, timed separately because it is an order of magnitude larger and runs on
        // the main thread. This is the number that decides whether the sheet's resolution and octave
        // count are affordable at all — see CloudSheetOverlay.Rebake for what triggers it.
        int sheet = CloudField.SheetResolution;
        float[] sheetIntensity = new float[sheet * sheet];
        CloudField.FillIntensity(sheetIntensity, sheet, sheet, 0.4f, 1, CloudField.SheetOctaves);

        watch.Restart();
        const int SheetBakes = 5;
        for (int i = 0; i < SheetBakes; i++)
            CloudField.FillIntensity(sheetIntensity, sheet, sheet, 0.4f, i, CloudField.SheetOctaves);

        watch.Stop();
        Console.WriteLine($"sheet bake        {watch.Elapsed.TotalMilliseconds / SheetBakes:0.00} ms " +
                          $"({sheet * sheet} texels x {CloudField.SheetOctaves} octaves)");
    }

    private static byte[] Grey(float[] values, int count, Func<float, float> select)
    {
        byte[] rgba = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            float v = select(values[i]);
            byte b = (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
            int o = i * 4;
            rgba[o] = b;
            rgba[o + 1] = b;
            rgba[o + 2] = b;
            rgba[o + 3] = 255;
        }

        return rgba;
    }
}
