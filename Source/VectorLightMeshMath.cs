using System;
using System.Runtime.InteropServices;

namespace CelestialLighting;

// The byte-level shape of one vector-light mesh, as RimWorld 1.7's engine stores it — computed here,
// from the bake's own arrays, so the upload can hand the engine its finished storage in one write.
//
// WHY THIS EXISTS. On 1.7 every Mesh.SetVertices / SetUVs / SetTriangles call re-encodes the WHOLE
// mesh: UnityStubs keeps managed copies of each channel, and every setter rebuilds a fresh native
// CpuMeshData from all of them and swaps it in. Clear, three setters and the bounds pass that
// SetTriangles runs made four full rebuilds per emitter per geometry change. The GPU side was never
// the problem — MeshUploader already coalesces those into one buffer upload per mesh per frame — the
// cost was the CPU re-encoding, ~24 ms per rebuild on the stress colony. Mesh.MeshData writes the
// storage once and swaps it in once (WaylithMeshUpload).
//
// LAYOUT IS THE ONE THE SETTERS PRODUCED, NOT A TIGHTER ONE. UnityStubs' rebuild widens every UV
// channel to four floats regardless of what was handed in, so the shipped mesh was position(3) +
// uv0(4) with z = w = 0. A two-float uv0 would be smaller, but it is a different vertex layout, and
// that is a different pipeline-cache key and a different input the shader's TEXCOORD0 is read from.
// Identical layout is what lets the change claim an identical frame rather than argue for one.
//
// NO BOUNDS CHECKS DOWNSTREAM, which is why the sizes live in tested code. The stub NativeArray's
// indexer writes through a raw pointer: a stream one float too long for its buffer is not an
// exception, it is a silent write into whatever native allocation sits next to it.
public static class VectorLightMeshMath
{
    public const int PositionFloats = 3;
    public const int UvFloats = 4;
    public const int StrideFloats = PositionFloats + UvFloats;

    // With the field UVs (UV1) as well: the layout SetUVs(1) produced on top of the one above, a
    // second four-float channel after uv0. Only the shader-max draw writes it — see
    // WaylithMeshUpload.WriteWithFieldUvs.
    public const int StrideWithFieldUvsFloats = StrideFloats + UvFloats;

    public static int Stride(bool fieldUvs) => fieldUvs ? StrideWithFieldUvsFloats : StrideFloats;

    // Unity's (and UnityStubs') rule: 16-bit indices until a vertex index no longer fits. Our
    // indices are always below the vertex count, so the count alone decides it.
    public const int MaxUInt16Vertices = 65536;

    public readonly struct Box
    {
        public readonly float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

        public Box(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
        {
            MinX = minX; MinY = minY; MinZ = minZ;
            MaxX = maxX; MaxY = maxY; MaxZ = maxZ;
        }
    }

    public static bool FitsUInt16(int vertexCount) => vertexCount <= MaxUInt16Vertices;

    // One interleaved vertex stream: x, altitude, z, u, v, 0, 0 per vertex — the same floats, in the
    // same slots, that SetVertices(Vector3(x, altitude, z)) + SetUVs(0, Vector2(u, v)) encoded.
    // Exactly count * stride long, so NativeArray.CopyFrom's length check is the only guard the
    // write needs.
    //
    // fieldUvs is empty for the plain layout, or exactly count * UvFloats — four floats per vertex,
    // copied through unchanged, because the caller already computes them (AppendFieldUvs) for the
    // batch and one statement of that mapping is enough. Any other length throws: a short array here
    // would otherwise leave trailing vertices sampling texel (0, 0), which renders as a plausible
    // wrong shadow rather than an error.
    public static float[] Interleave(
        float[] x, float[] z, float[] u, float[] v, int count, float altitude,
        ReadOnlySpan<float> fieldUvs = default)
    {
        bool withFieldUvs = !fieldUvs.IsEmpty;

        if (withFieldUvs && fieldUvs.Length != count * UvFloats)
            throw new ArgumentException(
                $"field UVs hold {fieldUvs.Length} floats for {count} vertices, need {count * UvFloats}");

        int stride = Stride(withFieldUvs);
        float[] stream = new float[count * stride];

        for (int i = 0; i < count; i++)
        {
            int at = i * stride;
            stream[at] = x[i];
            stream[at + 1] = altitude;
            stream[at + 2] = z[i];
            stream[at + 3] = u[i];
            stream[at + 4] = v[i];

            if (withFieldUvs)
                fieldUvs.Slice(i * UvFloats, UvFloats).CopyTo(stream.AsSpan(at + StrideFloats));
        }

        return stream;
    }

    // The index buffer in 16-bit form. Only valid when FitsUInt16 held for the mesh's vertex count;
    // the cast truncates rather than throwing, which is the reason that check is the caller's gate.
    public static ushort[] NarrowIndices(int[] triangles)
    {
        ushort[] narrow = new ushort[triangles.Length];

        for (int i = 0; i < triangles.Length; i++)
            narrow[i] = (ushort)triangles[i];

        return narrow;
    }

    // Min/max over every vertex, the same encapsulation UnityStubs ran on SetVertices. Every vertex
    // sits at one altitude, so y is a degenerate interval rather than a scan.
    public static Box Bounds(float[] x, float[] z, int count, float altitude)
    {
        float minX = x[0], maxX = x[0], minZ = z[0], maxZ = z[0];

        for (int i = 1; i < count; i++)
        {
            minX = Math.Min(minX, x[i]);
            maxX = Math.Max(maxX, x[i]);
            minZ = Math.Min(minZ, z[i]);
            maxZ = Math.Max(maxZ, z[i]);
        }

        return new Box(minX, altitude, minZ, maxX, altitude, maxZ);
    }

    // ---- Position + colour + uv0: the pawn-shadow batch's layout ----------------------------------
    //
    // What SetVertices + SetColors(Color32) + SetUVs(0, Vector2) encoded: position as three floats,
    // colour as four UNorm8 bytes, uv0 widened to four floats. Waylith packs attributes back to back
    // in declaration order with no alignment padding (VertexLayout), so the offsets below are the
    // engine's, not a choice.
    public const int ColorBytes = 4;
    public const int ColorOffsetBytes = PositionFloats * sizeof(float);
    public const int ColorUvOffsetBytes = ColorOffsetBytes + ColorBytes;
    public const int PositionColorUvStrideBytes = ColorUvOffsetBytes + UvFloats * sizeof(float);

    // xyz: three floats per vertex; rgba: four bytes per vertex; uv: two floats per vertex. Every
    // length is checked against xyz's vertex count, for the reason Interleave checks fieldUvs.
    public static byte[] InterleavePositionColorUv(
        ReadOnlySpan<float> xyz, ReadOnlySpan<byte> rgba, ReadOnlySpan<float> uv)
    {
        int count = xyz.Length / PositionFloats;

        if (xyz.Length != count * PositionFloats || rgba.Length != count * ColorBytes || uv.Length != count * 2)
            throw new ArgumentException(
                $"channel lengths {xyz.Length}/{rgba.Length}/{uv.Length} do not describe one vertex count");

        byte[] stream = new byte[count * PositionColorUvStrideBytes];
        Span<byte> bytes = stream;

        for (int i = 0; i < count; i++)
        {
            Span<byte> vertex = bytes.Slice(i * PositionColorUvStrideBytes, PositionColorUvStrideBytes);
            Span<float> position = MemoryMarshal.Cast<byte, float>(vertex.Slice(0, ColorOffsetBytes));
            Span<float> uv0 = MemoryMarshal.Cast<byte, float>(vertex.Slice(ColorUvOffsetBytes));

            xyz.Slice(i * PositionFloats, PositionFloats).CopyTo(position);
            rgba.Slice(i * ColorBytes, ColorBytes).CopyTo(vertex.Slice(ColorOffsetBytes));
            uv.Slice(i * 2, 2).CopyTo(uv0);
        }

        return stream;
    }

    // Min/max over three-float positions, for meshes whose vertices do not share one altitude.
    public static Box Bounds(ReadOnlySpan<float> xyz)
    {
        float minX = xyz[0], minY = xyz[1], minZ = xyz[2];
        float maxX = minX, maxY = minY, maxZ = minZ;

        for (int at = PositionFloats; at < xyz.Length; at += PositionFloats)
        {
            minX = Math.Min(minX, xyz[at]);
            maxX = Math.Max(maxX, xyz[at]);
            minY = Math.Min(minY, xyz[at + 1]);
            maxY = Math.Max(maxY, xyz[at + 1]);
            minZ = Math.Min(minZ, xyz[at + 2]);
            maxZ = Math.Max(maxZ, xyz[at + 2]);
        }

        return new Box(minX, minY, minZ, maxX, maxY, maxZ);
    }
}
