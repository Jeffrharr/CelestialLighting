using HarmonyLib;
using RimWorld;
using Verse;

namespace CelestialLighting;

// Subsystem 7 (DESIGN.md §7). Vanilla night glow is a flat floor; this makes the night floor the
// *sum* of a few dim, physically-motivated sources — starlight + airglow + moonlight — so darkness
// is emergent: legible under a full moon, genuinely dark on a new moon, and true pitch-black once
// the atmospheric floors are toggled off.
//
// It writes to WeatherWorker.CurSkyTarget's .glow ONLY (never .colors) and ONLY below the horizon,
// so it composes cleanly under Patch_TwilightColor (§2), which warms .colors during the dusk/dawn
// band *above* the horizon — the two never touch the same field in the same regime. Like §2, it
// recomputes the sun's true elevation from our own shared simulator rather than reading
// __result.glow, so night brightness tracks real celestial geometry rather than whatever an earlier
// postfix happened to leave in the field.
//
// Weather dimming genuinely is "a separate, later multiply on top", but not in the way an earlier
// version of this comment assumed: fog and rain do NOT clamp glow down. WeatherDef.maxGlow defaults
// to 1.0 and is set exactly once across all vanilla XML (Odyssey's Overcast, 0.95) — see DESIGN.md
// §13. §13 supplies that multiply on the COLOUR channel instead, deliberately leaving .glow alone so
// gameplay lighting stays vanilla under every weather.
[HarmonyPatch(typeof(WeatherWorker), nameof(WeatherWorker.CurSkyTarget))]
public static class Patch_NightRadiance
{
    static void Postfix(Map map, ref SkyTarget __result)
    {
        // Feature gate (default on): when off, leave vanilla's night glow floor untouched — the
        // faithful pre-feature baseline. Sits before the elevation lookup so "off" is a true no-op.
        if (!CelestialLightingFeatures.NightRadiance)
            return;

        // Enclosed map (a Biomes! Caverns cavern, vanilla's undercave). This is the gate that
        // matters most of the nine: the night floor this patch raises is starlight, airglow and
        // MOONLIGHT, none of which reach through a rock ceiling, and this is also the only patch in
        // the mod that writes SkyTarget.glow — so leaving it ungated did not merely tint a cave, it
        // lit one. See MapSkyMath.
        if (MapSky.IsEnclosed(map))
            return;

        // Same shared solar-position simulator every other subsystem uses (Patch_ShadowDirection,
        // Patch_ShadowStrength), so night timing can never disagree with the shadow/twilight patches
        // about where the sun actually is.
        float sunElevation = SolarPosition.ElevationForMap(map);

        // Above the night-floor band entirely (day / high twilight): nothing to do. This early-out
        // is also the guarantee that daytime glow is never touched — the blend weight would be 0
        // here anyway, but returning makes that explicit and skips the moon lookup.
        if (sunElevation > NightRadianceMath.NightFloorStartElevation)
            return;

        // The floor itself is no longer assembled here. §18b gave it two more consumers — #31 needs
        // what a cast shadow bottoms out at once vacuum removes the skylight fill, #33 needs the
        // umbral minimum an eclipse falls to — so it moved to the shared NightRadiance adapter and
        // all three read the same function. They cannot disagree about how dark this map's night is.
        //
        // The vacuum substitutions (airglow gone, starlight unextinguished, planetshine instead of
        // the moon as the dominant reflector) happen inside that read, so this patch needs no branch
        // of its own: it asks for the floor and blends toward it exactly as it always did.
        float nightGlow = NightRadiance.FloorGlowFor(map);
        __result.glow = NightRadianceMath.ApplyNightFloor(__result.glow, sunElevation, nightGlow);
    }
}
