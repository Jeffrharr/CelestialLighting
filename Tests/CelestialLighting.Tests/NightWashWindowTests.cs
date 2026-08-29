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
//   2. The reduction is real. The fake glow grid counts every query, so the 2,601 -> 441 claim is
//      measured here rather than reasoned about.
//
// The window is no longer *only* a performance refactor: it also reduces a light-blocking cell's
// meaningless glow reading to its neighbours' (issue #191, mirroring Verse.SectionLayer_Darkness).
// LatticeRun.Legacy below is therefore an INDEPENDENT ORACLE and not a second call into the code under
// test — it transcribes vanilla's LightAt and the pre-window per-read loop straight off the map, with
// no NightWashWindow anywhere in it — so `windowed == legacy` is a real claim rather than x - x == 0.
//
// Note on what does NOT vary in this scene: roofs. §9 reads GroundGlowAt with ignoreSky: true, so a
// roof changes nothing about what this layer sees — it only matters as the *trigger*
// (MapMeshFlagDefOf.Roofs is one of the two flags the layer subscribes to). The variety that matters
// here is glow — unlit, lamp-capped, above the lit-exempt anchor, and the gradients between — and,
// since the wall fix, edifices: a straight run, a corner, a block thick enough to have a cell whose
// every neighbour is also wall, and walls sitting on both a section boundary and the map edge.
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
    public void ForSection_InteriorSection_ReadsTheSectionPlusAOneCellSkirt()
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
    public void ForSection_InteriorSection_GathersOneCellWiderThanItReads()
    {
        // Seal answers a blocked cell with its eight neighbours, so every cell the vertex loop can read
        // needs its own neighbourhood gathered — one cell past the readable skirt, 21x21 == 441.
        NightWashWindow window = Window(InnerMin, InnerMin, InnerMax, InnerMax);

        Assert.Multiple(() =>
        {
            Assert.That(window.FillMinX, Is.EqualTo(InnerMin - 2));
            Assert.That(window.FillMinZ, Is.EqualTo(InnerMin - 2));
            Assert.That(window.FillMaxX, Is.EqualTo(InnerMax + 2));
            Assert.That(window.FillMaxZ, Is.EqualTo(InnerMax + 2));
            Assert.That(FillCellCount(window), Is.EqualTo((SectionSize + 4) * (SectionSize + 4)));
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

            // Both skirts clip, and the wider one does not walk two cells off the map to do it.
            Assert.That(window.FillMinX, Is.EqualTo(0));
            Assert.That(window.FillMinZ, Is.EqualTo(0));
            Assert.That(window.FillMaxX, Is.EqualTo(SectionSize + 1));
            Assert.That(window.FillMaxZ, Is.EqualTo(SectionSize + 1));
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

            Assert.That(window.FillMinX, Is.EqualTo(41));
            Assert.That(window.FillMaxX, Is.EqualTo(MapSize - 1));
            Assert.That(window.FillMaxZ, Is.EqualTo(MapSize - 1));
        });
    }

    // --- Reads: inside the border, on it, and past it ---

    [Test]
    public void Wash_IsStoredPerCellAndIndependently()
    {
        NightWashWindow window = Fill(
            Window(InnerMin, InnerMin, InnerMax, InnerMax), glow: NightDesaturationMath.LitExemptGlow);
        window.Resolve(40, 41, localGlow: 0f, blocksLight: false);
        window.Resolve(41, 41, localGlow: NightDesaturationMath.LitExemptGlow, blocksLight: false);
        window.Seal();

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
        NightWashWindow window = Fill(
            Window(InnerMin, InnerMin, InnerMax, InnerMax), glow: NightDesaturationMath.LitExemptGlow);
        window.Resolve(window.MinX, window.MinZ, localGlow: 0.25f, blocksLight: false);
        window.Resolve(window.MaxX, window.MaxZ, localGlow: 0f, blocksLight: false);
        window.Seal();

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


    // --- Light-blocking cells: the wall diamond (issue #191) ---

    [Test]
    public void Wash_OnAWall_TakesItsBrightestNonBlockingNeighbourRatherThanItsOwnZero()
    {
        // The whole defect in one cell. A wall reads ~0 from the glow grid however brightly lit its
        // faces are, because vanilla's flood never enters it, so the raw reading means "nobody asked"
        // and not "dark". The lit cell here is DIAGONAL to the wall, which also pins that the walk
        // covers all eight neighbours the way Verse.SectionLayer_Darkness.LightAt does and not just
        // the four orthogonals.
        FakeGlowGrid map = new FakeGlowGrid(MapSize);
        map.Fill(40, 40, 40, 40, NightDesaturationMath.LitExemptGlow);
        map.Wall(41, 41, 41, 41);

        NightWashWindow window = Bake(map, InnerMin, InnerMin, InnerMax, InnerMax);

        Assert.Multiple(() =>
        {
            Assert.That(window.At(41, 41), Is.EqualTo(0f), "the wall should read as lit as its lit face");
            Assert.That(window.At(42, 42), Is.EqualTo(1f), "open ground two cells out is still unlit");
        });
    }

    [Test]
    public void Wash_DeepInsideAThickWall_StaysFullyUnlit()
    {
        // The limit of the rule, and the reason it skips neighbours that are themselves blockers: a
        // cell whose every neighbour is also wall has no face on any lit side, so it is genuinely
        // unlit and must not borrow the light sitting one cell beyond the block.
        FakeGlowGrid map = new FakeGlowGrid(MapSize);
        map.Fill(48, 48, 56, 56, NightDesaturationMath.LitExemptGlow);
        map.Wall(51, 51, 53, 53);

        NightWashWindow window = Bake(map, InnerMin, InnerMin, InnerMax, InnerMax);

        Assert.Multiple(() =>
        {
            Assert.That(window.At(52, 52), Is.EqualTo(1f), "the middle of the block has no lit face");
            Assert.That(window.At(51, 51), Is.EqualTo(0f), "its corner does, diagonally");
        });
    }

    [Test]
    public void Lattice_AWallInUniformlyLitGround_EmitsNineIdenticalVertices()
    {
        // The artifact stated as the property that rules it out, and the reason this is a *mesh* bug
        // rather than a shade-too-dark one. Ground lit flat, one wall cell in the middle of it:
        // nothing in the neighbourhood varies, so a correct bake has nothing to shade and all nine of
        // the cell's vertices must carry one alpha.
        //
        // The second arm is the pre-fix bake, kept so the red stays legible and measured rather than
        // surviving as prose in a commit message: it put 255 on the centre vertex against 159 on the
        // corners, and SectionLayerGeometryMaker_Solid fans four triangles out of that centre — which
        // is the dark diamond apparently radiating from the middle of every wall tile.
        FakeGlowGrid map = new FakeGlowGrid(MapSize);
        map.Fill(0, 0, MapSize - 1, MapSize - 1, NightDesaturationMath.LitExemptGlow * 0.5f);
        map.Wall(30, 30, 30, 30);

        byte[] sealedAlphas = LatticeRun.Windowed(map, 30, 30, 30, 30).Alphas;
        byte[] beforeFix = LatticeRun.Unfixed(map, 30, 30, 30, 30).Alphas;

        Assert.Multiple(() =>
        {
            Assert.That(sealedAlphas, Has.Length.EqualTo(9));
            Assert.That(sealedAlphas, Is.All.EqualTo((byte)128),
                "a wall in flat light must shade flat");

            Assert.That(beforeFix[CentreVertex], Is.EqualTo(255), "pre-fix: the centre vertex spiked");
            Assert.That(beforeFix[0], Is.EqualTo(159), "pre-fix: its corners did not");
        });
    }

    [Test]
    public void Wash_OnAWallInTheSkirt_IsTheSameFromEitherSectionThatBakesIt()
    {
        // Why the gathered skirt is two cells and not one. A skirt cell is baked independently by both
        // sections that share it, and both feed it to the vertices on their shared boundary — so if a
        // blocked skirt cell resolved from only the context its own section happened to hold, the two
        // would disagree and print a seam every 17 cells.
        FakeGlowGrid map = Scene();
        NightWashWindow left = Bake(map, InnerMin - SectionSize, InnerMin, InnerMin - 1, InnerMax);
        NightWashWindow right = Bake(map, InnerMin, InnerMin, InnerMax, InnerMax);

        Assert.Multiple(() =>
        {
            for (int z = InnerMin; z <= InnerMax; z++)
            {
                Assert.That(right.At(InnerMin - 1, z), Is.EqualTo(left.At(InnerMin - 1, z)),
                    $"the shared column disagreed at z == {z}");
            }
        });
    }

    // --- Equivalence with per-read resolution ---

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
        // 9 reads x 17x17 cells == 2,601 glow queries on demand, against 21x21 == 441 baked: 5.9x
        // fewer GlowGrid.GroundGlowAt calls for the identical mesh. The gathered window went 361 -> 441
        // when Seal started needing a blocked cell's neighbourhood, which is the whole price of the
        // wall fix — against the 3,249 the rejected alternative would have cost a section cut out of
        // solid rock (see NightWashWindow's header).
        FakeGlowGrid map = Scene();
        LatticeRun legacy = LatticeRun.Legacy(map, InnerMin, InnerMin, InnerMax, InnerMax);
        LatticeRun windowed = LatticeRun.Windowed(map, InnerMin, InnerMin, InnerMax, InnerMax);

        Assert.Multiple(() =>
        {
            // 9 per cell, plus the oracle's extra neighbour walk on each blocked cell it meets — the
            // shape the window exists to collapse, whichever of the two terms dominates.
            Assert.That(legacy.GlowQueries, Is.GreaterThanOrEqualTo(9 * SectionSize * SectionSize));
            Assert.That(legacy.GlowQueries, Is.GreaterThanOrEqualTo(2601));
            Assert.That(windowed.GlowQueries, Is.EqualTo((SectionSize + 4) * (SectionSize + 4)));
            Assert.That(windowed.GlowQueries, Is.EqualTo(441));
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
            // 19x19: the gathered skirt exists on two sides only at the map origin.
            Assert.That(windowed.GlowQueries, Is.EqualTo((SectionSize + 2) * (SectionSize + 2)));
            Assert.That(map.OutOfBoundsQueries, Is.EqualTo(0));
        });
    }

    // --- Helpers ---

    // SectionLayerGeometryMaker_Solid's ninth vertex: the one at the cell's exact middle, with four
    // triangles fanning out of it.
    private const int CentreVertex = 8;

    private static NightWashWindow Window(int minX, int minZ, int maxX, int maxZ) =>
        NightWashWindow.ForSection(minX, minZ, maxX, maxZ, MapSize, MapSize);

    // One section's window, gathered off the fake grid and sealed — what ResolveWash does live.
    private static NightWashWindow Bake(FakeGlowGrid map, int minX, int minZ, int maxX, int maxZ)
    {
        NightWashWindow window = NightWashWindow.ForSection(minX, minZ, maxX, maxZ, map.Size, map.Size);
        for (int z = window.FillMinZ; z <= window.FillMaxZ; z++)
        {
            for (int x = window.FillMinX; x <= window.FillMaxX; x++)
                window.Resolve(x, z, map.GroundGlowAt(x, z), map.BlocksLight(x, z));
        }

        window.Seal();
        return window;
    }

    private static int CellCount(NightWashWindow window) =>
        (window.MaxX - window.MinX + 1) * (window.MaxZ - window.MinZ + 1);

    private static int FillCellCount(NightWashWindow window) =>
        (window.FillMaxX - window.FillMinX + 1) * (window.FillMaxZ - window.FillMinZ + 1);

    // Resolves every gathered cell to one flat value, so a test that cares about two or three specific
    // cells can set those and leave the rest defined rather than at whatever an unresolved slot holds.
    private static NightWashWindow Fill(NightWashWindow window, float glow)
    {
        for (int z = window.FillMinZ; z <= window.FillMaxZ; z++)
        {
            for (int x = window.FillMinX; x <= window.FillMaxX; x++)
                window.Resolve(x, z, glow, blocksLight: false);
        }

        return window;
    }

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

        // Walls, laid down after the light so none of them carries any of it — which is the live
        // situation the fix is about, since the glow flood never enters a blocker.
        //
        // A straight run across the lamp at (42, 42), i.e. the reported case: lit on one face, dark on
        // the other, every cell of it reading zero.
        map.Wall(38, 45, 48, 45);

        // A corner, so a blocked cell has blocked orthogonal neighbours and lit diagonal ones.
        map.Wall(48, 38, 48, 45);

        // Thick enough for (52, 52) to have all eight neighbours blocked — the cell that must stay
        // fully unlit rather than borrowing light from outside the block.
        map.Wall(51, 51, 53, 53);

        // On the boundary between two sections (x == 34) and hard against the map edge, so the seam
        // and clipping cases meet a blocker rather than only open ground.
        map.Wall(34, 20, 34, 30);
        map.Wall(0, 10, 2, 10);

        return map;
    }

    // Stands in for the live Map's glow grid. GroundGlowAt mirrors what the layer asks for
    // (ignoreSky: true — local light only), and counts queries so the reduction can be measured rather
    // than assumed.
    private sealed class FakeGlowGrid
    {
        private readonly int size;
        private readonly float[] glow;
        private readonly bool[] wall;

        public FakeGlowGrid(int size)
        {
            this.size = size;
            glow = new float[size * size];
            wall = new bool[size * size];
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

        // Stands in for `edificeGrid[c]?.def.blockLight`.
        public bool BlocksLight(int x, int z) => InBounds(x, z) && wall[z * size + x];

        // Raises a wall AND clears the glow under it, which is the fact this whole fixture is about:
        // vanilla's flood never enters a light-blocking edifice, so the reading in one is ~0 no matter
        // how bright either of its faces is. A Wall that left the light in place would model a
        // situation the live grid cannot produce and would quietly make the fix look like a no-op.
        public void Wall(int minX, int minZ, int maxX, int maxZ)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (InBounds(x, z))
                    {
                        wall[z * size + x] = true;
                        glow[z * size + x] = 0f;
                    }
                }
            }
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

        // The oracle: one glow query per read, with the explicit map-bounds guard the pre-window WashAt
        // carried, and the blocked-cell rule written out straight from
        // Verse.SectionLayer_Darkness.LightAt rather than delegated to the window. Nothing in here
        // touches NightWashWindow, which is what makes the equivalence assertions above a claim about
        // the window rather than a tautology.
        public static LatticeRun Legacy(FakeGlowGrid map, int minX, int minZ, int maxX, int maxZ)
        {
            map.ResetCounters();

            float LightAt(int x, int z)
            {
                float here = map.GroundGlowAt(x, z);
                if (!map.BlocksLight(x, z))
                    return here;

                for (int i = 0; i < 8; i++)
                {
                    int nx = x + Adjacent[i, 0];
                    int nz = z + Adjacent[i, 1];
                    if (map.InBounds(nx, nz) && !map.BlocksLight(nx, nz))
                        here = Math.Max(here, map.GroundGlowAt(nx, nz));
                }

                return here;
            }

            float WashAt(int x, int z) =>
                map.InBounds(x, z) ? NightDesaturationMath.CellWash(LightAt(x, z)) : 1f;

            return Run(map, minX, minZ, maxX, maxZ, WashAt);
        }

        // The shipped shape: gather the window once, seal it, then read it.
        public static LatticeRun Windowed(FakeGlowGrid map, int minX, int minZ, int maxX, int maxZ)
        {
            map.ResetCounters();

            NightWashWindow window =
                NightWashWindow.ForSection(minX, minZ, maxX, maxZ, map.Size, map.Size);

            for (int z = window.FillMinZ; z <= window.FillMaxZ; z++)
            {
                for (int x = window.FillMinX; x <= window.FillMaxX; x++)
                    window.Resolve(x, z, map.GroundGlowAt(x, z), map.BlocksLight(x, z));
            }

            window.Seal();

            return Run(map, minX, minZ, maxX, maxZ, window.At);
        }

        // What the layer produced before the fix: the raw reading straight into CellWash, blockers
        // included. Test-only, and kept rather than deleted so the defect stays measurable — an
        // assertion that names the old numbers is worth more than a commit message that describes
        // them.
        public static LatticeRun Unfixed(FakeGlowGrid map, int minX, int minZ, int maxX, int maxZ)
        {
            map.ResetCounters();

            float WashAt(int x, int z) =>
                map.InBounds(x, z) ? NightDesaturationMath.CellWash(map.GroundGlowAt(x, z)) : 1f;

            return Run(map, minX, minZ, maxX, maxZ, WashAt);
        }

        private static readonly int[,] Adjacent =
        {
            { -1, -1 }, { 0, -1 }, { 1, -1 },
            { -1, 0 }, { 1, 0 },
            { -1, 1 }, { 0, 1 }, { 1, 1 },
        };

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
