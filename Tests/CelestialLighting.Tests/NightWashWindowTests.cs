namespace CelestialLighting.Tests;

// Offline unit tests for §9's baked wash lattice (Source/NightWashWindow.cs, linked into this project
// so these run against the exact shipped file).
//
// The window is a pure performance refactor: it replaced 2,601 per-read glow-grid queries per section
// regenerate with one query per cell of a (Section.Size + 2)^2 window. Two things therefore have to be
// proved and not merely asserted in a commit message:
//
//   1. Equivalence. Driving the real vertex loop both ways — resolving on demand with the explicit
//      bounds guard the pre-window WashAt carried, versus baking the window and reading it — must
//      produce byte-identical vertex alphas, including on map-edge sections where the skirt is
//      clipped. LatticeRun below is a faithful transcription of
//      SectionLayer_NightDesaturation.AddCellColors (which cannot be linked here: it touches
//      LayerSubMesh/Color32), and it calls the shipped NightDesaturationMath.CellWash and
//      NightDesaturationMath.WashAlpha rather than paraphrasing either, so "byte-identical" means the
//      bytes that ship.
//   2. The reduction is real. The fake glow grid counts every query, so the 2,601 -> 361 claim is
//      measured here rather than reasoned about.
//
// Note on what does NOT vary in this scene: roofs. §9 reads GroundGlowAt with ignoreSky: true, so a
// roof changes nothing about what this layer sees — it only matters as the *trigger*
// (MapMeshFlagDefOf.Roofs is one of the two flags the layer subscribes to). The variety that matters
// here is glow: unlit, lamp-capped, above the lit-exempt anchor, and the gradients between.
[TestFixture]
public class NightWashWindowTests
{
    // ApiCompatibilityTests pins Verse's Section.Size at 17; hard-coded here so these tests state the
    // real geometry rather than a made-up one.
    private const int SectionSize = 17;

    // A section fully inside a 60x60 map: rect 34..50, skirt 33..51.
    private const int MapSize = 60;
    private const int InnerMin = 34;
    private const int InnerMax = InnerMin + SectionSize - 1;

    // --- Window geometry: the +1 skirt on each side, clipped at the map edge ---

    [Test]
    public void ForSection_InteriorSection_ResolvesTheSectionPlusAOneCellSkirt()
    {
        // (Section.Size + 2)^2 == 19x19 == 361 cells: exactly the union the vertex loop can read, since
        // every cell in the section reads its own eight neighbours.
        NightWashWindow window = Window(InnerMin, InnerMin, InnerMax, InnerMax);

        Assert.Multiple(() =>
        {
            Assert.That(window.MinX, Is.EqualTo(InnerMin - 1));
            Assert.That(window.MinZ, Is.EqualTo(InnerMin - 1));
            Assert.That(window.MaxX, Is.EqualTo(InnerMax + 1));
            Assert.That(window.MaxZ, Is.EqualTo(InnerMax + 1));
            Assert.That(CellCount(window), Is.EqualTo((SectionSize + 2) * (SectionSize + 2)));
        });
    }

    [Test]
    public void ForSection_AtMapOrigin_ClipsTheSkirtRatherThanGoingNegative()
    {
        NightWashWindow window = Window(0, 0, SectionSize - 1, SectionSize - 1);

        Assert.Multiple(() =>
        {
            Assert.That(window.MinX, Is.EqualTo(0));
            Assert.That(window.MinZ, Is.EqualTo(0));
            Assert.That(window.MaxX, Is.EqualTo(SectionSize));
            Assert.That(window.MaxZ, Is.EqualTo(SectionSize));
        });
    }

    [Test]
    public void ForSection_AtFarMapEdge_ClipsTheSkirtToTheLastCell()
    {
        // Regenerate's own rect is already clipped inside the map, so the far edge is a partial section
        // (43..59 here) whose skirt must stop at MapSize - 1.
        NightWashWindow window = Window(43, 43, MapSize - 1, MapSize - 1);

        Assert.Multiple(() =>
        {
            Assert.That(window.MinX, Is.EqualTo(42));
            Assert.That(window.MaxX, Is.EqualTo(MapSize - 1));
            Assert.That(window.MaxZ, Is.EqualTo(MapSize - 1));
        });
    }

    // --- Reads: inside the border, on it, and past it ---

    [Test]
    public void Wash_IsStoredPerCellAndIndependently()
    {
        NightWashWindow window = Window(InnerMin, InnerMin, InnerMax, InnerMax);
        window.Resolve(40, 41, localGlow: 0f);
        window.Resolve(41, 41, localGlow: NightDesaturationMath.LitExemptGlow);

        Assert.Multiple(() =>
        {
            Assert.That(window.At(40, 41), Is.EqualTo(1f));
            Assert.That(window.At(41, 41), Is.EqualTo(0f));

            // Row stride: the cell directly above must not alias the cell to the right.
            Assert.That(window.At(40, 42), Is.EqualTo(0f));
        });
    }

    [Test]
    public void Wash_OnTheSkirtBorder_IsStoredAndReadBack()
    {
        // The border cells are the whole point of the +1: they are read by the section's boundary cells
        // even though the section never emits a vertex for them.
        NightWashWindow window = Window(InnerMin, InnerMin, InnerMax, InnerMax);
        window.Resolve(window.MinX, window.MinZ, localGlow: 0.25f);
        window.Resolve(window.MaxX, window.MaxZ, localGlow: 0f);

        Assert.Multiple(() =>
        {
            Assert.That(window.At(window.MinX, window.MinZ), Is.EqualTo(0.5f));
            Assert.That(window.At(window.MaxX, window.MaxZ), Is.EqualTo(1f));
        });
    }

    [Test]
    public void Wash_PastTheBorder_ReadsAsFullyUnlit()
    {
        // Not a fallback: an off-window cell is an off-map cell, and the pre-window WashAt returned
        // exactly 1f for it so the wash runs off the map edge rather than fading out along it.
        NightWashWindow window = Window(0, 0, SectionSize - 1, SectionSize - 1);

        Assert.Multiple(() =>
        {
            Assert.That(window.At(-1, 4), Is.EqualTo(NightWashWindow.OffMapWash));
            Assert.That(window.At(4, -1), Is.EqualTo(NightWashWindow.OffMapWash));
            Assert.That(window.At(window.MaxX + 1, 4), Is.EqualTo(NightWashWindow.OffMapWash));
            Assert.That(window.At(4, window.MaxZ + 1), Is.EqualTo(NightWashWindow.OffMapWash));
        });
    }

    // --- Equivalence with the pre-refactor per-read resolution ---

    [Test]
    public void Lattice_InteriorSection_MatchesPerReadResolutionExactly()
    {
        FakeGlowGrid map = Scene();
        LatticeRun legacy = LatticeRun.Legacy(map, InnerMin, InnerMin, InnerMax, InnerMax);
        LatticeRun windowed = LatticeRun.Windowed(map, InnerMin, InnerMin, InnerMax, InnerMax);

        AssertSameVertices(legacy, windowed);
    }

    [TestCase(0, 0, TestName = "Lattice_MapOriginSection_MatchesPerReadResolutionExactly")]
    [TestCase(MapSize - SectionSize, MapSize - SectionSize,
        TestName = "Lattice_FarEdgeSection_MatchesPerReadResolutionExactly")]
    [TestCase(0, MapSize - SectionSize,
        TestName = "Lattice_CornerStraddlingSection_MatchesPerReadResolutionExactly")]
    public void Lattice_EdgeSection_MatchesPerReadResolutionExactly(int botLeftX, int botLeftZ)
    {
        // The case the clipped skirt exists for: cells the vertex loop asks about that are off the map,
        // where the window's read-past-the-border answer has to equal the old InBounds guard's 1f.
        FakeGlowGrid map = Scene();
        int maxX = botLeftX + SectionSize - 1;
        int maxZ = botLeftZ + SectionSize - 1;
        LatticeRun legacy = LatticeRun.Legacy(map, botLeftX, botLeftZ, maxX, maxZ);
        LatticeRun windowed = LatticeRun.Windowed(map, botLeftX, botLeftZ, maxX, maxZ);

        AssertSameVertices(legacy, windowed);
    }

    [Test]
    public void Lattice_EveryInteriorSectionOfTheScene_MatchesPerReadResolutionExactly()
    {
        // Sweeps the whole map a section at a time rather than trusting one hand-picked rect to have
        // straddled the interesting structure. Any seam, stride slip or off-by-one in the window shows
        // up on at least one of these.
        FakeGlowGrid map = Scene();

        Assert.Multiple(() =>
        {
            for (int botLeftZ = 0; botLeftZ < MapSize; botLeftZ += SectionSize)
            {
                for (int botLeftX = 0; botLeftX < MapSize; botLeftX += SectionSize)
                {
                    int maxX = Math.Min(botLeftX + SectionSize - 1, MapSize - 1);
                    int maxZ = Math.Min(botLeftZ + SectionSize - 1, MapSize - 1);
                    LatticeRun legacy = LatticeRun.Legacy(map, botLeftX, botLeftZ, maxX, maxZ);
                    LatticeRun windowed = LatticeRun.Windowed(map, botLeftX, botLeftZ, maxX, maxZ);

                    Assert.That(windowed.Alphas, Is.EqualTo(legacy.Alphas),
                        $"vertex alphas diverged for the section at ({botLeftX}, {botLeftZ})");
                }
            }
        });
    }

    // --- The reduction itself ---

    [Test]
    public void Windowed_QueriesEachCellOnceInsteadOfOncePerRead()
    {
        // 9 reads x 17x17 cells == 2,601 glow queries on demand, against 19x19 == 361 baked. This is
        // the whole issue: 7.2x fewer GlowGrid.GroundGlowAt calls for the identical mesh.
        FakeGlowGrid map = Scene();
        LatticeRun legacy = LatticeRun.Legacy(map, InnerMin, InnerMin, InnerMax, InnerMax);
        LatticeRun windowed = LatticeRun.Windowed(map, InnerMin, InnerMin, InnerMax, InnerMax);

        Assert.Multiple(() =>
        {
            Assert.That(legacy.GlowQueries, Is.EqualTo(9 * SectionSize * SectionSize));
            Assert.That(legacy.GlowQueries, Is.EqualTo(2601));
            Assert.That(windowed.GlowQueries, Is.EqualTo((SectionSize + 2) * (SectionSize + 2)));
            Assert.That(windowed.GlowQueries, Is.EqualTo(361));
        });
    }

    [Test]
    public void Windowed_NeverQueriesACellOutsideTheMap()
    {
        // The clipped window is what replaces the per-read InBounds guard; if it were unclipped the
        // fill loop would index off the map instead of the old code simply not asking.
        FakeGlowGrid map = Scene();
        LatticeRun windowed = LatticeRun.Windowed(map, 0, 0, SectionSize - 1, SectionSize - 1);

        Assert.Multiple(() =>
        {
            // 18x18: the skirt exists on two sides only at the map origin.
            Assert.That(windowed.GlowQueries, Is.EqualTo((SectionSize + 1) * (SectionSize + 1)));
            Assert.That(map.OutOfBoundsQueries, Is.EqualTo(0));
        });
    }

    // --- Helpers ---

    private static NightWashWindow Window(int minX, int minZ, int maxX, int maxZ) =>
        NightWashWindow.ForSection(minX, minZ, maxX, maxZ, MapSize, MapSize);

    private static int CellCount(NightWashWindow window) =>
        (window.MaxX - window.MinX + 1) * (window.MaxZ - window.MinZ + 1);

    private static void AssertSameVertices(LatticeRun legacy, LatticeRun windowed) =>
        Assert.That(windowed.Alphas, Is.EqualTo(legacy.Alphas),
            "vertex alphas diverged from per-read resolution");

    // A glow scene with every case §9's ramp distinguishes, laid out so that both the interior section
    // under test and the map-edge sections see real structure rather than flat terrain:
    //   - pitch-dark ground (full wash),
    //   - a lamp: a radial falloff through the whole 0..LitExemptGlow ramp, so neighbouring cells carry
    //     genuinely different washes and any averaging slip shows up,
    //   - a campfire above the lit-exempt anchor (zero wash) sitting inside dark ground,
    //   - lamp-capped light at exactly the anchor, the boundary CellWash switches on,
    //   - lit cells hard against the map edge, where the off-map 1f meets a 0f cell.
    private static FakeGlowGrid Scene()
    {
        FakeGlowGrid map = new FakeGlowGrid(MapSize);

        // A lamp near the middle of the interior section under test, with a linear falloff over 6 cells.
        map.Lamp(42, 42, radius: 6, peak: NightDesaturationMath.LitExemptGlow);

        // A second lamp straddling the section boundary, so the skirt cells carry non-trivial values.
        map.Lamp(InnerMin - 1, InnerMax + 1, radius: 4, peak: 0.4f);

        // A campfire: above the anchor, so it and its immediate surroundings are exempt entirely.
        map.Lamp(48, 36, radius: 2, peak: 0.9f);

        // Flat lamp-capped light, exactly at the anchor — the value CellWash switches to zero at.
        map.Fill(34, 46, 38, 50, NightDesaturationMath.LitExemptGlow);

        // Light pressed against all four map edges, so the edge sections average real light against the
        // off-map 1f rather than 0 against 1f.
        map.Lamp(0, 0, radius: 5, peak: 0.5f);
        map.Lamp(MapSize - 1, MapSize - 1, radius: 5, peak: 0.35f);
        map.Lamp(0, MapSize - 1, radius: 3, peak: 0.2f);
        map.Fill(20, 0, 26, 1, 0.3f);

        return map;
    }

    // Stands in for the live Map's glow grid. GroundGlowAt mirrors what the layer asks for
    // (ignoreSky: true — local light only), and counts queries so the reduction can be measured rather
    // than assumed.
    private sealed class FakeGlowGrid
    {
        private readonly int size;
        private readonly float[] glow;

        public FakeGlowGrid(int size)
        {
            this.size = size;
            glow = new float[size * size];
        }

        public int Size => size;

        public int GlowQueries { get; private set; }

        public int OutOfBoundsQueries { get; private set; }

        public void ResetCounters()
        {
            GlowQueries = 0;
            OutOfBoundsQueries = 0;
        }

        public bool InBounds(int x, int z) => x >= 0 && x < size && z >= 0 && z < size;

        public void Fill(int minX, int minZ, int maxX, int maxZ, float value)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                    Raise(x, z, value);
            }
        }

        // Linear falloff from `peak` at the centre to 0 at `radius`, taking the max where lamps overlap
        // — the same "brightest source wins" shape GlowGrid produces, and enough to give every cell in
        // a neighbourhood a different value.
        public void Lamp(int centreX, int centreZ, int radius, float peak)
        {
            for (int z = centreZ - radius; z <= centreZ + radius; z++)
            {
                for (int x = centreX - radius; x <= centreX + radius; x++)
                {
                    double distance = Math.Sqrt((x - centreX) * (x - centreX) + (z - centreZ) * (z - centreZ));
                    if (InBounds(x, z) && distance <= radius)
                        Raise(x, z, (float)(peak * (1.0 - distance / radius)));
                }
            }
        }

        public float GroundGlowAt(int x, int z)
        {
            GlowQueries++;
            if (!InBounds(x, z))
            {
                OutOfBoundsQueries++;
                return 0f;
            }

            return glow[z * size + x];
        }

        private void Raise(int x, int z, float value)
        {
            int i = z * size + x;
            if (value > glow[i])
                glow[i] = value;
        }
    }

    // SectionLayer_NightDesaturation's vertex loop, run either way. Only the source of a cell's wash
    // differs between Legacy and Windowed; the arithmetic below is one copy shared by both, transcribed
    // from AddCellColors including its vertex order.
    private sealed class LatticeRun
    {
        private LatticeRun(byte[] alphas, int glowQueries)
        {
            Alphas = alphas;
            GlowQueries = glowQueries;
        }

        // Only the alphas: AddCellColors' RGB is the constant white the material multiplies against, so
        // the alpha channel is the entire per-vertex output.
        public byte[] Alphas { get; }

        public int GlowQueries { get; }

        // The pre-refactor shape: one glow query per read, with the explicit map-bounds guard WashAt
        // used to carry.
        public static LatticeRun Legacy(FakeGlowGrid map, int minX, int minZ, int maxX, int maxZ)
        {
            map.ResetCounters();

            float WashAt(int x, int z) =>
                map.InBounds(x, z) ? NightDesaturationMath.CellWash(map.GroundGlowAt(x, z)) : 1f;

            return Run(map, minX, minZ, maxX, maxZ, WashAt);
        }

        // The shipped shape: bake the window once, then read it.
        public static LatticeRun Windowed(FakeGlowGrid map, int minX, int minZ, int maxX, int maxZ)
        {
            map.ResetCounters();

            NightWashWindow window =
                NightWashWindow.ForSection(minX, minZ, maxX, maxZ, map.Size, map.Size);

            for (int z = window.MinZ; z <= window.MaxZ; z++)
            {
                for (int x = window.MinX; x <= window.MaxX; x++)
                    window.Resolve(x, z, map.GroundGlowAt(x, z));
            }

            return Run(map, minX, minZ, maxX, maxZ, window.At);
        }

        private static LatticeRun Run(
            FakeGlowGrid map, int minX, int minZ, int maxX, int maxZ, Func<int, int, float> washAt)
        {
            List<byte> alphas = new List<byte>();

            // x outer, z inner: SectionLayerGeometryMaker_Solid emits its base geometry in that order,
            // so the colour list has to be appended in it too.
            for (int x = minX; x <= maxX; x++)
            {
                for (int z = minZ; z <= maxZ; z++)
                    AddCellAlphas(alphas, washAt, x, z);
            }

            return new LatticeRun(alphas.ToArray(), map.GlowQueries);
        }

        private static void AddCellAlphas(List<byte> alphas, Func<int, int, float> washAt, int x, int z)
        {
            float here = washAt(x, z);
            float west = washAt(x - 1, z);
            float east = washAt(x + 1, z);
            float south = washAt(x, z - 1);
            float north = washAt(x, z + 1);

            byte bottomLeft = Alpha((here + west + south + washAt(x - 1, z - 1)) * 0.25f);
            byte topLeft = Alpha((here + west + north + washAt(x - 1, z + 1)) * 0.25f);
            byte topRight = Alpha((here + east + north + washAt(x + 1, z + 1)) * 0.25f);
            byte bottomRight = Alpha((here + east + south + washAt(x + 1, z - 1)) * 0.25f);

            alphas.Add(bottomLeft);
            alphas.Add(Alpha((here + west) * 0.5f));
            alphas.Add(topLeft);
            alphas.Add(Alpha((here + north) * 0.5f));
            alphas.Add(topRight);
            alphas.Add(Alpha((here + east) * 0.5f));
            alphas.Add(bottomRight);
            alphas.Add(Alpha((here + south) * 0.5f));
            alphas.Add(Alpha(here));
        }

        private static byte Alpha(float wash) => NightDesaturationMath.WashAlpha(wash);
    }
}
