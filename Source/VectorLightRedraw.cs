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

        // Phase 5's lift telemetry counts one rebake, not everything since load. Cleared here rather
        // than per frame because sections regenerate lazily — only the dirty ones, only as they are
        // drawn — so a per-frame counter reads zero on every frame that happened not to rebake and
        // the probe would report a working feature as dead.
        VectorLightMask.ResetTelemetry();

        List<Map> maps = Find.Maps;

        if (maps == null)
            return;

        // GroundGlow rather than Roofs: it is the narrower of the two flags the lighting overlay
        // subscribes to, and unlike Roofs it is not also consumed by half the vanilla layers.
        for (int i = 0; i < maps.Count; i++)
        {
            // POLYGONS BEFORE THE DIRT, and the order is the whole point. §27 phase 3 bakes into the
            // lighting overlay during a section regenerate and skips any emitter whose polygon is not
            // ready — building one inside the bake charged 43 ms of geometry construction to a
            // whole-map rebake. The draw path builds them and re-dirties, but that lands a frame
            // later, and a toggle followed immediately by a screenshot photographs the frame in
            // between: the mask measured pixel-identical to vanilla with every probe healthy.
            //
            // Building here costs the same work on the same cadence — a flag flip is rare — and it
            // is synchronous, so the sections this method is about to dirty bake against polygons
            // that already exist rather than against ones that are about to.
            if (VectorLightMask.Active)
                VectorLightField.EnsurePolygons(maps[i]);

            maps[i].mapDrawer?.WholeMapChanged((ulong)MapMeshFlagDefOf.GroundGlow);
        }
    }
}
