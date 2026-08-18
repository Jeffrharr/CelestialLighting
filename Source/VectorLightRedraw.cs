using System.Collections.Generic;
using RimWorld;
using Verse;

namespace CelestialLighting;

// §27's toggle plumbing, on the same shape as NightDesaturationRedraw / EaveShadowRedraw /
// IndoorOcclusionRedraw — deliberately the same rather than a fourth variation.
//
// It exists because half of §27 is BAKED. Patch_VectorLightSuppress rewrites the lighting overlay's
// vertex colours during a section regenerate, which only happens when something dirties that section.
// So flipping the feature changes what should be on screen without changing anything that would
// provoke a rebake, and the map keeps rendering the previous answer until the player happens to build
// something. For the live harness that is worse than cosmetic: an A/B whose "after" frame is still
// showing the "before" bake measures nothing and looks like the feature having no effect.
public static class VectorLightRedraw
{
    // Seeded with the SHIPPED default, so the first ApplyToRuntime of a session is correctly a no-op
    // rather than a spurious whole-map rebake on every load.
    private static bool lastEnabled = CelestialLightingFeatures.VectorLights;

    public static void SyncTo(bool enabled)
    {
        if (enabled == lastEnabled)
            return;

        lastEnabled = enabled;
        ForceRebuild();
    }

    // Unconditional, for callers that bypass the settings object entirely — which is what the
    // harness's SetFeature step does.
    public static void ForceRebuild()
    {
        // Meshes are dropped whichever way the toggle moved. Turning off, so an off run holds no GPU
        // memory and cannot draw a stale polygon into the baseline; turning on, so the first frame
        // bakes against the map as it is now rather than as it was whenever the feature last ran.
        VectorLightField.ClearAll();

        List<Map> maps = Find.Maps;

        if (maps == null)
            return;

        // GroundGlow rather than Roofs: it is the narrower of the two flags the lighting overlay
        // subscribes to, and unlike Roofs it is not also consumed by half the vanilla layers.
        for (int i = 0; i < maps.Count; i++)
            maps[i].mapDrawer?.WholeMapChanged((ulong)MapMeshFlagDefOf.GroundGlow);
    }
}
