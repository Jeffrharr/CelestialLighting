using Verse;

namespace CelestialLighting;

// A per-invocation cache of the handful of values the sky composite's stages ask for over and
// over. It sits IN FRONT OF GeometryMemo rather than replacing it: the memo makes each lookup
// cheap, and this makes most of the lookups not happen.
//
// WHY THIS EXISTS. Counted across one pass of Patch_SkyTargetComposite, the fourteen stages ask
// for SkyBlackedOut seven times, IsEnclosed six, the sun's elevation six, the night floor glow
// three and weather dimming twice — around twenty-four calls for five distinct values, and
// SkyManager.CurrentSkyTarget runs the whole chain twice per frame per map. Memoising already
// took the *cost* of each call down; nothing had taken the *count* down, and a memo hit is not
// free: MapSky.SkyBlackedOut and WeatherDimming.DimmingFor both route through
// FrameStamp.Current(), which reads Time.frameCount, walks Find.TickManager.TicksAbs, builds a
// variant word out of four statics, constructs a GeometryStamp and then does a dictionary lookup
// and a struct compare. Twenty-four of those per pass, twice a frame, to answer five questions.
//
// WHY IT IS SOUND. Every value below is constant across a single composite pass by construction.
// GeometryMemo keys on frame, tick and a settings/warp variant word; none of the three can move
// between the first Apply and the last, because a pass is straight-line code inside one Harmony
// postfix with no tick advance in it. So this cache can never disagree with the memo behind it —
// it can only answer the same question sooner. That is a strictly shorter span than the one
// GeometryMemo already argues for itself, which is why it needs no invalidation hook of its own.
//
// WHY IT IS LAZY RATHER THAN COMPUTED UP FRONT. Filling these eagerly at the top of the pass
// would be a regression on exactly the maps that are cheapest today: every stage gates itself and
// returns early, so an enclosed map or a blacked-out sky currently touches almost nothing, and an
// eager fill would compute a sun elevation and a night floor for it anyway. First touch pays what
// the call costs today; a stage that early-outs before asking still pays nothing.
//
// WHAT IS DELIBERATELY NOT HERE. Vacuum.InVacuumForMap (asked five times) is `map.Biome.inVacuum`
// and MapSky.DrawsShadows (asked twice) is two field reads — a HasValue test and a field read cost
// about the same, so caching them would buy nothing and cost a reader the question of why they are
// special. SiteAltitude's three values are three *different* methods called once each, not a
// repeat. This follows the dividing line DESIGN.md already draws for GeometryMemo itself: the
// walk, not the call count.
//
// MUST BE PASSED BY `ref`. This is a mutable struct, so a copy — assigning it to a local, taking
// it in without `ref`, storing it in a readonly field — still answers every question CORRECTLY,
// but the copy's filled fields are discarded and the cache silently stops caching. That failure
// mode is invisible: no wrong pixel, no failing probe, just the old call count back. The stage
// signatures all take `ref SkyInputs` for this reason.
internal struct SkyInputs
{
    private readonly Map map;

    // Nullable rather than a sentinel-plus-bitmask: every one of these is a value whose whole
    // range is legitimate (a night floor glow of 0 and an elevation of 0 both mean something), so
    // there is no spare value to spend as "not yet asked". Nullable<T> is a struct, so this stays
    // allocation-free — which matters, because the alternative shapes for a per-call scratchpad
    // are a heap object twice a frame per map or a static that is not reentrant.
    private bool? isEnclosed;
    private bool? skyBlackedOut;
    private float? sunElevation;
    private SolarPosition.Inputs? solarInputs;
    private float? nightFloorGlow;
    private float? weatherDimming;

    internal SkyInputs(Map map)
    {
        this.map = map;
        isEnclosed = null;
        skyBlackedOut = null;
        sunElevation = null;
        solarInputs = null;
        nightFloorGlow = null;
        weatherDimming = null;
    }

    internal bool IsEnclosed => isEnclosed ??= MapSky.IsEnclosed(map);

    internal bool SkyBlackedOut => skyBlackedOut ??= MapSky.SkyBlackedOut(map);

    // Elevation and Inputs are cached separately even though ElevationForMap derives from
    // InputsForMap, because the ozone band reads the latitude out of Inputs directly while five
    // other stages want only the elevation. Caching just one of them would leave the other going
    // back through the memo.
    internal SolarPosition.Inputs SolarInputs => solarInputs ??= SolarPosition.InputsForMap(map);

    internal float SunElevation =>
        sunElevation ??= Formulas.SolarElevationDegrees(
            SolarInputs.Latitude, SolarInputs.Declination, SolarInputs.DayPercent);

    // The one entry here that is not memoised at all upstream: NightRadiance.FloorGlowFor reads
    // the settings block and walks MoonSeam.Provider on every call, and three stages want it.
    internal float NightFloorGlow => nightFloorGlow ??= NightRadiance.FloorGlowFor(map);

    internal float WeatherDimming => weatherDimming ??= CelestialLighting.WeatherDimming.DimmingFor(map);
}
