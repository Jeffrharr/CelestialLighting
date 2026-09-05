using System.Collections.Generic;
using Verse;

namespace CelestialLighting;

// The impure half of DESIGN.md §20c, and deliberately nothing more: it reads the two live values the
// pure model needs — the absolute tick and the map's world tile id — memoises the answer on the
// model's own hourly cadence, and hands a primitive back. Every constant, every clamp and every line
// of arithmetic lives in AerosolDrift.cs, which is Verse-free and unit tested offline.
//
// This is the same split as FrameStamp.cs / GeometryMemo.cs, for the same reason: a memo bug and a
// formula bug look identical from inside a running game, so the parts that CAN be tested offline are
// kept where they can be.
//
// WHY NOT A MapComponent. The obvious shape for "a smoothly varying per-map value" is a MapComponent,
// and that is what this mod's original plan sketched for cloud cover. Two things argue against it
// here, and both are specific rather than stylistic:
//
//   1. There is no state to persist. The drift is a pure function of (tileId, TicksAbs), both of
//      which RimWorld already saves. A MapComponent would add a scribe node carrying nothing the
//      save does not already contain.
//   2. That node is permanent. Verse.Map.ExposeComponents writes one <li Class="..."/> per component
//      into every save, and deleting the type later logs two red errors per map on the next load —
//      the exact trap MapComponent_SunShadowAxis.cs is a tombstone for. Taking on an unremovable
//      save-format entry to hold zero state would be paying that price for nothing.
//
// So the state is a static memo instead, shaped like SunClock.cs's per-tile day cache, which is this
// mod's established pattern for "recompute on a slow game-time cadence rather than per frame".
public static class AerosolDriftClock
{
    private readonly struct CachedSample
    {
        public readonly int SampleIndex;
        public readonly float Multiplier;

        public CachedSample(int sampleIndex, float multiplier)
        {
            SampleIndex = sampleIndex;
            Multiplier = multiplier;
        }
    }

    // Keyed by TILE id, not by Map.uniqueID, and that choice is what makes this cache incapable of
    // serving a wrong answer. The cached value is a pure function of exactly (tileId, sampleIndex),
    // so a hit requires both parts of the key to match, which means the inputs match, which means the
    // value is right. There is nothing a new game, a reloaded save or a second colony can do to that.
    //
    // Contrast SunClock.cs, which keys the same way but exposes a Clear() its header says is called
    // on load — nothing calls it, and it is fine for exactly this reason. Recording that here so the
    // absence of a Clear() on this cache reads as a decision rather than as the same oversight.
    //
    // Growth is bounded by the number of distinct tiles a session ever settles or visits — tens at
    // the very most, one eight-byte struct each — which is why there is no teardown hook either.
    //
    // Not thread-safe, deliberately, for the reason GeometryMemo spells out: every caller is on the
    // main thread (the WeatherWorker.CurSkyTarget postfix and the live probe), and locking a hot
    // render path against a caller that does not exist would cost more than it saves.
    private static readonly Dictionary<int, CachedSample> Cache = new Dictionary<int, CachedSample>();

    // What to scale this map's §20b aerosol column by right now, in roughly [0.65, 1.35].
    //
    // PER-FRAME COST. On the overwhelming majority of calls this is a dictionary lookup on an int key
    // plus one int compare — the noise itself runs once per tile per in-game HOUR (2500 ticks), which
    // at normal speed is once every ~42 real seconds. That matters because CurSkyTarget is evaluated
    // twice per SkyManagerUpdate per map and this mod hangs several postfixes off it (DESIGN.md §16
    // on what per-frame work costs in this codebase); an unmemoised two-octave fbm on that path would
    // be a small regression for no visible gain, since the value it recomputes is by construction the
    // one it just returned.
    public static float MultiplierForMap(Map map)
    {
        // Gated here rather than at the call site, mirroring CloudCoverClock.FractionForTick and
        // WeatherDimming.CloudOpacityFor: this is the one place every consumer actually goes through,
        // so gating here is the only way the patch and any future probe cannot drift apart about
        // whether the feature is on.
        //
        // 1 is the pre-feature value, not a disabled sentinel: AerosolDrift.ApplyMultiplier scales the
        // site's static column by this, so 1 leaves the undrifted baseline exactly as it was before
        // the drift existed. Returning 0 here would read as "no aerosol", which is a different sky.
        if (!CelestialLightingFeatures.AerosolDrift)
            return 1f;

        int tileId = map.Tile.tileId;
        int sampleIndex = AerosolDrift.SampleIndex(Find.TickManager.TicksAbs);

        if (Cache.TryGetValue(tileId, out CachedSample cached) && cached.SampleIndex == sampleIndex)
            return cached.Multiplier;

        // The tile id doubles as the noise seed, which is what makes the sequence stable across
        // save/load and reproducible: it is written by worldgen, saved with the world, and never
        // changes for a given tile. Two colonies on one planet get independent weather histories
        // because they sit on different tiles, and the same colony reloaded gets the identical
        // history because it sits on the same one.
        float multiplier = AerosolDrift.Multiplier(sampleIndex, tileId);
        Cache[tileId] = new CachedSample(sampleIndex, multiplier);
        return multiplier;
    }
}
