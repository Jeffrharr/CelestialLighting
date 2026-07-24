using HarmonyLib;
using Verse;

namespace CelestialLighting;

[StaticConstructorOnStartup]
public static class CelestialLightingMod
{
    // StaticConstructorOnStartup ensures this runs after all mods are loaded and RimWorld's own
    // static initialization is complete. PatchAll() scans this assembly for [HarmonyPatch]
    // classes and applies them.
    static CelestialLightingMod()
    {
        new Harmony("celestiallighting").PatchAll();
    }
}
