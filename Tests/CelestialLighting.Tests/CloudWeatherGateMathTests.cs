using CelestialLighting;
using NUnit.Framework;

namespace CelestialLighting.Tests;

// Offline coverage for §25f's weather gate — the rule that decides whether this mod's cloud sheets
// exist at all, and what a change of weather does to the ones already on screen.
//
// WHY THIS IS TESTED RATHER THAN LEFT TO THE LIVE RUN. Two of the three properties below are about
// what happens ACROSS a 4,000-tick vanilla cross-fade, and a harness scenario samples that window at
// a handful of instants. A pop between two of those instants is exactly the failure §25 already had
// to find once (a whole sky of cloud deleted in a single tick), so the monotone-fade property is
// asserted over a dense sweep here, where a dense sweep is free.
//
// The expected values are written as literals rather than recomputed from ClearShare, per the rule
// this repo learned the hard way: a differential test that computes both sides with the code under
// test asserts x - x == 0.
[TestFixture]
public class CloudWeatherGateMathTests
{
    private const float Tolerance = 1e-6f;

    // Settled weather, which is where a player spends almost all of their time: full cloud under a
    // Clear sky, none under anything else, whatever the transition lerp happens to read.
    [TestCase(0f, true, true, 1f)]
    [TestCase(0.5f, true, true, 1f)]
    [TestCase(1f, true, true, 1f)]
    [TestCase(0f, false, false, 0f)]
    [TestCase(0.5f, false, false, 0f)]
    [TestCase(1f, false, false, 0f)]
    public void SettledWeatherIsAllOrNothing(float lerp, bool lastClear, bool curClear, float expected)
    {
        Assert.That(
            CloudWeatherGateMath.ClearShare(lerp, lastClear, curClear),
            Is.EqualTo(expected).Within(Tolerance));
    }

    // A CLEAR-TO-CLEAR RE-ROLL IS NOT A TRANSITION. Vanilla re-rolls the same weather often enough
    // that this is a real state and not a contrived one, and both booleans are then true at once. The
    // two arms sum rather than one being selected precisely so this case holds at exactly 1 all the
    // way through; a `curIsClear ? lerp : ...` style pick would dip the sky to `lerp` and back for no
    // cause a player could see.
    [TestCase(0f)]
    [TestCase(0.25f)]
    [TestCase(0.5f)]
    [TestCase(0.75f)]
    [TestCase(1f)]
    public void ClearToClearNeverDips(float lerp)
    {
        Assert.That(
            CloudWeatherGateMath.ClearShare(lerp, lastIsClear: true, curIsClear: true),
            Is.EqualTo(1f).Within(Tolerance));
    }

    // The user-visible promise, going out of Clear: the share is 1 at the tick the front arrives and
    // 0 when it has fully arrived, so the cloud fades over vanilla's own 4,000 ticks instead of being
    // deleted at the first of them. The endpoints are the half of this that a scenario can catch.
    [TestCase(0f, 1f)]
    [TestCase(0.25f, 0.75f)]
    [TestCase(0.5f, 0.5f)]
    [TestCase(1f, 0f)]
    public void LeavingClearRampsDown(float lerp, float expected)
    {
        Assert.That(
            CloudWeatherGateMath.ClearShare(lerp, lastIsClear: true, curIsClear: false),
            Is.EqualTo(expected).Within(Tolerance));
    }

    // And the same line backwards on the way in, so a sky that clears up grows its cloud over the
    // same hour and a half rather than switching it on.
    [TestCase(0f, 0f)]
    [TestCase(0.25f, 0.25f)]
    [TestCase(0.5f, 0.5f)]
    [TestCase(1f, 1f)]
    public void ArrivingAtClearRampsUp(float lerp, float expected)
    {
        Assert.That(
            CloudWeatherGateMath.ClearShare(lerp, lastIsClear: false, curIsClear: true),
            Is.EqualTo(expected).Within(Tolerance));
    }

    // A lerp outside [0, 1] cannot produce a share outside it. `TransitionLerpFactor` clamps its own
    // upper end, but this core takes a primitive from anywhere and a share is multiplied straight
    // into a rendered alpha.
    [TestCase(-5f, true, false, 1f)]
    [TestCase(5f, true, false, 0f)]
    [TestCase(-5f, false, true, 0f)]
    [TestCase(5f, false, true, 1f)]
    public void LerpIsClamped(float lerp, bool lastClear, bool curClear, float expected)
    {
        Assert.That(
            CloudWeatherGateMath.ClearShare(lerp, lastClear, curClear),
            Is.EqualTo(expected).Within(Tolerance));
    }

    // THE REGRESSION THE WHOLE SECTION EXISTS FOR. Before §25f the deck was an additive term, so
    // settled Rain drew the full cap of sheets at cover 1.0 on top of §13's flat dimming of the same
    // deck. A zero share now has to mean no cloud however cloudy §22 thinks the air is.
    [TestCase(0f)]
    [TestCase(0.35f)]
    [TestCase(0.92f)]
    [TestCase(1f)]
    public void NoClearSkyMeansNoCloud(float clearCover)
    {
        Assert.That(
            CloudWeatherGateMath.CoverFromShare(0f, clearCover),
            Is.EqualTo(0f).Within(Tolerance));
    }

    // A full Clear sky passes §22's cover through untouched, which is what makes §25f a no-op on the
    // sky a player sees most of the time — the settled-Clear frames are bit-identical to pre-§25f.
    [TestCase(0f)]
    [TestCase(0.2113f)]
    [TestCase(0.92f)]
    [TestCase(1f)]
    public void FullClearSkyPassesCoverThrough(float clearCover)
    {
        Assert.That(
            CloudWeatherGateMath.CoverFromShare(1f, clearCover),
            Is.EqualTo(clearCover).Within(Tolerance));
    }

    [TestCase(0.5f, 0.4f, 0.2f)]
    [TestCase(0.25f, 0.92f, 0.23f)]
    [TestCase(2f, 0.5f, 0.5f)]      // share clamped before the product
    [TestCase(0.5f, 2f, 0.5f)]      // cover clamped before the product
    [TestCase(0.5f, -1f, 0f)]
    public void CoverIsTheClampedProduct(float share, float cover, float expected)
    {
        Assert.That(
            CloudWeatherGateMath.CoverFromShare(share, cover),
            Is.EqualTo(expected).Within(Tolerance));
    }

    // THE FADE ITSELF, ASSERTED WHERE IT IS ACTUALLY VISIBLE — on the per-sheet alpha the three lanes
    // multiply into their draw, not on the share. This walks a Clear-to-rain transition at 1/200 of
    // its length and requires that no sheet's alpha ever rises, that every sheet reaches exactly
    // zero, and that no single step drops one by more than a small fraction of its opacity.
    //
    // The step bound is the part a "does it fade" eyeball cannot make: a lane that dropped from full
    // to nothing over two adjacent samples would satisfy monotonicity and still pop.
    [Test]
    public void EverySheetFadesOutSmoothlyWhenTheWeatherLeavesClear()
    {
        const int cap = 11;
        const float clearCover = 0.92f;
        const int steps = 200;

        float[] previous = new float[cap];
        for (int i = 0; i < cap; i++)
            previous[i] = CloudSheetLayout.CoverageAlpha(i, clearCover, cap);

        for (int step = 1; step <= steps; step++)
        {
            float lerp = (float)step / steps;
            float share = CloudWeatherGateMath.ClearShare(lerp, lastIsClear: true, curIsClear: false);

            for (int i = 0; i < cap; i++)
            {
                float alpha = CloudWeatherGateMath.FadedCoverage(
                    CloudSheetLayout.CoverageAlpha(i, clearCover, cap), share);

                Assert.That(
                    alpha,
                    Is.LessThanOrEqualTo(previous[i] + Tolerance),
                    $"sheet {i} brightened at lerp {lerp}");

                // 1/cap is one sheet's whole share of the cover, so a drop that large in one 200th of
                // the transition is a sheet leaving all at once rather than fading.
                Assert.That(
                    previous[i] - alpha,
                    Is.LessThan(1f / cap),
                    $"sheet {i} stepped rather than faded at lerp {lerp}");

                previous[i] = alpha;
            }
        }

        for (int i = 0; i < cap; i++)
            Assert.That(previous[i], Is.EqualTo(0f).Within(Tolerance), $"sheet {i} survived the front");
    }

    // THE REGRESSION THAT MADE THIS A SECOND CUT, and the one property the monotone test above cannot
    // see. §25f first multiplied the share into the COVER and let CoverageAlpha decompose it, which
    // fades smoothly per sheet and still looks wrong, because cover is a count: the sky sheds clouds
    // one at a time off the top while the first few sit at full opacity until the very end. So the
    // assertion is not "each sheet fades" but "they all fade TOGETHER" — every sheet that is up holds
    // the same fraction of the opacity it started with, at every instant of the transition.
    //
    // Written against the ratio rather than against a table of expected alphas on purpose: the point
    // is that one number scales all of them, and a test that pinned eleven values would pass just as
    // happily on eleven unrelated curves.
    [Test]
    public void TheWholeSkyFadesAtOneRate()
    {
        const int cap = 11;
        const float clearCover = 0.92f;

        float[] settled = new float[cap];
        for (int i = 0; i < cap; i++)
            settled[i] = CloudSheetLayout.CoverageAlpha(i, clearCover, cap);

        for (int step = 0; step <= 100; step++)
        {
            float lerp = step / 100f;
            float share = CloudWeatherGateMath.ClearShare(lerp, lastIsClear: true, curIsClear: false);

            for (int i = 0; i < cap; i++)
            {
                float alpha = CloudWeatherGateMath.FadedCoverage(settled[i], share);

                Assert.That(
                    alpha,
                    Is.EqualTo(settled[i] * share).Within(Tolerance),
                    $"sheet {i} faded at its own rate at lerp {lerp}");
            }
        }
    }

    // The count is the entry latch's business and the weather may not touch it. Stated as its own
    // test because it is the half of the split that has no visible symptom until a front arrives: if
    // the share ever reached CoverageAlpha's cover argument again, this would still fade smoothly and
    // would still be the staircase.
    [TestCase(1f)]
    [TestCase(0.5f)]
    [TestCase(0.01f)]
    public void APartlyFadedSkyStillHasEverySheetInIt(float share)
    {
        const int cap = 11;
        const float clearCover = 0.92f;

        int settledCount = 0;
        int fadedCount = 0;

        for (int i = 0; i < cap; i++)
        {
            if (CloudSheetLayout.CoverageAlpha(i, clearCover, cap) > 0f)
                settledCount++;

            if (CloudWeatherGateMath.FadedCoverage(
                    CloudSheetLayout.CoverageAlpha(i, clearCover, cap), share) > 0f)
                fadedCount++;
        }

        Assert.That(fadedCount, Is.EqualTo(settledCount));
    }

    // The mirror of the fade-out, and the reason it is a separate test rather than a loop flag: a sky
    // clearing up must not put a cloud on screen instantly either.
    [Test]
    public void EverySheetFadesInSmoothlyWhenTheWeatherReturnsToClear()
    {
        const int cap = 11;
        const float clearCover = 0.92f;
        const int steps = 200;

        float[] previous = new float[cap];

        for (int i = 0; i < cap; i++)
            Assert.That(
                CloudWeatherGateMath.FadedCoverage(
                    CloudSheetLayout.CoverageAlpha(i, clearCover, cap), 0f),
                Is.EqualTo(0f).Within(Tolerance));

        for (int step = 1; step <= steps; step++)
        {
            float lerp = (float)step / steps;
            float share = CloudWeatherGateMath.ClearShare(lerp, lastIsClear: false, curIsClear: true);

            for (int i = 0; i < cap; i++)
            {
                float alpha = CloudWeatherGateMath.FadedCoverage(
                    CloudSheetLayout.CoverageAlpha(i, clearCover, cap), share);

                Assert.That(alpha, Is.GreaterThanOrEqualTo(previous[i] - Tolerance));
                Assert.That(alpha - previous[i], Is.LessThan(1f / cap));

                previous[i] = alpha;
            }
        }

        for (int i = 0; i < cap; i++)
            Assert.That(
                previous[i],
                Is.EqualTo(CloudSheetLayout.CoverageAlpha(i, clearCover, cap)).Within(Tolerance));
    }
}
