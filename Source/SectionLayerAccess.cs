using HarmonyLib;
using Verse;

namespace CelestialLighting;

// Verse.SectionLayer.section is a protected field with no public accessor. Patch_ShadowMeshPerimeter
// needs the owning Section (for its Map and CellRect) from inside a SectionLayer_Dynamic-typed
// Harmony patch parameter, so the reflection lookup lives here rather than inline in the patch.
// Kept as its own file even though there is only one caller left (Patch_ShadowTilt was the other,
// deleted when section 3's tilt moved into the mesh): the next patch that needs a Section from a
// SectionLayer should reuse this rather than reinvent the FieldRef.
public static class SectionLayerAccess
{
    private static readonly AccessTools.FieldRef<SectionLayer, Section> SectionField =
        AccessTools.FieldRefAccess<SectionLayer, Section>("section");

    public static Section GetSection(SectionLayer layer) => SectionField(layer);
}
