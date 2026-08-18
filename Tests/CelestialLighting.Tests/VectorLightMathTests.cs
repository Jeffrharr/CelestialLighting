using System;
using System.Linq;

namespace CelestialLighting.Tests;

// Offline unit tests for §27's pure core (Source/VectorLightMath.cs, linked into this project so
// these run against the exact shipped file).
//
// The emphasis here is different from the mod's other pure cores, and deliberately so. Those compute
// a number, and a wrong number is a wrong number. This one computes GEOMETRY, and geometry has two
// failure modes that no amount of asserting on individual values will catch: triangles that overlap
// (which on an additive pass doubles the light exactly where they meet — §17 shipped that bug once)
// and triangles wound the wrong way (which renders absolutely nothing while every probe still reports
// healthy). So the load-bearing tests below are the ones about the mesh AS A WHOLE — that its
// triangles tile its polygon exactly, and that they all face the same way.
[TestFixture]
public class VectorLightMathTests
{
    private const float Tolerance = 1e-4f;

    // ---- silhouette extraction ----------------------------------------------------------

    [Test]
    public void EmptyGridHasNoSegments()
    {
        VectorLightMath.Segment[] segments = VectorLightMath.SilhouetteSegments(new bool[25], 5, 5, 0, 0);
        Assert.That(segments.Length, Is.EqualTo(0));
    }

    [Test]
    public void OneBlockedCellIsBoundedByFourUnitEdges()
    {
        bool[] blocked = new bool[25];
        blocked[2 * 5 + 2] = true;

        VectorLightMath.Segment[] segments = VectorLightMath.SilhouetteSegments(blocked, 5, 5, 0, 0);

        Assert.That(segments.Length, Is.EqualTo(4));
        Assert.That(segments.Sum(Length), Is.EqualTo(4f).Within(Tolerance));
    }

    // The whole reason SilhouetteSegments exists rather than one rectangle per cell: a wall run must
    // come back as its OUTLINE, with the edges between abutting cells gone. Three cells side by side
    // have twelve edges and four of them are shared, so a correct outline is 8 units of perimeter in
    // 4 merged segments — not 12 units in 12.
    [Test]
    public void AWallRunMergesIntoItsOutline()
    {
        bool[] blocked = new bool[64];

        for (int x = 2; x <= 4; x++)
            blocked[3 * 8 + x] = true;

        VectorLightMath.Segment[] segments = VectorLightMath.SilhouetteSegments(blocked, 8, 8, 0, 0);

        Assert.That(segments.Length, Is.EqualTo(4));
        Assert.That(segments.Sum(Length), Is.EqualTo(8f).Within(Tolerance));
    }

    [Test]
    public void ASolidBlockHasNoInteriorEdges()
    {
        bool[] blocked = new bool[64];

        for (int z = 2; z <= 4; z++)
        {
            for (int x = 2; x <= 4; x++)
                blocked[z * 8 + x] = true;
        }

        VectorLightMath.Segment[] segments = VectorLightMath.SilhouetteSegments(blocked, 8, 8, 0, 0);

        Assert.That(segments.Length, Is.EqualTo(4));
        Assert.That(segments.Sum(Length), Is.EqualTo(12f).Within(Tolerance));
    }

    // The origin is what keeps the light, the walls and the finished mesh in one coordinate system.
    // Getting it wrong shifts every shadow by the window's corner, which on screen looks like the
    // lighting being subtly, unaccountably wrong rather than like an offset.
    [Test]
    public void SegmentsComeBackInWorldCoordinates()
    {
        bool[] blocked = new bool[25];
        blocked[2 * 5 + 2] = true;

        VectorLightMath.Segment[] segments = VectorLightMath.SilhouetteSegments(blocked, 5, 5, 100, 200);

        Assert.That(segments.Min(s => Math.Min(s.X1, s.X2)), Is.EqualTo(102f).Within(Tolerance));
        Assert.That(segments.Min(s => Math.Min(s.Z1, s.Z2)), Is.EqualTo(202f).Within(Tolerance));
    }

    // ---- ray casting --------------------------------------------------------------------

    [Test]
    public void AnUnobstructedRayReachesTheRadius()
    {
        Assert.That(
            VectorLightMath.CastRay(0f, 0f, 0f, 14f, new VectorLightMath.Segment[0]),
            Is.EqualTo(14f).Within(Tolerance));
    }

    [TestCase(0f, 5f)]
    [TestCase((float)Math.PI, 14f)]
    public void ARayStopsAtTheFirstWallItMeets(float angle, float expected)
    {
        // One wall five cells east of the light, running north-south, and nothing to the west.
        VectorLightMath.Segment[] wall = { new VectorLightMath.Segment(5f, -5f, 5f, 5f) };

        Assert.That(VectorLightMath.CastRay(0f, 0f, angle, 14f, wall), Is.EqualTo(expected).Within(Tolerance));
    }

    // The shadow edge, stated as the thing it actually is: two rays a hair apart, one stopped by the
    // wall and one that slips past its end and runs on to the radius. If this collapses, there is no
    // shadow — the wedge is exactly the gap between these two numbers.
    [Test]
    public void RaysEitherSideOfACornerDisagreeSharply()
    {
        VectorLightMath.Segment[] wall = { new VectorLightMath.Segment(5f, 0f, 5f, 5f) };
        float cornerAngle = (float)Math.Atan2(0f, 5f);

        float inside = VectorLightMath.CastRay(0f, 0f, cornerAngle + 0.01f, 14f, wall);
        float past = VectorLightMath.CastRay(0f, 0f, cornerAngle - 0.01f, 14f, wall);

        Assert.That(inside, Is.EqualTo(5f).Within(0.01f));
        Assert.That(past, Is.EqualTo(14f).Within(Tolerance));
    }

    // ---- the polygon --------------------------------------------------------------------

    [Test]
    public void AnUnobstructedLightIsARegularFan()
    {
        VectorLightMath.LightPolygon polygon =
            VectorLightMath.Build(0f, 0f, 12f, new VectorLightMath.Segment[0], 48);

        Assert.That(polygon.Count, Is.EqualTo(48));

        for (int i = 0; i < polygon.Count; i++)
            Assert.That(polygon.Distances[i], Is.EqualTo(12f).Within(Tolerance));
    }

    [Test]
    public void PolygonAnglesAreSortedAscending()
    {
        bool[] blocked = new bool[64];
        blocked[3 * 8 + 5] = true;

        VectorLightMath.Segment[] segments = VectorLightMath.SilhouetteSegments(blocked, 8, 8, 0, 0);
        VectorLightMath.LightPolygon polygon = VectorLightMath.Build(3.5f, 3.5f, 10f, segments, 48);

        for (int i = 1; i < polygon.Count; i++)
            Assert.That(polygon.Angles[i], Is.GreaterThanOrEqualTo(polygon.Angles[i - 1]));
    }

    [Test]
    public void NoRayEverReachesPastTheRadius()
    {
        bool[] blocked = new bool[64];
        blocked[3 * 8 + 5] = true;

        VectorLightMath.Segment[] segments = VectorLightMath.SilhouetteSegments(blocked, 8, 8, 0, 0);
        VectorLightMath.LightPolygon polygon = VectorLightMath.Build(3.5f, 3.5f, 10f, segments, 48);

        for (int i = 0; i < polygon.Count; i++)
            Assert.That(polygon.Distances[i], Is.LessThanOrEqualTo(10f + Tolerance));
    }

    // A glower can end up on a cell that blocks light — a wall-mounted lamp, or a mod's glowing wall.
    // Vanilla's flood simply starts inside and floods out. Ours must at least not produce garbage.
    [Test]
    public void ALightInsideAWallStillProducesAClosedPolygon()
    {
        bool[] blocked = new bool[64];
        blocked[4 * 8 + 4] = true;

        VectorLightMath.Segment[] segments = VectorLightMath.SilhouetteSegments(blocked, 8, 8, 0, 0);
        VectorLightMath.LightPolygon polygon = VectorLightMath.Build(4.5f, 4.5f, 10f, segments, 48);

        Assert.That(polygon.Count, Is.GreaterThan(3));

        for (int i = 0; i < polygon.Count; i++)
            Assert.That(float.IsNaN(polygon.Distances[i]), Is.False);
    }

    // ---- falloff ------------------------------------------------------------------------

    [Test]
    public void FalloffIsZeroBeyondTheRadius()
    {
        Assert.That(VectorLightMath.Falloff(14.1f, 14f), Is.EqualTo(0f));
    }

    [Test]
    public void FalloffDecreasesWithDistance()
    {
        float previous = float.MaxValue;

        for (float d = 1f; d <= 14f; d += 0.5f)
        {
            float value = VectorLightMath.Falloff(d, 14f);
            Assert.That(value, Is.LessThan(previous));
            previous = value;
        }
    }

    // Inside one cell the curve is held flat rather than allowed to diverge. Vanilla does the same by
    // seeding its flood at intDist = 100, and without it the inverse-square term would blow past 1
    // and clip the core into a flat disc exactly where the eye goes first.
    [Test]
    public void FalloffIsClampedInsideTheFirstCell()
    {
        Assert.That(VectorLightMath.Falloff(0f, 14f), Is.EqualTo(VectorLightMath.Falloff(1f, 14f)).Within(Tolerance));
        Assert.That(VectorLightMath.Falloff(1f, 14f), Is.LessThanOrEqualTo(1f));
    }

    // Pinned against vanilla's own curve, transcribed from ComputeGlowGridsJob.SetGlowFromDist. §27
    // changes WHERE light reaches; if it also quietly changed how bright a lamp is, no A/B of the
    // shape would be readable.
    [TestCase(1f, 14f)]
    [TestCase(5f, 14f)]
    [TestCase(13f, 14f)]
    public void FalloffMatchesVanillasCurve(float distance, float radius)
    {
        float linear = 1f - distance / radius;
        float inverseSquare = 1f / (distance * distance);
        float expected = linear + 0.4f * (inverseSquare - linear);

        Assert.That(VectorLightMath.Falloff(distance, radius), Is.EqualTo(expected).Within(Tolerance));
    }

    // The gradient is the falloff curve, so it inherits the curve's shape: brightest at the light,
    // dark at the rim, never increasing anywhere in between.
    [Test]
    public void FalloffGradientRunsFromBrightToDark()
    {
        byte[] gradient = VectorLightMath.FalloffGradient(14f, VectorLightMath.GradientSize);

        Assert.That(gradient.Length, Is.EqualTo(VectorLightMath.GradientSize));
        Assert.That(gradient[0], Is.GreaterThan((byte)200));

        // Not exactly zero, and that is vanilla's curve rather than a rounding slip: at the radius the
        // linear term has reached 0 but the inverse-square term has not, leaving 0.4/r^2 — one part in
        // 255 at radius 14. So the rim is a hard cutoff from imperceptible to nothing, which is fine
        // to ship and would be wrong to "fix" by rescaling, since that would change every lamp's
        // brightness to hide a step no one can see.
        Assert.That(gradient[gradient.Length - 1], Is.LessThanOrEqualTo((byte)1));

        for (int i = 1; i < gradient.Length; i++)
            Assert.That(gradient[i], Is.LessThanOrEqualTo(gradient[i - 1]));
    }

    // Two lights of different radius genuinely need different gradients — the inverse-square term is
    // 1/(u*radius)^2, so normalised distance alone does not determine brightness. If this ever came
    // out equal, one cached gradient could be shared by every light and the cache key would be wrong.
    [Test]
    public void GradientShapeDependsOnRadius()
    {
        byte[] small = VectorLightMath.FalloffGradient(6f, 64);
        byte[] large = VectorLightMath.FalloffGradient(24f, 64);

        Assert.That(small[32], Is.Not.EqualTo(large[32]));
    }

    // ---- the mesh, which is where the real risk is --------------------------------------

    [Test]
    public void MeshVertexAndTriangleCountsFollowTheLayout()
    {
        VectorLightMath.LightMesh mesh = BuildOpenMesh(out int rays);

        Assert.That(mesh.VertexCount, Is.EqualTo(rays + 1));
        Assert.That(mesh.Triangles.Length, Is.EqualTo(rays * 3));
    }

    // U is the whole brightness channel, so it has to BE distance over radius rather than merely
    // correlate with it — the gradient lookup has no way to notice if it drifts.
    [Test]
    public void UIsDistanceOverRadius()
    {
        VectorLightMath.LightMesh mesh = BuildOpenMesh(out int _);

        Assert.That(mesh.U[0], Is.EqualTo(0f).Within(Tolerance));

        for (int v = 1; v < mesh.VertexCount; v++)
        {
            float dx = mesh.X[v];
            float dz = mesh.Z[v];
            float expected = (float)Math.Sqrt(dx * dx + dz * dz) / 12f;
            Assert.That(mesh.U[v], Is.EqualTo(expected).Within(Tolerance));
        }
    }

    [Test]
    public void EveryTriangleIndexIsInRange()
    {
        VectorLightMath.LightMesh mesh = BuildOpenMesh(out int _);

        foreach (int index in mesh.Triangles)
            Assert.That(index, Is.InRange(0, mesh.VertexCount - 1));
    }

    // THE tiling test. The draw is additive, so two triangles overlapping anywhere means light is
    // counted twice there — a bright seam that no per-value assertion would ever notice. Summing the
    // triangle areas and comparing against the polygon's own fan area catches overlap (sum too large)
    // and gaps (sum too small) in one number.
    [Test]
    public void TrianglesTileThePolygonExactlyOnce()
    {
        bool[] blocked = new bool[64];
        blocked[3 * 8 + 5] = true;

        VectorLightMath.Segment[] segments = VectorLightMath.SilhouetteSegments(blocked, 8, 8, 0, 0);
        VectorLightMath.LightPolygon polygon = VectorLightMath.Build(3.5f, 3.5f, 6f, segments, 48);
        VectorLightMath.LightMesh mesh = VectorLightMath.BuildMesh(3.5f, 3.5f, 6f, polygon);

        Assert.That(TriangleAreaSum(mesh), Is.EqualTo(FanArea(polygon, 3.5f, 3.5f)).Within(1e-3f));
    }

    // Consistent winding, tested as "no triangle faces the other way". A single flipped face is
    // invisible on a numeric probe and, on a top-down camera that culls backfaces, is the difference
    // between a light and nothing at all.
    [Test]
    public void EveryNonDegenerateTriangleWindsTheSameWay()
    {
        VectorLightMath.LightMesh mesh = BuildOpenMesh(out int _);

        for (int t = 0; t < mesh.Triangles.Length; t += 3)
        {
            float area = SignedArea(mesh, t);

            if (Math.Abs(area) > 1e-6f)
                Assert.That(area, Is.LessThan(0f), $"triangle at index {t} winds the wrong way");
        }
    }

    // Ring vertices are clamped to their own ray's reach, which is what stops a ring drawn beyond an
    // obstruction from spilling through it. If this ever regresses, light passes through walls in a
    // ring pattern — recognisable, but only once you know to look for it.
    [Test]
    public void NoVertexEscapesTheVisibilityPolygon()
    {
        bool[] blocked = new bool[64];
        blocked[3 * 8 + 5] = true;

        VectorLightMath.Segment[] segments = VectorLightMath.SilhouetteSegments(blocked, 8, 8, 0, 0);
        VectorLightMath.LightPolygon polygon = VectorLightMath.Build(3.5f, 3.5f, 6f, segments, 48);
        VectorLightMath.LightMesh mesh = VectorLightMath.BuildMesh(3.5f, 3.5f, 6f, polygon);

        for (int v = 0; v < mesh.VertexCount; v++)
        {
            float dx = mesh.X[v] - 3.5f;
            float dz = mesh.Z[v] - 3.5f;
            Assert.That((float)Math.Sqrt(dx * dx + dz * dz), Is.LessThanOrEqualTo(6f + Tolerance));
        }
    }

    [Test]
    public void ADegeneratePolygonProducesNoMesh()
    {
        VectorLightMath.LightPolygon polygon = new VectorLightMath.LightPolygon(new float[0], new float[0], 0);
        VectorLightMath.LightMesh mesh = VectorLightMath.BuildMesh(0f, 0f, 6f, polygon);

        Assert.That(mesh.VertexCount, Is.EqualTo(0));
        Assert.That(mesh.Triangles.Length, Is.EqualTo(0));
    }

    // ---- daylight -----------------------------------------------------------------------

    [TestCase(0f, 1f)]
    [TestCase(0.5f, 0.5f)]
    [TestCase(1f, 0f)]
    public void DaylightScaleFadesTheLightOutAsTheSkyComesUp(float skyGlow, float expected)
    {
        Assert.That(VectorLightMath.DaylightScale(skyGlow), Is.EqualTo(expected).Within(Tolerance));
    }

    // ---- helpers ------------------------------------------------------------------------

    private static VectorLightMath.LightMesh BuildOpenMesh(out int rays)
    {
        rays = 48;

        VectorLightMath.LightPolygon polygon =
            VectorLightMath.Build(0f, 0f, 12f, new VectorLightMath.Segment[0], rays);

        return VectorLightMath.BuildMesh(0f, 0f, 12f, polygon);
    }

    private static float Length(VectorLightMath.Segment segment)
    {
        float dx = segment.X2 - segment.X1;
        float dz = segment.Z2 - segment.Z1;
        return (float)Math.Sqrt(dx * dx + dz * dz);
    }

    private static float SignedArea(VectorLightMath.LightMesh mesh, int triangleStart)
    {
        int a = mesh.Triangles[triangleStart];
        int b = mesh.Triangles[triangleStart + 1];
        int c = mesh.Triangles[triangleStart + 2];

        return 0.5f * ((mesh.X[b] - mesh.X[a]) * (mesh.Z[c] - mesh.Z[a])
                     - (mesh.X[c] - mesh.X[a]) * (mesh.Z[b] - mesh.Z[a]));
    }

    private static float TriangleAreaSum(VectorLightMath.LightMesh mesh)
    {
        float total = 0f;

        for (int t = 0; t < mesh.Triangles.Length; t += 3)
            total += Math.Abs(SignedArea(mesh, t));

        return total;
    }

    private static float FanArea(VectorLightMath.LightPolygon polygon, float lightX, float lightZ)
    {
        float total = 0f;

        for (int i = 0; i < polygon.Count; i++)
        {
            int next = (i + 1) % polygon.Count;

            float ax = (float)Math.Cos(polygon.Angles[i]) * polygon.Distances[i];
            float az = (float)Math.Sin(polygon.Angles[i]) * polygon.Distances[i];
            float bx = (float)Math.Cos(polygon.Angles[next]) * polygon.Distances[next];
            float bz = (float)Math.Sin(polygon.Angles[next]) * polygon.Distances[next];

            total += Math.Abs(0.5f * (ax * bz - bx * az));
        }

        return total;
    }
}
