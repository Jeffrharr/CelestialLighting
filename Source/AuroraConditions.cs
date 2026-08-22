using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// Thin adapter: the impure boundary for subsystem 11 (DESIGN.md §11). Resolves which live
// GameCondition, if any, should drive the auroral night-sky tint on a map, and hands the pure
// AuroraMath the primitives it needs. All the actual math (visibility ramp, fade, colour) lives in
// AuroraMath.cs under offline unit tests; this file only touches live Map/Def state.
//
// Two conditions drive the tint, and they are exactly the two events under which a real sky glows:
//
//   * SolarFlare (GameCondition_DisableElectricity) — has no sky visual at all in vanilla, yet a
//     real solar flare is precisely what *drives* an aurora. This is the pure addition.
//   * Aurora (GameCondition_Aurora) — the explicit vanilla event. It does define a SkyTarget, but
//     see below: at night that render is very nearly a no-op, so we are filling a hole rather than
//     fighting anything.
//
// Nothing else tints. That gate is the whole point of resolving specific defs here instead of, say,
// keying off darkness: an aurora that shows up on an ordinary clear night is worse than no aurora.
//
// Why tinting during the vanilla Aurora event does NOT double up. An earlier revision of this file
// claimed it would, and that claim was wrong — the same class of mistake as the maxGlow premise
// corrected in DESIGN.md §13, and worth spelling out so nobody re-derives it. SkyManager.
// CurrentSkyTarget composes each condition's SkyTarget with SkyTarget.LerpDarken, and
// SkyColorSet.LerpDarken is `Color.Lerp(A.sky, A.sky.Min(B.sky), t)` — a per-channel *minimum*. A
// condition can therefore only ever darken the sky. GameCondition_Aurora returns
// `Lerp(white, currentColor, 0.075) * max(0.73, sunGlow)`, which at night is ~(0.68, 0.73, 0.68) for
// its green palette entry, while the clear night sky is (0.482, 0.603, 0.682). The min discards
// vanilla's colour in every channel but blue, where it shaves 0.682 to 0.675 — about 1%. Its
// `glow = max(sunGlow, 0.25)` floor is min'd away just as completely, so it never brightens the map
// either, despite the letter promising exactly that. What vanilla's aurora *does* still deliver is
// the saturation drop (1.25 -> 1.0) and its SkyGaze joy bonus, neither of which we touch.
//
// That same LerpDarken min also caps how hard the tint can usefully be pushed during the event:
// vanilla's colour set is a per-channel ceiling while its condition runs, and past a tint of ~0.32
// green pins while red and blue keep falling, skewing the hue rather than deepening it. At the
// shipped AuroraMath.MaxSkyTintStrength (0.18) that ceiling is nowhere near binding, so suppressing
// vanilla's SkyTarget to lift it would buy nothing at all — in exchange for a second Harmony patch
// on a vanilla method and a conflict surface with every other mod that touches auroras.
public static class AuroraConditions
{
    // Cached on first successful lookup. SolarFlare is a core GameConditionDef but is NOT exposed on
    // RimWorld.GameConditionDefOf (unlike Eclipse/Aurora), so it must be resolved by defName. Only
    // caching on success avoids permanently latching a null if we're ever called before defs finish
    // loading.
    private static GameConditionDef _flareDefCache;

    // The fifth GameConditionManager chain walk, memoised on the same terms as MapSky's four. That
    // file's header says "all four gates that walk the chain are memoised" and draws the dividing
    // line at the WALK rather than the call count -- this one sits in a different file and was simply
    // missed by it, while being the most expensive walk of the set: ActiveTintDriver does up to THREE
    // (IsAlwaysDarkOutside, then Aurora, then SolarFlare), and it is asked twice per CurSkyTarget by
    // the sky tint and twice more per frame by the curtain draw.
    //
    // The whole-mod sweep is what promoted this from tidiness to a fix. At 15:00 with no aurora and
    // no flare -- the state a colony is in essentially always -- the tint stage measured 8.3 us a
    // call and the curtain draw 5.7, together about a quarter of the entire sky chain, to answer
    // "nothing is happening" four times over.
    //
    // SOUND FOR THE REASON MAPSKY'S ARE, and worth restating because a condition list looks like the
    // kind of thing a frame key cannot track: GeometryStamp carries the TICK as well as the frame,
    // and conditions are registered and ended on the tick, so a stamp that is still valid is a stamp
    // across which no condition can have appeared or gone. That is why vanilla's own cachedAlwaysDark
    // needs RegisterCondition/OnConditionEnd hooks and this needs none -- vanilla caches ACROSS ticks
    // and we do not.
    //
    // Caching the GameCondition reference rather than a bool is likewise safe within a tick: callers
    // read live state off it (RampFor reads TicksLeft), and the object handed back is the same object
    // a fresh walk would have found, not a snapshot of it.
    private static readonly GeometryMemo<GameCondition> TintDriverMemo = new GeometryMemo<GameCondition>();

    // Static delegate, per GeometryMemo's convention: building the lambda at the call site would
    // allocate a closure per call, which is the cost this change exists to remove wearing a hat.
    private static readonly Func<Map, GameCondition> ComputeTintDriver = ComputeTintDriverFor;

    private static GameConditionDef FlareDef
    {
        get
        {
            if (_flareDefCache == null)
                _flareDefCache = DefDatabase<GameConditionDef>.GetNamedSilentFail("SolarFlare");
            return _flareDefCache;
        }
    }

    // The active auroral-driver condition on this map, or null if none is active. Callers gate all
    // tinting on a non-null return.
    //
    // The vanilla Aurora event wins when both are somehow running at once: it is the named,
    // player-facing event with its own letter and joy bonus, so its colour cycle should be the one
    // on screen. A flare tinting on top of it would only mix two unrelated hues.
    public static GameCondition ActiveTintDriver(Map map)
    {
        // Null map and no-TickManager both bypass the memo rather than being cached under a made-up
        // key, exactly as MapSky.HasSky's do and for the same two reasons: the first has no uniqueID
        // to key on, and the second is every context outside a running game (main menu, world
        // generation, mod init), where FrameStamp.Current would dereference a null Find.TickManager.
        // Both fall through to the pre-memo code path, so the answer off-game is what it always was.
        if (map == null || Find.TickManager == null)
            return ComputeTintDriverFor(map);

        return TintDriverMemo.Get(map.uniqueID, FrameStamp.Current(), map, ComputeTintDriver);
    }

    private static GameCondition ComputeTintDriverFor(Map map)
    {
        // Null map answers null -- no map, no driver, no tint. Previously this method dereferenced
        // map unconditionally and the null case never arose because every caller had a live map; it
        // is spelled out now because the bypass above can route a null here.
        if (map == null)
            return null;

        // Always-dark maps (undercave and friends) have no sky to tint, and vanilla's own aurora
        // stands itself down there too — GameCondition_Aurora returns a null SkyTarget and a zero
        // lerp factor when IsAlwaysDarkOutside. Matching that keeps us from painting an aurora onto
        // a rock ceiling.
        if (map.gameConditionManager.IsAlwaysDarkOutside)
            return null;

        GameCondition auroraEvent = ActiveCondition(map, GameConditionDefOf.Aurora);
        if (auroraEvent != null)
            return auroraEvent;

        return ActiveCondition(map, FlareDef);
    }

    // Null-safe def lookup: a heavily-modified game could be missing a def entirely, and
    // GetActiveCondition(null) would throw rather than politely returning nothing.
    private static GameCondition ActiveCondition(Map map, GameConditionDef def)
    {
        if (def == null)
            return null;
        return map.gameConditionManager.GetActiveCondition(def);
    }

    // The hue to tint the sky toward for a given driver.
    //
    // During the vanilla Aurora event we take the condition's *own* CurrentColor rather than our
    // green/red shimmer. That colour is already cycling through vanilla's eight-entry palette
    // (greens, cyans, blues, violets) on its own transition timer, so borrowing it gives the event
    // the "undulating colors" its letter advertises, keeps our render and vanilla's pointing the
    // same direction in every channel — which matters under LerpDarken's per-channel min — and means
    // an aurora event never looks like a recolour of a solar flare.
    //
    // A solar flare has no colour of its own to borrow, so it gets AuroraMath's slow green<->red
    // shimmer between the two atomic-oxygen emission lines.
    //
    // CurrentColor indexes a palette array with fields that GameCondition_Aurora.Init() seeds from -1,
    // so reading it before Init would throw. It can't happen from here: GameConditionManager
    // .RegisterCondition calls Init() before the condition is in activeConditions, and our driver only
    // ever comes back out of GetActiveCondition. Vanilla's own SkyTarget relies on the same ordering.
    public static Color TintColorFor(GameCondition driver, int ticksGame)
    {
        if (driver is GameCondition_Aurora vanillaAurora)
            return vanillaAurora.CurrentColor;

        float phase = AuroraMath.PhaseFromTicks(ticksGame, AuroraMath.ShimmerPeriodTicks);
        AuroraMath.Rgb shimmer = AuroraMath.AuroralColorAtPhase(phase);
        return new Color(shimmer.R, shimmer.G, shimmer.B);
    }

    // Peak-agnostic blend strength for the sky colour right now on this map: 0 unless a driver is
    // active, the sky is dark enough to see an aurora, and the condition is within its active
    // (post-fade-in, pre-fade-out) window. Shared by Patch_AuroraTint and AuroraTintProbe so the
    // patch and the live probe can never derive a different value from each other — the same
    // discipline SolarPosition.cs enforces between the shadow patches.
    public static float CurrentSkyTintStrength(Map map)
    {
        GameCondition driver = ActiveTintDriver(map);
        if (driver == null)
            return 0f;

        float sunGlow = GenCelestial.CurCelestialSunGlow(map);
        float ramp = RampFor(driver);
        // Reads the same §18 vacuum gate the patch does (Vacuum.cs), so the aurora_tint probe keeps
        // agreeing with the patch on a space map instead of reporting a tint nothing renders, and the
        // same §11a curtain flag, so it reports the peak the tint is actually running at.
        return AuroraMath.SkyTintStrength(sunGlow, ramp, CurtainEnabled, Vacuum.InVacuumForMap(map));
    }

    // Whether §11a's ribbon curtain is the thing carrying the aurora right now, which is what decides
    // whether the flat §11 tint above runs at its solo peak or steps back to vanilla's as a base wash.
    //
    // Deliberately keyed on the FEATURE FLAGS ONLY, not on the curtain's instantaneous alpha. Both
    // layers derive their strength from the same NightVisibility × ramp product, so "is the curtain
    // drawing at this instant" and "is the tint non-zero at this instant" are the same question — using
    // the alpha here would make the tint's peak depend on a value derived from the tint's own inputs,
    // for no gain and one circular dependency. Keyed this way, the two layers ramp in lockstep and the
    // handover is invisible.
    public static bool CurtainEnabled =>
        CelestialLightingFeatures.Aurora && CelestialLightingFeatures.AuroraCurtain;

    // Peak-agnostic alpha for the §11a curtain overlay right now on this map. Same contract, and the
    // same reason for existing, as CurrentSkyTintStrength above: Patch_AuroraCurtainDraw and the
    // aurora_curtain live probe both come through here, so a scenario pins the number the renderer
    // actually used rather than one recomputed alongside it.
    public static float CurrentCurtainStrength(Map map)
    {
        if (!CurtainEnabled)
            return 0f;

        GameCondition driver = ActiveTintDriver(map);
        if (driver == null)
            return 0f;

        return CurtainStrengthFor(map, driver);
    }

    // The same value, for a driver the caller has ALREADY resolved.
    //
    // Split out for Patch_AuroraCurtainDraw, which needs the driver anyway — it is where tonight's hue
    // comes from — and was therefore walking the condition list twice per drawn frame: once inside
    // CurrentCurtainStrength above, and once more for itself immediately afterwards. Measured at ~8 of
    // the ~90 microseconds the fixed per-frame path costs (issue #60).
    //
    // Deliberately NOT CurrentCurtainStrength with an argument bolted on. The gates that belong to the
    // LOOKUP — the curtain feature flag and the null driver — stay above, so a caller holding a
    // non-null driver has already passed them and what remains here is only ever the arithmetic.
    public static float CurtainStrengthFor(Map map, GameCondition driver)
    {
        float sunGlow = GenCelestial.CurCelestialSunGlow(map);
        return AuroraMath.CurtainStrength(sunGlow, RampFor(driver));
    }

    // Fade ramp for a condition, translating its permanence into the "huge ticksLeft" AuroraMath
    // expects so a permanent flare (or permanent aurora — the Aurora def sets canBePermanent) holds
    // full strength instead of fading out. A non-permanent condition uses its real TicksLeft so the
    // tint eases away as the event ends.
    public static float RampFor(GameCondition condition)
    {
        float ticksLeft = condition.Permanent ? float.MaxValue : condition.TicksLeft;
        return AuroraMath.ConditionRampFactor(condition.TicksPassed, ticksLeft, AuroraMath.DefaultFadeTicks);
    }
}
