using System;
using Verse;

namespace CelestialLighting;

// Minimal, self-contained seam for the moon-position subsystem (DESIGN.md §6, GitHub #3), which is
// being built in parallel and is NOT yet merged. Night radiance (§7) only needs two primitives from
// the moon — how illuminated it is (phase) and how high it sits (altitude) — so that is the entire
// contract this seam exposes, and nothing here references the (as-yet-nonexistent)
// GameComponent_MoonPhase.
//
// The default provider reports "no moon" (a new moon well below the horizon), so moonlight
// contributes exactly 0 and the night floor is starlight + airglow only. That is a correct,
// shippable behavior on its own — this branch builds and behaves sensibly with no dependency on #3.
public readonly struct MoonState
{
    public readonly float IlluminatedFraction; // 0 = new moon, 1 = full moon
    public readonly float ElevationDegrees;     // altitude above the horizon; negative = below/set

    public MoonState(float illuminatedFraction, float elevationDegrees)
    {
        IlluminatedFraction = illuminatedFraction;
        ElevationDegrees = elevationDegrees;
    }

    // A new moon parked below the horizon: zero moonlight regardless of the max-moonlight setting.
    public static readonly MoonState None = new MoonState(0f, -90f);
}

public static class MoonSeam
{
    // TODO(integration): once the moon-position subsystem (#3/#6) is merged, replace this default
    // with a lookup into GameComponent_MoonPhase — it will supply the real per-tick illuminated
    // fraction and the moon's per-tile altitude (reusing SolarPosition/Formulas for the body's
    // elevation, exactly as the sun does). The Map -> MoonState signature IS the contract; wiring it
    // up should be a one-line reassignment here plus the component that performs the reassignment.
    public static Func<Map, MoonState> Provider = _ => MoonState.None;
}
