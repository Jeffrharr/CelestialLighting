using Verse;

namespace CelestialLighting;

// The single live-game read behind the whole §18 vacuum epic (DESIGN.md §18). Every subsystem that
// has to behave differently on an Odyssey space map goes through this one function, and every pure
// function that behaves differently takes the resulting `bool inVacuum` as its LAST parameter.
//
// THE CONVENTION (read this before adding a vacuum branch to any other subsystem — #30/#31/#32/#33
// all land on top of it):
//
//   1. The adapter (a Patch_* file, or a thin *Conditions/*Effect helper that already takes a Map)
//      calls Vacuum.InVacuumForMap(map) exactly once and passes the bool down.
//   2. The pure-math function takes `bool inVacuum` as its last parameter and early-returns the
//      vacuum value at the top, before any atmospheric math runs. Required, never defaulted: a
//      defaulted parameter lets a new call site silently opt out of the gate, and the whole point of
//      one shared gate is that you cannot forget it.
//   3. The offline test pins the vacuum value AND its sea-level counterpart in the same [TestCase]
//      sweep, so a regression in either shows up as a diverging pair rather than a single number
//      quietly matching a stale expectation.
//
// Why this is a plain field read and not a DLC gate: `inVacuum` is a field on BASE
// RimWorld.BiomeDef, not on anything Odyssey adds (verified by decompiling 1.6 Assembly-CSharp —
// all DLC code ships in the base assembly, and ApiCompatibilityTests.BiomeDef_HasInVacuum pins it).
// So this compiles and evaluates with Odyssey uninstalled and reads `false` on every vanilla biome.
// Do NOT wrap it in ModsConfig.OdysseyActive or soft-reference plumbing: that would add a branch
// that can only ever agree with this one, and a second thing to keep in sync.
//
// Scope note: this is deliberately a per-MAP question, not a per-cell one. A pressurised gravship
// hull sitting on a vacuum map is a per-cell question, and vanilla answers it with
// Verse.VacuumUtility.GetVacuum(cell, map) / IsRoomAirtight(room). Nothing in §18a needs that —
// sky colour is a whole-map property — so we deliberately do not reach for it here. §7b's indoor
// occlusion is the subsystem most likely to want the per-cell version later.
public static class Vacuum
{
    // True when this map has no atmosphere: an Odyssey orbital platform or space map. False for
    // every planet-surface biome, including with the DLC absent.
    public static bool InVacuumForMap(Map map) => map.Biome.inVacuum;
}
