using HarmonyLib;
using Verse;

namespace CelestialLighting;

// The accessibility brightness floor's live wiring. Thin adapter: it reads the final sky glow off
// the just-updated SkyManager, hands it to the pure BrightnessFloorMath.Apply, and writes the result
// back through the canonical setter. All of the actual "clamp upward" logic lives in the pure core
// and is unit-tested; this file only bridges live state.
//
// Why SkyManagerUpdate, and why Priority.Last:
//   SkyManagerUpdate() is the single place per frame that recomputes curSkyGlowInt from the current
//   sky target (verified by decompiling vanilla 1.6). Running as a *postfix* means we see the fully
//   resolved glow — after weather dimming and, once it lands, §7's pitch-black-night darkening.
//   DESIGN.md requires this floor to be "applied as the last step, after §7's floors and any weather
//   dimming", so we take Priority.Last to sort after any other postfix (including §7's, which shares
//   this method) that our own assembly registers.
//
// Why lift CurSkyGlow specifically:
//   CurSkyGlow is RimWorld's canonical "how bright is it right now" value — GlowGrid.GroundGlowAt
//   (and mods like Dub's Skylights) read it, so lifting it here brightens the visible map lighting
//   through the same path §7 darkens it. Writing through ForceSetCurSkyGlow (rather than poking a
//   private field) is exactly how §7 composes with third-party glow readers, so the floor and §7 are
//   deliberate mirror images sharing one seam.
//
// Opt-in and off by default: with brightnessFloorEnabled == false, BrightnessFloorMath.Apply is the
// identity, so this postfix is a guaranteed no-op on vanilla brightness until a player asks for it.
// This is an accessibility *brightening*, never a darkness penalty — consistent with the mod's
// visual-only scope.
[HarmonyPatch(typeof(SkyManager), nameof(SkyManager.SkyManagerUpdate))]
public static class Patch_BrightnessFloor
{
    [HarmonyPriority(Priority.Last)]
    static void Postfix(SkyManager __instance)
    {
        CelestialLightingSettings settings = CelestialLightingSettingsMod.Settings;

        // Settings can be null only if this postfix somehow runs before the Mod subclass constructed
        // (it does not in practice — the Mod is built during loading, long before any map ticks), but
        // guarding keeps a would-be NullReferenceException from ever touching the render loop.
        if (settings == null || !settings.brightnessFloorEnabled)
            return;

        float current = __instance.CurSkyGlow;
        float floored = BrightnessFloorMath.Apply(current, settings.brightnessFloor, enabled: true);

        // Only write when the floor actually lifted something, so bright daylight frames skip the
        // setter (and any overlay recomputation it triggers) entirely.
        if (floored > current)
            __instance.ForceSetCurSkyGlow(floored);
    }
}
