using CelestialLighting.Probes;

namespace CelestialLighting.Tests;

// Offline sweep for the door-swing instrument's arithmetic (issue #218).
//
// WHY THESE EXIST WHEN THE THING BEING MEASURED IS A LIVE RENDER. The instrument's whole job is to
// answer zero for a correct build, and "zero" is also what a broken instrument answers. Every case
// below states an expected value computed by hand rather than by the code under test, so a stub that
// returns zero fails the sweep instead of passing it — which is exactly how these were first run.
[TestFixture]
public class SwingExcursionMathTests
{
    // A monotone rise is what the fix is supposed to produce: every sample sits between the two
    // endpoints, so nothing departs the band.
    [TestCase(10f, 40f, 10f, 40f, 0f)]
    // A monotone fall, i.e. the closing swing. The endpoints arrive in the other order and the band
    // is the same one — this is the case an implementation that assumed first <= last gets wrong,
    // and it would report every door closing as a defect.
    [TestCase(40f, 10f, 10f, 40f, 0f)]
    // Nothing moved at all.
    [TestCase(25f, 25f, 25f, 25f, 0f)]
    // The opening defect: the region dips below where it started before climbing to where it belongs.
    // 10 - 4 = 6.
    [TestCase(10f, 40f, 4f, 40f, 6f)]
    // The closing defect, the mirror: the region brightens past where it started before falling.
    // 51 - 40 = 11.
    [TestCase(40f, 10f, 10f, 51f, 11f)]
    // Departures on BOTH sides. The worse one is the answer, not their sum and not the first found:
    // below is 10 - 7 = 3, above is 48 - 40 = 8.
    [TestCase(10f, 40f, 7f, 48f, 8f)]
    // Both sides again with the other one winning, so a "return the last computed" bug cannot pass
    // the pair: below is 10 - 1 = 9, above is 42 - 40 = 2.
    [TestCase(10f, 40f, 1f, 42f, 9f)]
    // A flat series that departed — the band has zero width, so any excursion at all is visible.
    [TestCase(25f, 25f, 25f, 30f, 5f)]
    public void Excursion_measures_the_worst_departure_from_the_endpoint_band(
        float first, float last, float min, float max, float expected)
    {
        Assert.That(
            SwingExcursionMath.Excursion(first, last, min, max),
            Is.EqualTo(expected).Within(1e-4f));
    }

    [Test]
    public void A_trace_with_no_samples_reports_nothing_rather_than_zero_excursion()
    {
        SwingExcursionMath.Trace trace = default;

        Assert.Multiple(() =>
        {
            Assert.That(trace.Count, Is.Zero);
            Assert.That(trace.Excursion, Is.Zero);
        });
    }

    // One sample is its own band. A scenario that armed the instrument and never swung anything
    // would otherwise be free to report a departure from nothing.
    [Test]
    public void A_single_sample_cannot_depart_from_itself()
    {
        SwingExcursionMath.Trace trace = default;
        trace = trace.Add(17f);

        Assert.Multiple(() =>
        {
            Assert.That(trace.Count, Is.EqualTo(1));
            Assert.That(trace.First, Is.EqualTo(17f));
            Assert.That(trace.Last, Is.EqualTo(17f));
            Assert.That(trace.Excursion, Is.Zero);
        });
    }

    // The fixed build's shape: the value climbs and stops. Endpoints are the extremes, so nothing
    // departs.
    [Test]
    public void A_monotone_rise_folds_to_no_excursion()
    {
        SwingExcursionMath.Trace trace = Fold(12f, 12f, 19f, 26f, 33f, 33f, 33f);

        Assert.Multiple(() =>
        {
            Assert.That(trace.Count, Is.EqualTo(7));
            Assert.That(trace.First, Is.EqualTo(12f));
            Assert.That(trace.Last, Is.EqualTo(33f));
            Assert.That(trace.Excursion, Is.Zero);
        });
    }

    // The unfixed build's shape: one frame of over-subtraction in the middle of an otherwise
    // monotone climb, then the next frame puts it back. 12 - 5 = 7.
    [Test]
    public void One_dark_frame_mid_rise_is_the_excursion_the_instrument_exists_to_find()
    {
        SwingExcursionMath.Trace trace = Fold(12f, 12f, 5f, 26f, 33f, 33f);

        Assert.Multiple(() =>
        {
            Assert.That(trace.Min, Is.EqualTo(5f));
            Assert.That(trace.Last, Is.EqualTo(33f));
            Assert.That(trace.Excursion, Is.EqualTo(7f).Within(1e-4f));
        });
    }

    // An unreadable sample must not become the minimum. The sentinel is -1, and folding it in would
    // report an excursion of 13 on a perfectly monotone series — the most convincing false positive
    // this instrument could produce.
    [Test]
    public void An_unreadable_sample_is_counted_apart_and_never_becomes_the_minimum()
    {
        SwingExcursionMath.Trace trace = Fold(12f, -1f, 26f, 33f);

        Assert.Multiple(() =>
        {
            Assert.That(trace.Count, Is.EqualTo(3));
            Assert.That(trace.Rejected, Is.EqualTo(1));
            Assert.That(trace.Min, Is.EqualTo(12f));
            Assert.That(trace.Excursion, Is.Zero);
        });
    }

    // A leading sentinel must not become `First` either, which is the other half of the same trap:
    // the band would be anchored at -1 and no later dip could ever depart from it.
    [Test]
    public void A_leading_unreadable_sample_does_not_anchor_the_band()
    {
        SwingExcursionMath.Trace trace = Fold(-1f, 12f, 5f, 33f);

        Assert.Multiple(() =>
        {
            Assert.That(trace.First, Is.EqualTo(12f));
            Assert.That(trace.Rejected, Is.EqualTo(1));
            Assert.That(trace.Excursion, Is.EqualTo(7f).Within(1e-4f));
        });
    }

    private static SwingExcursionMath.Trace Fold(params float[] samples)
    {
        SwingExcursionMath.Trace trace = default;

        foreach (float sample in samples)
        {
            trace = trace.Add(sample);
        }

        return trace;
    }
}
