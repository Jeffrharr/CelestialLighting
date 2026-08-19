using System;
using System.Diagnostics;
using System.IO;
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

        // A second argument names ONE section to run. Variant D marches every screen pixel rather
        // than every atlas texel, so its stills cost about as much as everything else here put
        // together — and iterating on a lighting constant means running that one section twenty
        // times, not running the field previews twenty times as a tax on it.
        if (args.Length > 1 && args[1] == "raymarch")
        {
            WriteRaymarchStills(outputDir);
            return;
        }

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

        WriteBlobAtlas(outputDir);
        WriteSunsetSequence();
        WriteVolumeSequence(outputDir);
        WriteVolumeFrames(outputDir);
        WriteRaymarchStills(outputDir);
    }

    // §25b: the blob atlas the game actually draws its sheets from, one row per deck.
    //
    // WHY THIS IS WORTH RENDERING AND THE FIELD ABOVE IS NOT ENOUGH. Everything above previews the
    // tiled illumination field, which is not what a cloud looks like — it is what the LIGHT off one
    // looks like, deliberately soft. §25's sheets come from this atlas instead, and "is that a
    // cirrus" is a question about its rows. Answering it here costs a second; answering it through
    // the harness costs a RimWorld boot, and the sheet arrives on screen at low alpha over terrain,
    // where a shape problem and a strength problem look the same.
    private static void WriteBlobAtlas(string outputDir)
    {
        // The game's own dimensions (CloudSheetOverlay.AtlasCells / AtlasSize). Restated rather than
        // read because those live in a file that imports UnityEngine and cannot be linked here — so
        // they are printed alongside the render, and a divergence is visible rather than silent.
        const int Cells = 3;
        const int Size = 384;

        float[] intensity = new float[Size * Size];

        Stopwatch watch = Stopwatch.StartNew();
        CloudField.FillBlobAtlas(
            intensity, Size, Cells, seed: 20260810, octaves: CloudField.SheetOctaves,
            rowCut: CloudDeckMath.ShapeCuts(),
            rowGain: CloudDeckMath.ShapeGains(),
            rowFrequencyU: CloudDeckMath.FrequenciesU(),
            rowFrequencyV: CloudDeckMath.FrequenciesV());
        watch.Stop();

        Png.Write($"{outputDir}/cloudatlas.png", Size, Size, Grey(intensity, Size * Size, v => v));

        Console.WriteLine();
        Console.WriteLine(
            $"blob atlas        {watch.Elapsed.TotalMilliseconds:0.00} ms " +
            $"({Size}x{Size}, {Cells}x{Cells} blobs of {Size / Cells}, " +
            $"{CloudField.SheetOctaves} octaves) — ONCE at load, never again");

        // Per-row coverage, which is the number behind "cirrus is thin". Rows run bottom-up in the
        // atlas the same way UVs do, so row 0 is the low deck.
        for (int row = 0; row < Cells; row++)
        {
            double sum = 0.0;
            int blobSize = Size / Cells;
            for (int y = row * blobSize; y < (row + 1) * blobSize; y++)
            {
                for (int x = 0; x < Size; x++)
                    sum += intensity[y * Size + x];
            }

            CloudDeckMath.Deck spec = CloudDeckMath.DeckAt(row);
            Console.WriteLine(
                $"  row {row} ({spec.AltitudeMetres / 1000f:0.0} km)  " +
                $"fill {sum / (blobSize * Size):0.000}  opacity {spec.Opacity:0.00}  " +
                $"cut {spec.ShapeCut:0.00} gain {spec.ShapeGain:0.0} " +
                $"freq {spec.FrequencyU:0.00}x{spec.FrequencyV:0.00}  " +
                $"→ effective {sum / (blobSize * Size) * spec.Opacity:0.000}");
        }
    }

    // §25b's headline claim as a table: the decks lose the sun in order, so a sunset has a sequence
    // in it rather than one recolour. Printed rather than rendered because it is a timing, and a
    // timing is a column of numbers — this is also the survey that says which elevations are worth
    // spending a live harness capture on (the whole sequence is under four degrees wide).
    private static void WriteSunsetSequence()
    {
        Console.WriteLine();
        Console.Write("elev   ");
        for (int deck = 0; deck < CloudDeckMath.DeckCount; deck++)
            Console.Write($"{CloudDeckMath.DeckAt(deck).AltitudeMetres / 1000f,6:0.0}km");

        Console.WriteLine();

        for (float elevation = 6f; elevation >= -5f; elevation -= 0.25f)
        {
            Console.Write($"{elevation,5:0.00}  ");
            for (int deck = 0; deck < CloudDeckMath.DeckCount; deck++)
            {
                float underlit = CloudSheetMath.UnderlitFraction(
                    elevation, CloudDeckMath.ShadowEntryDegrees(deck));
                Console.Write($"{underlit,8:0.000}");
            }

            Console.WriteLine();
        }
    }



    // §25c as an animation: the sun walked down through the whole sunset, one frame per step.
    //
    // WHY A FILM AND NOT MORE STILLS. The claim §25b makes and #144 says is unreachable is a
    // SEQUENCE — the decks lose the sun in order, bottom first — and a sequence is the one thing a
    // contact sheet of stills cannot show, because the eye has to hold nine frames in mind at once to
    // see it. It is also the honest instrument for judging whether the effect reads as weather rather
    // than as a texture swap, which is the failure §26 was rejected for.
    //
    // Written as numbered frames for Tools/AuroraPreview/make_animation.sh, which stitches them to a
    // GIF rather than a video deliberately: gwenview, the image viewer this box has, animates GIFs
    // inline and will not play video at all.
    private static void WriteVolumeFrames(string outputDir)
    {
        const int Cells = 3;
        const int Size = 384;
        const float Azimuth = 200f;
        const float DemoFloor = 1.00f;
        const float DemoAmplitude = 0.75f;

        // The whole sequence lives between about +6 and -4.5 degrees — under a quarter of an in-game
        // hour, which is why §25b's survey had to step at 0.02 h to find it at all.
        const float StartElevation = 7f;
        const float EndElevation = -4.5f;
        const int Frames = 110;

        string dir = $"{outputDir}/frames_sunset";
        Directory.CreateDirectory(dir);

        float[] density = new float[Size * Size];
        byte[] shaded = new byte[Size * Size * 4];
        byte[] flat = new byte[Size * Size * 4];
        byte[] volume = new byte[Size * Size * CloudVolumeMath.VolumeLayers];
        float[] heightField = new float[Size * Size];
        byte[] heightShaded = new byte[Size * Size * 4];
        byte[] quantShaded = new byte[Size * Size * 4];

        CloudField.FillBlobAtlas(
            density, Size, Cells, seed: 20260810, octaves: CloudField.SheetOctaves,
            rowCut: CloudDeckMath.ShapeCuts(), rowGain: CloudDeckMath.ShapeGains(),
            rowFrequencyU: CloudDeckMath.FrequenciesU(), rowFrequencyV: CloudDeckMath.FrequenciesV());

        // Baked ONCE for the whole film, which is the point: the volume is a function of the noise,
        // not of the sun, so a 110-frame sunset pays for it exactly once.
        CloudVolumeMath.FillBlobVolume(
            volume, Size, Cells, CloudVolumeMath.VolumeLayers, seed: 20260810,
            octaves: CloudField.SheetOctaves,
            rowCut: CloudDeckMath.ShapeCuts(), rowGain: CloudDeckMath.ShapeGains(),
            rowFrequencyU: CloudDeckMath.FrequenciesU(), rowFrequencyV: CloudDeckMath.FrequenciesV());

        float[] thick = new float[CloudDeckMath.DeckCount];
        for (int deck = 0; deck < CloudDeckMath.DeckCount; deck++)
            thick[deck] = CloudVolumeMath.ThicknessMetres(deck);

        CloudVolumeMath.FillHeightField(heightField, density, Size, Cells, thick);

        for (int frame = 0; frame < Frames; frame++)
        {
            float t = frame / (float)(Frames - 1);
            float elevation = StartElevation + (EndElevation - StartElevation) * t;

            SkyColorTemperature.Rgb lit = SkyColorTemperature.SkyColorForElevation(
                elevation, 1f, 0.1f, 1.3f, inVacuum: false);
            SkyColorTemperature.Rgb hue = PurpleLightMath.ComposedHue(
                elevation, 45f, 1f, 0.1f, 1.3f, inVacuum: false);
            SkyColorTemperature.Rgb shadow = new SkyColorTemperature.Rgb(
                hue.R * CloudVolumeMath.AmbientSkyFraction,
                hue.G * CloudVolumeMath.AmbientSkyFraction,
                hue.B * CloudVolumeMath.AmbientSkyFraction);

            CloudVolumeMath.ShadeBlobVolume(
                shaded, volume, Size, Cells, CloudVolumeMath.VolumeLayers, Azimuth, elevation,
                lit.R, lit.G, lit.B, shadow.R, shadow.G, shadow.B, 1f, inVacuum: false);
            CloudVolumeMath.ShadeBlobVolume(
                flat, volume, Size, Cells, CloudVolumeMath.VolumeLayers, Azimuth, elevation,
                lit.R, lit.G, lit.B, shadow.R, shadow.G, shadow.B, 0f, inVacuum: false);

            Png.Write($"{dir}/on_{frame:000}.png", Size, Size,
                Composite(shaded, density, Size, Cells, elevation, lit, isolate: false,
                    decoupleAlpha: true, directFloor: DemoFloor, amplitude: DemoAmplitude,
                    alphaFromAtlas: true));

            // The baseline is §25b EXACTLY as it ships — shipped floor, shipped amplitude, alpha
            // coupled to illumination, flat white atlas. Anything else would compare §25c against a
            // build nobody can run.
            Png.Write($"{dir}/off_{frame:000}.png", Size, Size,
                Composite(flat, density, Size, Cells, elevation, lit, isolate: false));

            // Approach A, the height-field march, at the same calibration — kept in the film even
            // though it is superseded, because "flatter" is a claim about a comparison and the
            // comparison has to be watchable rather than asserted.
            CloudVolumeMath.ShadeBlobAtlas(
                heightShaded, density, heightField, Size, Cells, Azimuth, elevation,
                lit.R, lit.G, lit.B, shadow.R, shadow.G, shadow.B, 1f, inVacuum: false);
            Png.Write($"{dir}/a_{frame:000}.png", Size, Size,
                Composite(heightShaded, density, Size, Cells, elevation, lit, isolate: false,
                    decoupleAlpha: true, directFloor: DemoFloor, amplitude: DemoAmplitude));

            // Approach C: the 3-D march with the azimuth snapped to 8 buckets, which is what baking
            // a handful of lit volumes at load would give.
            const float BucketDegrees = 360f / 8f;
            float snapped = MathF.Round(Azimuth / BucketDegrees) * BucketDegrees;
            CloudVolumeMath.ShadeBlobVolume(
                quantShaded, volume, Size, Cells, CloudVolumeMath.VolumeLayers, snapped, elevation,
                lit.R, lit.G, lit.B, shadow.R, shadow.G, shadow.B, 1f, inVacuum: false);
            Png.Write($"{dir}/c_{frame:000}.png", Size, Size,
                Composite(quantShaded, density, Size, Cells, elevation, lit, isolate: false,
                    decoupleAlpha: true, directFloor: DemoFloor, amplitude: DemoAmplitude,
                    alphaFromAtlas: true));

            // Approach B: the two-quad offset, no height field and no volume.
            Png.Write($"{dir}/b_{frame:000}.png", Size, Size,
                CompositeTwoPass(density, Size, Cells, elevation, Azimuth, lit, shadow));
        }

        Console.WriteLine();
        Console.WriteLine(
            $"sunset frames     {Frames} x2 written to {dir} " +
            $"({StartElevation:0.0} to {EndElevation:0.0} degrees)");
    }

    // §25c: the volumetric shading, composited the way the screen composites it (issue #144).
    //
    // WHY IT IS COMPOSITED RATHER THAN SHOWN RAW. The atlas RGB this writes is a MODULATION, not a
    // colour — it is multiplied by the sheet's own `material.color` and then alpha-blended over the
    // terrain. Looking at it raw would answer a question nobody asked: a modulation map always looks
    // like a plausible cloud because it is grey and lumpy. The question is whether the SUNSET reads,
    // and that only exists after the multiply and the blend, so this does both.
    //
    // The one thing it cannot reproduce is the layout — on screen these blobs are scattered, mirrored
    // and drifting across a map, not tiled 3x3. That is deliberate: the layout is §25's business and
    // is already verified; what is on trial here is one blob's lighting.
    private static void WriteVolumeSequence(string outputDir)
    {
        const int Cells = 3;
        const int Size = 384;

        // The elevations §25b actually measured, so the offline read and the live captures are talking
        // about the same instants rather than two different sunsets. The high ones are here to prove a
        // negative: at noon the sun is overhead, every cloud top is lit alike, and this lane should do
        // almost NOTHING. A volumetric model that changes the noon frame is one that has mistaken a
        // constant for a light.
        float[] elevations = { 56.72f, 30f, 10f, 5f, 2f, 0f, -0.68f, -1.49f, -2.44f, -3.70f };

        float[] density = new float[Size * Size];
        float[] height = new float[Size * Size];
        byte[] shaded = new byte[Size * Size * 4];
        byte[] quantised = new byte[Size * Size * 4];
        byte[] volumeShaded = new byte[Size * Size * 4];
        byte[] volume = new byte[Size * Size * CloudVolumeMath.VolumeLayers];
        float[] heightField = new float[Size * Size];
        byte[] heightShaded = new byte[Size * Size * 4];
        byte[] quantShaded = new byte[Size * Size * 4];
        byte[] flat = new byte[Size * Size * 4];

        CloudField.FillBlobAtlas(
            density, Size, Cells, seed: 20260810, octaves: CloudField.SheetOctaves,
            rowCut: CloudDeckMath.ShapeCuts(), rowGain: CloudDeckMath.ShapeGains(),
            rowFrequencyU: CloudDeckMath.FrequenciesU(), rowFrequencyV: CloudDeckMath.FrequenciesV());

        float[] thickness = new float[CloudDeckMath.DeckCount];
        for (int deck = 0; deck < CloudDeckMath.DeckCount; deck++)
            thickness[deck] = CloudVolumeMath.ThicknessMetres(deck);

        Stopwatch watch = Stopwatch.StartNew();
        CloudVolumeMath.FillHeightField(height, density, Size, Cells, thickness);
        watch.Stop();
        double heightMs = watch.Elapsed.TotalMilliseconds;

        watch.Restart();
        CloudVolumeMath.FillBlobVolume(
            volume, Size, Cells, CloudVolumeMath.VolumeLayers, seed: 20260810,
            octaves: CloudField.SheetOctaves,
            rowCut: CloudDeckMath.ShapeCuts(), rowGain: CloudDeckMath.ShapeGains(),
            rowFrequencyU: CloudDeckMath.FrequenciesU(), rowFrequencyV: CloudDeckMath.FrequenciesV());

        float[] thick = new float[CloudDeckMath.DeckCount];
        for (int deck = 0; deck < CloudDeckMath.DeckCount; deck++)
            thick[deck] = CloudVolumeMath.ThicknessMetres(deck);

        CloudVolumeMath.FillHeightField(heightField, density, Size, Cells, thick);
        watch.Stop();
        Console.WriteLine();
        Console.WriteLine(
            $"3-D volume bake   {watch.Elapsed.TotalMilliseconds:0.00} ms " +
            $"({Size}x{Size}x{CloudVolumeMath.VolumeLayers} = " +
            $"{Size * Size * CloudVolumeMath.VolumeLayers / 1048576f:0.0} MB) — ONCE at load, " +
            $"and it is the same buffer a shader raymarch would upload as a 3-D texture");

        Console.WriteLine();
        Console.WriteLine(
            $"height field      {heightMs:0.00} ms  " +
            $"(peak {CloudVolumeMath.MaxHeightFraction:0.00} of blob radius at " +
            $"{CloudVolumeMath.ThicknessReferenceMetres:0} m; " +
            $"decks {thickness[0]:0}/{thickness[1]:0}/{thickness[2]:0} m) — ONCE at load");
        Console.WriteLine();
        Console.WriteLine("elev    lit rgb              shadow rgb           march  shadeMs");

        // A fixed sun azimuth. Any value does for a still; 225 (south-west) puts the light coming from
        // the lower-left of the image, which is the direction a reader's eye assumes light comes from.
        // 200 rather than a round 225: 225 is EXACTLY on an 8-bucket boundary, so approach C's
        // quantisation would be a no-op and the render would claim a fidelity it had not tested.
        // 200 snaps to 180, a 20 degree error, which is close to the worst case for 8 buckets.
        const float Azimuth = 200f;

        // The calibration the variant renders share, picked off the floor/amplitude sweep below.
        // Every approach is drawn at the SAME settings, or the comparison is between calibrations
        // rather than between renderers.
        const float DemoFloor = 1.00f;
        const float DemoAmplitude = 0.75f;

        foreach (float elevation in elevations)
        {
            SkyColorTemperature.Rgb lit = SkyColorTemperature.SkyColorForElevation(
                elevation, pressureFraction: 1f, aerosolFraction: 0.1f,
                angstromExponent: 1.3f, inVacuum: false);
            // Scaled to an absolute radiance before use — ComposedHue is normalised to peak 1 and
            // carries hue only, and handing it over raw makes every channel ratio clamp at 1 and the
            // shadows lose their colour entirely. See CloudVolumeMath.AmbientSkyFraction.
            SkyColorTemperature.Rgb hue = PurpleLightMath.ComposedHue(
                elevation, latitudeDegrees: 45f, pressureFraction: 1f, aerosolFraction: 0.1f,
                angstromExponent: 1.3f, inVacuum: false);
            SkyColorTemperature.Rgb shadow = new SkyColorTemperature.Rgb(
                hue.R * CloudVolumeMath.AmbientSkyFraction,
                hue.G * CloudVolumeMath.AmbientSkyFraction,
                hue.B * CloudVolumeMath.AmbientSkyFraction);

            watch.Restart();
            CloudVolumeMath.ShadeBlobAtlas(
                shaded, density, height, Size, Cells, Azimuth, elevation,
                lit.R, lit.G, lit.B, shadow.R, shadow.G, shadow.B,
                strength: 1f, inVacuum: false);
            watch.Stop();

            CloudVolumeMath.ShadeBlobAtlas(
                flat, density, height, Size, Cells, Azimuth, elevation,
                lit.R, lit.G, lit.B, shadow.R, shadow.G, shadow.B,
                strength: 0f, inVacuum: false);

            string tag = elevation < 0f
                ? $"m{-elevation * 100f:0000}"
                : $"p{elevation * 100f:0000}";

            Png.Write($"{outputDir}/cv_{tag}_on.png", Size, Size,
                Composite(shaded, density, Size, Cells, elevation, lit, isolate: false));
            Png.Write($"{outputDir}/cv_{tag}_off.png", Size, Size,
                Composite(flat, density, Size, Cells, elevation, lit, isolate: false));

            // The SAME shading, composited at a fixed high alpha against a mid grey instead of
            // through §25b's illumination curve.
            //
            // This exists because the two frames above answer two questions at once and the answers
            // point in opposite directions. At sunset §25b's own curve multiplies the sheet's colour
            // AND its alpha by the same illumination — 0.55 at best — so a cloud lands at roughly
            // 0.14 alpha over near-black terrain and nothing about it can be seen, whatever it is
            // shaded like. That is the p90 1.47 in #144's table, and it is a VISIBILITY problem, not
            // a lighting one. This render removes the visibility term so the lighting can be judged
            // on its own; if the tops-lit/base-dark read is absent HERE, the model is wrong, and if
            // it is present here but absent above, the model is right and something else is hiding it.
            Png.Write($"{outputDir}/cv_{tag}_iso.png", Size, Size,
                Composite(shaded, density, Size, Cells, elevation, lit, isolate: true));
            Png.Write($"{outputDir}/cv_{tag}_isooff.png", Size, Size,
                Composite(flat, density, Size, Cells, elevation, lit, isolate: true));

            // The shading again, but with the sheet's ALPHA decoupled from how lit it is.
            //
            // A cloud's opacity is a property of the cloud — how much water is in the way — and not
            // of the light falling on it. §25b multiplies both by one `illumination`, which is right
            // for a deck that has lost the sun (a dark sheet at full alpha would black the colony out)
            // and wrong for the deck that still has it: a sunlit deck at sunset is the brightest thing
            // in the sky and it goes SHEER at exactly the moment it should dominate. This variant
            // keeps the colour term as shipped and lets a fully sunlit deck keep its own opacity.
            Png.Write($"{outputDir}/cv_{tag}_fix.png", Size, Size,
                Composite(shaded, density, Size, Cells, elevation, lit, isolate: false,
                    decoupleAlpha: true));
            Png.Write($"{outputDir}/cv_{tag}_fixoff.png", Size, Size,
                Composite(flat, density, Size, Cells, elevation, lit, isolate: false,
                    decoupleAlpha: true));

            // The calibration sweep, and it is here rather than in a spreadsheet because the quantity
            // being calibrated is a LOOK. §25b sets UnderlitDeckFloor to 0.55, meaning a deck in full
            // direct sun is rendered at just over half brightness; at sunset that is multiplied into
            // both the colour and the alpha, and the result is the near-black frame #144 opened with.
            // Rendering 0.55 against 0.75 and 1.00 side by side is the cheapest way to find out how
            // much of #144's invisibility is the renderer and how much is this one constant.
            // Two axes, because the frame is dark for two independent reasons and fixing one alone
            // does not move it. `floor` is how bright a deck in full direct sun is allowed to be
            // (§25b caps it at 0.55); `amplitude` is the sheet's own opacity before the blob's
            // density and the deck's opacity are multiplied into it (§25 sets 0.35, and since the
            // density field averages about 0.3 across a blob, the alpha that actually reaches the
            // screen is roughly a tenth of it).
            // APPROACH A2: the 3-D volume, light-marched. Same draw path, same layout, same deck
            // table — the only difference from A is that the light integral runs THROUGH a baked
            // density volume instead of across a height field.
            CloudVolumeMath.ShadeBlobVolume(
                volumeShaded, volume, Size, Cells, CloudVolumeMath.VolumeLayers, Azimuth, elevation,
                lit.R, lit.G, lit.B, shadow.R, shadow.G, shadow.B,
                strength: 1f, inVacuum: false);

            Png.Write($"{outputDir}/cv_{tag}_vol.png", Size, Size,
                Composite(volumeShaded, density, Size, Cells, elevation, lit, isolate: false,
                    decoupleAlpha: true, directFloor: DemoFloor, amplitude: DemoAmplitude,
                    alphaFromAtlas: true));

            Png.Write($"{outputDir}/cv_{tag}_voliso.png", Size, Size,
                Composite(volumeShaded, density, Size, Cells, elevation, lit, isolate: true,
                    alphaFromAtlas: true));

            // APPROACH C: the same shading with the sun azimuth SNAPPED to 8 buckets, which is what
            // baking a small set of lit-from-angle-theta atlases at load would look like. It is not a
            // different model — it is approach A quantised — so rendering it here is how we find out
            // whether the live re-shade is worth its cost or whether N baked variants would pass.
            const float BucketDegrees = 360f / 8f;
            float snapped = MathF.Round(Azimuth / BucketDegrees) * BucketDegrees;
            CloudVolumeMath.ShadeBlobAtlas(
                quantised, density, height, Size, Cells, snapped, elevation,
                lit.R, lit.G, lit.B, shadow.R, shadow.G, shadow.B,
                strength: 1f, inVacuum: false);

            Png.Write($"{outputDir}/cv_{tag}_quant.png", Size, Size,
                Composite(quantised, density, Size, Cells, elevation, lit, isolate: false,
                    decoupleAlpha: true, directFloor: DemoFloor, amplitude: DemoAmplitude));

            // APPROACH B: no height field at all. Draw the blob twice — a cool shadowed body, and a
            // warm copy shrunk and nudged toward the sun so it reads as the sunward TOP of a volume
            // whose base is hidden beneath it. This is the cheap control, and it exists to answer
            // whether the heightfield march is buying anything a two-quad trick would not: if the
            // frames are indistinguishable, approach A is 55 ms of bake for nothing.
            Png.Write($"{outputDir}/cv_{tag}_twopass.png", Size, Size,
                CompositeTwoPass(density, Size, Cells, elevation, Azimuth, lit, shadow));

            foreach (float floor in new[] { 0.55f, 1.00f })
            {
                foreach (float amplitude in new[] { 0.35f, 0.55f, 0.75f })
                {
                    Png.Write(
                        $"{outputDir}/cv_{tag}_f{floor * 100f:000}a{amplitude * 100f:000}.png",
                        Size, Size,
                        Composite(shaded, density, Size, Cells, elevation, lit, isolate: false,
                            decoupleAlpha: true, directFloor: floor, amplitude: amplitude));
                }
            }

            // How far a shadow reaches at this sun, in texels, which is the number that says whether
            // the march can see anything at all. Restated from ShadeBlobAtlas's own derivation.
            float peakTexels = (Size / Cells) * 0.5f * CloudVolumeMath.MaxHeightFraction;
            float tan = MathF.Abs(MathF.Tan(elevation * (MathF.PI / 180f)));
            float reach = MathF.Min(Size / Cells, peakTexels / MathF.Max(tan, 1e-3f));

            Console.WriteLine(
                $"{elevation,6:0.00}  " +
                $"({lit.R:0.000},{lit.G:0.000},{lit.B:0.000})  " +
                $"({shadow.R:0.000},{shadow.G:0.000},{shadow.B:0.000})  " +
                $"{reach,5:0.0}  {watch.Elapsed.TotalMilliseconds,6:0.00}");
        }
    }

    // Multiplies the modulation by the sheet's own colour and alpha-blends it over terrain, which is
    // what CloudSheetOverlay + ShaderDatabase.Transparent do between them on screen.
    //
    // The per-deck colour and alpha are rebuilt from the SHIPPED cores (§25b's DeckIllumination and
    // UnderlitFraction, §25's SheetAmplitude) rather than invented, so the only thing this preview
    // adds to the game's own arithmetic is the background to blend against.
    private static byte[] Composite(
        byte[] atlas, float[] density, int size, int cells, float elevation,
        SkyColorTemperature.Rgb lit, bool isolate, bool decoupleAlpha = false,
        float directFloor = CloudSheetMath.UnderlitDeckFloor,
        float amplitude = CloudSheetMath.SheetAmplitude,
        bool alphaFromAtlas = false)
    {
        // Sky glow as a stand-in. Vanilla drives it from the hour rather than the elevation, so this
        // is a preview convenience and NOT a claim about the game's curve — it only has to put noon
        // near 1 and civil twilight near 0 so the composite is judged against a plausible ground.
        float glow = Clamp01((elevation + 6f) / 12f);
        glow = glow * glow * (3f - 2f * glow);

        // Terrain. Dark and desaturated, because a sunset frame is dark: §25b's own −3.70° capture
        // measured the scene at rgb(27,17,11), and judging cloud contrast against white would flatter
        // it enormously.
        float bgR = isolate ? 0.22f : 0.05f + 0.30f * glow;
        float bgG = isolate ? 0.23f : 0.04f + 0.28f * glow;
        float bgB = isolate ? 0.26f : 0.03f + 0.24f * glow;

        int blobSize = size / cells;
        byte[] rgba = new byte[size * size * 4];

        for (int y = 0; y < size; y++)
        {
            int deck = y / blobSize;
            CloudDeckMath.Deck spec = CloudDeckMath.DeckAt(deck);

            float underlit = CloudSheetMath.UnderlitFraction(
                elevation, CloudDeckMath.ShadowEntryDegrees(deck));
            // DeckIllumination with its floor exposed, so the sweep above can move the one constant
            // without the preview drifting from the shipped shape of the curve.
            float ambientLevel = CloudSheetMath.SheetBrightness(glow);
            float directLevel = directFloor * Clamp01(underlit);
            float illumination = MathF.Max(ambientLevel, directLevel);

            // The sheet's own colour: §8's warm direct light rescaled by its brightest channel so the
            // multiply changes hue without darkening (§25b's DayColour treatment), mixed toward
            // neutral daylight grey by how underlit the deck is, then scaled by its illumination.
            float peak = MathF.Max(lit.R, MathF.Max(lit.G, lit.B));
            float warmR = peak <= 0f ? 1f : lit.R / peak;
            float warmG = peak <= 0f ? 1f : lit.G / peak;
            float warmB = peak <= 0f ? 1f : lit.B / peak;

            float matR = (0.86f + (warmR - 0.86f) * underlit) * illumination;
            float matG = (0.87f + (warmG - 0.87f) * underlit) * illumination;
            float matB = (0.90f + (warmB - 0.90f) * underlit) * illumination;

            // The shipped term: illumination scales opacity as well as colour.
            float alphaScale = illumination;

            if (decoupleAlpha)
            {
                // Ambient opacity still fades toward night — an unlit cloud in the dark should not
                // sit on the map at full strength — but a deck in DIRECT sun keeps its own, because
                // that is a fact about the cloud rather than about the light. Note this uses
                // `underlit` raw rather than UnderlitDeckFloor * underlit: the 0.55 floor is a
                // BRIGHTNESS calibration and has no business setting how much water is in the way.
                float ambient = CloudSheetMath.SheetBrightness(glow);
                alphaScale = MathF.Max(ambient, underlit);
            }

            float sheetAlpha = amplitude * spec.Opacity * alphaScale;

            if (isolate)
            {
                // Keep the HUE the shading produced and discard only the levels, so this stays a
                // render of the real model rather than a different one.
                float hueScale = 1f / MathF.Max(1e-4f, MathF.Max(matR, MathF.Max(matG, matB)));
                matR *= hueScale;
                matG *= hueScale;
                matB *= hueScale;
                sheetAlpha = 0.92f;
            }

            for (int x = 0; x < size; x++)
            {
                int i = y * size + x;
                int o = i * 4;

                // The 3-D path carries its own opacity — a real column optical depth — in the
                // atlas alpha, so it must not be multiplied by the 2-D density as well or the
                // integral it just computed would be thrown away and halved.
                float coverage = alphaFromAtlas ? atlas[o + 3] / 255f : Clamp01(density[i]);
                float a = coverage * Clamp01(sheetAlpha);
                float r = (atlas[o + 0] / 255f) * matR;
                float g = (atlas[o + 1] / 255f) * matG;
                float b = (atlas[o + 2] / 255f) * matB;

                rgba[o + 0] = ToByte(bgR + (r - bgR) * a);
                rgba[o + 1] = ToByte(bgG + (g - bgG) * a);
                rgba[o + 2] = ToByte(bgB + (b - bgB) * a);
                rgba[o + 3] = 255;
            }
        }

        return rgba;
    }


    // Approach B: the two-pass offset, composited without any height field.
    //
    // The offset is the plan-view displacement between a cloud's base and the sunward edge of its
    // top: a volume of height h lit from elevation e shows its lit face displaced by h/tan(e) toward
    // the sun. That grows without bound at the horizon, so it is capped at a quarter of the blob —
    // past that the two copies separate and read as two clouds rather than one lit from the side,
    // which is exactly the failure mode this control is here to expose.
    private static byte[] CompositeTwoPass(
        float[] density, int size, int cells, float elevation, float azimuth,
        SkyColorTemperature.Rgb lit, SkyColorTemperature.Rgb shadow)
    {
        int blobSize = size / cells;
        CloudVolumeMath.SunDirection(azimuth, out float dirU, out float dirV);

        float peakTexels = blobSize * 0.5f * CloudVolumeMath.MaxHeightFraction;
        float tan = MathF.Max(MathF.Abs(MathF.Tan(elevation * (MathF.PI / 180f))), 1e-3f);
        float offset = MathF.Min(blobSize * 0.25f, peakTexels / tan);

        // The lit copy is shrunk as well as moved: the sunward face of a dome is a smaller footprint
        // than the dome, and an unshrunk copy reads as a hard-edged duplicate.
        const float TopScale = 0.82f;

        float glow = Clamp01((elevation + 6f) / 12f);
        glow = glow * glow * (3f - 2f * glow);

        float bgR = 0.05f + 0.30f * glow;
        float bgG = 0.04f + 0.28f * glow;
        float bgB = 0.03f + 0.24f * glow;

        byte[] rgba = new byte[size * size * 4];

        for (int y = 0; y < size; y++)
        {
            int deck = y / blobSize;
            CloudDeckMath.Deck spec = CloudDeckMath.DeckAt(deck);

            float underlit = CloudSheetMath.UnderlitFraction(
                elevation, CloudDeckMath.ShadowEntryDegrees(deck));
            float illumination = MathF.Max(
                CloudSheetMath.SheetBrightness(glow), 1.00f * Clamp01(underlit));
            // HALVED, because this approach composites the blob TWICE. At the same per-pass alpha
            // it would lay down roughly double the coverage of the single-pass approaches and win
            // the comparison on opacity rather than on lighting, which is not the question.
            float sheetAlpha = 0.5f * 0.75f * spec.Opacity * MathF.Max(
                CloudSheetMath.SheetBrightness(glow), Clamp01(underlit));

            // Shadowed body and lit top, as absolute colours rather than a modulation, because this
            // approach composites two quads rather than tinting one.
            float peak = MathF.Max(lit.R, MathF.Max(lit.G, lit.B));
            float warmR = peak <= 0f ? 1f : lit.R / peak;
            float warmG = peak <= 0f ? 1f : lit.G / peak;
            float warmB = peak <= 0f ? 1f : lit.B / peak;

            float topR = (0.86f + (warmR - 0.86f) * underlit) * illumination;
            float topG = (0.87f + (warmG - 0.87f) * underlit) * illumination;
            float topB = (0.90f + (warmB - 0.90f) * underlit) * illumination;

            // Radiance = albedo x irradiance, computed directly rather than as a ratio against the
            // lit colour. The ratio form is what approach A is forced into (it can only scale the
            // sheet's one material colour), and doing it here too blew the blue channel to
            // saturation: at sunset lit.B is 0.042 and shadow.B is 0.300, so the ratio is 7 and every
            // shadowed cloud came out vivid electric blue. Two quads have no such constraint, so
            // they should not inherit its arithmetic.
            float baseR = 0.86f * shadow.R;
            float baseG = 0.87f * shadow.G;
            float baseB = 0.90f * shadow.B;

            for (int x = 0; x < size; x++)
            {
                int o = (y * size + x) * 4;

                float r = bgR;
                float g = bgG;
                float b = bgB;

                // Pass 1: the shadowed body, in place.
                float aBase = Clamp01(density[y * size + x]) * Clamp01(sheetAlpha);
                r += (Clamp01(baseR) - r) * aBase;
                g += (Clamp01(baseG) - g) * aBase;
                b += (Clamp01(baseB) - b) * aBase;

                // Pass 2: the lit top, shrunk about the blob's centre and displaced toward the sun.
                int cx = deckAlignedCentre(x, blobSize);
                int cy = deckAlignedCentre(y, blobSize);
                int sx = (int)MathF.Round(cx + (x - cx) / TopScale - dirU * offset);
                int sy = (int)MathF.Round(cy + (y - cy) / TopScale - dirV * offset);

                bool inside = sx >= (x / blobSize) * blobSize && sx < (x / blobSize + 1) * blobSize
                    && sy >= deck * blobSize && sy < (deck + 1) * blobSize;
                if (inside)
                {
                    float aTop = Clamp01(density[sy * size + sx]) * Clamp01(sheetAlpha);
                    r += (Clamp01(topR) - r) * aTop;
                    g += (Clamp01(topG) - g) * aTop;
                    b += (Clamp01(topB) - b) * aTop;
                }

                rgba[o + 0] = ToByte(r);
                rgba[o + 1] = ToByte(g);
                rgba[o + 2] = ToByte(b);
                rgba[o + 3] = 255;
            }
        }

        return rgba;
    }

    private static int deckAlignedCentre(int coordinate, int blobSize) =>
        (coordinate / blobSize) * blobSize + blobSize / 2;

    private static byte ToByte(float value) =>
        (byte)Math.Clamp((int)(Clamp01(value) * 255f + 0.5f), 0, 255);

    private static float Clamp01(float value) =>
        value < 0f ? 0f : (value > 1f ? 1f : value);

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

    // §25c variant D: the volume marched per screen pixel, laid out against A2's baked atlas at the
    // magnification the game draws them at (issue #144).
    //
    // WHY THE MAGNIFICATION IS THE WHOLE POINT OF THIS RENDER. Every other picture in this tool is
    // drawn at one output pixel per atlas texel, which silently hands A2 a resolution it does not
    // have on screen: in game a blob is 128 atlas texels stretched across several hundred pixels of
    // sheet. Comparing a per-pixel renderer against a baked one at 1:1 compares them at the one
    // magnification where the bake cannot lose, and would report the difference between them as
    // nearly nothing. So A2 is drawn here exactly as the GPU draws it — bilinearly upsampled by
    // Supersample — and D is marched at the output resolution, which is what it would cost.
    //
    // Everything else is held equal on purpose: same volume, same seed, same sun, same deck rows,
    // same background, and the same decoupled-alpha calibration §25c's film uses. The only variable
    // is which renderer produced the cloud.
    private static void WriteRaymarchStills(string outputDir)
    {
        const int Cells = 3;
        const int Size = 384;

        // 3x, because that is roughly what a 128-texel blob is stretched by when a sheet is drawn
        // across a map at a normal zoom. Not a quality knob: raising it does not make D better, it
        // makes the comparison less like the screen.
        const int Supersample = 3;

        const float Azimuth = 200f;
        const float DemoFloor = 1.00f;
        const float DemoAmplitude = 0.75f;

        // The elevations §25b measured live, so the offline read and the live captures name the same
        // instants. The high ones prove a negative: at noon every cloud top is lit alike and both
        // renderers should do almost nothing, and a variant that changes the noon frame has mistaken
        // a constant for a light.
        float[] elevations = { 56.72f, 30f, 10f, 5f, 2f, 0f, -0.68f, -1.49f, -2.44f, -3.70f };

        int outSize = Size * Supersample;

        float[] density = new float[Size * Size];
        byte[] volume = new byte[Size * Size * CloudVolumeMath.VolumeLayers];
        byte[] volumeShaded = new byte[Size * Size * 4];
        byte[] marched = new byte[outSize * outSize * 4];
        byte[] marchedThick = new byte[outSize * outSize * 4];

        CloudField.FillBlobAtlas(
            density, Size, Cells, seed: 20260810, octaves: CloudField.SheetOctaves,
            rowCut: CloudDeckMath.ShapeCuts(), rowGain: CloudDeckMath.ShapeGains(),
            rowFrequencyU: CloudDeckMath.FrequenciesU(), rowFrequencyV: CloudDeckMath.FrequenciesV());

        Stopwatch watch = Stopwatch.StartNew();
        CloudVolumeMath.FillBlobVolume(
            volume, Size, Cells, CloudVolumeMath.VolumeLayers, seed: 20260810,
            octaves: CloudField.SheetOctaves,
            rowCut: CloudDeckMath.ShapeCuts(), rowGain: CloudDeckMath.ShapeGains(),
            rowFrequencyU: CloudDeckMath.FrequenciesU(), rowFrequencyV: CloudDeckMath.FrequenciesV());
        watch.Stop();

        // Per-deck vertical extent. D takes this as a real argument where A2 only documents the
        // intent, so its cirrus row is the flat sheet the deck table says it is.
        float[] peaks = CloudRaymarchMath.RowPeakTexels(Size / Cells, CloudDeckMath.DeckCount);

        Console.WriteLine();
        Console.WriteLine(
            $"variant D  {CloudRaymarchMath.ViewSteps} view x {CloudRaymarchMath.LightSteps} light " +
            $"= {CloudRaymarchMath.ViewSteps * CloudRaymarchMath.LightSteps} fetches per pixel; " +
            $"volume bake {watch.Elapsed.TotalMilliseconds:0} ms once");
        Console.WriteLine(
            $"deck peaks {peaks[0]:0.0}/{peaks[1]:0.0}/{peaks[2]:0.0} texels of a " +
            $"{Size / Cells}-texel blob");
        Console.WriteLine();
        Console.WriteLine("elev     marchMs   meanA_D  meanA_A2   sdLum_D  sdLum_A2");

        foreach (float elevation in elevations)
        {
            SkyColorTemperature.Rgb lit = SkyColorTemperature.SkyColorForElevation(
                elevation, pressureFraction: 1f, aerosolFraction: 0.1f,
                angstromExponent: 1.3f, inVacuum: false);
            SkyColorTemperature.Rgb hue = PurpleLightMath.ComposedHue(
                elevation, latitudeDegrees: 45f, pressureFraction: 1f, aerosolFraction: 0.1f,
                angstromExponent: 1.3f, inVacuum: false);
            SkyColorTemperature.Rgb shadow = new SkyColorTemperature.Rgb(
                hue.R * CloudVolumeMath.AmbientSkyFraction,
                hue.G * CloudVolumeMath.AmbientSkyFraction,
                hue.B * CloudVolumeMath.AmbientSkyFraction);

            // D is marched with a WHITE lit colour and the per-channel shadow/lit RATIO as its dark
            // colour, and the deck's real colour is multiplied in by the composite afterwards.
            //
            // That is not a shortcut — it is exactly what the shader does, and it is exact rather
            // than approximate because the integral is linear in the two colours it interpolates
            // between. Scaling both endpoints scales the result. The payoff is that one march serves
            // all three deck rows, each of which has its own illumination and its own hue, instead
            // of three marches; on the GPU it is why `_Color` stays a per-draw uniform.
            watch.Restart();
            CloudRaymarchMath.Render(
                marched, volume, Size, Cells, CloudVolumeMath.VolumeLayers, Supersample,
                Azimuth, elevation,
                litR: 1f, litG: 1f, litB: 1f,
                shadowR: ShadowRatio(lit.R, shadow.R),
                shadowG: ShadowRatio(lit.G, shadow.G),
                shadowB: ShadowRatio(lit.B, shadow.B),
                rowPeakTexels: null, inVacuum: false);
            watch.Stop();

            // The same march again with each deck standing at its OWN thickness, written as a
            // separate picture rather than folded into the one above.
            //
            // Two changes in one frame is not a comparison. D against A2 is a claim about the
            // RENDERER, so the head-to-head has to hold the geometry fixed at the single reference
            // height A2 uses — otherwise a reader cannot tell whether a sheerer cirrus row came from
            // marching the view ray or from finally giving cirrus its 300 m. This second render is
            // where that second change is on trial, on its own.
            CloudRaymarchMath.Render(
                marchedThick, volume, Size, Cells, CloudVolumeMath.VolumeLayers, Supersample,
                Azimuth, elevation,
                litR: 1f, litG: 1f, litB: 1f,
                shadowR: ShadowRatio(lit.R, shadow.R),
                shadowG: ShadowRatio(lit.G, shadow.G),
                shadowB: ShadowRatio(lit.B, shadow.B),
                rowPeakTexels: peaks, inVacuum: false);

            CloudVolumeMath.ShadeBlobVolume(
                volumeShaded, volume, Size, Cells, CloudVolumeMath.VolumeLayers, Azimuth, elevation,
                lit.R, lit.G, lit.B, shadow.R, shadow.G, shadow.B, strength: 1f, inVacuum: false);

            byte[] a2 = Upscale(
                Composite(volumeShaded, density, Size, Cells, elevation, lit, isolate: false,
                    decoupleAlpha: true, directFloor: DemoFloor, amplitude: DemoAmplitude,
                    alphaFromAtlas: true),
                Size, Supersample);

            // `Cells`, not `Cells * Supersample`: the supersampling multiplies the PIXELS in a blob,
            // not the number of blobs, and getting that wrong bands each blob with three decks'
            // illumination at once — which at sunset paints a lit cirrus row across an unlit cumulus
            // one and looks, very convincingly, like the effect working.
            byte[] d = CompositeRaymarch(
                marched, outSize, Cells, elevation, lit, DemoFloor, DemoAmplitude);

            string tag = elevation < 0f
                ? $"m{-elevation * 100f:0000}"
                : $"p{elevation * 100f:0000}";

            byte[] thick = CompositeRaymarch(
                marchedThick, outSize, Cells, elevation, lit, DemoFloor, DemoAmplitude);

            Png.Write($"{outputDir}/rm_{tag}_a2.png", outSize, outSize, a2);
            Png.Write($"{outputDir}/rm_{tag}_d.png", outSize, outSize, d);
            Png.Write($"{outputDir}/rm_{tag}_dt.png", outSize, outSize, thick);
            Png.Write($"{outputDir}/rm_{tag}_pair.png", outSize * 2 + 8, outSize, SideBySide(a2, d, outSize));

            // Internal contrast, not delta-E against a baseline. Raising a cloud's lit ceiling makes
            // the frame plainly better and LOWERS its delta-E against the flat version, because a
            // brighter material colour times a darker modulation nets out to a similar mean — so the
            // headline number moves the wrong way for the right change. The standard deviation of
            // luminance WITHIN the cloud is the thing being bought here, and the mean alpha is
            // printed beside it because contrast bought by drawing more cloud is not contrast.
            Console.WriteLine(
                $"{elevation,6:0.00}  {watch.Elapsed.TotalMilliseconds,8:0}  " +
                $"{MeanAlpha(marched):0.0000}  {MeanAlphaAtlas(volumeShaded):0.0000}   " +
                $"{CloudContrast(d, a2, outSize)}");
        }

        Console.WriteLine();
        Console.WriteLine($"raymarch stills   written to {outputDir}/rm_*.png ({outSize}x{outSize})");
    }

    // The shaded side's colour as a fraction of the lit side's, per channel — the same quantity
    // CloudVolumeMath.ChannelModulation computes, guarded the same way against a lit channel that
    // has gone to nearly zero, which §8's blue genuinely does at the horizon.
    private static float ShadowRatio(float lit, float shadow) =>
        lit <= 1e-4f ? 1f : Clamp01(shadow / lit);

    // Blends variant D's PREMULTIPLIED output over the same background Composite uses, scaled by the
    // same per-deck colour and sheet alpha.
    //
    // The blend is `dst * (1 - a) + rgb` rather than the usual lerp, because the march already
    // multiplied its colour by its own coverage. That is `Blend One OneMinusSrcAlpha` on the GPU,
    // and it is the reason this variant can put a rim brighter than the deck's own colour on screen:
    // the source term is not bounded by the destination it is being mixed toward.
    private static byte[] CompositeRaymarch(
        byte[] premultiplied, int size, int cells, float elevation, SkyColorTemperature.Rgb lit,
        float directFloor, float amplitude)
    {
        float glow = Clamp01((elevation + 6f) / 12f);
        glow = glow * glow * (3f - 2f * glow);

        float bgR = 0.05f + 0.30f * glow;
        float bgG = 0.04f + 0.28f * glow;
        float bgB = 0.03f + 0.24f * glow;

        int blobSize = size / cells;
        byte[] rgba = new byte[size * size * 4];

        for (int y = 0; y < size; y++)
        {
            int deck = Math.Min(y / blobSize, CloudDeckMath.DeckCount - 1);
            CloudDeckMath.Deck spec = CloudDeckMath.DeckAt(deck);

            float underlit = CloudSheetMath.UnderlitFraction(
                elevation, CloudDeckMath.ShadowEntryDegrees(deck));
            float ambientLevel = CloudSheetMath.SheetBrightness(glow);
            float directLevel = directFloor * Clamp01(underlit);
            float illumination = MathF.Max(ambientLevel, directLevel);

            float peak = MathF.Max(lit.R, MathF.Max(lit.G, lit.B));
            float warmR = peak <= 0f ? 1f : lit.R / peak;
            float warmG = peak <= 0f ? 1f : lit.G / peak;
            float warmB = peak <= 0f ? 1f : lit.B / peak;

            float matR = (0.86f + (warmR - 0.86f) * underlit) * illumination;
            float matG = (0.87f + (warmG - 0.87f) * underlit) * illumination;
            float matB = (0.90f + (warmB - 0.90f) * underlit) * illumination;

            // Opacity is a fact about the cloud, not about the light on it — the decoupled term the
            // §25c film uses, so the two renders are calibrated alike.
            float ambient = CloudSheetMath.SheetBrightness(glow);
            float sheetAlpha = Clamp01(amplitude * spec.Opacity * MathF.Max(ambient, underlit));

            for (int x = 0; x < size; x++)
            {
                int o = (y * size + x) * 4;

                float a = (premultiplied[o + 3] / 255f) * sheetAlpha;
                float r = (premultiplied[o + 0] / 255f) * matR * sheetAlpha;
                float g = (premultiplied[o + 1] / 255f) * matG * sheetAlpha;
                float b = (premultiplied[o + 2] / 255f) * matB * sheetAlpha;

                rgba[o + 0] = ToByte(bgR * (1f - a) + r);
                rgba[o + 1] = ToByte(bgG * (1f - a) + g);
                rgba[o + 2] = ToByte(bgB * (1f - a) + b);
                rgba[o + 3] = 255;
            }
        }

        return rgba;
    }

    // Bilinear magnification, which is what the GPU does to A2's baked atlas on the way to the
    // screen. Point sampling here would flatter D by giving A2 visible texel edges it does not
    // actually have in game.
    private static byte[] Upscale(byte[] src, int srcSize, int factor)
    {
        int size = srcSize * factor;
        byte[] dst = new byte[size * size * 4];

        for (int y = 0; y < size; y++)
        {
            float sy = (y + 0.5f) / factor - 0.5f;
            int y0 = Math.Clamp((int)MathF.Floor(sy), 0, srcSize - 1);
            int y1 = Math.Clamp(y0 + 1, 0, srcSize - 1);
            float fy = Clamp01(sy - y0);

            for (int x = 0; x < size; x++)
            {
                float sx = (x + 0.5f) / factor - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(sx), 0, srcSize - 1);
                int x1 = Math.Clamp(x0 + 1, 0, srcSize - 1);
                float fx = Clamp01(sx - x0);

                int o = (y * size + x) * 4;
                for (int c = 0; c < 4; c++)
                {
                    float top = src[(y0 * srcSize + x0) * 4 + c] + fx *
                        (src[(y0 * srcSize + x1) * 4 + c] - src[(y0 * srcSize + x0) * 4 + c]);
                    float bottom = src[(y1 * srcSize + x0) * 4 + c] + fx *
                        (src[(y1 * srcSize + x1) * 4 + c] - src[(y1 * srcSize + x0) * 4 + c]);
                    dst[o + c] = (byte)Math.Clamp((int)(top + fy * (bottom - top) + 0.5f), 0, 255);
                }
            }
        }

        return dst;
    }

    // Two square images in one frame with a gap between them, so the comparison is side by side
    // rather than two files a reader has to alt-tab between.
    private static byte[] SideBySide(byte[] left, byte[] right, int size)
    {
        const int Gap = 8;
        int width = size * 2 + Gap;
        byte[] rgba = new byte[width * size * 4];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                bool inLeft = x < size;
                bool inRight = x >= size + Gap;

                if (inLeft || inRight)
                {
                    byte[] src = inLeft ? left : right;
                    int sx = inLeft ? x : x - size - Gap;
                    int so = (y * size + sx) * 4;
                    rgba[o + 0] = src[so + 0];
                    rgba[o + 1] = src[so + 1];
                    rgba[o + 2] = src[so + 2];
                }

                rgba[o + 3] = 255;
            }
        }

        return rgba;
    }

    private static float MeanAlpha(byte[] rgba)
    {
        double total = 0;
        int count = rgba.Length / 4;
        for (int i = 0; i < count; i++)
            total += rgba[i * 4 + 3];

        return (float)(total / count / 255.0);
    }

    private static float MeanAlphaAtlas(byte[] rgba) => MeanAlpha(rgba);

    // Standard deviation of luminance over the pixels the cloud actually covers, for both renders,
    // reported as "D  A2". Restricted to covered pixels because a frame is mostly background and the
    // background's own gradient would otherwise dominate the number being compared.
    private static string CloudContrast(byte[] d, byte[] a2, int size)
    {
        return $"{Sd(d):0.000}     {Sd(a2):0.000}";

        float Sd(byte[] image)
        {
            double sum = 0;
            double sumSq = 0;
            int count = 0;

            for (int i = 0; i < size * size; i++)
            {
                int o = i * 4;
                double luminance =
                    0.2126 * image[o] + 0.7152 * image[o + 1] + 0.0722 * image[o + 2];

                // Everything above the darkest background this frame can show. The background is a
                // flat colour per frame, so anything brighter than it by a whisker is cloud.
                if (luminance > BackgroundLuminance(image, size) + 1.5)
                {
                    sum += luminance;
                    sumSq += luminance * luminance;
                    count++;
                }
            }

            if (count == 0)
                return 0f;

            double mean = sum / count;
            return (float)Math.Sqrt(Math.Max(0, sumSq / count - mean * mean));
        }
    }

    // The frame's background luminance, read from its top-left corner — which is outside every blob
    // in this 3x3 layout by construction, since the blobs are radially faded to nothing at their
    // cell corners.
    private static double BackgroundLuminance(byte[] image, int size) =>
        0.2126 * image[0] + 0.7152 * image[1] + 0.0722 * image[2];
}
