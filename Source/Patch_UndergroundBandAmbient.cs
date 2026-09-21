using UnityEngine;
using Verse;

namespace CelestialLighting;

// §17c adapter — the thin half over UndergroundBandMath, which carries the full "why", including the
// measured reason cover alone cannot do this and why the light has to arrive in RGB.
//
// Runs only from AsAboveSoBelowCompat, i.e. only on a map As above, So below II has banded and only
// on the layer they draw. There is no vanilla-overlay counterpart on purpose: a map with no bands has
// nothing below the surface to light differently, and §17b already handles a map that is a cave all
// the way through.
//
// ORDERING, AND IT IS LOAD-BEARING. This runs LAST — after §7b's occlusion, whose alpha it overwrites
// for underground cells, and after §27's vector lighting, whose RGB it adds underneath. The ambient is
// a constant offset, so running it last means every difference §27 drew between neighbouring cells
// survives untouched; running it first would leave §27 free to overwrite the offset on any vertex it
// rewrites, and the cave would go dark again exactly where a lamp reaches.
public static class Patch_UndergroundBandAmbient
{
    // Returns whether it wrote, on the same contract as the other two mesh passes: a caller holding
    // the mesh uses it to decide whether the colour array needs uploading again.
    internal static bool ApplyToMesh(Map map, LayerSubMesh subMesh, CellRect rect)
    {
        if (!CelestialLightingFeatures.UndergroundBandEnclosure)
            return false;

        // Gated on indoor sky occlusion for the same reason §17b is: the minimum-indoor-brightness
        // slider is where this pass gets its level, and with that feature off the slider is not the
        // thing deciding indoor brightness any more, so honouring it here would be inventing a level
        // rather than carrying one through.
        if (!CelestialLightingFeatures.IndoorSkyOcclusion)
            return false;

        Mesh mesh = subMesh?.mesh;

        if (mesh == null)
            return false;

        Color32[] colors = mesh.colors32;
        int firstCentreInd = (rect.Width + 1) * (rect.Height + 1);

        if (colors == null || colors.Length != firstCentreInd + rect.Width * rect.Height)
            return false;

        // Resolved once per section rather than per cell: it reads a settings object, and a section
        // is 17x17 so this would otherwise be ~289 identical lookups per bake.
        byte ambient = UndergroundBandMath.AmbientLevel(
            IndoorOcclusionSettings.Current.MinIndoorBrightness);

        // ONE BAND QUESTION PER SECTION, ASKED AT ITS CENTRE. A section is 17 cells tall and their
        // bands are 192, so a section lies wholly within one band except where it straddles a
        // boundary — and the cells a straddling section would get wrong are the gutter rows between
        // bands, which are solid rock nobody sees into. Asking per cell would put a reflective call
        // on every vertex of every bake for a distinction no player can observe.
        IntVec3 centre = new IntVec3(rect.minX + rect.Width / 2, 0, rect.minZ + rect.Height / 2);

        if (!AsAboveSoBelowCompat.IsBelowSurface(map, centre))
            return false;

        WriteCentres(rect, colors, firstCentreInd, ambient);
        WriteCorners(firstCentreInd, colors, ambient);

        mesh.colors32 = colors;
        return true;
    }

    // The cell-centre vertices, which are what a cell's middle renders at.
    private static void WriteCentres(CellRect rect, Color32[] colors, int firstCentreInd, byte ambient)
    {
        int count = rect.Width * rect.Height;

        for (int i = 0; i < count; i++)
        {
            int vertex = firstCentreInd + i;
            colors[vertex] = Add(colors[vertex], ambient);
        }
    }

    // The corner lattice, given the same offset so the bilinear interpolation between corners and
    // centres stays even. Leaving the corners at their occluded value would draw a dark grid over the
    // cave — the corners are shared between four cells and vanilla interpolates across them, so a
    // centre-only offset reads as a lattice of bright dots rather than an evenly lit floor.
    private static void WriteCorners(int firstCentreInd, Color32[] colors, byte ambient)
    {
        for (int i = 0; i < firstCentreInd; i++)
            colors[i] = Add(colors[i], ambient);
    }

    private static Color32 Add(Color32 c, byte ambient) =>
        new Color32(
            UndergroundBandMath.AddAmbient(c.r, ambient),
            UndergroundBandMath.AddAmbient(c.g, ambient),
            UndergroundBandMath.AddAmbient(c.b, ambient),
            UndergroundBandMath.FullCoverAlpha);
}
