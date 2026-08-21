namespace CelestialLighting.Tests;

// Offline unit tests for §7b's baked verdict window (Source/SkyOcclusionWindow.cs, linked into this
// project so these run against the exact shipped file).
//
// The window is a pure performance refactor: it replaced ~1,585 per-read live-map resolutions per
// section regenerate with one resolution per cell of a (Section.Size + 2)^2 window. Two things
// therefore have to be proved and not merely asserted in a commit message:
//
//   1. Equivalence. Driving the real lattice loops both ways — resolving on demand with the explicit
//      bounds guard the pre-window code carried, versus baking the window and reading it — must
//      produce byte-identical vertex alphas, including on map-edge sections where the skirt is
//      clipped. LatticeRun below is a faithful transcription of Patch_IndoorSkyOcclusion's two
//      passes (which cannot be linked here: they touch Mesh/Map/Color32).
//   2. The short-circuits survive. EaveCells.Encloses returns before the Room query for an unroofed
//      cell, and the window must not have quietly forced every cell through a full resolution — so
//      the fake map counts room queries as well as resolutions.
//
// Both apply twice over, because the window now bakes two verdicts. The sky-falloff fraction §7c/§7d
// added was wired to the live map at every read for a month before it joined the window, and these
// tests could not have caught that: every case below drove the lattice with a falloff of exactly 0,
// so the term was present in the arithmetic and absent from what was being proved. The
// SkyFalloff-suffixed cases exist to close that hole — a varying, non-zero field, its own lookup
// counter, and the same equivalence bar the blocksSky half already had.
[TestFixture]
public class SkyOcclusionWindowTests
{
    // ApiCompatibilityTests pins Verse's Section.Size at 17; hard-coded here so these tests state the
    // real geometry rather than a made-up one.
    private const int SectionSize = 17;

    private const float NoFloor = 0f;

    // A section fully inside a 60x60 map: rect 34..50, skirt 33..51.
    private const int MapSize = 60;
    private const int InnerMin = 34;
    private const int InnerMax = InnerMin + SectionSize - 1;

    // --- Window geometry: the +1 skirt on each side, clipped at the map edge ---

    [Test]
    public void ForSection_InteriorSection_ResolvesTheSectionPlusAOneCellSkirt()
    {
        // (Section.Size + 2)^2 == 19x19 == 361 cells: exactly the union the lattice can read, since a
        // corner vertex at (x, z) reads cells (x-1..x, z-1..z) and the lattice runs one past the
        // section in both directions.
        SkyOcclusionWindow window = Window(InnerMin, InnerMin, InnerMax, InnerMax);

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
        SkyOcclusionWindow window = Window(0, 0, SectionSize - 1, SectionSize - 1);

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
        // Regenerate's own rect is already clipped inside the map, so the far edge is a partial
        // section (43..59 here) whose skirt must stop at MapSize - 1.
        SkyOcclusionWindow window = Window(43, 43, MapSize - 1, MapSize - 1);

        Assert.Multiple(() =>
        {
            Assert.That(window.MinX, Is.EqualTo(42));
            Assert.That(window.MaxX, Is.EqualTo(MapSize - 1));
            Assert.That(window.MaxZ, Is.EqualTo(MapSize - 1));
        });
    }

    // --- Reads: inside the border, on it, and past it ---

    [Test]
    public void Verdicts_AreStoredPerCellAndIndependently()
    {
        SkyOcclusionWindow window = Window(InnerMin, InnerMin, InnerMax, InnerMax);
        window.Resolve(40, 41, blocksSky: true, skyFalloffFraction: 0.25f);
        window.Resolve(41, 41, blocksSky: false, skyFalloffFraction: 0.75f);

        Assert.Multiple(() =>
        {
            Assert.That(window.BlocksSky(40, 41), Is.True);
            Assert.That(window.BlocksSky(41, 41), Is.False);

            // Row stride: the cell directly above must not alias the cell to the right.
            Assert.That(window.BlocksSky(40, 42), Is.False);

            // The second verdict is indexed the same way and stays with its own cell — the two are
            // stored in parallel arrays, so a stride slip in one would not show up in the other.
            Assert.That(window.SkyFalloffFraction(40, 41), Is.EqualTo(0.25f));
            Assert.That(window.SkyFalloffFraction(41, 41), Is.EqualTo(0.75f));
            Assert.That(window.SkyFalloffFraction(40, 42), Is.EqualTo(0f));
        });
    }

    [Test]
    public void Verdicts_OnTheSkirtBorder_AreStoredAndReadBack()
    {
        // The border cells are the whole point of the +1: they are read by the section's boundary
        // lattice points even though the section never writes a vertex for them.
        SkyOcclusionWindow window = Window(InnerMin, InnerMin, InnerMax, InnerMax);
        window.Resolve(window.MinX, window.MinZ, blocksSky: true, skyFalloffFraction: 0.5f);
        window.Resolve(window.MaxX, window.MaxZ, blocksSky: true, skyFalloffFraction: 0.5f);

        Assert.Multiple(() =>
        {
            Assert.That(window.BlocksSky(window.MinX, window.MinZ), Is.True);
            Assert.That(window.BlocksSky(window.MaxX, window.MaxZ), Is.True);
            Assert.That(window.SkyFalloffFraction(window.MinX, window.MinZ), Is.EqualTo(0.5f));
            Assert.That(window.SkyFalloffFraction(window.MaxX, window.MaxZ), Is.EqualTo(0.5f));
        });
    }

    [Test]
    public void Verdicts_PastTheBorder_ReadAsNoContribution()
    {
        // Not a fallback: an off-window cell is an off-map cell, and the pre-window corner pass gave
        // it exactly this treatment via `cell.InBounds(map)` — `|= false` on the OR.
        SkyOcclusionWindow window = Window(0, 0, SectionSize - 1, SectionSize - 1);

        Assert.Multiple(() =>
        {
            Assert.That(window.BlocksSky(-1, 4), Is.False);
            Assert.That(window.BlocksSky(4, -1), Is.False);
            Assert.That(window.BlocksSky(window.MaxX + 1, 4), Is.False);
            Assert.That(window.BlocksSky(4, window.MaxZ + 1), Is.False);

            // 0 is likewise the genuine answer for the falloff term, not a fallback: it is what the
            // deleted per-read InBounds guard substituted, and it loses the corner's MAX to any real
            // neighbour, exactly as `false` loses the OR.
            Assert.That(window.SkyFalloffFraction(-1, 4), Is.EqualTo(0f));
            Assert.That(window.SkyFalloffFraction(4, -1), Is.EqualTo(0f));
            Assert.That(window.SkyFalloffFraction(window.MaxX + 1, 4), Is.EqualTo(0f));
            Assert.That(window.SkyFalloffFraction(4, window.MaxZ + 1), Is.EqualTo(0f));
        });
    }

    // --- Equivalence with the pre-refactor per-cell resolution ---

    [Test]
    public void Lattice_InteriorSection_MatchesPerCellResolutionExactly()
    {
        FakeMap map = Scene();
        LatticeRun legacy = LatticeRun.Legacy(map, InnerMin, InnerMin, InnerMax, InnerMax, NoFloor);
        LatticeRun windowed = LatticeRun.Windowed(map, InnerMin, InnerMin, InnerMax, InnerMax, NoFloor);

        AssertSameVertices(legacy, windowed);
    }

    [Test]
    public void Lattice_WithAnIndoorBrightnessFloor_MatchesPerCellResolutionExactly()
    {
        // The floor is applied to corners before they are averaged into a boundary centre, so it is
        // the setting most likely to expose an ordering slip in the refactor.
        FakeMap map = Scene();
        LatticeRun legacy = LatticeRun.Legacy(map, InnerMin, InnerMin, InnerMax, InnerMax, 0.5f);
        LatticeRun windowed = LatticeRun.Windowed(map, InnerMin, InnerMin, InnerMax, InnerMax, 0.5f);

        AssertSameVertices(legacy, windowed);
    }

    [TestCase(0, 0, TestName = "Lattice_MapOriginSection_MatchesPerCellResolutionExactly")]
    [TestCase(MapSize - SectionSize, MapSize - SectionSize,
        TestName = "Lattice_FarEdgeSection_MatchesPerCellResolutionExactly")]
    public void Lattice_EdgeSection_MatchesPerCellResolutionExactly(int botLeftX, int botLeftZ)
    {
        // The case the clipped skirt exists for: cells the lattice asks about that are off the map.
        FakeMap map = Scene();
        int maxX = botLeftX + SectionSize - 1;
        int maxZ = botLeftZ + SectionSize - 1;
        LatticeRun legacy = LatticeRun.Legacy(map, botLeftX, botLeftZ, maxX, maxZ, NoFloor);
        LatticeRun windowed = LatticeRun.Windowed(map, botLeftX, botLeftZ, maxX, maxZ, NoFloor);

        AssertSameVertices(legacy, windowed);
    }

    // --- Equivalence for the second verdict: the sky-falloff fraction ---

    [Test]
    public void Lattice_WithASkyFalloffField_MatchesPerCellLookupExactly()
    {
        // The case the cases above could not see. Every test before this one drove the lattice with a
        // falloff of 0, where baked and per-read are trivially equal; a field that varies per cell is
        // what makes the corner pass's MAX-over-four-neighbours a real claim about indexing.
        FakeMap map = SkyFalloffField(Scene());
        LatticeRun legacy = LatticeRun.Legacy(map, InnerMin, InnerMin, InnerMax, InnerMax, NoFloor);
        LatticeRun windowed = LatticeRun.Windowed(map, InnerMin, InnerMin, InnerMax, InnerMax, NoFloor);

        AssertSameVertices(legacy, windowed);
    }

    [Test]
    public void Lattice_WithASkyFalloffFieldAndAnIndoorBrightnessFloor_MatchesPerCellLookupExactly()
    {
        // Both caps at once. CapOcclusion composes them (the more generous floor wins), so this is the
        // arrangement where a falloff read off by one cell could still be masked by the flat floor on
        // some vertices and not others — i.e. the one most likely to produce a partial diff.
        FakeMap map = SkyFalloffField(Scene());
        LatticeRun legacy = LatticeRun.Legacy(map, InnerMin, InnerMin, InnerMax, InnerMax, 0.5f);
        LatticeRun windowed = LatticeRun.Windowed(map, InnerMin, InnerMin, InnerMax, InnerMax, 0.5f);

        AssertSameVertices(legacy, windowed);
    }

    [TestCase(0, 0, TestName = "Lattice_MapOriginSectionWithASkyFalloffField_MatchesPerCellLookupExactly")]
    [TestCase(MapSize - SectionSize, MapSize - SectionSize,
        TestName = "Lattice_FarEdgeSectionWithASkyFalloffField_MatchesPerCellLookupExactly")]
    public void Lattice_EdgeSectionWithASkyFalloffField_MatchesPerCellLookupExactly(int botLeftX, int botLeftZ)
    {
        // Where the deleted SkyFalloffFractionAt guard mattered: a corner on the map edge asks about a
        // cell one step past it. The window has to answer 0 there and must never index the map for it.
        FakeMap map = SkyFalloffField(Scene());
        int maxX = botLeftX + SectionSize - 1;
        int maxZ = botLeftZ + SectionSize - 1;
        LatticeRun legacy = LatticeRun.Legacy(map, botLeftX, botLeftZ, maxX, maxZ, NoFloor);
        LatticeRun windowed = LatticeRun.Windowed(map, botLeftX, botLeftZ, maxX, maxZ, NoFloor);

        Assert.Multiple(() =>
        {
            AssertSameVertices(legacy, windowed);
            Assert.That(map.OutOfBoundsFalloffLookups, Is.EqualTo(0));
        });
    }

    // --- The reduction itself, and the short-circuits it must not have trampled ---

    [Test]
    public void Windowed_LooksUpSkyFalloffOncePerCellInsteadOfOncePerRead()
    {
        // The regression this whole change is about. SkyFalloffSource.FractionAt is the more expensive
        // of the two verdicts — two glow-grid reads per call, one of them the GroundGlowAt every
        // interop mod postfixes — and it was being paid 1,585 times per section regenerate.
        FakeMap map = SkyFalloffField(Scene());
        LatticeRun legacy = LatticeRun.Legacy(map, InnerMin, InnerMin, InnerMax, InnerMax, NoFloor);
        LatticeRun windowed = LatticeRun.Windowed(map, InnerMin, InnerMin, InnerMax, InnerMax, NoFloor);

        Assert.Multiple(() =>
        {
            Assert.That(legacy.FalloffLookups, Is.EqualTo(1585));
            Assert.That(windowed.FalloffLookups, Is.EqualTo(361));
        });
    }

    [Test]
    public void Windowed_ResolvesEachCellOnceInsteadOfOncePerRead()
    {
        // 18*18 corner vertices x 4 cells each + 17*17 centres == 1,585 resolutions on demand,
        // against 19*19 == 361 baked. This is the whole issue.
        FakeMap map = Scene();
        LatticeRun legacy = LatticeRun.Legacy(map, InnerMin, InnerMin, InnerMax, InnerMax, NoFloor);
        LatticeRun windowed = LatticeRun.Windowed(map, InnerMin, InnerMin, InnerMax, InnerMax, NoFloor);

        Assert.Multiple(() =>
        {
            Assert.That(legacy.Resolutions, Is.EqualTo(1585));
            Assert.That(windowed.Resolutions, Is.EqualTo(361));
        });
    }

    [Test]
    public void Windowed_UnroofedCells_StillShortCircuitBeforeTheRoomQuery()
    {
        // EaveCells.Encloses returns on `roof == null` without touching RoomLookup, and the window
        // resolves cells through that same call — so an open-terrain section must cost zero room
        // queries, exactly as it did before.
        FakeMap open = new FakeMap(MapSize);
        LatticeRun windowed = LatticeRun.Windowed(open, InnerMin, InnerMin, InnerMax, InnerMax, NoFloor);

        Assert.Multiple(() =>
        {
            Assert.That(windowed.Resolutions, Is.EqualTo(361));
            Assert.That(windowed.RoomQueries, Is.EqualTo(0));
        });
    }

    [Test]
    public void Windowed_RoofedCells_PayTheRoomQueryOnceEachInsteadOfOncePerRead()
    {
        // The other half of the short-circuit: where it does *not* fire (a fully roofed base, which
        // is also where the lamps are), the window is what collapses the cost.
        FakeMap roofed = new FakeMap(MapSize);
        roofed.Fill(0, 0, MapSize - 1, MapSize - 1, roofed: true);

        LatticeRun legacy = LatticeRun.Legacy(roofed, InnerMin, InnerMin, InnerMax, InnerMax, NoFloor);
        LatticeRun windowed = LatticeRun.Windowed(roofed, InnerMin, InnerMin, InnerMax, InnerMax, NoFloor);

        Assert.Multiple(() =>
        {
            Assert.That(legacy.RoomQueries, Is.EqualTo(1585));
            Assert.That(windowed.RoomQueries, Is.EqualTo(361));
            AssertSameVertices(legacy, windowed);
        });
    }

    [Test]
    public void Windowed_NeverResolvesACellOutsideTheMap()
    {
        // The clipped window is what replaces the per-read InBounds guard; if it were unclipped the
        // fill loop would index off the map instead.
        FakeMap map = Scene();
        LatticeRun windowed = LatticeRun.Windowed(map, 0, 0, SectionSize - 1, SectionSize - 1, NoFloor);

        Assert.Multiple(() =>
        {
            // 18x18: the skirt exists on two sides only at the map origin.
            Assert.That(windowed.Resolutions, Is.EqualTo((SectionSize + 1) * (SectionSize + 1)));
            Assert.That(map.OutOfBoundsResolutions, Is.EqualTo(0));
        });
    }

    // --- Helpers ---

    private static SkyOcclusionWindow Window(int minX, int minZ, int maxX, int maxZ) =>
        SkyOcclusionWindow.ForSection(minX, minZ, maxX, maxZ, MapSize, MapSize);

    private static int CellCount(SkyOcclusionWindow window) =>
        (window.MaxX - window.MinX + 1) * (window.MaxZ - window.MinZ + 1);

    private static void AssertSameVertices(LatticeRun legacy, LatticeRun windowed)
    {
        Assert.That(windowed.Corners, Is.EqualTo(legacy.Corners),
            "corner vertex alphas diverged from per-cell resolution");
        Assert.That(windowed.Centres, Is.EqualTo(legacy.Centres),
            "centre vertex alphas diverged from per-cell resolution");
    }

    // A scene with every classification §7b distinguishes: open ground, a sealed room, its walls, a
    // door in one wall, a thick-roofed (mountain) pocket, and a roofed porch whose room breathes
    // outdoor air (an eave). Laid out to straddle both the interior section under test and the map
    // origin section, so the edge cases see real structure rather than empty terrain.
    private static FakeMap Scene()
    {
        FakeMap map = new FakeMap(MapSize);

        // A sealed room, walls included: roofed everywhere, roof-holding on the perimeter.
        map.Fill(30, 30, 44, 44, roofed: true);
        map.Outline(30, 30, 44, 44, holdsRoof: true);
        map.SetDoor(37, 30);

        // A mined-out mountain pocket overlapping the room's corner: thick roof over open floor, and
        // over the room's own wall where the two meet. That wall is not buried — it is a built wall
        // standing under a mountain roof, not the mountain (#129).
        map.Fill(40, 40, 48, 48, roofed: true, thickRoof: true);

        // The unmined far end of that same pocket, which is buried: stone all the way up, no wall face.
        map.FillRock(46, 46, 48, 48);

        // A porch: roofed, not a mountain, and its room uses outdoor temperature — an eave, which is
        // never interior even though it is roofed.
        map.Fill(26, 26, 29, 33, roofed: true, usesOutdoorTemperature: true);

        // A second structure straddling the map origin section, so the clipped-skirt tests exercise
        // walls and doors right up against the map edge.
        map.Fill(0, 0, 8, 8, roofed: true);
        map.Outline(0, 0, 8, 8, holdsRoof: true);
        map.SetDoor(0, 4);

        return map;
    }

    // A sky-falloff fraction for every cell on the map, deliberately not smooth. Real fields are
    // gradients (a BFS out from a door, another mod's under-roof glow), and a gradient is the worst
    // possible test input for an indexing change: neighbouring cells hold nearly equal values, so
    // reading the wrong one still gives nearly the right answer and the equivalence assertion passes.
    // The mixed multipliers below give every cell in a 4-neighbourhood a distinct value, and are
    // asymmetric in x and z so a transposed index cannot pass either.
    private static FakeMap SkyFalloffField(FakeMap map)
    {
        for (int z = 0; z < map.Size; z++)
        {
            for (int x = 0; x < map.Size; x++)
                map.SetFalloff(x, z, ((x * 7 + z * 13) % 11) / 10f);
        }

        return map;
    }

    // Stands in for the live Map the adapter reads. Resolve() mirrors
    // Patch_IndoorSkyOcclusion.ResolveCell exactly, including the order in which EaveCells.Encloses
    // short-circuits, and drives the real pure cores (EavesMath, IndoorOcclusionMath) so the
    // equivalence being tested is over the shipped classification and not a paraphrase of it.
    private sealed class FakeMap
    {
        private readonly int size;
        private readonly bool[] roofed;
        private readonly bool[] thickRoof;
        private readonly bool[] holdsRoof;
        private readonly bool[] naturalRock;
        private readonly bool[] doors;
        private readonly bool[] outdoorTemperature;
        private readonly float[] skyFalloff;

        public FakeMap(int size)
        {
            this.size = size;
            roofed = new bool[size * size];
            thickRoof = new bool[size * size];
            holdsRoof = new bool[size * size];
            naturalRock = new bool[size * size];
            doors = new bool[size * size];
            outdoorTemperature = new bool[size * size];
            skyFalloff = new float[size * size];
        }

        public int Size => size;

        public int Resolutions { get; private set; }

        public int RoomQueries { get; private set; }

        public int OutOfBoundsResolutions { get; private set; }

        // Counted separately from Resolutions because the two used to fan out differently: the
        // blocksSky verdict was baked once per cell while the falloff fraction was still being fetched
        // once per read. A single counter would have averaged that gap away.
        public int FalloffLookups { get; private set; }

        // The deleted Patch_IndoorSkyOcclusion.SkyFalloffFractionAt existed only to keep this at zero:
        // IndoorGlowPassthrough indexes the glow grid directly and would have read off the end of it.
        // The window's clipped bounds are what replace that guard, so the count still has to be zero.
        public int OutOfBoundsFalloffLookups { get; private set; }

        public void ResetCounters()
        {
            Resolutions = 0;
            RoomQueries = 0;
            OutOfBoundsResolutions = 0;
            FalloffLookups = 0;
            OutOfBoundsFalloffLookups = 0;
        }

        public bool InBounds(int x, int z) => x >= 0 && x < size && z >= 0 && z < size;

        public void Fill(
            int minX, int minZ, int maxX, int maxZ,
            bool roofed = false, bool thickRoof = false, bool usesOutdoorTemperature = false)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    int i = Index(x, z);
                    this.roofed[i] |= roofed;
                    this.thickRoof[i] |= thickRoof;
                    outdoorTemperature[i] |= usesOutdoorTemperature;
                }
            }
        }

        public void Outline(int minX, int minZ, int maxX, int maxZ, bool holdsRoof)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    bool onPerimeter = x == minX || x == maxX || z == minZ || z == maxZ;
                    if (onPerimeter)
                        this.holdsRoof[Index(x, z)] = holdsRoof;
                }
            }
        }

        // Unmined stone, which is both the edifice holding its own thick roof up and the thing that
        // makes it the mountain rather than a wall standing under one (#129).
        public void FillRock(int minX, int minZ, int maxX, int maxZ)
        {
            Fill(minX, minZ, maxX, maxZ, roofed: true, thickRoof: true);
            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    int i = Index(x, z);
                    holdsRoof[i] = true;
                    naturalRock[i] = true;
                }
            }
        }

        public void SetDoor(int x, int z) => doors[Index(x, z)] = true;

        // Stands in for SkyFalloffSource.FractionAt, which cannot be linked here (it reaches Map and
        // GlowGrid). The values themselves are arbitrary; what matters for equivalence is that they
        // vary sharply from cell to cell, so a corner's MAX over its four neighbours is a different
        // number for every corner and any misindexing shows up as a diverged vertex rather than a
        // coincidence. See SkyFalloffField.
        public void SetFalloff(int x, int z, float fraction) => skyFalloff[Index(x, z)] = fraction;

        public float FalloffAt(int x, int z)
        {
            FalloffLookups++;
            if (!InBounds(x, z))
                OutOfBoundsFalloffLookups++;

            return skyFalloff[Index(x, z)];
        }

        public void Resolve(int x, int z, out bool blocksSky)
        {
            Resolutions++;
            if (!InBounds(x, z))
                OutOfBoundsResolutions++;

            int i = Index(x, z);
            blocksSky = IndoorOcclusionMath.BlocksSky(
                Encloses(i), roofed[i] && thickRoof[i], holdsRoof[i], doors[i], naturalRock[i]);
        }

        // EaveCells.Encloses, transcribed: the roof test comes first and is what keeps the room query
        // off the overwhelming majority of cells.
        private bool Encloses(int i)
        {
            if (!roofed[i])
                return false;

            RoomQueries++;
            return !EavesMath.IsEave(
                roofed: true,
                thickRoof: thickRoof[i],
                hasRoom: true,
                usesOutdoorTemperature: outdoorTemperature[i]);
        }

        private int Index(int x, int z) => z * size + x;
    }

    // Patch_IndoorSkyOcclusion's two mesh passes, run either way. Only the source of a cell's verdict
    // differs between Legacy and Windowed; the arithmetic below is one copy shared by both.
    private sealed class LatticeRun
    {
        private LatticeRun(
            float[] corners, byte[] centres, int resolutions, int roomQueries, int falloffLookups)
        {
            Corners = corners;
            Centres = centres;
            Resolutions = resolutions;
            RoomQueries = roomQueries;
            FalloffLookups = falloffLookups;
        }

        public float[] Corners { get; }

        public byte[] Centres { get; }

        public int Resolutions { get; }

        public int RoomQueries { get; }

        public int FalloffLookups { get; }

        // The pre-refactor shape: resolve on demand at every read, with the explicit map-bounds guard
        // the corner pass used to carry — on both verdicts, since each had one of its own.
        public static LatticeRun Legacy(FakeMap map, int minX, int minZ, int maxX, int maxZ, float floor)
        {
            map.ResetCounters();

            // The old corner pass took its verdict off one live-map visit per neighbour, which is what
            // the resolution counter counts.
            float Corner(int x, int z)
            {
                bool anyBlocksSky = false;
                float skyFalloffFraction = 0f;
                for (int i = 0; i < 4; i++)
                {
                    int cellX = x - (i & 1);
                    int cellZ = z - (i >> 1);
                    if (map.InBounds(cellX, cellZ))
                    {
                        map.Resolve(cellX, cellZ, out bool blocksSky);
                        anyBlocksSky |= blocksSky;

                        // MAX, against the OR above: the most sky any of the four neighbours receives
                        // is the floor this corner honours. The pre-window code took it inside the same
                        // guard, so an off-map neighbour contributed nothing to either.
                        skyFalloffFraction = Math.Max(skyFalloffFraction, map.FalloffAt(cellX, cellZ));
                    }
                }

                return IndoorOcclusionMath.CapOcclusion(
                    IndoorOcclusionMath.CornerOcclusion(anyBlocksSky), floor, skyFalloffFraction);
            }

            // The centre pass carried no guard of its own and needed none — a centre vertex is always
            // a cell of the section, hence always on the map.
            return Run(map, minX, minZ, maxX, maxZ, floor, Corner, CentreBlocksSky, map.FalloffAt);

            bool CentreBlocksSky(int x, int z)
            {
                map.Resolve(x, z, out bool blocksSky);
                return blocksSky;
            }
        }

        // The shipped shape: bake the window once, then read it.
        public static LatticeRun Windowed(FakeMap map, int minX, int minZ, int maxX, int maxZ, float floor)
        {
            map.ResetCounters();

            SkyOcclusionWindow window =
                SkyOcclusionWindow.ForSection(minX, minZ, maxX, maxZ, map.Size, map.Size);

            for (int z = window.MinZ; z <= window.MaxZ; z++)
            {
                for (int x = window.MinX; x <= window.MaxX; x++)
                {
                    map.Resolve(x, z, out bool blocksSky);
                    window.Resolve(x, z, blocksSky, map.FalloffAt(x, z));
                }
            }

            float Corner(int x, int z)
            {
                bool anyBlocksSky = false;
                float skyFalloffFraction = 0f;
                for (int i = 0; i < 4; i++)
                {
                    int cellX = x - (i & 1);
                    int cellZ = z - (i >> 1);
                    anyBlocksSky |= window.BlocksSky(cellX, cellZ);
                    skyFalloffFraction =
                        Math.Max(skyFalloffFraction, window.SkyFalloffFraction(cellX, cellZ));
                }

                return IndoorOcclusionMath.CapOcclusion(
                    IndoorOcclusionMath.CornerOcclusion(anyBlocksSky), floor, skyFalloffFraction);
            }

            return Run(
                map, minX, minZ, maxX, maxZ, floor, Corner, window.BlocksSky, window.SkyFalloffFraction);
        }

        private static LatticeRun Run(
            FakeMap map, int minX, int minZ, int maxX, int maxZ, float floor,
            Func<int, int, float> corner, Func<int, int, bool> centreBlocksSky,
            Func<int, int, float> centreFalloff)
        {
            int width = maxX - minX + 1;
            int height = maxZ - minZ + 1;
            int stride = width + 1;

            float[] corners = new float[stride * (height + 1)];
            for (int z = minZ; z <= maxZ + 1; z++)
            {
                for (int x = minX; x <= maxX + 1; x++)
                    corners[(z - minZ) * stride + (x - minX)] = corner(x, z);
            }

            byte[] centres = new byte[width * height];
            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    int c = (z - minZ) * stride + (x - minX);
                    float cornerSum = corners[c] + corners[c + 1] + corners[c + stride] + corners[c + stride + 1];
                    float occlusion = IndoorOcclusionMath.CentreOcclusion(centreBlocksSky(x, z), cornerSum);
                    centres[(z - minZ) * width + (x - minX)] =
                        IndoorOcclusionMath.CoverAlpha(
                            IndoorOcclusionMath.CapOcclusion(occlusion, floor, centreFalloff(x, z)), 0);
                }
            }

            return new LatticeRun(
                corners, centres, map.Resolutions, map.RoomQueries, map.FalloffLookups);
        }
    }
}
