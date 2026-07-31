using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// §11a's draw hook: advances and draws the aurora curtain (AuroraCurtainOverlay) while one of §11's two
// auroral driver conditions is running on the visible map.
//
// WHY NOT GameCondition.SkyOverlays, WHICH IS THE OBVIOUS ANSWER. Vanilla's mechanism for an animated
// sky visual attached to a condition is to override GameCondition.SkyOverlays(Map) and return a list;
// SkyManager.UpdateOverlays walks every active condition's list each frame. Issue #42 proposed hooking
// exactly that, and decompiling 1.6 shows it cannot work for a mod that does not own the condition:
//
//   1. UpdateOverlays never DRAWS anything. All it does with that list is call SetOverlayColor on each
//      entry. Drawing happens in GameCondition.GameConditionDraw, which is virtual and per-condition —
//      and neither GameCondition_Aurora nor GameCondition_DisableElectricity (SolarFlare) overrides it,
//      because neither has an overlay in vanilla. Only GameCondition_UnnaturalDarkness does.
//   2. The colour it sets is not ours to choose. It passes `curSky.colors.overlay` with
//      `alpha = gameCondition.SkyTargetLerpFactor(map)`. ForcedOverlayColor can override the RGB, but
//      the alpha is still clobbered — which would throw away the night-visibility-and-fade ramp that
//      makes this a night effect at all, replacing it with the condition's own unrelated transition.
//   3. SkyOverlays is virtual, so patching the base would silently stop applying to any condition that
//      overrides it — a fragile hook even setting aside 1 and 2.
//
// So we own the whole lifecycle instead, and hook GameConditionManager.GameConditionManagerDraw: the
// exact point in Map.MapUpdate where vanilla draws condition overlays, non-virtual, and already inside
// the `drawingMap && Find.CurrentMap == this` branch — so off-screen and non-current maps cost nothing.
//
// A MapComponent would also have worked and needed no patch at all, but Verse.Map.ExposeComponents
// scribes its component list into every save, so adding one is close to irreversible — see the
// MapComponent_SunShadowAxis tombstone for the two red errors per map that deleting one later costs.
// A postfix on a method we do not modify is the cheaper side of that trade.
[HarmonyPatch(typeof(GameConditionManager), nameof(GameConditionManager.GameConditionManagerDraw))]
public static class Patch_AuroraCurtainDraw
{
    static void Postfix(GameConditionManager __instance, Map map)
    {
        // GameConditionManagerDraw recurses into its Parent (the world-level manager) after drawing its
        // own conditions, so this postfix fires once per manager in the chain — twice for an ordinary
        // map. Comparing identity keeps us to the map's own pass; without it the curtain advances its
        // clock and regenerates twice per frame, at double cost, for no visible difference.
        if (map == null || map.gameConditionManager != __instance)
            return;

        // THE DRIVER IS RESOLVED ONCE, then handed to the strength calculation. This used to read the
        // other way round — CurrentCurtainStrength first, which resolves the driver internally, then
        // ActiveTintDriver again for the colour — so every drawn frame walked the condition list twice
        // to arrive at the same condition. The gates below are in the same order and short-circuit at
        // the same points, so nothing about WHEN the curtain draws has changed; it only asks once.
        //
        // Same driver and same colour source as §11, so the ribbons and the flat wash underneath cannot
        // disagree about tonight's hue. During a vanilla Aurora event that is the colour the event is
        // already cycling through; a solar flare gets AuroraMath's green/red emission-line shimmer.
        if (!AuroraConditions.CurtainEnabled)
            return;

        GameCondition driver = AuroraConditions.ActiveTintDriver(map);
        if (driver == null)
            return;

        // Ordering with §11's flat tint does not matter: that one blends SkyTarget.colors inside
        // WeatherWorker.CurSkyTarget, while this composites an additive layer at draw time. They meet on
        // screen, not in a shared value.
        float strength = AuroraConditions.CurtainStrengthFor(map, driver);
        if (strength <= 0f)
            return;

        Color tint = AuroraConditions.TintColorFor(driver, Find.TickManager.TicksGame);

        if (AuroraCurtainOverlay.Instance.Advance(
                map, Find.TickManager.TicksGame, strength, tint, AuroraFieldRegistry.Active.TintWeight))
        {
            AuroraCurtainOverlay.Instance.DrawOverlay(map);
        }
    }
}
