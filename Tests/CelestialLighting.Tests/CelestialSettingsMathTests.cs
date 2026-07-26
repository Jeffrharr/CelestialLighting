using System;

namespace CelestialLighting.Tests;

/// <summary>
/// Offline edge-case coverage for CelestialSettingsMath.cs — the preset bundles and what they
/// promise once the patches read them. No RimWorld/Unity assembly required (the file is pure
/// System). Complements ApiCompatibilityTests.cs, which only checks vanilla members still exist;
/// these check that our own preset/floor logic behaves.
/// </summary>
[TestFixture]
public class CelestialSettingsMathTests
{
    private const float Tolerance = 0.0001f;

    // --- Presets.Resolve ---

    [Test]
    public void Resolve_Realistic_ReturnsRealisticBundle()
    {
        var knobs = Presets.Resolve(CelestialPreset.Realistic);
        Assert.That(knobs.ShadowLengthScale, Is.EqualTo(Presets.Realistic.ShadowLengthScale).Within(Tolerance));
        Assert.That(knobs.MinNightBrightness, Is.EqualTo(0f).Within(Tolerance)); // genuinely black nights
    }

    [Test]
    public void Resolve_Cinematic_ReturnsCinematicBundle()
    {
        var knobs = Presets.Resolve(CelestialPreset.Cinematic);
        Assert.That(knobs.ShadowLengthScale, Is.EqualTo(Presets.Cinematic.ShadowLengthScale).Within(Tolerance));
        Assert.That(knobs.MinNightBrightness, Is.GreaterThan(0f)); // never fully black
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
        Assert.That(Presets.Cinematic.MinNightBrightness, Is.EqualTo(0.50f).Within(Tolerance));
        Assert.That(Presets.Cinematic.MinIndoorBrightness, Is.EqualTo(0.50f).Within(Tolerance));
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

    // --- What the two shadow knobs actually do, now that the patches read them ---

    [Test]
    public void Realistic_LeavesTheShadowModelExactlyAsComputed()
    {
        // "Physically faithful" has to mean the identity, or Realistic is just another look. Asserted
        // through the functions the patches call rather than on the raw knob values, since it is the
        // composed behaviour — not the number 1.0 — that the preset is promising.
        Formulas.ShadowVector scaled = Formulas.ScaleShadowVector(0f, 8f, Presets.Realistic.ShadowLengthScale);
        Assert.That(scaled.Y, Is.EqualTo(8f).Within(Tolerance));
        Assert.That(Formulas.ScaleShadowStrength(0.7f, Presets.Realistic.ShadowStrength),
            Is.EqualTo(0.7f).Within(Tolerance));
    }

    [Test]
    public void Cinematic_MakesShadowsLongerAndSofterThanRealistic()
    {
        // The preset's own description — "longer, softer shadows" — stated as behaviour. Both halves
        // matter: length alone would read as a sharper, more dramatic scene rather than a gentler one.
        Formulas.ShadowVector realistic = Formulas.ScaleShadowVector(0f, 8f, Presets.Realistic.ShadowLengthScale);
        Formulas.ShadowVector cinematic = Formulas.ScaleShadowVector(0f, 8f, Presets.Cinematic.ShadowLengthScale);
        Assert.That(cinematic.Y, Is.GreaterThan(realistic.Y));

        Assert.That(Formulas.ScaleShadowStrength(0.7f, Presets.Cinematic.ShadowStrength),
            Is.LessThan(Formulas.ScaleShadowStrength(0.7f, Presets.Realistic.ShadowStrength)));
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
            Assert.That(knobs.Desaturation, Is.InRange(0f, 1f));
            Assert.That(knobs.WeatherDimming, Is.InRange(0f, 0.5f));
            Assert.That(knobs.MinNightBrightness, Is.InRange(0f, 1f));
            Assert.That(knobs.MinIndoorBrightness, Is.InRange(0f, 1f));
        }
    }
}
