using RimWorldTestHarness.Mod.Probes;
using UnityEngine;
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

        // Whether §27 phase 2b's shader actually loaded and compiled: 1 if the max composition can
        // be drawn, 0 if the subsystem has fallen back to the crossfade.
        //
        // THIS IS THE ONE THAT CATCHES THE SILENT FAILURE. A missing or unloadable AssetBundle is
        // not an error — by design, because a mod with no shader must still have light — so every
        // other number in a max scenario stays perfectly healthy while the frames quietly show the
        // crossfade. Pin this at 1 in any arm that claims to be measuring the max, and a bundle that
        // did not ship fails the scenario instead of producing a plausible wrong measurement.
        MaxAvailable,

        // The mean of vanilla's own delivered glow, per mesh vertex, over every emitter — the
        // quantity the fragment program subtracts. Read alongside MaxExcess: a zero here means the
        // samples are not arriving, which would make the max composition silently identical to the
        // unsuppressed hard-edged arm.
        VanillaSample,

        // The mean excess our straight-line geometry delivers over vanilla's geodesic flood, per
        // mesh vertex — i.e. what the max composition actually ADDS, in vanilla's own glow units.
        //
        // THE NUMBER THAT SAYS THE FEATURE IS NOT A NO-OP. The standing objection to a max is that
        // our lit region is a subset of vanilla's, so the max is just vanilla again; a zero here
        // would mean exactly that, and would be grounds to drop the feature rather than debug it.
        // Above zero says our polygon is reaching cells vanilla's flood only reached the long way
        // round, which is the whole of §27's thesis expressed as one number.
        MaxExcess,
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

        if (metric == Metric.MaxAvailable)
            return VectorLightShader.Available ? 1f : 0f;

        if (metric == Metric.VanillaSample || metric == Metric.MaxExcess)
            return Composition(map);

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

    // Vanilla's glow, and our excess over it, averaged across every fan vertex of every emitter.
    //
    // RECOMPUTED RATHER THAN READ OFF THE UPLOADED UV1, per this file's convention and for its usual
    // reason: an off-screen light has no mesh, so reading the render would report zero for half the
    // colony depending on where the camera happened to be pointing. It walks the same pure functions
    // VectorLightOverlay.UploadVanillaSamples walks — same mesh builder, same half-cell pull, same
    // glow lookup — so it cannot agree with a composition the screen is not drawing.
    //
    // Fan vertices only. The penumbra wedges lie outside the polygon in the shadow, where our own
    // value is near zero by construction, and averaging them in would dilute the number towards zero
    // in proportion to how soft the edges are rather than to how much light the max is adding.
    private float Composition(Map map)
    {
        float vanillaTotal = 0f;
        float excessTotal = 0f;
        int samples = 0;

        foreach (VectorLightField.LightEntry entry in VectorLightField.LightsFor(map))
            AccumulateComposition(map, entry, ref vanillaTotal, ref excessTotal, ref samples);

        if (samples == 0)
            return 0f;

        float total = metric == Metric.VanillaSample ? vanillaTotal : excessTotal;

        return total / samples;
    }

    private static void AccumulateComposition(
        Map map, VectorLightField.LightEntry entry, ref float vanillaTotal, ref float excessTotal,
        ref int samples)
    {
        float lightX = entry.Cell.x + 0.5f;
        float lightZ = entry.Cell.z + 0.5f;

        VectorLightMath.Segment[] segments =
            VectorLightBlockers.SegmentsAround(map, entry.Cell, entry.Radius);

        VectorLightMath.LightPolygon polygon = VectorLightMath.Build(
            lightX, lightZ, entry.Radius, segments, VectorLightMath.DefaultBaseRayCount);

        VectorLightMath.LightMesh mesh =
            VectorLightMath.BuildMesh(lightX, lightZ, entry.Radius, polygon, sourceRadius: 0f);

        // Vertex 0 is the apex, sitting on the light itself, where both models are saturated and the
        // difference between them is meaningless. Skipping it keeps one guaranteed zero out of an
        // average taken over a few dozen boundary vertices.
        for (int i = 1; i < mesh.VertexCount; i++)
        {
            VectorLightMath.SampleTowardLight(
                mesh.X[i], mesh.Z[i], lightX, lightZ, VectorLightMath.VanillaSamplePull,
                out float sampleX, out float sampleZ);

            Color32 glow = GlowAt(map, sampleX, sampleZ);

            float vanilla = System.Math.Max(
                VectorLightMath.GlowUnit(glow.r),
                System.Math.Max(VectorLightMath.GlowUnit(glow.g), VectorLightMath.GlowUnit(glow.b)));

            float ours = VectorLightMath.Falloff(mesh.U[i] * entry.Radius, entry.Radius);

            vanillaTotal += vanilla;
            excessTotal += VectorLightMath.MaxComposedChannel(ours, vanilla);
            samples++;
        }
    }

    private static Color32 GlowAt(Map map, float x, float z)
    {
        int cellX = System.Math.Min(System.Math.Max((int)System.Math.Floor(x), 0), map.Size.x - 1);
        int cellZ = System.Math.Min(System.Math.Max((int)System.Math.Floor(z), 0), map.Size.z - 1);

        return map.glowGrid.VisualGlowAt(new IntVec3(cellX, 0, cellZ));
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
