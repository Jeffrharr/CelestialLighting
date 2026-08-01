using System.Collections.Generic;
using RimWorld;
using Verse;

namespace CelestialLighting;

// Thin adapter over MapSkyMath (which carries the full "why" for the skyless gate, including why
// shadows deliberately use a different flag). This file only pulls the primitives off live
// BiomeDef state.
//
// The four questions every map-kind-sensitive patch in this mod asks, and the ONLY four:
//
//   HasSky(map)       — can weather roll overhead? §13's question, and only §13's.
//   IsEnclosed(map)   — is there a ceiling between this map and the sky? Gates the sky-sourced and
//                       sky-tinting effects.
//   DrawsShadows(map) — does this map render shadows at all? Gates the shadow subsystems.
//   SkyBlackedOut(map)— is the sky opaque right now? The dynamic one; composes with either of the two
//                       above depending on the lane. Issue #35.
//
// The first three are separate on purpose, and orbit is why. Orbit answers false / false / true: no
// weather, no ceiling, harsh unfiltered sunlight. Collapsing the first two would silently strip orbit
// of every sky effect; collapsing in the third would delete shadows from the one place sunlight is
// completely unobstructed. The fourth is separate for a different reason: it is the only one that is
// not a BiomeDef property, so it cannot be a clause on any of them.
//
// This lived as a private WeatherDimming.HasSky until Biomes! Caverns showed the question was
// general rather than a §13 detail; WeatherDimming now delegates here, so §13's behaviour and its
// live scenario are unchanged by the move.
//
// NOT CACHED, deliberately, and this survives the promotion from one caller to nine. Every call
// site is per-map-per-frame at worst — once per weather inside SkyManagerUpdate, once per
// CurShadowStrength, once per section regenerate — never per cell, and the list walked is a dozen
// entries at most. A cache keyed on BiomeDef would buy nothing measurable, would add a write to be
// reasoned about against RimWorld 1.6's worker threads, and would have to be invalidated against
// the harness's own SetBiome step, which mutates map.Biome at runtime specifically so scenarios
// like weather_dimming_skyless.json can sweep biomes inside a single run.
//
// SkyBlackedOut is not cached either, and it is the cheaper of the two: a real colony carries a
// handful of active conditions at most, against the dozen weather entries IsEnclosed already walks at
// the very same call sites. It is also the one predicate here that genuinely changes mid-session, so a
// cache would need the invalidation hooks (RegisterCondition / OnConditionEnd) that the static gates
// do not — cost and risk on the same side of the ledger. Vanilla makes the opposite trade with
// GameConditionManager.cachedAlwaysDark and pays for it with exactly those two hooks.
public static class MapSky
{
    // Whether weather can roll overhead — see MapSkyMath.HasSky. This is §13's question. If you are
    // reaching for it to decide whether a SKY effect applies, you want IsEnclosed below instead:
    // this one is false in orbit, which has no weather but a perfect view of the sky.
    public static bool HasSky(Map map)
    {
        BiomeDef biome = map?.Biome;

        return MapSkyMath.HasSky(
            biome != null,
            biome != null && biome.disableSkyLighting,
            biome == null ? 0 : WeatherChoiceCount(biome));
    }

    // Whether there is a ceiling between this map and the sky — see MapSkyMath.IsEnclosed for why
    // this is a different question from HasSky and why orbit must answer false to it.
    //
    // `inVacuum` is vanilla's own field, set by Odyssey's Space/Orbit biomes and by no cave biome, so
    // separating "no atmosphere" from "no ceiling" costs us no def-name list.
    public static bool IsEnclosed(Map map)
    {
        BiomeDef biome = map?.Biome;
        return MapSkyMath.IsEnclosed(HasSky(map), biome != null && biome.inVacuum);
    }

    // Whether this map renders shadows at all.
    //
    // Vanilla's own flag, honoured by vanilla itself at SectionLayer_SunShadows.Visible
    // (`Map?.Biome?.disableShadows != true`). Reading the same field means our shadow subsystems can
    // never disagree with vanilla about which maps are shadowless — the same discipline EaveCells
    // applies by borrowing Room.UsesOutdoorTemperature rather than inventing an "indoors" test.
    //
    // Biomes! Caverns sets this on all three of its enclosed cavern biomes, so honouring it is what
    // makes our shadow work stop there rather than being computed and then silently discarded by
    // that mod's own DrawLayer skip.
    //
    // Null map or null biome answers TRUE, not false: an unknown map should keep rendering what
    // vanilla would render. That is the opposite default to HasSky above, and deliberately so —
    // each defaults to "leave vanilla alone", which for a gate that SUPPRESSES our effect means
    // false, and for a gate that ENABLES our effect means true.
    public static bool DrawsShadows(Map map)
    {
        BiomeDef biome = map?.Biome;
        return biome == null || !biome.disableShadows;
    }

    // Whether the sky over this map is opaque RIGHT NOW — an active non-eclipse blackout condition.
    // See MapSkyMath.ConditionBlacksOutSky for which conditions those are, why the class rather than a
    // def list is the test, and why the eclipse must be carved out.
    //
    // Composes with the two static gates rather than replacing either, because the two lanes need
    // different companions: a sky-colour effect wants `IsEnclosed || SkyBlackedOut` and a shadow effect
    // wants `!DrawsShadows || SkyBlackedOut`. There is no single combined predicate that serves both, so
    // each call site names the two terms it needs.
    //
    // FALSE for a null map, matching DrawsShadows' direction rather than HasSky's: an unknown map should
    // keep rendering what it renders today, and for a gate that SUPPRESSES our effect that means false.
    public static bool SkyBlackedOut(Map map) => AnyCondition(map, condition => BlacksOutSky(condition, map));

    // Whether Anomaly's `UnnaturalDarkness` specifically is live over this map right now — a narrower
    // question than SkyBlackedOut above, which also fires for Odyssey's DarkenedSkies/SunBlocker.
    //
    // §7a's own MinNightBrightness floor (Patch_PitchBlackOverlay) is the one caller: that floor exists
    // to keep an ordinary moonless night navigable, and UnnaturalDarkness is not an ordinary night — it
    // is Anomaly's own gameplay-critical horror event (the flavour text is literally "stay in the
    // light"; GameCondition_UnnaturalDarkness.AffectedByDarkness spawns DarknessExposure hediffs off
    // the same darkness). Composing our accessibility floor with vanilla's own dread mechanic would
    // wash the event out to whatever brightness the player picked for an ordinary Tuesday night, which
    // is not a call this mod should make for them. DarkenedSkies and SunBlocker get no such carve-out —
    // those are aesthetic, not a DLC's own set-piece, so the player's chosen floor is left to mean what
    // they set it to mean.
    public static bool UnnaturalDarknessActive(Map map) =>
        AnyCondition(map, condition => condition is GameCondition_UnnaturalDarkness && condition.CanApplyOnMap(map));

    // Shared walk of the manager chain the same way vanilla's own GameConditionManager.
    // ElectricityDisabled does: a map's own conditions, then the world's, which is where quest- and
    // planet-scale conditions live. Reading map.gameConditionManager.ActiveConditions alone would miss
    // those.
    private static bool AnyCondition(Map map, System.Predicate<GameCondition> test)
    {
        if (map == null)
            return false;

        GameConditionManager manager = map.gameConditionManager;
        while (manager != null)
        {
            if (AnyConditionMatches(manager.ActiveConditions, test))
                return true;

            manager = manager.Parent;
        }

        return false;
    }

    private static bool AnyConditionMatches(List<GameCondition> conditions, System.Predicate<GameCondition> test)
    {
        if (conditions == null)
            return false;

        for (int i = 0; i < conditions.Count; i++)
        {
            if (conditions[i] != null && test(conditions[i]))
                return true;
        }

        return false;
    }

    private static bool BlacksOutSky(GameCondition condition, Map map)
    {
        // Class test first, because it is one or two type checks and rejects almost every condition a
        // real colony ever carries, whereas CanApplyOnMap below is several branches and a possible
        // waterBodyTracker walk.
        //
        // A null GameConditionDefOf.Eclipse (defs not loaded yet) compares equal to a null condition
        // def and reads as "this IS the eclipse", i.e. as not blacked out — the direction that leaves
        // rendering alone.
        bool blacksOut = MapSkyMath.ConditionBlacksOutSky(
            condition is GameCondition_NoSunlight,
            condition is GameCondition_UnnaturalDarkness,
            condition.def == GameConditionDefOf.Eclipse);
        if (!blacksOut)
            return false;

        // CanApplyOnMap and nothing else, deliberately: this is exactly the filter
        // SkyManager.CurrentSkyTarget applies when it decides whether a condition's SkyTarget composes
        // into the sky, so our gate opens and closes on precisely the frames vanilla's own darkening
        // does. It also gets the underground exclusion for free — DarkenedSkies, Eclipse AND
        // UnnaturalDarkness all set allowUnderground false, so none is ever reported on a cave map.
        //
        // NOT HiddenByOtherCondition, even though vanilla's ElectricityDisabled pairs the two. That one
        // reports `silencedByConditions` (DarkenedSkies is silenced by Anomaly's UnnaturalDarkness) and
        // governs the UI label and gameplay silencing — CurrentSkyTarget ignores it and darkens the sky
        // anyway, so consulting it here would re-open our gate on a map that is still visibly black.
        return condition.CanApplyOnMap(map);
    }

    // How many weathers this biome can actually roll, i.e. how many it lists at a commonality above
    // zero. Counting *possibilities* rather than entries matters: Biomes! Caverns lists vanilla's
    // Rain, FoggyRain and DryThunderstorm on its cavern biomes at commonality 0 precisely to suppress
    // them, so an entry count would read those caverns as having a climate when their author has said
    // the opposite.
    //
    // Reading the live def is also what makes this correct where reading the XML would not be: the
    // list here is already the INHERITED one, so `Undercave` arrives with both its own weather and the
    // `Underground` it inherits from `Biome_Underground`. Any offline reasoning about this rule has to
    // reproduce that merge or it will undercount — see WeatherDimmingMath.BiomeHasChangingWeather.
    private static int WeatherChoiceCount(BiomeDef biome)
    {
        List<WeatherCommonalityRecord> records = biome.baseWeatherCommonalities;
        if (records == null)
            return 0;

        int count = 0;
        foreach (WeatherCommonalityRecord record in records)
        {
            if (record != null && record.commonality > 0f)
                count++;
        }

        return count;
    }
}
