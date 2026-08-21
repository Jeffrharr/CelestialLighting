using System;
using System.Collections.Generic;

namespace CelestialLighting.Tests;

// An ORACLE, not a helper: Verse.Glow.ComputeGlowGridsJob transcribed line for line from the
// decompiled source, sharing no code whatsoever with the §27 files it judges.
//
// WHY THIS FILE EXISTS AT ALL, given how much of it looks like duplication. Every claim §27's
// composition makes is a claim about a DIFFERENCE against what vanilla put in a cell, and a test
// that computes both halves with the code under test is asserting `x - x == 0`, which passes for a
// formula that is wrong in the same way on both sides. That exact test existed in an earlier attempt
// at the max and passed while the term it guarded lifted every room in the game by a fifth. So the
// numbers are checked against an actual flood over an actual lattice: this file runs the Dijkstra,
// and the assertions are that our arithmetic lands on the same bytes it does.
//
// WHAT IT IS USED FOR HERE. VectorLightSaturationMathTests rings lamps around a one-cell wall column
// and asks whether the shadow behind the column gets deeper as lamps are added. Vanilla is the
// oracle for that — its flood is unambiguously monotone in emitter count — and "vanilla" has to mean
// vanilla's own arithmetic rather than our restatement of it, or the test proves only that we are
// self-consistent. Recovered from the abandoned phase 3b branch (894796b), which built it for the
// same reason and never merged.
//
// FAITHFUL, INCLUDING THE PARTS THAT LOOK LIKE BUGS. The diagonal-blocker bits are a single 8-bit
// array that vanilla clears once per LIGHT and not once per popped cell, and three of the four
// `continue`s in its neighbour loop skip the write to that array — so a stale flank bit from the
// previously popped cell can veto a diagonal step. Tidying that up here would make the oracle
// disagree with the game rather than with our code, which is the one failure mode an oracle cannot
// have.
public static class VanillaGlowFlood
{
    private const int CardinalCost = 100;
    private const int DiagonalCost = 141;

    // ComputeGlowGridsJob.Directions, in its order: four cardinals, then four diagonals. The order is
    // load-bearing — `(i < 4) ? 100 : 141` charges by it, and cases 4-7 of the switch name bits 0-3
    // by it.
    private static readonly (int dx, int dz)[] Directions =
    {
        (0, -1), (1, 0), (0, 1), (-1, 0),
        (1, -1), (1, 1), (-1, 1), (-1, -1),
    };

    private enum Status
    {
        Unvisited,
        Open,
        Finalized,
    }

    private struct Cell
    {
        public int IntDist;
        public Status Status;
    }

    // One emitter's local glow area, exactly as the job fills it: `diameter x diameter` Color32,
    // memcleared, indexed from the emitter's own cell at the centre.
    public sealed class Result
    {
        public readonly int Radius;
        public readonly int Diameter;
        public readonly int[] R;
        public readonly int[] G;
        public readonly int[] B;
        public readonly int[] IntDist;

        public Result(int radius)
        {
            Radius = radius;
            Diameter = radius * 2 + 1;
            R = new int[Diameter * Diameter];
            G = new int[Diameter * Diameter];
            B = new int[Diameter * Diameter];
            IntDist = new int[Diameter * Diameter];
        }

        public int Index(int dx, int dz) => (dz + Radius) * Diameter + (dx + Radius);

        public bool InRange(int dx, int dz) =>
            dx >= -Radius && dx <= Radius && dz >= -Radius && dz <= Radius;
    }

    // `blocked` is indexed the same way as the result: local deltas from the emitter, true where an
    // edifice blocks light. The emitter's own cell is never consulted, matching the job — its flood
    // starts there unconditionally.
    public static Result Flood(int colourR, int colourG, int colourB, float glowRadius, Func<int, int, bool> blocked)
    {
        // GlowLight's constructor: `radius = Mathf.CeilToInt(glowRadius)`, `diameter = CeilToInt(
        // glowRadius * 2 + 1)`. The diameter is only ever used to size and index the local array, so
        // a symmetric radius*2+1 addresses the same cells for every radius the game actually hands
        // out; anything wider would only add cells the radius cut below rejects anyway.
        int radius = (int)Math.Ceiling(glowRadius);
        Result result = new Result(radius);

        int span = radius * 2 + 1;
        Cell[] area = new Cell[span * span];
        bool[] flankBlocked = new bool[8];

        int seed = Index(0, 0, radius, span);
        area[seed].IntDist = 100;

        // A list standing in for UnsafeHeap: pop the smallest intDist each time, which is what the
        // heap's comparer does. Slower and clearer, which is the right trade for an oracle.
        List<(int dx, int dz)> queue = new List<(int, int)> { (0, 0) };

        int ceiling = (int)Math.Round((double)(glowRadius * 100f));

        while (queue.Count > 0)
        {
            int best = 0;

            for (int i = 1; i < queue.Count; i++)
            {
                int here = Index(queue[i].dx, queue[i].dz, radius, span);
                int there = Index(queue[best].dx, queue[best].dz, radius, span);

                if (area[here].IntDist < area[there].IntDist)
                    best = i;
            }

            (int dx, int dz) = queue[best];
            queue.RemoveAt(best);

            int index = Index(dx, dz, radius, span);
            area[index].Status = Status.Finalized;

            SetGlowFromDist(result, area[index].IntDist, dx, dz, colourR, colourG, colourB, glowRadius);

            for (int i = 0; i < Directions.Length; i++)
            {
                int nx = dx + Directions[i].dx;
                int nz = dz + Directions[i].dz;

                // `num3 < 0 || num3 >= num2` — off the local array. Note it does NOT write
                // flankBlocked[i], which is the stale-bit behaviour named in the header.
                if (nx < -radius || nx > radius || nz < -radius || nz > radius)
                    continue;

                int neighbour = Index(nx, nz, radius, span);

                if (area[neighbour].Status == Status.Finalized)
                    continue;

                bool flag = blocked(nx, nz);
                flankBlocked[i] = flag;

                if (flag)
                    continue;

                int step = i < 4 ? CardinalCost : DiagonalCost;
                int candidate = area[index].IntDist + step;

                if (candidate > ceiling)
                    continue;

                // The diagonal rule: a diagonal is refused only when BOTH of its flanking cardinals
                // are blockers, which is the leak §27 exists to close.
                if (i == 4 && flankBlocked[0] && flankBlocked[1])
                    continue;

                if (i == 5 && flankBlocked[1] && flankBlocked[2])
                    continue;

                if (i == 6 && flankBlocked[2] && flankBlocked[3])
                    continue;

                if (i == 7 && flankBlocked[0] && flankBlocked[3])
                    continue;

                if (area[neighbour].Status == Status.Unvisited)
                {
                    area[neighbour].IntDist = int.MaxValue;
                    area[neighbour].Status = Status.Open;
                }

                if (candidate < area[neighbour].IntDist)
                {
                    area[neighbour].IntDist = candidate;
                    area[neighbour].Status = Status.Open;
                    queue.Add((nx, nz));
                }
            }
        }

        return result;
    }

    private static void SetGlowFromDist(
        Result result, int intDist, int dx, int dz, int colourR, int colourG, int colourB, float glowRadius)
    {
        float num = -1f / glowRadius;
        float num2 = intDist / 100f;

        int r = 0;
        int g = 0;
        int b = 0;

        if (num2 <= glowRadius)
        {
            float invSq = 1f / (num2 * num2);
            float num3 = Lerp(1f + num * num2, invSq, 0.4f);

            r = (int)(colourR * num3);
            g = (int)(colourG * num3);
            b = (int)(colourB * num3);
        }

        if (r <= 0 && g <= 0 && b <= 0)
            return;

        r = Math.Max(r, 0);
        g = Math.Max(g, 0);
        b = Math.Max(b, 0);

        Project(r, g, b, out int outR, out int outG, out int outB);

        int index = result.Index(dx, dz);
        result.R[index] = outR;
        result.G[index] = outG;
        result.B[index] = outB;
        result.IntDist[index] = intDist;
    }

    // Mathf.Lerp: `a + (b - a) * Clamp01(t)`.
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    // ColorInt.ProjectToColor32Fast, RGB half.
    private static void Project(int r, int g, int b, out int outR, out int outG, out int outB)
    {
        int max = Math.Max(r, Math.Max(g, b));

        if (max > 255)
        {
            outR = r * 255 / max;
            outG = g * 255 / max;
            outB = b * 255 / max;
            return;
        }

        outR = r;
        outG = g;
        outB = b;
    }

    private static int Index(int dx, int dz, int radius, int span) => (dz + radius) * span + (dx + radius);
}
