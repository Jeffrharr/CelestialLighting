using System;
using NUnit.Framework;

namespace CelestialLighting.Tests;

// The one claim the per-emitter shadow box (VectorLightMath.CoverageShadowBounds) makes: the mask's
// shadow stage can walk the box instead of the whole square and produce the same sums to the byte.
//
// THREE HALVES, BECAUSE THE BOX RESTS ON A CLAIM ABOUT VANILLA. First, the reach lemma:
// VectorLightLiftMath.VanillaCanDeliver says where vanilla's flood can put light, and that is held
// against VanillaGlowFlood — the job transcribed from decompiled source, sharing no code with the
// predicate — over random wall layouts and radii: no cell the flood lit is one the predicate
// denied. Second, the definition: every cell outside the box is either fully lit or denied, and
// every edge of the box touches a cell that is neither, so the box is exact rather than merely
// safe. Third, the replay: the shadow stage's own arithmetic — `own * (255 - coverage) / 255` with
// the flood's colour as `own` — accumulated over each section tile twice on the polygon fixture's
// scenes, once over the square and once clipped to the box, and the two must be identical. That is
// the claim the live counters check the other half of: shadow_cells_edited must read the same on
// both arms of stress_light_mask_bounds.json.
//
// WHY THE SCENES ARE THE POLYGON FIXTURE'S. The box is read off a coverage grid, and a coverage
// grid's shadow has a shape only real geometry produces: a wedge behind a wall, a ring of rim cells
// an inscribed 48-gon reads as partly lit, the near-total shadow of a sealed room. A random grid
// exercises the arithmetic; these exercise the shapes the box is for.
//
// WHY THE FIRST CUT OF THE BOX IS A TEST HERE. Bounding on coverage alone gives the whole square
// for every emitter — the grid marks the corners outside the disc fully dark — and that version
// would have passed a replay test perfectly while saving nothing. AnUnobstructedEmitterHasNoBox
// is the assertion that would have failed it.
[TestFixture]
public class VectorLightShadowBoundsTests
{
    private const int Rays = VectorLightMath.DefaultBaseRayCount;
    private const int Samples = VectorLightMath.DefaultCoverageSamples;

    // ---- the definition ----------------------------------------------------------------------

    // ---- the reach lemma ---------------------------------------------------------------------

    // No cell the flood lit is one the predicate denies, over random walls and radii, colours
    // bright enough that a reached cell rounds to a non-zero byte. The converse is not asserted:
    // a reached cell can still round to black, and the box only needs the one direction.
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void VanillaNeverLightsACellThePredicateDenies(int seed)
    {
        Random random = new Random(seed);

        for (int round = 0; round < 60; round++)
        {
            float glowRadius = (float)(1.0 + random.NextDouble() * 13.5);
            int radius = (int)Math.Ceiling(glowRadius);
            int span = radius * 2 + 1;
            bool[] blocked = new bool[span * span];
            int walls = random.Next(0, 12);

            for (int k = 0; k < walls; k++)
                blocked[random.Next(blocked.Length)] = true;

            VanillaGlowFlood.Result flood = VanillaGlowFlood.Flood(
                255, 255, 255, glowRadius,
                (dx, dz) => blocked[(dz + radius) * span + (dx + radius)]);

            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int i = flood.Index(dx, dz);
                    bool lit = flood.R[i] > 0 || flood.G[i] > 0 || flood.B[i] > 0;

                    if (lit)
                    {
                        Assert.That(
                            VectorLightLiftMath.VanillaCanDeliver(dx, dz, glowRadius), Is.True,
                            $"seed {seed} round {round}: flood lit ({dx}, {dz}) at radius {glowRadius}");
                    }
                }
            }
        }
    }

    // The rule's edge, by hand, at the two radii where rounding decides: 5.995 rounds to 600 and
    // admits a straight run of five cells; 5.994 rounds to 599 and does not.
    [TestCase(5, 0, 5.995f, true)]
    [TestCase(5, 0, 5.994f, false)]
    [TestCase(3, 3, 5.23f, true)]   // 100 + 3 * 141 = 523
    [TestCase(3, 3, 5.22f, false)]
    [TestCase(0, 0, 1f, true)]      // the seed alone
    [TestCase(0, 0, 0.99f, false)]
    public void ThePredicateReadsTheJobsBudget(int dx, int dz, float glowRadius, bool expected)
    {
        Assert.That(VectorLightLiftMath.VanillaCanDeliver(dx, dz, glowRadius), Is.EqualTo(expected));
    }

    // ---- the definition ----------------------------------------------------------------------

    [Test]
    public void ANullGridHasNoShadow()
    {
        Assert.That(VectorLightMath.CoverageShadowBounds(null, 4, 4f).Empty, Is.True);
        Assert.That(VectorLightMath.CoverageShadowBounds(new byte[0], 4, 4f).Empty, Is.True);
    }

    [Test]
    public void AFullyLitGridHasNoShadow()
    {
        byte[] grid = new byte[9 * 9];
        Array.Fill(grid, (byte)255);

        Assert.That(VectorLightMath.CoverageShadowBounds(grid, 4, 4f).Empty, Is.True);
    }

    [Test]
    public void NoneIsEmptyAndACellIsATightBox()
    {
        Assert.That(VectorLightMath.ShadowBounds.None.Empty, Is.True);

        byte[] grid = new byte[9 * 9];
        Array.Fill(grid, (byte)255);
        grid[(4 + 2) * 9 + (4 - 3)] = 254; // offset (-3, +2): octile 100 + 100 + 2 * 141 = 482

        VectorLightMath.ShadowBounds box = VectorLightMath.CoverageShadowBounds(grid, 4, 4.82f);

        Assert.That(box.Empty, Is.False);
        Assert.That((box.MinDx, box.MaxDx, box.MinDz, box.MaxDz), Is.EqualTo((-3, -3, 2, 2)));

        // And the same cell one hundredth of a radius out of vanilla's reach is no box at all.
        Assert.That(VectorLightMath.CoverageShadowBounds(grid, 4, 4.81f).Empty, Is.True);
    }

    // A zero grid — the polygon that never got built — is shadow everywhere, and the box has to
    // say everything vanilla can reach rather than nothing, or the mask would skip an emitter
    // whose grid says every cell is dark. At radius 3 the reach is the octile disc: the corners
    // (100 + 3 * 141 = 523 > 300) are out, the axes (100 + 300 = 400 > 300) are out too, so the
    // box is the offsets with octile cost at most 300: two cells along an axis.
    [Test]
    public void AnAllDarkGridIsVanillasWholeReach()
    {
        VectorLightMath.ShadowBounds box = VectorLightMath.CoverageShadowBounds(new byte[7 * 7], 3, 3f);

        Assert.That((box.MinDx, box.MaxDx, box.MinDz, box.MaxDz), Is.EqualTo((-2, 2, -2, 2)));
    }

    [TestCase(1)]
    [TestCase(7)]
    [TestCase(13)]
    public void RandomGridsAreBoundedExactly(int seed)
    {
        Random random = new Random(seed);

        for (int round = 0; round < 200; round++)
        {
            int radius = random.Next(0, 12);
            int span = radius * 2 + 1;
            byte[] grid = new byte[span * span];
            Array.Fill(grid, (byte)255);

            // Sparse, so most rounds have a box well inside the square and some have none.
            int shadowed = random.Next(0, 4);

            for (int k = 0; k < shadowed; k++)
                grid[random.Next(grid.Length)] = (byte)random.Next(0, 255);

            AssertExact(grid, radius, radius, $"seed {seed} round {round}");
        }
    }

    // ---- the polygon fixture's scenes -----------------------------------------------------------

    // THE CASE THE WHOLE BOX IS FOR. An open-ground lamp's grid is dark at the corners and partly
    // lit around the rim — 248 of 841 cells under 255 at radius 14 — and every one of them is
    // outside vanilla's reach. A box on coverage alone reads the whole square here; the real one
    // reads nothing, and the mask does not walk the emitter at all.
    [Test]
    public void AnUnobstructedEmitterHasNoBox()
    {
        VectorLightMath.ShadowBounds box = AssertScene(g => { }, 20.5f, 20.5f, 14f, expectEdits: false);

        Assert.That(box.Empty, Is.True);
    }

    [Test]
    public void ASingleWallIsBoundedExactlyAndOnOneSide()
    {
        // The wall runs north-south six cells east of the lamp; its shadow lies entirely east of
        // it, and the box is what lets the sections west of the lamp decline the emitter.
        VectorLightMath.ShadowBounds box = AssertScene(g => g.Wall(20, 12, 20, 28), 14.5f, 20.5f, 14f);

        Assert.That(box.Empty, Is.False);
        Assert.That(box.MinDx, Is.GreaterThan(0), "shadow starts east of the lamp");
    }

    [Test]
    public void AClutteredColonyIsBoundedExactly()
    {
        AssertScene(VectorLightLayout.RoomBlock, 20.5f, 20.5f, 14f);
    }

    [Test]
    public void FreeStandingPillarsAreBoundedExactly()
    {
        AssertScene(g => g.Pillars(3), 20.5f, 20.5f, 14f);
    }

    // The lamp sits inside a solid block, so vanilla's flood never leaves its own cell and the
    // stage edits nothing — the box is a superset here (coverage is dark all round and the reach
    // test cannot see walls), and the replay is what says the superset costs nothing but a walk.
    [Test]
    public void AnEmitterSealedInASmallRoomIsBoundedExactly()
    {
        AssertScene(g => g.Wall(17, 17, 23, 23), 20.5f, 20.5f, 14f, expectEdits: false);
    }

    // A lamp whose vanilla radius is not an integer, since the fixture's other radii all are and
    // the rounding in the budget is exactly where an off-by-one would live. A three-cell wall
    // three cells east, so that vanilla's flood both reaches it and gets round its ends to light
    // the cells behind it — the fixture's long wall six cells out is past a 6.4 reach entirely,
    // and a long one closer in is reached but not rounded, and either way the scene edits nothing.
    // (An edit needs vanilla to have lit a cell our polygon shadows, which takes a detour.)
    [TestCase(6.4f)]
    [TestCase(6.5f)]
    [TestCase(6.6f)]
    [TestCase(7.05f)]
    public void AFractionalRadiusIsBoundedExactly(float radius)
    {
        AssertScene(g => g.Wall(17, 19, 17, 21), 14.5f, 20.5f, radius);
    }

    [TestCase(0f)]
    [TestCase((float)Math.PI)]
    [TestCase((float)(Math.PI / 2.0))]
    [TestCase((float)(-Math.PI / 2.0))]
    [TestCase(2.7f)]
    [TestCase(-2.7f)]
    public void AWallOnAnyBearingIsBoundedExactly(float bearing)
    {
        int wx = 20 + (int)Math.Round(8.0 * Math.Cos(bearing));
        int wz = 20 + (int)Math.Round(8.0 * Math.Sin(bearing));

        AssertScene(g => g.Wall(wx, wz, wx, wz), 20.5f, 20.5f, 14f);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static VectorLightMath.ShadowBounds AssertScene(
        Action<VectorLightLayout> build, float lightX, float lightZ, float radius, bool expectEdits = true)
    {
        VectorLightLayout layout = new VectorLightLayout();
        build(layout);

        VectorLightMath.LightPolygon polygon = VectorLightMath.Build(
            lightX, lightZ, radius, layout.Segments(), Rays);
        int cellX = (int)Math.Floor(lightX);
        int cellZ = (int)Math.Floor(lightZ);
        int radiusCells = (int)Math.Ceiling(radius);

        byte[] grid = VectorLightMath.BuildCoverage(polygon, cellX, cellZ, radiusCells, Samples);

        // Vanilla's own answer for `own`, from the oracle, over the same edifices.
        VanillaGlowFlood.Result flood = VanillaGlowFlood.Flood(
            255, 200, 150, radius, (dx, dz) => layout.Blocked(cellX + dx, cellZ + dz));

        AssertExact(grid, radiusCells, radius, "scene");
        AssertSameShadowSums(grid, cellX, cellZ, radiusCells, radius, flood, expectEdits);

        return VectorLightMath.CoverageShadowBounds(grid, radiusCells, radius);
    }

    // Every cell outside the box is fully lit or out of vanilla's reach; every edge of the box
    // touches a cell that is neither.
    private static void AssertExact(byte[] grid, int radiusCells, float glowRadius, string what)
    {
        VectorLightMath.ShadowBounds box = VectorLightMath.CoverageShadowBounds(grid, radiusCells, glowRadius);
        int span = radiusCells * 2 + 1;
        bool anyShadow = false;
        bool minDx = false, maxDx = false, minDz = false, maxDz = false;

        for (int zi = 0; zi < span; zi++)
        {
            for (int xi = 0; xi < span; xi++)
            {
                int dx = xi - radiusCells;
                int dz = zi - radiusCells;
                bool inside = !box.Empty
                    && dx >= box.MinDx && dx <= box.MaxDx && dz >= box.MinDz && dz <= box.MaxDz;
                bool dark = grid[zi * span + xi] < 255
                    && VectorLightLiftMath.VanillaCanDeliver(dx, dz, glowRadius);

                anyShadow |= dark;

                if (dark)
                {
                    Assert.That(inside, Is.True, $"shadowed cell ({dx}, {dz}) outside box, {what}");
                    minDx |= dx == box.MinDx;
                    maxDx |= dx == box.MaxDx;
                    minDz |= dz == box.MinDz;
                    maxDz |= dz == box.MaxDz;
                }
            }
        }

        Assert.That(box.Empty, Is.EqualTo(!anyShadow), $"emptiness, {what}");

        if (anyShadow)
            Assert.That(minDx && maxDx && minDz && maxDz, Is.True, $"box not tight, {what}");
    }

    // The shadow stage's arithmetic, over every 17x17 section tile of a 3x3 block around the lamp
    // plus the one-cell margin it accumulates into, walked over the vanilla square and over the
    // square clipped to the box, with the flood's colour as `own`. The two sums fold each cell's
    // position in, so agreeing means the same amount was subtracted at the same cells rather than
    // the same total somewhere.
    private static void AssertSameShadowSums(
        byte[] grid, int cellX, int cellZ, int radiusCells, float glowRadius, VanillaGlowFlood.Result flood,
        bool expectEdits)
    {
        const int sectionSize = 17;
        const int margin = 1;

        VectorLightMath.ShadowBounds box = VectorLightMath.CoverageShadowBounds(grid, radiusCells, glowRadius);
        long edits = 0;

        for (int sz = -1; sz <= 1; sz++)
        {
            for (int sx = -1; sx <= 1; sx++)
            {
                int rectMinX = (cellX / sectionSize + sx) * sectionSize;
                int rectMinZ = (cellZ / sectionSize + sz) * sectionSize;
                int rectMaxX = rectMinX + sectionSize - 1;
                int rectMaxZ = rectMinZ + sectionSize - 1;

                // The vanilla square, clamped to the section's accumulation grid.
                int minX = Math.Max(cellX - radiusCells, rectMinX - margin);
                int maxX = Math.Min(cellX + radiusCells, rectMaxX + margin);
                int minZ = Math.Max(cellZ - radiusCells, rectMinZ - margin);
                int maxZ = Math.Min(cellZ + radiusCells, rectMaxZ + margin);

                long full = Walk(grid, cellX, cellZ, radiusCells, flood, minX, maxX, minZ, maxZ, ref edits);

                bool reaches = !box.Empty
                    && cellX + box.MaxDx >= rectMinX - margin
                    && cellX + box.MinDx <= rectMaxX + margin
                    && cellZ + box.MaxDz >= rectMinZ - margin
                    && cellZ + box.MinDz <= rectMaxZ + margin;

                long clipped = 0;
                long ignored = 0;

                if (reaches)
                {
                    clipped = Walk(
                        grid, cellX, cellZ, radiusCells, flood,
                        Math.Max(minX, cellX + box.MinDx), Math.Min(maxX, cellX + box.MaxDx),
                        Math.Max(minZ, cellZ + box.MinDz), Math.Min(maxZ, cellZ + box.MaxDz), ref ignored);
                }

                Assert.That(clipped, Is.EqualTo(full), $"section ({sx}, {sz})");
            }
        }

        // A scene whose walls shadow nothing vanilla lit would pass the replay vacuously; the
        // fixture's walled scenes put real shadow inside the disc, and this says so.
        if (expectEdits)
            Assert.That(edits, Is.GreaterThan(0), "the walled scene edited nothing");
    }

    private static long Walk(
        byte[] grid, int cellX, int cellZ, int radiusCells, VanillaGlowFlood.Result flood,
        int minX, int maxX, int minZ, int maxZ, ref long edits)
    {
        long sum = 0;

        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                int coverage = VectorLightMath.CoverageAt(grid, cellX, cellZ, radiusCells, x, z);

                if (coverage < 255)
                {
                    int dx = x - cellX;
                    int dz = z - cellZ;
                    int own = flood.InRange(dx, dz)
                        ? flood.R[flood.Index(dx, dz)] + flood.G[flood.Index(dx, dz)] + flood.B[flood.Index(dx, dz)]
                        : 0;

                    if (own > 0)
                    {
                        sum += (own * (255 - coverage) / 255) * (1L + x * 1000L + z * 1000000L);
                        edits++;
                    }
                }
            }
        }

        return sum;
    }
}
