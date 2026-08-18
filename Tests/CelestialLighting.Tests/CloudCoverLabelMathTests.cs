using CelestialLighting;
using NUnit.Framework;

namespace CelestialLighting.Tests;

// Offline coverage for §22's weather-panel suffix. The percentage half is small; the fit rule is the
// reason this file exists, because the case it protects against — two mods appending to one 230px
// label until it wraps onto the temperature row — is only reachable live with a second mod installed,
// and only in states (a very dry map, a long weather name) that take in-game weeks to arrive.
[TestFixture]
public class CloudCoverLabelMathTests
{
    [TestCase(0f, 0)]
    [TestCase(0.271f, 27)]
    [TestCase(0.5f, 50)]
    [TestCase(1f, 100)]
    public void Percent_RoundsTheFractionToWholePercent(float fraction, int expected)
    {
        Assert.That(CloudCoverLabelMath.Percent(fraction), Is.EqualTo(expected));
    }

    [TestCase(-0.5f, 0)]
    [TestCase(1.5f, 100)]
    [TestCase(float.NaN, 0)]
    public void Percent_ClampsRatherThanReportingAnImpossibleSky(float fraction, int expected)
    {
        // A cloud fraction is a fraction by contract, so an out-of-range one is a bug elsewhere; the
        // label is the wrong place to surface it as "-50% cloudy".
        Assert.That(CloudCoverLabelMath.Percent(fraction), Is.EqualTo(expected));
    }

    [Test]
    public void Percent_RoundsHalfToEven_MatchingMathfRoundToInt()
    {
        // This file is Unity-free, so it uses Math.Round where the patch used to use Mathf.RoundToInt.
        // Both round half to even, and these are the inputs where a naive away-from-zero rounding
        // would disagree — pinned so the swap stays a swap rather than a behaviour change.
        Assert.That(CloudCoverLabelMath.Percent(0.005f), Is.EqualTo(0));
        Assert.That(CloudCoverLabelMath.Percent(0.015f), Is.EqualTo(2));
    }

    [Test]
    public void Suffix_ReadsAsTheShippedString()
    {
        Assert.That(CloudCoverLabelMath.Suffix(27), Is.EqualTo(" - 27% cloudy"));
    }

    [Test]
    public void FitsOneLine_AllowsTextUpToTheInsetWidth()
    {
        // Vanilla insets the drawn rect by 15px, so a 230px rect fits exactly 215px of text. Exact
        // equality counts as fitting: Widgets.Label only wraps once the text is genuinely wider.
        Assert.That(CloudCoverLabelMath.FitsOneLine(215f, 230f), Is.True);
        Assert.That(CloudCoverLabelMath.FitsOneLine(214.9f, 230f), Is.True);
    }

    [Test]
    public void FitsOneLine_RejectsTextOnePixelOver()
    {
        // The whole point of the rule: one pixel over is a wrapped second line inside a 26px rect,
        // which lands on the temperature readout above it. There is no partial credit.
        Assert.That(CloudCoverLabelMath.FitsOneLine(215.1f, 230f), Is.False);
    }

    [TestCase(float.NaN, 230f)]
    [TestCase(float.PositiveInfinity, 230f)]
    [TestCase(100f, float.NaN)]
    public void FitsOneLine_TreatsAnUnmeasurableWidthAsFitting(float measured, float rectWidth)
    {
        // Failure direction matters more than the answer. A font that has not loaded, or a zero-size
        // screen mid-resolution-change, must not read as "does not fit" — that would drop the suffix
        // and leave the feature looking broken long after the transient passed. Degrade to the old
        // behaviour (draw it, maybe wrap) instead.
        Assert.That(CloudCoverLabelMath.FitsOneLine(measured, rectWidth), Is.True);
    }

    [Test]
    public void FitsOneLine_RejectsEverythingInARectTooSmallToHoldAnything()
    {
        // A rect narrower than the inset yields a negative budget. Nothing fits, including the empty
        // string, and that is the correct answer — there is no room to draw into.
        Assert.That(CloudCoverLabelMath.FitsOneLine(0f, 10f), Is.False);
    }
}
