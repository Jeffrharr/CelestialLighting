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
}
