using RimWorld;
using RimWorldTestHarness.Mod.Probes;
using UnityEngine;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj —
// the shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool. See
// RimWorldTestHarness/DESIGN.md's "Where probe tests live" for the full reasoning.
//
// Reads back Formulas.SolarDeclinationDegrees — the same pure formula Patch_ShadowDirection calls
// to drive the solar-position shadow simulator — for the day-of-year the live game clock is
// currently on. FormulasTests.cs already pins SolarDeclinationDegrees(15f) == 0f as the equinox
// regression case; this probe is what lets Tests/Scenarios/shadow_lean_equinox.json assert that same
// invariant end-to-end against a real running game instead of just the offline unit test.
public sealed class ShadowLeanProbe : IProbe
{
    public string Name => "shadow_lean";

    public float Read(Map map)
    {
        Vector2 longLat = Find.WorldGrid.LongLatOf(map.Tile);
        int dayOfYear = GenDate.DayOfYear(Find.TickManager.TicksAbs, longLat.x);
        return Formulas.SolarDeclinationDegrees(dayOfYear);
    }
}
