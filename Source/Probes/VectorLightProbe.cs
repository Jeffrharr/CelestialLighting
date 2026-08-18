using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// §27's live measurements, for the harness.
//
// NUMERIC, NOT PIXELS — and issue #3 is the reason that is stated rather than assumed. Measuring an
// effect like this from the frame has already produced two confident wrong answers in this repo: a
// brightness centroid dragged sideways by a colonist standing near the opening "proved" an inverted
// sign, and a profile across a beam "showed" three brightness bands that were really two beams
// projected onto one axis. A shadow's area is not something a centroid can be talked out of.
//
// RECOMPUTED FROM THE PURE CORE, NOT READ OFF THE RENDER. The probe runs the same
// VectorLightBlockers -> VectorLightMath path the draw runs, rather than reporting the cached mesh.
// Two reasons: a light outside the camera is never baked, so reading the render would report zero for
// half the colony depending on where it was looking; and asking the same functions the renderer asks
// is the repo's convention for probes (see EaveCellProbe), so a probe can never agree with a formula
// the screen is not using.
//
// DELIBERATELY NOT GATED ON THE FEATURE FLAG. It reports what the map contains, not what is switched
// on — which is what lets an A/B distinguish "the two frames match because the toggle did nothing"
// from "because this scene has no wall to cast anything".
public sealed class VectorLightProbe : IProbe
{
    public enum Metric
    {
        // How many emitters the map has. A zero here explains every other number below and is the
        // first thing to check when a scenario measures nothing.
        Count,

        // Total lit area, in square cells, summed over every emitter.
        LitArea,

        // The share of what each light COULD reach that is in shadow, weighted by area. This is the
        // number that says the walls are doing something: it is 0 on open ground with no obstruction
        // whatever the lights are doing, and rises as geometry blocks them.
        ShadowFraction,

        // Total fan vertices across every emitter — the mesh cost, and the thing that moves if the
        // ray budget or the corner-ray rule ever changes.
        //
        // FAN vertices, and only those: this is recomputed from the polygon and never builds a mesh,
        // so §27's penumbra wedges are invisible to it. That is not an oversight, but it did read as
        // one once — the first soft-edge A/B reported this metric identical in both arms and looked
        // like a flag that was not reaching the renderer. PenumbraArea is the metric for that.
        Vertices,

        // Total area of the penumbra wedges, in square cells, summed over every emitter — §27 phase
        // 2's soft shadow edges.
        //
        // THE ONE METRIC HERE THAT DOES READ THE FEATURE FLAG, against the file's convention above,
        // and deliberately. The others answer "what does this map contain", where gating would
        // destroy an A/B's ability to tell a dead toggle from an empty scene. This one answers a
        // different question — "how much soft edge is being DRAWN" — for which the flag is not
        // context but the subject. It reads 0 with vector_light_penumbra off and the measured area
        // with it on, so a flag that stopped reaching the mesh builder fails a scenario rather than
        // quietly producing two identical frames.
        PenumbraArea,
    }

    private readonly Metric metric;

    public string Name { get; }

    public VectorLightProbe(string name, Metric metric)
    {
        Name = name;
        this.metric = metric;
    }

    public float Read(Map map)
    {
        if (map == null)
            return 0f;

        float litArea = 0f;
        float openArea = 0f;
        float penumbraArea = 0f;
        int vertices = 0;
        int count = 0;

        foreach (VectorLightField.LightEntry entry in VectorLightField.LightsFor(map))
        {
            count++;
            Accumulate(map, entry, ref litArea, ref openArea, ref vertices, ref penumbraArea);
        }

        return Report(count, litArea, openArea, vertices, penumbraArea);
    }

    private static void Accumulate(
        Map map, VectorLightField.LightEntry entry, ref float litArea, ref float openArea,
        ref int vertices, ref float penumbraArea)
    {
        VectorLightMath.Segment[] segments =
            VectorLightBlockers.SegmentsAround(map, entry.Cell, entry.Radius);

        VectorLightMath.LightPolygon polygon = VectorLightMath.Build(
            entry.Cell.x + 0.5f, entry.Cell.z + 0.5f, entry.Radius, segments,
            VectorLightMath.DefaultBaseRayCount);

        litArea += Area(polygon);
        vertices += polygon.Count + 1;
        penumbraArea += WedgeArea(map, entry, polygon);

        // The unobstructed comparison is the polygon the same ray budget would produce with nothing in
        // the way — the inscribed 48-gon, not the true circle. Comparing against pi*r^2 instead would
        // report a standing 0.1% shadow on completely open ground, which is a floor that would have to
        // be remembered and subtracted every time anyone read this number.
        openArea += 0.5f * VectorLightMath.DefaultBaseRayCount * entry.Radius * entry.Radius
            * (float)System.Math.Sin(2.0 * System.Math.PI / VectorLightMath.DefaultBaseRayCount);
    }

    private float Report(int count, float litArea, float openArea, int vertices, float penumbraArea)
    {
        switch (metric)
        {
            case Metric.Count:
                return count;
            case Metric.LitArea:
                return litArea;
            case Metric.Vertices:
                return vertices;
            case Metric.PenumbraArea:
                return penumbraArea;
            default:
                return openArea <= 0f ? 0f : 1f - litArea / openArea;
        }
    }

    // The area the soft edges cover, taken from the mesh the renderer would build for this light with
    // the flag in the state it is actually in — so the source radius comes from the same expression
    // VectorLightOverlay.Rebuild uses, not from a constant this file picked.
    //
    // Summed over the triangles PAST FanTriangleCount, which is the only place the wedges are: the fan
    // tiles the visibility polygon exactly once and the wedges lie outside it, in the shadow. Summing
    // the whole index buffer would report the lit area plus the soft edges and would be dominated by
    // the former, which is the number LitArea already gives.
    private static float WedgeArea(
        Map map, VectorLightField.LightEntry entry, VectorLightMath.LightPolygon polygon)
    {
        float sourceRadius = CelestialLightingFeatures.VectorLightPenumbra
            ? VectorLightMath.DefaultSourceRadius
            : 0f;

        VectorLightMath.LightMesh mesh = VectorLightMath.BuildMesh(
            entry.Cell.x + 0.5f, entry.Cell.z + 0.5f, entry.Radius, polygon, sourceRadius);

        float total = 0f;

        for (int t = mesh.FanTriangleCount; t < mesh.Triangles.Length; t += 3)
        {
            int a = mesh.Triangles[t];
            int b = mesh.Triangles[t + 1];
            int c = mesh.Triangles[t + 2];

            total += 0.5f * System.Math.Abs(
                (mesh.X[b] - mesh.X[a]) * (mesh.Z[c] - mesh.Z[a])
                - (mesh.X[c] - mesh.X[a]) * (mesh.Z[b] - mesh.Z[a]));
        }

        return total;
    }

    private static float Area(VectorLightMath.LightPolygon polygon)
    {
        float total = 0f;

        for (int i = 0; i < polygon.Count; i++)
        {
            int next = (i + 1) % polygon.Count;

            double ax = System.Math.Cos(polygon.Angles[i]) * polygon.Distances[i];
            double az = System.Math.Sin(polygon.Angles[i]) * polygon.Distances[i];
            double bx = System.Math.Cos(polygon.Angles[next]) * polygon.Distances[next];
            double bz = System.Math.Sin(polygon.Angles[next]) * polygon.Distances[next];

            total += (float)System.Math.Abs(0.5 * (ax * bz - bx * az));
        }

        return total;
    }
}
