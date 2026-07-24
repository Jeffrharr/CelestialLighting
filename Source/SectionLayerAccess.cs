using HarmonyLib;
using Verse;

namespace CelestialLighting;

// Verse.SectionLayer.section is a protected field with no public accessor. Both Patch_ShadowTilt
// and Patch_ShadowMeshPerimeter need the owning Section (for its Map and CellRect) from inside a
// SectionLayer_Dynamic-typed Harmony patch parameter, so the one reflection lookup lives here
// instead of being duplicated per patch file.
public static class SectionLayerAccess
{
    private static readonly AccessTools.FieldRef<SectionLayer, Section> SectionField =
        AccessTools.FieldRefAccess<SectionLayer, Section>("section");

    public static Section GetSection(SectionLayer layer) => SectionField(layer);
}
