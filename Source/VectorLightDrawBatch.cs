using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CelestialLighting;

// Vector lighting's emitters drawn several to a call instead of one each.
//
// WHAT THE IDLE FRAME IS MADE OF, which is the only reason this exists. With the polygon work, the
// mask work and the property hold all landed, a frame where nothing moves costs the overlay pass one
// Graphics.DrawMesh per visible emitter and essentially nothing else. That call is per emitter
// because four values are RENDER STATE on a per-emitter MaterialPropertyBlock — the light's colour,
// its strength this frame, how much of vanilla's glow to subtract, and vanilla's own square as a
// texture — and render state is what a draw call IS. There is no way to merge two draws that
// disagree about it.
//
// So the four stop being render state. The three scalars go into uniform arrays the vertex program
// indexes, the texture becomes a slice of a texture array, and the mesh carries which slot each
// emitter occupies in UV1.z — a channel that was already a float4 with z already written as zero.
// One combined mesh per (radius bucket, composition, field size) then draws in one call.
//
// WHY THOSE THREE AND NOT THE MAP. The falloff gradient is a material texture keyed on the radius
// bucket; the blend mode and the render queue are the composition; and every slice of a texture
// array must be the same size, which is the field diameter. Those are exactly the things a single
// draw call has to hold constant, so they are exactly what the batch key is.
//
// WHAT IT REFUSES, and every refusal falls back to the shipped per-emitter draw rather than to
// nothing:
//
//   - the flag off, or the shader, the keyword, texture arrays or slice copying unavailable
//     (VectorLightShader.BatchActive);
//   - the MoteGlow fallback composition, which has no uniform array to read a colour out of;
//   - an emitter with no vanilla field, which composes as a plain additive pass and has no slice to
//     point at. Pointing it at an unwritten slice would work only because its weight is zero, and a
//     correctness that rests on a multiplication by zero is one refactor from a garbage subtraction;
//   - anything past VectorLightShader.BatchCapacity in one key, because a uniform array is a fixed
//     allocation and writing past it is a silently truncated batch, not a bigger one.
//
// THE COST SIDE IS REAL AND IS MEASURED. A combined mesh must be rebuilt whenever its membership or
// any member's geometry moves, and that is more vertex writing than rebuilding one emitter's fan.
// This is a steady-frame win paid for on the frames that move, which is why
// VectorLightField.BatchMeshBuilds sits in the same counter bank as the draw-call saving rather than
// somewhere it could be read without it.
//
// ALL OF IT IS MAIN-THREAD, like everything else that hands Unity an object. The static scratch
// below follows the repo's rule for static scratch: it belongs to the calling thread, and the
// calling thread here is the draw postfix.
internal static class VectorLightDrawBatch
{
    // One key's worth of emitters, and everything cached between frames that describes them.
    private sealed class Batch
    {
        public float Radius;
        public VectorLightOverlay.Composition Composition;
        public int Diameter;

        // This frame's members, refilled from empty by Begin. Parallel to the two uniform arrays
        // below by index — a member's position in this list IS its slot, which is what the mesh
        // writes into UV1.z and what indexes the texture array.
        public readonly List<Member> Members = new List<Member>();

        // What the combined mesh below was last built from. Compared against Members to decide
        // whether this frame may reuse it; see SameMembership for what "the same" means and why it
        // is more than the count.
        public Member[] Built = new Member[0];
        public int BuiltCount;
        public float BuiltAltitude = float.NaN;

        public Mesh Mesh;
        public MaterialPropertyBlock Props;

        // Vanilla's square for every member, one slice each, plus what is actually in each slice
        // right now. The pair is what lets a frame copy only the slices whose emitter's texture
        // changed — which on a steady frame is none of them.
        public Texture2DArray Slices;
        public Texture2D[] SliceSources = new Texture2D[0];
        public int[] SliceVersions = new int[0];

        // Always full length, never a prefix. SetVectorArray fixes the uniform array's size on first
        // use, so handing it a shorter array on a quieter frame would shrink it permanently and
        // truncate every busier frame after — Unity's own documented trap. The tail is never read:
        // no vertex carries a slot the members list did not fill.
        public readonly Vector4[] Colors = new Vector4[VectorLightShader.BatchCapacity];
        public readonly Vector4[] Parameters = new Vector4[VectorLightShader.BatchCapacity];
    }

    // One emitter's place in a batch, and everything about it the combined mesh depends on.
    //
    // THE VERSION AND THE SQUARE ARE BOTH IN HERE because both move the vertices. The mesh version
    // says our own geometry changed; the field origin and diameter say vanilla moved the square
    // those vertices are mapped into, which rewrites UV1 without touching a position. Comparing only
    // the entries would reuse a mesh whose coordinates point at the wrong texels — a shadow sampling
    // its neighbour's glow, which reads as a composition error rather than a stale cache.
    private struct Member
    {
        public VectorLightField.LightEntry Entry;
        public int MeshVersion;
        public int StartX;
        public int StartZ;
        public int Diameter;
    }

    private static readonly Dictionary<(int Bucket, int Composition, int Diameter), Batch> Batches =
        new Dictionary<(int, int, int), Batch>();

    // The batches that actually took members this frame, so Flush walks those rather than every key
    // the session has ever seen. A colony that once had a radius-14 sun lamp keeps its batch object
    // forever — the mesh and the slice array are the expensive things to rebuild — and this is what
    // stops that history costing a frame anything.
    private static readonly List<Batch> Live = new List<Batch>();

    // Scratch for the combined mesh, and the reason it is not VectorLightOverlay's. Those lists are
    // filled and drained inside one emitter's upload; these accumulate across every member of a
    // batch and are read at Flush, long after the last upload returned. Sharing them would work
    // today and break the first time a rebuild happened between two Enqueues.
    private static readonly List<Vector3> Verts = new List<Vector3>();
    private static readonly List<Vector2> Uvs = new List<Vector2>();
    private static readonly List<Vector4> VanillaUvs = new List<Vector4>();
    private static readonly List<int> Tris = new List<int>();

    // Read once per frame in Begin rather than per emitter. Every clause behind BatchActive is a
    // cached verdict or a static bool, so this is a cheap read either way — but asking it once means
    // a frame cannot change its mind halfway through and submit half its emitters one way.
    private static bool active;

    // The altitude every emitter in the frame draws at. One number for the whole frame — it is
    // AltitudeLayer.VisEffects, which does not vary per emitter — so it lives beside `active` rather
    // than on a batch, and a batch's cached mesh records which value it was built with, because a
    // mesh bakes the altitude into every vertex's y.
    private static float frameAltitude;

    public static void Begin(float altitude)
    {
        active = VectorLightShader.BatchActive;
        frameAltitude = altitude;

        for (int i = 0; i < Live.Count; i++)
            Live[i].Members.Clear();

        Live.Clear();
    }

    // Takes this emitter into a batch, or declines it. TRUE MEANS DRAWN — by Flush, later this
    // frame — and false means the caller must draw it the shipped way.
    public static bool Enqueue(
        VectorLightField.LightEntry entry, VectorLightOverlay.Composition composition,
        Color color, float strength, float vanillaWeight, float skyAmbient, bool composed)
    {
        if (!active || !Batchable(entry, composition, composed))
            return false;

        Batch batch = BatchFor(entry, composition);

        // FULL IS A REFUSAL, NOT AN OVERFLOW. See this file's header: the uniform array is a fixed
        // allocation, so the sixty-fifth emitter cannot go in it. It draws itself instead, which
        // costs one call — the same call it would have cost with the feature off.
        if (batch.Members.Count >= VectorLightShader.BatchCapacity)
            return false;

        int slot = batch.Members.Count;

        batch.Members.Add(new Member
        {
            Entry = entry,
            MeshVersion = entry.MeshVersion,
            StartX = entry.FieldStartX,
            StartZ = entry.FieldStartZ,
            Diameter = entry.FieldDiameter,
        });

        // The same four numbers WriteProps would have put on this emitter's own block, in the same
        // order and with the same meanings — see VectorLightOverlay.WriteProps. Colour and strength
        // are one float4 because _Color always was; the two composition scalars share one with the
        // slot because three floats fit where four do.
        batch.Colors[slot] = new Vector4(color.r, color.g, color.b, strength);
        batch.Parameters[slot] = new Vector4(vanillaWeight, skyAmbient, slot, 0f);

        if (slot == 0)
        {
            batch.Radius = entry.Radius;
            batch.Composition = composition;
            batch.Diameter = entry.FieldDiameter;
            Live.Add(batch);
        }

        return true;
    }

    public static void Flush()
    {
        for (int i = 0; i < Live.Count; i++)
            Submit(Live[i]);
    }

    // Drops every cached mesh and slice array, for exactly the reasons VectorLightField.ClearAll
    // drops every emitter's: an off run must hold no GPU memory of the feature, and an on run must
    // not be able to draw something built before the toggle moved. Called from the same place, so
    // the two caches cannot end up disagreeing about which half of a rebuild they are on.
    //
    // KEPT AS AN OBJECT-LEVEL DROP RATHER THAN A DICTIONARY CLEAR ALONE, because a Mesh and a
    // Texture2DArray are Unity objects: dropping the last managed reference to one leaks it until
    // the scene unloads, and the batches are keyed on a bucket a colony can cycle through as lamps
    // are built and deconstructed.
    public static void Discard()
    {
        foreach (Batch batch in Batches.Values)
        {
            if (batch.Mesh != null)
                Object.Destroy(batch.Mesh);

            if (batch.Slices != null)
                Object.Destroy(batch.Slices);
        }

        Batches.Clear();
        Live.Clear();
    }

    // Whether this emitter can be carried by a batch at all, asked about the emitter rather than
    // about the machine — VectorLightShader.BatchActive has already answered for the machine.
    private static bool Batchable(
        VectorLightField.LightEntry entry, VectorLightOverlay.Composition composition, bool composed)
    {
        // The fallback pass draws through MoteGlow, whose program knows nothing about a uniform
        // array. Asked first because it is the cheapest of the three and the one that is constant
        // for a whole frame.
        if (composition == VectorLightOverlay.Composition.Additive)
            return false;

        // `composed` is the caller's own answer to "is there a field to compose against", and it is
        // what decides the vanilla weight — so taking an emitter the caller called uncomposed would
        // put a zero-weight emitter into a slice nobody filled. The two texture tests below are not
        // redundant with it: composed is about this frame's decision, and these are about whether
        // the objects a slice copy needs actually exist.
        if (!composed || entry.VanillaField == null || entry.FieldDiameter <= 0)
            return false;

        return entry.VanillaField.width == entry.FieldDiameter
            && entry.Built.VertexCount > 0;
    }

    private static Batch BatchFor(
        VectorLightField.LightEntry entry, VectorLightOverlay.Composition composition)
    {
        // A TUPLE RATHER THAN THREE FIELDS PACKED INTO AN INT, which is what this was first written
        // as. Packing needs a width for each field, and the diameter is the one whose width is not
        // ours to choose — it is 2*ceil(vanilla's radius)+1, and a mod that raises a lamp's radius
        // raises it with no warning. A field that overflows its bits does not fail, it ALIASES: two
        // differently-sized emitters agree on a key, and the second one to arrive gets copied into a
        // texture array sized for the first. The tuple cannot do that.
        //
        // The diameter is in the key at all because it is only USUALLY implied by the bucket, and
        // "usually" is doing real work there — a bucket spans an eighth of a cell and vanilla's own
        // diameter comes off a CeilToInt, so two radii that round to one bucket can land either side
        // of it. Two different-sized squares in one texture array is not a wrong colour; it is an
        // exception at allocation time.
        var key = (
            VectorLightOverlay.RadiusKey(entry.Radius), (int)composition, entry.FieldDiameter);

        if (!Batches.TryGetValue(key, out Batch batch))
        {
            batch = new Batch();
            Batches[key] = batch;
        }

        return batch;
    }

    private static void Submit(Batch batch)
    {
        int count = batch.Members.Count;

        if (count > VectorLightField.LargestDrawBatch)
            VectorLightField.LargestDrawBatch = count;

        EnsureMesh(batch);

        if (batch.Mesh == null)
            return;

        EnsureSlices(batch);

        batch.Props ??= new MaterialPropertyBlock();

        VectorLightShader.SetBatch(batch.Props, batch.Slices, batch.Colors, batch.Parameters);

        Graphics.DrawMesh(
            batch.Mesh, Vector3.zero, Quaternion.identity,
            VectorLightOverlay.BatchMaterialFor(batch.Radius, batch.Composition),
            0, null, 0, batch.Props);

        VectorLightField.DrawCalls++;
    }

    // The combined mesh, rebuilt only when it has to be.
    //
    // THE THREE CHANNELS COME FROM VectorLightOverlay'S OWN APPENDERS, not from copies of them. A
    // combined mesh that disagreed with the per-emitter mesh about a vertex, a uv or a winding would
    // draw a different frame, and the batched arm's whole claim is that it draws the same one — so
    // the loops are written once over there and called from both places.
    private static void EnsureMesh(Batch batch)
    {
        if (batch.Mesh != null && SameMembership(batch))
            return;

        Verts.Clear();
        Uvs.Clear();
        VanillaUvs.Clear();
        Tris.Clear();

        for (int slot = 0; slot < batch.Members.Count; slot++)
        {
            Member member = batch.Members[slot];
            VectorLightMath.LightMesh built = member.Entry.Built;

            VectorLightOverlay.AppendGeometry(built, frameAltitude, Verts, Uvs, Tris);
            VectorLightOverlay.AppendFieldUvs(
                built, member.StartX, member.StartZ, member.Diameter, slot, VanillaUvs);
        }

        if (Verts.Count == 0)
        {
            batch.Mesh = null;
            return;
        }

        if (batch.Mesh == null)
        {
            batch.Mesh = new Mesh { name = "CelestialLighting_VectorLightBatch" };

            // UInt32 BECAUSE A COMBINED MESH IS NOT ONE FAN. A single emitter never comes near the
            // 65535-vertex ceiling of the default 16-bit index buffer, and sixty-four of them can:
            // the ceiling is per MESH, not per submesh, and overflowing it is an exception in the
            // middle of the draw chain rather than a truncated mesh.
            batch.Mesh.indexFormat = IndexFormat.UInt32;
        }

        batch.Mesh.Clear();
        batch.Mesh.SetVertices(Verts);
        batch.Mesh.SetUVs(0, Uvs);
        batch.Mesh.SetUVs(1, VanillaUvs);
        batch.Mesh.SetTriangles(Tris, 0);

        RecordMembership(batch);
        VectorLightField.BatchMeshBuilds++;
    }

    // Whether this frame's members are the same emitters, in the same order, with the same geometry
    // and the same squares as the ones the cached mesh was built from.
    //
    // ORDER MATTERS AND IS NOT INCIDENTAL. A member's index is its slot, and the slot is baked into
    // the mesh's UV1 and into which slice it samples. Two frames with the same emitters in a
    // different order are a different mesh, so this compares position by position rather than as
    // sets — and the order is the roster's enumeration order, which is stable between frames for an
    // unchanged roster.
    private static bool SameMembership(Batch batch)
    {
        if (batch.BuiltCount != batch.Members.Count || batch.BuiltAltitude != frameAltitude)
            return false;

        for (int i = 0; i < batch.BuiltCount; i++)
        {
            Member was = batch.Built[i];
            Member now = batch.Members[i];

            if (was.Entry != now.Entry
                || was.MeshVersion != now.MeshVersion
                || was.StartX != now.StartX
                || was.StartZ != now.StartZ
                || was.Diameter != now.Diameter)
            {
                return false;
            }
        }

        return true;
    }

    private static void RecordMembership(Batch batch)
    {
        if (batch.Built.Length < batch.Members.Count)
            batch.Built = new Member[batch.Members.Count];

        for (int i = 0; i < batch.Members.Count; i++)
            batch.Built[i] = batch.Members[i];

        batch.BuiltCount = batch.Members.Count;
        batch.BuiltAltitude = frameAltitude;
    }

    // Vanilla's square for every member, stacked into one texture array — and re-copied only for the
    // slices whose emitter's texture actually moved.
    //
    // A GPU-SIDE COPY, NOT A REFILL. Graphics.CopyTexture hands the driver a blit between two
    // textures that already exist; the alternative would be re-running CopyField's per-texel loop
    // into the array, which is the expensive half of a field upload and would be paid again here for
    // no new information. It is also what keeps the per-emitter path completely untouched: the
    // emitter still owns its own Texture2D, filled exactly as it always was, and the batch copies
    // from it.
    private static void EnsureSlices(Batch batch)
    {
        int count = batch.Members.Count;

        EnsureArray(batch, count);

        for (int slot = 0; slot < count; slot++)
        {
            VectorLightField.LightEntry entry = batch.Members[slot].Entry;

            // BOTH HALVES OF THE TEST MATTER. The version catches vanilla's glow moving under a
            // stationary emitter; the source identity catches the emitter in this slot CHANGING,
            // which happens whenever the roster or the cull order does and which a version number
            // cannot see — two different emitters' version counters are unrelated numbers.
            if (batch.SliceSources[slot] == entry.VanillaField
                && batch.SliceVersions[slot] == entry.FieldVersion)
            {
                continue;
            }

            Graphics.CopyTexture(entry.VanillaField, 0, 0, batch.Slices, slot, 0);

            batch.SliceSources[slot] = entry.VanillaField;
            batch.SliceVersions[slot] = entry.FieldVersion;
        }
    }

    private static void EnsureArray(Batch batch, int count)
    {
        if (batch.Slices != null && batch.Slices.depth >= count
            && batch.Slices.width == batch.Diameter)
        {
            return;
        }

        if (batch.Slices != null)
            Object.Destroy(batch.Slices);

        // GROWN IN STEPS OF EIGHT, never shrunk. A colony's emitter count moves by one or two as the
        // camera pans, and an array sized exactly to the frame would be reallocated — and every
        // slice re-copied — on most of those. Eight is small enough that the memory is nothing
        // (a radius-14 square is 33x33x4 bytes, so eight slices is 34 KB) and large enough that a
        // pan does not reallocate.
        int depth = Mathf.Min(
            Mathf.Max(8, (count + 7) / 8 * 8), VectorLightShader.BatchCapacity);

        batch.Slices = new Texture2DArray(
            batch.Diameter, batch.Diameter, depth, TextureFormat.RGBA32, mipChain: false)
        {
            // The same two settings EnsureField puts on the per-emitter texture, for the same two
            // reasons — clamp so a penumbra wedge reaching a hair past the square does not fetch the
            // glow from the opposite side of the lamp, bilinear to match what vanilla's own lighting
            // overlay does with these numbers. A slice keeps its own clamp, which is the whole
            // reason this is an array and not an atlas.
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            name = "CelestialLighting_VectorLightFields",
        };

        // Every slice is now unwritten, whatever was copied into the old array. Sized to the array
        // rather than to the frame so a later, larger frame cannot index past them.
        batch.SliceSources = new Texture2D[depth];
        batch.SliceVersions = new int[depth];
    }
}
