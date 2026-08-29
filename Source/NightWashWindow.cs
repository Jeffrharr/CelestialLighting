namespace CelestialLighting;

// The baked per-cell wash buffer §9's vertex loop reads, so the glow grid is asked about each cell
// exactly once per section regenerate instead of once per read.
//
// Pure math only — no UnityEngine or Verse types anywhere in this file, same discipline as
// NightDesaturationMath and SkyOcclusionWindow — so it compiles into both Source (net481, runs inside
// RimWorld) and Tests (net8.0, runs standalone via `dotnet test`) via a linked <Compile Include>. The
// *fill* has to touch Map/GlowGrid/EdificeGrid, so it lives in the adapter
// (SectionLayer_NightDesaturation.ResolveWash) and hands its readings back in here; only the
// geometry, the storage and the reads are in this file.
//
// Why a precomputed window instead of a lookup per read (deliberately the same solution
// EaveShadowGrid uses for §15 and SkyOcclusionWindow for §7b, rather than a third one):
//   The vertex loop reads nine cells per cell it emits vertices for — itself, its four orthogonal
//   neighbours, and its four diagonal neighbours — so a full 17x17 section costs 9 * 289 == 2,601
//   GlowGrid.GroundGlowAt calls, where the 441-cell union those reads span answers all of them
//   identically. It matters because the trigger is not the frame but MapMeshFlagDefOf.Roofs |
//   GroundGlow, and GlowGrid.DirtyCell raises *both* — so every lamp toggle, every fire growing or
//   dying, every sun lamp cycling at dawn pays this, not just the roof edits it looks like it pays.
//   Live measurement (DESIGN.md §16) put the unmemoised layer at 271 µs per section regenerate, the
//   single most expensive section layer on the map, vanilla's included.
//
// TWO WINDOWS, NOT ONE, and the difference is the whole of the wall fix (issue #191).
//
//   A cell holding a light-blocking edifice reads ~0 from the glow grid however brightly lit both of
//   its faces are: vanilla's flood never enters a wall, so the reading is not "this wall is dark", it
//   is "nobody asked". Feeding that straight into CellWash gave every wall cell a full wash while its
//   neighbours took almost none, and SectionLayerGeometryMaker_Solid's nine vertices then drew the
//   difference as a diamond — full wash on the centre vertex, tapering to the corners the lit
//   neighbours pull up, with the four triangle diagonals creasing across it. On screen that is a dark
//   blob apparently radiating out of the middle of every wall tile, mildest on floors whose glow sits
//   well under their neighbours'. Vanilla hits exactly this in SectionLayer_Darkness — same geometry
//   maker, same GroundGlowAt(ignoreSky: true) signal — and answers it by giving a blocked cell the
//   MAX glow of itself and its eight non-blocking neighbours, which is what Seal below reproduces.
//   (Vanilla's own lighting overlay does the same thing differently: it drops blockLight cells from
//   the corner average outright. That shape has nothing to say about a centre vertex, which is a
//   single cell with nothing to average, so the darkness layer is the analogue to copy.)
//
//   Resolving a blocked cell therefore needs its neighbours, so the raw readings are gathered over a
//   TWO-cell skirt while the washes they reduce to are stored over the one-cell skirt the vertex loop
//   actually reads. The alternative — a one-cell skirt, with the adapter querying the grid directly
//   for the neighbours of the blocked cells it meets — was rejected on two counts: a skirt cell would
//   then resolve from whatever context its own section happened to have, so two sections sharing a
//   boundary vertex could disagree and print a 17-cell seam; and its cost is unbounded in exactly the
//   case that is not hypothetical, since every cell of a section cut out of unmined rock is a blocker
//   and would pay nine queries instead of one (3,249 against this design's fixed 441).
//
// Why a window and not the whole map:
//   A section only ever emits vertices for its own cells, and the widest read any of those makes is
//   one cell past the section boundary, so the readable union is [minX-1, maxX+1] x [minZ-1, maxZ+1];
//   the gathered union is one cell wider again, for the reason above. Same bound as EaveShadowGrid's
//   and SkyOcclusionWindow's plus the neighbourhood Seal needs.
//
// Allocated per regenerate rather than kept as a reusable static scratch buffer, matching both of
// those: ~3.6 KB of transient arrays against a call that only fires when a section is dirtied (never
// per frame), and a shared buffer would corrupt the mesh if anything ever re-entered Regenerate from
// inside Regenerate.
public readonly struct NightWashWindow
{
    // What a read outside the window answers. Not a fallback and not an error: the only cells the
    // vertex loop can ask about outside these bounds are cells off the map edge, and the pre-window
    // WashAt returned exactly this for them (`if (!cell.InBounds(map)) return 1f;`) — the map edge
    // reads as unlit rather than as an exemption, so the wash runs cleanly off the edge instead of
    // fading out along it. Folding that guard in here removes a per-read bounds check from the hot
    // loop and keeps the property that two sections averaging the same shared boundary vertex compute
    // an identical value — no 17-cell seams.
    public const float OffMapWash = 1f;

    // The wash the vertex loop reads, over the one-cell skirt.
    private readonly float[] wash;
    private readonly int minX;
    private readonly int minZ;
    private readonly int maxX;
    private readonly int maxZ;
    private readonly int width;

    // The raw readings Seal reduces, over the two-cell skirt.
    private readonly float[] glow;
    private readonly bool[] blocked;
    private readonly int fillMinX;
    private readonly int fillMinZ;
    private readonly int fillMaxX;
    private readonly int fillMaxZ;
    private readonly int fillWidth;

    private NightWashWindow(
        float[] wash, int minX, int minZ, int maxX, int maxZ, int width,
        float[] glow, bool[] blocked, int fillMinX, int fillMinZ, int fillMaxX, int fillMaxZ,
        int fillWidth)
    {
        this.wash = wash;
        this.minX = minX;
        this.minZ = minZ;
        this.maxX = maxX;
        this.maxZ = maxZ;
        this.width = width;
        this.glow = glow;
        this.blocked = blocked;
        this.fillMinX = fillMinX;
        this.fillMinZ = fillMinZ;
        this.fillMaxX = fillMaxX;
        this.fillMaxZ = fillMaxZ;
        this.fillWidth = fillWidth;
    }

    // `section*` bounds must already be clipped inside the map (Regenerate's own CellRect is). Both
    // skirts are clipped separately here, so a section on the map edge simply resolves a smaller
    // window and the cells that fell off the edge answer with OffMapWash — see above for why that is
    // the correct answer rather than an error.
    public static NightWashWindow ForSection(
        int sectionMinX, int sectionMinZ, int sectionMaxX, int sectionMaxZ, int mapSizeX, int mapSizeZ)
    {
        int minX = Max(sectionMinX - 1, 0);
        int minZ = Max(sectionMinZ - 1, 0);
        int maxX = Min(sectionMaxX + 1, mapSizeX - 1);
        int maxZ = Min(sectionMaxZ + 1, mapSizeZ - 1);

        int fillMinX = Max(sectionMinX - 2, 0);
        int fillMinZ = Max(sectionMinZ - 2, 0);
        int fillMaxX = Min(sectionMaxX + 2, mapSizeX - 1);
        int fillMaxZ = Min(sectionMaxZ + 2, mapSizeZ - 1);

        int width = maxX - minX + 1;
        int fillWidth = fillMaxX - fillMinX + 1;
        int fillCells = fillWidth * (fillMaxZ - fillMinZ + 1);

        return new NightWashWindow(
            new float[width * (maxZ - minZ + 1)], minX, minZ, maxX, maxZ, width,
            new float[fillCells], new bool[fillCells], fillMinX, fillMinZ, fillMaxX, fillMaxZ,
            fillWidth);
    }

    // The gathered bounds, so the adapter's fill loop walks exactly the cells this window stores and
    // never has to re-derive the skirt or re-check the map edge. Wider than the readable bounds below
    // by one cell — see the header.
    public int FillMinX => fillMinX;

    public int FillMinZ => fillMinZ;

    public int FillMaxX => fillMaxX;

    public int FillMaxZ => fillMaxZ;

    // The readable bounds: the section plus the one-cell skirt its vertex loop averages over.
    public int MinX => minX;

    public int MinZ => minZ;

    public int MaxX => maxX;

    public int MaxZ => maxZ;

    // Stores one cell's raw readings. Called once per cell by the fill loop over the Fill* bounds;
    // callers must stay inside them (unlike the reads, a write outside the window is always a bug in
    // the fill loop). Nothing is readable until Seal has run.
    //
    // Takes raw glow rather than a wash so the adapter stays a pure pass-through of what the glow grid
    // said, and CellWash — the actual §9 formula — is applied on this side of the boundary where it is
    // linked into the test project and covered by NightDesaturationMathTests.
    //
    // `blocksLight` is the cell's edifice verdict, vanilla's own `edificeGrid[c]?.def.blockLight`. It
    // is the flag that says the glow reading beside it is meaningless rather than dark.
    public void Resolve(int x, int z, float localGlow, bool blocksLight)
    {
        int i = (z - fillMinZ) * fillWidth + (x - fillMinX);
        glow[i] = localGlow;
        blocked[i] = blocksLight;
    }

    // Reduces the gathered readings to the washes the vertex loop reads. Must run once, after the
    // fill loop and before the first At.
    //
    // Split from Resolve rather than folded into it because a blocked cell is answered by its
    // neighbours, and at the moment the fill loop reaches it half of them have not been read yet.
    // Every cell resolved here sees the same neighbourhood it would see in any other section's
    // window, which is what keeps two sections' shared boundary vertices in exact agreement.
    public void Seal()
    {
        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
                wash[(z - minZ) * width + (x - minX)] = NightDesaturationMath.CellWash(GlowFor(x, z));
        }
    }

    // How much of the wash the cell at (x, z) takes, in [0, 1]. Cells outside the window read as
    // OffMapWash; before Seal every cell reads 0 (no wash), which is why Seal is required rather than
    // optional.
    public float At(int x, int z) =>
        Contains(x, z) ? wash[(z - minZ) * width + (x - minX)] : OffMapWash;

    // The glow a cell should be judged by. An ordinary cell is judged by its own reading; a cell
    // holding a light-blocking edifice is judged by the brightest of itself and its eight non-blocking
    // neighbours, because the flood never entered it and its own reading means "nobody asked" rather
    // than "dark". Mirrors Verse.SectionLayer_Darkness.LightAt, including taking the max over the
    // diagonals and skipping neighbours that are themselves blockers — so the middle of a thick wall,
    // whose every neighbour is also wall, still reads unlit, which is correct.
    private float GlowFor(int x, int z)
    {
        int here = (z - fillMinZ) * fillWidth + (x - fillMinX);
        if (!blocked[here])
            return glow[here];

        float brightest = glow[here];
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (Lit(x + dx, z + dz, out float neighbour) && neighbour > brightest)
                    brightest = neighbour;
            }
        }

        return brightest;
    }

    // A neighbour's reading, and whether it is one worth taking. Off-map neighbours are skipped the
    // same way vanilla's LightAt skips them — the map edge contributes nothing rather than
    // contributing darkness — and so is the cell's own index, harmlessly, since the caller seeded the
    // max with it already.
    private bool Lit(int x, int z, out float value)
    {
        value = 0f;
        if (x < fillMinX || x > fillMaxX || z < fillMinZ || z > fillMaxZ)
            return false;

        int i = (z - fillMinZ) * fillWidth + (x - fillMinX);
        if (blocked[i])
            return false;

        value = glow[i];
        return true;
    }

    private bool Contains(int x, int z) => x >= minX && x <= maxX && z >= minZ && z <= maxZ;

    private static int Max(int a, int b) => a > b ? a : b;

    private static int Min(int a, int b) => a < b ? a : b;
}
