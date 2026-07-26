using System;

namespace CelestialLighting.Tests;

/// <summary>
/// Offline edge-case coverage for CelestialSettingsMath.cs — the preset bundles and the
/// accessibility brightness-floor clamp. No RimWorld/Unity assembly required (the file is pure
/// System). Complements ApiCompatibilityTests.cs, which only checks vanilla members still exist;
/// these check that our own preset/floor logic behaves.
/// </summary>
[TestFixture]
public class CelestialSettingsMathTests
{
    private const float Tolerance = 0.0001f;

    // --- BrightnessFloorMath.Apply ---

    [TestCase(0.05f, 0.15f, ExpectedResult = 0.15f)] // below floor: lifted to floor
    [TestCase(0.50f, 0.15f, ExpectedResult = 0.50f)] // above floor: untouched
    [TestCase(0.15f, 0.15f, ExpectedResult = 0.15f)] // exactly at floor: unchanged
    [TestCase(0.00f, 0.20f, ExpectedResult = 0.20f)] // pitch black lifted
    [TestCase(1.00f, 0.30f, ExpectedResult = 1.00f)] // full daylight untouched
    public float Apply_Enabled_ClampsUpwardOnly(float glow, float floor)
    {
        return BrightnessFloorMath.Apply(glow, floor, enabled: true);
    }

    [TestCase(0.05f, 0.15f, ExpectedResult = 0.05f)] // disabled is the identity even below floor
    [TestCase(0.80f, 0.15f, ExpectedResult = 0.80f)]
    public float Apply_Disabled_IsIdentity(float glow, float floor)
    {
        return BrightnessFloorMath.Apply(glow, floor, enabled: false);
    }

    [Test]
    public void Apply_ClampsFloorAboveOneDownToOne()
    {
        // A hand-edited or future-widened floor above 1 must not push displayed glow past the valid
        // 0–1 range.
        Assert.That(BrightnessFloorMath.Apply(0.2f, floor: 5f, enabled: true), Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void Apply_ClampsNegativeFloorUpToZero_LeavingGlowUnchanged()
    {
        // A negative floor is nonsensical; it clamps to 0, so it can never lower a positive glow.
        Assert.That(BrightnessFloorMath.Apply(0.3f, floor: -1f, enabled: true), Is.EqualTo(0.3f).Within(Tolerance));
    }

    [Test]
    public void Apply_ClampsGlowAboveOneDownToOne()
    {
        Assert.That(BrightnessFloorMath.Apply(1.5f, floor: 0.2f, enabled: true), Is.EqualTo(1f).Within(Tolerance));
    }

    // --- Presets.Resolve ---

    [Test]
    public void Resolve_Realistic_ReturnsRealisticBundle()
    {
        var knobs = Presets.Resolve(CelestialPreset.Realistic);
        Assert.That(knobs.ShadowLengthScale, Is.EqualTo(Presets.Realistic.ShadowLengthScale).Within(Tolerance));
        Assert.That(knobs.NightRadianceFloor, Is.EqualTo(0f).Within(Tolerance)); // genuinely black nights
    }

    [Test]
    public void Resolve_Cinematic_ReturnsCinematicBundle()
    {
        var knobs = Presets.Resolve(CelestialPreset.Cinematic);
        Assert.That(knobs.ShadowLengthScale, Is.EqualTo(Presets.Cinematic.ShadowLengthScale).Within(Tolerance));
        Assert.That(knobs.NightRadianceFloor, Is.GreaterThan(0f)); // never fully black
    }

    [Test]
    public void Realistic_LeavesBothBrightnessFloorsAtZero()
    {
        // Realistic's defining promise: an unlit night is actually dark, outdoors and indoors alike.
        // A nonzero floor here would quietly break the one preset players pick *for* the darkness.
        Assert.That(Presets.Realistic.MinNightBrightness, Is.EqualTo(0f).Within(Tolerance));
        Assert.That(Presets.Realistic.MinIndoorBrightness, Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void Cinematic_LiftsBothBrightnessFloorsTogether()
    {
        // Indoor occlusion and night darkness compound, so lifting only one still leaves a sealed
        // room under a moonless night unreadable. Pin them equal, not merely both nonzero.
        Assert.That(Presets.Cinematic.MinNightBrightness, Is.EqualTo(0.30f).Within(Tolerance));
        Assert.That(Presets.Cinematic.MinIndoorBrightness, Is.EqualTo(0.30f).Within(Tolerance));
    }

    [Test]
    public void Resolve_Custom_Throws()
    {
        // Custom has no bundle to hand back — applying a preset for it would stomp the player's own
        // tuned values, so it must be a loud error, not a silent fallback.
        Assert.Throws<ArgumentOutOfRangeException>(() => Presets.Resolve(CelestialPreset.Custom));
    }

    [Test]
    public void Presets_DifferInTheirCorrelatedKnobs()
    {
        // The whole point of presets: the "cinematic" look is a genuinely different bundle, not a
        // relabel of "realistic". Assert the two headline knobs actually diverge.
        Assert.That(Presets.Cinematic.NightRadianceFloor, Is.GreaterThan(Presets.Realistic.NightRadianceFloor));
        Assert.That(Presets.Cinematic.Desaturation, Is.LessThan(Presets.Realistic.Desaturation));
        // §13 follows the same taste axis: cinematic storms stay photogenic, realistic ones go murky.
        Assert.That(Presets.Cinematic.WeatherDimming, Is.LessThan(Presets.Realistic.WeatherDimming));
        // Same axis again: cinematic nights stay readable, realistic ones go genuinely black.
        Assert.That(Presets.Cinematic.MinNightBrightness, Is.GreaterThan(Presets.Realistic.MinNightBrightness));
        Assert.That(Presets.Cinematic.MinIndoorBrightness, Is.GreaterThan(Presets.Realistic.MinIndoorBrightness));
    }

    [TestCase(CelestialPreset.Realistic, ExpectedResult = true)]
    [TestCase(CelestialPreset.Cinematic, ExpectedResult = true)]
    [TestCase(CelestialPreset.Custom, ExpectedResult = false)]
    public bool IsOpinionated_TrueOnlyForNamedPresets(CelestialPreset preset)
    {
        return Presets.IsOpinionated(preset);
    }

    [Test]
    public void AllPresetKnobs_AreWithinTheirSliderRanges()
    {
        // Guards against a preset default drifting outside the settings-window slider bounds, which
        // would make the UI silently clamp a chosen preset to a different value.
        foreach (var knobs in new[] { Presets.Realistic, Presets.Cinematic })
        {
            Assert.That(knobs.ShadowLengthScale, Is.InRange(0.5f, 2.0f));
            Assert.That(knobs.ShadowStrength, Is.InRange(0f, 1f));
            Assert.That(knobs.NightRadianceFloor, Is.InRange(0f, 0.3f));
            Assert.That(knobs.Desaturation, Is.InRange(0f, 1f));
            Assert.That(knobs.WeatherDimming, Is.InRange(0f, 0.5f));
            Assert.That(knobs.MinNightBrightness, Is.InRange(0f, 1f));
            Assert.That(knobs.MinIndoorBrightness, Is.InRange(0f, 1f));
        }
    }
}
