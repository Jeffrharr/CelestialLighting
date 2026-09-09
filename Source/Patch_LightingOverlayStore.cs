using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// §27's edit of the lighting overlay, made on vanilla's colour array BEFORE vanilla stores it,
// instead of on a copy read back out of the mesh afterwards.
//
// WHAT THE POSTFIX WAS PAYING. SectionLayer_LightingOverlay.GenerateLightingOverlay builds a fresh
// Color32[] per section and stores it with one `subMesh.mesh.colors32 = array`. Patch_VectorLightSuppress
// then ran after Regenerate, read the array back (`mesh.colors32`: a native copy out plus a fresh
// managed array), edited the copy, and stored it again (a second native copy in). Per section that
// is two native transitions and 2.4 KB of garbage the edit never needed, because the array vanilla
// just built is exactly the one the edit wants -- it is simply out of reach by the time a postfix
// runs: a local of a private static method, already stored and dropped.
//
// WHAT THIS DOES. The transpiler replaces vanilla's one store with a call to Store below, which
// hands the array to the same edit the postfix would have made and then performs vanilla's store
// itself. Nothing about the edit changes; only where it happens. The bytes on the mesh are the same
// by construction: the postfix path edited a copy of this array and wrote the copy back.
//
// WHY A TRANSPILER, this mod's second. No prefix can reach the array because it does not exist
// yet, and no postfix can because it is gone. The seam is the store, and the store is one IL
// instruction. The transpiler finds it by the method it calls (Mesh.set_colors32) rather than by
// position, and touches nothing else, so another mod's transpiler on the same method -- Biomes!
// Caverns has one, on the roof reads -- composes with it as long as the store survives. If the seam
// is missing the transpiler returns the method as it found it and logs once, and the postfix,
// finding the hook did not run for its mesh, does what it always did; the saving is lost and
// nothing else is. ApiCompatibilityTests pins the seam and the argument order Store depends on.
//
// WHAT IT DOES NOT TOUCH. Bake() calls the same method for free-standing previews, with `centered`
// set and a filter; those never went through the postfix (it patches Regenerate, not this method)
// and do not go through the edit now either. Patch_IndoorSkyOcclusion's postfix still reads the
// mesh back for its alpha pass; that round trip is its own and is unchanged here.
//
// FLAG OFF is the postfix path exactly as before: Store makes vanilla's store and nothing else, and
// the postfix reads the mesh back and edits it. See CelestialLightingFeatures.VectorLightOverlayInPlace.
[HarmonyPatch(typeof(SectionLayer_LightingOverlay), "GenerateLightingOverlay")]
public static class Patch_LightingOverlayStore
{
    // The mesh Store last edited for, so the postfix can tell "the hook did this section" from
    // "the hook never ran". A reference rather than a bool because a bool left set by a regenerate
    // whose postfix chain was cut short would silently skip the NEXT section's edit; a stale
    // reference cannot match a different section's mesh.
    private static Mesh handled;

    // How many stores the hook edited through, and how many sections the postfix found it had not.
    // The second is the number that says the transpiler did not apply, and it must read zero on
    // every arm with the flag on: a miss is the postfix path silently paying the round trip the
    // arm claims to have removed.
    public static long Stores;
    public static long Misses;

    public static void ResetTelemetry()
    {
        Stores = 0;
        Misses = 0;
    }

    // Whether the hook edited this mesh's colours on the store just made. Clears on read, so it
    // answers once and only for the regenerate that set it.
    public static bool TakeHandled(Mesh mesh)
    {
        bool did = handled != null && handled == mesh;
        handled = null;
        return did;
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo setColors = AccessTools.PropertySetter(typeof(Mesh), nameof(Mesh.colors32));
        MethodInfo store = AccessTools.Method(typeof(Patch_LightingOverlayStore), nameof(Store));

        bool replaced = false;

        foreach (CodeInstruction instruction in instructions)
        {
            // Only the first store is ours. Vanilla makes one; another mod's transpiler adding a
            // second would more likely be storing something of its own than restating vanilla's.
            bool isSeam = !replaced && instruction.Calls(setColors);

            if (!isSeam)
            {
                yield return instruction;
            }
            else
            {
                replaced = true;

                // The stack here is [mesh][array]. Push the method's own map, rect, centered and
                // filter -- arguments 0, 2, 4 and 5; 1 is the LayerSubMesh Store has no use for and
                // 3 is the ref int firstCenterInd -- and call Store, which pops all six and pushes
                // nothing, exactly as the setter did. Any branch aimed at the store now lands on
                // the first pushed argument rather than jumping over a half-built stack; vanilla
                // has no such branch, and this is what survives one appearing.
                CodeInstruction first = new CodeInstruction(OpCodes.Ldarg_0);
                instruction.MoveLabelsTo(first);
                instruction.MoveBlocksTo(first);

                yield return first;
                yield return new CodeInstruction(OpCodes.Ldarg_2);
                yield return CodeInstruction.LoadArgument(4);
                yield return CodeInstruction.LoadArgument(5);
                yield return new CodeInstruction(OpCodes.Call, store);
            }
        }

        if (!replaced)
            Log.Warning("[CelestialLighting] SectionLayer_LightingOverlay.GenerateLightingOverlay no "
                + "longer stores mesh.colors32 where expected; the lighting overlay edit falls back "
                + "to its postfix and pays the mesh round trip.");
    }

    // Vanilla's store, with the edit in front of it. Public because Harmony emits a call to it.
    public static void Store(
        Mesh mesh, Color32[] colors, Map map, CellRect rect, bool centered, Predicate<int> filter)
    {
        if (Wanted(centered, filter) && colors != null)
        {
            handled = mesh;
            Stores++;
            Patch_VectorLightSuppress.EditInPlace(map, colors, rect);
        }

        mesh.colors32 = colors;
    }

    // A Regenerate, not a Bake: Regenerate calls with the defaults, Bake with `centered` set and a
    // filter. Bake's free-standing previews never went through the postfix and are not ours now.
    private static bool Wanted(bool centered, Predicate<int> filter)
    {
        return CelestialLightingFeatures.VectorLights
            && CelestialLightingFeatures.VectorLightOverlayInPlace
            && !centered
            && filter == null;
    }
}
