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
        // agreeing with the patch on a space map instead of reporting a tint nothing renders.
        return AuroraMath.SkyTintStrength(sunGlow, ramp, Vacuum.InVacuumForMap(map));
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
