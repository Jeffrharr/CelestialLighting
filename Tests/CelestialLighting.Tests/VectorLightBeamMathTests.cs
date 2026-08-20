using System;
using CelestialLighting;
using NUnit.Framework;

namespace CelestialLighting.Tests;

// §27 phase 3d: the owed-light beam as geometry.
//
// THE ONE PROPERTY WORTH MORE THAN THE REST is that an all-false mask emits NOTHING. That is the
// guarantee phases 1 and 2 could not make — both drew over the lit room and had to be calibrated back
// down — and it is what makes a drawn beam safe at last. It is asserted here structurally rather than
// by measuring a frame, because "the room did not move" has to be true at every resolution and for
// every polygon, not just in the fixture that happened to be photographed.
[TestFixture]
public class VectorLightBeamMathTests
{
    // A square-ish polygon at a fixed radius: a lamp in the open, nothing occluding.
    private static VectorLightMath.LightPolygon Circle(int rays, float radius)
    {
        float[] angles = new float[rays];
        float[] distances = new float[rays];

        for (int i = 0; i < rays; i++)
        {
            angles[i] = VectorLightBeamMath.TwoPi * i / rays;
            distances[i] = radius;
        }

        return new VectorLightMath.LightPolygon(angles, distances, rays);
    }

    private static bool[] Mask(int sectors, int steps, Func<int, int, bool> owed)
    {
        bool[] flags = new bool[sectors * steps];

        for (int s = 0; s < sectors; s++)
        {
            for (int i = 0; i < steps; i++)
                flags[s * steps + i] = owed(s, i);
        }

        return flags;
    }

    // THE ROOM. Vanilla delivered everywhere the polygon can see, so nothing is owed and the layer
    // must produce an empty mesh — not a dim one, not a small one, an empty one.
    [Test]
    public void NothingOwed_DrawsNoGeometryAtAll()
    {
        var polygon = Circle(48, 10f);
        int steps = VectorLightBeamMath.StepsFor(10f);

        VectorLightMath.LightMesh mesh = VectorLightBeamMath.BuildOwedMesh(
            5f, 5f, 10f, polygon, Mask(48, steps, (s, i) => false), steps, null, null);

        Assert.That(mesh.VertexCount, Is.Zero);
        Assert.That(mesh.Triangles.Length, Is.Zero, "an oversized triangle buffer uploads degenerates");
    }

    // And the counterpart: geometry appears exactly where the mask says it should, in ONE sector only.
    // A beam through a doorway is a handful of sectors, so a builder that leaked into its neighbours
    // would widen every beam without changing any number this suite otherwise checks.
    [Test]
    public void OwedInOneSector_DrawsInThatSectorOnly()
    {
        var polygon = Circle(48, 10f);
        int steps = VectorLightBeamMath.StepsFor(10f);

        VectorLightMath.LightMesh mesh = VectorLightBeamMath.BuildOwedMesh(
            0f, 0f, 10f, polygon, Mask(48, steps, (s, i) => s == 0), steps, null, null);

        Assert.That(mesh.VertexCount, Is.EqualTo(4), "one contiguous run is one quad");

        float lo = polygon.Angles[0];
        float hi = polygon.Angles[1];

        for (int i = 0; i < mesh.VertexCount; i++)
        {
            float angle = (float)Math.Atan2(mesh.Z[i], mesh.X[i]);

            if (angle < 0f)
                angle += VectorLightBeamMath.TwoPi;

            // The centre vertex sits at the origin where the angle is meaningless; every other
            // vertex has to lie on one of the sector's two bounding rays.
            if (mesh.X[i] != 0f || mesh.Z[i] != 0f)
            {
                Assert.That(Math.Min(Math.Abs(angle - lo), Math.Abs(angle - hi)), Is.LessThan(1e-3f),
                    $"vertex {i} left its sector");
            }
        }
    }

    // Consecutive owed steps MERGE. Left per-step, a radius-10 lamp is 41 steps across 48 sectors and
    // the rebuild would upload about two thousand quads per emitter.
    [Test]
    public void ConsecutiveStepsMergeIntoOneQuad()
    {
        var polygon = Circle(4, 10f);
        int steps = VectorLightBeamMath.StepsFor(10f);

        VectorLightMath.LightMesh merged = VectorLightBeamMath.BuildOwedMesh(
            0f, 0f, 10f, polygon, Mask(4, steps, (s, i) => s == 0 && i < 8), steps, null, null);

        Assert.That(merged.VertexCount, Is.EqualTo(4), "eight adjacent steps are one run");
    }

    // A gap in the mask is a gap in the geometry: two runs, two quads. This is the case a doorway
    // actually produces when a pillar splits the beam, and merging across the gap would light the
    // pillar's own shadow.
    [Test]
    public void AGapInTheMaskSplitsTheRun()
    {
        var polygon = Circle(4, 10f);
        int steps = VectorLightBeamMath.StepsFor(10f);

        VectorLightMath.LightMesh split = VectorLightBeamMath.BuildOwedMesh(
            0f, 0f, 10f, polygon, Mask(4, steps, (s, i) => s == 0 && (i < 4 || (i > 8 && i < 12))), steps, null, null);

        Assert.That(split.VertexCount, Is.EqualTo(8), "two runs are two quads");
    }

    // U is what samples the baked falloff, so it has to be distance/radius exactly — the same
    // coordinate the lit fan uses. If the beam sampled a different profile it would stop reading as
    // the room's own light continuing, which is the entire point of reusing the gradient.
    [Test]
    public void UIsDistanceOverRadiusSoTheBeamSharesVanillasFalloff()
    {
        var polygon = Circle(4, 8f);
        int steps = VectorLightBeamMath.StepsFor(8f);

        VectorLightMath.LightMesh mesh = VectorLightBeamMath.BuildOwedMesh(
            0f, 0f, 8f, polygon, Mask(4, steps, (s, i) => s == 0), steps, null, null);

        for (int i = 0; i < mesh.VertexCount; i++)
        {
            float distance = (float)Math.Sqrt(mesh.X[i] * mesh.X[i] + mesh.Z[i] * mesh.Z[i]);

            Assert.That(mesh.U[i], Is.EqualTo(distance / 8f).Within(1e-4f));
            Assert.That(mesh.U[i], Is.InRange(0f, 1f));
            Assert.That(mesh.V[i], Is.Zero, "there is no soft edge to cross on a beam quad");
        }
    }

    // Nothing may be emitted beyond the polygon, however far the mask claims light is owed. The mask
    // is sampled on a grid and the polygon is not, so the two disagree at the rim by construction —
    // and geometry past the boundary would draw light through the wall that stopped it.
    [Test]
    public void GeometryNeverEscapesThePolygon()
    {
        float[] angles = new float[4];
        float[] distances = { 3f, 9f, 9f, 9f };   // one ray stopped short by an occluder

        for (int i = 0; i < 4; i++)
            angles[i] = VectorLightBeamMath.TwoPi * i / 4;

        var polygon = new VectorLightMath.LightPolygon(angles, distances, 4);
        int steps = VectorLightBeamMath.StepsFor(9f);

        VectorLightMath.LightMesh mesh = VectorLightBeamMath.BuildOwedMesh(
            0f, 0f, 9f, polygon, Mask(4, steps, (s, i) => true), steps, null, null);

        for (int i = 0; i < mesh.VertexCount; i++)
        {
            float distance = (float)Math.Sqrt(mesh.X[i] * mesh.X[i] + mesh.Z[i] * mesh.Z[i]);

            Assert.That(distance, Is.LessThanOrEqualTo(9f + 1e-3f), $"vertex {i} escaped the polygon");
        }

        // The short ray is the one that matters: nothing on it may reach past where it stopped.
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            bool onShortRay = Math.Abs(mesh.Z[i]) < 1e-3f && mesh.X[i] > 0f;

            if (onShortRay)
                Assert.That(mesh.X[i], Is.LessThanOrEqualTo(3f + 1e-3f), "drew through the occluder");
        }
    }

    // Every triangle index has to address a vertex that exists. A trimmed buffer whose indices were
    // not trimmed with it is the failure this catches, and in game it presents as a corrupt mesh
    // rather than as an exception.
    [Test]
    public void TriangleIndicesStayInRange()
    {
        var polygon = Circle(48, 10f);
        int steps = VectorLightBeamMath.StepsFor(10f);

        VectorLightMath.LightMesh mesh = VectorLightBeamMath.BuildOwedMesh(
            0f, 0f, 10f, polygon, Mask(48, steps, (s, i) => (s + i) % 3 == 0), steps, null, null);

        Assert.That(mesh.Triangles.Length % 3, Is.Zero);

        foreach (int index in mesh.Triangles)
            Assert.That(index, Is.InRange(0, mesh.VertexCount - 1));
    }

    // A degenerate emitter must not throw or allocate geometry.
    [TestCase(0f)]
    [TestCase(-1f)]
    public void ZeroRadiusIsEmpty(float radius)
    {
        var polygon = Circle(8, 5f);

        VectorLightMath.LightMesh mesh = VectorLightBeamMath.BuildOwedMesh(
            0f, 0f, radius, polygon, new bool[8 * 4], 4, null, null);

        Assert.That(mesh.VertexCount, Is.Zero);
    }

    // THE MOUTH OF THE BEAM IS A CHORD, NOT AN ARC. Every run in this mask starts at the same radius,
    // so without the per-ray clamp both corners of every quad sit at that radius and the beam's near
    // edge bows back towards the lamp between the rays — through the wall, into the lit room. In game
    // that measured as a 40-level lift in a band just inside the doorway on a frame that had read
    // exactly zero the build before.
    [Test]
    public void PerRayNearClampPushesTheMouthOutToTheAperture()
    {
        var polygon = Circle(16, 10f);
        int steps = VectorLightBeamMath.StepsFor(10f);
        bool[] mask = Mask(16, steps, (s, i) => i >= 10);

        float[] near = new float[16];

        for (int i = 0; i < 16; i++)
            near[i] = 4f;

        VectorLightMath.LightMesh clamped = VectorLightBeamMath.BuildOwedMesh(
            0f, 0f, 10f, polygon, mask, steps, near, null);

        for (int i = 0; i < clamped.VertexCount; i++)
        {
            float distance = (float)Math.Sqrt(
                clamped.X[i] * clamped.X[i] + clamped.Z[i] * clamped.Z[i]);

            Assert.That(distance, Is.GreaterThanOrEqualTo(4f - 1e-3f),
                $"vertex {i} sits inside the ray's first owed radius");
        }
    }

    // A ray that owes nothing anywhere hands back a radius past the emitter, and that must collapse
    // its corner onto the polygon boundary rather than throw or draw a wedge off into open ground.
    [Test]
    public void ARayThatOwesNothingCollapsesToThePolygon()
    {
        var polygon = Circle(8, 6f);
        int steps = VectorLightBeamMath.StepsFor(6f);
        float[] near = new float[8];

        for (int i = 0; i < 8; i++)
            near[i] = i == 0 ? 0f : float.MaxValue;

        VectorLightMath.LightMesh mesh = VectorLightBeamMath.BuildOwedMesh(
            0f, 0f, 6f, polygon, Mask(8, steps, (s, i) => true), steps, near, null);

        for (int i = 0; i < mesh.VertexCount; i++)
        {
            float distance = (float)Math.Sqrt(mesh.X[i] * mesh.X[i] + mesh.Z[i] * mesh.Z[i]);

            Assert.That(distance, Is.LessThanOrEqualTo(6f + 1e-3f), "escaped the polygon");
            Assert.That(float.IsNaN(mesh.X[i]) || float.IsInfinity(mesh.X[i]), Is.False,
                "a never-owed ray leaked its sentinel into the geometry");
        }
    }

    [Test]
    public void SectorMidAngleWrapsAcrossTheSeam()
    {
        var polygon = Circle(4, 5f);
        float mid = VectorLightBeamMath.SectorMidAngle(polygon, 3);

        // Sector 3 runs from 3pi/2 back round to 0, so its middle is 7pi/4 -- NOT 3pi/4, which is
        // what a plain average of the two endpoints gives once one of them has wrapped.
        Assert.That(mid, Is.EqualTo(VectorLightBeamMath.TwoPi * 7f / 8f).Within(1e-3f));
    }
}
