using Verse;

namespace CelestialLighting;

// Thin live-state adapter over EavesMath (which carries the full "why" for §15): resolves the
// effective shadow-caster height of every cell one section regenerate can look at, so the mesh
// builder in Patch_ShadowMeshPerimeter reads plain floats and never has to know that roofs are
// involved at all.
//
// Why a precomputed patch instead of a lookup per read:
//   Vanilla's Regenerate reads each cell up to five times — once as the cell being extruded, and
//   once more as each of its four neighbours' neighbour. Resolving on demand would mean up to five
//   Room queries per cell; baking the (Section.Size + 2)^2 = 361-cell window once means exactly one.
//
// Why a window and not the whole map:
//   This is the specific thing that makes re-implementing Perspective: Eaves worth it rather than
//   calling into it. Its RoofShadows.GetAdjustedList copies the entire edifice array and walks the
//   entire roof grid on EVERY section regenerate — on a 275x275 map a full-map roof change is ~121
//   sections x ~75k cells of room queries and ~36 MB of transient arrays. A section only ever draws
//   its own cells plus a one-cell skirt, so that is all we resolve.
//
// Allocated per regenerate rather than kept as a reusable static scratch buffer: 361 floats is
// ~1.4 KB against a call that only fires when a section is dirtied (never per frame), and a shared
// buffer would corrupt the mesh if anything ever re-entered Regenerate from inside Regenerate.
public readonly struct EaveShadowGrid
{
    private readonly float[] heights;
    private readonly int minX;
    private readonly int minZ;
    private readonly int width;

    private EaveShadowGrid(float[] heights, int minX, int minZ, int width)
    {
        this.heights = heights;
        this.minX = minX;
        this.minZ = minZ;
        this.width = width;
    }

    // `sectionRect` must already be clipped inside the map (Regenerate does this). The skirt is
    // clipped separately, so a section on the map edge simply resolves a smaller window — the mesh
    // builder's own bounds guards mean it never asks for a cell outside it.
    public static EaveShadowGrid Build(Map map, CellRect sectionRect, bool eaveShadows)
    {
        int minX = Max(sectionRect.minX - 1, 0);
        int minZ = Max(sectionRect.minZ - 1, 0);
        int maxX = Min(sectionRect.maxX + 1, map.Size.x - 1);
        int maxZ = Min(sectionRect.maxZ + 1, map.Size.z - 1);

        int width = maxX - minX + 1;
        float[] heights = new float[width * (maxZ - minZ + 1)];

        Building[] innerArray = map.edificeGrid.InnerArray;
        CellIndices cellIndices = map.cellIndices;

        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
                heights[(z - minZ) * width + (x - minX)] = CellHeight(map, innerArray, cellIndices, x, z, eaveShadows);
        }

        return new EaveShadowGrid(heights, minX, minZ, width);
    }

    // Caster height at one cell. Callers must stay inside the window Build resolved.
    public float At(int x, int z) => heights[(z - minZ) * width + (x - minX)];

    private static float CellHeight(
        Map map, Building[] innerArray, CellIndices cellIndices, int x, int z, bool eaveShadows)
    {
        Building edifice = innerArray[cellIndices.CellToIndex(x, z)];
        float edificeHeight = edifice == null ? 0f : edifice.def.staticSunShadowHeight;

        // Feature off means bit-for-bit the pre-§15 lookup: whatever the edifice grid says, nothing
        // else. That is what makes "off" a faithful vanilla baseline for the harness A/B, and it is
        // also the fast path — no roof grid, no region query.
        if (!eaveShadows)
            return edificeHeight;

        // EaveCells short-circuits on the roof grid before it reaches the room query, which is the
        // expensive half — this runs over every cell of a 361-cell window, and the overwhelming
        // majority of cells on any map are unroofed.
        //
        // CastsRoofShadow, not IsEave: a mountain roofline casts here even though it is not an eave
        // for §7b/§15b's purposes. See EavesMath for the seam that split them.
        IntVec3 cell = new IntVec3(x, 0, z);
        return EavesMath.CasterHeight(edificeHeight, EaveCells.CastsRoofShadow(map, cell));
    }

    private static int Max(int a, int b) => a > b ? a : b;

    private static int Min(int a, int b) => a < b ? a : b;
}
