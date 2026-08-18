using HarmonyLib;
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

    public static void BlockerChanged(GlowGrid grid, IntVec3 cell)
    {
        VectorLightField.MarkGeometryDirtyAround(GlowGridAccess.GetMap(grid), cell);
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
