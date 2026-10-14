using System.Runtime.InteropServices;

namespace CelestialLighting.Tests;

/// <summary>
/// The one-write mesh layout for RimWorld 1.7 (<see cref="VectorLightMeshMath"/>). Every size here is
/// load-bearing: the stub NativeArray the stream is copied into writes through a raw pointer, so a
/// wrong length is native memory corruption rather than an exception.
/// </summary>
[TestFixture]
public class VectorLightMeshMathTests
{
    private static readonly float[] X = { 1f, -2f, 3.5f };
    private static readonly float[] Z = { 4f, 5f, -6f };
    private static readonly float[] U = { 0f, 0.5f, 1f };
    private static readonly float[] V = { 0f, 0.25f, 0.75f };

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(3)]
    public void Interleave_IsExactlyOneStridePerVertex(int count)
    {
        float[] stream = VectorLightMeshMath.Interleave(X, Z, U, V, count, altitude: 7f);

        Assert.That(stream.Length, Is.EqualTo(count * VectorLightMeshMath.StrideFloats));
    }

    [Test]
    public void Interleave_MatchesTheSetterLayout()
    {
        // What SetVertices(Vector3(x, altitude, z)) + SetUVs(0, Vector2(u, v)) encoded on 1.7: a
        // four-float uv0 with z = w = 0. Vertex 1 checked in full so a slot swap cannot hide.
        float[] stream = VectorLightMeshMath.Interleave(X, Z, U, V, 3, altitude: 7f);
        int at = VectorLightMeshMath.StrideFloats;

        Assert.That(stream[at..(at + VectorLightMeshMath.StrideFloats)],
            Is.EqualTo(new[] { -2f, 7f, 5f, 0.5f, 0.25f, 0f, 0f }));
    }

    [Test]
    public void Interleave_StopsAtCountNotArrayLength()
    {
        // The bake's arrays can be longer than the mesh; vertices past VertexCount are not geometry.
        float[] stream = VectorLightMeshMath.Interleave(X, Z, U, V, 2, altitude: 0f);

        Assert.That(stream, Does.Not.Contain(3.5f));
    }

    [Test]
    public void Interleave_WithFieldUvs_AppendsUv1AfterUv0()
    {
        // The layout SetUVs(1) added on top: a second four-float channel, copied through unchanged.
        float[] fieldUvs = { 0.1f, 0.2f, 0f, 0f, 0.3f, 0.4f, 1f, 0f };

        float[] stream = VectorLightMeshMath.Interleave(X, Z, U, V, 2, altitude: 7f, fieldUvs);
        int at = VectorLightMeshMath.StrideWithFieldUvsFloats;

        Assert.That(stream.Length, Is.EqualTo(2 * VectorLightMeshMath.StrideWithFieldUvsFloats));
        Assert.That(stream[at..(at + VectorLightMeshMath.StrideWithFieldUvsFloats)],
            Is.EqualTo(new[] { -2f, 7f, 5f, 0.5f, 0.25f, 0f, 0f, 0.3f, 0.4f, 1f, 0f }));
    }

    [TestCase(7)]
    [TestCase(9)]
    public void Interleave_RefusesFieldUvsOfTheWrongLength(int floats)
    {
        // Short would leave the last vertices sampling texel (0, 0): a wrong shadow, not an error.
        Assert.Throws<ArgumentException>(() =>
            VectorLightMeshMath.Interleave(X, Z, U, V, 2, altitude: 0f, new float[floats]));
    }

    [TestCase(1, true)]
    [TestCase(65535, true)]
    [TestCase(65536, true)]
    [TestCase(65537, false)]
    public void FitsUInt16_AtTheBoundary(int vertexCount, bool fits)
    {
        // 65536 vertices means a highest index of 65535, which still fits in 16 bits.
        Assert.That(VectorLightMeshMath.FitsUInt16(vertexCount), Is.EqualTo(fits));
    }

    [Test]
    public void NarrowIndices_KeepsEveryIndexAndItsOrder()
    {
        int[] triangles = { 0, 1, 2, 2, 1, 65535 };

        ushort[] narrow = VectorLightMeshMath.NarrowIndices(triangles);

        Assert.That(narrow, Is.EqualTo(new ushort[] { 0, 1, 2, 2, 1, 65535 }));
    }

    [Test]
    public void Bounds_EncloseEveryVertexAtTheAltitude()
    {
        VectorLightMeshMath.Box box = VectorLightMeshMath.Bounds(X, Z, 3, altitude: 7f);

        Assert.That(new[] { box.MinX, box.MinY, box.MinZ, box.MaxX, box.MaxY, box.MaxZ },
            Is.EqualTo(new[] { -2f, 7f, -6f, 3.5f, 7f, 5f }));
    }

    [Test]
    public void Bounds_OfOneVertexIsThatPoint()
    {
        VectorLightMeshMath.Box box = VectorLightMeshMath.Bounds(X, Z, 1, altitude: 0f);

        Assert.That(new[] { box.MinX, box.MaxX, box.MinZ, box.MaxZ }, Is.EqualTo(new[] { 1f, 1f, 4f, 4f }));
    }

    [Test]
    public void Bounds_IgnoreVerticesPastCount()
    {
        VectorLightMeshMath.Box box = VectorLightMeshMath.Bounds(X, Z, 2, altitude: 0f);

        Assert.That(box.MaxX, Is.EqualTo(1f));
        Assert.That(box.MinZ, Is.EqualTo(4f));
    }

    [Test]
    public void PositionColorUv_StrideIsThirtyTwoBytes()
    {
        // 12 position + 4 colour + 16 uv0, back to back: Waylith's VertexLayout adds no padding.
        Assert.That(VectorLightMeshMath.PositionColorUvStrideBytes, Is.EqualTo(32));
    }

    [Test]
    public void InterleavePositionColorUv_PutsEachChannelAtItsOffset()
    {
        float[] xyz = { 0f, 0f, 0f, 1f, 2f, 3f };
        byte[] rgba = { 0, 0, 0, 0, 10, 20, 30, 40 };
        float[] uv = { 0f, 0f, 0.5f, 0.75f };

        byte[] stream = VectorLightMeshMath.InterleavePositionColorUv(xyz, rgba, uv);
        Span<byte> second = stream.AsSpan(VectorLightMeshMath.PositionColorUvStrideBytes);

        Assert.That(stream.Length, Is.EqualTo(2 * VectorLightMeshMath.PositionColorUvStrideBytes));
        Assert.That(MemoryMarshal.Cast<byte, float>(second[..12]).ToArray(), Is.EqualTo(new[] { 1f, 2f, 3f }));
        Assert.That(second[12..16].ToArray(), Is.EqualTo(new byte[] { 10, 20, 30, 40 }));
        Assert.That(MemoryMarshal.Cast<byte, float>(second[16..32]).ToArray(),
            Is.EqualTo(new[] { 0.5f, 0.75f, 0f, 0f }));
    }

    [TestCase(6, 4, 4)]
    [TestCase(6, 8, 2)]
    [TestCase(5, 8, 4)]
    public void InterleavePositionColorUv_RefusesMismatchedChannels(int xyz, int rgba, int uv)
    {
        Assert.Throws<ArgumentException>(() =>
            VectorLightMeshMath.InterleavePositionColorUv(new float[xyz], new byte[rgba], new float[uv]));
    }

    [Test]
    public void BoundsOfPositions_EncloseEveryAxis()
    {
        float[] xyz = { 1f, 5f, -1f, -3f, 2f, 4f, 0f, 9f, 0f };

        VectorLightMeshMath.Box box = VectorLightMeshMath.Bounds(xyz);

        Assert.That(new[] { box.MinX, box.MinY, box.MinZ, box.MaxX, box.MaxY, box.MaxZ },
            Is.EqualTo(new[] { -3f, 2f, -1f, 1f, 9f, 4f }));
    }
}
