using CelestialLighting;
using NUnit.Framework;

namespace CelestialLighting.Tests;

// Offline coverage for the alternative §11a shape function (issue #42).
//
// Most of what is worth asserting here is not "does it look right" — that is what
// Tools/AuroraPreview is for — but the two properties the file's own comments CLAIM and that nothing
// else can catch:
//
//   * The field tiles. A seam is invisible in a still and unmistakable in a live run, hours in.
//   * The drift-wrap arithmetic holds. AuroraCurtainHemRays.DriftWrapCycle contains a four-step proof
//     that the wrap is bit-exact; every step of that proof is a constraint on the constants table, and
//     the tests below are what stop someone editing a period or a coefficient and quietly voiding it.
//
// The wrap in particular is a bug that appears once every 4,000,000 ticks. There is no other way to
// find it.
[TestFixture]
public class AuroraCurtainHemRaysTests
{
    // Tight enough that a real seam fails, loose enough that float rounding does not. The wrap tests
    // below compare a sample at u against one at u+1, and `(u + 1) * period` does not round to
    // `u * period + period` in single precision — a few parts in 10^7. That discrepancy cannot occur
    // in the shipped texture, which only ever samples texels 0..width-1 and lets the GPU wrap the UVs,
    // so it is an artifact of how the property is probed rather than of the field.
    private const float Tolerance = 5e-5f;

    // --- The drift-wrap proof, restated as constraints on the table ---

    // Step 1 of the proof in DriftWrapCycle: every lattice period divides 120, so that a shift of
    // 600 lattice units (what the wrap applies) is a whole number of periods for every field.
    [Test]
    public void EveryLatticePeriod_Divides120()
    {
        for (int i = 0; i < AuroraCurtainHemRays.CurtainCount; i++)
        {
            AuroraCurtainHemRays.CurtainSpec c = AuroraCurtainHemRays.Curtain(i);

            Assert.That(120 % c.HemPeriod, Is.Zero, $"curtain {i} hem period");
            Assert.That(120 % c.RayPeriod, Is.Zero, $"curtain {i} ray period");
            Assert.That(120 % c.RayClumpPeriod, Is.Zero, $"curtain {i} ray clump period");
            Assert.That(120 % c.EnvelopePeriod, Is.Zero, $"curtain {i} envelope period");
        }

        Assert.That(120 % AuroraCurtainHemRays.HueWobblePeriod, Is.Zero, "hue wobble period");
    }

    // Step 2: horizontal coefficients sit on a 0.25 grid. Step 3: vertical ones on a 1/32 grid. Both
    // grids are powers of two, which is step 4 — the products with 2400 are exact floats, not rounded
    // ones, so the wrap is bit-identical rather than merely imperceptible.
    [Test]
    public void EveryDriftCoefficient_SitsOnItsGrid()
    {
        for (int i = 0; i < AuroraCurtainHemRays.CurtainCount; i++)
        {
            AuroraCurtainHemRays.CurtainSpec c = AuroraCurtainHemRays.Curtain(i);

            AssertOnGrid(c.HemDrift, 0.25f, $"curtain {i} hem drift");
            AssertOnGrid(c.RayDrift, 0.25f, $"curtain {i} ray drift");
            AssertOnGrid(c.EnvelopeDrift, 0.25f, $"curtain {i} envelope drift");
            AssertOnGrid(c.HemRise, 1f / 32f, $"curtain {i} hem rise");
        }

        AssertOnGrid(AuroraCurtainHemRays.HueWobbleDrift, 0.25f, "hue wobble drift");
    }

    [Test]
    public void DriftWrapTicks_MatchesDriftWrapCycle()
    {
        Assert.That(
            AuroraCurtainHemRays.DriftWrapTicks * AuroraCurtainHemRays.DriftRate,
            Is.EqualTo(AuroraCurtainHemRays.DriftWrapCycle).Within(0.01f));
    }

    // The point of all of the above: the field at the wrap boundary is the field at zero. Sampled
    // across the tile rather than at one point, because a seam that only shows in one band is still a
    // seam sweeping across somebody's colony.
    [Test]
    public void Field_IsUnchangedAcrossTheDriftWrap()
    {
        for (int gy = 0; gy < 8; gy++)
        {
            for (int gx = 0; gx < 8; gx++)
            {
                float u = gx / 8f;
                float v = gy / 8f;

                AuroraCurtainHemRays.Sample before = AuroraCurtainHemRays.At(u, v, 0f);
                AuroraCurtainHemRays.Sample after =
                    AuroraCurtainHemRays.At(u, v, AuroraCurtainHemRays.DriftWrapTicks);

                Assert.That(after.Alpha, Is.EqualTo(before.Alpha).Within(0.004f), $"alpha at {u},{v}");
            }
        }
    }

    // --- Tileability ---

    // Horizontal wrap. Every field varies with u only through an AuroraNoise sample at an integer
    // period, so this should be exact, not approximate.
    [TestCase(0f)]
    [TestCase(37000f)]
    public void Field_WrapsExactlyInU(float time)
    {
        for (int gy = 0; gy < 8; gy++)
        {
            for (int gx = 0; gx < 8; gx++)
            {
                float u = gx / 8f;
                float v = gy / 8f;

                AuroraCurtainHemRays.Sample here = AuroraCurtainHemRays.At(u, v, time);
                AuroraCurtainHemRays.Sample wrapped = AuroraCurtainHemRays.At(u + 1f, v, time);

                Assert.That(wrapped.Alpha, Is.EqualTo(here.Alpha).Within(Tolerance), $"alpha at {u},{v}");
                Assert.That(wrapped.Hue, Is.EqualTo(here.Hue).Within(Tolerance), $"hue at {u},{v}");
            }
        }
    }

    // Vertical wrap, which is the interesting one: it holds because the height above the hem is a
    // wrapped difference, not because the hem positions were tuned to make it hold.
    [TestCase(0f)]
    [TestCase(37000f)]
    public void Field_WrapsExactlyInV(float time)
    {
        for (int gy = 0; gy < 8; gy++)
        {
            for (int gx = 0; gx < 8; gx++)
            {
                float u = gx / 8f;
                float v = gy / 8f;

                AuroraCurtainHemRays.Sample here = AuroraCurtainHemRays.At(u, v, time);
                AuroraCurtainHemRays.Sample wrapped = AuroraCurtainHemRays.At(u, v + 1f, time);

                Assert.That(wrapped.Alpha, Is.EqualTo(here.Alpha).Within(Tolerance), $"alpha at {u},{v}");
                Assert.That(wrapped.Hue, Is.EqualTo(here.Hue).Within(Tolerance), $"hue at {u},{v}");
            }
        }
    }

    // The bound that makes the vertical wrap provable rather than lucky: a curtain's light has to
    // reach exactly zero before it climbs round into its own hem.
    [Test]
    public void NoCurtainReachesRoundIntoItsOwnHem()
    {
        Assert.That(
            AuroraCurtainHemRays.RayHeightMax + AuroraCurtainHemRays.HemUnderhang,
            Is.LessThan(1f));

        for (int i = 0; i < AuroraCurtainHemRays.CurtainCount; i++)
        {
            Assert.That(
                AuroraCurtainHemRays.Curtain(i).RayHeight,
                Is.LessThanOrEqualTo(AuroraCurtainHemRays.RayHeightMax),
                $"curtain {i}");
        }
    }

    // --- The base of the curtain must stay inside the quad ---

    // The live run taught this one. Vertical drift used to be `drift * HemRise` with drift growing
    // without bound, so every hem migrated steadily up the tile and wrapped around. Under a repeating
    // texture that was invisible. Drawn as a bounded sheet it means THE BASE LEAVES THE QUAD — the hem
    // slides off one edge and reappears at the other, and a curtain with no visible bottom is not a
    // curtain, it is a smear.
    //
    // Swept across u and across a whole drift cycle, because the failure is a slow migration: any
    // single sample looks fine and the band is only violated some hours later.
    [Test]
    public void EveryHem_StaysInsideTheQuad_AtEveryPointOfTheDriftCycle()
    {
        for (int i = 0; i < AuroraCurtainHemRays.CurtainCount; i++)
        {
            AuroraCurtainHemRays.CurtainSpec c = AuroraCurtainHemRays.Curtain(i);

            for (int step = 0; step <= 48; step++)
            {
                float drift = AuroraCurtainHemRays.DriftWrapCycle * step / 48f;

                for (int gx = 0; gx < 16; gx++)
                {
                    float hem = AuroraCurtainHemRays.EvaluateColumn(c, gx / 16f, drift).Hem;

                    Assert.That(hem - AuroraCurtainHemRays.HemUnderhang,
                        Is.GreaterThan(0f),
                        $"curtain {i} hem underhang falls off the bottom at drift {drift}");
                    Assert.That(hem + c.RayHeight,
                        Is.LessThan(1f),
                        $"curtain {i} rays run off the top at drift {drift}");
                }
            }
        }
    }

    // The feather is what removes the hard horizontal seam a bounded quad's edge otherwise rules
    // across the map — the live run showed exactly that seam before this existed.
    [Test]
    public void FieldFadesToNothingAtBothQuadEdges()
    {
        Assert.That(AuroraCurtainHemRays.VerticalFeather(0f), Is.EqualTo(0f).Within(1e-5f));
        Assert.That(AuroraCurtainHemRays.VerticalFeather(1f), Is.EqualTo(0f).Within(1e-5f));
        Assert.That(AuroraCurtainHemRays.VerticalFeather(0.5f), Is.EqualTo(1f).Within(1e-5f));

        for (int gx = 0; gx < 24; gx++)
        {
            float u = gx / 24f;

            Assert.That(AuroraCurtainHemRays.At(u, 0f, 9000f).Alpha, Is.EqualTo(0f).Within(1e-4f),
                $"bottom edge is not dark at u={u} — this is a visible seam");
            Assert.That(AuroraCurtainHemRays.At(u, 0.999f, 9000f).Alpha, Is.LessThan(0.02f),
                $"top edge is not dark at u={u} — this is a visible seam");
        }
    }

    // --- The fast path must be the reference path ---

    // FillRows hoists the noise into a per-column table; At evaluates it per point. The whole
    // performance claim rests on those being the same function, and nothing but this test says so.
    [Test]
    public void FillRows_AgreesWithAt()
    {
        const int side = 32;
        byte[] rgba = new byte[side * side * 4];

        AuroraCurtainHemRays.FillRows(rgba, side, side, 0, side, 4321f, 0f, 0f, 0f, 0f);

        for (int y = 0; y < side; y += 3)
        {
            for (int x = 0; x < side; x += 3)
            {
                AuroraCurtainHemRays.Sample expected =
                    AuroraCurtainHemRays.At(x / (float)side, y / (float)side, 4321f);

                int alpha = rgba[(y * side + x) * 4 + 3];
                Assert.That(alpha, Is.EqualTo(ToByte(expected.Alpha)).Within(1), $"alpha at {x},{y}");
            }
        }
    }

    // Filling in slices is what the adapter actually does, and it must produce the same texture as
    // filling in one pass — otherwise the scratch table is leaking state between calls.
    [Test]
    public void FillRows_InSlices_MatchesOnePass()
    {
        const int side = 24;
        byte[] whole = new byte[side * side * 4];
        byte[] sliced = new byte[side * side * 4];

        AuroraCurtainHemRays.FillRows(whole, side, side, 0, side, 900f, 0.2f, 0.9f, 0.3f, 0.3f);

        for (int row = 0; row < side; row += 5)
            AuroraCurtainHemRays.FillRows(sliced, side, side, row, 5, 900f, 0.2f, 0.9f, 0.3f, 0.3f);

        Assert.That(sliced, Is.EqualTo(whole));
    }

    // --- Shape properties #42 actually asked for ---

    // Structure, not a wash: a real curtain leaves most of the sky genuinely dark. This is the same
    // property AuroraCurtainTests pins for the contour field, at the same thresholds, so the two are
    // held to one standard.
    [Test]
    public void Field_LeavesMostOfTheSkyDark()
    {
        const int side = 64;
        int bright = 0;
        int dark = 0;

        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                float alpha = AuroraCurtainHemRays.At(x / (float)side, y / (float)side, 0f).Alpha;

                if (alpha > 0.4f)
                    bright++;

                if (alpha < 0.05f)
                    dark++;
            }
        }

        Assert.That(bright, Is.LessThan(side * side / 5), "too much of the sky is bright — this is a wash");

        // 28%, loosened from the 33% that was inherited from the contour field's version of this test,
        // and the reason is a change of architecture rather than a change of taste.
        //
        // This measures darkness WITHIN ONE TILE. While the tile was stretched over the whole map those
        // were the same quantity, so one threshold could police both "the arcs have gaps between them"
        // and "the sky is mostly empty". They are now different quantities: the tile is drawn as a
        // bounded SHEET covering part of the map, so in-tile density says nothing about how much of the
        // sky is lit. Taller rays deliberately filled more of the tile — that is the approved look — and
        // holding the old number would have meant undoing it to satisfy a property the tile no longer
        // owns.
        //
        // What this still usefully guards is that arcs remain separated INSIDE a sheet. The map-level
        // property — most of the sky dark, the aurora not obscuring the game — belongs to the sheet
        // layout and is asserted in AuroraSheetLayoutTests.
        Assert.That(dark, Is.GreaterThan(side * side * 28 / 100), "arcs have merged — no dark sky between them");
    }

    // Several colours at once, which is the requirement a single flat tint could never meet. The hue
    // here is driven by height above the hem, so this is really asserting that the altitude
    // stratification survives into the rendered palette.
    [Test]
    public void LitPixels_SpanMoreThanOneHueBand()
    {
        const int side = 64;
        bool sawGreen = false;
        bool sawRed = false;

        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                AuroraCurtainHemRays.Sample s =
                    AuroraCurtainHemRays.At(x / (float)side, y / (float)side, 0f);

                if (s.Alpha > 0.05f)
                {
                    sawGreen |= s.Hue >= AuroraMath.HueGreenLow && s.Hue <= AuroraMath.HueGreenHigh;
                    sawRed |= s.Hue > AuroraMath.HueGreenHigh;
                }
            }
        }

        Assert.That(sawGreen, Is.True, "no green — the dominant auroral line is missing");
        Assert.That(sawRed, Is.True, "no red tops — the altitude stratification is not reaching visible pixels");
    }

    // Motion. A field that does not change over time is a wallpaper, and #42's complaint was as much
    // about stillness as about shape.
    [Test]
    public void Field_ChangesOverTime()
    {
        int changed = 0;

        for (int gy = 0; gy < 16; gy++)
        {
            for (int gx = 0; gx < 16; gx++)
            {
                float u = gx / 16f;
                float v = gy / 16f;

                float now = AuroraCurtainHemRays.At(u, v, 0f).Alpha;
                float later = AuroraCurtainHemRays.At(u, v, 2500f).Alpha;

                if (System.Math.Abs(now - later) > 0.02f)
                    changed++;
            }
        }

        Assert.That(changed, Is.GreaterThan(40), "an in-game hour barely moved the field");
    }

    // --- Robustness ---

    [Test]
    public void Alpha_StaysInRange_EverywhereAndAlways()
    {
        float[] times = { 0f, 1f, 60000f, 1234567f, AuroraCurtainHemRays.DriftWrapTicks - 1 };

        foreach (float time in times)
        {
            for (int gy = 0; gy < 12; gy++)
            {
                for (int gx = 0; gx < 12; gx++)
                {
                    AuroraCurtainHemRays.Sample s =
                        AuroraCurtainHemRays.At(gx / 12f, gy / 12f, time);

                    Assert.That(s.Alpha, Is.InRange(0f, 1f), $"alpha at t={time}");
                    Assert.That(s.Hue, Is.InRange(0f, 1f), $"hue at t={time}");
                }
            }
        }
    }

    // The hem is the brightest part of a curtain — the one silhouette property the whole approach
    // exists to produce. Checked as a column average so a single dim ray cannot fail it.
    [Test]
    public void HemIsBrighterThanTheBodyAboveIt()
    {
        const int side = 96;
        AuroraCurtainHemRays.CurtainSpec c = AuroraCurtainHemRays.Curtain(0);

        float atHem = 0f;
        float aboveHem = 0f;

        for (int x = 0; x < side; x++)
        {
            float u = x / (float)side;
            AuroraCurtainHemRays.ColumnState col = AuroraCurtainHemRays.EvaluateColumn(c, u, 0f);

            atHem += AuroraCurtainHemRays.CurtainAt(col, col.Hem + 0.004f, 0f).Alpha;
            aboveHem += AuroraCurtainHemRays.CurtainAt(col, col.Hem + c.RayHeight * 0.6f, 0f).Alpha;
        }

        Assert.That(atHem, Is.GreaterThan(aboveHem * 2f));
    }

    private static void AssertOnGrid(float value, float grid, string what)
    {
        float steps = value / grid;
        Assert.That(steps, Is.EqualTo((float)System.Math.Round(steps)).Within(1e-6f), what);
    }

    private static int ToByte(float v)
    {
        float c = v < 0f ? 0f : (v > 1f ? 1f : v);
        return (int)(c * 255f + 0.5f);
    }
}
