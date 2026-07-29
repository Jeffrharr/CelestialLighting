using System.Linq;
using System.Reflection;

namespace CelestialLighting.Tests;

/// <summary>
/// Offline unit tests for §18c's pure core (Source/ShadowFillMath.cs, linked into this project so
/// these exercise the shipped file rather than a copy). No RimWorld/Unity assembly required.
///
/// The subsystem is a split, so the tests are written as one, per §18's convention (Source/Vacuum.cs):
/// every vacuum expectation is pinned in the SAME [TestCase] as its sea-level counterpart, so a
/// regression in either shows up as a diverging pair rather than as one number quietly matching a
/// stale expectation on its own.
/// </summary>
[TestFixture]
public class ShadowFillMathTests
{
    // Vanilla's daylight glow at a high sun, i.e. the lit ground a daytime umbra is a multiply
    // against.
    private const float FullDaylight = 1f;

    // The night light budget in each regime, as §18b publishes it, for the shipped defaults with no
    // moon up. Spelled through NightRadianceMath.NightFloorGlow rather than as 0.0317 / 0.0400
    // because §18c CONSUMES that floor and must never hold a second copy of it: when §18b's model
    // moves, these move with it and the expectations below are what tell us by how much.
    private static float NightFloor(bool inVacuum, float moonlightGlow = 0f) =>
        NightRadianceMath.NightFloorGlow(
            NightRadianceMath.DefaultStarlightGlow,
            NightRadianceMath.DefaultAirglowGlow,
            moonlightGlow,
            NightRadianceMath.DefaultMaxMoonlightGlow,
            inVacuum);

    // --- The split itself, as a diverging pair ---

    [TestCase(true, 0.031668f, TestName = "UmbraFill_Vacuum_FallsToTheNightBudget")]
    [TestCase(false, 0.740126f, TestName = "UmbraFill_SeaLevel_KeepsItsSkylightFill")]
    public void DaytimeUmbraKeepIsTheFillThatRegimeActuallyHas(bool inVacuum, float expectedKeep)
    {
        // The one gated entry point, called exactly as Patch_WeatherShadowColor calls it: the live
        // sky palette goes in either way, and the vacuum arm declines to look at it.
        UmbraFill fill = ShadowFillMath.DaytimeUmbraFill(
            ShadowFillMath.SeaLevelUmbraR, ShadowFillMath.SeaLevelUmbraG, ShadowFillMath.SeaLevelUmbraB,
            NightFloor(inVacuum), FullDaylight, inVacuum);

        // Sea level keeps 74% of the lit ground (a 26% darkening); vacuum keeps 3.2% (a 97%
        // darkening). The pair IS the subsystem.
        Assert.That(Luminance(fill), Is.EqualTo(expectedKeep).Within(0.0005f));
    }

    [Test]
    public void VacuumIsHarsherThanSeaLevelByAnOrderOfMagnitude()
    {
        // Stated as a ratio and as a floor rather than an equality, so §18b's night budget is free to
        // move without breaking this — while a vacuum umbra quietly softened back toward the
        // atmospheric value still fails. This is the assertion that guards the issue's "do not soften
        // it back for taste".
        float vacuum = ShadowFillMath.VacuumUmbraKeep(NightFloor(inVacuum: true), FullDaylight);
        Assert.That(ShadowFillMath.SeaLevelUmbraKeep / vacuum, Is.GreaterThan(15f));
    }

    [TestCase(true, TestName = "SkyPaletteIsIgnored_InVacuum")]
    [TestCase(false, TestName = "SkyPaletteIsHonoured_AtSeaLevel")]
    public void OnlyTheAtmosphericArmConsultsTheSkyPalette(bool inVacuum)
    {
        // Hand the two arms a wildly different palette (vanilla's near-white non-Clear 0.92) and see
        // which one notices. "Drop the sky tint" is structural in DaytimeUmbraFill — the vacuum arm
        // never reads the arguments — and this is what pins that it stays structural.
        UmbraFill clear = ShadowFillMath.DaytimeUmbraFill(
            ShadowFillMath.SeaLevelUmbraR, ShadowFillMath.SeaLevelUmbraG, ShadowFillMath.SeaLevelUmbraB,
            NightFloor(inVacuum), FullDaylight, inVacuum);
        UmbraFill overcast = ShadowFillMath.DaytimeUmbraFill(
            0.92f, 0.92f, 0.92f, NightFloor(inVacuum), FullDaylight, inVacuum);

        if (inVacuum)
            Assert.That(Luminance(overcast), Is.EqualTo(Luminance(clear)).Within(1e-6f));
        else
            Assert.That(Luminance(overcast), Is.GreaterThan(Luminance(clear) + 0.1f));
    }

    [Test]
    public void VacuumUmbraIsNeutralGrey()
    {
        // Both surviving sources (unextinguished starlight, moonlight) are neutral, and the
        // reflected-planet term that would have tinted it is the one the deck cannot see — see
        // ShadowFillMath's header. A tint creeping in here would fight §9's low-light desaturation
        // for the same look, exactly as §6a documents for the night.
        UmbraFill fill = ShadowFillMath.DaytimeUmbraFill(
            ShadowFillMath.SeaLevelUmbraR, ShadowFillMath.SeaLevelUmbraG, ShadowFillMath.SeaLevelUmbraB,
            NightFloor(inVacuum: true), FullDaylight, inVacuum: true);

        Assert.Multiple(() =>
        {
            Assert.That(fill.G, Is.EqualTo(fill.R));
            Assert.That(fill.B, Is.EqualTo(fill.R));
        });
    }

    // --- The umbra is an ABSOLUTE floor, not a relative one ---

    [TestCase(1.0f)]
    [TestCase(0.7f)]
    [TestCase(0.3f)]
    [TestCase(0.1f)]
    public void VacuumUmbraRendersAtTheNightBudgetWhateverTheDaylightIs(float litGlow)
    {
        // colors.shadow is a MULTIPLY on already-lit ground, so the whole reason VacuumUmbraKeep
        // divides by the lit glow is to make the product land ON the floor rather than under it. A
        // vacuum shadow at a low sun must be as bright as a vacuum night, not several times darker
        // than one — "a shadow darker than the darkness" is the bug this pins.
        float floor = NightFloor(inVacuum: true);
        Assert.That(litGlow * ShadowFillMath.VacuumUmbraKeep(floor, litGlow),
            Is.EqualTo(floor).Within(0.0005f));
    }

    [Test]
    public void VacuumUmbraStopsDarkeningOnceTheFillReachesTheDirectBeam()
    {
        // The one clamp in §18c, and a bound rather than a taste call: with the fill at or above the
        // lit ground there is no contrast left to draw, and a keep above 1 would render a shadow
        // BRIGHTER than the ground beside it.
        Assert.Multiple(() =>
        {
            Assert.That(ShadowFillMath.VacuumUmbraKeep(0.04f, 0.04f), Is.EqualTo(1f));
            Assert.That(ShadowFillMath.VacuumUmbraKeep(0.2f, 0.05f), Is.EqualTo(1f));
            Assert.That(ShadowFillMath.VacuumUmbraKeep(0f, 0f), Is.EqualTo(1f));
        });
    }

    [Test]
    public void VacuumUmbraIsTrueBlackWhenTheNightBudgetIsZero()
    {
        // Deliberately not floored above 0: the playability clamp for darkness is
        // NightRadianceSettings.MinNightBrightness, one knob in one place, and a second floor here
        // would let the two disagree.
        Assert.That(ShadowFillMath.VacuumUmbraKeep(0f, FullDaylight), Is.EqualTo(0f));
    }

    [Test]
    public void NegativeNightBudgetIsTreatedAsZeroRatherThanInvertingTheShadow()
    {
        Assert.That(ShadowFillMath.VacuumUmbraKeep(-0.5f, FullDaylight), Is.EqualTo(0f));
    }

    [Test]
    public void MoonlitVacuumUmbraIsBrighterThanNewMoonInBothRegimes()
    {
        // §18c's answer to "is a vacuum shadow just black". It tracks the moon, and it tracks it
        // HARDER than the ground does, because §18c's moon term is unextinguished (VacuumRadianceMath
        // .MoonlightGlow) while airglow — the term that props up a sea-level new moon — is gone.
        float fullMoon = NightRadianceMath.DefaultMaxMoonlightGlow;

        float vacuumNew = ShadowFillMath.VacuumUmbraKeep(NightFloor(true), FullDaylight);
        float vacuumFull = ShadowFillMath.VacuumUmbraKeep(NightFloor(true, fullMoon), FullDaylight);
        float seaNew = NightFloor(false);
        float seaFull = NightFloor(false, fullMoon);

        Assert.Multiple(() =>
        {
            Assert.That(vacuumFull, Is.GreaterThan(vacuumNew * 6f),
                "a full moon must lift the vacuum umbra well clear of black");
            Assert.That(vacuumFull / vacuumNew, Is.GreaterThan(seaFull / seaNew),
                "orbit must have MORE dynamic range between its darkest and brightest nights than the ground");
        });
    }

    // --- The "must not touch" guarantee: the geometric penumbra is identical in both regimes ---

    // The geometric half of a sun-up shadow, exactly as Patch_ShadowStrength composes it in its
    // sun-up branch: the elevation existence ramp times the angular-size penumbra contrast factor. It
    // takes no inVacuum because there is none to take — see the two structural guards below.
    private static float SunShadowStrength(float elevationDegrees) =>
        Formulas.ShadowIntensityFromElevation(elevationDegrees)
        * PenumbraMath.PenumbraContrastFactor(elevationDegrees);

    [TestCase(75f)]
    [TestCase(45f)]
    [TestCase(20f)]
    [TestCase(8f)]
    [TestCase(3f)]
    [TestCase(0.5f)]
    public void GeometricPenumbraIsUnchangedBetweenVacuumAndSeaLevel(float elevationDegrees)
    {
        // Matched sun elevations, both regimes, in one case — the pairing §18's convention asks for
        // and the "this issue must not touch PenumbraMath" guarantee stated as an assertion.
        float strength = SunShadowStrength(elevationDegrees);

        float seaLevelKeep = ShadowFillMath.SeaLevelUmbraKeep;
        float vacuumKeep = ShadowFillMath.VacuumUmbraKeep(NightFloor(inVacuum: true), FullDaylight);

        float seaLevel = ShadowFillMath.RenderedUmbraKeep(seaLevelKeep, strength);
        float vacuum = ShadowFillMath.RenderedUmbraKeep(vacuumKeep, strength);

        // Each regime's darkening is (shared geometric strength) x (its own fill deficit): the
        // geometry enters both products identically and at full precision.
        Assert.That(1f - seaLevel, Is.EqualTo(strength * (1f - seaLevelKeep)).Within(1e-6f));
        Assert.That(1f - vacuum, Is.EqualTo(strength * (1f - vacuumKeep)).Within(1e-6f));

        // Which means the RATIO between the two darkenings is a pure function of the fill and is
        // therefore CONSTANT across the whole sky. That is the real guarantee: if anything
        // vacuum-derived ever crept into the softening — a widened penumbra, an extra elevation term,
        // a scattering-shaped taper — the ratio would start to vary with elevation and every case
        // here would fail at once.
        float expectedRatio = (1f - vacuumKeep) / (1f - seaLevelKeep);
        Assert.That((1f - vacuum) / (1f - seaLevel), Is.EqualTo(expectedRatio).Within(1e-4f),
            $"vacuum/sea-level darkening ratio drifted at elevation {elevationDegrees}");
    }

    [Test]
    public void PenumbraMathAdmitsNoVacuumInput()
    {
        // Structural half of the guarantee above, and the one that fails LOUDLY if a later change
        // tries to make the penumbra vacuum-aware: PenumbraMath's entire public surface is a function
        // of sun elevation and nothing else. The sun subtends the same half-degree with or without
        // air in the way, so there is nothing for an inVacuum to do in there — and under §18's
        // convention a vacuum-aware pure function MUST take one, which is what makes this test able
        // to detect the change at all.
        MethodInfo[] methods = typeof(PenumbraMath)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);

        Assert.That(methods, Is.Not.Empty, "PenumbraMath's public surface vanished");

        foreach (MethodInfo method in methods)
        {
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                Assert.That(parameter.ParameterType, Is.EqualTo(typeof(float)),
                    $"PenumbraMath.{method.Name} grew a non-elevation parameter '{parameter.Name}'");
                Assert.That(parameter.Name, Is.EqualTo("elevationDegrees"),
                    $"PenumbraMath.{method.Name} grew a non-elevation parameter '{parameter.Name}'");
            }
        }
    }

    [Test]
    public void VacuumUmbraAdmitsNoElevationInput()
    {
        // The mirror-image guard. §18c changes WHAT FILLS a shadow, never how its edge behaves, so
        // its entry point must stay blind to where the sun is.
        MethodInfo? method = typeof(ShadowFillMath).GetMethod(nameof(ShadowFillMath.VacuumUmbraKeep));
        Assert.That(method, Is.Not.Null);

        string[] parameters = method!.GetParameters().Select(p => p.Name!).ToArray();
        Assert.That(parameters, Is.EqualTo(new[] { "nightFloorGlow", "litGlow" }),
            "VacuumUmbraKeep must depend on the fill and the lit ground, and on nothing else");
    }

    [Test]
    public void DaytimeUmbraFillTakesInVacuumLastAndRequired()
    {
        // §18's gate convention, pinned rather than trusted: last parameter, required, never
        // defaulted — a defaulted gate lets a new call site silently opt out of the whole epic.
        MethodInfo? method = typeof(ShadowFillMath).GetMethod(nameof(ShadowFillMath.DaytimeUmbraFill));
        Assert.That(method, Is.Not.Null);

        ParameterInfo last = method!.GetParameters().Last();
        Assert.Multiple(() =>
        {
            Assert.That(last.Name, Is.EqualTo("inVacuum"));
            Assert.That(last.ParameterType, Is.EqualTo(typeof(bool)));
            Assert.That(last.HasDefaultValue, Is.False);
        });
    }

    // --- RenderedUmbraKeep, the shared composition helper ---

    [TestCase(true)]
    [TestCase(false)]
    public void NoShadowStrengthMeansNoDarkeningInEitherRegime(bool inVacuum)
    {
        // SkyManager's lerp at strength 0 returns pure white: the fill is irrelevant when no shadow
        // is being cast at all.
        float keep = inVacuum
            ? ShadowFillMath.VacuumUmbraKeep(NightFloor(true), FullDaylight)
            : ShadowFillMath.SeaLevelUmbraKeep;
        Assert.That(ShadowFillMath.RenderedUmbraKeep(keep, 0f), Is.EqualTo(1f).Within(0.0005f));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void FullShadowStrengthRendersExactlyTheUmbraKeep(bool inVacuum)
    {
        float keep = inVacuum
            ? ShadowFillMath.VacuumUmbraKeep(NightFloor(true), FullDaylight)
            : ShadowFillMath.SeaLevelUmbraKeep;
        Assert.That(ShadowFillMath.RenderedUmbraKeep(keep, 1f), Is.EqualTo(keep).Within(0.0005f));
    }

    [TestCase(-0.5f, 0.5f, 0.5f)]  // a negative keep cannot darken past black
    [TestCase(1.5f, 0.5f, 1f)]     // a keep above 1 cannot render a shadow brighter than the ground
    [TestCase(0.04f, 2f, 0.04f)]   // a strength above 1 clamps to the full umbra
    public void RenderedUmbraKeepIsClampedAtBothEnds(float umbraKeep, float strength, float expected)
    {
        Assert.That(ShadowFillMath.RenderedUmbraKeep(umbraKeep, strength),
            Is.EqualTo(expected).Within(0.0005f));
    }

    private static float Luminance(UmbraFill fill) => EaveShadeMath.ShadowKeep(fill.R, fill.G, fill.B);
}
