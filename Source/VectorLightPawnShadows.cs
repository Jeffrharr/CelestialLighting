using System.Collections.Generic;
using System.Threading.Tasks;
using RimWorld;
using UnityEngine;
using Verse;
using Contribution = CelestialLighting.PawnShadowMath.ContributionData;
using DrawnShadow = CelestialLighting.PawnShadowMath.DrawnShadow;

namespace CelestialLighting;

// §27 phase 4: pawns cast shadows away from the lamps that light them.
//
// WHAT VANILLA DOES, AND WHY IT CANNOT DO THIS. A pawn's shadow is one mesh from ShadowMeshPool
// drawn at a fixed per-def offset with MatBases.SunShadowFade. Its direction and length come from
// `_CastVect`, a **global** the sky manager sets once a frame — so every shadow on the map leans the
// same way, which is right for the sun and useless for a torch. Per-lamp direction is not reachable
// through that material at all, and it is not reachable by setting the global between draws either:
// Graphics.DrawMesh is DEFERRED, so a global written between calls applies to whichever call resolves
// last. VectorLightOverlay's header records the same trap costing §17 a branch.
//
// SO THE EXTRUSION IS BAKED HERE INSTEAD. The mesh is the caster's footprint extruded along +X by
// the shadow's length, pointed with the transform — no per-frame mesh rebuild, and the meshes are
// cached per (footprint, length) bucket the way VectorLightOverlay caches a gradient per radius.
//
// AND IT IS NOT DRAWN THROUGH MatBases.SunShadowFade, which was the first attempt and rendered a
// small OPAQUE BLACK BOX rather than a long faint shadow. Two things were wrong with borrowing that
// material and both are inherent to it: its vertex ALPHA is the extrusion channel (`vertexAlpha *
// _CastVect`), so the mesh's own colours are not free for us to use, and its opacity comes from the
// material colour that SkyManager lerps for the whole map rather than from anything a per-draw
// property block can reach. A solid-colour transparent material has neither problem: no _CastVect,
// and alpha that means alpha. It costs one cached material per opacity bucket, which is the same
// trade SolidColorMaterials exists to make.
//
// WHY THE MASK IS WHAT MAKES IT CORRECT. A pawn behind a wall must not throw a shadow from a lamp
// that cannot see it. §27 phase 3 already bakes, per emitter, the share of every cell that emitter
// reaches — so the question costs one array lookup. Nothing else in the mod can answer it: the
// crossfade knows only a global constant, and vanilla's glow grid would say yes, because its light
// bends around corners.
//
// ROOFS AND EAVES ARE DELIBERATELY NOT SKIPPED. Vanilla's Graphic_Shadow bails on any roofed cell,
// which is correct for sunlight and exactly backwards here — indoors under a lamp is the whole point
// of the feature, and §15's eaves are likewise a sun concept with no bearing on a torch. This is the
// one place §27 knowingly renders a shadow where vanilla renders none.
[StaticConstructorOnStartup]
public static class VectorLightPawnShadows
{
    // Cached per (footprint, length) bucket, quarter-cell keys, on the same reasoning and in the
    // same shape as VectorLightOverlay's gradient cache: a colony has a handful of distinct pawn
    // sizes and the length quantises to a few dozen values, so the cache saturates almost at once.
    private static readonly Dictionary<long, Mesh> MeshCache = new Dictionary<long, Mesh>();

    private static readonly List<Vector3> Verts = new List<Vector3>();
    private static readonly List<Color32> Colors = new List<Color32>();
    private static readonly List<Vector2> Uvs = new List<Vector2>();
    private static readonly List<int> Tris = new List<int>();

    // One material per opacity step. Quantised to 16 levels because these are faint, overlapping
    // shadows: the eye cannot tell 0.104 from 0.110, and an unquantised key would mint a material
    // per pawn per lamp per frame — SolidColorMaterials caches for the life of the process.
    private const int OpacitySteps = 16;

    private static readonly Dictionary<int, Material> MaterialCache = new Dictionary<int, Material>();

    // The feathered path's own bucket cache, kept SEPARATE from the flat one rather than keyed by a
    // flag: these are different shaders, and one dictionary holding both would hand an arm the other
    // arm's material the first time a bucket was reused — an A/B that measures nothing while every
    // flag reads as set, which is the same trap the mesh cache's taper key exists to avoid.
    private static readonly Dictionary<int, Material> FeatheredMaterialCache =
        new Dictionary<int, Material>();

    private const int RampTexels = 64;

    private static Texture2D RampTextureCache;

    // What tip opacity RampTextureCache was built for, so the harness's flat-ramp control arm gets a
    // rebuilt row instead of the shipped one.
    private static float RampTip = float.NaN;

    // One lamp's contribution to the pawn currently being drawn, carried from the first pass to the
    // second so the second does not recompute a distance and a coverage lookup it already paid for.
    // Now an alias for PawnShadowMath.ContributionData -- see that file's header for why the
    // arithmetic that fills and reads this moved there.
    //
    // ONE PER THREAD, NOT ONE SHARED LIST. Build (and the Gather/ShareFor/OtherIlluminanceAt calls
    // it makes) writes and reads this while the parallel build phase below may be running several
    // pawns' Builds concurrently across the pool — a single shared list would interleave two
    // threads' contributions into one pawn's shadow, which is not a crash, just a wrong shadow. The
    // property lazily creates one list per worker, on the same reasoning as
    // VectorLightField.Scratch: a few hundred bytes per thread the pool reuses, against a bug a
    // CIELAB comparison would never catch.
    [System.ThreadStatic]
    private static List<Contribution> ThreadContributions;

    private static List<Contribution> Contributions =>
        ThreadContributions ??= new List<Contribution>();

    // Everything Build needs about one pawn, resolved from live game state BEFORE any fan-out. The
    // parallel build phase below only ever touches primitives and plain structs — never a Pawn, a
    // Map, or anything else `Verse`/`UnityEngine` could mutate out from under a worker thread — and
    // this struct is the boundary that makes that true. Mirrors the split
    // VectorLightField.BakeSelected draws between its serial gather and its parallel bake.
    private struct PawnShadowInput
    {
        public Vector3 DrawPos;
        public int PositionX;
        public int PositionZ;
        public ShadowData Shadow;
        public bool Roofed;
    }

    // One gathered pawn per slot, and one persistent, INDEX-OWNED results list per slot. The results
    // list is what makes the parallel phase safe: each worker writes only PendingShadows[i], for the
    // i it was handed, so two pawns building concurrently never touch the same list — and because
    // the list is a slot rather than a per-call return value, it survives past the Parallel.For join
    // for the serial draw pass to read. Reused frame to frame rather than reallocated, same as every
    // other scratch collection in this file.
    private static readonly List<PawnShadowInput> PendingInputs = new List<PawnShadowInput>();
    private static readonly List<Vector3> PendingAnchors = new List<Vector3>();
    private static readonly List<List<DrawnShadow>> PendingShadows = new List<List<DrawnShadow>>();

    public static void Draw(Map map)
    {
        if (!CelestialLightingFeatures.VectorLightPawnShadows || map == null)
            return;

        // Nothing to cast from if §27 itself is off, and nothing trustworthy to ask about occlusion
        // if the mask is not the one composing — see the header.
        if (!CelestialLightingFeatures.VectorLights || !VectorLightMask.Active)
            return;

        IReadOnlyList<Pawn> pawns = map.mapPawns?.AllPawnsSpawned;

        if (pawns == null || pawns.Count == 0)
            return;

        Dictionary<object, VectorLightField.LightEntry>.ValueCollection lights =
            VectorLightField.LightsFor(map);

        if (lights.Count == 0)
            return;

        CellRect view = Find.CameraDriver.CurrentViewRect;
        float skyGlow = map.skyManager.CurSkyGlow;
        float altitude = AltitudeLayer.Shadows.AltitudeFor();

        // The roster every pawn below will walk, culled against the camera ONCE rather than paid
        // for by each pawn — see CelestialLightingFeatures.VectorLightShadowLampCull. A pawn is
        // only considered inside `view`, so a lamp whose reach misses `view` cannot light one, and
        // the culled list draws exactly what the whole roster would.
        CollectLamps(lights, view, CelestialLightingFeatures.VectorLightShadowLampCull);

        if (Lamps.Count == 0)
            return;

        // SERIAL GATHER: every live-state read a pawn's shadow depends on, resolved here on the
        // calling thread, before either the parallel or the serial build phase below can start. See
        // VectorLightField.BakeSelected's header for why this split is what makes the phase after it
        // safe rather than merely convenient.
        GatherInputs(map, pawns, view);

        if (PendingInputs.Count == 0)
            return;

        // PURE ARITHMETIC PHASE: parallel when the flag and the batch size justify it, serial
        // otherwise, but ALWAYS in the same index order either way — nothing here may skip a pawn or
        // reorder the roster, on the same reasoning ShouldFanOut's own header gives.
        BuildAll(skyGlow);

        DrawAll(altitude);
    }

    // Below this many gathered pawns the batch is built on the calling thread. Mirrors
    // VectorLightField.ParallelBakeMinimum: a fan-out has to wake pool threads, hand out ranges and
    // join, which is not free against one pawn's Build, and a threshold nobody can measure the
    // effect of is a knob that only generates support questions. Not a setting for the same reason.
    private const int ParallelBuildMinimum = 4;

    private static bool ShouldFanOutBuild(int count) =>
        CelestialLightingFeatures.VectorLightShadowParallelBuild
        && count >= ParallelBuildMinimum
        && System.Environment.ProcessorCount > 1;

    // Reads live pawn/map state onto plain primitives, on the calling thread — the SERIAL half of
    // the split. Nothing past this method may touch `pawn`, `map` or `view` again this frame.
    private static void GatherInputs(Map map, IReadOnlyList<Pawn> pawns, CellRect view)
    {
        PendingInputs.Clear();
        PendingAnchors.Clear();

        for (int i = 0; i < pawns.Count; i++)
        {
            Pawn pawn = pawns[i];

            // Culled against the camera first, for the same reason VectorLightOverlay culls: a
            // colony's pawns are mostly off screen and this runs every frame.
            if (pawn == null || !pawn.Spawned || !view.Contains(pawn.Position))
                continue;

            // Then the states vanilla refuses to draw a shadow in, which are not about sunlight and
            // so are not ours to diverge from — see VectorLightMath.PawnCastsShadow. Asked after the
            // camera cull because it is the more expensive of the two (IsPsychologicallyInvisible
            // walks the hediff set) and the cull rejects most of a colony.
            if (!CastsShadow(pawn))
                continue;

            ShadowData shadow = ShadowDataOf(pawn);
            Vector3 centre = pawn.DrawPos;

            PendingInputs.Add(new PawnShadowInput
            {
                DrawPos = centre,
                PositionX = pawn.Position.x,
                PositionZ = pawn.Position.z,
                Shadow = shadow,
                Roofed = RoofedAt(map, pawn),
            });

            // Resolved here rather than inside Build, even though Build resolves its own copy too
            // (see AnchorOf's own header for why that duplication is deliberate): the DRAW needs the
            // anchor and Build does not hand one back, so this is the gather's own copy for the same
            // reason DrawFor used to keep one before this split existed.
            PendingAnchors.Add(AnchorOf(centre, shadow));
        }

        while (PendingShadows.Count < PendingInputs.Count)
            PendingShadows.Add(new List<DrawnShadow>());
    }

    // The PURE ARITHMETIC PHASE, run either across the pool or on the calling thread — see
    // ShouldFanOutBuild. Every call writes only PendingShadows[i] for the i it was given, which is
    // what makes the fan-out safe: two workers never share a results list, and Contributions is
    // thread-local so two workers never share scratch either.
    private static void BuildAll(float skyGlow)
    {
        int count = PendingInputs.Count;

        if (LargestBuildBatch < count)
            LargestBuildBatch = count;

        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        // Projected onto the pure core's primitive LightEntryData ONCE here, serially, rather than
        // once per pawn inside the fan-out below -- see RefreshLampsData's own header for why this
        // is what actually makes the Build call safe to hand to Parallel.For.
        RefreshLampsData();

        if (ShouldFanOutBuild(count))
        {
            ParallelBuildPasses++;
            Parallel.For(0, count, i => Build(PendingInputs[i], LampsData, skyGlow, PendingShadows[i]));
        }
        else
        {
            SerialBuildPasses++;

            for (int i = 0; i < count; i++)
                Build(PendingInputs[i], LampsData, skyGlow, PendingShadows[i]);
        }

        BuildWallMs += clock.Elapsed.TotalMilliseconds;
    }

    // The lamps the pawn-shadow pass may draw from this frame. Static and reused for the reason
    // Contributions is: filled once per Draw on the main thread, read by every pawn in that Draw,
    // and nothing in it outlives the call.
    private static readonly List<VectorLightField.LightEntry> Lamps =
        new List<VectorLightField.LightEntry>();

    // Fill Lamps from the roster, dropping lamps whose reach misses `view` when `cull` is set.
    //
    // THE OFF ARM COPIES THE WHOLE ROSTER rather than handing Gather the dictionary's own
    // collection, so the two arms run the same Gather over the same list type and differ only in
    // how long that list is. A copy of a few hundred references costs a fraction of one pawn's
    // walk over them; making the off arm avoid it would mean two Gather bodies, and then the A/B
    // would be comparing two loops rather than two rosters.
    private static void CollectLamps(
        Dictionary<object, VectorLightField.LightEntry>.ValueCollection lights, CellRect view,
        bool cull)
    {
        Lamps.Clear();

        foreach (VectorLightField.LightEntry entry in lights)
        {
            if (!cull || VectorLightMath.ReachTouchesRect(
                    entry.Cell.x, entry.Cell.z, entry.Radius,
                    view.minX, view.minZ, view.maxX, view.maxZ))
            {
                Lamps.Add(entry);
            }
        }
    }

    // Lamps' own primitive projection, rebuilt from it every Draw -- see PawnShadowMath.
    // LightEntryData's header for exactly which fields survive the trip. Reused rather than
    // reallocated like every other scratch list in this file.
    private static readonly List<PawnShadowMath.LightEntryData> LampsData =
        new List<PawnShadowMath.LightEntryData>();

    // WHY THIS CONVERSION HAS TO HAPPEN HERE, ONCE, RATHER THAN INSIDE Build ON THE POOL. Coverage
    // is a `byte[]` and Polygon a VectorLightMath.LightPolygon -- both copied by reference or by
    // value with nothing left mutable behind them, so sharing one LampsData list across every
    // worker's Build call this frame is exactly as safe as the pre-split code already was, which
    // read `entry.Coverage` straight off the shared Lamps list inside the very same Parallel.For.
    // Doing it once here rather than once per pawn is the only thing that changes: a colony's lamp
    // count is far smaller than its pawn count, so re-deriving the same handful of LightEntryData
    // values on every worker would be strictly wasted work, not a safety question.
    private static void RefreshLampsData()
    {
        LampsData.Clear();

        for (int i = 0; i < Lamps.Count; i++)
        {
            VectorLightField.LightEntry entry = Lamps[i];

            LampsData.Add(new PawnShadowMath.LightEntryData
            {
                CellX = entry.Cell.x,
                CellZ = entry.Cell.z,
                Radius = entry.Radius,
                CoverageRadius = entry.CoverageRadius,
                Coverage = entry.Coverage,
                Polygon = entry.Polygon,
            });
        }
    }

    // Everything about one shadow a pawn casts from one lamp, resolved once.
    //
    // A struct carried between two consumers rather than a draw that computes as it goes, because
    // the PROBE has to see these numbers. The quantity issue #166 is about is a LENGTH, and a length
    // is no more visible to a screenshot than an alpha was: a shadow that stops at a wall and one
    // that crosses it differ only in pixels on the far side of the wall.
    //
    // Now an alias for PawnShadowMath.DrawnShadow (see this file's `using DrawnShadow = ...` line):
    // the struct itself has no UnityEngine/Verse dependency, so it moved to the pure file alongside
    // the arithmetic that fills it, for the offline test's sake.

    // ---- counters, read by VectorLightPawnShadowBuildProbe -----------------------------------
    //
    // Mirrors VectorLightField's own counter block: raw increments the build/draw passes make,
    // never derived here, so a probe reading them is reading what actually ran rather than a
    // recomputation that would agree with a formula the frame is not using.
    public static int ParallelBuildPasses;
    public static int SerialBuildPasses;
    public static int LargestBuildBatch;
    public static double BuildWallMs;
    public static int DrawCalls;
    public static int ShadowsDrawn;

    // The batched draw's per-shadow CPU loop alone -- AppendShadowQuad's rotation and vertex
    // writes -- separated from mesh upload and the DrawMesh call around it, because that loop is
    // the specific candidate for moving onto the GPU (the same uniform-array trick
    // VectorLightDrawBatch already uses for emitters), and the mesh upload cost would still be
    // paid either way. Stays 0 on the unbatched arm, where this loop never runs.
    public static double AppendWallMs;

    public static void ResetCounters()
    {
        ParallelBuildPasses = 0;
        SerialBuildPasses = 0;
        LargestBuildBatch = 0;
        BuildWallMs = 0.0;
        DrawCalls = 0;
        ShadowsDrawn = 0;
        AppendWallMs = 0.0;
    }

    private static void DrawAll(float altitude)
    {
        if (CelestialLightingFeatures.VectorLightShadowBatch)
            DrawAllBatched(altitude);
        else
            DrawAllUnbatched(altitude);
    }

    // THE OFF ARM: one Graphics.DrawMesh call per shadow, through the cached small mesh and the
    // per-opacity-bucket material — byte-identical to the pre-batching draw, because it is the same
    // draw, only reached through PendingAnchors/PendingShadows instead of one pawn at a time.
    private static void DrawAllUnbatched(float altitude)
    {
        for (int p = 0; p < PendingInputs.Count; p++)
        {
            Vector3 anchor = PendingAnchors[p];
            List<DrawnShadow> shadows = PendingShadows[p];

            for (int i = 0; i < shadows.Count; i++)
            {
                DrawnShadow drawn = shadows[i];
                Mesh mesh = MeshFor(drawn.Half, drawn.Length, drawn.Taper);

                if (mesh == null)
                    continue;

                // A material per opacity step rather than a property block. Graphics.DrawMesh is
                // deferred, so writing one shared material's colour between calls gives every
                // shadow in the frame whichever opacity was written last — the trap
                // VectorLightOverlay's header records §17 paying for. Distinct materials sidestep
                // it without a property block. Started at the silhouette's trailing edge rather
                // than at its centre, so the length computed above is length BEYOND the caster —
                // the same thing it means for a sun shadow, whose skirt is extruded from that edge
                // too. Pushing the transform rather than baking the offset into the mesh keeps the
                // cache keyed on two numbers instead of three.
                Graphics.DrawMesh(
                    mesh,
                    new Vector3(
                        anchor.x + drawn.UnitX * drawn.TrailingEdge, altitude,
                        anchor.z + drawn.UnitZ * drawn.TrailingEdge),
                    Quaternion.Euler(0f, drawn.AngleDegrees, 0f), MaterialForShadow(drawn.Opacity),
                    0);

                DrawCalls++;
                ShadowsDrawn++;
            }
        }
    }

    // THE ON ARM: every shadow this frame folded into ONE combined mesh and ONE Graphics.DrawMesh
    // call — see the class header addendum below MeshFor for why this needs no new shader, no
    // texture array and no asset bundle. Feathered and flat shadows are never mixed in one frame,
    // because CelestialLightingFeatures.VectorLightShadowFeather is a global flag rather than a
    // per-pawn one, so one combined mesh and one material always covers the whole frame's shadows.
    private static void DrawAllBatched(float altitude)
    {
        BatchVerts.Clear();
        BatchColors.Clear();
        BatchUvs.Clear();
        BatchTris.Clear();

        bool feathered = CelestialLightingFeatures.VectorLightShadowFeather;
        int shadowCount = 0;

        System.Diagnostics.Stopwatch appendClock = System.Diagnostics.Stopwatch.StartNew();

        for (int p = 0; p < PendingInputs.Count; p++)
        {
            Vector3 anchor = PendingAnchors[p];
            List<DrawnShadow> shadows = PendingShadows[p];

            for (int i = 0; i < shadows.Count; i++)
            {
                DrawnShadow drawn = shadows[i];

                Vector3 origin = new Vector3(
                    anchor.x + drawn.UnitX * drawn.TrailingEdge, altitude,
                    anchor.z + drawn.UnitZ * drawn.TrailingEdge);

                AppendShadowQuad(origin, Quaternion.Euler(0f, drawn.AngleDegrees, 0f), drawn,
                    feathered);

                shadowCount++;
            }
        }

        AppendWallMs += appendClock.Elapsed.TotalMilliseconds;

        ShadowsDrawn += shadowCount;

        if (shadowCount == 0)
            return;

        Mesh mesh = BatchMesh();
        mesh.Clear();
        mesh.SetVertices(BatchVerts);
        mesh.SetColors(BatchColors);
        mesh.SetUVs(0, BatchUvs);
        mesh.SetTriangles(BatchTris, 0);

        Material material = feathered ? FeatheredBatchMaterial() : FlatBatchMaterial();

        Graphics.DrawMesh(mesh, Vector3.zero, Quaternion.identity, material, 0);

        DrawCalls++;
    }

    // Reused across frames like every other scratch collection here: a combined mesh is rebuilt
    // every frame regardless (the shadows move), so there is nothing to gain by pooling the buffers
    // themselves beyond not re-allocating their backing arrays.
    private static readonly List<Vector3> BatchVerts = new List<Vector3>();
    private static readonly List<Color32> BatchColors = new List<Color32>();
    private static readonly List<Vector2> BatchUvs = new List<Vector2>();
    private static readonly List<int> BatchTris = new List<int>();

    private static Mesh BatchMeshInstance;

    private static Mesh BatchMesh()
    {
        if (BatchMeshInstance == null)
        {
            BatchMeshInstance = new Mesh { name = "CelestialLighting_PawnShadowBatch" };

            // UInt32 rather than the default 16-bit index buffer: four verts per shadow means the
            // default format runs out at ~16,383 shadows in one frame, which stress_pawn_colony.json
            // exists specifically to approach. Paying for wider indices unconditionally is cheaper
            // than reasoning about when a sixteen-bit mesh silently drops triangles.
            BatchMeshInstance.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        }

        return BatchMeshInstance;
    }

    // Appends one shadow's quad to the frame's batch buffers, in WORLD SPACE — the batched draw call
    // carries no per-instance transform, so each shadow's local silhouette (the same four corners
    // MeshFor lays out, unrotated) is rotated and translated here instead of at draw time. The
    // rotation is the SAME Quaternion.Euler(0f, angleDegrees, 0f) the unbatched draw hands to
    // Graphics.DrawMesh, applied to the vertex directly rather than left for the GPU to apply via a
    // transform — real Unity math rather than a hand-rolled trig formula this repo's offline test
    // project (no UnityEngine reference) could not have verified against Unity's own convention
    // anyway.
    private static void AppendShadowQuad(
        Vector3 origin, Quaternion rotation, DrawnShadow drawn, bool feathered)
    {
        float tipHalf = drawn.Half * drawn.Taper;

        int baseIndex = BatchVerts.Count;

        BatchVerts.Add(origin + rotation * new Vector3(0f, 0f, -drawn.Half));
        BatchVerts.Add(origin + rotation * new Vector3(0f, 0f, drawn.Half));
        BatchVerts.Add(origin + rotation * new Vector3(drawn.Length, 0f, tipHalf));
        BatchVerts.Add(origin + rotation * new Vector3(drawn.Length, 0f, -tipHalf));

        // Opacity rides in vertex alpha instead of a per-material colour, quantised to the SAME 16
        // steps MaterialFor/FeatheredMaterialFor use — so a batched frame lands on the same visible
        // opacity as the unbatched one, off by at most one 255th from the float-step-over-16 versus
        // byte-round-trip-through-255 rounding. That delta is what the batch equivalence measurement
        // has to characterise, not assume away.
        int step = Mathf.Clamp(Mathf.RoundToInt(drawn.Opacity * OpacitySteps), 1, OpacitySteps);
        byte alpha = (byte)Mathf.RoundToInt((float)step / OpacitySteps * 255f);
        Color32 color = new Color32(255, 255, 255, alpha);

        BatchColors.Add(color);
        BatchColors.Add(color);
        BatchColors.Add(color);
        BatchColors.Add(color);

        if (feathered)
        {
            // Same UV layout MeshFor bakes for the unbatched feathered mesh: U is the fraction along
            // the shadow, which is what the shared ramp texture is a function of.
            BatchUvs.Add(new Vector2(0f, 0f));
            BatchUvs.Add(new Vector2(0f, 1f));
            BatchUvs.Add(new Vector2(1f, 1f));
            BatchUvs.Add(new Vector2(1f, 0f));
        }
        else
        {
            // The flat batch's texture is one opaque white texel, so every UV samples the same
            // colour — but it still needs one, because Map/Transparent (unlike Map/SolidColor, the
            // unbatched flat path's shader) samples _MainTex.
            BatchUvs.Add(Vector2.zero);
            BatchUvs.Add(Vector2.zero);
            BatchUvs.Add(Vector2.zero);
            BatchUvs.Add(Vector2.zero);
        }

        BatchTris.Add(baseIndex + 0);
        BatchTris.Add(baseIndex + 1);
        BatchTris.Add(baseIndex + 2);
        BatchTris.Add(baseIndex + 0);
        BatchTris.Add(baseIndex + 2);
        BatchTris.Add(baseIndex + 3);
    }

    private static Texture2D FlatBatchTextureCache;
    private static Material FlatBatchMaterialCache;

    // One opaque-white texel and one material for every flat shadow in the frame, together. The flat
    // path's unbatched shader (Map/SolidColor) ignores vertex colour outright — see MeshFor's own
    // header on that — so batching the flat path means switching IT to Map/Transparent too, the same
    // shader the feathered path already uses, with a texture that contributes nothing but a sample
    // Map/Transparent needs to have. The render queue is copied from the flat material for the exact
    // reason FeatheredMaterialFor's own copy is: a shader swap at a different queue composites
    // against the lighting overlay at a different moment, which reads as a wrong formula rather than
    // an ordering bug.
    private static Material FlatBatchMaterial()
    {
        if (FlatBatchMaterialCache != null)
            return FlatBatchMaterialCache;

        if (FlatBatchTextureCache == null)
        {
            FlatBatchTextureCache = new Texture2D(1, 1, TextureFormat.ARGB32, false)
            {
                name = "CelestialLighting_PawnShadowFlatBatchTex",
                wrapMode = TextureWrapMode.Clamp,
            };
            FlatBatchTextureCache.SetPixel(0, 0, Color.white);
            FlatBatchTextureCache.Apply();
        }

        FlatBatchMaterialCache = MaterialPool.MatFrom(new MaterialRequest
        {
            shader = ShaderDatabase.Transparent,
            mainTex = FlatBatchTextureCache,
            color = Color.white,
            renderQueue = MaterialFor(1f).renderQueue,
            needsMainTex = true,
        });

        return FlatBatchMaterialCache;
    }

    private static Material FeatheredBatchMaterialCache;
    private static Texture2D FeatheredBatchMaterialRamp;

    // The feathered path's own batch material: the SAME ramp texture FeatheredMaterialFor already
    // shares across every unbatched feathered shadow, with the per-shadow opacity moved out of the
    // material colour (white here) and into vertex alpha instead. This is why the feathered path
    // was already most of the way to batchable before this change — one texture per frame, not one
    // per shadow, was the design from the start.
    private static Material FeatheredBatchMaterial()
    {
        // ASKED FIRST, same reasoning as FeatheredMaterialFor's own call: the ramp can be rebuilt
        // out from under a stale cache, and RampTexture() is what tells this apart from that.
        Texture2D ramp = RampTexture();

        if (FeatheredBatchMaterialCache != null && FeatheredBatchMaterialRamp == ramp)
            return FeatheredBatchMaterialCache;

        FeatheredBatchMaterialCache = MaterialPool.MatFrom(new MaterialRequest
        {
            shader = ShaderDatabase.Transparent,
            mainTex = ramp,
            color = Color.white,
            colorTwo = Color.white,
            renderQueue = MaterialFor(1f).renderQueue,
            needsMainTex = true,
        });
        FeatheredBatchMaterialRamp = ramp;

        return FeatheredBatchMaterialCache;
    }

    // Every shadow this pawn casts, geometry and opacity both. The one place either is decided.
    //
    // The draw and the probe call this same builder rather than each deriving its own answer, which
    // is the repo's probe convention and has earned its keep twice in this file already: phase 4b's
    // share and #166's clip are both quantities a screenshot reports only indirectly.
    //
    // A THIN ADAPTER over PawnShadowMath, and deliberately calling Gather and BuildFrom as TWO
    // separate calls rather than the pure core's own one-shot Build combinator — see
    // PawnShadowMath.Build's header for why collapsing them here would silently stop
    // circ_vlshadowgather and circ_vlshadowbuild from measuring two independent call sites.
    private static void Build(
        PawnShadowInput input, List<PawnShadowMath.LightEntryData> lights,
        float skyGlow, List<DrawnShadow> into)
    {
        PawnShadowMath.PawnShadowInputData data = ToInputData(input);

        float totalForShare = Gather(input, lights);

        PawnShadowMath.BuildFrom(
            data,
            skyGlow,
            CelestialLightingFeatures.VectorLightShadowShape,
            CelestialLightingFeatures.VectorLightShadowShares,
            CelestialLightingFeatures.VectorLightShadowGroundShares,
            CelestialLightingFeatures.VectorLightShadowClip,
            totalForShare,
            Contributions,
            into);
    }

    // The primitive projection Build and Gather both hand to PawnShadowMath — see
    // PawnShadowMath.PawnShadowInputData's own header for why anchor and centre travel separately.
    private static PawnShadowMath.PawnShadowInputData ToInputData(PawnShadowInput input)
    {
        ShadowData shadow = input.Shadow;
        Vector3 centre = input.DrawPos;
        Vector3 anchor = AnchorOf(centre, shadow);

        return new PawnShadowMath.PawnShadowInputData
        {
            CentreX = centre.x,
            CentreZ = centre.z,
            AnchorX = anchor.x,
            AnchorZ = anchor.z,
            PositionX = input.PositionX,
            PositionZ = input.PositionZ,
            HalfX = HalfExtent(shadow?.BaseX, shadow),
            HalfZ = HalfExtent(shadow?.BaseZ, shadow),
            CasterHeightShaped = CasterHeightOf(shadow),
            Roofed = input.Roofed,
        };
    }

    // Which lamps light this pawn and how much each contributes, left in Contributions, with the
    // denominator their shares are taken against returned.
    //
    // Split out of DrawFor so the PROBE can call it. That is the repo's probe convention and it
    // matters more than usual here: the quantity under test is an alpha, which no screenshot can
    // report directly, and a probe that recomputed the share from its own copy of the arithmetic
    // could agree with the intended physics while the renderer drew something else.
    //
    // A thin adapter over PawnShadowMath.Gather — see that function's own header for the two-pass
    // reason this returns a denominator rather than drawing anything itself.
    private static float Gather(PawnShadowInput input, List<PawnShadowMath.LightEntryData> lights) =>
        PawnShadowMath.Gather(
            ToInputData(input), lights, CelestialLightingFeatures.VectorLightShadowShares,
            Contributions);

    // Where the caster's footprint actually sits, which is NOT where the pawn is drawn. Vanilla
    // offsets it by ShadowData.offset — (0, 0, -0.3) for a human, i.e. at the feet — and both
    // Graphic_Shadow.DrawWorker and Printer_Shadow.PrintShadow honour that. §27 anchored on DrawPos
    // instead, so a colonist's lamp shadow left their torso while their sun shadow left their feet,
    // 0.3 cells apart and both on screen at dusk (issue #159). The offset is applied unrotated for
    // the same reason vanilla's dynamic path applies it unrotated: PawnRenderer draws pawn shadows
    // as Rot4.North regardless of which way the pawn faces.
    //
    // A named function rather than the expression twice, now that the builder needs it too: the
    // draw's transform and the ground sample below have to agree about where a shadow starts, and
    // this file has already been bitten once by the same quantity being spelled out in more than one
    // place.
    private static Vector3 AnchorOf(Vector3 centre, ShadowData shadow) =>
        shadow == null ? centre : centre + shadow.offset;

    // Every shadow this pawn is about to have drawn, for the probe alone.
    //
    // Returns exactly what the renderer will draw because it runs the same builder — so the peak
    // arm, the composited rosette and the reach a scenario pins are facts about the frame rather
    // than about a model of it.
    public static void ShadowsFor(Map map, Pawn pawn, List<DrawnShadow> into)
    {
        into.Clear();

        if (map == null || pawn == null || !pawn.Spawned || !CastsShadow(pawn))
            return;

        Dictionary<object, VectorLightField.LightEntry>.ValueCollection lights =
            VectorLightField.LightsFor(map);

        if (lights.Count == 0)
            return;

        // The WHOLE roster, uncalled: the probe is asked about pawns the camera may not be on, and
        // the cull is only sound for pawns inside the view rect. It changes nothing for a pawn in
        // view, because the culled list is a superset of every lamp Gather accepts.
        CollectLamps(lights, default, cull: false);
        RefreshLampsData();

        PawnShadowInput input = new PawnShadowInput
        {
            DrawPos = pawn.DrawPos,
            PositionX = pawn.Position.x,
            PositionZ = pawn.Position.z,
            Shadow = ShadowDataOf(pawn),
            Roofed = RoofedAt(map, pawn),
        };

        Build(input, LampsData, map.skyManager.CurSkyGlow, into);
    }

    // Which of the two draw paths this shadow takes. The flag is read here rather than at the call
    // site so the probe and the draw cannot end up on different ones.
    private static Material MaterialForShadow(float opacity) =>
        CelestialLightingFeatures.VectorLightShadowFeather
            ? FeatheredMaterialFor(opacity)
            : MaterialFor(opacity);

    private static Material MaterialFor(float opacity)
    {
        int step = Mathf.Clamp(Mathf.RoundToInt(opacity * OpacitySteps), 1, OpacitySteps);

        if (!MaterialCache.TryGetValue(step, out Material material))
        {
            // SimpleSolidColorMaterial rather than a hand-rolled one over ShaderDatabase
            // .Transparent: that shader samples _MainTex, and a material with no texture drew
            // NOTHING AT ALL — a silent nothing, which after the opaque black box is the second way
            // this has failed to look like a shadow. SolidColor is the shader meant for a coloured
            // quad with no texture, and SolidColorMaterials caches by colour for us.
            //
            // (The feathered path below DOES go through Map/Transparent, and gets away with it for
            // exactly the reason this one could not: it supplies a texture.)
            material = SolidColorMaterials.SimpleSolidColorMaterial(
                new Color(0f, 0f, 0f, (float)step / OpacitySteps));
            MaterialCache[step] = material;
        }

        return material;
    }

    // The same shadow, faded along its length by a ramp texture rather than held flat.
    //
    // WHY A TEXTURE AND NOT A PER-VERTEX ALPHA. The obvious cheap route is to grade the mesh's own
    // vertex colours, and it is closed twice over: `Map/SolidColor` ignores vertex colour outright,
    // and the material that reads it (`Custom/Sun shadow fade`) spends its alpha channel on the
    // extrusion. It is closed a third time on principle — a per-vertex *value* interpolates linearly
    // across a triangle, so a curve sampled that way is wrong by triangle length, which is precisely
    // how the shader-max attempt failed before it was fixed with a texture. A UV is a *position*, and
    // position across a triangle really is linear, so the curve stays exact wherever it is sampled.
    //
    // One texture for the whole map, not one per shadow: the ramp is a pure function of the fraction
    // along the shadow, so every shadow at every length and every opacity samples the same row. The
    // per-shadow opacity stays in the material colour, where it already was, and `Map/Transparent`
    // multiplies the two.
    private static Material FeatheredMaterialFor(float opacity)
    {
        // ASKED FIRST, ON EVERY DRAW, and deliberately not only when the material lookup misses.
        // The two caches invalidate each other — rebuilding the ramp clears the materials — so
        // reaching the ramp only through a material-cache miss makes the pair unreachable the moment
        // one material exists: the row is fixed for the rest of the session and nothing can ever
        // change it again.
        //
        // That is not hypothetical. The harness runs one step per frame, so a scenario arm that sets
        // two flags renders a frame between them; the control arm built its material in that
        // one-frame window, with the first flag applied and the second not, and then held the wrong
        // ramp for the whole arm. Every derived number said the arm had changed and the pixels never
        // did. When the ramp has not moved this costs one float comparison, which is not worth
        // trading a whole class of invisible staleness for.
        Texture2D ramp = RampTexture();

        int step = Mathf.Clamp(Mathf.RoundToInt(opacity * OpacitySteps), 1, OpacitySteps);

        if (!FeatheredMaterialCache.TryGetValue(step, out Material material))
        {
            // THE RENDER QUEUE IS COPIED FROM THE FLAT MATERIAL, DELIBERATELY. Turning this feature
            // on swaps the shader, and a shader at a different queue composites against the lighting
            // overlay at a different moment — which shows up as the whole shadow changing darkness
            // rather than as a gradient appearing, and reads as a wrong formula rather than an
            // ordering bug. Pinning the queue to the one the flat path already used makes the swap
            // compositionally neutral, so the A/B measures the curve and nothing else. The scenario
            // keeps a flat-ramp arm through THIS material anyway, because a comment is not a control.
            material = MaterialPool.MatFrom(new MaterialRequest
            {
                shader = ShaderDatabase.Transparent,
                mainTex = ramp,
                color = new Color(0f, 0f, 0f, (float)step / OpacitySteps),
                colorTwo = Color.white,
                renderQueue = MaterialFor(opacity).renderQueue,
                needsMainTex = true,
            });

            FeatheredMaterialCache[step] = material;
        }

        return material;
    }

    // One texel row, alpha falling from full at the caster to nothing at the tip.
    //
    // 64 texels because the ramp is sampled bilinearly across a shadow that is rarely more than a
    // cell or two on screen — at the zoom these are looked at, a cell is about 50 px, so 64 texels
    // over the whole length is already finer than the pixels it lands on. Rebuilt when the curve
    // changes, which in a shipped game is never: the harness's flat-ramp control arm is the only
    // thing that moves it.
    private static Texture2D RampTexture()
    {
        float frontLoad = VectorLightMath.PawnShadowFadeFrontLoad;

        // KEYED ON THE CURVE'S OWN MIDPOINT, NOT ON THE CONSTANT BEHIND IT. Reading the constant
        // would make this cache blind to anything that changes the curve without changing the
        // constant — which is exactly what the harness's flat-ramp control arm does, by postfixing
        // PawnShadowFade. Keyed this way, flipping that arm invalidates the row on the next draw
        // instead of leaving the previous arm's gradient on screen, which is the failure an in-run
        // A/B would otherwise photograph as "the flag did nothing".
        //
        // The MIDPOINT rather than the endpoint, which the endpoint version of this got wrong: the
        // curve now ends at exactly 0 by construction, so its tip is 0 for every front-load value
        // and would key every distinct curve to the same cache entry. Halfway along, it separates
        // them — and it still separates the flat control arm, which reads 1 there.
        float key = VectorLightMath.PawnShadowFade(0.5f, frontLoad);

        if (RampTextureCache != null && RampTip == key)
            return RampTextureCache;

        Texture2D ramp = new Texture2D(RampTexels, 1, TextureFormat.ARGB32, false)
        {
            name = "CelestialLighting_PawnShadowRamp",

            // Clamp, not repeat: a bilinear sample at the very tip would otherwise blend the far end
            // of the ramp with its own full-opacity start and put a bright seam on the last texel.
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };

        for (int i = 0; i < RampTexels; i++)
        {
            // Sampled at the texel CENTRE, which is what a bilinear fetch of u = i/(n-1) actually
            // returns, so the row's two ends really are the ramp's two endpoints.
            float along = RampTexels == 1 ? 0f : (float)i / (RampTexels - 1);

            // RGB stays black: the shadow's colour is the material's, and Map/Transparent multiplies
            // texel by _Color, so anything else here would tint the shadow.
            ramp.SetPixel(
                i, 0, new Color(0f, 0f, 0f, VectorLightMath.PawnShadowFade(along, frontLoad)));
        }

        ramp.Apply();

        RampTextureCache = ramp;
        RampTip = key;

        // EVERY CACHED MATERIAL IS NOW STALE, because a material binds a texture OBJECT and this has
        // just made a new one. Without this line the first arm to build a material pins its ramp for
        // the rest of the session: a later arm rebuilds the row, every recomputed number agrees the
        // ramp changed, and the screen goes on sampling the old texture. That failure is close to
        // invisible — the probes move, the frames do not — and it cost a live run here before the
        // probe was changed to read the bound texture rather than a freshly derived value.
        //
        // Clearing rather than re-binding in place because the bucket set is a handful of entries
        // rebuilt on the next draw, and a rebuild is cheaper to reason about than a mutation.
        FeatheredMaterialCache.Clear();

        return ramp;
    }

    // The alpha of the ramp row THE DRAW IS ACTUALLY BOUND TO, at `along` of the way to the tip.
    //
    // For the probe, and deliberately read off a live material's own texture rather than recomputed
    // from the formula — or even off the current cached row. This is the number the screen samples,
    // so a scenario pinning it pins a fact about the frame. A probe that re-derived it agreed with
    // the formula while the screen sampled a stale row, which is exactly the disagreement that hid
    // the material-cache bug above: the arm's numbers all moved and its pixels did not.
    //
    // TAKES A POSITION because the tip alone stopped being enough to identify the curve once the
    // curve started ending at zero. A scenario pinning only the endpoint would pass against any
    // shape that happens to vanish, including a broken one; pinning the endpoint AND the midpoint
    // says the row reaching the GPU has both the right ends and the right bend.
    //
    // Falls back to building the row when nothing has drawn yet, so a probe read before the first
    // frame reports the ramp that is about to be used rather than a sentinel.
    public static float BoundRampAlphaAt(float along)
    {
        int texel = Mathf.Clamp(
            Mathf.RoundToInt(along * (RampTexels - 1)), 0, RampTexels - 1);

        foreach (Material material in FeatheredMaterialCache.Values)
        {
            if (material != null && material.mainTexture is Texture2D bound)
                return bound.GetPixel(texel, 0).a;
        }

        Texture2D ramp = RampTexture();

        return ramp == null ? 1f : ramp.GetPixel(texel, 0).a;
    }

    // The caster's own shadow data, read where vanilla reads it — which is two places, not one.
    //
    // PawnRenderer.DrawShadowInternal consults `race.specialShadowData` and the body graphic's
    // `graphicData.shadowData`, and HUMANLIKES ONLY HAVE THE FIRST: Races_Humanlike.xml declares
    // specialShadowData (volume 0.3, 0.8, 0.4 and offset 0, 0, -0.3) and no graphicData.shadowData
    // at all. §27 read only the second, so this returned null for every colonist in the game and
    // they all fell through to a hardcoded 0.6-wide square against a real width of 0.3 — twice the
    // width of the sun shadow standing beside it, and with no offset (issue #159).
    //
    // THE CLAIM THAT ANIMALS WERE UNAFFECTED WAS WRONG, and it survived because it sounded like the
    // reassuring half of #159. Animals do not declare their shadow on the ThingDef's `graphicData`
    // at all — a Cat declares it on the **PawnKindDef's adult life stage** `bodyGraphicData`, volume
    // (0.25, 0.3, 0.25), and `def.graphicData.shadowData` is null. So this returned null for every
    // animal in the game too, and they all fell through to the same human-shaped fallback: a cat was
    // drawn 0.8 cells tall against a real 0.3, and at a 0.3 half-width against a real 0.125. That is
    // a shadow the length of a colonist's and TWICE THE WIDTH of one, under a sprite a third the
    // size. Reported from play as "a cat just walked through with an enormous shadow".
    //
    // The fix reads vanilla's OTHER source rather than a third guess: `PawnRenderer` draws
    // `race.specialShadowData` and then `BodyGraphic?.ShadowGraphic`, and that second graphic is
    // built from `Graphic.data.shadowData` — the resolved body graphic, which is where a life
    // stage's declaration actually ends up. `Graphic.data` is public, so this needs no reflection.
    // The ThingDef's own graphicData stays on the end as a last resort for defs that declare it
    // there. Reading the RESOLVED graphic also means an animal gets the shadow of the life stage it
    // is actually in — a kitten is not a cat — which no def-level read can express.
    // The four live reads behind VectorLightMath.PawnCastsShadow, in one place. Public for the same
    // reason ShadowDataOf is: the probe has to ask the function the renderer asks, or it can report
    // a pawn as suppressed while the screen still draws them.
    //
    // Each read is the one vanilla itself uses, deliberately rather than a near-equivalent:
    // GetPosture() is what PawnRenderer gates on, IsPsychologicallyInvisible() is what sets the
    // PawnRenderFlags.Invisible it gates on alongside, and Swimming /
    // DrawNonHumanlikeSwimmingGraphic / Flying are the three DrawShadowInternal itself branches on.
    // Reaching for something that merely correlates — Downed instead of posture, say — would drift
    // from vanilla the moment Ludeon changed one of them.
    public static bool CastsShadow(Pawn pawn)
    {
        if (pawn?.def == null)
            return false;

        return VectorLightMath.PawnCastsShadow(
            standing:  pawn.GetPosture() == PawnPosture.Standing,
            invisible: pawn.IsPsychologicallyInvisible(),
            swimming:  pawn.Swimming || pawn.DrawNonHumanlikeSwimmingGraphic,
            flying:    pawn.Flying,
            // The fifth clause, and the only one that is not a pawn STATE: a def that declares no
            // shadow anywhere casts none in vanilla either. Asked through ShadowDataOf so the
            // question is answered by the same lookup the draw uses — a separate check here could
            // pass a pawn the draw then had no rectangle for.
            hasShadowData: ShadowDataOf(pawn) != null);
    }

    // Public because the probe asks THIS function rather than re-deriving the answer, which is the
    // repo's probe convention (see EaveCellProbe): a probe that recomputes can agree with a formula
    // the screen is not using, and this is precisely a bug about the screen using a different
    // rectangle from the one anyone expected.
    // How tall the draw treats this caster as being, and how wide its silhouette is from the centre
    // line. Public, and called by Build rather than duplicated in it, because the PROBE reads these
    // — and the bug they exist to pin was precisely a fallback firing where real data existed. A
    // probe with its own copy of the fallback would have agreed with the renderer while both were
    // wrong, which is the failure mode the repo's ask-the-renderer's-function rule is for.
    public static float CasterHeightOf(Pawn pawn) => CasterHeightOf(ShadowDataOf(pawn));

    // The same answer from shadow data already in hand, which is how Build asks it: the draw
    // resolves a pawn's ShadowData once per frame and passes it down rather than re-walking the
    // render tree for each question about it.
    private static float CasterHeightOf(ShadowData shadow) =>
        shadow == null ? VectorLightMath.DefaultPawnHeight : shadow.BaseY;

    public static float CasterHalfWidthOf(Pawn pawn)
    {
        ShadowData shadow = ShadowDataOf(pawn);

        return HalfExtent(shadow?.BaseX, shadow);
    }

    // The shipped default is a HALF extent already while ShadowData's are full widths, which is why
    // one branch halves and the other does not. Kept in one place because getting that asymmetry
    // wrong is invisible until two casters stand side by side.
    private static float HalfExtent(float? baseExtent, ShadowData shadow) =>
        shadow == null || baseExtent == null
            ? VectorLightMath.DefaultPawnShadowHalfExtent
            : baseExtent.Value * 0.5f;

    // Whether the caster stands under a roof, which is what decides that the map-wide sky glow has
    // nothing to say about this pawn — see VectorLightMath.DaylightScale.
    //
    // Asked at the pawn's own cell, the same cell vanilla's Graphic_Shadow asks about before
    // refusing to draw a sun shadow, so the two subsystems cannot disagree about who is indoors.
    // Not asked per shadow cell: a shadow that reaches out through a doorway belongs to a pawn who
    // is still indoors, and splitting one shadow across two lighting regimes would read as a seam.
    private static bool RoofedAt(Map map, Pawn pawn) =>
        map?.roofGrid != null && pawn.Position.InBounds(map) && map.roofGrid.Roofed(pawn.Position);

    public static ShadowData ShadowDataOf(Pawn pawn) =>
        pawn.def?.race?.specialShadowData
        ?? BodyGraphicShadowData(pawn)
        ?? pawn.def?.graphicData?.shadowData;

    // The resolved body graphic's shadow, guarded because it is reachable before the render tree has
    // initialised — a pawn spawned this frame has no BodyGraphic yet, and asking costs a null rather
    // than an exception only if every hop is checked. Falling through to the def-level read (and then
    // to the human-shaped default) for that one frame is the safe direction: it is what shipped for
    // every animal until now.
    private static ShadowData BodyGraphicShadowData(Pawn pawn) =>
        pawn?.Drawer?.renderer?.BodyGraphic?.data?.shadowData;

    // The silhouette extruded along +X, at alpha zero throughout so the shader leaves it where it is
    // put. Direction and the push out to the trailing edge both come from the transform; only the
    // half-width and the LENGTH are baked, which is what makes a couple of dozen cached meshes cover
    // a whole colony.
    //
    // The half-width is bucketed at a THIRTY-SECOND of a cell where the length is bucketed at a
    // quarter, and the asymmetry is the point: these widths are sub-cell (a human's silhouette runs
    // 0.15 to 0.20) so quarter-cell buckets would round every one of them to the same 0.25 and throw
    // away the direction-dependence this is here to express, while a length of 3.1 versus 3.25 cells
    // is invisible. A human sweeps two buckets over a full circuit of the lamp.
    private static Mesh MeshFor(float half, float length, float taper)
    {
        // The taper joins the key because the shape flag can change it mid-session, and a cache that
        // ignored it would hand the new arm the old arm's mesh — an A/B that measures nothing while
        // every flag reads as set.
        long key = ((long)Mathf.RoundToInt(half * 32f) << 32)
            | ((uint)Mathf.RoundToInt(length * 4f) << 1)
            | (uint)(taper >= 1f ? 1 : 0);

        if (MeshCache.TryGetValue(key, out Mesh cached))
            return cached;

        Verts.Clear();
        Colors.Clear();
        Tris.Clear();

        // CONSTANT WIDTH, which reverses an earlier decision in this same file rather than merely
        // differing from it. The taper (a tip at 32% of the base) was added because the first
        // capture, at full width, "read as a plank" — and it did, because the shadows were up to six
        // cells long and nothing that long is shaped like a pawn.
        //
        // The premise went away when the geometry got shorter. Vanilla's own sun shadow is the
        // footprint rectangle plus a skirt of CONSTANT width extruded from the trailing edge
        // (`MeshMakerShadows.NewShadowMesh` duplicates each edge's two vertices and lets the shader
        // push them along _CastVect) — there is no taper anywhere in it. So the blocky shape is not
        // a compromise here, it is the shape the game's other shadows already are, and matching it
        // is what makes a lamp shadow and a sun shadow on the same pawn look like two shadows rather
        // than two effects.
        float tipHalf = half * taper;

        Verts.Add(new Vector3(0f, 0f, -half));
        Verts.Add(new Vector3(0f, 0f, half));
        Verts.Add(new Vector3(length, 0f, tipHalf));
        Verts.Add(new Vector3(length, 0f, -tipHalf));

        // U runs 0 at the trailing edge to 1 at the tip, which is the fraction-along the fade ramp
        // is a function of. V is constant because the ramp has no cross-shadow variation — vanilla's
        // skirt is uniform across its width and only dissolves along its length.
        //
        // Baked unconditionally, including for the flat path: SolidColor ignores UVs, so they cost
        // four Vector2s in a cache that saturates at a couple of dozen meshes and save the mesh cache
        // from needing the feather flag in its key. Length is already in the key, so a shadow that
        // changes length gets a fresh mesh and the ramp restretches with it — which is what makes the
        // fade a fraction of each shadow's own length rather than a fixed distance in cells.
        Uvs.Clear();
        Uvs.Add(new Vector2(0f, 0f));
        Uvs.Add(new Vector2(0f, 1f));
        Uvs.Add(new Vector2(1f, 1f));
        Uvs.Add(new Vector2(1f, 0f));

        // WHITE, WHICH REVERSES THIS LINE'S ORIGINAL VALUE AND IS THE REASON THE FEATHERED PATH
        // DREW NOTHING THE FIRST TIME IT RAN. These were (0,0,0,0), chosen back when the plan was to
        // draw through the game's own shadow material, where vertex alpha IS the extrusion distance
        // and zero means "leave this vertex where it is". Nothing has drawn through that material for
        // a long time; `Map/SolidColor` ignores vertex colour entirely, so the zeros were inert and
        // stayed put looking deliberate.
        //
        // `Map/Transparent` does NOT ignore them — it multiplies the texel by the vertex colour, the
        // way every map-layer mesh in the game relies on (Printer_Plane's DefaultColors are white for
        // exactly this reason). Against a black, alpha-zero vertex colour that product is zero
        // everywhere, so the shadow rendered as a perfect nothing: no error, no warning, every probe
        // green, and a frame identical to the one with the feature switched off.
        //
        // White is inert for the flat path (SolidColor still ignores it) and correct for the
        // feathered one, so the two can go on sharing one cached mesh.
        for (int i = 0; i < 4; i++)
            Colors.Add(new Color32(255, 255, 255, 255));

        Tris.Add(0);
        Tris.Add(1);
        Tris.Add(2);
        Tris.Add(0);
        Tris.Add(2);
        Tris.Add(3);

        Mesh mesh = new Mesh { name = "CelestialLighting_PawnShadow" };
        mesh.SetVertices(Verts);
        mesh.SetColors(Colors);
        mesh.SetUVs(0, Uvs);
        mesh.SetTriangles(Tris, 0);

        MeshCache[key] = mesh;
        return mesh;
    }
}
