using System;

namespace CelestialLighting.Tests;

// A grid of light-blocking cells, turned into the silhouette segments a bake is handed.
//
// SHARED BETWEEN FIXTURES ON PURPOSE, rather than each one growing its own. The visibility polygon
// and the coverage grid are two stages of the same bake and both are asserted bit-for-bit against an
// oracle, so a scene that catches something in one is a scene worth having in the other — and two
// private copies drifting apart would quietly leave one stage tested over easier geometry than the
// other. Extracted from VectorLightBuildCullTests, which is where it was first written.
internal sealed class VectorLightLayout
{
    public const int Size = 41;

    private readonly bool[] blocked = new bool[Size * Size];

    public void Wall(int x0, int z0, int x1, int z1)
    {
        for (int z = z0; z <= z1; z++)
        {
            for (int x = x0; x <= x1; x++)
                Set(x, z);
        }
    }

    public void Pillars(int pitch)
    {
        for (int z = 0; z < Size; z += pitch)
        {
            for (int x = 0; x < Size; x += pitch)
                Set(x, z);
        }
    }

    public void Clear(int x, int z)
    {
        if (Inside(x, z))
            blocked[z * Size + x] = false;
    }

    public VectorLightMath.Segment[] Segments() =>
        VectorLightMath.SilhouetteSegments(blocked, Size, Size, 0, 0);

    private void Set(int x, int z)
    {
        if (Inside(x, z))
            blocked[z * Size + x] = true;
    }

    private static bool Inside(int x, int z) => x >= 0 && x < Size && z >= 0 && z < Size;

    // ---- the scenes both fixtures sweep ----------------------------------------------------

    public static VectorLightMath.Segment[] Grid(Action<VectorLightLayout> build)
    {
        VectorLightLayout layout = new VectorLightLayout();
        build(layout);
        return layout.Segments();
    }

    public static void RoomBlock(VectorLightLayout layout)
    {
        for (int i = 0; i * 7 < Size; i++)
        {
            layout.Wall(0, i * 7, Size - 1, i * 7);
            layout.Wall(i * 7, 0, i * 7, Size - 1);
        }

        // Doorways, so the light reaches past its own room and the window fills with wall from the
        // next one. A sealed grid would leave the polygon bounded by four segments however dense it
        // looks.
        for (int i = 0; i * 7 < Size; i++)
        {
            for (int j = 0; j * 7 < Size; j++)
                layout.Clear(i * 7, j * 7 + 3);
        }
    }
}
