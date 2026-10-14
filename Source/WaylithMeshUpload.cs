using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace CelestialLighting;

// Replaces one vector-light mesh's geometry in a single storage swap on RimWorld 1.7, the mesh half
// of the upload WaylithFieldUpload does for the field textures.
//
// WHY THE ORDINARY PATH IS EXPENSIVE ON 1.7. UnityStubs' Mesh keeps a managed copy of every channel
// and, on each SetVertices / SetUVs / SetTriangles, re-encodes ALL of them into a brand-new native
// CpuMeshData and swaps it in (Mesh.RebuildFromCompat). Clear + three setters + the bounds pass
// SetTriangles adds was four full encodes per emitter, every one but the last thrown away.
// VectorLightMeshMath has the numbers.
//
// WHAT THIS DOES INSTEAD. Mesh.MeshData — Unity's own one-shot API, which UnityStubs implements as
// "build a CpuMeshData, then swap it in once" (Mesh.ApplyWritableMeshData). That swap also resets the
// stub's managed channel cache, so a later SetUVs(1) (the field UVs DrawLight writes) decodes the
// storage we wrote rather than a stale copy — the property a direct EngineMesh.ReplaceCpuStorage
// would have broken, and the reason this goes through the public API instead.
//
// SAME BYTES. Layout, index width, sub-mesh and bounds are the ones the setters produced; see
// VectorLightMeshMath for the layout and why it is not the tighter one.
internal static class WaylithMeshUpload
{
    private static readonly VertexAttributeDescriptor[] Layout =
    {
        new VertexAttributeDescriptor(
            VertexAttribute.Position, VertexAttributeFormat.Float32, VectorLightMeshMath.PositionFloats),
        new VertexAttributeDescriptor(
            VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, VectorLightMeshMath.UvFloats),
    };

    // With UV1 too: the layout SetUVs(1) produced on top of the plain one — a second four-float
    // channel after uv0, which only the shader-max composition reads.
    private static readonly VertexAttributeDescriptor[] LayoutWithFieldUvs =
    {
        Layout[0],
        Layout[1],
        new VertexAttributeDescriptor(
            VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, VectorLightMeshMath.UvFloats),
    };

    // fieldUvs null writes the plain layout; otherwise exactly one Vector4 per vertex, as
    // AppendFieldUvs builds them.
    internal static void Write(
        Mesh mesh, VectorLightMath.LightMesh built, float altitude, List<Vector4> fieldUvs = null)
    {
        int count = built.VertexCount;
        bool narrow = VectorLightMeshMath.FitsUInt16(count);

        VectorLightMeshMath.Box box = VectorLightMeshMath.Bounds(built.X, built.Z, count, altitude);

        Mesh.MeshDataArray array = Mesh.AllocateWritableMeshData(1);
        Mesh.MeshData data = array[0];

        System.ReadOnlySpan<float> uv1 = fieldUvs == null
            ? default
            : MemoryMarshal.Cast<Vector4, float>(CollectionsMarshal.AsSpan(fieldUvs));

        data.SetVertexBufferParams(count, fieldUvs == null ? Layout : LayoutWithFieldUvs);
        data.GetVertexData<float>().CopyFrom(
            VectorLightMeshMath.Interleave(built.X, built.Z, built.U, built.V, count, altitude, uv1));

        WriteIndicesAndApply(mesh, array, data, built.Triangles, narrow, box);
    }

    private static readonly VertexAttributeDescriptor[] PositionColorUvLayout =
    {
        Layout[0],
        new VertexAttributeDescriptor(
            VertexAttribute.Color, VertexAttributeFormat.UNorm8, VectorLightMeshMath.ColorBytes),
        Layout[1],
    };

    // The pawn-shadow batch's combined mesh, which is rebuilt EVERY FRAME while shadows are drawn —
    // so on the setter path it paid Clear plus four full encodes per frame, not per geometry change.
    // Same one-swap mechanism as Write; the layout is the one SetVertices + SetColors(Color32) +
    // SetUVs(0, Vector2) produced. Not empty: the caller returns before drawing nothing.
    internal static void WritePositionColorUv(
        Mesh mesh, List<Vector3> verts, List<Color32> colors, List<Vector2> uvs, List<int> tris)
    {
        System.ReadOnlySpan<float> xyz = MemoryMarshal.Cast<Vector3, float>(CollectionsMarshal.AsSpan(verts));
        int count = verts.Count;
        bool narrow = VectorLightMeshMath.FitsUInt16(count);

        VectorLightMeshMath.Box box = VectorLightMeshMath.Bounds(xyz);

        Mesh.MeshDataArray array = Mesh.AllocateWritableMeshData(1);
        Mesh.MeshData data = array[0];

        data.SetVertexBufferParams(count, PositionColorUvLayout);
        data.GetVertexData<byte>().CopyFrom(VectorLightMeshMath.InterleavePositionColorUv(
            xyz,
            MemoryMarshal.Cast<Color32, byte>(CollectionsMarshal.AsSpan(colors)),
            MemoryMarshal.Cast<Vector2, float>(CollectionsMarshal.AsSpan(uvs))));

        int[] indices = tris.ToArray();
        WriteIndicesAndApply(mesh, array, data, indices, narrow, box);
    }

    // The batched draw's combined mesh: per-emitter layout with field UVs, from many emitters' lists.
    // Always 32-bit indices, which the batch has asked for since it existed — sixty-four fans can
    // pass the 16-bit ceiling.
    internal static void WriteBatch(
        Mesh mesh, List<Vector3> verts, List<Vector2> uvs, List<Vector4> fieldUvs, List<int> tris)
    {
        System.ReadOnlySpan<float> xyz = MemoryMarshal.Cast<Vector3, float>(CollectionsMarshal.AsSpan(verts));

        Mesh.MeshDataArray array = Mesh.AllocateWritableMeshData(1);
        Mesh.MeshData data = array[0];

        data.SetVertexBufferParams(verts.Count, LayoutWithFieldUvs);
        data.GetVertexData<float>().CopyFrom(VectorLightMeshMath.InterleavePositionUvs(
            xyz,
            MemoryMarshal.Cast<Vector2, float>(CollectionsMarshal.AsSpan(uvs)),
            MemoryMarshal.Cast<Vector4, float>(CollectionsMarshal.AsSpan(fieldUvs))));

        WriteIndicesAndApply(mesh, array, data, tris.ToArray(), narrow: false, VectorLightMeshMath.Bounds(xyz));
    }

    // The open-sky mask's whole-map mesh (cloud sheet and sky overlays): position + uv0, 32-bit
    // indices as it always asked for. Rebuilt only when a roof changes, but each rebuild is the whole
    // map's unroofed runs — thousands of quads — so three full encodes became one.
    internal static void WritePositionUv(Mesh mesh, Vector3[] verts, Vector2[] uvs, int[] tris)
    {
        System.ReadOnlySpan<float> xyz = MemoryMarshal.Cast<Vector3, float>(verts);

        Mesh.MeshDataArray array = Mesh.AllocateWritableMeshData(1);
        Mesh.MeshData data = array[0];

        data.SetVertexBufferParams(verts.Length, Layout);
        data.GetVertexData<float>().CopyFrom(VectorLightMeshMath.InterleavePositionUvs(
            xyz, MemoryMarshal.Cast<Vector2, float>(uvs), default));

        WriteIndicesAndApply(mesh, array, data, tris, narrow: false, VectorLightMeshMath.Bounds(xyz));
    }

    // A section layer's vertex colours rewritten IN PLACE, for layers whose geometry never changes
    // after it is first built and whose colours change on every rebuild (night desaturation).
    //
    // WHY. LayerSubMesh.FinalizeMesh(MeshParts.Colors) calls Mesh.SetColors, and on 1.7 every setter
    // builds a brand-new CpuMeshData from the stub's managed channel cache — positions, colours and
    // triangles all re-encoded — to change four bytes a vertex. Measured on night desaturation, that
    // was 83 of a section rebuild's ~114 us. Here the colours are written straight into the existing
    // vertex stream at the colour attribute's offset, and the engine is told the storage changed,
    // which queues the same GPU upload a setter would have.
    //
    // NARROW ON PURPOSE. Refuses anything but the simple case — the mesh already holds exactly this
    // many vertices, with a four-byte UNorm8 colour attribute — and the caller falls back to
    // FinalizeMesh, which is always correct. A section's first build is always the fallback: its
    // base geometry has no colour channel until a setter has written one.
    //
    // THE STUB'S CACHE GOES STALE, AND THAT IS SAFE FOR THIS CALLER. UnityStubs' Mesh keeps a managed
    // copy of every channel and rebuilds from it on the next setter; after this write that copy still
    // holds the previous colours. A layer that only ever changes its colours never calls another
    // setter on the mesh, and if one did, the very next rebuild writes every colour again.
    internal static bool TryWriteColors(Mesh mesh, List<Color32> colors)
    {
        Waylith.Graphics.Meshes.Mesh engine = mesh.EngineMesh;

        if (engine == null || !engine.IsReadable)
            return false;

        Waylith.Graphics.Meshes.CpuMeshData storage = engine.CpuStorage;

        if (storage.VertexCount != colors.Count
            || !storage.Layout.TryGetAttribute(
                Waylith.Graphics.Meshes.VertexAttributeSemantic.Color,
                out Waylith.Graphics.Meshes.VertexAttribute colour)
            || colour.Format != Waylith.Graphics.Meshes.VertexAttributeFormat.UNorm8
            || colour.Dimension != 4)
        {
            return false;
        }

        int stride = storage.Layout.GetStreamStride(colour.Stream);
        System.Span<byte> stream = storage.GetVertexStream(colour.Stream).AsSpan();
        System.ReadOnlySpan<Color32> source = CollectionsMarshal.AsSpan(colors);

        for (int i = 0; i < source.Length; i++)
            MemoryMarshal.Write(stream.Slice(i * stride + colour.OffsetBytes, 4), in source[i]);

        engine.MarkCpuStorageChanged();
        return true;
    }

    private static void WriteIndicesAndApply(
        Mesh mesh, Mesh.MeshDataArray array, Mesh.MeshData data, int[] indices, bool narrow,
        VectorLightMeshMath.Box box)
    {
        data.SetIndexBufferParams(indices.Length, narrow ? IndexFormat.UInt16 : IndexFormat.UInt32);
        if (narrow)
            data.GetIndexData<ushort>().CopyFrom(VectorLightMeshMath.NarrowIndices(indices));
        else
            data.GetIndexData<int>().CopyFrom(indices);

        Bounds bounds = default;
        bounds.SetMinMax(new Vector3(box.MinX, box.MinY, box.MinZ), new Vector3(box.MaxX, box.MaxY, box.MaxZ));

        data.subMeshCount = 1;
        data.SetSubMesh(0, new SubMeshDescriptor(0, indices.Length) { bounds = bounds });

        Mesh.ApplyAndDisposeWritableMeshData(array, mesh);

        // The mesh-level box straight into the engine type, not through Unity's Bounds: that
        // round-trips min/max through centre/extents and can move them by an ulp. Nothing on these
        // draw paths culls against it on 1.7, but exact is free here.
        mesh.EngineMesh.Bounds = new Waylith.Graphics.Meshes.MeshBounds(
            box.MinX, box.MinY, box.MinZ, box.MaxX, box.MaxY, box.MaxZ);
    }
}
