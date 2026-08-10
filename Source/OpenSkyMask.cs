using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// The additive pass's roof mask, live half: builds and caches the open-sky mesh its consumers draw
// through, from the runs OpenSkyMaskMath computes off Map.roofGrid.
//
// SHARED, NOT §24's. It was written for snow glare (issue #90) and is now also what §23b's cloud
// underlight draws through (issue #88 option 2), which is the "one pass, many contributors" half of
// epic #103 arriving one consumer at a time rather than as an API guessed at in advance. Nothing
// here knows what is being drawn: the answer to "which cells can see the sky" is a property of the
// map, not of the effect, so both consumers ask the same cache, dirty on the same roof writes, and
// share the one rebuild.
//
// REBUILT ON ROOF CHANGE, NOT PER FRAME, and that is the whole reason this is affordable. §16's cost
// ledger (issues #20, #60) is about MapMeshFlagDefOf.Snow, which SnowGrid.CheckVisualOrPathCostChange
// raises constantly during snowfall — a per-cell snow term would rebuild geometry all winter. Roofs
// are the opposite kind of state: they change when a player builds, when something collapses, and
// otherwise never. So a mask keyed on ROOFS gets per-cell precision at a rebuild rate closer to zero
// than to per-frame, which is exactly the trade §21's own deferred per-cell arm could not make.
//
// Keyed by map rather than held as a single static, because two maps can be loaded at once (a caravan
// map alongside the colony) and they have unrelated roofs. Small dictionary, one entry per map that
// has ever drawn through the mask, cleared by MarkDirty when the map is gone.
//
// [StaticConstructorOnStartup] for the usual reason — `new Mesh()` must happen on Unity's main
// thread. See SnowGlareOverlay's header.
[StaticConstructorOnStartup]
public static class OpenSkyMask
{
    private sealed class MaskEntry
    {
        public Mesh Mesh;          // null means "the whole map is open, use wholeMapPlane"
        public bool Dirty = true;
    }

    private static readonly Dictionary<int, MaskEntry> Entries = new Dictionary<int, MaskEntry>();

    // Marks a map's mask stale. Called from Patch_OpenSkyMaskInvalidation on every roof write, so it
    // must stay trivial — it sets a bool and returns, and the rebuild happens on the next frame that
    // actually draws. A map that never draws through the mask therefore never pays for the roofs it
    // builds.
    public static void MarkDirty(Map map)
    {
        if (map == null)
            return;

        if (Entries.TryGetValue(map.uniqueID, out MaskEntry entry))
            entry.Dirty = true;
    }

    // The mesh to draw for this map, rebuilding it first if a roof has changed since the last draw.
    // Returns MeshPool.wholeMapPlane whenever nothing is roofed — the overwhelmingly common case, and
    // a shared mesh vanilla already keeps resident — and null when the map is entirely roofed, which
    // the caller must read as "draw nothing".
    public static Mesh MeshFor(Map map)
    {
        if (map == null)
            return null;

        if (!Entries.TryGetValue(map.uniqueID, out MaskEntry entry))
        {
            entry = new MaskEntry();
            Entries[map.uniqueID] = entry;
        }

        if (entry.Dirty)
        {
            Rebuild(map, entry);
            entry.Dirty = false;
        }

        return entry.Mesh;
    }

    private static void Rebuild(Map map, MaskEntry entry)
    {
        int width = map.Size.x;
        int height = map.Size.z;

        List<OpenSkyMaskMath.Run> runs =
            OpenSkyMaskMath.UnroofedRuns(ReadRoofed(map, width, height), width, height);

        // Nothing roofed: hand back vanilla's shared plane and keep no mesh of our own. Checked before
        // the empty case because it is the one that happens on almost every map almost all the time.
        if (OpenSkyMaskMath.CoversWholeMap(runs, width, height))
        {
            entry.Mesh = MeshPool.wholeMapPlane;
            return;
        }

        if (runs.Count == 0)
        {
            entry.Mesh = null;
            return;
        }

        entry.Mesh = BuildMesh(runs, width, height);
    }

    private static bool[] ReadRoofed(Map map, int width, int height)
    {
        bool[] roofed = new bool[width * height];
        RoofGrid grid = map.roofGrid;

        // Indexed z * width + x, matching what OpenSkyMaskMath expects and what RimWorld's own cell
        // indexing uses, so neither side has to transpose.
        for (int z = 0; z < height; z++)
        {
            int rowStart = z * width;
            for (int x = 0; x < width; x++)
                roofed[rowStart + x] = grid.Roofed(x, z);
        }

        return roofed;
    }

    // One quad per run, in the XZ plane at y = 0 — the draw call supplies the altitude, the same way
    // SkyOverlay.DrawWorldOverlay does for the shared plane.
    //
    // Vertex order and triangle winding are copied from AuroraCurtainOverlay's own quad builder,
    // which took them from the decompiled Verse.MeshMakerPlanes.NewPlaneMesh rather than reasoning
    // them out — a quad wound the wrong way renders as nothing at all, with no error and no clue.
    //
    // THE UVs ARE MAP-SPACE, NOT PER-QUAD, and that is the one thing here that is not vanilla's shape.
    // Each vertex gets (x / width, z / height), so the whole mask is one continuous 0..1 chart over
    // the map no matter how many runs it was cut into. Per-quad 0..1 UVs — the obvious copy of the
    // aurora's single-quad builder, and what this file did while §24 was its only consumer — would
    // stamp a whole copy of the texture into every run, so a colony's rooms would slice the sky into
    // dozens of misaligned tiles.
    //
    // §24 cannot see the difference (its material has no texture, so the sampler returns Unity's
    // default white whatever the coordinates say), which is exactly why this is safe to change under
    // it: the convention is chosen once, here, by the consumer that does sample a texture, rather
    // than each consumer bringing its own mesh. It also matches MeshPool.wholeMapPlane, the fallback
    // returned above — vanilla's weather overlays rely on that plane charting 0..1 across the map and
    // tile themselves with mainTextureScale, which is precisely what a consumer of this mesh must do.
    //
    // IndexFormat.UInt32 because the bound is not obviously safe: 16-bit indices cap a mesh at 65,535
    // vertices, i.e. ~16,000 runs, and while a realistic colony produces a few hundred, a pathological
    // roof pattern (alternating roofed cells) on a large map would exceed it. Paying for 32-bit
    // indices on a mesh rebuilt this rarely is far cheaper than a silently truncated mask.
    private static Mesh BuildMesh(List<OpenSkyMaskMath.Run> runs, int width, int height)
    {
        int quads = runs.Count;
        float uScale = 1f / width;
        float vScale = 1f / height;
        Vector3[] verts = new Vector3[quads * 4];
        Vector2[] uvs = new Vector2[quads * 4];
        int[] tris = new int[quads * 6];

        for (int i = 0; i < quads; i++)
        {
            OpenSkyMaskMath.Run run = runs[i];
            int v = i * 4;
            int t = i * 6;

            // Cell (x, z) spans world x..x+1, so a run ending at XEnd reaches XEnd + 1.
            float x0 = run.XStart;
            float x1 = run.XEnd + 1;
            float z0 = run.Z;
            float z1 = run.Z + 1;

            verts[v + 0] = new Vector3(x0, 0f, z0);
            verts[v + 1] = new Vector3(x0, 0f, z1);
            verts[v + 2] = new Vector3(x1, 0f, z1);
            verts[v + 3] = new Vector3(x1, 0f, z0);

            uvs[v + 0] = new Vector2(x0 * uScale, z0 * vScale);
            uvs[v + 1] = new Vector2(x0 * uScale, z1 * vScale);
            uvs[v + 2] = new Vector2(x1 * uScale, z1 * vScale);
            uvs[v + 3] = new Vector2(x1 * uScale, z0 * vScale);

            tris[t + 0] = v + 0;
            tris[t + 1] = v + 1;
            tris[t + 2] = v + 2;
            tris[t + 3] = v + 0;
            tris[t + 4] = v + 2;
            tris[t + 5] = v + 3;
        }

        Mesh mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.SetTriangles(tris, 0);
        return mesh;
    }
}
