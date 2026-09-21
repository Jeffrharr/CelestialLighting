using CelestialLighting;
using NUnit.Framework;

namespace CelestialLighting.Tests;

// Subsystem 17c pure core: what an As above, So below II band below the surface renders at.
//
// The interesting cases are the two ends of the minimum-indoor-brightness slider, because those are
// the shipped presets and each carries a promise the cave has to keep: Cinematic's 0.50 must give a
// legible cave and Realistic's 0.0 must keep it black, at EVERY hour rather than only at noon.
[TestFixture]
public class UndergroundBandMathTests
{
    [TestCase(0f, 0)]            // Realistic: a black cave, and black at noon too
    [TestCase(0.5f, 128)]        // Cinematic: rounds up to 128 rather than truncating to 127
    [TestCase(1f, 255)]
    [TestCase(0.25f, 64)]
    public void AmbientLevel_MapsSliderToByte(float slider, int expected) =>
        Assert.That(UndergroundBandMath.AmbientLevel(slider), Is.EqualTo((byte)expected));

    // Out of range in both directions rather than only above: a settings file edited by hand, or a
    // preset from a future version, can carry either.
    [TestCase(-1f, 0)]
    [TestCase(-0.0001f, 0)]
    [TestCase(1.5f, 255)]
    [TestCase(float.NaN, 0)]
    public void AmbientLevel_ClampsOutOfRange(float slider, int expected) =>
        Assert.That(UndergroundBandMath.AmbientLevel(slider), Is.EqualTo((byte)expected));

    // The property the whole pass rests on: the ambient is an OFFSET, so every difference §27 drew
    // between neighbouring cells survives it untouched. That is what a falloff is made of.
    [TestCase(87, 128, 215)]     // the torch's own cell, measured at 0.342 live
    [TestCase(51, 128, 179)]     // three cells out, measured at 0.200 live
    [TestCase(0, 128, 128)]      // unlit rock comes up to the cave's own level
    [TestCase(255, 0, 255)]      // ambient 0 changes nothing at all
    [TestCase(0, 0, 0)]
    public void AddAmbient_IsAConstantOffset(int existing, int ambient, int expected) =>
        Assert.That(
            UndergroundBandMath.AddAmbient((byte)existing, (byte)ambient), Is.EqualTo((byte)expected));

    [TestCase(200, 128)]
    [TestCase(255, 255)]
    [TestCase(128, 128)]
    public void AddAmbient_SaturatesInsteadOfWrapping(int existing, int ambient) =>
        Assert.That(
            UndergroundBandMath.AddAmbient((byte)existing, (byte)ambient), Is.EqualTo((byte)255));

    // The regression this whole function was rewritten for. A max flattened the measured falloff
    // 87 -> 51 -> 22 to a single value because the Cinematic ambient of 128 is brighter than all
    // three, which left a burning torch completely invisible in its own cave. Stated as the
    // difference between neighbours rather than as absolute levels, because the offset is what
    // preserves them and the absolute values move with the slider.
    [Test]
    public void AddAmbient_PreservesFalloffAMaxWouldHaveFlattened()
    {
        const byte ambient = 128;
        byte lamp = UndergroundBandMath.AddAmbient(87, ambient);
        byte near = UndergroundBandMath.AddAmbient(51, ambient);
        byte far = UndergroundBandMath.AddAmbient(22, ambient);

        Assert.That(lamp - near, Is.EqualTo(87 - 51));
        Assert.That(near - far, Is.EqualTo(51 - 22));
        Assert.That(lamp, Is.GreaterThan(near));
        Assert.That(near, Is.GreaterThan(far));
    }

    [Test]
    public void FullCover_IsTotal() =>
        Assert.That(UndergroundBandMath.FullCoverAlpha, Is.EqualTo((byte)255));
}
