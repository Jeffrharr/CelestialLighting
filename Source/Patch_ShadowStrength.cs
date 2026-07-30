using HarmonyLib;
using RimWorld;
using Verse;

namespace CelestialLighting;

// GenCelestial.CurShadowStrength(Map) is what actually drives the visible shadow: SkyManager
// reads it directly as the alpha channel of the shader global it sets every frame
// (SetSunShadowVector writes Vector4(vector.x, 0, vector.y, CurShadowStrength(map)) to
// MapSunLightDirection) and to tint MatBases.SunShadow/SunShadowFade — none of that reads
// GetLightSourceInfo's LightInfo.intensity, which Patch_ShadowDirection patches separately.
// Vanilla's CurShadowStrength (Clamp01(Abs(CurCelestialSunGlow(map) - 0.6f) / 0.15f)) stays near
// full strength through the night (its "moonlight" reading), so patching only
// Patch_ShadowDirection was not enough to actually suppress moon shadows in-game — the shader was
// still being told to render at near-full opacity regardless of what LightInfo.intensity said.
//
// This Postfix overrides the result with the same elevation-based intensity Patch_ShadowDirection
// uses (via the shared SolarPosition.ElevationForMap), so the two can never disagree: 0 once the
// sun drops past Formulas.AtmosphericRefractionDegrees, ramping to full strength over the same
// window above it.
//
// Once the sun is down, the moon can still cast a (much fainter) shadow: we hand off to the same
// MoonPosition.ShadowForMap adapter Patch_ShadowDirection uses for its night branch, so the alpha
// set here always matches the vector set there. If the moon is down or new, ShadowForMap returns
// null and the night stays shadowless — exactly what the sun-only intensity (0 at night) already
// gave.
//
// §6b note: the elevation test below is now only "who is the caster", not "can the moon be seen".
// ShadowForMap answers the second question itself, from the moonlight-to-skylight ratio, and returns
// null through the whole of civil twilight because the sky is still hundreds of times brighter than
// the moon. So the two branches no longer meet at a step: the sun's ramp has already reached 0 by
// the time this crosses over, and the moon's is still 0 for several degrees after it.
[HarmonyPatch(typeof(GenCelestial), nameof(GenCelestial.CurShadowStrength))]
public static class Patch_ShadowStrength
{
    static void Postfix(Map map, ref float __result)
    {
        // Shadowless map (Biomes! Caverns' cavern biomes, and any biome declaring vanilla's
        // disableShadows). Vanilla itself honours this flag at SectionLayer_SunShadows.Visible, so
        // nothing we compute here would ever be drawn — and Biomes! Caverns additionally Prefix-skips
        // that layer's DrawLayer outright. Reading vanilla's own field is what stops our shadow
        // subsystems from disagreeing with vanilla about which maps are shadowless.
        //
        // Deliberately NOT MapSky.IsEnclosed: orbit has no ceiling and the harshest sunlight in the
        // game, and gating shadows on the sky question would delete them from exactly the wrong place.
        if (!MapSky.DrawsShadows(map))
            return;

        // Sky blacked out right now (issue #35 — Glowforest's permanent sulfur overcast, a smoke vent's
        // 3-days-in-7 one, Royalty's sun blocker; never an eclipse, see
        // MapSkyMath.ConditionBlacksOutSky). No direct beam arrives, so nothing casts a shadow.
        //
        // THIS IS THE PATCH THAT ACTUALLY REMOVES THE SHADOW, and it has to WRITE zero rather than
        // return — returning would hand back vanilla's own value, which is near full strength at noon
        // because CurShadowStrength is a pure function of CurCelestialSunGlow with no condition term at
        // all. Zero here reaches the screen twice over: SkyManager sets
        // `MatBases.SunShadow.color = Color.Lerp(Color.white, curSky.colors.shadow, CurShadowStrength)`,
        // and a white multiply is an invisible shadow, and it writes the same zero into the
        // MapSunLightDirection shader global.
        //
        // Vanilla LOOKS like it handles this and does not. GameCondition_NoSunlight's EclipseSkyColors
        // carries Color.white as its shadow colour, which reads as "suppress the shadow" — but
        // SkyManager composes conditions with SkyColorSet.LerpDarken, i.e. `A.shadow.Min(B.shadow)`
        // per channel, and white is the per-channel MAXIMUM. So that white is a no-op and a
        // blacked-out map kept its full-strength daytime shadows in vanilla too. Ours were worse only in
        // that we overwrite the strength with an elevation-derived value and so could not accidentally
        // inherit a dark-sky reduction that was never there. Measured on a live blacked-out Glowforest
        // with the sun low: MatBases.SunShadow.color.r read 0.778 — a 22% ground darkening with crisp
        // sun-angled bands — under a sky whose glow was already zero (sky_blackout_gate.json).
        //
        // This zero is also what silently carries §15b's eave shade with it: Patch_EaveShade reads the
        // finished MatBases.SunShadow.color rather than re-deriving depth, so a white cast band gives a
        // white — invisible — porch shade on the same frame, with no mesh to rebuild.
        if (MapSky.SkyBlackedOut(map))
        {
            __result = 0f;
            return;
        }

        float elevation = SolarPosition.ElevationForMap(map);

        // Weather softening (§13). Under a cloud deck the direct beam is replaced by diffuse
        // skylight and cast shadows lose their edge — under heavy overcast they vanish outright.
        // Vanilla models none of this: CurShadowStrength is Clamp01(Abs(CurCelestialSunGlow - 0.6)
        // / 0.15) and CurCelestialSunGlow is a pure function of latitude/longitude/tick with no
        // weather term at all, so vanilla renders a blizzard's shadows exactly as crisp as a clear
        // noon's. Worse, this Postfix *overwrites* vanilla's result with a purely elevation-derived
        // value, which left us more weather-blind than vanilla rather than less.
        //
        // Shadows need their own seam here rather than riding along on §13's sky tint: shadow alpha
        // is derived from CurCelestialSunGlow and never from SkyManager.CurSkyGlow or
        // SkyTarget.colors, so nothing §13 does to the sky would ever have reached it.
        //
        // Read once, applied to BOTH branches below — unlike the penumbra factor, which models the
        // SUN's angular disk and is meaningless at night. Clouds hide the moon exactly as they hide
        // the sun.
        float weatherShadow = WeatherDimmingMath.ShadowContrastFactor(
            WeatherDimming.CloudOpacityFor(map), WeatherDimmingSettings.MaxDimming);

        // Sun up: it is the shadow caster. Use the elevation-based solar intensity, attenuated by
        // the angular-size penumbra near the horizon when that feature is on.
        if (elevation > Formulas.AtmosphericRefractionDegrees)
        {
            float strength = Formulas.ShadowIntensityFromElevation(elevation);

            // Angular-size penumbra (feature-gated): attenuate the shadow strength toward the
            // horizon, where the solar-disk penumbra widens and shadows lose contrast.
            // CurShadowStrength is the CORRECT lever for this — it is exactly what SkyManager lerps
            // MatBases.SunShadow.color by (the material colour that actually darkens the ground) and
            // writes into the _CastVect global. An earlier attempt folded this into a per-section
            // _CastVect.w override, which a live A/B proved inert: visible opacity is the global
            // material colour, not a per-draw _CastVect.w — DESIGN.md §3 has the scan of the shipped
            // shader showing _CastVect.w is read by nothing at all. Penumbra is a map-wide function
            // of sun elevation, so this global point is
            // its natural home. It models the SUN's disk only, so it lives in this sun-up branch and
            // never scales the moon shadow below.
            if (CelestialLightingFeatures.PenumbraContrast)
                strength *= PenumbraMath.PenumbraContrastFactor(elevation);

            __result = Formulas.ScaleShadowStrength(strength * weatherShadow, ShadowSettings.Strength);
            return;
        }

        // Sun down: hand off to the same MoonPosition.ShadowForMap adapter Patch_ShadowDirection
        // uses for its night branch, so the alpha set here always matches the vector set there. If
        // the moon is down or new, ShadowForMap returns null and the night stays shadowless.
        (UnityEngine.Vector2 vector, float strength)? moonShadow = MoonPosition.ShadowForMap(map);
        float moonStrength = moonShadow.HasValue ? moonShadow.Value.strength * weatherShadow : 0f;

        // The user's "Shadow strength" slider, applied last and to both branches — the same knob
        // Patch_ShadowDirection applies to LightInfo.intensity. This is the value that actually
        // reaches the shader (SkyManager writes it into MapSunLightDirection's alpha and lerps
        // MatBases.SunShadow.color by it), so a slider that missed this point would move the mesh's
        // extrusion without moving what the player sees.
        __result = Formulas.ScaleShadowStrength(moonStrength, ShadowSettings.Strength);
    }
}
