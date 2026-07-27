using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace CelestialLighting;

// §15's other half: making the sun-shadow mesh rebuild when a ROOF changes.
//
// The problem. `SectionLayer_SunShadows`'s constructor sets
//
//     relevantChangeTypes = MapMeshFlagDefOf.Buildings;
//
// and nothing else — the layer has never had a reason to care about roofs, because vanilla's only
// shadow casters are edifices. Now that EaveShadowGrid folds roof state into a cell's caster
// height, a roof appearing or disappearing changes the mesh, but `RoofGrid.SetRoof` dirties only
// `MapMeshFlagDefOf.Roofs` (decompiled to confirm). Without this patch, building a porch roof
// produces no shadow until something unrelated happens to dirty that section's Buildings flag —
// which reads exactly like the feature is broken.
//
// Why a constructor Postfix. Perspective: Eaves solves the same problem by transpiling
// `MapDrawer.MapMeshDirty` call sites inside `RoofGrid.SetRoof` AND `Building.SpawnSetup` to force
// both flags. That is two hot, widely-patched vanilla methods rewritten to fix a subscription. The
// subscription is the actual thing that is wrong, so we change that instead: one Postfix on a
// constructor no other mod has any reason to touch, and `Building.SpawnSetup` needs nothing at all
// because it already dirties Buildings, which this layer already listens to.
//
// `relevantChangeTypes` is a public ulong field on MapDrawLayer and MapMeshFlagDef converts to it
// implicitly, so OR-ing is a plain widening of the subscription — it can only cause extra
// regenerates, never suppress one another mod is relying on.
//
// What those extra regenerates cost. This is one of four separate places that add work to a
// map-mesh dirty flag (the other three are SectionLayer_NightDesaturation, SectionLayer_EaveShade
// and Patch_IndoorSkyOcclusion), and no one of the four can show the total — DESIGN.md §16 has the
// flag-to-layers table and the live timings. Two things there qualify the paragraph above: the
// widened layer costs 26 µs per section regenerate (6% of what the mod adds to a `Roofs` flag), and
// `Roofs` is not the rare player-initiated flag it sounds like — `GlowGrid.DirtyCell` raises it
// alongside `GroundGlow`, so this widening also rebuilds the sun-shadow mesh every time a lamp
// toggles, which can never change it. Cheap, but the one strictly-wasted regenerate we introduce.
//
// Known limitation (shared with Perspective: Eaves, and not introduced by us): a cell's eave status
// depends on its ROOM, and enclosing a room can flip cells that are nowhere near the wall that
// closed it — sealing a doorway makes an entire porch stop being a porch, but only the doorway's
// section gets dirtied. Chasing that would mean hooking region/room recalculation, which is a far
// more invasive and far hotter path than this feature is worth; any subsequent roof or building
// edit in the affected section resolves it. Documented in DESIGN.md §15 rather than papered over.
[HarmonyPatch]
public static class Patch_ShadowRoofInvalidation
{
    // SectionLayer_SunShadows is internal, so it can only be named by string — the same reason
    // Patch_ShadowMeshPerimeter and Patch_SunShadowAxisInvalidation both use AccessTools.TypeByName.
    static MethodBase TargetMethod() =>
        AccessTools.Constructor(
            AccessTools.TypeByName("Verse.SectionLayer_SunShadows"), new[] { typeof(Section) });

    static void Postfix(MapDrawLayer __instance)
    {
        __instance.relevantChangeTypes |= (ulong)MapMeshFlagDefOf.Roofs;
    }
}
