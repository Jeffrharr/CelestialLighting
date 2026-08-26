using HarmonyLib;
using RimWorld;
using Verse;

namespace CelestialLighting;

// §27's invalidation, hooked onto the four vanilla methods that are the actual writes rather than
// onto a map-mesh dirty flag.
//
// WHY NOT A MapMeshFlagDef SUBSCRIPTION, which is the obvious route. GlowGrid.DirtyCell raises Roofs
// AND GroundGlow together, so subscribing to either means every lamp toggle, every fire growing, and
// every sun lamp cycling invalidates us — §16 measures that as the most frequently raised flag in the
// game. Patch_OpenSkyMaskInvalidation records the same conclusion for §24 and hooks RoofGrid.SetRoof
// directly for the same reason: the write is strictly cheaper than the flag, and it is also more
// precise about what changed.
//
// TWO KINDS OF CHANGE, DELIBERATELY SPLIT. Registering or deregistering an emitter changes WHO is
// lighting the map, and costs one resync of the roster against vanilla's own sets. Adding or removing
// a light blocker changes the SHAPE thrown by the handful of lights that can see that cell, and
// costs nothing to any other light. Collapsing them into one flag would mean a lamp being switched
// off rebaked every polygon on the map, which is exactly the provocation pattern §16 records killing
// the across-map tilt ramp over.
//
// No timer anywhere. Everything below is provoked by something a player or a game event did.
public static class VectorLightInvalidation
{
    public static void RosterChanged(GlowGrid grid)
    {
        VectorLightField.MarkRosterDirty(GlowGridAccess.GetMap(grid));
    }

    // The one caller that carries a moved BLOCKER, which is why it is also the one that may throw
    // away a recorded silhouette. LightBlockerAdded/Removed are called only from Building.SpawnSetup
    // and DeSpawn, so this fires exactly when the whole-cell occluder grid around a cell changes and
    // at no other time — see issue #188 item C for what rests on that being true.
    public static void BlockerChanged(GlowGrid grid, IntVec3 cell)
    {
        VectorLightField.MarkGeometryDirtyAround(
            GlowGridAccess.GetMap(grid), cell, blockerMoved: true);
    }
}

[HarmonyPatch(typeof(GlowGrid), nameof(GlowGrid.RegisterGlower))]
public static class Patch_VectorLightGlowerRegistered
{
    static void Postfix(GlowGrid __instance) => VectorLightInvalidation.RosterChanged(__instance);
}

[HarmonyPatch(typeof(GlowGrid), nameof(GlowGrid.DeRegisterGlower))]
public static class Patch_VectorLightGlowerDeregistered
{
    static void Postfix(GlowGrid __instance) => VectorLightInvalidation.RosterChanged(__instance);
}

// Glowing terrain registers through its own path, with no CompGlower involved. It is patched here for
// the same reason VectorLightField reads litTerrain at all: §27 suppresses vanilla's render of every
// artificial light source, so anything it does not know about goes dark rather than merely unimproved.
[HarmonyPatch(typeof(GlowGrid), nameof(GlowGrid.RegisterTerrain))]
public static class Patch_VectorLightTerrainRegistered
{
    static void Postfix(GlowGrid __instance) => VectorLightInvalidation.RosterChanged(__instance);
}

[HarmonyPatch(typeof(GlowGrid), nameof(GlowGrid.DeregisterTerrain))]
public static class Patch_VectorLightTerrainDeregistered
{
    static void Postfix(GlowGrid __instance) => VectorLightInvalidation.RosterChanged(__instance);
}

// The two blocker writes are called only from Verse.Building's SpawnSetup and DeSpawn, gated on
// ThingDef.blockLight — so patching them here means §27's occluders and vanilla's are, by
// construction, the same set of cells changing at the same moment.
[HarmonyPatch(typeof(GlowGrid), nameof(GlowGrid.LightBlockerAdded))]
public static class Patch_VectorLightBlockerAdded
{
    static void Postfix(GlowGrid __instance, IntVec3 cell) =>
        VectorLightInvalidation.BlockerChanged(__instance, cell);
}

[HarmonyPatch(typeof(GlowGrid), nameof(GlowGrid.LightBlockerRemoved))]
public static class Patch_VectorLightBlockerRemoved
{
    static void Postfix(GlowGrid __instance, IntVec3 cell) =>
        VectorLightInvalidation.BlockerChanged(__instance, cell);
}

// §27e's invalidation, and the one place §27 hooks something that is not a glow-grid write.
//
// WHY A NEW HOOK WAS NEEDED AT ALL. The two blocker patches above are the whole of §27's geometry
// invalidation, and they can never fire for a door opening: vanilla writes lightBlockers once in
// Building.SpawnSetup and clears it in DeSpawn, and Building_Door.DoorOpen sets openInt, clears the
// reachability cache, fires this notification -- and touches the glow grid not at all. So without
// this, vector_light_open_doors would change what BlocksLight answers while nothing ever told the
// field to rebake, and the beam would appear whenever some unrelated edit happened to dirty the
// same lights. That failure looks like a formula bug and is a missing subscription.
//
// WHY MapEvents AND NOT Building_Door.DoorOpen. DoorOpen is `protected virtual`, so a modded door
// class can override it and never call base -- and modded door classes are exactly the population
// this feature is for. Notify_DoorOpened is the notification vanilla itself raises and other
// systems already listen to, so patching it catches every door that behaves like a door. A subclass
// that overrides DoorOpen without calling base is invisible here, but it is equally invisible to
// vanilla's own fog and reachability updates, so it is already broken in ways that are not ours.
//
// Cost: MarkGeometryDirtyAround is bounded to the lights whose window covers the door's cell, the
// same as any wall being built. What is new is the CADENCE -- a wall is built once, a door opens
// every time a pawn walks through it. That is measured in vector_light_open_door rather than
// asserted here.
public static class VectorLightDoorEvents
{
    // Both notifications do the same two things, so they share one body: tell §27 the shape around
    // this cell changed, and -- only under the comparison flag -- tell vanilla's glow grid too.
    //
    // ORDER IS LOAD-BEARING. The glow-grid call is made LAST, because LightBlockerAdded/Removed are
    // themselves patched above and will invalidate us a second time. Doing our own invalidation
    // first means the field is dirty regardless of which flags are on, rather than depending on the
    // comparison flag to provoke it.
    private static void DoorStateChanged(Building_Door door, bool nowOpen)
    {
        if (door == null)
        {
            return;
        }

        Map map = door.Map;
        if (map == null)
        {
            return;
        }

        if (CelestialLightingFeatures.VectorLightOpenDoors)
        {
            // NOT a blocker move, even though this is the first tick of a swing. A door about to
            // slide is still whatever it was — the whole-cell grid changes when the aperture leaves
            // zero, which is a step or two later, and the memo notices that by re-reading the door
            // rather than by being told. If the door was BUILT rather than opened, LightBlockerAdded
            // fired and already invalidated it.
            VectorLightField.MarkGeometryDirtyAround(map, door.Position, blockerMoved: false);

            // Phase 2: the notification only fires at the START of the slide, so hand the door to the
            // component that will keep dirtying it, once per quantisation step, until the leaves stop
            // moving. Registered on close as well as open — an interrupted door animates back down.
            GameComponent_DoorAperture.Watch(door);
        }

        // The comparison arm: move vanilla's own blocker bit, so gameplay light agrees with the
        // drawn frame instead of merely being disagreed with. Gated separately and shipped off --
        // this is the flag that makes plants grow and pawns see, which nothing else in §27 does.
        // Skipped entirely when the door is see-through, because its bit was never set: SpawnSetup
        // only writes lightBlockers when def.blockLight is true, and clearing a bit vanilla never
        // set, then setting it on close, would make a glass door start blocking gameplay light the
        // first time anyone shut it.
        if (CelestialLightingFeatures.VectorLightDoorGlowBlocker
            && door.def != null && door.def.blockLight)
        {
            if (nowOpen)
            {
                map.glowGrid.LightBlockerRemoved(door.Position);
            }
            else
            {
                map.glowGrid.LightBlockerAdded(door.Position);
            }
        }
    }

    public static void Opened(Building_Door door) => DoorStateChanged(door, nowOpen: true);

    public static void Closed(Building_Door door) => DoorStateChanged(door, nowOpen: false);
}

[HarmonyPatch(typeof(MapEvents), nameof(MapEvents.Notify_DoorOpened))]
public static class Patch_VectorLightDoorOpened
{
    static void Postfix(Building_Door door) => VectorLightDoorEvents.Opened(door);
}

[HarmonyPatch(typeof(MapEvents), nameof(MapEvents.Notify_DoorClosed))]
public static class Patch_VectorLightDoorClosed
{
    static void Postfix(Building_Door door) => VectorLightDoorEvents.Closed(door);
}
