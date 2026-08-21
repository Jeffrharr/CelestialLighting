namespace CelestialLighting;

// The baked per-cell verdict buffer §7b's two mesh passes read, so the live map is asked about each
// cell exactly once per section regenerate instead of once per read.
//
// TWO verdicts per cell, not one, and the second arrived late. The window originally carried only
// `blocksSky`; §7c/§7d then added a sky-falloff term to both passes (SkyFalloffSource.FractionAt) and
// wired it straight to the live map at every read, reintroducing the exact 5-reads-per-cell fan-out
// this type exists to remove — and doing it on a costlier lookup than the one it was written for.
// Both verdicts now fall out of the same fill loop. Anything a pass reads per cell belongs here; that
// is the rule this file is for.
//
// Pure math only — no UnityEngine or Verse types anywhere in this file, same discipline as
// IndoorOcclusionMath — so it compiles into both Source (net481, runs inside RimWorld) and Tests
// (net8.0, runs standalone via `dotnet test`) via a linked <Compile Include>. The *fill* has to touch
// Map/RoofGrid/EdificeGrid, so it lives in the adapter (Patch_IndoorSkyOcclusion.ResolveCell) and
// hands its answers back in here; only the geometry, the storage and the reads are in this file.
//
// Why a precomputed window instead of a lookup per read (the same argument EaveShadowGrid makes for
// §15, deliberately solved the same way rather than a second way):
//   The lighting mesh's lattice reads each cell up to five times — four times as one of the corner
//   vertices it meets at, and once as its own centre vertex. At Section.Size == 17 that is
//   18*18*4 + 17*17 == 1,585 verdicts per regenerate, and each one can reach
//   EaveCells.Encloses -> RoomLookup.RoomAtNoRebuild -> Region -> District -> Room, i.e. a pointer
//   walk per call. Baking the (Section.Size + 2)^2 == 361-cell window once means exactly one verdict
//   per cell: ~4.4x fewer lookups for ~722 bytes of transient array. That matters because the trigger
//   is not the frame but MapMeshFlagDefOf.GroundGlow, which *any* glower change dirties — a lamp
//   toggling, a fire growing — so the cost lands in bursts on ordinary gameplay events.
//
//   The falloff verdict is the more expensive of the two by some way, which is why the same 4.4x buys
//   more here than it did for blocksSky: SkyFalloffSource.FractionAt reads the glow grid twice per
//   call, and one of those two is GroundGlowAt — the patched surface, so every interop mod's postfix
//   in the load order ran 1,585 times per regenerate as well.
//
// Why a window and not the whole map:
//   A section only ever writes its own cells plus the one-cell skirt its boundary lattice points
//   reach into, so that is all we resolve. Same bound as EaveShadowGrid's, arrived at from the same
//   place: corner vertex (x, z) reads the four cells (x-1..x, z-1..z), and the lattice runs one past
//   the section in each direction, so the union is [minX-1, maxX+1] x [minZ-1, maxZ+1].
//
// Allocated per regenerate rather than kept as a reusable static scratch buffer, matching
// EaveShadowGrid: 361 bools plus 361 floats against a call that only fires when a section is dirtied
// (never per frame), and a shared buffer would corrupt the mesh if anything ever re-entered
// Regenerate from inside Regenerate.
public readonly struct SkyOcclusionWindow
{
    // One verdict per cell. Used to also carry a second, door bit alongside this one — both fell out of
    // the same roof-grid + edifice-grid fetch, so they were packed together — but the door bit's only
    // reader was the corner pass's flat doorSkyLeak cap, which §7c's distance-graded sky falloff now
    // supersedes (see IndoorOcclusionMath.CornerOcclusion's header). With no remaining reader, a plain
    // bool per cell replaced the bit-packed byte.
    private readonly bool[] blocksSky;

    // How much sky is reaching each cell, per SkyFalloffSource — the third argument to
    // IndoorOcclusionMath.CapOcclusion. A parallel array rather than an array of a two-field struct:
    // the two passes read the two verdicts at different rates (the corner pass takes an OR of one and
    // a MAX of the other over the same four cells, the centre pass reads only blocksSky), and keeping
    // them separate leaves each read a single indexed load of the size it actually needs.
    private readonly float[] skyFalloff;
    private readonly int minX;
    private readonly int minZ;
    private readonly int maxX;
    private readonly int maxZ;
    private readonly int width;

    private SkyOcclusionWindow(
        bool[] blocksSky, float[] skyFalloff, int minX, int minZ, int maxX, int maxZ, int width)
    {
        this.blocksSky = blocksSky;
        this.skyFalloff = skyFalloff;
        this.minX = minX;
        this.minZ = minZ;
        this.maxX = maxX;
        this.maxZ = maxZ;
        this.width = width;
    }

    // `section*` bounds must already be clipped inside the map (Regenerate's own CellRect is). The
    // one-cell skirt is clipped separately here, so a section on the map edge simply resolves a
    // smaller window and the cells that fell off the edge answer as "no contribution" — see the read
    // accessors for why that is the correct answer rather than an error.
    public static SkyOcclusionWindow ForSection(
        int sectionMinX, int sectionMinZ, int sectionMaxX, int sectionMaxZ, int mapSizeX, int mapSizeZ)
    {
        int minX = Max(sectionMinX - 1, 0);
        int minZ = Max(sectionMinZ - 1, 0);
        int maxX = Min(sectionMaxX + 1, mapSizeX - 1);
        int maxZ = Min(sectionMaxZ + 1, mapSizeZ - 1);

        int width = maxX - minX + 1;
        int cells = width * (maxZ - minZ + 1);
        return new SkyOcclusionWindow(
            new bool[cells], new float[cells], minX, minZ, maxX, maxZ, width);
    }

    // The resolved bounds, so the adapter's fill loop walks exactly the cells this window stores and
    // never has to re-derive the skirt or re-check the map edge.
    public int MinX => minX;

    public int MinZ => minZ;

    public int MaxX => maxX;

    public int MaxZ => maxZ;

    // Bakes one cell's verdicts. Called once per cell by the fill loop; callers must stay inside the
    // bounds above (unlike the reads, a write outside the window is always a bug in the fill loop).
    //
    // Both verdicts are taken in one call rather than two, so a fill loop physically cannot resolve a
    // cell for one of them and forget the other — the failure mode would be a silently stale zero in
    // half the window, which reads on screen as a correctly-shaped room lit slightly wrong.
    public void Resolve(int x, int z, bool blocksSky, float skyFalloffFraction)
    {
        int index = (z - minZ) * width + (x - minX);
        this.blocksSky[index] = blocksSky;
        skyFalloff[index] = skyFalloffFraction;
    }

    // Is this cell interior — does it block the sky outright? See IndoorOcclusionMath.BlocksSky for
    // what that means and why it is narrower than "roofed".
    //
    // Reads outside the window answer "no contribution" (not interior) instead of throwing, because
    // that is the genuine answer and not a fallback: the only cells the two passes can ask about
    // outside these bounds are cells off the map edge, and the pre-window code skipped them with an
    // explicit `cell.InBounds(map)` guard whose effect on the corner pass's OR was exactly `|= false`.
    // Folding it in here removes a per-read bounds check from the hot loop and keeps the property that
    // two sections baking the same shared boundary vertex compute an identical value — no 17-cell seams.
    public bool BlocksSky(int x, int z) =>
        Contains(x, z) && blocksSky[(z - minZ) * width + (x - minX)];

    // How much sky reaches this cell, in [0, 1] — 0 meaning "no floor", which is what
    // IndoorOcclusionMath.CapOcclusion already treats as "no cap".
    //
    // An off-window read answers 0 for the same reason BlocksSky answers false, and it is likewise the
    // genuine answer rather than a fallback: the pre-window corner pass guarded its own lookup with
    // `cell.InBounds(map)` and substituted exactly this 0 for an off-map neighbour, which then lost the
    // MAX to any real cell the corner touched. The guard was needed because
    // IndoorGlowPassthrough.SkyFractionAt indexes the glow grid directly and assumes an in-bounds
    // cell; the window satisfies that contract by construction, since ForSection clips to the map and
    // the fill loop only ever walks what it clipped.
    public float SkyFalloffFraction(int x, int z) =>
        Contains(x, z) ? skyFalloff[(z - minZ) * width + (x - minX)] : 0f;

    private bool Contains(int x, int z) => x >= minX && x <= maxX && z >= minZ && z <= maxZ;

    private static int Max(int a, int b) => a > b ? a : b;

    private static int Min(int a, int b) => a < b ? a : b;
}
