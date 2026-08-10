using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// §24's roof mask, live half: builds and caches the open-sky mesh SnowGlareOverlay draws, from the
// runs SnowGlareMaskMath computes off Map.roofGrid.
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
// has ever drawn glare, cleared by MarkDirty when the map is gone.
//
// [StaticConstructorOnStartup] for the usual reason — `new Mesh()` must happen on Unity's main
// thread. See SnowGlareOverlay's header.
[StaticConstructorOnStartup]
public static class SnowGlareMask
{
    private sealed class MaskEntry
    {
        public Mesh Mesh;          // null means "the whole map is open, use wholeMapPlane"
        public bool Dirty = true;
    }

    private static readonly Dictionary<int, MaskEntry> Entries = new Dictionary<int, MaskEntry>();

    // Marks a map's mask stale. Called from Patch_SnowGlareRoofInvalidation on every roof write, so it
    // must stay trivial — it sets a bool and returns, and the rebuild happens on the next frame that
    // actually draws. A map that never draws glare therefore never pays for the roofs it builds.
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

        List<SnowGlareMaskMath.Run> runs =
            SnowGlareMaskMath.UnroofedRuns(ReadRoofed(map, width, height), width, height);

        // Nothing roofed: hand back vanilla's shared plane and keep no mesh of our own. Checked before
        // the empty case because it is the one that happens on almost every map almost all the time.
        if (SnowGlareMaskMath.CoversWholeMap(runs, width, height))
        {
            entry.Mesh = MeshPool.wholeMapPlane;
            return;
        }

        if (runs.Count == 0)
        {
            entry.Mesh = null;
            return;
        }

        entry.Mesh = BuildMesh(runs);
    }

    private static bool[] ReadRoofed(Map map, int width, int height)
    {
        bool[] roofed = new bool[width * height];
        RoofGrid grid = map.roofGrid;

        // Indexed z * width + x, matching what SnowGlareMaskMath expects and what RimWorld's own cell
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
    // Vertex order, UVs and triangle winding are copied from AuroraCurtainOverlay's own quad builder,
    // which took them from the decompiled Verse.MeshMakerPlanes.NewPlaneMesh rather than reasoning
    // them out — a quad wound the wrong way renders as nothing at all, with no error and no clue.
    //
    // IndexFormat.UInt32 because the bound is not obviously safe: 16-bit indices cap a mesh at 65,535
    // vertices, i.e. ~16,000 runs, and while a realistic colony produces a few hundred, a pathological
    // roof pattern (alternating roofed cells) on a large map would exceed it. Paying for 32-bit
    // indices on a mesh rebuilt this rarely is far cheaper than a silently truncated mask.
    private static Mesh BuildMesh(List<SnowGlareMaskMath.Run> runs)
    {
        int quads = runs.Count;
        Vector3[] verts = new Vector3[quads * 4];
        Vector2[] uvs = new Vector2[quads * 4];
        int[] tris = new int[quads * 6];

        for (int i = 0; i < quads; i++)
        {
            SnowGlareMaskMath.Run run = runs[i];
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

            uvs[v + 0] = new Vector2(0f, 0f);
            uvs[v + 1] = new Vector2(0f, 1f);
            uvs[v + 2] = new Vector2(1f, 1f);
            uvs[v + 3] = new Vector2(1f, 0f);

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
