using RimWorld;
using Verse;

namespace CelestialLighting;

// Thin adapter: the impure boundary for subsystem 11 (DESIGN.md §11). Resolves which live
// GameCondition, if any, should drive the auroral night-sky tint on a map, and hands the pure
// AuroraMath the primitives it needs. All the actual math (visibility ramp, fade, colour) lives in
// AuroraMath.cs under offline unit tests; this file only touches live Map/Def state.
//
// Why only the solar flare (and not the Aurora condition): vanilla's GameCondition_Aurora ALREADY
// renders its own shifting auroral colours via a GameCondition.SkyTarget(Map) override that
// SkyManager applies on top of WeatherWorker.CurSkyTarget. Tinting it again here would double up and
// fight vanilla's own render. The solar-flare condition (GameCondition_DisableElectricity) has no
// such visual at all, yet a real solar flare is exactly what *drives* auroras — so tinting the night
// sky during a flare adds the missing visual without conflicting with anything vanilla does. Any
// future aurora-style condition that lacks its own sky render could be added to the driver set here.
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

    // The active auroral-driver condition on this map, or null if none is active (or the def isn't
    // present, e.g. a heavily-modified game). Callers gate all tinting on a non-null return.
    public static GameCondition ActiveTintDriver(Map map)
    {
        GameConditionDef def = FlareDef;
        if (def == null)
            return null;
        return map.gameConditionManager.GetActiveCondition(def);
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
        return AuroraMath.SkyTintStrength(sunGlow, ramp);
    }

    // Fade ramp for a condition, translating its permanence into the "huge ticksLeft" AuroraMath
    // expects so a permanent flare holds full strength instead of fading out. A non-permanent flare
    // uses its real TicksLeft so the tint eases away as the flare ends.
    public static float RampFor(GameCondition condition)
    {
        float ticksLeft = condition.Permanent ? float.MaxValue : condition.TicksLeft;
        return AuroraMath.ConditionRampFactor(condition.TicksPassed, ticksLeft, AuroraMath.DefaultFadeTicks);
    }
}
