using RimWorld;
using Verse;

namespace CelestialLighting;

// Thin adapter for §21: pulls the two primitives AlbedoCavityMath needs off live Map state — how
// much weather buildup is lying on the ground, and how much cloud deck is overhead — and answers
// one question: how much brighter is this map than the bare-ground map the mod's brightness anchors
// were calibrated on.
//
// SHARED READ, same discipline as NightRadiance.FloorGlowFor and WeatherDimming.CloudOpacityFor.
// Only §7's night floor consumes it today, but the cavity is a property of the MAP rather than of
// the night — §21's per-cell shadow-fill half (DESIGN.md §21) is expected to want the same number —
// so it is a function anyone can call rather than a value one patch leaves behind for another to
// find.
//
// WHY MEAN DEPTH AND NOT A PER-CELL READ. `map.snowGrid.TotalDepth` is a maintained running total,
// not a per-call grid scan: decompiling Verse.SnowGrid against 1.6's Assembly-CSharp shows a
// `private double totalDepth` incremented inside AddDepth and SetDepth, exposed as
// `public float TotalDepth => (float)totalDepth`. So this read is O(1) regardless of map size, and
// dividing by Map.Area (Size.x * Size.z) gives a mean depth already normalized by
// SnowGrid.MaxDepth = 1f. `SnowGrid.GetDepth(cell)` exists and would let the shadow-fill half go
// per-cell later; DESIGN.md §21 records why that is deliberately deferred rather than merely
// unwritten (§16's section-invalidation cost ledger, issues #20 and #60). `Map.sandGrid` shares the
// same shape (Verse.SandGrid.TotalDepth), so the sand read below costs the same one field access.
//
// A whole-map average is also the RIGHT model for the term it feeds, not merely the cheap one. The
// cavity is a multi-bounce integral over everything the cloud base can see, which at cloud-base
// height is most of the map — so the ambient lift over a half-thawed map genuinely is the areal
// mean, and a cell standing on bare mud in the middle of a snowfield is genuinely lifted by its
// neighbours. Averaging over Map.Area rather than over snow-capable cells only is part of that:
// roofed cells, water and building footprints hold no snow (or sand) and correctly dilute the mean.
public static class SurfaceBuildup
{
    // How much the surface-cloud cavity amplifies diffuse light on this map, as a multiplier on the
    // mod's existing bare-ground brightness. Exactly 1 — a true no-op — when the feature is off,
    // when there is no map or no snow grid yet (map generation), when nothing is lying on the
    // ground, and on a vacuum map.
    public static float CavityGainFor(Map map) =>
        CavityGainFor(map, CloudOpacityOrClear(map));

    // Is there ANY weather buildup on this map — the cheapest possible "can the cavity do anything
    // here" question, for callers that run every frame and want to leave before doing real work.
    //
    // Two field reads and no arithmetic: both grids keep TotalDepth as a maintained running total (see
    // this file's header), so this is O(1) regardless of map size. False is a complete answer, not an
    // approximation — with no buildup both ramps sit at BareGroundAlbedo, CavityGain is exactly 1, and
    // every consumer's no-op path follows. §24's draw hook is the caller this exists for.
    public static bool HasBuildup(Map map)
    {
        if (map == null)
            return false;

        return (map.snowGrid?.TotalDepth ?? 0f) > 0f || (map.sandGrid?.TotalDepth ?? 0f) > 0f;
    }

    // Overload for callers that have ALREADY read §13's cloud opacity, which the daytime consumer
    // has: WeatherDimming.DimmingFor computes the opacity, derives the dimming from it, and then
    // needs the gain for the same opacity. Threading it through rather than letting this re-read it
    // is not micro-optimisation — CloudOpacityFor walks MapSky's biome weather-commonality list and
    // the GameConditionManager chain, both of which MapSky's header records as deliberately NOT
    // memoized, and CurSkyTarget is called twice per map per frame by SkyManager.
    public static float CavityGainFor(Map map, float cloudOpacity)
    {
        // Feature gate (default on): when off, every consumer sees a gain of exactly 1 and the mod
        // renders precisely what it did before §21 — the faithful pre-feature baseline the harness
        // A/B screenshots against. Sits first so "off" costs nothing.
        if (!CelestialLightingFeatures.SnowAlbedo)
            return 1f;

        if (map == null)
            return 1f;

        // Read once and pass down, per Vacuum.cs's convention — the pure function owns the vacuum
        // branch, this adapter owns the single live read. Deliberately NOT an early return of its
        // own: a second place that knows what vacuum means is a second place that can disagree with
        // AlbedoCavityMath about it.
        bool inVacuum = Vacuum.InVacuumForMap(map);

        float snowSurfaceAlbedo = AlbedoCavityMath.BuildupSurfaceAlbedo(
            MeanSnowDepth(map),
            AlbedoCavityMath.BareGroundAlbedo,
            AlbedoCavityMath.SettledSnowAlbedo,
            AlbedoCavityMath.FreshSnowAlbedo);

        // Odyssey's desert sibling. Sand has no "settling" story the way snow does — RimWorld never
        // reports a sandstorm ageing a dune the way it ages a snowpack — so there is no middle albedo
        // to give BuildupSurfaceAlbedo's second segment: both the bare and shallow arguments are
        // BareGroundAlbedo, which flattens the 0->ShallowBuildupDepth segment to a no-op and leaves a
        // single ramp from bare ground straight to SandAlbedo across ShallowBuildupDepth->1. Pinned
        // by BuildupSurfaceAlbedo_TakesItsAlbedosAsArguments_SoSandNeedsNoSecondRamp.
        float sandSurfaceAlbedo = AlbedoCavityMath.BuildupSurfaceAlbedo(
            MeanSandDepth(map),
            AlbedoCavityMath.BareGroundAlbedo,
            AlbedoCavityMath.BareGroundAlbedo,
            AlbedoCavityMath.SandAlbedo);

        float surfaceAlbedo = AlbedoCavityMath.CombinedSurfaceAlbedo(snowSurfaceAlbedo, sandSurfaceAlbedo);

        // §13's classifier, unduplicated: the same blended opacity that darkens the sky and softens
        // the shadows is the a_cloud that closes the cavity for every weather except Clear, where
        // §22's continuous fraction takes over (see CloudOpacityOrClear below and issue #100). That
        // is the seam the whole subsystem hangs on — WeatherCloudDeck's per-def override reaches
        // this term for free, so a mod author who declares "my heat shimmer is not a cloud deck" is
        // telling §21 as well as §13, from one place. The skyless-map cases (caves, pocket maps,
        // orbit) come along too: CloudOpacityFor returns 0 for them via MapSky.HasSky, which leaves
        // a clear-sky backscatter and a gain of ~1.07 at worst — and their buildup depth is 0
        // anyway, so the surface term pins the gain at exactly 1 regardless.
        //
        // ONE COUPLING WORTH STATING, because it is a user-visible consequence rather than an
        // implementation detail: CloudOpacityFor is gated on CelestialLightingFeatures.WeatherDimming.
        // Turning §13 off therefore also opens the cavity here on every NON-Clear map — it reads as
        // a clear sky and snow buys ~7% instead of ~2.3x. That is the honest answer rather than a
        // bug: with §13 off the mod has no model of a cloud deck at all, and inventing a second one
        // for §21 alone would be exactly the "two sources of truth for how overcast it is" that
        // sharing the classifier exists to prevent. §22's own gate (CelestialLightingFeatures
        // .CloudCover) is independent of §13's, so a Clear map keeps reading its cloud-cover fraction
        // regardless of whether §13 is on — see CloudOpacityOrClear below.
        float cloudAlbedo = AlbedoCavityMath.CloudBaseAlbedo(cloudOpacity);

        return AlbedoCavityMath.CavityGain(
            surfaceAlbedo, AlbedoCavityMath.BareGroundAlbedo, cloudAlbedo, inVacuum);
    }

    // §13's blended cloud opacity for every weather except Clear; §22's continuous cloud-cover
    // fraction for Clear (issue #100). Before §22 existed, §13 scored Clear as exactly opacity 0 —
    // "no opinion", not "no cloud" — so on a partly-cloudy Clear day the cavity was reading a
    // literal clear sky (ClearSkyAlbedo, ~1.07x at most) even while §22 was already rendering that
    // same sky as up to a third overcast. §22's own hourly-drifting fraction is the more specific
    // answer for exactly this one weather, so the cavity should read it instead of falling through
    // to §13's abstention.
    //
    // Gated on the map's actual current weather being Clear, not on `weatherOpacity == 0`, for the
    // same reason AlbedoCavityMath.EffectiveCloudOpacity's header gives: §13 can also read 0 because
    // it is turned off or because the map has no sky at all, and neither of those is "this map is
    // in Clear weather right now" — conflating them would leak §22's cloud drift into caves and
    // pocket maps that have no business seeing a tile's weather at all.
    //
    // FEATURE-OFF FALLBACK: CloudCoverClock.FractionForMap returns exactly 0 when
    // CelestialLightingFeatures.CloudCover is off (its own gate, checked once for both of its
    // callers — see that file's header), which is the same 0 §13 already reports for Clear. So with
    // §22 off this reproduces the discrete pre-#100 behaviour bit-for-bit: a Clear sky reads as a
    // literal clear sky, exactly as it did before this file changed.
    private static float CloudOpacityOrClear(Map map)
    {
        if (map == null)
            return 0f;

        float weatherOpacity = WeatherDimming.CloudOpacityFor(map);
        bool weatherIsClear = map.weatherManager?.curWeather == WeatherDefOf.Clear;

        // Only read CloudCoverClock when it can matter — it is a cheap per-tile dictionary lookup,
        // but there is no reason to touch it (or seed its cache) for a map that is not in Clear
        // weather right now.
        float cloudCoverFraction = weatherIsClear ? CloudCoverClock.FractionForMap(map) : 0f;

        return AlbedoCavityMath.EffectiveCloudOpacity(weatherOpacity, weatherIsClear, cloudCoverFraction);
    }

    // Mean snow-buildup depth across the whole map, in [0,1]. 0 before the grid exists (a map still
    // being generated asks for sky targets) or if Area is somehow zero, which is the same answer as
    // "bare ground" and so degrades to a no-op rather than to a divide.
    private static float MeanSnowDepth(Map map)
    {
        SnowGrid grid = map.snowGrid;
        if (grid == null)
            return 0f;

        int area = map.Area;
        if (area <= 0)
            return 0f;

        return grid.TotalDepth / area;
    }

    // Mean sand-buildup depth across the whole map, in [0,1]. Same shape and the same reasons as
    // MeanSnowDepth above — SandGrid.TotalDepth is the same maintained running total SnowGrid keeps,
    // per ApiCompatibilityTests.Map_HasSandGrid_ShapedLikeSnowGrid, so this read is O(1) regardless
    // of map size and costs nothing extra worth measuring.
    private static float MeanSandDepth(Map map)
    {
        SandGrid grid = map.sandGrid;
        if (grid == null)
            return 0f;

        int area = map.Area;
        if (area <= 0)
            return 0f;

        return grid.TotalDepth / area;
    }
}
