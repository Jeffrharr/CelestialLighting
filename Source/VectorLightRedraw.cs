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
    // Seeded with the SHIPPED defaults, so the first ApplyToRuntime of a session is correctly a
    // no-op rather than a spurious whole-map rebake on every load.
    private static bool lastEnabled = CelestialLightingFeatures.VectorLights;
    private static float lastReach = VectorLightSettings.Reach;

    // TWO INPUTS, because both are baked. The checkbox decides whether the mask bakes at all, and
    // the reach multiplier decides what radius every polygon on the map is cast to — so a reach
    // change that dirtied nothing would leave the whole colony drawing the previous setting's
    // shapes, which is this class's failure mode in its worst form: the shapes are all still
    // plausible, they are simply the previous setting's.
    //
    // THE TWO TAKE DIFFERENT PATHS, and the reason is cadence rather than how much each changes.
    // Both genuinely invalidate every polygon on the map. But ApplyToRuntime runs every frame the
    // settings window is open, so a checkbox is crossed once per click while a SLIDER is crossed on
    // ~60 consecutive frames as the mouse is dragged — and every one of those crossings is a real
    // change that detection cannot collapse. So the per-value path has to be the cheap one, or the
    // drag stutters for as long as it lasts.
    public static void SyncTo(bool enabled, float reach)
    {
        bool switchMoved = enabled != lastEnabled;
        bool reachMoved = reach != lastReach;

        if (!switchMoved && !reachMoved)
            return;

        lastEnabled = enabled;
        lastReach = reach;

        // The switch wins when both moved: it is the more thorough of the two and subsumes the
        // other, so doing both would rebake the map twice to reach the same state.
        if (switchMoved)
        {
            ForceRebuild();
            return;
        }

        RebuildForReach();
    }

    // The slider's path: re-derive every emitter's radius and rebake, WITHOUT dropping the meshes and
    // per-emitter glow textures that are about to be rebuilt anyway.
    //
    // WHY NOT ForceRebuild, which would also work. ClearAll destroys every mesh and texture on the
    // map. That is right for the master switch — an off run must hold no GPU memory and must not be
    // able to draw a stale polygon into an A/B baseline — and wrong for a slider, because a reach
    // change does not invalidate an emitter's mesh EXISTING, only its geometry, and Upsert already
    // marks exactly that when the drawn radius differs. Destroying the lot would be work done only
    // to arrive back where we started, once per frame of a drag.
    //
    // Public because the harness's own reach override calls it: a scenario has to exercise the path
    // a player's settings screen exercises, or the arms measure a route that never ships.
    public static void RebuildForReach()
    {
        // What makes the next resync run Upsert over every emitter — which is where the reach
        // multiplier is read, and where a changed drawn radius sets GeometryDirty and PolygonDirty.
        // Everything after that is the ordinary invalidation the field already knows how to do.
        VectorLightField.MarkAllRostersDirty();
        RebuildSections();
    }

    // Unconditional and heavy: drops every mesh and texture first. For the master switch, and for
    // callers that bypass the settings object entirely — which is what the harness's SetFeature step
    // does for the master flag.
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

        RebuildSections();
    }

    // Build every polygon, then dirty every section — the half both paths share.
    //
    // POLYGONS BEFORE THE DIRT, and the order is the whole point. §27 phase 3 bakes into the lighting
    // overlay during a section regenerate and skips any emitter whose polygon is not ready — building
    // one inside the bake charged 43 ms of geometry construction to a whole-map rebake. The draw path
    // builds them and re-dirties, but that lands a frame later, and a change followed immediately by
    // a screenshot photographs the frame in between: the mask measured pixel-identical to vanilla
    // with every probe healthy.
    //
    // WHOLE MAP AS THE BUILD WINDOW, i.e. no view cull, even though the draw path culls. Its whole
    // job is to leave the field in a state the next frame can be photographed from; deferring builds
    // here would hand the harness a first frame that is still catching up, which is the failure this
    // was written to prevent in the first place. Issue #188 item B.
    private static void RebuildSections()
    {
        // Find.Maps is empty during startup and on the main menu, which also keeps the
        // MapMeshFlagDefOf lookup from running before defs are loaded.
        List<Map> maps = Find.Maps;

        if (maps == null)
            return;

        // GroundGlow rather than Roofs: it is the narrower of the two flags the lighting overlay
        // subscribes to, and unlike Roofs it is not also consumed by half the vanilla layers.
        for (int i = 0; i < maps.Count; i++)
        {
            if (VectorLightMask.Active)
                VectorLightField.EnsurePolygons(
                    maps[i], SectionDirtyMath.WholeMap(maps[i].Size.x, maps[i].Size.z));

            maps[i].mapDrawer?.WholeMapChanged((ulong)MapMeshFlagDefOf.GroundGlow);
        }
    }
}
