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
        VectorLightMath.LightMesh mesh = VectorLightMath.BuildMesh(
            3.5f, 3.5f, 6f, polygon, VectorLightMath.DefaultSourceRadius);

        // Only the fan. The penumbra wedges that follow it are deliberately OUTSIDE the polygon —
        // that is what a soft edge is — so counting them here would read as overlap and fail a mesh
        // that is correct. FanTriangleCount exists to draw that line.
        Assert.That(
            TriangleAreaSum(mesh, mesh.FanTriangleCount),
            Is.EqualTo(FanArea(polygon, 3.5f, 3.5f)).Within(1e-3f));
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
        VectorLightMath.LightMesh mesh = VectorLightMath.BuildMesh(
            3.5f, 3.5f, 6f, polygon, VectorLightMath.DefaultSourceRadius);

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
        VectorLightMath.LightMesh mesh = VectorLightMath.BuildMesh(
            0f, 0f, 6f, polygon, VectorLightMath.DefaultSourceRadius);

        Assert.That(mesh.VertexCount, Is.EqualTo(0));
        Assert.That(mesh.Triangles.Length, Is.EqualTo(0));
    }

    // ---- penumbra -----------------------------------------------------------------------

    // The ramp's endpoints. Anything that got these backwards would draw the soft band inverted —
    // bright where the shadow is and dark where the light is — which reads on screen as a glowing
    // outline around every shadow rather than as an error.
    [TestCase(0f, 1f)]
    [TestCase(1f, 0f)]
    public void PenumbraVisibleFractionRunsFromFullyLitToFullyOccluded(float across, float expected)
    {
        Assert.That(VectorLightMath.PenumbraVisibleFraction(across), Is.EqualTo(expected).Within(Tolerance));
    }

    // Halfway across, a straight edge bisects the source disc, so exactly half of it is still
    // visible. This is the one interior point of the curve with an exact closed form, which makes it
    // the only place a wrong-but-plausible ramp (a linear one, say, which also passes the endpoint
    // cases above) can be separated from the right one by a single value.
    [Test]
    public void PenumbraVisibleFractionIsAHalfAcrossTheMiddle()
    {
        Assert.That(VectorLightMath.PenumbraVisibleFraction(0.5f), Is.EqualTo(0.5f).Within(Tolerance));
    }

    // The S-curve claim itself: shallow at both ends, steepest in the middle. A linear ramp would
    // pass every assertion above and still leave the visible crease at each end of the band that
    // choosing a circular segment was meant to remove, so the shape needs its own test.
    [Test]
    public void PenumbraVisibleFractionIsSteepestAcrossTheMiddle()
    {
        float atEdge = VectorLightMath.PenumbraVisibleFraction(0f) - VectorLightMath.PenumbraVisibleFraction(0.1f);
        float atMiddle = VectorLightMath.PenumbraVisibleFraction(0.45f) - VectorLightMath.PenumbraVisibleFraction(0.55f);

        Assert.That(atMiddle, Is.GreaterThan(atEdge * 2f));
    }

    [Test]
    public void PenumbraVisibleFractionNeverIncreases()
    {
        for (int i = 1; i <= 64; i++)
        {
            float previous = VectorLightMath.PenumbraVisibleFraction((i - 1) / 64f);
            float current = VectorLightMath.PenumbraVisibleFraction(i / 64f);

            Assert.That(current, Is.LessThanOrEqualTo(previous + Tolerance), $"rose at {i}");
        }
    }

    // Zero width exactly at the occluding corner. This is the property that keeps a shadow sharp
    // where it meets the wall casting it — the thing a constant-width wedge gets wrong, and gets
    // wrong in the most visible possible place.
    [Test]
    public void PenumbraHalfWidthIsZeroAtTheCorner()
    {
        Assert.That(VectorLightMath.PenumbraHalfWidth(4f, 4f, 0.5f), Is.EqualTo(0f).Within(Tolerance));
        Assert.That(VectorLightMath.PenumbraHalfWidth(3f, 4f, 0.5f), Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void PenumbraHalfWidthGrowsWithDistancePastTheCorner()
    {
        float near = VectorLightMath.PenumbraHalfWidth(5f, 4f, 0.5f);
        float far = VectorLightMath.PenumbraHalfWidth(9f, 4f, 0.5f);

        Assert.That(far, Is.GreaterThan(near));
    }

    // The asymptote, which is what sets how wide a soft edge can ever get: s/d0, approached but never
    // reached. Pinned because it is the only bound on the wedge — nothing downstream clamps it — so a
    // sign slip or an inverted ratio here would open a wedge that swallowed the whole polygon.
    [Test]
    public void PenumbraHalfWidthApproachesTheSourceOverCornerRatio()
    {
        Assert.That(VectorLightMath.PenumbraHalfWidth(10000f, 4f, 0.5f), Is.EqualTo(0.125f).Within(1e-4f));
        Assert.That(VectorLightMath.PenumbraHalfWidth(50f, 4f, 0.5f), Is.LessThan(0.125f));
    }

    // Doubling the source doubles the penumbra, which is the whole physical content of the model:
    // the softness of a shadow is set by how big the light is, not by how bright it is.
    [Test]
    public void PenumbraHalfWidthScalesWithTheSource()
    {
        float small = VectorLightMath.PenumbraHalfWidth(8f, 4f, 0.5f);
        float large = VectorLightMath.PenumbraHalfWidth(8f, 4f, 1f);

        Assert.That(large, Is.EqualTo(small * 2f).Within(Tolerance));
    }

    // The invariant the feature flag rests on. Row 0 of the 2-D gradient must BE the old 1-D falloff
    // curve, so that a mesh built with no source radius — every vertex at V = 0 — samples exactly
    // what the hard-edged version sampled, with no second texture and no branch in the draw.
    [Test]
    public void PenumbraGradientFirstRowIsTheFalloffCurve()
    {
        byte[] flat = VectorLightMath.FalloffGradient(12f, VectorLightMath.GradientSize);
        byte[] baked = VectorLightMath.PenumbraGradient(
            12f, VectorLightMath.GradientSize, VectorLightMath.PenumbraGradientSize);

        for (int i = 0; i < flat.Length; i++)
            Assert.That(baked[i], Is.EqualTo(flat[i]), $"column {i}");
    }

    [Test]
    public void PenumbraGradientLastRowIsFullyDark()
    {
        byte[] baked = VectorLightMath.PenumbraGradient(
            12f, VectorLightMath.GradientSize, VectorLightMath.PenumbraGradientSize);
        int lastRow = (VectorLightMath.PenumbraGradientSize - 1) * VectorLightMath.GradientSize;

        for (int i = 0; i < VectorLightMath.GradientSize; i++)
            Assert.That(baked[lastRow + i], Is.EqualTo(0), $"column {i}");
    }

    // Separability, which is the reason this is one texture rather than a shader: every row is the
    // falloff curve scaled by that row's ramp value, so a single bilinear sample reproduces the
    // product exactly. If that ever stopped holding, the texture would be lossy and the shader
    // argument would come back.
    [Test]
    public void PenumbraGradientIsTheFalloffTimesTheRamp()
    {
        byte[] flat = VectorLightMath.FalloffGradient(12f, VectorLightMath.GradientSize);
        byte[] baked = VectorLightMath.PenumbraGradient(
            12f, VectorLightMath.GradientSize, VectorLightMath.PenumbraGradientSize);

        for (int row = 0; row < VectorLightMath.PenumbraGradientSize; row++)
        {
            float ramp = VectorLightMath.PenumbraVisibleFraction(
                (float)row / (VectorLightMath.PenumbraGradientSize - 1));

            for (int column = 0; column < VectorLightMath.GradientSize; column += 17)
            {
                Assert.That(
                    baked[row * VectorLightMath.GradientSize + column],
                    Is.EqualTo((int)Math.Round(flat[column] * ramp)).Within(1),
                    $"row {row} column {column}");
            }
        }
    }

    // OFF REPRODUCES PHASE 1 EXACTLY, vertex for vertex and index for index. The repo's rule is that
    // a flag turned off must reproduce pre-feature behaviour rather than merely have no effect, and
    // for a feature whose off state is "pass zero to the same function" that rule is only worth
    // anything if something checks the two really do coincide.
    [Test]
    public void ASourceRadiusOfZeroLeavesTheHardEdgedMeshUntouched()
    {
        VectorLightMath.LightMesh hard = BuildBlockedMesh(0f);

        Assert.That(hard.VertexCount, Is.EqualTo(hard.FanTriangleCount / 3 + 1));
        Assert.That(hard.Triangles.Length, Is.EqualTo(hard.FanTriangleCount));

        for (int v = 0; v < hard.VertexCount; v++)
            Assert.That(hard.V[v], Is.EqualTo(0f), $"vertex {v}");
    }

    [Test]
    public void ASourceRadiusAddsWedgesBeyondTheFanAndLeavesTheFanAlone()
    {
        VectorLightMath.LightMesh hard = BuildBlockedMesh(0f);
        VectorLightMath.LightMesh soft = BuildBlockedMesh(VectorLightMath.DefaultSourceRadius);

        Assert.That(soft.FanTriangleCount, Is.EqualTo(hard.FanTriangleCount));
        Assert.That(soft.Triangles.Length, Is.GreaterThan(soft.FanTriangleCount));

        // The fan's own vertices are untouched, so the soft edge only ever ADDS light — it cannot
        // dim what phase 1 already lit. That is deliberate: §27's standing risk is rooms coming out
        // too dark, and an additive pass could not take light back out anyway.
        for (int v = 0; v < hard.VertexCount; v++)
        {
            Assert.That(soft.X[v], Is.EqualTo(hard.X[v]).Within(Tolerance), $"x {v}");
            Assert.That(soft.Z[v], Is.EqualTo(hard.Z[v]).Within(Tolerance), $"z {v}");
            Assert.That(soft.U[v], Is.EqualTo(hard.U[v]).Within(Tolerance), $"u {v}");
            Assert.That(soft.V[v], Is.EqualTo(0f), $"v {v}");
        }
    }

    // The same winding trap as the fan, on the geometry most likely to fall into it: a wedge opens
    // clockwise or anticlockwise depending on which side of the corner the shadow is, so its index
    // order has to flip with it. Get that wrong and half the soft edges in a scene render as nothing
    // at all, on a top-down camera that culls backfaces, while every numeric probe stays healthy.
    [Test]
    public void EveryNonDegenerateWedgeTriangleWindsWithTheFan()
    {
        VectorLightMath.LightMesh mesh = BuildBlockedMesh(VectorLightMath.DefaultSourceRadius);

        Assert.That(mesh.Triangles.Length, Is.GreaterThan(mesh.FanTriangleCount), "no wedges built");

        for (int t = mesh.FanTriangleCount; t < mesh.Triangles.Length; t += 3)
        {
            float area = SignedArea(mesh, t);

            if (Math.Abs(area) > 1e-6f)
                Assert.That(area, Is.LessThan(0f), $"wedge triangle at index {t} winds the wrong way");
        }
    }

    // A wedge lives in the shadow, but it must not escape the light's own reach — an emitter whose
    // soft edge poked past its radius would light cells vanilla's own falloff says are dark, and
    // would do it further from the lamp the wider the wedge got.
    [Test]
    public void NoWedgeVertexEscapesTheRadius()
    {
        VectorLightMath.LightMesh mesh = BuildBlockedMesh(VectorLightMath.DefaultSourceRadius);

        for (int v = 0; v < mesh.VertexCount; v++)
        {
            float dx = mesh.X[v] - 8.5f;
            float dz = mesh.Z[v] - 8.5f;

            Assert.That((float)Math.Sqrt(dx * dx + dz * dz), Is.LessThanOrEqualTo(10f + Tolerance));
            Assert.That(mesh.V[v], Is.InRange(0f, 1f));
            Assert.That(mesh.U[v], Is.InRange(0f, 1f));
        }
    }

    // An unobstructed light has no corners, so it has no shadow edges and must gain no wedges. The
    // shadow-edge test keys on a large distance jump between rays an epsilon apart in angle, and an
    // open circle has neither, so a wedge appearing here would mean the detector was firing on the
    // polygon merely curving — softening the rim of every lamp into a wider, dimmer halo.
    [Test]
    public void AnUnobstructedLightGainsNoWedges()
    {
        VectorLightMath.LightMesh mesh = BuildOpenMesh(out int rays);

        Assert.That(mesh.FanTriangleCount, Is.EqualTo(rays * 3));
        Assert.That(mesh.Triangles.Length, Is.EqualTo(mesh.FanTriangleCount));
    }

    // ---- the vanilla crossfade ----------------------------------------------------------

    // The endpoints are the whole contract: floor 0 must be §27 untouched and floor 1 must be
    // nothing at all from us. Anything that drifted at either end would make the knob a brightness
    // control rather than a redistribution, which is the exact failure the crossfade exists to avoid.
    [Test]
    public void TheCrossfadeEndpointsAreExactlyOnAndExactlyOff()
    {
        Assert.That(VectorLightMath.BlendedStrength(0.35f, 0f), Is.EqualTo(0.35f).Within(Tolerance));
        Assert.That(VectorLightMath.BlendedStrength(0.35f, 1f), Is.EqualTo(0f).Within(Tolerance));
    }

    [TestCase(0.25f, 0.75f)]
    [TestCase(0.5f, 0.5f)]
    [TestCase(0.75f, 0.25f)]
    public void TheCrossfadeGivesAwayExactlyTheShareItKeeps(float floor, float expectedShare)
    {
        Assert.That(
            VectorLightMath.BlendedStrength(1f, floor), Is.EqualTo(expectedShare).Within(Tolerance));
    }

    // Out-of-range floors are clamped rather than allowed to invert our contribution. A negative one
    // would make us brighter than unblended §27, which is the direction that washes a room out.
    [TestCase(-1f, 1f)]
    [TestCase(2f, 0f)]
    public void TheCrossfadeClampsRatherThanInverting(float floor, float expected)
    {
        Assert.That(VectorLightMath.BlendedStrength(1f, floor), Is.EqualTo(expected).Within(Tolerance));
    }

    // Vanilla's own channel, scaled. Floor 0 must be a true zero — that is §27's suppression, and a
    // floor that left 1 or 2 levels behind would put a faint wash into every shadow the subsystem
    // just carved.
    [TestCase((byte)200, 0f, (byte)0)]
    [TestCase((byte)200, 1f, (byte)200)]
    [TestCase((byte)200, 0.5f, (byte)100)]
    [TestCase((byte)255, 0.5f, (byte)128)]
    public void TheFlooredChannelScalesVanillasOwnLight(byte channel, float floor, byte expected)
    {
        Assert.That(VectorLightMath.FlooredChannel(channel, floor), Is.EqualTo(expected));
    }

    // Rounds rather than truncates. Truncation biases every channel down half a level, which across
    // a whole lighting overlay reads as the floor being dimmer than it was asked for — and it is the
    // sort of error that looks like the constant being wrong rather than the cast being wrong.
    [Test]
    public void TheFlooredChannelRoundsRatherThanTruncating()
    {
        Assert.That(VectorLightMath.FlooredChannel(3, 0.5f), Is.EqualTo(2));
    }

    // ---- the subtractive mask (§27 phase 3) ---------------------------------------------

    // With nothing in the way the polygon is the inscribed 48-gon, so the boundary is the radius at
    // every ray and a little under it between them. Both bounds matter: a boundary reading OVER the
    // radius would light cells the mesh never reaches, and one reading far under would shadow cells
    // that are in plain sight.
    [TestCase(0f)]
    [TestCase(0.7f)]
    [TestCase(1.9f)]
    [TestCase(-2.4f)]
    [TestCase(3.1f)]
    public void AnUnobstructedBoundaryIsTheRadiusInEveryDirection(float angle)
    {
        VectorLightMath.LightPolygon polygon = OpenPolygon(10f);
        float boundary = VectorLightMath.BoundaryDistanceAt(polygon, angle);

        Assert.That(boundary, Is.LessThanOrEqualTo(10f + Tolerance));
        Assert.That(boundary, Is.GreaterThan(10f * 0.99f));
    }

    // The seam at +-pi is its own case in the search, and it is the one an off-by-one would leave
    // returning the first ray's distance for a whole quadrant. A wall placed due WEST of the light
    // straddles that seam, so this fails if the wrap is wrong and passes trivially if it is not
    // exercised — which is why the wall is there rather than on an axis-aligned convenience.
    [Test]
    public void TheBoundaryWrapsAcrossTheSeamAtPi()
    {
        VectorLightMath.LightPolygon polygon = WalledPolygon();

        float justUnder = VectorLightMath.BoundaryDistanceAt(polygon, (float)(-Math.PI + 0.001));
        float justOver = VectorLightMath.BoundaryDistanceAt(polygon, (float)(Math.PI - 0.001));

        Assert.That(justUnder, Is.GreaterThan(0f));
        Assert.That(justOver, Is.GreaterThan(0f));

        // Either side of the seam is the same direction to within a thousandth of a radian, so the
        // two answers have to agree. They will not if one of them fell off the end of the array.
        Assert.That(justUnder, Is.EqualTo(justOver).Within(0.25f));
    }

    // A cell the light plainly sees is fully lit, and one squarely behind a wall is fully dark. These
    // are the two ends the mask subtracts nothing and everything at.
    [Test]
    public void CoverageIsOneInPlainSightAndZeroBehindAWall()
    {
        VectorLightMath.LightPolygon polygon = WalledPolygon();

        Assert.That(
            VectorLightMath.LitFraction(polygon, 8.5f, 8.5f, 10, 8, 2),
            Is.EqualTo(1f).Within(Tolerance));
        Assert.That(
            VectorLightMath.LitFraction(polygon, 8.5f, 8.5f, 3, 8, 2),
            Is.EqualTo(0f).Within(Tolerance));
    }

    // THE POINT OF SAMPLING THE CELL RATHER THAN ITS CENTRE. The lighting overlay can only place a
    // boundary to within a cell, so the cell the shadow edge actually crosses has to report a
    // FRACTION — that fraction is what turns a staircase into a ramp. A yes/no test would make this
    // cell either 0 or 1 and there would be nothing between the lit region and the dark one.
    [Test]
    public void TheCellTheEdgeCrossesIsPartlyLit()
    {
        VectorLightMath.LightPolygon polygon = WalledPolygon();
        bool foundPartial = false;

        // Walk the column just past the wall and look for the cell the boundary runs through.
        for (int z = 3; z <= 13 && !foundPartial; z++)
        {
            float lit = VectorLightMath.LitFraction(polygon, 8.5f, 8.5f, 3, z, 4);
            foundPartial = lit > 0f && lit < 1f;
        }

        Assert.That(foundPartial, Is.True, "no cell straddled the shadow boundary");
    }

    // More samples must never turn a fully lit or fully dark cell into a partly lit one: the sample
    // count controls how finely the EDGE is resolved and nothing else. A test that only checked the
    // edge would not notice a sampling grid that had drifted off the cell.
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(4)]
    [TestCase(8)]
    public void TheSampleCountDoesNotMoveTheFullyLitOrFullyDarkCells(int samples)
    {
        VectorLightMath.LightPolygon polygon = WalledPolygon();

        Assert.That(
            VectorLightMath.LitFraction(polygon, 8.5f, 8.5f, 10, 8, samples),
            Is.EqualTo(1f).Within(Tolerance));
        Assert.That(
            VectorLightMath.LitFraction(polygon, 8.5f, 8.5f, 3, 8, samples),
            Is.EqualTo(0f).Within(Tolerance));
    }

    // The light's own position is lit whatever the geometry says, because the distance is zero and
    // there is no direction to ask about. Without the zero check this is an atan2(0, 0) away from
    // being whatever the boundary happens to be at angle zero.
    [Test]
    public void TheLightsOwnPositionIsAlwaysLit()
    {
        Assert.That(VectorLightMath.IsLit(WalledPolygon(), 8.5f, 8.5f, 8.5f, 8.5f), Is.True);
    }

    // A degenerate polygon reports nothing lit rather than everything. The mask scales by (1 - lit),
    // so an empty polygon reading "fully lit" would silently disable the subsystem — the failure that
    // looks like the feature being switched off rather than like a bug.
    [Test]
    public void AnEmptyPolygonLightsNothing()
    {
        VectorLightMath.LightPolygon empty =
            new VectorLightMath.LightPolygon(new float[0], new float[0], 0);

        Assert.That(VectorLightMath.BoundaryDistanceAt(empty, 1f), Is.EqualTo(0f).Within(Tolerance));
        Assert.That(
            VectorLightMath.LitFraction(empty, 8.5f, 8.5f, 8, 8, 2), Is.EqualTo(0f).Within(Tolerance));
    }

    // The beam that rides on top of phase 3's mask is exactly what the crossfade already delivers
    // over the half of vanilla it keeps. Pinning the identity rather than the number keeps the two
    // tied together: retuning DefaultStrength or DefaultVanillaFloor moves both, which is the point
    // — the combination is meant to be comparable to the crossfade it is trying to beat, and a beam
    // that quietly drifted away from that would make the comparison meaningless without failing.
    [Test]
    public void TheMaskBeamDeliversWhatTheCrossfadeAlreadyDoes()
    {
        Assert.That(
            VectorLightMath.MaskBeamStrength,
            Is.EqualTo(VectorLightMath.BlendedStrength(
                VectorLightMath.DefaultStrength, VectorLightMath.DefaultVanillaFloor))
                .Within(Tolerance));
    }

    // And it must be a genuine fraction of the full pass rather than all of it: drawing our whole
    // model over a vanilla that is still rendering is epic #145's rejected option 1, which measured
    // 6 L* bright. The mask removes the shadowed light before this lands, but it does not remove any
    // of the lit light, so full strength here would still double the lit region.
    [Test]
    public void TheMaskBeamIsAFractionOfTheFullPass()
    {
        Assert.That(VectorLightMath.MaskBeamStrength, Is.GreaterThan(0f));
        Assert.That(VectorLightMath.MaskBeamStrength, Is.LessThan(VectorLightMath.DefaultStrength));
    }

    // THE TEST THAT MAKES THE CACHE A PURE OPTIMISATION. The baked grid replaced a per-cell
    // LitFraction call that cost 239 us per section; it is only allowed to do that if it answers
    // exactly what the call answered. Every cell of the emitter's square is checked rather than a
    // sample of them, because an indexing error in a grid is precisely the sort of bug that is
    // correct in the middle and wrong at one edge.
    [Test]
    public void TheBakedCoverageAgreesWithSamplingEveryCell()
    {
        VectorLightMath.LightPolygon polygon = WalledPolygon();
        const int radius = 10;
        byte[] grid = VectorLightMath.BuildCoverage(polygon, 8, 8, radius, 2);

        for (int cz = 8 - radius; cz <= 8 + radius; cz++)
        {
            for (int cx = 8 - radius; cx <= 8 + radius; cx++)
            {
                float sampled = VectorLightMath.LitFraction(polygon, 8.5f, 8.5f, cx, cz, 2);
                byte baked = VectorLightMath.CoverageAt(grid, 8, 8, radius, cx, cz);

                Assert.That(
                    baked / 255f, Is.EqualTo(sampled).Within(1f / 255f),
                    "cell (" + cx + ", " + cz + ")");
            }
        }
    }

    // Outside the square the answer is FULLY LIT, not fully dark. The emitter delivers nothing
    // there, so a caller has nothing to subtract either way — but 0 would mean "wholly shadowed",
    // and a caller that arrived with a non-zero glow would darken a cell this emitter never lit.
    // The safe direction is to subtract nothing.
    [TestCase(-3, 8)]
    [TestCase(8, 40)]
    [TestCase(100, 100)]
    public void CoverageOutsideTheSquareReadsFullyLit(int cellX, int cellZ)
    {
        byte[] grid = VectorLightMath.BuildCoverage(WalledPolygon(), 8, 8, 10, 2);

        Assert.That(VectorLightMath.CoverageAt(grid, 8, 8, 10, cellX, cellZ), Is.EqualTo(255));
    }

    // The skip that makes an unobstructed emitter free. In open ground most emitters shadow nothing,
    // and the bake drops them for the whole section rather than looking a grid up cell by cell.
    //
    // ASKED OF THE POLYGON RATHER THAN THE BAKED GRID, and an earlier version that asked the grid
    // failed here for a reason worth keeping: a grid covers the emitter's SQUARE while the polygon
    // covers its circle, so the corners are outside the light whatever the geometry does and an
    // all-255 grid never occurs. A ray stopping short of the radius is shadow; discretisation is not.
    [Test]
    public void AnUnobstructedEmitterIsSkippableAndAWalledOneIsNot()
    {
        Assert.That(VectorLightMath.IsUnobstructed(OpenPolygon(10f), 10f), Is.True);
        Assert.That(VectorLightMath.IsUnobstructed(WalledPolygon(), 10f), Is.False);
    }

    // And a degenerate polygon is NOT skippable. Returning true would mean "nothing is shadowed",
    // which reads as the subsystem being off rather than as the polygon being broken.
    [Test]
    public void AnEmptyPolygonIsNotSkippable()
    {
        Assert.That(
            VectorLightMath.IsUnobstructed(
                new VectorLightMath.LightPolygon(new float[0], new float[0], 0), 10f),
            Is.False);
    }

    // A degenerate polygon bakes to all-zero rather than all-lit, matching LitFraction's own answer
    // for it — see AnEmptyPolygonLightsNothing. It must not read as "nothing is shadowed", which
    // would silently disable the subsystem instead of failing.
    [Test]
    public void AnEmptyPolygonBakesToNoCoverage()
    {
        VectorLightMath.LightPolygon empty =
            new VectorLightMath.LightPolygon(new float[0], new float[0], 0);
        byte[] grid = VectorLightMath.BuildCoverage(empty, 8, 8, 4, 2);

        Assert.That(grid.Length, Is.EqualTo(9 * 9));
        Assert.That(VectorLightMath.CoverageAt(grid, 8, 8, 4, 8, 8), Is.EqualTo(0));
    }

    private static VectorLightMath.LightPolygon OpenPolygon(float radius)
    {
        return VectorLightMath.Build(8.5f, 8.5f, radius, new VectorLightMath.Segment[0], 48);
    }

    // A light at the centre of a 16x16 field with a wall column due west of it, three cells long, so
    // the shadow it throws crosses the +-pi seam of the angle space.
    private static VectorLightMath.LightPolygon WalledPolygon()
    {
        bool[] blocked = new bool[16 * 16];
        blocked[7 * 16 + 5] = true;
        blocked[8 * 16 + 5] = true;
        blocked[9 * 16 + 5] = true;

        VectorLightMath.Segment[] segments = VectorLightMath.SilhouetteSegments(blocked, 16, 16, 0, 0);
        return VectorLightMath.Build(8.5f, 8.5f, 10f, segments, 48);
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

        return VectorLightMath.BuildMesh(0f, 0f, 12f, polygon, VectorLightMath.DefaultSourceRadius);
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

    private static float TriangleAreaSum(VectorLightMath.LightMesh mesh, int indexCount)
    {
        float total = 0f;

        for (int t = 0; t < indexCount; t += 3)
            total += Math.Abs(SignedArea(mesh, t));

        return total;
    }

    // A light in a small room with a free-standing block in it, which is the smallest layout that
    // produces real shadow edges in more than one direction — and so the smallest one that exercises
    // wedges opening BOTH ways round the polygon. A single blocker only ever yields a mirrored pair
    // and would let a sign error through on half the geometry.
    private static VectorLightMath.LightMesh BuildBlockedMesh(float sourceRadius)
    {
        bool[] blocked = new bool[16 * 16];
        blocked[6 * 16 + 6] = true;
        blocked[6 * 16 + 7] = true;
        blocked[10 * 16 + 3] = true;
        blocked[4 * 16 + 11] = true;

        VectorLightMath.Segment[] segments = VectorLightMath.SilhouetteSegments(blocked, 16, 16, 0, 0);
        VectorLightMath.LightPolygon polygon = VectorLightMath.Build(8.5f, 8.5f, 10f, segments, 48);

        return VectorLightMath.BuildMesh(8.5f, 8.5f, 10f, polygon, sourceRadius);
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
