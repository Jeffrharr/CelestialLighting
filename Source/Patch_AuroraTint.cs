using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// Subsystem 11 (DESIGN.md §11): while an auroral event is running — a solar flare or vanilla's own
// Aurora condition, and nothing else — shift the *night* sky toward auroral colours. For a flare
// that visual is missing from vanilla entirely (GameCondition_DisableElectricity has no sky render);
// for the Aurora event it is nominally present but almost invisible, because SkyManager composes
// conditions with a per-channel min that discards vanilla's brighter-than-night colour set.
// AuroraConditions carries the full derivation and the measured numbers.
//
// Visual only: the flare's electronics disruption, the aurora's joy bonus, and every other gameplay
// effect are untouched, and this blends only SkyTarget.colors, never SkyTarget.glow — so the
// brightness value other mods read (Dub's Skylights et al.) is unaffected, exactly like
// Patch_TwilightColor.
//
// Same injection point as Patch_TwilightColor: a Postfix on WeatherWorker.CurSkyTarget. The two
// blend different, non-overlapping things (twilight warms the sky at dusk-glow; this tints it only
// at deep night and only during an auroral event), so they stack cleanly regardless of postfix
// order. The night-visibility ramp, fade, and flare shimmer colour are all pure functions in
// AuroraMath under offline unit tests; this file only lifts primitives off live state and lerps the
// result in.
[HarmonyPatch(typeof(WeatherWorker), nameof(WeatherWorker.CurSkyTarget))]
public static class Patch_AuroraTint
{
    static void Postfix(Map map, ref SkyTarget __result)
    {
        // Feature gate (default on): when off, leave the sky exactly as vanilla/§2/§8 composed it —
        // the faithful pre-feature baseline. Sits before the condition lookup so "off" is a true no-op.
        if (!CelestialLightingFeatures.Aurora)
            return;

        // Enclosed map (a Biomes! Caverns cavern, vanilla's undercave). An aurora is an
        // upper-atmosphere effect; a solar flare can be in progress overhead while the map itself is
        // under rock, and vanilla's condition is map-wide rather than sky-aware, so without this the
        // flare tinted the inside of a cave green. See MapSkyMath.
        if (MapSky.IsEnclosed(map))
            return;

        // Sky blacked out right now (issue #35 — Glowforest, a smoke vent, a sun blocker; never an
        // eclipse, see MapSkyMath.ConditionBlacksOutSky). An aurora burns ~100 km up, well above any
        // sulfur cloud, and is hidden by one exactly as it is hidden by a rock ceiling.
        //
        // Partly redundant with AuroraConditions.ActiveTintDriver below, which already stands down on
        // GameConditionManager.IsAlwaysDarkOutside — and deliberately kept anyway, because that guard is
        // narrower on both axes: vanilla's flag counts only PERMANENT blackouts (so it misses a smoke
        // vent's timed one, which is most of issue #35) and does not exclude the eclipse. It stays
        // because its own justification is different: it mirrors what vanilla's GameCondition_Aurora
        // does to itself, and matching vanilla there is worth keeping independently of this gate.
        if (MapSky.SkyBlackedOut(map))
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

        // Two gates, from two subsystems, both threaded into AuroraMath rather than early-returned here
        // so each reason sits beside the strength it modifies.
        //
        // §18's vacuum gate (Vacuum.cs): from a 200 km platform you are above the 630 km emission
        // sheet's underside view, so a full-screen tint is the wrong presentation at any intensity.
        //
        // §11a's curtain: whether the ribbons are carrying the aurora decides which peak this flat tint
        // runs at — on its own it goes to 0.18, underneath ribbons it steps back to vanilla's 0.075 as a
        // base wash. See AuroraConditions.CurtainEnabled for why that is asked of the feature flags
        // rather than of the curtain's current alpha.
        bool inVacuum = Vacuum.InVacuumForMap(map);
        bool curtained = AuroraConditions.CurtainEnabled;

        float skyStrength = AuroraMath.SkyTintStrength(sunGlow, ramp, curtained, inVacuum);
        if (skyStrength <= 0f)
            return;

        // Hue depends on which event is driving: vanilla's Aurora lends us its own cycling palette
        // colour, a solar flare gets AuroraMath's green<->red emission-line shimmer. The branch lives
        // in AuroraConditions because only the vanilla-aurora arm touches live condition state.
        Color tintColor = AuroraConditions.TintColorFor(driver, Find.TickManager.TicksGame);

        float overlayStrength = AuroraMath.OverlayTintStrength(sunGlow, ramp, curtained, inVacuum);
        __result.colors.sky = Color.Lerp(__result.colors.sky, tintColor, skyStrength);
        __result.colors.overlay = Color.Lerp(__result.colors.overlay, tintColor, overlayStrength);
    }
}
