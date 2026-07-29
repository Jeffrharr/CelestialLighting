using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// Vanilla's GenCelestial.GetLightSourceInfo, for LightType.Shadow, doesn't derive its result from
// real sun position at all: length is a plain linear lerp across the day fraction, and the only
// public signal for "how far below the horizon is the sun" (CurCelestialSunGlow) clamps to exactly
// 0 the instant the sun dips under — throwing away how far below it actually is. That makes it
// impossible to tell "just set" from "polar midnight" using vanilla's own numbers, and its
// near-pole handling (SunOffsetFractionFromLatitudeCurve) is a two-point curve tuned only for
// 70-75 degrees latitude, not a real declination model.
//
// This Postfix replaces the Shadow-type result outright with a small solar-position simulator
// (see Formulas.cs "Solar-position shadow simulator"): real elevation/azimuth from
// latitude+declination+hour-angle, shadow length from cot(elevation) so shadows get naturally
// dramatic near the horizon instead of vanilla's shrink-to-nothing-at-dusk curve, and existence
// gated on Formulas.AtmosphericRefractionDegrees (not a bare 0) so the shadow doesn't vanish a few
// minutes before real-world atmospheric refraction would have let it linger.
//
// NOTE: __result.intensity here does NOT control whether a shadow is actually visible in-game —
// the shadow shader's alpha comes from a separate call to GenCelestial.CurShadowStrength(map),
// patched independently by Patch_ShadowStrength using the same SolarPosition.ElevationForMap so
// the two never disagree. Nor does vanilla read it for LightType.Shadow — decompiling SkyManager
// shows intensity consumed only on the LightingSun/LightingMoon paths. It is still set, and set
// consistently with Patch_ShadowStrength, because any third-party postfix on GetLightSourceInfo
// that does read it should see our answer rather than vanilla's un-suppressed one. One line to
// avoid handing another mod a stale number.
[HarmonyPatch(typeof(GenCelestial), nameof(GenCelestial.GetLightSourceInfo))]
public static class Patch_ShadowDirection
{
    static void Postfix(Map map, GenCelestial.LightType type, ref GenCelestial.LightInfo __result)
    {
        if (type != GenCelestial.LightType.Shadow)
            return;

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

        SolarPosition.Inputs inputs = SolarPosition.InputsForMap(map);
        float elevation = Formulas.SolarElevationDegrees(inputs.Latitude, inputs.Declination, inputs.DayPercent);

        if (elevation <= Formulas.AtmosphericRefractionDegrees)
        {
            // Sun is physically below the horizon (past atmospheric refraction). Vanilla's "night"
            // branch here (the folded, num2 == -0.9f curve) renders a second shadow meant to
            // represent moonlight, but it isn't tied to any real moon position. Now that
            // GameComponent_MoonPhase exists we drive a *real* moon-cast shadow: if the moon is up
            // and illuminated, MoonPosition.ShadowForMap returns its (position-derived) vector and
            // faint strength; otherwise (moon down, new moon, or a sky still too bright for the moon
            // to compete with — see §6b) it returns null and we suppress the shadow entirely rather
            // than show a fake one. Patch_ShadowStrength consults the same MoonPosition adapter, so
            // the shader alpha it sets always agrees with this vector.
            //
            // This branch is therefore no longer a handover: crossing it does not start a moon
            // shadow, it only stops the sun's. The moon's fades in on its own several degrees later,
            // once the twilight sky has dropped to within reach of the moon's ~0.27 lux.
            (Vector2 vector, float strength)? moonShadow = MoonPosition.ShadowForMap(map);
            if (moonShadow.HasValue)
            {
                Apply(ref __result, moonShadow.Value.vector.x, moonShadow.Value.vector.y, moonShadow.Value.strength);
            }
            else
            {
                __result.intensity = 0f;
            }
            return;
        }

        float azimuth = Formulas.SolarAzimuthDegrees(inputs.Latitude, inputs.Declination, elevation, inputs.DayPercent);
        Formulas.ShadowVector shadow = Formulas.ShadowVectorFromSunPosition(elevation, azimuth);

        Apply(ref __result, shadow.X, shadow.Y, Formulas.ShadowIntensityFromElevation(elevation));
    }

    // Writes a computed shadow through the user's two Look sliders. Both branches above go through
    // here so the sun's shadow and the moon's are scaled identically — "Shadow length" means the same
    // thing at midnight as at noon — and so the knobs are applied in exactly one place per patch.
    // Patch_ShadowStrength applies the same strength knob to CurShadowStrength, which is the alpha the
    // shader actually renders with.
    private static void Apply(ref GenCelestial.LightInfo info, float x, float y, float intensity)
    {
        Formulas.ShadowVector scaled = Formulas.ScaleShadowVector(x, y, ShadowSettings.LengthScale);
        info.vector = new Vector2(scaled.X, scaled.Y);
        info.intensity = Formulas.ScaleShadowStrength(intensity, ShadowSettings.Strength);
    }
}
