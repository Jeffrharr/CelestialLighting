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

    // Length of one full retrograde regression of the lunar node, in in-game days. Only the opt-in
    // natural-eclipse trigger (DESIGN.md §10a) reads this; it sets how often a new moon coincides with
    // a node and therefore how rare eclipses are. Persisted like synodicPeriodDays; defaults to
    // MoonMath.DefaultNodalPeriodDays (tuned for "an eclipse every few game years").
    public float nodalPeriodDays = MoonMath.DefaultNodalPeriodDays;

    // Dev-only epoch shifts (in ticks) added to the absolute tick before deriving the synodic and
    // nodal cycle positions. Zero in all normal play — the shipped mod never writes them. The test
    // harness sets them (via EclipseStaging) to phase-slide the moon onto a new-moon-at-node alignment
    // so a real natural eclipse can be filmed on demand instead of waiting years. Deliberately NOT
    // persisted (absent from ExposeData) so they can never leak into a real save.
    public long debugSynodicShiftTicks = 0L;
    public long debugNodalShiftTicks = 0L;

    public GameComponent_MoonPhase(Game game)
    {
    }

    // Convenience accessor: the live moon component for the current game, or null if there is no game
    // loaded yet (e.g. on the main menu). Callers on the render/patch path must null-check, because a
    // patch can fire while Current.Game is momentarily null during load.
    public static GameComponent_MoonPhase Current =>
        Verse.Current.Game?.GetComponent<GameComponent_MoonPhase>();

    private long SynodicPeriodTicks => (long)(synodicPeriodDays * GenDate.TicksPerDay);

    private long NodalPeriodTicks => (long)(nodalPeriodDays * GenDate.TicksPerDay);

    // Fraction through the synodic cycle right now, in [0, 1). Derived purely from the absolute tick
    // count (via MoonMath), so there is no per-tick state to keep in sync or persist. The dev-only
    // debug shift is +0 in all normal play (see the field), so this is the plain absolute tick then.
    public float CyclePosition =>
        MoonMath.SynodicCyclePosition(Find.TickManager.TicksAbs + debugSynodicShiftTicks, SynodicPeriodTicks);

    // Fraction through the nodal regression cycle right now, in [0, 1) — the second coordinate the
    // natural-eclipse geometry needs (where the moon's orbit currently crosses the ecliptic). Same
    // stateless derivation from the absolute tick as CyclePosition.
    public float NodalCyclePosition =>
        MoonMath.NodalCyclePosition(Find.TickManager.TicksAbs + debugNodalShiftTicks, NodalPeriodTicks);

    // Illuminated fraction of the disc, 0 at new and 1 at full — the scalar both moon-cast shadows
    // and moonlight scale by.
    public float IlluminatedFraction => MoonMath.IlluminatedFraction(CyclePosition);

    public MoonMath.MoonPhase Phase => MoonMath.PhaseFor(CyclePosition);

    public bool IsWaxing => MoonMath.IsWaxing(CyclePosition);

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref synodicPeriodDays, "synodicPeriodDays", MoonMath.DefaultSynodicMonthDays);
        Scribe_Values.Look(ref nodalPeriodDays, "nodalPeriodDays", MoonMath.DefaultNodalPeriodDays);
        // debugSynodicShiftTicks / debugNodalShiftTicks are intentionally not persisted — they are a
        // dev-only staging aid and must never survive into a real save.
    }
}
