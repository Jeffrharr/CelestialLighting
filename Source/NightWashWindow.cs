namespace CelestialLighting;

// The baked per-cell wash buffer §9's vertex loop reads, so the glow grid is asked about each cell
// exactly once per section regenerate instead of once per read.
//
// Pure math only — no UnityEngine or Verse types anywhere in this file, same discipline as
// NightDesaturationMath and SkyOcclusionWindow — so it compiles into both Source (net481, runs inside
// RimWorld) and Tests (net8.0, runs standalone via `dotnet test`) via a linked <Compile Include>. The
// *fill* has to touch Map/GlowGrid, so it lives in the adapter
// (SectionLayer_NightDesaturation.ResolveWash) and hands its glow readings back in here; only the
// geometry, the storage and the reads are in this file.
//
// Why a precomputed window instead of a lookup per read (deliberately the same solution
// EaveShadowGrid uses for §15 and SkyOcclusionWindow for §7b, rather than a third one):
//   The vertex loop reads nine cells per cell it emits vertices for — itself, its four orthogonal
//   neighbours, and its four diagonal neighbours — so a full 17x17 section costs 9 * 289 == 2,601
//   GlowGrid.GroundGlowAt calls, where the (Section.Size + 2)^2 == 361-cell union those reads span
//   answers all of them identically. That is a ~7.2x reduction in glow queries for ~1.4 KB of
//   transient array. It matters because the trigger is not the frame but MapMeshFlagDefOf.Roofs |
//   GroundGlow, and GlowGrid.DirtyCell raises *both* — so every lamp toggle, every fire growing or
//   dying, every sun lamp cycling at dawn pays this, not just the roof edits it looks like it pays.
//   Live measurement (DESIGN.md §16) put the unmemoised layer at 271 µs per section regenerate, the
//   single most expensive section layer on the map, vanilla's included.
//
// Why a window and not the whole map:
//   A section only ever emits vertices for its own cells, and the widest read any of those makes is
//   one cell past the section boundary (the diagonal neighbours of the boundary cells), so the union
//   is [minX-1, maxX+1] x [minZ-1, maxZ+1] and that is all we resolve. Same bound as EaveShadowGrid's
//   and SkyOcclusionWindow's, arrived at from the same place.
//
// Allocated per regenerate rather than kept as a reusable static scratch buffer, matching both of
// those: 361 floats is ~1.4 KB against a call that only fires when a section is dirtied (never per
// frame), and a shared buffer would corrupt the mesh if anything ever re-entered Regenerate from
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

    private readonly float[] wash;
    private readonly int minX;
    private readonly int minZ;
    private readonly int maxX;
    private readonly int maxZ;
    private readonly int width;

    private NightWashWindow(float[] wash, int minX, int minZ, int maxX, int maxZ, int width)
    {
        this.wash = wash;
        this.minX = minX;
        this.minZ = minZ;
        this.maxX = maxX;
        this.maxZ = maxZ;
        this.width = width;
    }

    // `section*` bounds must already be clipped inside the map (Regenerate's own CellRect is). The
    // one-cell skirt is clipped separately here, so a section on the map edge simply resolves a
    // smaller window and the cells that fell off the edge answer with OffMapWash — see above for why
    // that is the correct answer rather than an error.
    public static NightWashWindow ForSection(
        int sectionMinX, int sectionMinZ, int sectionMaxX, int sectionMaxZ, int mapSizeX, int mapSizeZ)
    {
        int minX = Max(sectionMinX - 1, 0);
        int minZ = Max(sectionMinZ - 1, 0);
        int maxX = Min(sectionMaxX + 1, mapSizeX - 1);
        int maxZ = Min(sectionMaxZ + 1, mapSizeZ - 1);

        int width = maxX - minX + 1;
        return new NightWashWindow(new float[width * (maxZ - minZ + 1)], minX, minZ, maxX, maxZ, width);
    }

    // The resolved bounds, so the adapter's fill loop walks exactly the cells this window stores and
    // never has to re-derive the skirt or re-check the map edge.
    public int MinX => minX;

    public int MinZ => minZ;

    public int MaxX => maxX;

    public int MaxZ => maxZ;

    // Bakes one cell's wash from its local glow. Called once per cell by the fill loop; callers must
    // stay inside the bounds above (unlike the reads, a write outside the window is always a bug in
    // the fill loop).
    //
    // Takes raw glow rather than a wash so the adapter stays a pure pass-through of what the glow grid
    // said, and CellWash — the actual §9 formula — is applied on this side of the boundary where it is
    // linked into the test project and covered by NightDesaturationMathTests.
    public void Resolve(int x, int z, float localGlow) =>
        wash[(z - minZ) * width + (x - minX)] = NightDesaturationMath.CellWash(localGlow);

    // How much of the wash the cell at (x, z) takes, in [0, 1]. Cells outside the window read as
    // OffMapWash; cells inside it that the fill loop never resolved read as 0 (no wash), which is why
    // the fill loop is required to walk the full MinX..MaxX / MinZ..MaxZ rectangle.
    public float At(int x, int z) =>
        Contains(x, z) ? wash[(z - minZ) * width + (x - minX)] : OffMapWash;

    private bool Contains(int x, int z) => x >= minX && x <= maxX && z >= minZ && z <= maxZ;

    private static int Max(int a, int b) => a > b ? a : b;

    private static int Min(int a, int b) => a < b ? a : b;
}
