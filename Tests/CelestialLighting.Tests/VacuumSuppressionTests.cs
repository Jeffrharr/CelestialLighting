using System;

namespace CelestialLighting.Tests;

/// <summary>
/// Offline coverage for the §18a vacuum gate (DESIGN.md §18, adapter in Source/Vacuum.cs): the three
/// atmospheric colour effects that must collapse on an Odyssey space map — twilight colour, sky
/// colour temperature, and aurora tint.
///
/// Every case here pins BOTH halves of the gate at the same sun elevation: the vacuum value and its
/// sea-level counterpart. That is deliberate. Asserting only "vacuum == 0" passes just as happily
/// when the sea-level effect has itself regressed to zero, which would hide a broken subsystem
/// behind a green vacuum test. Pinning the pair means any regression in either half shows up as a
/// diverging pair rather than a single number quietly agreeing with a stale expectation.
///
/// Live A/B validation of these on an actual orbital map is blocked on
/// Jeffrharr/RimWorldTestHarness#17 (scenarios cannot currently reach the Orbit planet layer), so
/// this fixture is the whole verification story for §18a until that lands.
/// </summary>
[TestFixture]
public class VacuumSuppressionTests
{
    private const float Tolerance = 0.001f;

    // Vanilla's GenCelestial.CurCelestialSunGlow, replicated here so the sweeps below walk a
    // physically coherent sun rather than an arbitrary (glow, elevation) pairing: glow and elevation
    // in a real frame are two views of one sun position, and a gate that only looks right when they
    // disagree is not a gate we would trust. The formula is vanilla's published one-liner —
    // Clamp01(InverseLerp(0, 0.7, sin(elevation))) — and is already documented as such in
    // Formulas.cs's civil-twilight notes. Note what it throws away: it pins to exactly 0 the moment
    // the sun sets, which is precisely why the twilight subsystem needs elevation as a second input.
    private static float SunGlowAtElevation(float elevationDegrees) =>
        Clamp01(MathF.Sin(elevationDegrees * MathF.PI / 180f) / 0.7f);

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

    // --- Twilight colour (§2) — zero in vacuum, at every sun elevation ---
    //
    // Twilight IS scattering: sunlight lighting up air the ground can no longer see the sun through.
    // With no air the warm nudge is not shortened or narrowed, it is absent. Note the sea-level
    // column is nonzero across the whole dusk band including well below the geometric horizon (the
    // civil-twilight persistence term) — that below-horizon linger is the most atmospheric part of
    // the subsystem and goes with the rest.

    [TestCase(45f, 0f)] // high sun: glow is far outside the band, sea level already 0
    [TestCase(20f, 0.332200f)] // upper edge of the glow-keyed band
    [TestCase(10f, 0.389822f)] // near peak warmth
    [TestCase(5f, 0.195656f)] // fading toward sunset
    [TestCase(2f, 0.078346f)] // last of the above-horizon band
    [TestCase(-0.83f, 0.228250f)] // refraction-adjusted horizon: persistence has taken over
    [TestCase(-2f, 0.550000f)] // civil-twilight peak, the full latitude peak height
    [TestCase(-4f, 0.275000f)] // fading back down
    [TestCase(-6f, 0f)] // end of civil twilight: sea level has reached 0 on its own
    [TestCase(-30f, 0f)] // deep night
    public void TwilightWarmth_IsZeroInVacuum_AndPinnedAtSeaLevel(float elevation, float seaLevel)
    {
        float glow = SunGlowAtElevation(elevation);

        Assert.That(Formulas.TwilightWarmthFactor(glow, elevation, strength: 1f, inVacuum: true),
            Is.EqualTo(0f).Within(Tolerance),
            $"twilight survived into vacuum at elevation {elevation}");
        Assert.That(Formulas.TwilightWarmthFactor(glow, elevation, strength: 1f, inVacuum: false),
            Is.EqualTo(seaLevel).Within(Tolerance),
            $"sea-level twilight moved at elevation {elevation}");
    }

    // Latitude strength scales the sea-level factor but must not scale the vacuum one back up: the
    // gate is a hard zero, not a multiplier, so a polar orbital platform is exactly as untwilit as an
    // equatorial one.
    [TestCase(0f)]
    [TestCase(0.5f)]
    [TestCase(1f)]
    public void TwilightWarmth_VacuumIsZeroAtEveryLatitudeStrength(float strength)
    {
        for (float elevation = -30f; elevation <= 90f; elevation += 0.5f)
        {
            float glow = SunGlowAtElevation(elevation);
            Assert.That(Formulas.TwilightWarmthFactor(glow, elevation, strength, inVacuum: true),
                Is.EqualTo(0f).Within(Tolerance),
                $"twilight survived into vacuum at elevation {elevation}, strength {strength}");
        }
    }

    // The legacy glow-keyed-only path (CelestialLightingFeatures.CivilTwilightPersistence off) takes
    // the same gate, so turning that feature off cannot smuggle ground twilight back onto a space map.
    [TestCase(0.35f, 0.550000f)] // band peak
    [TestCase(0.2f, 0.314286f)]
    [TestCase(0.99f, 0f)] // full daylight, outside the band at sea level too
    public void TwilightFactor_LegacyGlowPath_IsZeroInVacuum_AndPinnedAtSeaLevel(float glow, float seaLevel)
    {
        Assert.That(Formulas.TwilightFactor(glow, strength: 1f, inVacuum: true),
            Is.EqualTo(0f).Within(Tolerance));
        Assert.That(Formulas.TwilightFactor(glow, strength: 1f, inVacuum: false),
            Is.EqualTo(seaLevel).Within(Tolerance));
    }

    // --- Sky colour temperature (§8) — pinned flat to the unreddened anchor ---
    //
    // Warm-at-low-sun is Rayleigh reddening through a long air path; the sun's own emitted spectrum
    // does not change as it descends. So the curve pins to ZenithKelvin (5772 K, the photospheric
    // anchor) at every elevation, and the tint strength — which is the path-length term — goes to
    // zero so no residual amber creeps in.

    [TestCase(45f, 4829.0f)]
    [TestCase(30f, 3886.0f)]
    [TestCase(10f, 2628.7f)]
    [TestCase(0f, SkyColorTemperature.HorizonKelvin)]
    [TestCase(-6f, SkyColorTemperature.HorizonKelvin)]
    [TestCase(-30f, SkyColorTemperature.HorizonKelvin)]
    [TestCase(90f, SkyColorTemperature.ZenithKelvin)]
    public void ColorTemperature_PinsToUnreddenedAnchorInVacuum_AndRampsAtSeaLevel(
        float elevation, float seaLevelKelvin)
    {
        // pressureFraction 1 throughout: this pair is about the §18 gate, so the §20 site-altitude
        // input is held at its sea-level identity value and the sea-level column keeps meaning
        // exactly what it meant before §20 existed. The separate claim — that pressureFraction 0
        // reaches the same place the gate does — is pinned in SkyColorTemperatureTests. Note the ramp
        // itself no longer takes an aerosol input at all: since §20c the aerosol's colour is applied
        // per channel outside this function, so this is the clean-air curve by construction.
        Assert.That(SkyColorTemperature.ColorTemperatureKelvin(elevation, pressureFraction: 1f, inVacuum: true),
            Is.EqualTo(SkyColorTemperature.ZenithKelvin).Within(0.5f),
            $"vacuum colour temperature varied with sun altitude at elevation {elevation}");
        Assert.That(SkyColorTemperature.ColorTemperatureKelvin(elevation, pressureFraction: 1f, inVacuum: false),
            Is.EqualTo(seaLevelKelvin).Within(0.5f),
            $"sea-level colour temperature moved at elevation {elevation}");
    }

    [TestCase(60f, 0f)] // daylight altitude: no tint at sea level either
    [TestCase(30f, 0.5f)]
    [TestCase(10f, 0.833333f)]
    [TestCase(0f, 1f)] // horizon: sea level is at maximum reddening
    [TestCase(-0.83f, 1f)]
    [TestCase(-4f, 0.386847f)]
    [TestCase(-6f, 0f)] // end of civil twilight
    [TestCase(-30f, 0f)]
    public void SkyColorTintStrength_IsZeroInVacuum_AndPinnedAtSeaLevel(float elevation, float seaLevel)
    {
        Assert.That(SkyColorTemperature.TintStrength(elevation, pressureFraction: 1f, inVacuum: true),
            Is.EqualTo(0f).Within(Tolerance),
            $"vacuum sky tint was applied at elevation {elevation}");
        Assert.That(SkyColorTemperature.TintStrength(elevation, pressureFraction: 1f, inVacuum: false),
            Is.EqualTo(seaLevel).Within(Tolerance),
            $"sea-level sky tint moved at elevation {elevation}");
    }

    // Pinning the Kelvin alone would NOT make the effect flat: the Helland fit puts 5772 K near
    // (1.00, 0.95, 0.90), not at pure white, so an elevation-dependent blend toward it would still
    // creep amber into the sky as the sun dropped. This pins that the vacuum colour is constant
    // across the whole sweep — and, together with the zero tint strength above, that nothing of it
    // is ever actually blended in.
    [Test]
    public void SkyColorForElevation_IsConstantInVacuum_ButVariesAtSeaLevel()
    {
        SkyColorTemperature.Rgb anchor = SkyColorTemperature.SkyColorForElevation(
            0f, pressureFraction: 1f, aerosolFraction: 0f,
            angstromExponent: AerosolSpectrum.ReferenceAngstromExponent, inVacuum: true);
        for (float elevation = -30f; elevation <= 90f; elevation += 2.5f)
        {
            SkyColorTemperature.Rgb vacuum = SkyColorTemperature.SkyColorForElevation(
                elevation, pressureFraction: 1f, aerosolFraction: 0f,
                angstromExponent: AerosolSpectrum.ReferenceAngstromExponent, inVacuum: true);
            Assert.That(vacuum.R, Is.EqualTo(anchor.R).Within(Tolerance), $"R varied at {elevation}");
            Assert.That(vacuum.G, Is.EqualTo(anchor.G).Within(Tolerance), $"G varied at {elevation}");
            Assert.That(vacuum.B, Is.EqualTo(anchor.B).Within(Tolerance), $"B varied at {elevation}");
        }

        // The sea-level counterpart, so "constant" cannot pass by the whole curve having gone flat.
        SkyColorTemperature.Rgb horizon = SkyColorTemperature.SkyColorForElevation(
            0f, pressureFraction: 1f, aerosolFraction: 0f,
            angstromExponent: AerosolSpectrum.ReferenceAngstromExponent, inVacuum: false);
        SkyColorTemperature.Rgb high = SkyColorTemperature.SkyColorForElevation(
            60f, pressureFraction: 1f, aerosolFraction: 0f,
            angstromExponent: AerosolSpectrum.ReferenceAngstromExponent, inVacuum: false);
        Assert.That(horizon.B, Is.LessThan(high.B - 0.05f),
            "sea-level horizon sky is no longer measurably redder than the high-sun sky");
    }

    // --- Aurora tint (§11) — off ---
    //
    // A presentation argument, not an intensity one: the 630 nm emission sheet sits ~630 km up and an
    // orbital platform sits at 200 km, so you are looking down on a localised curtain rather than up
    // through a sky-filling one. A full-screen colour blend is the wrong shape at any strength, which
    // is why this is a hard zero rather than a scale factor.

    // The sea-level column is NightVisibility(glow) x AuroraMath.MaxSkyTintStrength, so it moves
    // whenever that shipped peak is retuned — as it was when the peak dropped 0.35 -> 0.18 because
    // the old value read as a colour grade rather than an aurora. These are the recomputed values,
    // not widened tolerances: a pin that tracks the shipped constant is the point, and the fact that
    // only the sea-level half of each pair moved (the vacuum half stayed at 0) is exactly the
    // diverging-pair signal this fixture exists to produce.
    [TestCase(45f, 0f)] // daylight: washed out at sea level too
    [TestCase(20f, 0.0051299f)] // sky just dark enough for a trace
    [TestCase(10f, 0.1133690f)]
    [TestCase(5f, 0.1689713f)]
    [TestCase(0f, 0.18f)] // sunset onward: full tint at sea level
    [TestCase(-6f, 0.18f)]
    [TestCase(-30f, 0.18f)] // deep night
    public void AuroraSkyTint_IsZeroInVacuum_AndPinnedAtSeaLevel(float elevation, float seaLevel)
    {
        float glow = SunGlowAtElevation(elevation);

        Assert.That(AuroraMath.SkyTintStrength(glow, ramp: 1f, curtained: false, inVacuum: true),
            Is.EqualTo(0f).Within(Tolerance),
            $"aurora sky tint survived into vacuum at elevation {elevation}");
        Assert.That(AuroraMath.SkyTintStrength(glow, ramp: 1f, curtained: false, inVacuum: false),
            Is.EqualTo(seaLevel).Within(Tolerance),
            $"sea-level aurora sky tint moved at elevation {elevation}");
    }

    // Same story as the sky column, against AuroraMath.MaxOverlayTintStrength (0.15 -> 0.08).
    [TestCase(45f, 0f)]
    [TestCase(10f, 0.0503862f)]
    [TestCase(0f, 0.08f)]
    [TestCase(-30f, 0.08f)]
    public void AuroraOverlayTint_IsZeroInVacuum_AndPinnedAtSeaLevel(float elevation, float seaLevel)
    {
        float glow = SunGlowAtElevation(elevation);

        Assert.That(AuroraMath.OverlayTintStrength(glow, ramp: 1f, curtained: false, inVacuum: true),
            Is.EqualTo(0f).Within(Tolerance),
            $"aurora overlay tint survived into vacuum at elevation {elevation}");
        Assert.That(AuroraMath.OverlayTintStrength(glow, ramp: 1f, curtained: false, inVacuum: false),
            Is.EqualTo(seaLevel).Within(Tolerance),
            $"sea-level aurora overlay tint moved at elevation {elevation}");
    }

    // The condition fade ramp must not reopen the gate either — a flare mid-fade is still a flare
    // seen from the wrong side of the emission sheet.
    [TestCase(0f)]
    [TestCase(0.25f)]
    [TestCase(1f)]
    [TestCase(5f)] // out-of-range ramps are clamped, not trusted
    public void AuroraTint_VacuumIsZeroAtEveryConditionRamp(float ramp)
    {
        for (float elevation = -30f; elevation <= 90f; elevation += 2.5f)
        {
            float glow = SunGlowAtElevation(elevation);
            Assert.That(AuroraMath.SkyTintStrength(glow, ramp, curtained: false, inVacuum: true),
                Is.EqualTo(0f).Within(Tolerance), $"sky tint at elevation {elevation}, ramp {ramp}");
            Assert.That(AuroraMath.OverlayTintStrength(glow, ramp, curtained: false, inVacuum: true),
                Is.EqualTo(0f).Within(Tolerance), $"overlay tint at elevation {elevation}, ramp {ramp}");
        }
    }
}
