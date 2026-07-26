using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file, same discipline as
// Formulas.cs / NightRadianceMath.cs. Compiled into both Source (net481, inside RimWorld) and
// Tests (net8.0, standalone) via a linked <Compile Include>, so the exact code that ships is the
// exact code under test. Anything needing Mathf/Color/Map belongs in Patch_WeatherDimming.
//
// Subsystem 13 (DESIGN.md §13): make weather actually darken the sky and soften shadows.
//
// WHY THIS EXISTS AT ALL. Vanilla does not dim sky glow by weather. WeatherWorker.CurSkyTarget
// computes `result.glow = Math.Min(CurCelestialSunGlow(map), def.maxGlow)`, and `maxGlow` defaults
// to 1.0 and is set exactly ONCE across all vanilla XML — Odyssey's `Overcast`, at 0.95. Rain, Fog,
// Blizzard, thunderstorms, Sandstorm and BlindFog all leave it at the default. So a blizzard is,
// as far as light is concerned, identical to a clear noon. §9's low-light desaturation was written
// believing otherwise ("an overcast night desaturates more than a clear one for free"); it never
// did, because at night celestial glow is ~0 under every weather alike. This subsystem is what
// finally makes that true.
//
// WHICH CHANNEL WE WRITE — the single most important decision here. SkyTarget carries two
// independent outputs, and SkyManagerUpdate consumes them separately:
//
//   .glow            -> curSkyGlowInt -> GlowGrid.GroundGlowAt  == GAMEPLAY
//                       (PlantProperties.growMinGlow 0.51, CompPowerPlantSolar, pawn psych-glow)
//   .colors.sky /    -> MatBases.LightOverlay.color, MatBases.FogOfWar,
//   .overlay /          Find.CameraColor.saturation                == PURE RENDER
//   .saturation
//
// We write the colour channel and never touch .glow. That is not a workaround — it is the channel
// vanilla already uses for weather: clear skies push skyColorsDay.sky = (1,1,1), every overcast or
// wet weather pushes (0.8,0.8,0.8). A real 20% visual darkening that never reaches GroundGlowAt.
// We deepen it and make it scale with storm intensity. This keeps CLAUDE.md's "scope is
// visual/atmospheric only" intact with no amendment: plant growth, solar output and pawn vision
// stay bit-for-bit vanilla under every weather.
//
// HOW A WEATHER IS CLASSIFIED — three layers, none of them a defName list (issue #31):
//
//   1. BiomeHasChangingWeather   is there weather over this map at all?     (map-level veto)
//   2. PaletteOpacity            how overcast does the palette look?        (evidence)
//   3. PrecipitationEvidence     is anything falling out of the sky?        (evidence)
//
// with WeatherCloudDeck as a per-def escape hatch for content that none of the three can settle.
// §13 originally shipped only layer 2, on the strength of a regularity in vanilla's palettes that
// modded content turned out not to share; the reasoning for each layer, and the census that
// motivated it, is on the individual members below.
public static class WeatherDimmingMath
{
    // --- Classification anchors, read off the vanilla WeatherDef census (see PaletteOpacity) ---

    // Rec. 709 luma of a clear sky palette (1,1,1) and of the shared overcast/wet palette
    // (0.8,0.8,0.8). These bracket the luminance ramp: at or above clear, no cloud; at or below
    // overcast, a full deck.
    public const float ClearSkyLuminance = 1.0f;
    public const float OvercastSkyLuminance = 0.8f;

    // Saturation of the same two palettes. Vanilla's clear-family saturation is 1.25 and the
    // overcast/wet family is 0.9.
    public const float ClearSaturation = 1.25f;
    public const float OvercastSaturation = 0.9f;

    // The heaviest obscuration rate in vanilla — Odyssey's Sandstorm, sandRate 1.6. Blizzard's
    // snowRate 1.5 and every rainRate of 1.0 sit below it, so dividing by this maps vanilla's whole
    // range into [0,1] with the worst storm at exactly 1.
    public const float ObscurationReference = 1.6f;

    // Share of the maximum dimming that a *dry* deck (fog, overcast, dry thunderstorm — cloud but
    // no falling precipitation) already gets. The remaining 40% is bought by precipitation
    // intensity. Not 0: an overcast sky is meaningfully darker than a clear one even with no rain,
    // which is the single most common case players will see.
    public const float DryDeckShare = 0.6f;

    // Default peak sky darkening, as a fraction of the rendered sky brightness. 0.30 at the very
    // worst vanilla storm. Deliberately modest: this multiplies the *light overlay colour*, which
    // is already a fairly dark multiply at dusk/night, so large values crush the picture to mud
    // rather than reading as weather.
    public const float DefaultMaxDimming = 0.30f;

    // How much of the shadow's contrast a full cloud deck removes. Under heavy overcast direct
    // sunlight is replaced almost entirely by diffuse skylight and cast shadows genuinely vanish;
    // 0.85 leaves a faint 15% so the scene keeps some grounding rather than looking unlit.
    public const float MaxShadowSoftening = 0.85f;

    // Above this, a weather's rainRate/snowRate/sandRate counts as "something is falling out of the
    // sky", which is categorical evidence of a deck — see PrecipitationEvidence. Just above zero
    // rather than at it, so a def that leaves a rate at literal 0f is unambiguously dry. It
    // deliberately does not try to tell 0.05 (Anomalies Expected's blood fog) from 1.0: how hard it
    // is coming down is ObscurationIntensity's job, not this one's.
    public const float PrecipitationEpsilon = 1e-4f;

    // The smallest number of possible weathers a biome must offer before we treat its map as having a
    // climate at all — see BiomeHasChangingWeather. Not a tuning knob: 2 is the arithmetic minimum
    // for "the weather can change".
    public const int MinWeatherChoicesForClimate = 2;

    // --- Cloud classification ---

    // Rec. 709 luma. Not a naive channel average: a modded palette that is chromatic rather than
    // grey should be judged the way an eye weighs it, so green counts far more than blue.
    public static float Luminance(float r, float g, float b) =>
        0.2126f * r + 0.7152f * g + 0.0722f * b;

    // 0 at a clear sky's luminance, 1 at or below the overcast palette's. Descending range, which
    // InverseLerpClamped handles; the clamp means a modded palette darker than vanilla's overcast
    // plateaus at 1 instead of running away.
    public static float LuminanceDeficit(float luminance) =>
        InverseLerpClamped(ClearSkyLuminance, OvercastSkyLuminance, luminance);

    // 0 at the clear-family saturation, 1 at or below the overcast family's. Same descending shape.
    public static float SaturationDeficit(float saturation) =>
        InverseLerpClamped(ClearSaturation, OvercastSaturation, saturation);

    // How much of a cloud deck this weather's own day palette implies, in [0,1].
    //
    // THE PRODUCT IS BOTH CLASSIFIER AND GUARD. A weather must be BOTH greyer AND flatter than clear
    // to count as cloud. Taking the product rather than a max or a sum is what makes the vanilla
    // census come out right with no defName list anywhere:
    //
    //   family                      sky            saturation   lumDef  satDef  opacity
    //   Clear, Windy, Orbit         (1,1,1)        1.25         0       0       0
    //   Underground, Undercave      (0.3,0.4,0.4)  1.25         1       0       0
    //   MetalHell                   (0.4,0.5,0.5)  1.25         1       0       0
    //   UnnaturalDarkness           (.482,.603,.682) 1.25       1       0       0
    //   Fog/Rain/Blizzard/Sandstorm (0.8,0.8,0.8)  0.9          1       1       1
    //   GrayPall / UnnaturalFog     (.482,.603,.682) 0.75/0.5   1       1       1
    //
    // Every dark-palette NON-weather (the underground set, plus Anomaly's UnnaturalDarkness) keeps
    // the clear family's saturation of 1.25, so its saturation deficit is exactly 0 and the product
    // zeroes it structurally. Luminance alone would misfire on all four. Orbit is safe under either
    // rule only because its palette is byte-identical to Clear's — that is luck, and the product
    // does not depend on it.
    //
    // WHAT THIS IS *NOT* ENOUGH FOR, and the reason it is no longer the whole classifier. §13
    // originally shipped this as the complete story, on the strength of the regularity above. An
    // audit of every WeatherDef in 81 installed defs across vanilla + 24 workshop mods (issue #31,
    // reproducible via Tools/WeatherAudit) showed the `saturation: 1.25` convention is a *vanilla*
    // convention that modded content does not follow: Biomes! Caverns' BMT_Calm and MultiFloors'
    // MF_UndergroundWeather are cave environments shipping overcast-shaped palettes, and this
    // function rates them 1.00 and 0.71. The fix is not to complicate the palette rule — every
    // palette-only discriminator tried against that census (day-versus-night contrast in
    // particular) traded those two false positives for a larger set of real modded fogs and palls
    // that ship flat or inverted palettes. The fix is that "is there a sky over this map at all" is
    // a question about the MAP, answered by BiomeHasChangingWeather below, and "is there a deck
    // overhead" gains a second, independent line of evidence in PrecipitationEvidence.
    //
    // Kept public and separately named because it is one *factor* now, not the answer, and the tests
    // pin it independently of what is layered over it.
    public static float PaletteOpacity(float r, float g, float b, float saturation) =>
        LuminanceDeficit(Luminance(r, g, b)) * SaturationDeficit(saturation);

    // 1 when this weather makes anything fall out of the sky, 0 when it does not.
    //
    // Deliberately categorical rather than proportional. Precipitation is not a hint about cloud
    // cover, it is a consequence of it — rain, snow and blown sand all require something overhead
    // to fall from, so any nonzero rate settles the question of WHETHER there is a deck outright.
    // How hard it is coming down is a separate axis that ObscurationIntensity already handles, and
    // conflating the two would double-count intensity.
    //
    // This is what closes the false-NEGATIVE half of the modded-weather problem. Alpha Biomes'
    // AB_ForsakenRainyNight_Alternate is a rainstorm (rainRate 1.0) whose day palette is only
    // partway to overcast, so the palette rule alone rated it 0.43 — a visibly wet sky dimmed as if
    // half-clear. Vanilla is unaffected: every precipitating vanilla weather already scores a full
    // 1.00 on the palette rule, so flooring at 1 changes nothing there.
    public static float PrecipitationEvidence(float rainRate, float snowRate, float sandRate) =>
        Max3(rainRate, snowRate, sandRate) > PrecipitationEpsilon ? 1f : 0f;

    // How much of a cloud deck this weather implies, in [0,1] — the classifier proper.
    //
    // MAX, not product: the two lines of evidence are independent and either one alone is
    // sufficient. A dry overcast has a grey flat palette and no precipitation; a modded storm may
    // have precipitation and an idiosyncratic palette. Requiring both would reintroduce the false
    // negative that PrecipitationEvidence exists to fix.
    //
    // Still no defName list, and still no registration step: a modded weather is classified purely
    // from data it already declares for its own rendering and simulation. Where that genuinely
    // cannot settle it, WeatherCloudDeck lets the def say so outright.
    public static float CloudOpacity(
        float r, float g, float b, float saturation,
        float rainRate, float snowRate, float sandRate)
    {
        float palette = PaletteOpacity(r, g, b, saturation);
        float precipitation = PrecipitationEvidence(rainRate, snowRate, sandRate);
        return palette > precipitation ? palette : precipitation;
    }

    // --- The structural guard: does this map have weather at all? ---

    // Whether a biome offering `weatherChoices` possible weathers has a climate we should dim.
    //
    // THE CHEAPEST GUARD THAT COVERS THE REAL CASES, and the one §13 originally argued it did not
    // need. A biome that lists exactly one weather with nonzero commonality can never change weather:
    // whatever palette that single def ships is the map's fixed environment, not a deck that rolled in
    // and will roll out. Caves, pocket maps, ancient complexes and orbit are that shape, and asking
    // the question this way needs no roof grid, no cell iteration and no list of ours to maintain.
    //
    // WHAT IT DOES AND DOES NOT CLAIM — stated carefully, because an earlier version of this comment
    // overclaimed and a live run caught it. This does NOT cleanly partition skyless from open-air.
    // Vanilla's `Undercave` biome offers TWO weathers, not one: it declares `Undercave` and inherits
    // `Underground` from `Biome_Underground`, and RimWorld's XML inheritance recurses into the shared
    // `baseWeatherCommonalities` node and appends rather than replacing. So Undercave sits at 2,
    // exactly like the open-air Duskwood, and this rule treats both as having a climate.
    //
    // What makes that safe is not the count, it is which biomes can do harm. A skyless biome only
    // dims wrongly if it can roll a weather whose palette classifies above 0, and across all 65
    // biomes in vanilla + 24 installed workshop mods, EVERY skyless biome above the threshold offers
    // only weathers that already classify to exactly 0 (Undercave rolls `Undercave` or `Underground`,
    // both of which keep the clear family's saturation). Every biome that could actually have dimmed
    // wrongly — `BMT_CrystalCaverns`, `BMT_EarthenDepths`, `MF_BasementBiome`, `AE_BloodLakeBiome`,
    // `AE_ChristmasTreeBiome` — offers exactly one weather and is caught. Tools/WeatherAudit prints
    // that as a `worst` column per biome plus a short boundary list, which is the check to re-run
    // after a mod-list change rather than trusting this paragraph.
    //
    // A third condition was measured and rejected: `generatesNaturally == false` would additionally
    // catch Undercave and every vanilla pocket map, but it also catches Deep Mining's
    // `DMSE_ImpactCraterBiome`, an open-air crater with Clear/Rain/DryThunderstorm that genuinely
    // wants dimming. It buys only biomes that were already harmless and costs a real one.
    //
    // Counting must be over NONZERO commonalities. Biomes! Caverns lists vanilla's
    // Rain/FoggyRain/DryThunderstorm on its cavern biomes at commonality 0 specifically to suppress
    // them; counting entries rather than possibilities would read those caverns as having a climate
    // and reinstate the exact false positive this closes.
    //
    // The cost of being wrong is small and one-directional: a hypothetical open-air biome that
    // genuinely offers a single weather forever gets no dimming, which leaves the author's own
    // palette rendering exactly as they authored it.
    public static bool BiomeHasChangingWeather(int weatherChoices) =>
        weatherChoices >= MinWeatherChoicesForClimate;

    // Blends the outgoing and incoming weather's opacity across a weather transition, so a front
    // rolling in eases rather than snapping. Mirrors exactly how vanilla lerps RainRate/SnowRate:
    // WeatherManager.TransitionLerpFactor runs 0 -> 1 over WeatherManager.TransitionTicks (4000).
    public static float BlendOpacity(float lastOpacity, float curOpacity, float transitionLerpFactor) =>
        Lerp(lastOpacity, curOpacity, Clamp01(transitionLerpFactor));

    // Precipitation intensity in [0,1], normalised against the heaviest vanilla storm. Max (not sum)
    // of the three rates because they are alternatives, not additives — no vanilla weather both
    // rains and sandstorms, and a modded one that did should read as one heavy storm, not a
    // double-strength one.
    public static float ObscurationIntensity(float rainRate, float snowRate, float sandRate)
    {
        float heaviest = Max3(rainRate, snowRate, sandRate);
        return Clamp01(heaviest / ObscurationReference);
    }

    // The final 0..1 fraction by which the rendered sky is darkened.
    //
    // Cloud opacity decides WHETHER (and how much of) a deck is overhead; precipitation decides how
    // hard it is coming down, moving the result across a band from DryDeckShare of maxDimming up to
    // the whole of it. maxDimming is the user's strength slider, so setting it to 0 makes every term
    // vanish and the whole subsystem a true no-op.
    public static float DimmingFraction(
        float cloudOpacity, float rainRate, float snowRate, float sandRate, float maxDimming)
    {
        float floor = DryDeckShare * maxDimming;
        float band = Lerp(floor, maxDimming, ObscurationIntensity(rainRate, snowRate, sandRate));
        return Clamp01(Clamp01(cloudOpacity) * band);
    }

    // --- Consumers ---

    // The multiplier Patch_WeatherDimming applies to SkyColorSet.sky and .overlay. Multiplying
    // (never assigning) is what lets this stack with §2's twilight warmth, §8's colour temperature
    // and §11's aurora tint instead of overwriting whichever ran first.
    public static float SkyTintFactor(float dimming) => Clamp01(1f - Clamp01(dimming));

    // Perceived brightness for §9's Purkinje ramp: what the sky LOOKS like once the deck is
    // overhead, as opposed to SkyTarget.glow, which is what the game's lighting grid still reads.
    //
    // This is the seam that finally delivers §9's original promise. §9 keys its rod-vision ramp on
    // brightness, and it wants the *apparent* brightness — a rainy night genuinely looks darker and
    // so should desaturate further. Feeding it this instead of raw .glow gets that without writing
    // a single gameplay-visible value.
    public static float ApparentGlow(float glow, float dimming) =>
        Clamp01(glow) * SkyTintFactor(dimming);

    // The multiplier Patch_ShadowStrength applies to cast-shadow alpha. Under a deck, direct sun is
    // replaced by diffuse skylight and shadows lose their edge; at full opacity only
    // (1 - MaxShadowSoftening) of the contrast survives.
    //
    // Scaled by the user's strength slider relative to the shipped default so that sliding to 0 is a
    // no-op here too, and so the one knob governs the whole subsystem coherently. Clamped at 1 so
    // pushing the slider past the default deepens the sky without ever driving shadows negative.
    //
    // Unlike PenumbraMath.PenumbraContrastFactor — which models the SUN's angular disk and so
    // applies only while the sun is up — this applies to the moon's shadow as well: clouds hide the
    // moon exactly as they hide the sun.
    public static float ShadowContrastFactor(float cloudOpacity, float maxDimming)
    {
        float strength = Clamp01(maxDimming / DefaultMaxDimming);
        return Clamp01(1f - Clamp01(cloudOpacity) * strength * MaxShadowSoftening);
    }

    // --- §13a: what the shadow alpha actually renders as ---

    // Vanilla's daytime shadow colour on Clear — the one weather Ludeon actually tuned the value for
    // (Core/Defs/WeatherDefs/Weathers.xml, skyColorsDay.shadow). Every other weather flattens all
    // four sky-colour sets to 0.92. Kept here as the reference §13a is defined against, so the tests
    // can express the fix in the units a player perceives. The patch itself reads the live
    // WeatherDefOf.Clear rather than this constant, so a Ludeon retune moves the shipped behaviour
    // and this number only governs what the tests claim.
    public const float ClearDayShadowValue = 0.718f;

    // What vanilla flattens colors.shadow to under EVERY non-Clear weather, at every sky-glow anchor.
    public const float WeatherShadowValue = 0.92f;

    // Below this, a cast shadow stops being perceptible on mid-tone ground — about 5 values out of
    // 255. Not a tuning knob: it is the threshold the §13a regression is stated in terms of, since
    // the bug was never a wrong number, only a number too small to see.
    public const float PerceptibleDarkening = 0.02f;

    // The darkening a cast shadow actually renders, in [0, 1] — 0 is invisible, 1 is pure black.
    //
    // Inverts SkyManager's own lerp rather than second-guessing it: the shader draws
    // Color.Lerp(Color.white, colors.shadow, alpha), so the rendered value is
    // 1 - alpha * (1 - shadowColorValue), and the darkening is the complement of that. This exists
    // because "the shadow alpha is correct" and "the player can see a shadow" turned out to be
    // different claims twice over — §6a at night, §13a in daylight — and only this composition
    // tells them apart.
    public static float RenderedShadowDarkening(float alpha, float shadowColorValue) =>
        Clamp01(alpha) * (1f - Clamp01(shadowColorValue));

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float InverseLerpClamped(float a, float b, float v) => Clamp01((v - a) / (b - a));

    private static float Max3(float a, float b, float c)
    {
        float ab = a > b ? a : b;
        return ab > c ? ab : c;
    }
}
