using System.Collections.Generic;
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
        }

        // Registered on close as well as open — an interrupted door animates back down — and NOT
        // gated on VectorLightOpenDoors, because the glow-grid half below needs the same clock: this
        // notification fires at the START of the slide and the bit moves at the END of it.
        GameComponent_DoorAperture.Watch(door);

        // A CLOSING DOOR STOPS BEING A HOLE IMMEDIATELY, which is why this is asked here and the
        // opening half is not. `nowOpen` is the end the door is heading for, so on a close the
        // predicate has already gone false and this restores the bit on the first tick of the swing;
        // on an OPEN it is still false until the leaves finish, and the component above is what asks
        // again when they do. See DoorApertureMath.GlowGridHoleWanted for why both edges err toward
        // blocked.
        ReconcileGlowBlocker(door);
    }

    // Make vanilla's own light-blocker bit agree with what the door is currently doing.
    //
    // THE ONE PLACE THE BIT IS WRITTEN, called from the door's own notifications, from the aperture
    // clock when a swing finishes, and from map load. Three callers reaching the same predicate is
    // the point: the bit is a single piece of state with no history, so a caller that decided for
    // itself would eventually disagree with another one and leave a door lighting a room it does not
    // open onto — a bug with no symptom until somebody looks at the right wall.
    //
    // IDEMPOTENT BY CONSTRUCTION. GlowGrid.lightBlockers is a NativeBitArray and both calls are a
    // plain Set, so writing the same value twice costs a dirty-lights pass and changes nothing. That
    // is what lets every caller here be unconditional rather than having to know what the last one
    // did.
    public static void ReconcileGlowBlocker(Building_Door door)
    {
        if (door == null)
        {
            return;
        }

        Map map = door.Map;
        if (map == null || door.def == null)
        {
            return;
        }

        // OWNERSHIP FIRST, AND IT IS NOT THE SAME QUESTION AS THE PREDICATE'S. A see-through door
        // answers "not a hole" for a reason that has nothing to do with whether it is open — its bit
        // was never set, so it is not ours to write — and falling through to LightBlockerAdded would
        // make glass doors start blocking gameplay light the first time anyone shut one. The
        // predicate says the same thing from its own inputs; asking here as well is what stops the
        // `else` branch below from acting on it.
        if (!door.def.blockLight)
        {
            return;
        }

        // The aperture our own fan is drawing, not the door's raw animation — see
        // DoorApertureMath.RenderedOpenFraction for why those are different questions.
        float rendered = DoorApertureMath.RenderedOpenFraction(
            CelestialLightingFeatures.VectorLightDoorAperture, DoorAccess.OpenFraction(door));

        // THE FLAG IS PART OF THE ANSWER, NOT A GUARD ON ASKING IT, and having it as a guard was a
        // real bug rather than a stylistic choice. Returning early when the feature is off leaves
        // whatever holes it had already opened standing open, so turning it off did NOT restore
        // vanilla — it froze the map in whatever state the last door event left. This repo's rule is
        // that a flag turned off reproduces the pre-feature behaviour EXACTLY, and here that
        // behaviour is "a door's cell always blocks", which falls out of folding the flag into
        // `hole` and letting the same write run.
        //
        // This is GAMEPLAY light — plant growth, pawn vision, work speed and every mod reading
        // GroundGlowAt move with it — so it is the one term in §27 that stays behind its own flag
        // rather than riding on VectorLights.
        bool hole = CelestialLightingFeatures.VectorLightDoorGlowBlocker
            && DoorApertureMath.GlowGridHoleWanted(door.def.blockLight, door.Open, rendered);

        if (hole)
        {
            map.glowGrid.LightBlockerRemoved(door.Position);
        }
        else
        {
            map.glowGrid.LightBlockerAdded(door.Position);
        }
    }

    // WHAT THE FLAGS CHANGING HAS TO CALL, and its absence is what made the first live run of this
    // feature photograph a frame bit-identical to the arm before it.
    //
    // Every other caller here is provoked by a DOOR moving. These flags change the ANSWER for doors
    // that are not moving and may never move again: a scenario flips the feature on with a door
    // already standing open, and a player toggles it in the settings screen with half the base's
    // doors held open. Nothing in the door's own lifecycle fires, VectorLightRedraw.ForceRebuild
    // rebuilds OUR geometry and never touches vanilla's blocker bits, and the map keeps the previous
    // answer until each door happens to be used again — which in a paused scenario is never.
    public static void ReconcileAllDoors()
    {
        List<Map> maps = Find.Maps;

        if (maps == null)
        {
            return;
        }

        for (int i = 0; i < maps.Count; i++)
        {
            ReconcileDoors(maps[i]);
        }
    }

    // BOTH LISTERS, and the colonist one alone is a real miss rather than a tidy-up. Ancient ruins,
    // abandoned bases and every quest map arrive with doors nobody owns, and a door left standing
    // open in generated content is exactly the case that never gets walked through to heal itself.
    public static void ReconcileDoors(Map map)
    {
        if (map?.listerBuildings == null)
        {
            return;
        }

        Reconcile(map.listerBuildings.allBuildingsColonist);
        Reconcile(map.listerBuildings.allBuildingsNonColonist);
    }

    // Doors are a small fraction of a colony's buildings and this runs on map load and on a settings
    // change, so the sweep is not worth narrowing — narrowing it would mean keeping a second list in
    // step with vanilla's, which is the kind of bookkeeping that goes stale silently.
    private static void Reconcile(List<Building> buildings)
    {
        for (int i = 0; i < buildings.Count; i++)
        {
            if (buildings[i] is Building_Door door)
            {
                ReconcileGlowBlocker(door);
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

// The state a door's blocker bit is in after a LOAD, and the one case the notifications cannot cover.
//
// WHY IT IS BROKEN WITHOUT THIS. A door that was open when the game was saved comes back with
// `openInt` true and raises no Notify_DoorOpened — the notification is an EVENT, and nothing
// happened. Meanwhile Building.SpawnSetup has written def.blockLight into lightBlockers exactly as it
// does for a shut door. So every door left open across a save reloads as a light blocker while the
// artwork shows it standing open, and stays that way until somebody next walks through it.
//
// That was recorded as an acceptable rough edge while this was a comparison arm nobody shipped. It is
// not acceptable now: "an open door is a wall gap" that quietly stops being true on load is worse
// than not making the claim, because the failure is invisible until a player notices one room is
// darker than it was before they saved.
//
// WHY Map.FinalizeInit. It runs once per map after everything on it has spawned, on load and on
// generation alike, which is exactly the moment the grid and the doors can first disagree. Doing it
// from SpawnSetup instead would fight vanilla's own write on the same tick and depend on patch order.
[HarmonyPatch(typeof(Map), nameof(Map.FinalizeInit))]
public static class Patch_VectorLightDoorBlockersOnLoad
{
    static void Postfix(Map __instance) => VectorLightDoorEvents.ReconcileDoors(__instance);
}

