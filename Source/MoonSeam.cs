using System;
using Verse;

namespace CelestialLighting;

// Minimal, self-contained seam for the moon-position subsystem (DESIGN.md §6), which is
// being built in parallel and is NOT yet merged. Night radiance (§7) only needs two primitives from
// the moon — how illuminated it is (phase) and how high it sits (altitude) — so that is the entire
// contract this seam exposes, and nothing here references the (as-yet-nonexistent)
// GameComponent_MoonPhase.
//
// The default provider reports "no moon" (a new moon well below the horizon), so moonlight
// contributes exactly 0 and the night floor is starlight + airglow only. That is a correct,
// shippable behavior on its own — this branch builds and behaves sensibly with no dependency on §6.
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
    // The default reports "no moon" so §7 builds and unit-tests standalone with no dependency on the
    // moon-position subsystem (§6). Now that §6 is merged, CelestialLightingMod's startup reassigns
    // this to a lookup into MoonPosition (which reads GameComponent_MoonPhase for the live phase and
    // reuses SolarPosition/Formulas for the moon's per-tile altitude, exactly as the sun does). The
    // Map -> MoonState signature IS the contract; the reassignment lives in CelestialLightingMod
    // rather than here so this file stays Verse-free and offline-testable.
    public static Func<Map, MoonState> Provider = _ => MoonState.None;
}
