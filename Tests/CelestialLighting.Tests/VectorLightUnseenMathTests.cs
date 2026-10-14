using System;
using System.Collections.Generic;

namespace CelestialLighting.Tests;

// The one claim VectorLightUnseenMath makes: when it says a change cannot move a lamp's polygon, a
// rebake after the change would have produced the coverage grid the lamp already holds.
//
// THE DIFFERENTIAL IS THE TEST THAT MATTERS, and it is held to byte equality. Declining a rebake is
// the direction that goes quietly wrong — a lamp holding a shadow the door has already moved renders
// as a beam stopping a cell short in one frame of one scene, with every probe green. So the sweep
// does the rebake anyway, every time the predicate declines one, and compares what the mask would
// have read. The coverage grid rather than the polygon, because the polygon is EXPECTED to differ by
// collinear vertices (see the predicate's header) and the grid is the thing a pixel is made of.
//
// The direct cases below it pin the obvious answers so a later margin change that turns the
// predicate into "always true" — which would pass the differential trivially — fails loudly.
[TestFixture]
public class VectorLightUnseenMathTests
{
    private const int Rays = VectorLightMath.DefaultBaseRayCount;
    private const int Samples = VectorLightMath.DefaultCoverageSamples;

    // ---- the obvious answers -----------------------------------------------------------------

    [Test]
    public void ADoorInPlainViewIsSeen()
    {
        VectorLightMath.Segment[] walls = VectorLightLayout.Grid(g => g.Wall(26, 14, 26, 26));

        Assert.That(Touches(walls, 20, 20, 12f, 26, 20), Is.True);
    }

    [Test]
    public void ADoorBehindAWallIsUnseen()
    {
        // A wall at x = 23 the whole height of the reach, and the door three cells behind it.
        VectorLightMath.Segment[] walls = VectorLightLayout.Grid(g => g.Wall(23, 4, 23, 36));

        Assert.That(Touches(walls, 20, 20, 12f, 26, 20), Is.False);
    }

    [Test]
    public void ADoorBeyondTheRadiusIsUnseen()
    {
        VectorLightMath.Segment[] walls = VectorLightLayout.Grid(g => { });

        Assert.That(Touches(walls, 20, 20, 6f, 30, 20), Is.False);
    }

    // The door in the wall the light is facing: its face IS the polygon's boundary, which is the
    // inclusive edge of the clip. The case an exclusive test would get wrong.
    [Test]
    public void ADoorSetInTheLitWallIsSeen()
    {
        VectorLightMath.Segment[] walls = VectorLightLayout.Grid(g => g.Wall(24, 4, 24, 36));

        Assert.That(Touches(walls, 20, 20, 12f, 24, 20), Is.True);
    }

    // The arc sliver the margin exists for: no wall at all, and a door whose near corner sits
    // between the chord and the circle, on the bisector of two base rays where the chord cuts in
    // furthest. Outside the fan, inside reach — a rebake would aim a ray at that corner and find it.
    //
    // AT VANILLA'S LARGEST RADIUS AND NINE TENTHS OF THE WAY OUT, so the corner is further past the
    // chord than Slack alone reaches: only the sagitta term catches it, and a margin reduced to the
    // constant fails here rather than passing on the slack's coincidence.
    [Test]
    public void ADoorCornerInTheArcSliverIsSeen()
    {
        float radius = 40f;
        float lightX = 20.5f;
        float lightZ = 20.5f;

        VectorLightMath.LightPolygon polygon =
            VectorLightMath.Build(lightX, lightZ, radius, VectorLightLayout.Grid(g => { }), Rays);

        // Base rays sit at -pi + 2*pi*i/Rays, so pi/Rays is half-way between the two either side of 0.
        double bisector = Math.PI / Rays;
        double chord = radius * Math.Cos(bisector);
        double corner = chord + (radius - chord) * 0.9;

        float x = lightX + (float)(corner * Math.Cos(bisector));
        float z = lightZ + (float)(corner * Math.Sin(bisector));

        Assert.That(VectorLightUnseenMath.TouchesRect(
            polygon.Angles, polygon.Distances, polygon.Count, lightX, lightZ,
            x, z, x + 1f, z + 1f, VectorLightUnseenMath.Margin(radius, Rays)), Is.True);
    }

    [Test]
    public void TheMarginGrowsWithTheRadius()
    {
        Assert.That(VectorLightUnseenMath.Margin(40f, Rays),
            Is.GreaterThan(VectorLightUnseenMath.Margin(8f, Rays)));
        Assert.That(VectorLightUnseenMath.Margin(0f, Rays), Is.EqualTo(VectorLightUnseenMath.Slack));
    }

    [Test]
    public void ADegenerateFanAnswersSeen()
    {
        Assert.That(VectorLightUnseenMath.TouchesRect(
            new float[2], new float[2], 2, 0f, 0f, 5f, 5f, 6f, 6f, 0f), Is.True);
    }

    // ---- the differential --------------------------------------------------------------------

    // Three door states — shut (a wall cell), open (nothing), and part-open (a leaf across half the
    // cell, the shape DoorApertureMath emits mid-swing) — and every ordered pair of them, over
    // randomised walls at a fixed seed. Whenever the predicate declines, the rebake is done anyway
    // and its grid must equal the one held.
    [Test]
    public void ADeclinedRebakeWouldHaveChangedNothing()
    {
        Random random = new Random(20261014);

        int declined = 0;
        int asked = 0;

        for (int trial = 0; trial < 400; trial++)
        {
            Scene scene = Scene.Random(random);

            for (int from = 0; from < 3; from++)
            {
                for (int to = 0; to < 3; to++)
                {
                    if (from != to)
                    {
                        asked++;
                        declined += CheckPair(scene, from, to, $"trial {trial} {from}->{to}");
                    }
                }
            }
        }

        // Not vacuous: a predicate that always answered "seen" would pass every comparison above by
        // never making one. The real ratio on this sweep is reported so a reader can see it.
        TestContext.WriteLine($"declined {declined} of {asked}");
        Assert.That(declined, Is.GreaterThan(asked / 10));
    }

    private static int CheckPair(Scene scene, int from, int to, string label)
    {
        VectorLightMath.LightPolygon held = scene.Polygon(from);

        bool seen = VectorLightUnseenMath.TouchesRect(
            held.Angles, held.Distances, held.Count, scene.LightX, scene.LightZ,
            scene.DoorX, scene.DoorZ, scene.DoorX + 1, scene.DoorZ + 1,
            VectorLightUnseenMath.Margin(scene.Radius, Rays));

        if (seen)
            return 0;

        byte[] kept = scene.Coverage(held);
        byte[] rebaked = scene.Coverage(scene.Polygon(to));

        Assert.That(rebaked, Is.EqualTo(kept), label);
        return 1;
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static bool Touches(
        VectorLightMath.Segment[] walls, int lightCellX, int lightCellZ, float radius, int doorX, int doorZ)
    {
        float lightX = lightCellX + 0.5f;
        float lightZ = lightCellZ + 0.5f;

        VectorLightMath.LightPolygon polygon = VectorLightMath.Build(lightX, lightZ, radius, walls, Rays);

        return VectorLightUnseenMath.TouchesRect(
            polygon.Angles, polygon.Distances, polygon.Count, lightX, lightZ,
            doorX, doorZ, doorX + 1, doorZ + 1, VectorLightUnseenMath.Margin(radius, Rays));
    }

    private sealed class Scene
    {
        public int LightCellX;
        public int LightCellZ;
        public float LightX => LightCellX + 0.5f;
        public float LightZ => LightCellZ + 0.5f;
        public float Radius;
        public int DoorX;
        public int DoorZ;
        public bool LeafAlongX;

        private VectorLightLayout layout;

        public static Scene Random(Random random)
        {
            Scene scene = new Scene();
            scene.layout = new VectorLightLayout();

            int walls = random.Next(6, 40);

            for (int i = 0; i < walls; i++)
            {
                int x = random.Next(4, 37);
                int z = random.Next(4, 37);
                int length = random.Next(1, 9);

                if (random.Next(2) == 0)
                    scene.layout.Wall(x, z, x, Math.Min(z + length, 39));
                else
                    scene.layout.Wall(x, z, Math.Min(x + length, 39), z);
            }

            scene.LightCellX = random.Next(10, 31);
            scene.LightCellZ = random.Next(10, 31);
            scene.Radius = 4f + (float)random.NextDouble() * 12f;

            // Anywhere in reach except on the light itself, and often inside a wall line, which is
            // where doors are.
            scene.DoorX = scene.LightCellX;
            scene.DoorZ = scene.LightCellZ;

            while (scene.DoorX == scene.LightCellX && scene.DoorZ == scene.LightCellZ)
            {
                scene.DoorX = scene.LightCellX + random.Next(-16, 17);
                scene.DoorZ = scene.LightCellZ + random.Next(-16, 17);
            }

            scene.LeafAlongX = random.Next(2) == 0;
            scene.layout.Clear(scene.LightCellX, scene.LightCellZ);

            return scene;
        }

        // 0 shut, 1 open, 2 part-open.
        public VectorLightMath.LightPolygon Polygon(int state)
        {
            if (state == 0)
                layout.Wall(DoorX, DoorZ, DoorX, DoorZ);
            else
                layout.Clear(DoorX, DoorZ);

            List<VectorLightMath.Segment> segments = new List<VectorLightMath.Segment>(layout.Segments());

            if (state == 2)
                segments.Add(Leaf());

            return VectorLightMath.Build(LightX, LightZ, Radius, segments.ToArray(), Rays);
        }

        public byte[] Coverage(VectorLightMath.LightPolygon polygon) =>
            VectorLightMath.BuildCoverage(
                polygon, LightCellX, LightCellZ, (int)Math.Ceiling(Radius), Samples);

        // One leaf across the cell's middle, reaching 40% of the way: a door caught mid-swing.
        private VectorLightMath.Segment Leaf()
        {
            float face = (LeafAlongX ? DoorZ : DoorX) + 0.5f;
            float start = LeafAlongX ? DoorX : DoorZ;

            return LeafAlongX
                ? new VectorLightMath.Segment(start, face, start + 0.4f, face)
                : new VectorLightMath.Segment(face, start, face, start + 0.4f);
        }
    }
}
