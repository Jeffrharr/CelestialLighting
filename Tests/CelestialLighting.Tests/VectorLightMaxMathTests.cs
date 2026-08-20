using System;

namespace CelestialLighting.Tests;

// Offline unit tests for §27's max composition (Source/VectorLightMaxMath.cs, linked into this
// project so these run against the exact shipped file).
//
// THE LOAD-BEARING TESTS ARE THE ZERO ONES. This term's whole claim is that it adds nothing wherever
// vanilla's light already went straight there — that is what lets the doorway be as bright as the
// curve says without the room moving at all. A term that is merely CLOSE to zero in the open is a
// term that lifts every lit cell of every colony by a unit or two, which is precisely the failure
// that shipped once and was reported from play as "the room itself is too bright".
//
// So the free-space assertions are equality, not tolerance, and they are made against
// VanillaGlowFlood — an actual Dijkstra over an actual lattice, transcribed from the game and
// sharing no code with what it judges. See that file's header for why a differential test written
// any other way is a tautology.
[TestFixture]
public class VectorLightMaxMathTests
{
    // A vanilla torch: ThingDefs_Buildings_Furniture's Torch lamp glows radius 12 at (252, 187, 113)
    // in ColorInt units. Any emitter would do; using a real one keeps the numbers in the range the
    // live probes read.
    private const int TorchR = 252;
    private const int TorchG = 187;
    private const int TorchB = 113;
    private const float TorchRadius = 12f;

    private static bool NothingBlocked(int dx, int dz) => false;

    // ---- the flood's own arithmetic ------------------------------------------------------

    [Test]
    public void SourceCellCostsTheSeedAndNotZero()
    {
        Assert.That(VectorLightMaxMath.FreeFloodCost(0, 0), Is.EqualTo(100));
    }

    [TestCase(1, 0, 200)]
    [TestCase(0, 1, 200)]
    [TestCase(-3, 0, 400)]
    [TestCase(1, 1, 241)]
    [TestCase(-2, 2, 382)]
    [TestCase(3, 1, 441)]
    [TestCase(-4, -7, 964)]
    public void FreeFloodCostIsSeedPlusOctile(int dx, int dz, int expected)
    {
        Assert.That(VectorLightMaxMath.FreeFloodCost(dx, dz), Is.EqualTo(expected));
    }

    // The oracle test for the distance itself: whatever the flood ACTUALLY accumulated getting to a
    // cell with nothing in the way is what the closed form has to produce. Every cell, not a sample.
    [Test]
    public void FreeFloodCostMatchesTheFloodItActuallyRuns()
    {
        VanillaGlowFlood.Result flood =
            VanillaGlowFlood.Flood(TorchR, TorchG, TorchB, TorchRadius, NothingBlocked);

        for (int dz = -flood.Radius; dz <= flood.Radius; dz++)
        {
            for (int dx = -flood.Radius; dx <= flood.Radius; dx++)
            {
                int index = flood.Index(dx, dz);

                // Cells the flood never reached record nothing; the radius cut below is what the
                // closed form answers for those, and it is tested separately.
                if (flood.IntDist[index] == 0)
                    continue;

                Assert.That(
                    VectorLightMaxMath.FreeFloodCost(dx, dz), Is.EqualTo(flood.IntDist[index]),
                    $"cell ({dx}, {dz})");
            }
        }
    }

    [TestCase(0.5f, 50)]
    [TestCase(12f, 1200)]
    [TestCase(11.5f, 1150)]
    // Banker's rounding, which is what Mathf.RoundToInt does: 0.125 * 100 is 12.5 exactly in float
    // and rounds to the EVEN 12, where `(int)(f + 0.5f)` would give 13.
    [TestCase(0.125f, 12)]
    [TestCase(0.135f, 14)]
    public void RoundToIntFollowsUnitysRounding(float value, int expected)
    {
        Assert.That(VectorLightMaxMath.RoundToIntLikeUnity(value * 100f), Is.EqualTo(expected));
    }

    // ---- the straight-line value, against the oracle -------------------------------------

    // THE CENTRAL TEST OF THE FILE. In free space the straight line IS the path the flood took, so
    // every cell must come back byte-identical — all three channels, at every distance, out to the
    // rim where the curve underflows.
    [TestCase(4f)]
    [TestCase(8f)]
    [TestCase(12f)]
    [TestCase(14f)]
    public void StraightLineGlowReproducesTheFloodExactlyInFreeSpace(float radius)
    {
        VanillaGlowFlood.Result flood =
            VanillaGlowFlood.Flood(TorchR, TorchG, TorchB, radius, NothingBlocked);

        for (int dz = -flood.Radius; dz <= flood.Radius; dz++)
        {
            for (int dx = -flood.Radius; dx <= flood.Radius; dx++)
            {
                int index = flood.Index(dx, dz);

                bool stored = VectorLightMaxMath.StraightLineGlow(
                    TorchR, TorchG, TorchB, dx, dz, radius, out int r, out int g, out int b);

                bool floodStored = flood.R[index] != 0 || flood.G[index] != 0 || flood.B[index] != 0;

                Assert.That(stored, Is.EqualTo(floodStored), $"stored at ({dx}, {dz})");
                Assert.That(r, Is.EqualTo(flood.R[index]), $"red at ({dx}, {dz})");
                Assert.That(g, Is.EqualTo(flood.G[index]), $"green at ({dx}, {dz})");
                Assert.That(b, Is.EqualTo(flood.B[index]), $"blue at ({dx}, {dz})");
            }
        }
    }

    // The consequence of the test above, stated as the thing the feature actually promises. This is
    // the assertion the whole composition rests on: an unobstructed lamp owes NOTHING anywhere, so
    // an open room renders at exactly vanilla's level and the beam's brightness is free to be set on
    // its own merits.
    [Test]
    public void NothingIsOwedAnywhereAroundAnUnobstructedLamp()
    {
        VanillaGlowFlood.Result flood =
            VanillaGlowFlood.Flood(TorchR, TorchG, TorchB, TorchRadius, NothingBlocked);

        for (int dz = -flood.Radius; dz <= flood.Radius; dz++)
        {
            for (int dx = -flood.Radius; dx <= flood.Radius; dx++)
            {
                int index = flood.Index(dx, dz);

                VectorLightMaxMath.StraightLineGlow(
                    TorchR, TorchG, TorchB, dx, dz, TorchRadius, out int r, out int g, out int b);

                Assert.That(
                    VectorLightMaxMath.OwedChannel(r, flood.R[index], 255), Is.Zero, $"red at ({dx}, {dz})");
                Assert.That(
                    VectorLightMaxMath.OwedChannel(g, flood.G[index], 255), Is.Zero, $"green at ({dx}, {dz})");
                Assert.That(
                    VectorLightMaxMath.OwedChannel(b, flood.B[index], 255), Is.Zero, $"blue at ({dx}, {dz})");
            }
        }
    }

    // PROVING THE TEST ABOVE CAN GO RED, which is the only thing that makes it evidence. The seed is
    // the single constant that phase 2b got wrong, and dropping it must break free-space parity
    // loudly rather than shift it by a rounding error — so this asserts the size of the error the
    // seed prevents rather than merely that there is one.
    [Test]
    public void DroppingTheSourceSeedWouldOweLightAllOverAnOpenRoom()
    {
        VanillaGlowFlood.Result flood =
            VanillaGlowFlood.Flood(TorchR, TorchG, TorchB, TorchRadius, NothingBlocked);

        int owing = 0;
        int worst = 0;

        for (int dz = -flood.Radius; dz <= flood.Radius; dz++)
        {
            for (int dx = -flood.Radius; dx <= flood.Radius; dx++)
            {
                if (dx == 0 && dz == 0)
                    continue;

                int index = flood.Index(dx, dz);

                if (flood.R[index] == 0)
                    continue;

                // The unseeded distance: octile alone, i.e. the curve sampled one whole cell nearer
                // the lamp than vanilla ever samples it.
                float unseeded = (VectorLightMaxMath.FreeFloodCost(dx, dz) - VectorLightMaxMath.SourceSeedCost) / 100f;
                float falloff = VectorLightMaxMath.VanillaFalloff(unseeded, TorchRadius);
                VectorLightMaxMath.ProjectLikeVanilla(
                    (int)(TorchR * falloff), (int)(TorchG * falloff), (int)(TorchB * falloff),
                    out int r, out int _, out int _);

                int owed = VectorLightMaxMath.OwedChannel(r, flood.R[index], 255);

                if (owed > 0)
                    owing++;

                worst = Math.Max(worst, owed);
            }
        }

        Assert.That(owing, Is.GreaterThan(100), "the unseeded form owes light in the open");
        Assert.That(worst, Is.GreaterThan(40), "and not by a rounding error");
    }

    // ---- what a wall does ----------------------------------------------------------------

    // A wall with a one-cell gap in it, which is the shape the whole feature is about. Behind the
    // wall the flood has to come round through the gap, so what it delivers is dimmer than the
    // straight line — and THAT is where the max has something to add.
    [Test]
    public void LightIsOwedOnlyWhereTheFloodHadToBend()
    {
        // A wall along z = 3 for the whole window, with a gap at x = 0.
        bool Blocked(int dx, int dz) => dz == 3 && dx != 0;

        VanillaGlowFlood.Result flood =
            VanillaGlowFlood.Flood(TorchR, TorchG, TorchB, TorchRadius, Blocked);

        int owedBehindTheWall = 0;

        for (int dz = -flood.Radius; dz <= flood.Radius; dz++)
        {
            for (int dx = -flood.Radius; dx <= flood.Radius; dx++)
            {
                int index = flood.Index(dx, dz);

                VectorLightMaxMath.StraightLineGlow(
                    TorchR, TorchG, TorchB, dx, dz, TorchRadius, out int r, out int _, out int _);

                int owed = VectorLightMaxMath.OwedChannel(r, flood.R[index], 255);

                // In front of the wall nothing bent, so nothing is owed — the room the lamp stands
                // in is untouched even though the lamp is now an obstructed emitter.
                if (dz < 3)
                    Assert.That(owed, Is.Zero, $"in front of the wall at ({dx}, {dz})");

                if (dz > 3 && owed > 0)
                    owedBehindTheWall++;
            }
        }

        Assert.That(owedBehindTheWall, Is.GreaterThan(0), "light is owed somewhere past the wall");
    }

    // The case vanilla's grid cannot express at all, and §27e's entire headline: a door standing
    // open. The glow grid never learns the door moved, so it delivers a stored ZERO past it, while
    // our polygon sees straight through. A term proportional to what vanilla delivered would be zero
    // here — which is how an earlier ratio-shaped attempt at this threw no beam through a doorway at
    // all — so the test pins that the whole straight-line value comes through.
    [Test]
    public void ACellVanillaNeverReachedIsOwedItsWholeStraightLineValue()
    {
        VectorLightMaxMath.StraightLineGlow(
            TorchR, TorchG, TorchB, 0, 4, TorchRadius, out int r, out int g, out int b);

        Assert.That(VectorLightMaxMath.OwedChannel(r, 0, 255), Is.EqualTo(r));
        Assert.That(VectorLightMaxMath.OwedChannel(g, 0, 255), Is.EqualTo(g));
        Assert.That(VectorLightMaxMath.OwedChannel(b, 0, 255), Is.EqualTo(b));
        Assert.That(r, Is.GreaterThan(0), "the straight line reaches four cells at radius 12");
    }

    // ---- coverage, and the composition itself --------------------------------------------

    [TestCase(0, 0)]
    [TestCase(64, 14)]
    [TestCase(128, 29)]
    [TestCase(255, 58)]
    // Above a byte cannot happen from CoverageAt, but the clamp is what stops a caller's arithmetic
    // error becoming a beam brighter than the curve allows.
    [TestCase(400, 58)]
    public void OwedLightIsScaledByTheShareOfTheCellWeCanSee(int coverage, int expected)
    {
        Assert.That(VectorLightMaxMath.OwedChannel(58, 0, coverage), Is.EqualTo(expected));
    }

    [Test]
    public void OwedLightIsZeroWhereVanillaDeliveredMore()
    {
        Assert.That(VectorLightMaxMath.OwedChannel(40, 58, 255), Is.Zero);
        Assert.That(VectorLightMaxMath.OwedChannel(58, 58, 255), Is.Zero);
    }

    // THE COMPOSITION, ASSERTED AS THE IDENTITY IT CLAIMS TO BE. What the renderer ends up drawing is
    // vanilla's own value less the net term, and that has to equal the share of the cell we can see
    // of the larger of the two models. Swept rather than sampled, because the interesting failures
    // are at the boundaries — coverage 0, coverage full, and the crossover where the two models are
    // equal.
    [Test]
    public void SubtractingTheNetTermLeavesTheCoveredShareOfTheMax()
    {
        foreach (int delivered in new[] { 0, 1, 37, 58, 128, 254, 255 })
        {
            foreach (int straight in new[] { 0, 1, 37, 58, 128, 254, 255 })
            {
                foreach (int coverage in new[] { 0, 1, 64, 128, 254, 255 })
                {
                    int net = VectorLightMaxMath.NetShadowChannel(straight, delivered, coverage);
                    int rendered = delivered - net;

                    int larger = straight > delivered ? straight : delivered;
                    int expected = delivered - (delivered * (255 - coverage) / 255)
                        + (straight > delivered ? (straight - delivered) * coverage / 255 : 0);

                    Assert.That(
                        rendered, Is.EqualTo(expected),
                        $"delivered {delivered}, straight {straight}, coverage {coverage}");

                    // And the identity that expression exists to express, to within the integer
                    // division the renderer does in bytes: at full coverage it is exactly the max,
                    // at zero coverage exactly nothing.
                    if (coverage >= 255)
                        Assert.That(rendered, Is.EqualTo(larger));

                    if (coverage <= 0)
                        Assert.That(rendered, Is.Zero);
                }
            }
        }
    }

    // The flag's off arm has to be the shipped renderer bit for bit, not an approximation of it —
    // otherwise the A/B measures the composition plus an unrelated drift. With the max off the mask
    // passes a straight-line value of zero, and this pins that the term collapses to exactly the
    // subtraction phase 3 has always done.
    [TestCase(58, 0)]
    [TestCase(58, 64)]
    [TestCase(58, 128)]
    [TestCase(58, 255)]
    [TestCase(172, 200)]
    public void WithNoStraightLineValueTheTermIsPhaseThreesSubtractionExactly(int delivered, int coverage)
    {
        Assert.That(
            VectorLightMaxMath.NetShadowChannel(0, delivered, coverage),
            Is.EqualTo(delivered * (255 - coverage) / 255));
    }

    // A cell we cannot see at all still has its bent light taken away — the max does not weaken the
    // shadow, which was the standing worry about composing anything with vanilla's flood.
    [Test]
    public void TheShadowIsUntouchedByTheMax()
    {
        Assert.That(VectorLightMaxMath.NetShadowChannel(255, 58, 0), Is.EqualTo(58));
    }

    // ---- the curve -----------------------------------------------------------------------

    [Test]
    public void FalloffIsZeroPastTheRadius()
    {
        Assert.That(VectorLightMaxMath.VanillaFalloff(12.01f, 12f), Is.Zero);
    }

    // THE BRIGHTEST ANY CELL EVER GETS is the curve at distance 1, because the seed means no cell is
    // ever evaluated nearer than that — 0.95 of the emitter's own colour, which is why a torch peaks
    // at 239 rather than 252 and never troubles the projection's clamp.
    [Test]
    public void TheSeededSourceIsTheCurvesLargestReachableValue()
    {
        float atSource = VectorLightMaxMath.VanillaFalloff(1f, 12f);

        Assert.That(atSource, Is.EqualTo(0.95f).Within(1e-6f));
        Assert.That(VectorLightMaxMath.VanillaFalloff(1.41f, 12f), Is.LessThan(atSource));
    }

    // Vanilla's curve has NO minimum-distance clamp, which is the one place it parts company with
    // VectorLightMath.Falloff — and the reason this file transcribes the curve rather than reusing
    // that one. Inside a cell of the source the inverse-square term runs away, which vanilla never
    // sees and §27's own fan would (its apex sits at distance zero), so §27 clamps and vanilla must
    // not. A clamp quietly shared between them makes the difference nonzero next to every lamp.
    [Test]
    public void ThereIsNoMinimumDistanceClampTheWayOurOwnCurveHasOne()
    {
        Assert.That(VectorLightMaxMath.VanillaFalloff(0.5f, 12f), Is.GreaterThan(1f));
        Assert.That(VectorLightMath.Falloff(0.5f, 12f), Is.EqualTo(VectorLightMath.Falloff(1f, 12f)));
    }

    [Test]
    public void ProjectionKeepsHueAndClipsOnlyLevel()
    {
        VectorLightMaxMath.ProjectLikeVanilla(510, 255, 0, out int r, out int g, out int b);

        Assert.That(r, Is.EqualTo(255));
        Assert.That(g, Is.EqualTo(127));
        Assert.That(b, Is.Zero);
    }

    [Test]
    public void ProjectionLeavesInRangeChannelsAlone()
    {
        VectorLightMaxMath.ProjectLikeVanilla(252, 187, 113, out int r, out int g, out int b);

        Assert.That(r, Is.EqualTo(252));
        Assert.That(g, Is.EqualTo(187));
        Assert.That(b, Is.EqualTo(113));
    }
}
