using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// Subsystem 11 (DESIGN.md §11): while a solar-flare condition is active, shift the *night* sky
// toward auroral greens/reds — the visual an actual solar flare would produce and that vanilla's
// SolarFlare condition (GameCondition_DisableElectricity) omits entirely. Visual only: the flare's
// electronics disruption and every other gameplay effect are untouched, and this blends only
// SkyTarget.colors, never SkyTarget.glow — so the brightness value other mods read (Dub's Skylights
// et al.) is unaffected, exactly like Patch_TwilightColor. Deliberately does NOT tint the vanilla
// Aurora condition, which already renders its own shifting sky colours (see AuroraConditions).
//
// Same injection point as Patch_TwilightColor: a Postfix on WeatherWorker.CurSkyTarget. The two
// blend different, non-overlapping things (twilight warms the sky at dusk-glow; this tints it green
// only at deep night and only during a flare), so they stack cleanly regardless of postfix order.
// The auroral colour, night-visibility ramp, and fade are all pure functions in AuroraMath under
// offline unit tests; this file only lifts primitives off live state and lerps the result in.
[HarmonyPatch(typeof(WeatherWorker), nameof(WeatherWorker.CurSkyTarget))]
public static class Patch_AuroraTint
{
    static void Postfix(Map map, ref SkyTarget __result)
    {
        // Feature gate (default on): when off, leave the sky exactly as vanilla/§2/§8 composed it —
        // the faithful pre-feature baseline. Sits before the condition lookup so "off" is a true no-op.
        if (!CelestialLightingFeatures.Aurora)
            return;

        GameCondition driver = AuroraConditions.ActiveTintDriver(map);
        if (driver == null)
            return;

        // Deliberately re-derive sun glow from GenCelestial.CurCelestialSunGlow rather than reading
        // __result.glow, for the same reason Patch_TwilightColor does: the aurora's night-only
        // visibility should track true sun position, not displayed brightness. That matters
        // concretely here because §7 rewrites __result.glow below the horizon with its night floor —
        // exactly the regime an aurora lives in — so reading it would tie aurora visibility to moon
        // phase. (The reason first given here, a weather clamp via maxGlow, was wrong: it is set
        // exactly once in all of vanilla. See DESIGN.md §13.)
        float sunGlow = GenCelestial.CurCelestialSunGlow(map);
        float ramp = AuroraConditions.RampFor(driver);

        float skyStrength = AuroraMath.SkyTintStrength(sunGlow, ramp);
        if (skyStrength <= 0f)
            return;

        float phase = AuroraMath.PhaseFromTicks(Find.TickManager.TicksGame, AuroraMath.ShimmerPeriodTicks);
        AuroraMath.Rgb tint = AuroraMath.AuroralColorAtPhase(phase);
        Color tintColor = new Color(tint.R, tint.G, tint.B);

        float overlayStrength = AuroraMath.OverlayTintStrength(sunGlow, ramp);
        __result.colors.sky = Color.Lerp(__result.colors.sky, tintColor, skyStrength);
        __result.colors.overlay = Color.Lerp(__result.colors.overlay, tintColor, overlayStrength);
    }
}
