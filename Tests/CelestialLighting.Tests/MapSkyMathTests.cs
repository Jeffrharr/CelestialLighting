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
}
