using RimWorld;
using Verse;

namespace CelestialLighting;

// Game-wide single moon (see DESIGN.md §6). A GameComponent, not a MapComponent, because the moon's
// phase is a property of the whole game clock, shared by every map and tile at once — canon says the
// planet has several moons, but we deliberately model one representative moon (nothing in the mod's
// scope needs more; only the opt-in eclipse trigger in §10 would). RimWorld auto-instantiates every
// GameComponent subclass via Activator.CreateInstance(type, game), so the (Game) constructor below is
// required and no XML registration is needed.
//
// The component is a thin adapter: it holds the one piece of configurable state (the cycle length)
// and turns the live game tick into a cycle position, then hands primitives to MoonMath (the pure,
// unit-tested core) for phase/illumination. Per-map moon *position* (which depends on tile latitude)
// lives in the MoonShadow adapter, not here, so this stays genuinely game-wide.
public class GameComponent_MoonPhase : GameComponent
{
    // Length of one full new->full->new cycle, in in-game days. Persisted so a configured value
    // survives save/load; defaults to MoonMath.DefaultSynodicMonthDays. A future settings slider is
    // the intended writer of this field (there is no ModSettings screen yet).
    // TODO(integration): bind this to the mod's ModSettings once the settings screen (see DESIGN.md
    // "Settings, presets, and the brightness floor") exists.
    public float synodicPeriodDays = MoonMath.DefaultSynodicMonthDays;

    public GameComponent_MoonPhase(Game game)
    {
    }

    // Convenience accessor: the live moon component for the current game, or null if there is no game
    // loaded yet (e.g. on the main menu). Callers on the render/patch path must null-check, because a
    // patch can fire while Current.Game is momentarily null during load.
    public static GameComponent_MoonPhase Current =>
        Verse.Current.Game?.GetComponent<GameComponent_MoonPhase>();

    private long SynodicPeriodTicks => (long)(synodicPeriodDays * GenDate.TicksPerDay);

    // Fraction through the synodic cycle right now, in [0, 1). Derived purely from the absolute tick
    // count (via MoonMath), so there is no per-tick state to keep in sync or persist.
    public float CyclePosition =>
        MoonMath.SynodicCyclePosition(Find.TickManager.TicksAbs, SynodicPeriodTicks);

    // Illuminated fraction of the disc, 0 at new and 1 at full — the scalar both moon-cast shadows
    // and moonlight scale by.
    public float IlluminatedFraction => MoonMath.IlluminatedFraction(CyclePosition);

    public MoonMath.MoonPhase Phase => MoonMath.PhaseFor(CyclePosition);

    public bool IsWaxing => MoonMath.IsWaxing(CyclePosition);

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref synodicPeriodDays, "synodicPeriodDays", MoonMath.DefaultSynodicMonthDays);
    }
}
