using System.Collections.Generic;
using RimWorld;
using Verse;

namespace CelestialLighting;

// Orchestrates the NATURAL eclipse (DESIGN.md §10a): while the eclipse mode includes natural eclipses
// (NaturalOnly or the default Both) and the "Eclipse effects" master is on, it watches the modeled
// moon and, when the moon geometrically transits the sun (the rare
// new-moon-at-a-node alignment the inclination/node model in MoonMath produces), fires a real, SHORT
// vanilla Eclipse GameCondition on every player map and publishes the transit's magnitude so the
// darkening reads as a full or a partial eclipse. The random Eclipse *incident* is separately
// suppressed while this mode is on (Patch_SuppressRandomEclipse) so the two never double-fire.
//
// This component holds no astronomy of its own — all geometry, magnitude, and duration math lives in
// the pure cores (MoonMath/EclipseMath) behind EclipseIntegration. Its only job is orchestration:
// *when* to fire, on which maps, and not firing twice for one passage. RimWorld auto-instantiates
// every GameComponent subclass with a (Game) constructor, so no manual registration is needed; when
// the feature is off this ticks a single cheap flag-check and does nothing else.
public class GameComponent_NaturalEclipse : GameComponent
{
    // How often to sample the geometry. Even a dead-central natural eclipse lasts ~an hour (2500
    // ticks) at the default synodic period, so a handful of checks per hour never misses the window
    // while the per-tick cost the rest of the time is a single modulo and a bool test.
    private const int CheckIntervalTicks = 500;

    // After firing, ignore further transits for this long. If our computed duration ends a hair before
    // the geometric window actually closes, this stops the condition from instantly re-firing and
    // flickering. One game-day dwarfs any single eclipse yet is far shorter than the years between
    // real ones, so it can never swallow a genuine second eclipse.
    private const int RefireCooldownTicks = GenDate.TicksPerDay;

    // TicksGame of the last fire, or -1 if none this session. Not persisted: the cooldown only guards
    // against same-passage re-fire (a sub-day window), so a fresh -1 after load is harmless — the very
    // next check re-derives everything from the (stateless) moon geometry.
    private int lastFiredTick = -1;

    public GameComponent_NaturalEclipse(Game game)
    {
    }

    public override void GameComponentTick()
    {
        // The "Eclipse darkening" toggle (CelestialLightingFeatures.EclipseDarkening) is the master for
        // all of CelestialLighting's eclipse handling — with it off the mod does nothing to eclipses
        // (vanilla flat dim, vanilla random timing), so the geometric trigger stands down too.
        if (!CelestialLightingFeatures.EclipseDarkening)
            return;

        if (!EclipseModeRules.NaturalTriggerActive(EclipseSettings.Mode))
            return;

        if (Find.TickManager.TicksGame % CheckIntervalTicks != 0)
            return;

        if (IsWithinRefireCooldown())
            return;

        FireEclipseWherePossible();
    }

    private bool IsWithinRefireCooldown() =>
        lastFiredTick >= 0 && Find.TickManager.TicksGame - lastFiredTick < RefireCooldownTicks;

    // The transit is a global celestial fact (one shared moon), so a single geometry check gates every
    // map; the condition is still registered per map because each map's solar power and colonist mood
    // react on their own. Records the fire time only if at least one map actually took the eclipse.
    private void FireEclipseWherePossible()
    {
        bool anyFired = false;
        foreach (Map map in Find.Maps)
        {
            anyFired |= TryFireEclipseOn(map);
        }

        if (anyFired)
            lastFiredTick = Find.TickManager.TicksGame;
    }

    private bool TryFireEclipseOn(Map map)
    {
        if (!ShouldFireOn(map))
            return false;

        double magnitude = EclipseIntegration.CurrentTransitMagnitude(map);
        int duration = EclipseIntegration.CurrentEclipseDurationTicks(magnitude);
        if (duration <= 0)
            return false;

        // Publish the magnitude before registering so the darkening patch reads the right central/
        // partial value from the eclipse's first frame.
        EclipseIntegration.ActiveNaturalMagnitude = magnitude;
        GameCondition condition = GameConditionMaker.MakeCondition(GameConditionDefOf.Eclipse, duration);
        map.gameConditionManager.RegisterCondition(condition);
        // Tag it as ours so that in Both mode the darkening patch renders this one with the natural
        // ramp while the storyteller's random eclipses still render unnatural.
        EclipseIntegration.MarkNaturalCondition(condition);
        return true;
    }

    // Fire only when the map can see the sky at all, AND a transit is geometrically active on this map,
    // AND the map is not already under an Eclipse (ours from a previous tick, or a lingering vanilla
    // one) — the last two must never stack.
    //
    // The MapSky.IsEnclosed term is why a colony living in a Biomes! Caverns cavern is never handed an
    // eclipse it could not possibly witness. It carries more weight here than in the render patches:
    // this is the one place §10a REGISTERS a GameCondition, so an ungated natural eclipse underground
    // would raise a real alert and apply real solar-power consequences on a map with no sun overhead —
    // a gameplay effect rather than a tint. Deliberately per-map rather than hoisted out of the
    // Find.Maps walk: the transit is a shared celestial fact, but whether a given map can witness it
    // is a property of that map.
    private bool ShouldFireOn(Map map) =>
        !MapSky.IsEnclosed(map)
        && EclipseIntegration.ShouldEclipseBeActive(map)
        && map.gameConditionManager.GetActiveCondition(GameConditionDefOf.Eclipse) == null;
}
