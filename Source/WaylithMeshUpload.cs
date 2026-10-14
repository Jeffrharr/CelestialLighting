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
