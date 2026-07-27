using System.Reflection;
using HarmonyLib;
using Verse;

namespace CelestialLighting;

// Subscribes SectionLayer_SunShadows to this mod's private CL_SunShadowAxis dirty flag, so
// MapComponent_SunShadowAxis can rebuild the shadow mesh — and only the shadow mesh — when the
// baked per-section length variation goes stale (DESIGN.md section 3).
//
// This is the same one-line widening, on the same constructor, as Patch_ShadowRoofInvalidation
// (section 15), and it is a separate file for the same reason the two subscriptions have separate
// justifications: that one exists because roofs can change a caster's height, this one because the
// sun moves. Harmony runs both Postfixes; each only ORs a bit into a public ulong field, so they
// cannot interfere with each other or suppress a subscription another mod is relying on.
//
// Unlike the Roofs widening, this flag is raised by nothing but us, so it adds no work to any
// vanilla edit — see the flag-to-layers table in DESIGN.md section 16, which this deliberately does
// not change: CL_SunShadowAxis has exactly one subscriber and vanilla never raises it.
[HarmonyPatch]
public static class Patch_SunShadowAxisInvalidation
{
    // SectionLayer_SunShadows is internal, so it can only be named by string — the same reason
    // Patch_ShadowMeshPerimeter and Patch_ShadowRoofInvalidation both use AccessTools.TypeByName.
    static MethodBase TargetMethod() =>
        AccessTools.Constructor(
            AccessTools.TypeByName("Verse.SectionLayer_SunShadows"), new[] { typeof(Section) });

    static void Postfix(MapDrawLayer __instance)
    {
        __instance.relevantChangeTypes |= (ulong)CelestialLightingDefOf.CL_SunShadowAxis;
    }
}
