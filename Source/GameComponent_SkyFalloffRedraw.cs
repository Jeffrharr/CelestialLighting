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
//
// IT NOW DRIVES §9's WASH MESHES TOO, DESPITE THE NAME. §9's DarkAreaDesaturation moves the
// rod-vision factor out of the material and into the baked section mesh, which gives that mesh the
// identical staleness this class was written for: a room baked at noon would keep its noon wash into
// the night unless something happened to dirty it. Both subsystems need the same question asked at
// the same cadence about the same maps, so they share one tick rather than growing a second
// GameComponent — a new component type is not free to add and, more to the point, not free to REMOVE
// later, since Game.ExposeSmallComponents scribes a node per component (the same trap
// MapComponent_SunShadowAxis.cs is a tombstone for).
//
// THE TYPE NAME IS DELIBERATELY NOT UPDATED to match its widened job, for that same reason: the
// scribe writes this class's name into every save that has run it, so renaming it would strand those
// nodes and log errors on load. The name records where it came from; this comment records what it
// does.
//
// The two subsystems keep SEPARATE baselines and separate drift tests. They are not the same
// quantity: §7b's is CurSkyGlow itself, while §9's is PurkinjeFactor of the composed, weather-
// attenuated glow, which is flat 0 above sky glow 0.5 and flat 1 below 0.05. Sharing one baseline
// would make each fire on the other's transitions — §9 rebuilding all through a daylight glow change
// that cannot move its factor off zero, which is most of a day.
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

    // §9's baseline: the rod-vision factor its wash meshes were last baked against, keyed the same way
    // and for the same reasons as lastBakedGlow above.
    private static readonly Dictionary<int, float> lastBakedWashFactor = new Dictionary<int, float>();

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

        bool falloff = FalloffMeshesVaryWithGlow();
        bool wash = WashMeshesVaryWithGlow();
        if (!falloff && !wash)
            return;

        List<Map> maps = Find.Maps;
        for (int i = 0; i < maps.Count; i++)
            CheckMap(maps[i], falloff, wash);
    }

    // §7b's arm of the gate. Neither sky-falloff source varies with CurSkyGlow when both are off (or
    // the whole subsystem is off) — SkyFalloffSource.FractionAt then returns a flat 0 for every cell
    // regardless of the sky overhead, so there is nothing there that can be stale.
    private static bool FalloffMeshesVaryWithGlow() =>
        CelestialLightingFeatures.IndoorSkyOcclusion && AnyBakedTermVariesWithGlow();

    // §9's arm. The wash mesh only carries a sky term at all when DarkAreaDesaturation is on — with
    // it off the factor is back on the material, which is rewritten every frame and cannot go stale —
    // and a wash scaled to nothing by the slider has no visible mesh to rebuild.
    private static bool WashMeshesVaryWithGlow() =>
        CelestialLightingFeatures.LowLightDesaturation
        && CelestialLightingFeatures.DarkAreaDesaturation
        && PurkinjeSettings.TintStrength > 0f;

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

    private static void CheckMap(Map map, bool falloff, bool wash)
    {
        if (falloff)
            CheckFalloff(map);

        if (wash)
            CheckWash(map);
    }

    private static void CheckFalloff(Map map)
    {
        int mapId = map.uniqueID;
        float curGlow = map.skyManager.CurSkyGlow;

        bool hasBaseline = lastBakedGlow.TryGetValue(mapId, out float bakedGlow);
        if (hasBaseline && !SkyFalloffRedrawMath.ShouldRedraw(bakedGlow, curGlow, SkyFalloffRedrawMath.DefaultThreshold))
            return;

        lastBakedGlow[mapId] = curGlow;
        IndoorOcclusionRedraw.ForceRebuildMap(map);
    }

    // §9's wash meshes, against the SAME factor the bake will read — composed through
    // NightRadiance.VisualGlowFor and WeatherDimming exactly as Patch_NightDesaturationStrength does,
    // rather than against raw CurSkyGlow. Reading the raw glow here would put the trigger and the
    // thing triggered on two different quantities: an eclipse drives glow to 0 without moving the
    // composed factor at night, and weather attenuates the composed factor without moving the glow at
    // all, so each would fire redraws the bake does not need and miss ones it does.
    //
    // This is why the drift test is on the FACTOR and not the glow: the factor is flat 0 through the
    // whole of daylight and flat 1 through the whole of night, so a threshold on it fires only across
    // the two twilight ramps — the bounded-transition property the header argues makes this safe,
    // stated in the quantity that actually reaches the mesh.
    private static void CheckWash(Map map)
    {
        int mapId = map.uniqueID;
        float curFactor = PurkinjeMath.PurkinjeFactor(
            WeatherDimmingMath.ApparentGlow(
                NightRadiance.VisualGlowFor(map, map.skyManager.CurSkyGlow),
                WeatherDimming.DimmingFor(map)));

        bool hasBaseline = lastBakedWashFactor.TryGetValue(mapId, out float bakedFactor);
        if (hasBaseline && !SkyFalloffRedrawMath.ShouldRedraw(bakedFactor, curFactor, SkyFalloffRedrawMath.DefaultThreshold))
            return;

        lastBakedWashFactor[mapId] = curFactor;
        NightDesaturationRedraw.ForceRebuildMap(map);
    }
}
