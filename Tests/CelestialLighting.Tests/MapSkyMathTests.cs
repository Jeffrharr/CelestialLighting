using CelestialLighting;
using NUnit.Framework;

namespace CelestialLighting.Tests;

// Covers the map-kind gates (MapSkyMath) — the rules that stop sky- and sun-sourced effects applying
// to maps that cannot see either. Grown in two passes: the skyless gate for Biomes! Caverns
// compatibility (a rock ceiling), then ConditionBlacksOutSky for issue #35 (an opaque sky on an
// otherwise ordinary open-air map).
//
// The interesting cases here are the ones with real biomes behind them, so each is named for the
// content that motivates it. The composed rule is trivial; what is worth pinning is which real map
// lands on which side of it, because that is what silently regressed before.
[TestFixture]
public class MapSkyMathTests
{
    // --- HasSky: can weather roll overhead? (§13's question) ---

    [Test]
    public void HasSky_NoBiome_IsSkyless()
    {
        // Pocket maps mid-generation hand back a null biome. Skyless is the conservative answer:
        // declining to apply an effect leaves vanilla's own rendering untouched.
        Assert.That(MapSkyMath.HasSky(false, false, 99), Is.False);
    }

    [Test]
    public void HasSky_DisableSkyLighting_IsSkyless()
    {
        // Vanilla's Undercave. The flag wins outright regardless of how many weathers are offered —
        // Undercave offers two (its own plus the Underground it inherits from Biome_Underground).
        Assert.That(MapSkyMath.HasSky(true, true, 2), Is.False);
    }

    [TestCase(0)]
    [TestCase(1)]
    public void HasSky_FewerThanTwoWeathers_IsSkyless(int weatherChoices)
    {
        // Biomes! Caverns' BMT_CrystalCaverns / BMT_EarthenDepths / BMT_FungalForest all sit at
        // exactly 1: one cavern weather at commonality 100, with vanilla's Rain/FoggyRain/
        // DryThunderstorm listed at 0 specifically to suppress them. Counting nonzero commonalities
        // rather than entries is what keeps them on this side of the line.
        Assert.That(MapSkyMath.HasSky(true, false, weatherChoices), Is.False);
    }

    [TestCase(2)]
    [TestCase(7)]
    public void HasSky_TwoOrMoreWeathers_HasSky(int weatherChoices)
    {
        Assert.That(MapSkyMath.HasSky(true, false, weatherChoices), Is.True);
    }

    // --- IsEnclosed: is there a ceiling between this map and the sky? ---

    [Test]
    public void IsEnclosed_Cavern_IsEnclosed()
    {
        // A Biomes! Caverns cavern: no weather to roll, and not a vacuum. This is the case the whole
        // gate exists for.
        Assert.That(MapSkyMath.IsEnclosed(hasSky: false, inVacuum: false), Is.True);
    }

    [Test]
    public void IsEnclosed_Orbit_IsNotEnclosed()
    {
        // THE REGRESSION THIS FILE EXISTS TO PREVENT. Orbit is skyless by the weather rule — it
        // offers exactly one weather — but it has no ceiling at all, just no atmosphere. Folding the
        // two questions together would have silently stripped every sky effect from orbit while
        // nominally fixing caves. Orbit's own treatment is separate work; until then it must behave
        // exactly as it does today.
        Assert.That(MapSkyMath.IsEnclosed(hasSky: false, inVacuum: true), Is.False);
    }

    [Test]
    public void IsEnclosed_OpenAir_IsNotEnclosed()
    {
        Assert.That(MapSkyMath.IsEnclosed(hasSky: true, inVacuum: false), Is.False);
    }

    [Test]
    public void IsEnclosed_HasSkyWins_EvenInVacuum()
    {
        // Degenerate combination (a vacuum biome offering two weathers) — pinned only so the
        // composition is total and a future edit cannot make "enclosed" depend on vacuum alone.
        Assert.That(MapSkyMath.IsEnclosed(hasSky: true, inVacuum: true), Is.False);
    }

    // --- The two gates never collapse into one ---

    [Test]
    public void EnclosedAndSkyless_AgreeExceptInVacuum()
    {
        // Restates the invariant in one place: IsEnclosed is HasSky's complement everywhere EXCEPT
        // vacuum, which is the single reason both predicates exist rather than one.
        foreach (bool hasSky in new[] { false, true })
        {
            Assert.That(
                MapSkyMath.IsEnclosed(hasSky, inVacuum: false), Is.EqualTo(!hasSky),
                "outside a vacuum, enclosed must be exactly the complement of has-sky");
        }

        Assert.That(
            MapSkyMath.IsEnclosed(hasSky: false, inVacuum: true), Is.False,
            "in a vacuum, skyless must NOT imply enclosed");
    }

    // --- ConditionBlacksOutSky: is the sky opaque right now? (issue #35) ---

    [Test]
    public void ConditionBlacksOutSky_DarkenedSkies_BlacksOut()
    {
        // Odyssey's DarkenedSkies, the headline case both ways round: permanent on Glowforest via
        // biomeMapConditions, and timed on any map with an AncientSmokeVent (~3 days on / 4 off).
        // Same def, same class, so one test covers both — which is the point of keying on the class.
        Assert.That(
            MapSkyMath.ConditionBlacksOutSky(
                isNoSunlightCondition: true, isUnnaturalDarkness: false, isEclipse: false),
            Is.True);
    }

    [Test]
    public void ConditionBlacksOutSky_Eclipse_DoesNotBlackOut()
    {
        // THE CARVE-OUT, and the one failure mode nothing downstream could catch. Eclipse is the same
        // GameCondition_NoSunlight class, and §10/§10a exist to reshape it — so a gate on the class
        // alone would switch our own eclipse handling off while every effect probe still read zero,
        // because an eclipse sky is near black either way.
        Assert.That(
            MapSkyMath.ConditionBlacksOutSky(
                isNoSunlightCondition: true, isUnnaturalDarkness: false, isEclipse: true),
            Is.False);
    }

    [Test]
    public void ConditionBlacksOutSky_UnnaturalDarkness_BlacksOut()
    {
        // Anomaly's UnnaturalDarkness is GameCondition_ForceWeather, not GameCondition_NoSunlight —
        // isNoSunlightCondition is false here — yet its own SkyTarget returns the identical
        // GameCondition_NoSunlight.EclipseSkyColors at glow 0, composed through the same LerpDarken as
        // every other condition. Missing this term means the gate silently misses a fifth blackout
        // source: §17's design doc originally enumerated only four.
        Assert.That(
            MapSkyMath.ConditionBlacksOutSky(
                isNoSunlightCondition: false, isUnnaturalDarkness: true, isEclipse: false),
            Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ConditionBlacksOutSky_OtherCondition_NeverBlacksOut(bool isEclipse)
    {
        // Every other condition a colony carries — a solar flare, a toxic fallout, a psychic drone.
        // The class term is necessary, not just sufficient: a def-name check that forgot it would let
        // any non-eclipse condition suppress the whole mod.
        Assert.That(
            MapSkyMath.ConditionBlacksOutSky(
                isNoSunlightCondition: false, isUnnaturalDarkness: false, isEclipse),
            Is.False);
    }

    // --- AmbientGlow: a cave has no day ---

    [TestCase(0f)]
    [TestCase(0.0996f)]
    [TestCase(1f)]
    public void AmbientGlow_Enclosed_IsConstantWhateverTheSunIsDoing(float diurnal)
    {
        // The whole point: measured on a live enclosed map, sky glow ran 1.00 at noon and 0.00 at
        // midnight, so caves were legible by day and black by night. All three inputs must now
        // produce the same output, because a sealed cave has no day.
        Assert.That(
            MapSkyMath.AmbientGlow(enclosed: true, diurnalGlow: diurnal),
            Is.EqualTo(MapSkyMath.EnclosedAmbientGlow));
    }

    [TestCase(0f)]
    [TestCase(0.0996f)]
    [TestCase(1f)]
    public void AmbientGlow_NotEnclosed_PassesTheSunStraightThrough(float diurnal)
    {
        // An open map keeps its day/night cycle untouched, including orbit, which is not enclosed.
        Assert.That(
            MapSkyMath.AmbientGlow(enclosed: false, diurnalGlow: diurnal), Is.EqualTo(diurnal));
    }

    [Test]
    public void AmbientGlow_HandsSeventeenBAFullSkyToScale()
    {
        // Full rather than pre-dimmed, so that §7b's minimum-indoor-brightness cap is the single
        // thing deciding cave brightness and both presets keep meaning what they advertise:
        // Cinematic 0.50 -> a lit cave at every hour, Realistic 0.0 -> black at every hour.
        Assert.That(MapSkyMath.EnclosedAmbientGlow, Is.EqualTo(1f));
    }

    // --- WeatherRespondsToSun: the cave-weather filter ---------------------------------------
    //
    // The palettes below are transcribed from the shipped 1.6 XML rather than invented, because the
    // claim under test is about real content: "a cave weather is one whose palette does not move".
    // If Ludeon ever gives Underground a diurnal cycle these fixtures go stale, and a stale fixture
    // here is the signal that the rule stopped describing the game.

    // Core Weathers.xml, `Clear`. The reference sun-responsive weather.
    private static MapSkyMath.SkyPalette ClearDay() =>
        new MapSkyMath.SkyPalette(1f, 1f, 1f, 0.718f, 0.745f, 0.757f, 1f, 1f, 1f);
    private static MapSkyMath.SkyPalette ClearDusk() =>
        new MapSkyMath.SkyPalette(0.858f, 0.650f, 0.423f, 0.955f, 0.886f, 0.914f, 0.8f, 0.8f, 0.8f);
    private static MapSkyMath.SkyPalette ClearNightEdge() =>
        new MapSkyMath.SkyPalette(0.482f, 0.603f, 0.682f, 0.92f, 0.92f, 0.92f, 0.6f, 0.6f, 0.6f);
    private static MapSkyMath.SkyPalette ClearNightMid() =>
        new MapSkyMath.SkyPalette(0.482f, 0.603f, 0.682f, 0.85f, 0.85f, 0.85f, 0.6f, 0.6f, 0.6f);

    // Core Weathers.xml, `Underground` — identical in all four blocks. Anomaly's `Undercave`
    // inherits this wholesale (ParentName="Weather_Underground", adding only a label and a sound),
    // which is the whole reason the old count saw two weathers where there is one palette.
    private static MapSkyMath.SkyPalette UndergroundAnyHour() =>
        new MapSkyMath.SkyPalette(0.3f, 0.4f, 0.4f, 0.85f, 0.85f, 0.85f, 0.6f, 0.6f, 0.6f);

    [Test]
    public void WeatherRespondsToSun_TrueForClear()
    {
        Assert.That(
            MapSkyMath.WeatherRespondsToSun(ClearDay(), ClearDusk(), ClearNightEdge(), ClearNightMid()),
            Is.True,
            "Clear is the reference diurnal weather — if this reads false the filter would strip the "
            + "sky from every ordinary biome");
    }

    [Test]
    public void WeatherRespondsToSun_FalseForUnderground()
    {
        MapSkyMath.SkyPalette flat = UndergroundAnyHour();

        Assert.That(MapSkyMath.WeatherRespondsToSun(flat, flat, flat, flat), Is.False,
            "vanilla's Underground palette is frozen across all four blocks — 'There is no weather "
            + "underground', as its own description says");
    }

    // The bug this filter was written for, expressed as the count the gate actually consumes: two
    // rollable weathers, both the same frozen cave palette, must not read as a climate.
    [Test]
    public void UndercaveBiome_HasNoSky_EvenWithTwoInheritedWeatherEntries()
    {
        MapSkyMath.SkyPalette flat = UndergroundAnyHour();
        int sunResponsive = 0;
        foreach (var _ in new[] { "Underground", "Undercave" })
        {
            if (MapSkyMath.WeatherRespondsToSun(flat, flat, flat, flat))
                sunResponsive++;
        }

        Assert.That(sunResponsive, Is.EqualTo(0));
        Assert.That(MapSkyMath.HasSky(biomeExists: true, disableSkyLighting: false, sunResponsive),
            Is.False,
            "Undercave's merged weather list holds two entries but only one palette, and that palette "
            + "never moves — counting entries called a sealed cave skyful");
    }

    // The pre-filter behaviour, pinned so the regression is legible: the SAME two entries counted
    // without the filter clear the threshold and hand the cave a sky.
    [Test]
    public void UndercaveBiome_UnfilteredCountIsWhatUsedToGiveItASky()
    {
        Assert.That(MapSkyMath.HasSky(biomeExists: true, disableSkyLighting: false, weatherChoices: 2),
            Is.True,
            "this is the old behaviour, kept as a witness — two counted entries is what "
            + "BiomeHasChangingWeather reads as a climate");
    }

    // A one-directional filter: it can subtract sky-capable weathers, never add them. A biome that
    // has a sky today keeps it unless EVERY weather it rolls is frozen.
    [Test]
    public void MixedBiome_KeepsItsSky_WhenOnlySomeWeathersAreFrozen()
    {
        MapSkyMath.SkyPalette flat = UndergroundAnyHour();

        bool caveWeather = MapSkyMath.WeatherRespondsToSun(flat, flat, flat, flat);
        bool realWeather = MapSkyMath.WeatherRespondsToSun(
            ClearDay(), ClearDusk(), ClearNightEdge(), ClearNightMid());

        int sunResponsive = (caveWeather ? 1 : 0) + (realWeather ? 1 : 0);

        Assert.That(sunResponsive, Is.EqualTo(1));
        Assert.That(MapSkyMath.HasSky(true, false, sunResponsive), Is.False,
            "one sun-responsive weather is still below BiomeHasChangingWeather's threshold — this "
            + "documents where the composed rule lands, which is NOT the same as the filter deciding it");
    }

    // Each of the three carried axes on its own is enough. Written as separate cases because a
    // regression that drops one axis from SkyPalette.MaxDifference would still pass a test that only
    // ever varies `sky`.
    [TestCase(0, TestName = "WeatherRespondsToSun_DetectsVariationInSky")]
    [TestCase(1, TestName = "WeatherRespondsToSun_DetectsVariationInShadow")]
    [TestCase(2, TestName = "WeatherRespondsToSun_DetectsVariationInOverlay")]
    public void WeatherRespondsToSun_DetectsVariationInEachCarriedAxis(int axis)
    {
        MapSkyMath.SkyPalette day = UndergroundAnyHour();
        MapSkyMath.SkyPalette moved = axis switch
        {
            0 => new MapSkyMath.SkyPalette(0.9f, 0.4f, 0.4f, 0.85f, 0.85f, 0.85f, 0.6f, 0.6f, 0.6f),
            1 => new MapSkyMath.SkyPalette(0.3f, 0.4f, 0.4f, 0.20f, 0.85f, 0.85f, 0.6f, 0.6f, 0.6f),
            _ => new MapSkyMath.SkyPalette(0.3f, 0.4f, 0.4f, 0.85f, 0.85f, 0.85f, 0.9f, 0.6f, 0.6f),
        };

        Assert.That(MapSkyMath.WeatherRespondsToSun(day, moved, day, day), Is.True);
    }

    // Each of the three compared blocks is checked against day, so a weather that only moves at
    // night-mid is still diurnal. Guards against a regression that compares day-to-dusk only.
    [TestCase(1, TestName = "WeatherRespondsToSun_ComparesDusk")]
    [TestCase(2, TestName = "WeatherRespondsToSun_ComparesNightEdge")]
    [TestCase(3, TestName = "WeatherRespondsToSun_ComparesNightMid")]
    public void WeatherRespondsToSun_ComparesEveryBlockAgainstDay(int movedBlock)
    {
        MapSkyMath.SkyPalette flat = UndergroundAnyHour();
        MapSkyMath.SkyPalette moved =
            new MapSkyMath.SkyPalette(0.3f, 0.9f, 0.4f, 0.85f, 0.85f, 0.85f, 0.6f, 0.6f, 0.6f);

        Assert.That(
            MapSkyMath.WeatherRespondsToSun(
                flat,
                movedBlock == 1 ? moved : flat,
                movedBlock == 2 ? moved : flat,
                movedBlock == 3 ? moved : flat),
            Is.True);
    }

    // The tolerance is an authoring-typo allowance, not a visibility threshold. A hand-slip stays a
    // cave; vanilla's smallest real diurnal step (Clear's overlay, 1.0 by day against 0.8 at dusk)
    // is a hundred times above it.
    [TestCase(0.0f, false)]
    [TestCase(0.0001f, false)]
    [TestCase(0.001f, false)]
    [TestCase(0.01f, true)]
    [TestCase(0.2f, true)]
    public void WeatherRespondsToSun_ToleranceSeparatesTyposFromCycles(float drift, bool expected)
    {
        MapSkyMath.SkyPalette day = UndergroundAnyHour();
        MapSkyMath.SkyPalette drifted =
            new MapSkyMath.SkyPalette(0.3f + drift, 0.4f, 0.4f, 0.85f, 0.85f, 0.85f, 0.6f, 0.6f, 0.6f);

        Assert.That(MapSkyMath.WeatherRespondsToSun(day, drifted, day, day), Is.EqualTo(expected));
    }
}
