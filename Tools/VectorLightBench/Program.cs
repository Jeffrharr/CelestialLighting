using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CelestialLighting.Tools;

// Where a vector-light bake's time actually goes, decomposed by stage and swept across how much wall
// the emitter can see.
//
// WHY THIS AND NOT VectorLightPreview's TimeBake. That timer reports ONE number for the whole bake,
// over three hand-authored look-dev scenes that between them contain about eight silhouette
// segments. Both halves of that are wrong for a performance question. A single number cannot say
// which stage to optimise, and eight segments is not the population the game bakes against — a lamp
// in a built colony sees furniture, interior partitions and door frames, and the visibility polygon
// is QUADRATIC in what it sees: every ray is tested against every segment, and each segment adds
// six more rays. Measuring the cost on a clean scene and extrapolating linearly understates a
// cluttered one by an order of magnitude, which is exactly the kind of measurement that reports a
// subsystem as free.
//
// WHAT THE NUMBERS ARE WORTH. This is net8.0 on the desktop CLR and the game is Mono: the repo has
// already measured 295 ms here landing as 1221 ms in game. Treat every absolute below as a SHAPE —
// which stage dominates, and how each one grows with clutter — and take the cost itself from the
// live Circinus run. A ratio between two builds transfers; a millisecond does not.
//
//   dotnet run --project Tools/VectorLightBench -c Release
public static class Program
{
    // Enough repeats that the stopwatch is not the measurement, few enough that the whole sweep
    // stays under a few seconds. The cluttered scenes are ~100x the cheap ones, so the count is
    // scaled per stage rather than fixed.
    private const int TargetMs = 120;

    // Long enough for tier-1 promotion (the runtime's threshold is 30 calls plus a background
    // compile) on even the most expensive stage in the sweep.
    private const int WarmupMs = 60;

    // How many times the A/B alternates per scene, and the statistic taken over them.
    //
    // MINIMUM, NOT MEAN, and it is not cherry-picking. This box carries a load average in the teens
    // from other agents, and the mean of a contended sample measures the contention: an early cut of
    // this table put the same brute-force arm at 2.23 ms on one pass and 7.40 ms on the next. Noise
    // on a benchmark is one-sided — the scheduler can only ever add time — so the fastest round is
    // the closest thing to the uncontended cost, and it is what both arms are judged on equally.
    private const int Rounds = 5;

    public static int Main(string[] args)
    {
        Console.WriteLine(
            $"{"scene",-14}{"segs",6}{"rays",6}{"brute",9}{"build",9}{"gain",8}" +
            $"{"silh",8}{"cover",8}{"mesh",8}{"total",8}");
        Console.WriteLine(new string('-', 85));

        // Swept TWICE, and the second pass is the one to read. It is not paranoia: the first cut of
        // this tool reported the silhouette pass costing more on an empty window than a cluttered
        // one, and a repeated sweep is what separates "this stage really is slower here" from "this
        // stage ran first". Two passes that disagree mean the timer is still measuring itself.
        int passes = args.Length > 0 && args[0] == "--once" ? 1 : 2;

        for (int pass = 0; pass < passes; pass++)
        {
            if (pass > 0)
                Console.WriteLine(new string('-', 85));

            foreach (Scene scene in Scenes())
                Report(scene);
        }

        return 0;
    }

    // A clutter spectrum, not a look-dev set. Each scene puts one radius-14 lamp — vanilla's
    // standing-lamp reach, and the largest radius the common lights use — in a progressively busier
    // room, so the sweep isolates the one variable the bake is quadratic in.
    private static IEnumerable<Scene> Scenes()
    {
        // The floor: nothing in range at all. Whatever this costs is what every outdoor lamp on the
        // map pays, and it is the number a whole-map rebake multiplies by.
        yield return Empty("open", 14f);

        // One room around the lamp. The common indoor case and the cheapest realistic one: four
        // walls merge into four long runs, so the silhouette is tiny however many cells it spans.
        yield return Room("room", 14f);

        // The scene every §27 screenshot is shot in — a room with a doorway — plus the interior
        // partition a real base has on the other side of it.
        yield return Rooms("rooms", 14f);

        // A built colony: rooms on a 7-cell grid, which is roughly what a player's bedroom block
        // looks like. This is the population the bake actually runs against in a mature save.
        yield return Grid("colony", 14f, 7);

        // Tighter rooms. Not a worst case anyone builds on purpose, but stockpiles, workshops and
        // hospital blocks get here, and it shows the growth curve rather than one point on it.
        yield return Grid("colony-tight", 14f, 5);

        // Mid clutter, and it exists to interrogate the index's own threshold rather than to model a
        // room anyone builds. The gain is worthless if the cutoff sits above the segment counts real
        // scenes produce, so the sweep needs points between "a room" (8) and "a colony" (224).
        yield return Grid("mid", 14f, 13);
        yield return Pillars("mid-pillars", 14f, 6);

        // Free-standing clutter: one blocker every third cell, no runs to merge. The adversarial
        // case for a silhouette that pays for merged runs, and the ceiling on segment count.
        yield return Pillars("pillars", 14f, 3);

        // Same clutter, half the reach. Radius is the other axis the cost moves on and the one a
        // torch or a campfire lives at, so the sweep would be misread without it.
        yield return Pillars("pillars-r7", 7f, 3);
    }

    private static void Report(Scene scene)
    {
        // Every stage is timed on the SAME inputs the previous stage produced, in the order the game
        // runs them, so the columns sum to a bake rather than to four independent measurements.
        VectorLightMath.Segment[] segments = null;
        double silh = Time(() => segments = Silhouette(scene));

        // The A/B, INTERLEAVED rather than run as two blocks. This machine routinely carries a load
        // average in the teens from other agents, and a swept-then-swept comparison attributes
        // whatever the machine happened to be doing to whichever arm held the clock at the time.
        // Alternating inside one process makes both arms pay the same interference; the sky-falloff
        // hoist was measured this way and for this reason.
        VectorLightMath.LightPolygon polygon = default;
        VectorLightMath.LightPolygon reference = default;

        double build = double.MaxValue;
        double brute = double.MaxValue;

        for (int round = 0; round < Rounds; round++)
        {
            build = Math.Min(build, Time(() => polygon = VectorLightMath.Build(
                scene.LightX, scene.LightZ, scene.Radius, segments,
                VectorLightMath.DefaultBaseRayCount)));

            brute = Math.Min(brute, Time(() => reference = VectorLightBuildOracle.Build(
                scene.LightX, scene.LightZ, scene.Radius, segments,
                VectorLightMath.DefaultBaseRayCount)));
        }

        int radiusCells = (int)Math.Ceiling(scene.Radius);
        double cover = Time(() => VectorLightMath.BuildCoverage(
            polygon, (int)scene.LightX, (int)scene.LightZ, radiusCells,
            VectorLightMath.DefaultCoverageSamples));

        double mesh = Time(() => VectorLightMath.BuildMesh(
            scene.LightX, scene.LightZ, scene.Radius, polygon, VectorLightMath.DefaultSourceRadius));

        // Equality is asserted HERE as well as in the unit test, because a benchmark that quietly
        // times a wrong answer is the classic way an optimisation reports a win it never earned.
        string verdict = Identical(polygon, reference) ? "" : "   MISMATCH";

        Console.WriteLine(
            $"{scene.Name,-14}{segments.Length,6}{polygon.Count,6}" +
            $"{brute,9:F3}{build,9:F3}{brute / Math.Max(build, 1e-9),7:F2}x" +
            $"{silh,8:F3}{cover,8:F3}{mesh,8:F3}" +
            $"{silh + build + cover + mesh,8:F3}{verdict}");
    }

    // Bit-for-bit, not within a tolerance: the cull is meant to remove work rather than change an
    // answer, so any difference at all is a defect and not a rounding budget.
    private static bool Identical(VectorLightMath.LightPolygon a, VectorLightMath.LightPolygon b)
    {
        if (a.Count != b.Count)
            return false;

        for (int i = 0; i < a.Count; i++)
        {
            if (a.Angles[i] != b.Angles[i] || a.Distances[i] != b.Distances[i])
                return false;
        }

        return true;
    }

    // Warm for a fixed WALL-CLOCK window before timing anything, then measure for another.
    //
    // WHY NOT A PILOT CALL. The obvious version — call once, time once, divide TargetMs by that to
    // pick a repeat count — was the first cut here and it produced a table that read backwards: the
    // silhouette pass cost 0.092 ms on an EMPTY window and 0.013 ms on a cluttered one, which is the
    // opposite of the work done. Tiered JIT is why. A first call runs in tier 0, so the pilot
    // over-reads, so the repeat count comes out small, so the whole measurement also finishes in
    // tier 0 and never reaches optimised code. Cheap stages are hit hardest, which is precisely
    // where an optimisation is about to be judged. Warming on a clock rather than a count means the
    // promotion has happened before the first timed iteration whatever the stage costs.
    private static double Time(Action action)
    {
        Stopwatch warm = Stopwatch.StartNew();
        while (warm.Elapsed.TotalMilliseconds < WarmupMs)
            action();

        int repeats = 0;
        Stopwatch watch = Stopwatch.StartNew();

        while (watch.Elapsed.TotalMilliseconds < TargetMs)
        {
            action();
            repeats++;
        }

        return watch.Elapsed.TotalMilliseconds / repeats;
    }

    // The extraction the game adapter does, minus the live-state reads: blockers within the light's
    // reach padded by a cell, in world coordinates. Mirrors VectorLightBlockers.SegmentsAround so the
    // window size and the origin offset are the ones the game pays for.
    private static VectorLightMath.Segment[] Silhouette(Scene scene)
    {
        int pad = (int)Math.Ceiling(scene.Radius) + 1;
        int minX = Math.Max((int)scene.LightX - pad, 0);
        int minZ = Math.Max((int)scene.LightZ - pad, 0);
        int maxX = Math.Min((int)scene.LightX + pad, scene.Width - 1);
        int maxZ = Math.Min((int)scene.LightZ + pad, scene.Height - 1);

        int w = maxX - minX + 1;
        int h = maxZ - minZ + 1;
        bool[] blocked = new bool[w * h];

        for (int z = 0; z < h; z++)
        {
            for (int x = 0; x < w; x++)
                blocked[z * w + x] = scene.BlockedAt(minX + x, minZ + z);
        }

        return VectorLightMath.SilhouetteSegments(blocked, w, h, minX, minZ);
    }

    // ---- scenes ------------------------------------------------------------------------------

    private const int Size = 80;
    private const int Centre = 40;

    private static Scene Empty(string name, float radius) => new Scene(name, radius);

    private static Scene Room(string name, float radius)
    {
        Scene scene = new Scene(name, radius);
        scene.Rect(Centre - 9, Centre - 7, Centre + 9, Centre + 7, hollow: true);
        return scene;
    }

    private static Scene Rooms(string name, float radius)
    {
        Scene scene = Room(name, radius);
        scene.Clear(Centre + 9, Centre, Centre + 9, Centre);
        scene.Rect(Centre + 9, Centre - 7, Centre + 24, Centre + 7, hollow: true);
        scene.Rect(Centre + 14, Centre - 7, Centre + 14, Centre + 2, hollow: false);
        return scene;
    }

    // Rooms on a repeating grid, walls shared. `pitch` is the room's outer size, so 7 is a 5x5
    // interior — a vanilla bedroom.
    private static Scene Grid(string name, float radius, int pitch)
    {
        Scene scene = new Scene(name, radius);

        for (int i = 0; i * pitch < Size; i++)
        {
            for (int x = 0; x < Size; x++)
                scene.Set(x, i * pitch);

            for (int z = 0; z < Size; z++)
                scene.Set(i * pitch, z);
        }

        // A doorway per wall span, because a colony without doors is a solid grid and every lamp
        // would see exactly its own four walls. The gaps are what let a light reach the next ring of
        // rooms, and reaching further is what puts more segments in the window.
        for (int i = 0; i * pitch < Size; i++)
        {
            for (int j = 0; j * pitch < Size; j++)
            {
                scene.Clear(i * pitch, j * pitch + pitch / 2, i * pitch, j * pitch + pitch / 2);
                scene.Clear(i * pitch + pitch / 2, j * pitch, i * pitch + pitch / 2, j * pitch);
            }
        }

        scene.Clear((int)scene.LightX, (int)scene.LightZ, (int)scene.LightX, (int)scene.LightZ);
        return scene;
    }

    private static Scene Pillars(string name, float radius, int pitch)
    {
        Scene scene = new Scene(name, radius);

        for (int z = 0; z < Size; z += pitch)
        {
            for (int x = 0; x < Size; x += pitch)
                scene.Set(x, z);
        }

        scene.Clear((int)scene.LightX, (int)scene.LightZ, (int)scene.LightX, (int)scene.LightZ);
        return scene;
    }

    private sealed class Scene
    {
        public readonly string Name;
        public readonly float Radius;
        public readonly int Width = Size;
        public readonly int Height = Size;
        public readonly float LightX = Centre + 0.5f;
        public readonly float LightZ = Centre + 0.5f;

        private readonly bool[] blocked = new bool[Size * Size];

        public Scene(string name, float radius)
        {
            Name = name;
            Radius = radius;
        }

        public bool BlockedAt(int x, int z)
        {
            bool inside = x >= 0 && x < Width && z >= 0 && z < Height;
            return inside && blocked[z * Width + x];
        }

        public void Set(int x, int z)
        {
            if (x >= 0 && x < Width && z >= 0 && z < Height)
                blocked[z * Width + x] = true;
        }

        public void Clear(int x0, int z0, int x1, int z1)
        {
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (x >= 0 && x < Width && z >= 0 && z < Height)
                        blocked[z * Width + x] = false;
                }
            }
        }

        public void Rect(int x0, int z0, int x1, int z1, bool hollow)
        {
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    bool edge = x == x0 || x == x1 || z == z0 || z == z1;

                    if (!hollow || edge)
                        Set(x, z);
                }
            }
        }
    }
}
