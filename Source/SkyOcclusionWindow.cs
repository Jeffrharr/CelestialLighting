namespace CelestialLighting;

// The baked per-cell verdict buffer §7b's two mesh passes read, so the live map is asked about each
// cell exactly once per section regenerate instead of once per read.
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
//   per cell: ~4.4x fewer lookups for ~361 bytes of transient array. That matters because the trigger
//   is not the frame but MapMeshFlagDefOf.GroundGlow, which *any* glower change dirties — a lamp
//   toggling, a fire growing — so the cost lands in bursts on ordinary gameplay events.
//
// Why a window and not the whole map:
//   A section only ever writes its own cells plus the one-cell skirt its boundary lattice points
//   reach into, so that is all we resolve. Same bound as EaveShadowGrid's, arrived at from the same
//   place: corner vertex (x, z) reads the four cells (x-1..x, z-1..z), and the lattice runs one past
//   the section in each direction, so the union is [minX-1, maxX+1] x [minZ-1, maxZ+1].
//
// Allocated per regenerate rather than kept as a reusable static scratch buffer, matching
// EaveShadowGrid: ~722 bytes (two byte arrays now — see doorLeak below) against a call that only
// fires when a section is dirtied (never per frame), and a shared buffer would corrupt the mesh if
// anything ever re-entered Regenerate from inside Regenerate.
public readonly struct SkyOcclusionWindow
{
    // Two bits per cell rather than two arrays: both verdicts are read together by the corner pass
    // and both fall out of the same roof-grid + edifice-grid fetch, so they are stored together and
    // indexed once.
    private const byte BlocksSkyBit = 1;
    private const byte DoorBit = 2;

    private readonly byte[] verdicts;

    // Per-door sky leak (IndoorOcclusionMath.DoorSkyLeakFor), quantized to a byte the same way
    // CoverAlpha quantizes occlusion. A second array rather than packed into `verdicts` because it
    // needs the full 0-255 range — a door's leak can legitimately resolve to exactly 0 (a sealed
    // blast door), which a spare verdict bit could not distinguish from "not a door" without a third
    // state. Only meaningful where DoorBit is set; the fill loop leaves it 0 everywhere else, which
    // happens to be harmless since callers gate every read on IsDoor first.
    private readonly byte[] doorLeak;

    private readonly int minX;
    private readonly int minZ;
    private readonly int maxX;
    private readonly int maxZ;
    private readonly int width;

    private SkyOcclusionWindow(byte[] verdicts, byte[] doorLeak, int minX, int minZ, int maxX, int maxZ, int width)
    {
        this.verdicts = verdicts;
        this.doorLeak = doorLeak;
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
        int count = width * (maxZ - minZ + 1);
        return new SkyOcclusionWindow(new byte[count], new byte[count], minX, minZ, maxX, maxZ, width);
    }

    // The resolved bounds, so the adapter's fill loop walks exactly the cells this window stores and
    // never has to re-derive the skirt or re-check the map edge.
    public int MinX => minX;

    public int MinZ => minZ;

    public int MaxX => maxX;

    public int MaxZ => maxZ;

    // Bakes one cell's verdict. Called once per cell by the fill loop; callers must stay inside the
    // bounds above (unlike the reads, a write outside the window is always a bug in the fill loop).
    // doorLeakFraction is ignored (and may be anything) when isDoor is false.
    public void Resolve(int x, int z, bool blocksSky, bool isDoor, float doorLeakFraction)
    {
        byte verdict = 0;
        if (blocksSky)
            verdict |= BlocksSkyBit;

        if (isDoor)
            verdict |= DoorBit;

        int index = (z - minZ) * width + (x - minX);
        verdicts[index] = verdict;
        doorLeak[index] = isDoor ? Quantize(doorLeakFraction) : (byte)0;
    }

    // Is this cell interior — does it block the sky outright? See IndoorOcclusionMath.BlocksSky for
    // what that means and why it is narrower than "roofed".
    public bool BlocksSky(int x, int z) => (Verdict(x, z) & BlocksSkyBit) != 0;

    // Is this cell a doorway? Read by the corner pass, which caps a corner a door touches rather than
    // raising it (IndoorOcclusionMath.CornerOcclusion).
    public bool IsDoor(int x, int z) => (Verdict(x, z) & DoorBit) != 0;

    // This cell's door leak (IndoorOcclusionMath.DoorSkyLeakFor), or 0 for a non-door / off-window
    // cell — the correct default, since a caller only ever combines this with IsDoor already true.
    public float DoorLeak(int x, int z) =>
        Contains(x, z) ? doorLeak[(z - minZ) * width + (x - minX)] / 255f : 0f;

    // Reads outside the window answer "no contribution" (neither interior nor a door) instead of
    // throwing, because that is the genuine answer and not a fallback: the only cells the two passes
    // can ask about outside these bounds are cells off the map edge, and the pre-window code skipped
    // them with an explicit `cell.InBounds(map)` guard whose effect on the corner pass's two ORs was
    // exactly `|= false`. Folding it in here removes a per-read bounds check from the hot loop and
    // keeps the property that two sections baking the same shared boundary vertex compute an
    // identical value — no 17-cell seams.
    private byte Verdict(int x, int z) =>
        Contains(x, z) ? verdicts[(z - minZ) * width + (x - minX)] : (byte)0;

    private bool Contains(int x, int z) => x >= minX && x <= maxX && z >= minZ && z <= maxZ;

    // Same rounding IndoorOcclusionMath.CoverAlpha uses to quantize a 0..1 fraction to a byte.
    private static byte Quantize(float fraction01) =>
        (byte)(int)(Clamp01(fraction01) * 255f + 0.5f);

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

    private static int Max(int a, int b) => a > b ? a : b;

    private static int Min(int a, int b) => a < b ? a : b;
}
