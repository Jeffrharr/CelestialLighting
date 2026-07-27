using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// The live-state half of section 3's shadow tilt: it owns the shadow AXIS that the per-section
// length variation is currently baked against, and it decides when that bake has gone stale.
//
// WHY THIS EXISTS AT ALL. The variation used to ride a per-draw MaterialPropertyBlock in
// Patch_ShadowTilt, which made it free to keep current (it was recomputed every frame) but cost
// every visible shadow section its place in Unity's draw batching, and depended on a
// MaterialPropertyBlock being able to override a $Globals uniform the shader never declared in its
// Properties block — an engine behaviour nobody had confirmed, so the whole effect might have been
// inert (issue #11). It was also, measured, the most expensive thing in this mod: the first live
// profiler capture (issue #23) put that Prefix at 0.300 ms/frame average and 1.590 ms max, 7.73% of
// the frame and two thirds of the whole mod's CPU cost, at ~34 visible sections — and that figure
// excludes the render-thread batching penalty entirely. Baking the variation into the mesh's vertex
// alpha instead is definitionally consumed by the shader, restores batching, and deletes the Prefix
// on SectionLayer_SunShadows.DrawLayer outright. What it buys in exchange is this file: a baked
// value has to be invalidated.
//
// THE INVALIDATION, and why it is not "dirty every frame". A naive rebake-per-frame would be far
// worse than what it replaces: 29.4 us per section regenerate (measured post-bake, DESIGN.md section
// 16) across every on-screen section, 60 times a second, is ~60 ms/s at 34 sections against the old
// design's 18 ms/s. Instead the axis is compared against the one the meshes were baked with, and the
// meshes are rebuilt only once it has drifted past Formulas.ShadowAxisRebakeDegrees — half an alpha
// level's worth of drift, derived in full where that constant is defined. At the shipped 0.5 degrees
// this fires roughly 720 times a game day: 1.00 ms per rebake at 34 sections, ~0.7/s at normal speed
// and ~2.2/s at 3x, so 0.012-0.037 ms/frame amortised against the old 0.300 ms EVERY frame. Roughly
// 8-25x cheaper on CPU before the recovered batching is counted at all.
//
// WHY EVERY SECTION READS ONE STORED AXIS rather than the live one. Sections regenerate
// independently and for unrelated reasons — a wall goes up, a lamp toggles (see section 16 on
// GlowGrid.DirtyCell). If each read the live shadow vector at its own regenerate time, a map would
// accumulate sections baked at different azimuths and the gradient across it would break into
// visible steps. One stored axis per map means every section always agrees, whatever order they
// happen to be rebuilt in.
//
// WHY MapComponentUpdate AND NOT MapComponentTick. GenCelestial.GetLightSourceInfo routes through
// SolarPosition's per-frame memo (GeometryMemo, issue #12), whose key includes both the frame and
// the tick. Calling it from the tick path can land on a different (frame, tick) stamp than the
// render path's call in the same frame, which would double the mod's solar evaluations per frame —
// the exact thing the memo exists to prevent, and something the geometry_eval_count scenario pins.
// From the render path the call is always a memo hit. The tick-interval guard below then makes it
// free while the game is paused, because the sun does not move while the game is paused either.
public class MapComponent_SunShadowAxis : MapComponent
{
    // Compare no more often than this. 15 ticks is a quarter second at normal speed, over which the
    // axis moves ~0.09 degrees — a fifth of the rebake threshold, so the throttle can never be what
    // makes a rebake late. It exists only to keep GetLightSourceInfo (whose vanilla half is not
    // memoized) off the per-frame path. See Formulas.ShadowAxisCheckDue for why the comparison has
    // to tolerate the clock moving backwards.
    private const int CheckIntervalTicks = 15;

    private Vector2 bakedAxis;
    private bool hasBakedAxis;

    // Deliberately far enough from tick 0 that the first Update always checks, without risking the
    // subtraction in ShadowAxisCheckDue overflowing the way int.MinValue would.
    private int lastCheckedTick = -CheckIntervalTicks * 2;

    public MapComponent_SunShadowAxis(Map map)
        : base(map)
    {
    }

    // The length multiplier Patch_ShadowMeshPerimeter bakes into a section's vertex alpha. Falls
    // back to the live axis if the component is somehow missing, so a mesh is never built against
    // no axis at all.
    public static float LengthScaleFor(Map map, CellRect sectionRect)
    {
        MapComponent_SunShadowAxis component = map.GetComponent<MapComponent_SunShadowAxis>();
        Vector2 axis = component == null ? CurrentAxis(map) : component.BakedAxis;
        return LengthScale(map, sectionRect, axis);
    }

    public override void MapComponentUpdate()
    {
        int tick = Find.TickManager.TicksGame;
        if (!Formulas.ShadowAxisCheckDue(tick, lastCheckedTick, CheckIntervalTicks))
            return;

        lastCheckedTick = tick;

        GenCelestial.LightInfo live = GenCelestial.GetLightSourceInfo(map, GenCelestial.LightType.Shadow);

        // No shadow is being drawn (night with no moon up, or the user's strength slider at zero),
        // so nothing can have gone visibly stale. Skipping here is what keeps a moonless night from
        // rebaking every half second for a mesh nobody can see; the first frame a shadow does come
        // back, the axis will be wildly different from the stored one and the rebake happens then.
        if (live.intensity <= 0f)
            return;

        if (!IsStale(live.vector))
            return;

        bakedAxis = live.vector;
        hasBakedAxis = true;

        // Our own flag, so this regenerates SectionLayer_SunShadows and nothing else — see
        // Patch_SunShadowAxisInvalidation and the flag def's own comment. WholeMapChanged only
        // raises dirty flags; Section.TryUpdate then regenerates just the sections the camera can
        // see and defers the rest, so a 275x275 map costs the same here as a 200x200 one.
        map.mapDrawer?.WholeMapChanged((ulong)CelestialLightingDefOf.CL_SunShadowAxis);
    }

    private bool IsStale(Vector2 live)
    {
        Vector2 baked = BakedAxis;
        return Formulas.ShadowAxisDeltaDegrees(baked.x, baked.y, live.x, live.y)
            >= Formulas.ShadowAxisRebakeDegrees;
    }

    // Resolved lazily rather than in the constructor: MapComponents are built during map generation,
    // before there is any meaningful sun to ask about, and the first thing that wants an axis is
    // whichever comes first of the first section regenerate and the first Update.
    private Vector2 BakedAxis
    {
        get
        {
            if (!hasBakedAxis)
            {
                bakedAxis = CurrentAxis(map);
                hasBakedAxis = true;
            }

            return bakedAxis;
        }
    }

    // The single source of truth for "which way do shadows fall", the same call SkyManager pushes
    // into the _CastVect shader global and the same one Patch_ShadowDirection postfixes — so this
    // follows the moon at night and the user's Look sliders for free, and can never disagree with
    // what is actually on screen.
    private static Vector2 CurrentAxis(Map map) =>
        GenCelestial.GetLightSourceInfo(map, GenCelestial.LightType.Shadow).vector;

    // Thin adapter around the pure core: turn a section's offset from the map center into the
    // normalized (<= 1, so it always fits in a byte) length multiplier for its whole submesh.
    private static float LengthScale(Map map, CellRect sectionRect, Vector2 axis)
    {
        Vector3 mapCenter = new Vector3(map.Size.x / 2f, 0f, map.Size.z / 2f);
        Vector3 sectionCenter = sectionRect.CenterVector3;

        float positionFraction = Formulas.ShadowLengthPositionFraction(
            offsetX: sectionCenter.x - mapCenter.x,
            offsetZ: sectionCenter.z - mapCenter.z,
            shadowDirX: axis.x,
            shadowDirZ: axis.y,
            mapSizeX: map.Size.x,
            mapSizeZ: map.Size.z);

        return Formulas.NormalizedShadowLengthScale(positionFraction, Formulas.ShadowLengthVariation);
    }
}
