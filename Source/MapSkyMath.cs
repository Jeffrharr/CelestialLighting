namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file, the same discipline
// Formulas.cs / EavesMath.cs / IndoorOcclusionMath.cs follow. Compiled into both Source (net481,
// runs inside RimWorld) and Tests (net8.0, runs standalone via `dotnet test`) through a linked
// <Compile Include>, so the exact shipped code is the code under test. Anything that needs a
// Map/BiomeDef belongs in MapSky, not here.
//
// THE SKYLESS GATE. One question — "is there sky over this map at all?" — asked once, in one
// place, so that every subsystem which derives its output from the sky agrees about which maps
// have one.
//
// Why this is a shared rule rather than a §13 detail. The rule was born inside §13 weather dimming
// (issue #31: modded cave environments ship overcast-shaped palettes, so no palette-only classifier
// can tell a cloud deck from a cave ceiling). But the question it answers was never specific to
// weather. Sunset warmth (§2), blackbody colour temperature (§8), aurora tint (§11), blood-moon
// crimson (§12), an eclipse (§10) and a night floor lifted by starlight and MOONLIGHT (§5) are all
// equally meaningless under a rock ceiling, and every one of them was applying itself to sealed
// cave maps because each had been written to ask about the sun or the moon rather than about the
// sky in between. Biomes! Caverns is the case that made this concrete — see DESIGN.md §13.
//
// WHAT THIS DELIBERATELY DOES NOT GATE, because "no sky" is not the same question as either of
// these and conflating them produced wrong answers in testing:
//
//   - SHADOWS. Vacuum maps are skyless by the weather rule and are exactly where sunlight is
//     harshest. Shadows key on the biome's own `disableShadows` flag instead (MapSky.DrawsShadows),
//     which is vanilla's field, which vanilla itself honours at SectionLayer_SunShadows.Visible, and
//     which Biomes! Caverns sets on its cavern biomes. Two gates, deliberately, not one.
//   - HOW DARK AN UNLIT PLACE GETS (§7a pitch-black nights, §7b indoor sky occlusion). A sealed
//     cave rendering dark because it has no sky is the correct result and the mod's whole premise,
//     not a bug to gate away. Those two stay on.
//   - §9 Purkinje desaturation, which keys on MEASURED glow rather than on the sky, and therefore
//     already self-corrects to whatever light a cave actually has. Gating it would break it.
//
// A THIRD KIND OF MAP: one that has a sky and no ceiling, but nothing visible through it (issue #35).
// Odyssey's Glowforest sits under a permanent sulfur overcast, and any map with an AncientSmokeVent
// spends roughly 3 days in 7 under the same one. Neither is enclosed and both draw shadows, so both
// gates above stay open and every sun- and moon-derived effect ran on them. That question is
// ConditionBlacksOutSky below, and it is dynamic rather than a BiomeDef property — see its header.
public static class MapSkyMath
{
    // Composes the two independent conditions, either of which means there is no sky.
    //
    // `biomeExists` is false for a map with no biome at all — a degenerate case (pocket maps
    // mid-generation). Treated as skyless: declining to apply a sky effect leaves vanilla's own
    // rendering untouched, which is the safe direction to be wrong in, and it is the same default
    // §13 has shipped with.
    //
    // `disableSkyLighting` is the biome's own declaration that there is nothing overhead. It is the
    // same field SkyManagerUpdate branches on to stop writing colors.sky to MatBases.LightOverlay
    // at all, so it is vanilla's notion of "skyless", not one we invented.
    //
    // `weatherChoices` is how many weathers the biome can actually roll. The reasoning for the
    // threshold, what it catches, what it provably does not catch, and why being wrong is cheap and
    // one-directional all live on WeatherDimmingMath.BiomeHasChangingWeather — deliberately not
    // restated here, so there is one place to correct if a future census moves it.
    public static bool HasSky(bool biomeExists, bool disableSkyLighting, int weatherChoices)
    {
        if (!biomeExists)
            return false;

        if (disableSkyLighting)
            return false;

        return WeatherDimmingMath.BiomeHasChangingWeather(weatherChoices);
    }

    // Whether this map is ENCLOSED — has no view of the sky at all. This, not HasSky, is what the
    // sky-sourced effects gate on.
    //
    // WHY THESE ARE TWO QUESTIONS AND NOT ONE. HasSky answers "can weather roll overhead", which is
    // what §13 needs. It is false for both a cavern and for orbit, and those two are not the same
    // situation at all: a cavern has a rock ceiling, while orbit has no atmosphere but a completely
    // unobstructed view of the sun and stars. Vanilla already distinguishes them with `inVacuum`
    // (BiomeDef.inVacuum, set by Odyssey's `Space`/`Orbit`, not set by any cave biome), so no def-name
    // list is needed to tell a vacuum apart from a ceiling.
    //
    // Orbit is therefore deliberately NOT enclosed, and every sky effect keeps running there exactly
    // as it does today. What twilight or a blackbody colour shift should mean with no atmosphere to
    // scatter through is a real question with a different answer from "nothing", and it is being
    // handled as its own piece of work rather than being folded into cave compatibility by accident.
    //
    // Structuring it this way is also what keeps the §13 move behaviour-preserving: HasSky is
    // untouched, so weather_dimming_skyless.json — which pins Orbit at zero dimming — still passes
    // unchanged and remains the proof of that.
    public static bool IsEnclosed(bool hasSky, bool inVacuum) => !hasSky && !inVacuum;

    // Whether ONE active GameCondition means the sky over this map is opaque right now — no sun, no
    // moon, no stars. MapSky.SkyBlackedOut folds this over the conditions actually affecting a map.
    //
    // This is the third map-kind question, and the only DYNAMIC one. HasSky and IsEnclosed are
    // properties of a BiomeDef and cannot change while a map is loaded; this one turns on and off
    // several times a game year on the same map, which is exactly why it could not be answered by
    // adding a clause to IsEnclosed.
    //
    // WHY A CONDITION CLASS RATHER THAN A DEF LIST. Most things that black out a RimWorld sky are a
    // GameCondition_NoSunlight, and there are four such sources across vanilla and the DLCs:
    //
    //   - Odyssey's `DarkenedSkies` (GameCondition_NoSunlight_Instant) as a PERMANENT biome condition.
    //     `Glowforest` declares it in `biomeMapConditions` and says so in its own description —
    //     "geysers spew out massive sulfur clouds that cast the region into permanent darkness". That
    //     map is emphatically not enclosed: it is an ordinary surface tile offering eleven weathers and
    //     setting none of disableSkyLighting / disableShadows / inVacuum.
    //   - The same `DarkenedSkies`, TIMED, from an `AncientSmokeVent`'s CompAncientVentEmitter, which
    //     cycles it roughly 3 days on / 4 days off on any map that has one. This is the case that
    //     proves the question is per-map runtime state rather than biome data.
    //   - Royalty's `SunBlocker` machine.
    //   - Vanilla's `Eclipse` — which is why the carve-out below exists.
    //
    // A FIFTH source is a different class entirely: Anomaly's `UnnaturalDarkness`
    // (`GameCondition_UnnaturalDarkness`) derives from `GameCondition_ForceWeather`, not
    // `GameCondition_NoSunlight`, so the `is GameCondition_NoSunlight` test alone misses it — even
    // though its own `SkyTarget` override returns the identical `GameCondition_NoSunlight.
    // EclipseSkyColors` at glow 0, and `SkyManager.CurrentSkyTarget` composes ANY condition's
    // `SkyTarget`/`SkyTargetLerpFactor` through the same `LerpDarken`, regardless of class. It needs no
    // eclipse-style carve-out of its own: unlike DarkenedSkies, nothing in this mod reshapes it, so
    // there is no second meaning of "UnnaturalDarkness" to protect from the gate.
    //
    // Keying on the class means a modded blackout condition is caught for free, exactly as IsEnclosed
    // catches every modded cave biome through vanilla biome data rather than a def-name list.
    //
    // THE ECLIPSE CARVE-OUT, which is the one way to get this wrong that no A/B on a blacked-out map
    // would reveal. Eclipse is the same condition class, and §10/§10a exist specifically to reshape it,
    // so gating on the class alone would quietly switch our own eclipse handling off. Excluded by def
    // here, the mirror image of Patch_EclipseDarkening excluding SunBlocker by def — same technique,
    // opposite direction.
    //
    // It is also a genuinely different fact about the world, not just an implementation carve-out: an
    // eclipse covers the SUN while leaving the sky transparent, which is why stars come out during a
    // total one. Nothing comes out under a sulfur overcast.
    public static bool ConditionBlacksOutSky(bool isNoSunlightCondition, bool isUnnaturalDarkness, bool isEclipse) =>
        (isNoSunlightCondition && !isEclipse) || isUnnaturalDarkness;

    // The ambient level an enclosed map sits at, all day, every day.
    //
    // Full rather than some dimmer value on purpose. This is the value BEFORE §7b's occlusion, and
    // on an enclosed map essentially every cell is roofed, so what actually reaches the floor is
    // this scaled by the minimum-indoor-brightness cap. Handing §7b a full sky is what makes that
    // slider the single thing deciding how bright a cave is: Cinematic's 0.50 gives a clearly-lit
    // cave at every hour, and Realistic's 0.0 keeps caves pitch black at every hour, so both presets
    // go on meaning exactly what they advertise instead of acquiring a second, hidden knob.
    public const float EnclosedAmbientGlow = 1f;

    // Replaces an enclosed map's diurnal sky glow with that constant.
    //
    // WHY A CAVE MUST NOT TRACK THE SUN. Measured on a live enclosed map, sky glow ran 1.00 at noon
    // and 0.00 at midnight, so a cave was legible by day and pure black by night. The minimum
    // indoor brightness slider could not prevent it, because it is a FRACTION of the sky rather than
    // a floor under it — 50% of a full sky is a lit cave, and 50% of nothing is still nothing. A
    // sealed cave has no day, so the fix is to stop the sun reaching it at all rather than to floor
    // the result afterwards.
    //
    // This also lands where Biomes! Caverns already was. Its BMT_Calm weather gives skyColorsDay,
    // skyColorsDusk, skyColorsNightEdge and skyColorsNightMid *identical* values — its authors
    // deliberately gave caverns no diurnal variation whatsoever, and holding glow constant is the
    // same statement made about brightness rather than about colour.
    //
    // GAMEPLAY-INERT, and that was checked rather than assumed. Raising glow sounds like it should
    // make plants grow and pawns see in the dark, but Verse.GlowGrid.GroundGlowAt only consults
    // CurSkyGlow when `!map.roofGrid.Roofed(c)`, and Biomes! Caverns does not transpile GlowGrid —
    // so on a cavern, where every cell carries BMT_RockRoofStable, sky glow is already excluded from
    // gameplay light and only lamps count. The effect here is confined to what the lighting overlay
    // renders. On an enclosed map that happens to have open cells, those cells do take the constant
    // as real light; that is the intended reading — an underground map has no day for its open
    // areas either — but it is the one case where this is more than a visual change.
    public static float AmbientGlow(bool enclosed, float diurnalGlow) =>
        enclosed ? EnclosedAmbientGlow : diurnalGlow;
}
