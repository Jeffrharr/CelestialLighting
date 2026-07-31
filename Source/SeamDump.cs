using Verse;

namespace CelestialLighting;

// TEMPORARY DIAGNOSTIC for docs/EAVE-SEAM.md — deleted once the eave seam is understood.
//
// Every inference from rendered pixels has been exhausted: the emitters, their heights and their
// counts are provably identical between a roofline that seams and one that does not, and the band
// still renders shorter. This prints what EmitCell actually decides, per cell, for one small window,
// so the mesh can be read rather than deduced.
public static class SeamDump
{
    // The window to dump, in map coordinates. Set to the inset strip in
    // Tests/Scenarios/eave_seam_inset_strip.json and its immediate surroundings.
    // Two windows: the bare strip (clean) and the walled inset strip (seams), from
    // Tests/Scenarios/eave_seam_inset_strip.json, so the two meshes can be diffed cell for cell.
    private const int MinZ = 166;
    private const int MaxZ = 174;

    public static bool Enabled;

    public static bool Covers(int x, int z) =>
        Enabled && z >= MinZ && z <= MaxZ
        && ((x >= 83 && x <= 98) || (x >= 147 && x <= 162));

    // Dumps the finished submesh: every vertex whose z sits near a roofline in the window, with the
    // alpha the shader will displace it by. This is the geometry itself rather than the decisions
    // that produced it — the decisions have already been shown identical between a clean roofline
    // and a seaming one.
    public static void Mesh(string tag, System.Collections.Generic.List<UnityEngine.Vector3> verts,
        System.Collections.Generic.List<UnityEngine.Color32> colors)
    {
        if (!Enabled)
            return;

        for (int i = 0; i < verts.Count; i++)
        {
            UnityEngine.Vector3 v = verts[i];
            if (!Covers((int)v.x, (int)v.z) || v.z < 169f || v.z > 172f)
                continue;

            Log.Message($"SEAMVERT {tag} #{i} pos=({v.x:F2},{v.z:F2}) a={colors[i].a}");
        }
    }

    public static void Cell(int i, int j, float height, EaveShadowGrid casters, Map map)
    {
        bool west = i > 0 && casters.At(i - 1, j) < height;
        bool east = i < map.Size.x - 1 && casters.At(i + 1, j) < height;
        bool south = j > 0 && casters.At(i, j - 1) < height;
        bool north = j < map.Size.z - 1 && casters.At(i, j + 1) < height;

        Log.Message(
            $"SEAMDUMP ({i},{j}) h={height:F2} n={casters.At(i, j + 1):F2} s={casters.At(i, j - 1):F2} "
            + $"w={casters.At(i - 1, j):F2} e={casters.At(i + 1, j):F2} "
            + $"skirts[W{(west ? 1 : 0)} E{(east ? 1 : 0)} S{(south ? 1 : 0)} N{(north ? 1 : 0)}] "
            + $"alpha={Formulas.ShadowCasterAlphaByte(height)}");
    }
}
