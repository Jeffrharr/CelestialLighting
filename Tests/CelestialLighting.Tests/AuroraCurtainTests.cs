using CelestialLighting;
using NUnit.Framework;

namespace CelestialLighting.Tests;

// Offline coverage for §11a's field generator.
//
// The interesting tests here are the three that pin issue #42's actual acceptance criteria as
// assertions rather than as taste: several colours coexisting, structure rather than a uniform wash,
// and a field that changes over time. Those are the things a flat tint could not do at any strength,
// so they are the things that must not silently regress.
[TestFixture]
public class AuroraCurtainTests
{
    private const float Tolerance = 1e-5f;

    // --- Amplify: the ribbon-forming response curve ---

    [Test]
    public void Amplify_IsMonotonic()
    {
        float previous = -1f;

        for (int i = 0; i <= 100; i++)
        {
            float v = AuroraCurtain.Amplify(i / 100f);
            Assert.That(v, Is.GreaterThanOrEqualTo(previous), $"not monotonic at {i / 100f}");
            Assert.That(v, Is.GreaterThanOrEqualTo(0f));
            previous = v;
        }
    }

    [Test]
    public void Amplify_IsNotNormalised_WhichIsWhyWaveDividesByItsPeak()
    {
        // Amplify is m⁴·v² + m·v⁴ + v⁸, so Amplify(1) is ~1.167, not 1 — it is a response curve, not a
        // unit-range mapping. Wave is what normalises, dividing by Amplify(0.95) because 0.95 is the most
        // Intensity can ever hand it. Pinning the overshoot here documents why that division exists; drop
        // it and every alpha in the texture would clip.
        Assert.That(AuroraCurtain.Amplify(1f), Is.GreaterThan(1f));
        Assert.That(AuroraCurtain.Amplify(1f), Is.LessThan(1.2f));
    }

    [Test]
    public void Amplify_CrushesEverythingBelowMidRange()
    {
        // This is the property that separates a ribbon from its background: without a steep response the
        // whole field lights up and the effect degenerates into the flat wash §11a exists to replace.
        Assert.That(AuroraCurtain.Amplify(0.5f), Is.LessThan(0.02f));
        Assert.That(AuroraCurtain.Amplify(0.2f), Is.LessThan(0.001f));
        Assert.That(AuroraCurtain.Amplify(0.95f), Is.GreaterThan(0.5f));
    }

    [Test]
    public void Amplify_ClampsOutOfRangeInput()
    {
        Assert.That(AuroraCurtain.Amplify(-5f), Is.EqualTo(0f).Within(Tolerance));
        Assert.That(AuroraCurtain.Amplify(5f), Is.EqualTo(AuroraCurtain.Amplify(1f)).Within(Tolerance));
    }

    // --- Intensity: distance from the contour ---

    [Test]
    public void Intensity_PeaksOnTheContourAndFallsOffEitherSide()
    {
        float onContour = AuroraCurtain.Intensity(0.5f);

        Assert.That(onContour, Is.GreaterThan(AuroraCurtain.Intensity(0.2f)));
        Assert.That(onContour, Is.GreaterThan(AuroraCurtain.Intensity(0.8f)));
        Assert.That(onContour, Is.EqualTo(0.95f).Within(Tolerance),
            "peak must stay at 0.95, which is what Wave normalises against");
    }

    [Test]
    public void Intensity_IsSymmetricAboutTheContour()
    {
        Assert.That(AuroraCurtain.Intensity(0.3f),
            Is.EqualTo(AuroraCurtain.Intensity(0.7f)).Within(Tolerance));
    }

    [Test]
    public void Intensity_KeepsAFaintPedestalFarFromTheContour()
    {
        // The 0.2 floor is what survives Amplify as a sky-wide glow, so the curtain hangs in a lit sky
        // rather than in a void. Zero here would make the background pure black.
        Assert.That(AuroraCurtain.Intensity(0f), Is.EqualTo(0.2f).Within(Tolerance));
        Assert.That(AuroraCurtain.Intensity(1f), Is.EqualTo(0.2f).Within(Tolerance));
    }

    // --- Wave: #42's "structure, not saturation" ---

    [Test]
    public void Wave_StaysWithinUnitRange()
    {
        for (int i = 0; i < 64; i++)
        {
            for (int j = 0; j < 64; j++)
            {
                float w = AuroraCurtain.Wave(i / 64f, j / 64f, 1000f);
                Assert.That(w, Is.GreaterThanOrEqualTo(0f).And.LessThanOrEqualTo(1f));
            }
        }
    }

    [Test]
    public void Wave_IsRibbonShaped_NotAUniformWash()
    {
        // #42's core complaint, as an assertion. A flat tint has the same value everywhere; a curtain is
        // mostly dark with a minority of bright band. If a retune ever flattens this distribution, the
        // subsystem has quietly become the thing it replaced.
        int bright = 0;
        int dim = 0;
        const int side = 96;

        for (int i = 0; i < side; i++)
        {
            for (int j = 0; j < side; j++)
            {
                float w = AuroraCurtain.Wave(i / (float)side, j / (float)side, 5000f);
                if (w > 0.4f)
                    bright++;
                if (w < 0.05f)
                    dim++;
            }
        }

        int total = side * side;
        Assert.That(bright, Is.GreaterThan(total / 200), "no bright ribbon at all — nothing would show");
        Assert.That(bright, Is.LessThan(total / 2), "bright everywhere — that is a wash, not a ribbon");
        Assert.That(dim, Is.GreaterThan(total / 5), "no genuinely dark sky between the bands");
    }

    [Test]
    public void Wave_ChangesOverTime()
    {
        // "Bands drift and undulate over time" — the requirement a static texture cannot meet. Sampled at
        // a fixed point, the field must actually differ across a plausible viewing interval.
        int changed = 0;

        for (int i = 0; i < 64; i++)
        {
            float u = i / 64f;
            float before = AuroraCurtain.Wave(u, 0.5f, 0f);
            float after = AuroraCurtain.Wave(u, 0.5f, 20000f);

            if ((before > after ? before - after : after - before) > 0.02f)
                changed++;
        }

        Assert.That(changed, Is.GreaterThan(16), "field is essentially static over time");
    }

    [Test]
    public void Wave_IsTileable_SoThePanHasNoSeam()
    {
        // The curtain is UV-panned every frame, so a discontinuity at the tile edge would sweep across
        // the colony as a hard line. u=0 and u=1 are the same column of the wrapped field.
        for (int j = 0; j < 32; j++)
        {
            float v = j / 32f;
            Assert.That(
                AuroraCurtain.Wave(0f, v, 3000f),
                Is.EqualTo(AuroraCurtain.Wave(1f, v, 3000f)).Within(1e-4f),
                $"horizontal seam at v={v}");
            Assert.That(
                AuroraCurtain.Wave(v, 0f, 3000f),
                Is.EqualTo(AuroraCurtain.Wave(v, 1f, 3000f)).Within(1e-4f),
                $"vertical seam at u={v}");
        }
    }

    // --- Palette: #42's "several colours visible at the same time" ---

    [Test]
    public void PaletteColor_HoldsGreenThroughTheMiddleAndFringesEitherSide()
    {
        AuroraMath.Rgb mid = AuroraCurtain.PaletteColor(0.5f);
        Assert.That(mid.G, Is.EqualTo(AuroraMath.OxygenGreen.G).Within(Tolerance));

        // Below the green band it slides to nitrogen violet: blue must dominate.
        AuroraMath.Rgb low = AuroraCurtain.PaletteColor(0f);
        Assert.That(low.B, Is.GreaterThan(low.G));

        // Above it, to the high-altitude oxygen red.
        AuroraMath.Rgb high = AuroraCurtain.PaletteColor(1f);
        Assert.That(high.R, Is.GreaterThan(high.G));
    }

    [Test]
    public void PaletteColor_IsContinuousAtBothBandEdges()
    {
        // A step at either edge would render as a visible colour boundary cutting across a ribbon.
        AssertNearlyEqual(
            AuroraCurtain.PaletteColor(AuroraCurtain.HueGreenLow - 0.001f),
            AuroraCurtain.PaletteColor(AuroraCurtain.HueGreenLow + 0.001f));

        AssertNearlyEqual(
            AuroraCurtain.PaletteColor(AuroraCurtain.HueGreenHigh - 0.001f),
            AuroraCurtain.PaletteColor(AuroraCurtain.HueGreenHigh + 0.001f));
    }

    [Test]
    public void HueField_YieldsSeveralDistinctHues_AcrossOneTile()
    {
        // #42: "several colours visible at the same time, in bands". A single global colour scores 1 here
        // by construction, which is exactly why this test exists.
        bool sawViolet = false;
        bool sawGreen = false;
        bool sawRed = false;
        const int side = 96;

        for (int i = 0; i < side; i++)
        {
            for (int j = 0; j < side; j++)
            {
                float h = AuroraCurtain.HueField(i / (float)side, j / (float)side, 4000f);

                if (h < AuroraCurtain.HueGreenLow)
                    sawViolet = true;
                else if (h > AuroraCurtain.HueGreenHigh)
                    sawRed = true;
                else
                    sawGreen = true;
            }
        }

        Assert.That(sawGreen, Is.True, "no green core — an aurora always has one");
        Assert.That(sawViolet, Is.True, "no violet fringe reached");
        Assert.That(sawRed, Is.True, "no red fringe reached");
    }

    // --- Envelope: brightness varies across the sky ---

    [Test]
    public void Envelope_LeavesSomeSkyLitAndSomeEmpty()
    {
        // "Brightness varies across the sky rather than being uniform." A constant envelope would put the
        // aurora everywhere at once, which is the flat-wash failure again in a different coat.
        int lit = 0;
        int empty = 0;
        const int side = 64;

        for (int i = 0; i < side; i++)
        {
            for (int j = 0; j < side; j++)
            {
                float e = AuroraCurtain.Envelope(i / (float)side, j / (float)side, 2000f);
                if (e > 0.6f)
                    lit++;
                if (e < 0.05f)
                    empty++;
            }
        }

        Assert.That(lit, Is.GreaterThan(side * side / 20), "envelope suppresses nearly everything");
        Assert.That(empty, Is.GreaterThan(side * side / 20), "envelope never clears — no empty sky");
    }

    // --- Drift wrapping: precision, and the seamlessness of the wrap ---

    [Test]
    public void DriftWrapTicksAndRateAgreeWithTheLatticeCycle()
    {
        // The two constants are stated independently for clarity, so pin that they describe the same
        // cycle. If either is retuned alone, every field's wrap stops landing on a period boundary.
        Assert.That(AuroraCurtain.DriftWrapTicks * AuroraCurtain.DriftRate,
            Is.EqualTo(AuroraCurtain.DriftWrapCycle).Within(0.5f));
    }

    [Test]
    public void Drift_WrapsIntoTheCycleAndStaysNonNegative()
    {
        Assert.That(AuroraCurtain.Drift(0f), Is.EqualTo(0f).Within(Tolerance));
        Assert.That(AuroraCurtain.Drift(AuroraCurtain.DriftWrapTicks * 3.5f),
            Is.GreaterThanOrEqualTo(0f).And.LessThan(AuroraCurtain.DriftWrapCycle));

        // Negative time cannot come from TicksGame, but a negative drift would mirror the field rather
        // than offset it, so the guard is worth pinning.
        Assert.That(AuroraCurtain.Drift(-1f), Is.GreaterThanOrEqualTo(0f));
    }

    [Test]
    public void Wave_IsUnchangedAcrossTheDriftWrap()
    {
        // The payoff for choosing 840 lattice units: every field's drift term lands on an exact multiple
        // of its own period, so the wrap is invisible rather than a once-every-1.4M-ticks pop.
        for (int i = 0; i < 24; i++)
        {
            float u = i / 24f;

            Assert.That(
                AuroraCurtain.Wave(u, 0.4f, AuroraCurtain.DriftWrapTicks),
                Is.EqualTo(AuroraCurtain.Wave(u, 0.4f, 0f)).Within(2e-3f),
                $"drift wrap is not seamless at u={u}");
        }
    }

    // --- FillRows: the slicing contract with the adapter ---

    [Test]
    public void FillRows_WritesOnlyTheRequestedSlice()
    {
        const int side = 32;
        byte[] buffer = new byte[side * side * 4];

        AuroraCurtain.FillRows(buffer, side, side, firstRow: 8, rowCount: 4, time: 100f,
            tintR: 0f, tintG: 0f, tintB: 0f, tintWeight: 0f);

        // Rows outside [8, 12) must be untouched — the adapter relies on this to roll a refresh across
        // many frames without disturbing rows baked earlier.
        for (int y = 0; y < side; y++)
        {
            bool inSlice = y >= 8 && y < 12;
            bool anyNonZero = false;

            for (int x = 0; x < side * 4; x++)
            {
                if (buffer[y * side * 4 + x] != 0)
                    anyNonZero = true;
            }

            if (!inSlice)
                Assert.That(anyNonZero, Is.False, $"row {y} was written but is outside the slice");
        }
    }

    [Test]
    public void FillRows_ProducesIdenticalBytes_WhetherBakedWholeOrInSlices()
    {
        // The core safety property behind incremental refresh: a field assembled from several slices at
        // the same time value must be byte-identical to one baked in a single pass. Anything less is a
        // visible tear at the slice boundary.
        const int side = 32;
        byte[] whole = new byte[side * side * 4];
        byte[] sliced = new byte[side * side * 4];

        AuroraCurtain.FillRows(whole, side, side, 0, side, 777f, 0.2f, 0.4f, 0.6f, 0.3f);

        for (int row = 0; row < side; row += 5)
            AuroraCurtain.FillRows(sliced, side, side, row, 5, 777f, 0.2f, 0.4f, 0.6f, 0.3f);

        Assert.That(sliced, Is.EqualTo(whole));
    }

    [Test]
    public void FillRows_ClipsASliceRunningPastTheLastRow()
    {
        // The adapter's rolling cursor can ask for more rows than remain; that must clip, not overrun.
        const int side = 16;
        byte[] buffer = new byte[side * side * 4];

        Assert.DoesNotThrow(() =>
            AuroraCurtain.FillRows(buffer, side, side, firstRow: 14, rowCount: 10, time: 0f,
                tintR: 0f, tintG: 0f, tintB: 0f, tintWeight: 0f));
    }

    [Test]
    public void FillRows_ToleratesDegenerateArguments()
    {
        // Defensive: a null buffer or nonsense dimensions must no-op rather than take the render down.
        Assert.DoesNotThrow(() =>
            AuroraCurtain.FillRows(null!, 8, 8, 0, 8, 0f, 0f, 0f, 0f, 0f));
        Assert.DoesNotThrow(() =>
            AuroraCurtain.FillRows(new byte[4], 0, 0, 0, 1, 0f, 0f, 0f, 0f, 0f));
        Assert.DoesNotThrow(() =>
            AuroraCurtain.FillRows(new byte[16 * 4], 4, 4, firstRow: -3, rowCount: 2, time: 0f,
                tintR: 0f, tintG: 0f, tintB: 0f, tintWeight: 0f));
    }

    [Test]
    public void FillRows_FullTintWeightOverridesThePaletteEntirely()
    {
        // The tint knob's two extremes, pinned so DriverTintWeight can be retuned with confidence about
        // what it interpolates between.
        const int side = 8;
        byte[] buffer = new byte[side * side * 4];

        AuroraCurtain.FillRows(buffer, side, side, 0, side, 500f,
            tintR: 1f, tintG: 0f, tintB: 0f, tintWeight: 1f);

        for (int p = 0; p < side * side; p++)
        {
            Assert.That(buffer[p * 4], Is.EqualTo(255), "red channel should be saturated by the tint");
            Assert.That(buffer[p * 4 + 1], Is.EqualTo(0), "green should be fully displaced");
            Assert.That(buffer[p * 4 + 2], Is.EqualTo(0), "blue should be fully displaced");
        }
    }

    [Test]
    public void DriverTintWeight_LeavesThePaletteDominant()
    {
        // If this ever passed ~0.5 the curtain would be closer to one flat driver colour than to its own
        // multi-hue palette, which would undo #42's "several colours at once" requirement.
        Assert.That(AuroraCurtain.DriverTintWeight, Is.GreaterThan(0f).And.LessThan(0.5f));
    }

    private static void AssertNearlyEqual(AuroraMath.Rgb a, AuroraMath.Rgb b)
    {
        Assert.That(a.R, Is.EqualTo(b.R).Within(0.02f));
        Assert.That(a.G, Is.EqualTo(b.G).Within(0.02f));
        Assert.That(a.B, Is.EqualTo(b.B).Within(0.02f));
    }
}
