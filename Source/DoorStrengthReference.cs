using RimWorld;

namespace CelestialLighting;

// Live-state half of §7d's door-strength weighting (DoorLeakMath is the Verse-free formula). One
// job: read ThingDefOf.Door.BaseMaxHitPoints -- the "our default should be wooden doors" reference
// value DoorLeakMath.DoorStepCost compares every crossed door against.
//
// Memoized rather than re-read every Rebuild: ThingDef.BaseMaxHitPoints recomputes its abstract stat
// value (walking statBases) on every access with no cache of its own, and def data cannot change
// within a running game (defs are loaded once at startup and never re-authored at runtime), so
// re-deriving it once per Rebuild -- which itself only runs when the map is dirtied -- would still be
// paying a stat walk this value can never actually change on. Mirrors SunClock's own "compute once,
// keep until told otherwise" shape, just with no invalidation trigger at all rather than a dirty flag,
// because nothing can dirty it.
public static class DoorStrengthReference
{
    private static float? woodDoorBaseMaxHitPoints;

    public static float WoodDoorBaseMaxHitPoints =>
        woodDoorBaseMaxHitPoints ??= ThingDefOf.Door.BaseMaxHitPoints;
}
