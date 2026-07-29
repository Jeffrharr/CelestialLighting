using System;
using System.Collections.Generic;

namespace CelestialLighting;

// Pure core for the per-frame celestial-geometry memo (issue #12). No UnityEngine/Verse dependency,
// so it compiles into the offline test project and its hit/miss/invalidation behaviour is unit
// tested directly rather than inferred from in-game symptoms.
//
// WHY THIS EXISTS. Solar and lunar geometry is recomputed roughly fourteen times per map per frame:
// Game.UpdatePlay calls MapUpdate() on *every* loaded map, SkyManager.CurrentSkyTarget() evaluates
// WeatherWorker.CurSkyTarget twice (current weather and last weather, then lerps), and we hang nine
// postfixes off that method — five of which need the sun. Add the two GenCelestial.CurShadowStrength
// calls per SkyManagerUpdate, and a single tick's worth of unchanging geometry gets rebuilt over and
// over. Each rebuild is not just
// arithmetic: it goes through Find.WorldGrid.LongLatOf -> PlanetLayer.GetTileCenter, a
// managed->native [BurstCompile] transition, before any trigonometry runs.
//
// The answer is a memo, not a redesign of the call graph: SolarPosition.InputsForMap and
// MoonPosition.SkyForMap are the only two entry points that do this work (every other consumer
// already routes through them — a discipline both file headers spell out), so caching exactly those
// two collapses ~14 evaluations per map per frame to 2.
public readonly struct GeometryStamp : IEquatable<GeometryStamp>
{
    // Unity's frame counter. This is the coarse key: it is what guarantees a cached value can live
    // for at most one frame, so anything feeding the geometry that we did NOT think to name below
    // self-heals on the next frame instead of sticking forever (which a tick-only key would do while
    // the game is paused, since then the tick never advances).
    public readonly int Frame;

    // The absolute tick. Needed as well as Frame because the two are not locked together: at 3x/4x
    // speed the TickManager runs several ticks inside one frame, and while paused it runs none. Both
    // memoized functions are defined in terms of the tick, so keying on the frame alone would freeze
    // a value across a tick boundary for any caller that runs on the tick path rather than the render
    // path.
    public readonly int Tick;

    // Packed bits of every *mode* input that is neither the map nor the tick — see FrameStamp.Variant
    // for the layout. These exist so that flipping one of the dev-only warp flags can never be served
    // a stale value: the harness writes SunClockAdapter.WarpEnabled / MoonPosition.WarpMoonClock
    // mid-run, and although it executes exactly one scenario step per frame (so a probe always reads
    // on a later frame than the SetFeature that moved the flag), relying on that scheduling detail
    // would make our correctness a property of the harness's internals. Keying on the flag values
    // themselves costs one int compare and removes the question.
    public readonly int Variant;

    // One continuous non-tick input, carried the same way and for the same reason as Variant. Only
    // the moon uses it (its synodic cycle position, which the harness's eclipse staging shifts
    // mid-run by writing GameComponent_MoonPhase.debugSynodicShiftTicks); the sun passes 0.
    public readonly float Scalar;

    public GeometryStamp(int frame, int tick, int variant, float scalar)
    {
        Frame = frame;
        Tick = tick;
        Variant = variant;
        Scalar = scalar;
    }

    // Returns this stamp with the moon's extra inputs folded in. Derived from the solar stamp rather
    // than built independently because MoonPosition.SkyForMap calls SolarPosition.InputsForMap: the
    // moon's answer is only valid while the sun's is, so its key must be a strict superset.
    public GeometryStamp WithScalar(float scalar) => new GeometryStamp(Frame, Tick, Variant, scalar);

    // Scalar is compared with float.Equals, not ==, deliberately: Equals treats NaN as equal to NaN.
    // A NaN cycle position should degrade to "cached like any other value", not to a permanent miss
    // that quietly reinstates the full per-frame cost this file exists to remove.
    public bool Equals(GeometryStamp other) =>
        Frame == other.Frame && Tick == other.Tick && Variant == other.Variant && Scalar.Equals(other.Scalar);

    // The nullable pragma is scoped to this one line because this file is compiled twice under
    // different settings: the shipped csproj is nullable-oblivious (writing `object?` there would
    // warn CS8632), while the test csproj enables nullable (leaving it `object` warns CS8765 for not
    // matching object.Equals's annotation). Enabling for the signature and restoring immediately
    // satisfies both without imposing a nullable context on the rest of the file.
#nullable enable
    public override bool Equals(object? obj) => obj is GeometryStamp other && Equals(other);
#nullable restore

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = Frame;
            hash = (hash * 397) ^ Tick;
            hash = (hash * 397) ^ Variant;
            hash = (hash * 397) ^ Scalar.GetHashCode();
            return hash;
        }
    }
}

// A one-entry-per-map memo. Each map keeps only its most recent (stamp, value) pair — there is no
// eviction policy to tune because a stamp is never revisited: frames and ticks only move forward, so
// a superseded entry can never be asked for again.
public sealed class GeometryMemo<TValue>
{
    private readonly struct Entry
    {
        public readonly GeometryStamp Stamp;
        public readonly TValue Value;

        public Entry(GeometryStamp stamp, TValue value)
        {
            Stamp = stamp;
            Value = value;
        }
    }

    // Keyed by Map.uniqueID rather than by Map so a removed map's entry cannot keep the Map object
    // alive. Growth is bounded by the number of maps a session ever creates (tens at the very most,
    // one small struct each), which is why there is no teardown hook.
    //
    // Not thread-safe, and deliberately so: every caller is on the main thread (SkyManagerUpdate,
    // WeatherWorker.CurSkyTarget postfixes, SectionLayer DrawLayer, and the harness's Root_Play.Update
    // postfix), and paying for locking on a hot render path to defend against a caller that does not
    // exist would undo the saving.
    private readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();

    // Instrumentation, kept because it is what the unit tests assert on: "did this actually cache"
    // is otherwise invisible from the outside, since a correct memo is by construction indistinguishable
    // from no memo at all.
    public int Hits { get; private set; }

    public int Misses { get; private set; }

    // arg/compute are passed separately rather than as a closure so callers can hold one static
    // readonly delegate and allocate nothing per call — the whole point is to be cheaper than the
    // work being avoided.
    public TValue Get<TArg>(int mapId, GeometryStamp stamp, TArg arg, Func<TArg, TValue> compute)
    {
        if (entries.TryGetValue(mapId, out Entry entry) && entry.Stamp.Equals(stamp))
        {
            Hits++;
            return entry.Value;
        }

        Misses++;

        // Computed before the store, so a throwing compute leaves no entry behind: a half-built value
        // cached under a valid stamp would be served for the rest of the frame.
        TValue value = compute(arg);
        entries[mapId] = new Entry(stamp, value);
        return value;
    }

    public void Clear()
    {
        entries.Clear();
        Hits = 0;
        Misses = 0;
    }
}
