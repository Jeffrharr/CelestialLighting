using System.Collections.Generic;
using RimWorld;
using Verse;

namespace CelestialLighting;

// §7b mesh staleness fix. SkyFalloffSource.FractionAt (both its NativeSkyFalloffGrid and
// IndoorGlowPassthrough arms) is a function of CurSkyGlow, but Patch_IndoorSkyOcclusion only writes
// its answer when SectionLayer_LightingOverlay.Regenerate runs, and vanilla only reruns that when a
// section is DIRTIED — a roof edit or a glow change from a lamp (see Patch_IndoorSkyOcclusion's own
// header). Time passing is not one of those triggers, so an interior cell's sky-derived brightness
// reads whatever CurSkyGlow was the last time something else happened to dirty its section: a room
// baked at noon stays noon-bright straight through to midnight if nobody ever touches a roof or a lamp
// in it.
//
// WHY A CLOCK, GIVEN THIS REPO'S OWN TOMBSTONE SAYS NOT TO. DESIGN.md's "Removed: the across-map
// length gradient" killed MapComponent_SunShadowAxis for being "the one feature dirtying sections on a
// clock, forever" — sun azimuth sweeps continuously all day, so ANY nonzero drift threshold still fired
// on a bounded schedule for the whole day, every day. CurSkyGlow does not share that shape:
// SunClockMath.GlowFromElevation (and vanilla's own WeatherWorker.CurSkyTarget, which it drives) holds
// glow flat at 0 through the night and flat at 1 through the day, moving only across the two civil-
// twilight ramps a few thousand ticks wide out of a 60000-tick day. Gating on actual drift rather than
// a fixed cadence means this only ever redraws during a dawn or dusk transition that is genuinely
// happening, not "forever" — the exact failure mode the tombstone records.
//
// PER-MAP, NOT WHOLE-SESSION. Two maps can sit at different local times (different longitude) or under
// different weather, so each map tracks its own last-baked glow and only its own meshes get rebuilt
// when it drifts — a redraw on one map's dusk never pays for every other map's unrelated daytime.
public class GameComponent_SkyFalloffRedraw : GameComponent
{
    // Coarse enough that the per-check cost (one skyManager read plus a float compare, per map) is
    // negligible next to a lamp toggle; fine enough that even a short civil-twilight ramp is sampled
    // several times rather than jumped over in one step (matches the ≤0.25 in-game-hour survey
    // granularity this repo already uses to avoid missing narrow effects — 250 ticks is 0.1 h).
    private const int CheckIntervalTicks = 250;

    // Keyed by MAP uniqueID, which is the same key GeometryMemo uses and for the same reason: the
    // value is this map's last-baked glow, so the key has to be the thing that identifies a map.
    //
    // IT USED TO BE THE TILE ID, on the reasoning that a map's tile does not change mid-game and no
    // two maps share one. The second half is false for pocket maps. Every one of them — Anomaly's
    // labyrinth, metal hell and undercave, Odyssey's ancient stockpile, insect lair and space pocket —
    // carries PlanetTile.Invalid, tileId -1, so they all collided on a single entry (see
    // MapWorldTile.cs). Two pocket maps open at once therefore overwrote each other's baseline, and
    // whichever one was checked second compared its own glow against the OTHER map's and skipped the
    // redraw it needed. That reads as an indoor-occlusion mesh that stops tracking dusk, on one map
    // only, which is close to undiagnosable from a bug report. It also directly contradicted this
    // class's own PER-MAP header.
    //
    // What the tile key bought was survival across a reload, and that was never worth anything here:
    // a map's meshes are baked correctly at spawn, so the worst a missing entry does is trigger one
    // redundant redraw at the next check. uniqueID pays that and gets the invariant the value needs.
    // Never cleared for a despawned map, as before: an orphaned float costs a few bytes and is
    // bounded by how many maps existed this process's lifetime, never by anything that grows per tick.
    private static readonly Dictionary<int, float> lastBakedGlow = new Dictionary<int, float>();

    public GameComponent_SkyFalloffRedraw(Game game)
    {
    }

    public override void GameComponentTick()
    {
        if (Find.TickManager.TicksGame % CheckIntervalTicks != 0)
            return;

        // Harness-only escape hatch (CelestialLightingFeatures.SkyFalloffRedrawKey) — off reproduces
        // the pre-fix bug exactly, so a scenario can jump the clock with the flag off and screenshot
        // the stale mesh the fix exists to remove, then flip it on and take the same jump again.
        if (!CelestialLightingFeatures.SkyFalloffRedraw)
            return;

        // Neither source varies with CurSkyGlow when both are off (or the whole subsystem is off) —
        // SkyFalloffSource.FractionAt then returns a flat 0 for every cell regardless of the sky
        // overhead, so there is nothing here that can be stale and this should not even read the maps.
        if (!CelestialLightingFeatures.IndoorSkyOcclusion)
            return;

        if (!AnyBakedTermVariesWithGlow())
            return;

        List<Map> maps = Find.Maps;
        for (int i = 0; i < maps.Count; i++)
            CheckMap(maps[i]);
    }

    // Is there anything in the baked alphas that a change in CurSkyGlow would move? Two independent
    // reasons there might be, and the second is new — the decoupled indoor floor divides
    // MinIndoorBrightness by §7a's keep factor, which is itself a function of the glow, so the cover a
    // sealed room bakes now drifts with the sun even on a map where both sky-falloff sources are off.
    // Missing that would have left the compensation frozen at whatever the sun was doing the last time a
    // lamp was toggled: correct at that hour, and progressively wrong at every other one.
    //
    // The floor is checked as well as the flag because MinIndoorBrightness at 0 makes
    // EffectiveIndoorFloor the constant 0 whatever the sky does — that is the Realistic preset, and it
    // should not pay for a clock it cannot use.
    private static bool AnyBakedTermVariesWithGlow()
    {
        if (CelestialLightingFeatures.NativeSkyFalloff || CelestialLightingFeatures.IndoorGlowPassthrough)
            return true;

        return CelestialLightingFeatures.DecoupledIndoorFloor
            && IndoorOcclusionSettings.Current.MinIndoorBrightness > 0f;
    }

    private static void CheckMap(Map map)
    {
        int mapId = map.uniqueID;
        float curGlow = map.skyManager.CurSkyGlow;

        bool hasBaseline = lastBakedGlow.TryGetValue(mapId, out float bakedGlow);
        if (hasBaseline && !SkyFalloffRedrawMath.ShouldRedraw(bakedGlow, curGlow, SkyFalloffRedrawMath.DefaultThreshold))
            return;

        lastBakedGlow[mapId] = curGlow;
        IndoorOcclusionRedraw.ForceRebuildMap(map);
    }
}
