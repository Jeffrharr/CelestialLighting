using System;
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
// MEMOISED PER FRAME PER MAP (§28). This header used to say "NOT CACHED, deliberately", on the
// grounds that "every call site is per-map-per-frame at worst". That premise was wrong, and it was
// measured wrong rather than argued wrong: a Circinus arm over a 540-frame window counted HasSky at
// 18,515 calls and SkyBlackedOut at 10,600 — 34.3 and 19.6 times PER FRAME, not once. The reason is
// structural and was always visible in the call graph: SkyManager.CurrentSkyTarget evaluates
// WeatherWorker.CurSkyTarget twice, eight of our postfixes hang off it, and each of those asks two
// gates. Two by eight by two is most of the count; the draw lanes supply the rest. Together the two
// gates were 12.9% of the whole per-frame sky budget, spent re-deriving an answer that cannot change
// inside one frame.
//
// THE OLD OBJECTIONS WERE TO A DIFFERENT CACHE. Both are answered by keying on the frame stamp
// rather than on the subject:
//
//   "A cache keyed on BiomeDef ... would have to be invalidated against the harness's own SetBiome
//   step" — a BiomeDef key would; a frame key does not. SetBiome mutates map.Biome and the next
//   frame carries a new stamp, so the entry is superseded without anything having to know that
//   biomes are mutable. weather_dimming_skyless.json sweeps biomes inside one run and is unaffected.
//
//   "SkyBlackedOut ... would need the invalidation hooks (RegisterCondition / OnConditionEnd)" — it
//   would if the key were the condition list. GeometryStamp carries the TICK as well as the frame,
//   and conditions are added and removed on the tick, so a stamp that is still valid is a stamp
//   across which no condition can have moved. That is why vanilla's cachedAlwaysDark needs two hooks
//   and this needs none: vanilla caches across ticks and we do not.
//
// The memo is GeometryMemo, unchanged and shared with §12's solar/lunar geometry, because this is
// the identical bug in a different subsystem — the same ~14-evaluations-per-frame call graph, for
// the gates instead of the trigonometry. Reusing it also inherits its one-entry-per-map keying on
// uniqueID (a removed map cannot be kept alive) and its static-delegate calling convention, which
// is what keeps a hit from allocating.
//
// DrawsShadows is NOT memoised: it is two field reads and no loop, so a dictionary lookup and a
// stamp compare would cost more than it does. IsEnclosed is not memoised either, because it is a
// field read composed onto HasSky, and memoising HasSky already removes everything expensive
// underneath it.
public static class MapSky
{
    // One memo per gate rather than one memo of a packed pair. The two are asked from different call
    // sites in different lanes -- a shadow patch asks SkyBlackedOut and never HasSky -- so a shared
    // entry would compute the gate its caller did not ask for, which for SkyBlackedOut means walking
    // the whole GameConditionManager chain on behalf of a caller that only wanted the biome.
    private static readonly GeometryMemo<bool> HasSkyMemo = new GeometryMemo<bool>();
    private static readonly GeometryMemo<bool> BlackedOutMemo = new GeometryMemo<bool>();

    // Held as static delegates for the reason GeometryMemo's own header gives: Get takes arg and
    // compute separately so a caller can hand it a delegate it already owns. Building the lambda at
    // the call site instead would allocate a closure per call, which on a 34-calls-a-frame path is
    // the cost this change exists to remove, reintroduced in a different shape.
    private static readonly Func<Map, bool> ComputeHasSky = ComputeHasSkyFor;
    private static readonly Func<Map, bool> ComputeBlackedOut = ComputeBlackedOutFor;

    // Whether weather can roll overhead — see MapSkyMath.HasSky. This is §13's question. If you are
    // reaching for it to decide whether a SKY effect applies, you want IsEnclosed below instead:
    // this one is false in orbit, which has no weather but a perfect view of the sky.
    public static bool HasSky(Map map)
    {
        // Null map and no-TickManager both bypass the memo rather than being cached under a made-up
        // key. The first has no uniqueID to key on; the second is every context outside a running
        // game (main menu, world generation, mod init), where FrameStamp.Current would dereference a
        // null Find.TickManager. Both fall through to exactly the pre-memo code path, so the gate
        // answers off-game precisely what it answered before.
        if (map == null || Find.TickManager == null)
            return ComputeHasSkyFor(map);

        return HasSkyMemo.Get(map.uniqueID, FrameStamp.Current(), map, ComputeHasSky);
    }

    private static bool ComputeHasSkyFor(Map map)
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
    public static bool SkyBlackedOut(Map map)
    {
        if (map == null || Find.TickManager == null)
            return ComputeBlackedOutFor(map);

        return BlackedOutMemo.Get(map.uniqueID, FrameStamp.Current(), map, ComputeBlackedOut);
    }

    private static bool ComputeBlackedOutFor(Map map) => AnyCondition(map, ConditionTest.BlacksOutSky);

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
    public static bool UnnaturalDarknessActive(Map map) => AnyCondition(map, ConditionTest.UnnaturalDarkness);

    // Whether vanilla's `Eclipse` specifically is live over this map right now — the same narrow shape
    // as UnnaturalDarknessActive above, and narrow for the same reason. SkyBlackedOut deliberately
    // EXCLUDES the eclipse (see MapSkyMath.ConditionBlacksOutSky's carve-out: an eclipse covers the sun
    // while leaving the sky transparent, which is why stars come out during a total one), so it cannot
    // answer this and a caller must ask directly.
    //
    // Gated on the DEF, not the class. GameCondition_NoSunlight is also Royalty's SunBlocker machine and
    // Odyssey's DarkenedSkies — an artificial blackout and a sulfur overcast respectively, neither of
    // which has any claim to being "as bright as night". Only the celestial event does. This is the same
    // technique, in the same direction, as Patch_EclipseDarkening's own `def != GameConditionDefOf.Eclipse`
    // guard.
    //
    // §7a's MinNightBrightness floor (Patch_PitchBlackOverlay, via
    // NightRadianceMath.EclipseFlooredMinNightBrightness) is the one caller.
    //
    // CanApplyOnMap for the same reason SkyBlackedOut uses it: it is exactly the filter
    // SkyManager.CurrentSkyTarget applies when deciding whether a condition composes into the sky, so
    // this reports true on precisely the frames vanilla's own darkening happens — and it gets the
    // underground exclusion for free, since Eclipse sets allowUnderground false.
    public static bool EclipseActive(Map map) => AnyCondition(map, ConditionTest.Eclipse);

    // Shared walk of the manager chain the same way vanilla's own GameConditionManager.
    // ElectricityDisabled does: a map's own conditions, then the world's, which is where quest- and
    // planet-scale conditions live. Reading map.gameConditionManager.ActiveConditions alone would miss
    // those.
    // Which question the shared walk below is answering. An enum rather than the Predicate<GameCondition>
    // this used to take, because every one of the three predicates needed the map and so captured it:
    // a captured variable means the compiler builds a closure object and a delegate at the CALL site,
    // on every call, and these are called tens of times a frame. The enum passes the map down as an
    // ordinary argument instead, so the walk allocates nothing and the three tests stay in one place.
    private enum ConditionTest
    {
        BlacksOutSky,
        UnnaturalDarkness,
        Eclipse,
    }

    private static bool AnyCondition(Map map, ConditionTest test)
    {
        if (map == null)
            return false;

        GameConditionManager manager = map.gameConditionManager;
        while (manager != null)
        {
            if (AnyConditionMatches(manager.ActiveConditions, test, map))
                return true;

            manager = manager.Parent;
        }

        return false;
    }

    private static bool AnyConditionMatches(List<GameCondition> conditions, ConditionTest test, Map map)
    {
        if (conditions == null)
            return false;

        for (int i = 0; i < conditions.Count; i++)
        {
            GameCondition condition = conditions[i];
            if (condition != null && Matches(condition, test, map))
                return true;
        }

        return false;
    }

    // The three tests the walk can be asked for, kept verbatim from the lambdas they replace so the
    // refactor is provably behaviour-preserving: same order of operations, same short-circuits, same
    // CanApplyOnMap filter, and the same reasons — see BlacksOutSky below, and the two public gates
    // above for why each narrow test is a def/class check ANDed with CanApplyOnMap.
    private static bool Matches(GameCondition condition, ConditionTest test, Map map)
    {
        switch (test)
        {
            case ConditionTest.BlacksOutSky:
                return BlacksOutSky(condition, map);

            case ConditionTest.UnnaturalDarkness:
                return condition is GameCondition_UnnaturalDarkness && condition.CanApplyOnMap(map);

            default:
                return condition.def == GameConditionDefOf.Eclipse && condition.CanApplyOnMap(map);
        }
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
