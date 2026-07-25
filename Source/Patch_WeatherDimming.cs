using HarmonyLib;
using Verse;

namespace CelestialLighting;

// Weather dimming (DESIGN.md §13). Clouds, rain and storms darken the rendered sky in proportion to
// how much deck is overhead and how hard it is coming down.
//
// This closes a gap that is easy to miss because it looks like vanilla already handles it. It does
// not: WeatherWorker.CurSkyTarget sets `result.glow = Math.Min(CurCelestialSunGlow(map),
// def.maxGlow)`, and `maxGlow` defaults to 1.0 and is set exactly ONCE in all of vanilla's XML —
// Odyssey's Overcast, at 0.95. Rain, Fog, Blizzard, thunderstorms, Sandstorm and BlindFog all leave
// it at the default, so vanilla's weather changes the sky's COLOUR but never its brightness.
//
// COLOUR-ONLY, and that is the whole design (see WeatherDimmingMath's header for the channel table).
// SkyTarget.glow feeds SkyManager.CurSkyGlow -> GlowGrid.GroundGlowAt, which drives plant growth
// (PlantProperties.growMinGlow), solar panel output and pawn psych-glow. SkyColorSet.sky/.overlay
// feed MatBases.LightOverlay.color and the weather overlays, which are pure render. We write only
// the second, so CLAUDE.md's "scope is visual/atmospheric only" holds with no asterisk: under every
// weather, at every strength, gameplay lighting stays bit-for-bit vanilla.
//
// That is also not a workaround — it is the channel vanilla itself uses for weather. Clear skies
// ship skyColorsDay.sky = (1,1,1); every overcast/wet weather ships (0.8,0.8,0.8). We deepen that
// existing 20% and make it scale with storm intensity rather than being a flat two-state step.
//
// ORDERING. Because this touches only .colors, it is order-independent against every other postfix
// on CurSkyTarget and needs no HarmonyPriority: §7 writes .glow and never reads .colors; §2/§8/§11
// blend .colors but recompute their own brightness from the solar simulator rather than reading
// anything we write. §9 is the one patch that consumes our result, and it does so by calling
// WeatherDimming.DimmingFor(map) itself rather than by observing a value we left behind — a shared
// adapter read instead of an ordering dependency, which is what makes that coupling robust.
[HarmonyPatch(typeof(WeatherWorker), nameof(WeatherWorker.CurSkyTarget))]
public static class Patch_WeatherDimming
{
    static void Postfix(Map map, ref SkyTarget __result)
    {
        // The feature gate lives inside WeatherDimming.DimmingFor, so "off" returns 0 here and the
        // early-return below makes it a true no-op — each WeatherDef's palette is left exactly as
        // vanilla renders it, which is the faithful pre-feature baseline the harness A/Bs against.
        float dimming = WeatherDimming.DimmingFor(map);
        if (dimming <= 0f)
            return;

        // Multiply, never assign. The sky is already carrying §2's twilight warmth, §8's colour
        // temperature and possibly §11's aurora or §12's blood-moon tint by now; scaling preserves
        // all of them and simply darkens whatever they produced, exactly as a cloud deck would.
        float tint = WeatherDimmingMath.SkyTintFactor(dimming);
        __result.colors.sky = ScaleRgb(__result.colors.sky, tint);
        __result.colors.overlay = ScaleRgb(__result.colors.overlay, tint);

        // __result.glow is deliberately NOT touched — see the header. §9 gets its weather-aware
        // brightness from WeatherDimmingMath.ApparentGlow instead, which keeps the gameplay value
        // and the perceived value cleanly separate.
    }

    // Scales RGB and leaves ALPHA ALONE. Emphatically not `color * factor`, which Unity defines as
    // scaling all four channels: SkyManager assigns colors.sky straight to MatBases.LightOverlay.color,
    // and that material's alpha is how much of the lighting overlay is drawn at all — vanilla writes
    // (1,1,1,0) to switch it off entirely for disableSkyLighting biomes. Scaling alpha down would
    // therefore fade the darkening overlay OUT and make heavy weather render *brighter*, the exact
    // opposite of the intent.
    private static UnityEngine.Color ScaleRgb(UnityEngine.Color color, float factor) =>
        new UnityEngine.Color(color.r * factor, color.g * factor, color.b * factor, color.a);
}
