namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file, same discipline as
// IndoorOcclusionMath. Compiled into both Source (net481, runs inside RimWorld) and Tests (net8.0,
// runs standalone via `dotnet test`) via a linked <Compile Include>.
//
// Subsystem 7c pure core (DESIGN.md §7c): native under-roof sky falloff, no Ambient Light
// (f1995.ambientlight) dependency. §7b's own occlusion is a blanket "every interior cell is fully
// dark" — this is the gradient near an opening that §7a's Ambient Light compat (§7b's third
// CapOcclusion argument) already gives players who have that mod installed, built natively for
// players who don't.
//
// Deliberately MIRRORS IndoorOcclusionMath.AmbientLightUnderRoofSkyLight / AmbientLightFalloffNoSky
// in shape — curSkyGlow * passThrough01 * (1 - depth/maxDepth), zero at depth <= 0 — rather than
// sharing code with it. The two formulas are conceptually independent (this one answers "how much sky
// should WE let leak in, absent any third-party mod", not "what does Ambient Light's own private
// formula compute"), and forcing one to call the other would make a deliberate future change to either
// read as breaking the other. See SkyFalloffSource for why the two sources are mutually exclusive per
// cell rather than composed — different mods, different maxDepth values by construction, and Max()-ing
// two independently-tuned gradients would put a visible seam wherever the smaller maxDepth runs out.
public static class NativeSkyFalloffMath
{
    // maxDepth lands in the same register as Ambient Light's own shipped default (12, confirmed by
    // decompiling AmbientLightFalloff.dll's AmbientLightSettings this session), so a player who tries
    // the game with and without Ambient Light installed sees a similar CHARACTER of falloff near a
    // doorway. passThroughPercent does NOT match Ambient Light's own 55f default: live playtesting on
    // the shipped 55f found a typical roofed room reading as lit up rather than gently graded near the
    // door (see NativeSkyFalloffSettings for the two matching sliders this drove) — 25f is the new
    // out-of-box "starting brightness right at the opening" that reads as a gradient, not a room light.
    public const int DefaultMaxDepth = 12;
    public const float DefaultPassThroughPercent = 25f;

    // depth: BFS distance from the nearest unroofed/non-blocking cell (NativeSkyFalloffGrid.DepthAt),
    // 0 or less meaning "not reached" — either the cell is unroofed outright (nothing to compute; the
    // ordinary vanilla sky cover already applies) or it sits further than maxDepth from any opening.
    // curSkyGlow: SkyManager.CurSkyGlow, the SAME sky term §7/§7a already floor at night, so a
    // genuinely pitch-black night still yields a near-zero fraction here even one step from a door —
    // there is nothing to redistribute once the source itself is dark, matching Ambient Light's own
    // stated design intent ("dynamically lightens based on outdoor sky brightness").
    public static float FractionAt(int depth, float curSkyGlow, int maxDepth, float passThroughPercent)
    {
        if (curSkyGlow <= 0.001f)
            return 0f;

        return Clamp01(curSkyGlow * FalloffNoSky(depth, maxDepth, passThroughPercent));
    }

    private static float FalloffNoSky(int depth, int maxDepth, float passThroughPercent)
    {
        if (depth <= 0)
            return 0f;

        int clampedMaxDepth = maxDepth < 1 ? 1 : maxDepth;
        float passThrough01 = Clamp01(passThroughPercent / 100f);
        float depthFraction = Clamp01((float)depth / clampedMaxDepth);
        return Clamp01(passThrough01 * (1f - depthFraction));
    }

    // Whether one BUILDING standing on a cell stops the flood there -- asked once per building in the
    // cell, not once per cell, because vanilla asks it that way (see below). `blocksLight` is that
    // building's ThingDef.blockLight, `isDoor` is altitudeLayer == AltitudeLayer.DoorMoveable, and
    // `isApertureFixture` is NativeSkyFalloffGrid.IsWallApertureFixture -- a vent or a cooler, the two
    // vanilla buildings whose whole job is to fill a hole in a wall. The adapter
    // (NativeSkyFalloffGrid.CellBlocksFlood) reads all three off the live thing grid.
    //
    // THE BLOCKER SET IS VANILLA'S, EXACTLY -- the same argument VectorLightBlockers' own header makes
    // for the vector-light occluders, and for the same reason. Verse.Building.SpawnSetup writes
    // def.blockLight into GlowGrid's lightBlockers bit array on spawn and clears it on despawn, so
    // blockLight is precisely the set vanilla's own glow flood refuses to pass. A flood that answers a
    // DIFFERENT question than the gameplay light disagrees with what the player sees in exactly the
    // places they are looking.
    //
    // This used to also require def.holdsRoof, mirrored from AmbientLightFalloff.MapComp_AmbientLight's
    // own RebuildDistance, on the reading that a solid cell is a cell holding up the roof. It is not: a
    // core Vent is Impassable, fillPercent 1, blockLight TRUE and holdsRoof FALSE, so it was crossed
    // exactly like an open doorway -- an interior cell behind a vent read depth 2 and fraction
    // 0.2625 at noon, bit-identical to a cell behind a plain wood door, and a sealed room with a vent
    // in its wall glowed at night while the same room without one stayed black. The same class covers
    // ten more core defs (Cooler, GeothermalGenerator, WatermillGenerator, Noctolith and the ship
    // parts), every one of them Impassable and light-blocking. Vent and Cooler are the two players
    // build INTO an exterior wall, so they were also the two that leaked.
    //
    // A door is still not a blocker, and that clause is why this is not simply `blocksLight`: the flood
    // has to cross a threshold, which is what DoorLeakMath's crossing multiplier then dims. Core's
    // FenceGate is the one light-blocking, non-roof-holding def that must NOT become solid here, and
    // the door clause is what keeps it crossable.
    //
    // `isApertureFixture` is the one place this deliberately says MORE than blockLight does, and it was
    // added for a reported leak through Replace Stuff's over-wall vent (Vent_Over). That def is a vent
    // meant to be built into a wall's own cell, and because the wall beside it already blocks, the mod
    // sets blockLight FALSE on the vent itself -- so a vent built WITHOUT a wall under it (which the
    // same mod allows, by prefixing PlaceWorker_Vent to accept any cell) is, to every blockLight test
    // including vanilla's own, an open hole. Measured: the interior cell behind one read depth 2 and
    // fraction 0.2625, identical to a cell behind a plain wood door, while the wall-plus-vent
    // arrangement beside it read 0. Vanilla never notices because vanilla has no sky flood at all -- a
    // vent with no light behind it passes no gameplay light either way -- and it is exactly the case
    // where a mod's blockLight=false means "the wall does the blocking here", not "you can see through
    // me". A glass wall means the second, keeps blockLight=false, is not a vent or a cooler, and still
    // crosses freely.
    //
    // Deliberately NOT the whole Building_TempControl hierarchy, which would also take in
    // Building_Heater: a heater is a free-standing appliance inside a room, so blocking its cell would
    // notch a dark cell out of the interior gradient for no reason. Vent and cooler are the two whose
    // PlaceWorkers demand a wall between two rooms.
    public static bool BlocksFlood(bool blocksLight, bool isDoor, bool isApertureFixture) =>
        (blocksLight || isApertureFixture) && !isDoor;

    // Whether a building spawning or despawning can change any answer the flood depends on, i.e.
    // whether Patch_SkyFalloffDirty has to invalidate the cached grid for it. The complement pair to
    // BlocksFlood rather than the same predicate: a DOOR appearing changes no BlocksFlood answer (it
    // was crossable before and after) but does change the crossing multiplier the strengths carry, so
    // the invalidation set is the union, not the blocker set.
    //
    // Stated here beside BlocksFlood on purpose. The two drifting apart is silent by construction --
    // the grid simply keeps serving a stale answer, which looks like a formula bug and not a missing
    // trigger -- and that is exactly what happened: the old trigger gated on holdsRoof, so building a
    // vent fired no invalidation at all and the fix to the blocker set alone would not have shown up
    // until something else happened to dirty the map.
    public static bool AffectsFlood(bool blocksLight, bool isDoor, bool isApertureFixture) =>
        blocksLight || isDoor || isApertureFixture;

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
}
