using CelestialLighting;
using NUnit.Framework;

namespace CelestialLighting.Tests;

// §27e phase 2: where the two door leaves stand part-way through a slide, and the quantisation that
// makes tracking them affordable.
[TestFixture]
public class DoorApertureMathTests
{
    // THE TWO ENDS ARE THE LOAD-BEARING CASES, because both are pinned live by measurements taken
    // before this file existed: a shut door must reproduce the closed-door occluder exactly, and a
    // fully open one must reproduce a bare doorway exactly. Phase 2 is only allowed to change what
    // happens BETWEEN them.
    [Test]
    public void ShutDoorLeavesCoverTheWholeCell()
    {
        DoorApertureMath.LeafSpans(4f, 0f, out float aStart, out float aEnd, out float bStart, out float bEnd);

        Assert.That(aStart, Is.EqualTo(4f).Within(1e-6));
        Assert.That(aEnd, Is.EqualTo(4.5f).Within(1e-6));
        Assert.That(bStart, Is.EqualTo(4.5f).Within(1e-6));
        Assert.That(bEnd, Is.EqualTo(5f).Within(1e-6));

        // Meeting in the middle with no gap is what makes a shut door indistinguishable from a wall.
        Assert.That(bStart - aEnd, Is.EqualTo(0f).Within(1e-6), "a shut door must leave no gap");
    }

    [Test]
    public void FullyOpenDoorLeavesVanish()
    {
        DoorApertureMath.LeafSpans(4f, 1f, out float aStart, out float aEnd, out float bStart, out float bEnd);

        Assert.That(aEnd - aStart, Is.EqualTo(0f).Within(1e-6));
        Assert.That(bEnd - bStart, Is.EqualTo(0f).Within(1e-6));
        Assert.That(DoorApertureMath.LeafWorthEmitting(aStart, aEnd), Is.False,
            "a vanished leaf must not be emitted -- a zero-length segment is a degenerate ray target");
        Assert.That(DoorApertureMath.LeafWorthEmitting(bStart, bEnd), Is.False);
    }

    // The gap is the feature: it must equal openPct exactly, so the beam's width is a direct readout
    // of how far the door has slid rather than some eased function of it.
    [TestCase(0f,    ExpectedResult = 0f)]
    [TestCase(0.25f, ExpectedResult = 0.25f)]
    [TestCase(0.5f,  ExpectedResult = 0.5f)]
    [TestCase(0.75f, ExpectedResult = 0.75f)]
    [TestCase(1f,    ExpectedResult = 1f)]
    public float GapWidthEqualsOpenFraction(float openPct)
    {
        DoorApertureMath.LeafSpans(0f, openPct, out _, out float aEnd, out float bStart, out _);
        return (float)System.Math.Round(bStart - aEnd, 5);
    }

    // The leaves stay inside the cell at every aperture, and stay symmetric about its centre. If
    // either failed, the beam would be off-centre in the doorway, which reads as the door being
    // mis-drawn rather than as a lighting bug.
    [Test]
    public void LeavesStayInsideTheCellAndSymmetric()
    {
        for (int i = 0; i <= 20; i++)
        {
            float p = i / 20f;
            DoorApertureMath.LeafSpans(7f, p, out float aStart, out float aEnd, out float bStart, out float bEnd);

            Assert.That(aStart, Is.EqualTo(7f).Within(1e-6), $"low edge moved at p={p}");
            Assert.That(bEnd, Is.EqualTo(8f).Within(1e-6), $"high edge moved at p={p}");
            Assert.That(aEnd, Is.LessThanOrEqualTo(bStart + 1e-6), $"leaves crossed at p={p}");
            Assert.That((aEnd - 7f), Is.EqualTo(8f - bStart).Within(1e-6), $"asymmetric at p={p}");
        }
    }

    // A modded door's OpenPct is `protected virtual` and can return anything at all; the geometry
    // must not follow it out of the cell.
    [TestCase(-5f)]
    [TestCase(-0.001f)]
    [TestCase(1.001f)]
    [TestCase(99f)]
    public void OutOfRangeOpenFractionIsClamped(float openPct)
    {
        DoorApertureMath.LeafSpans(0f, openPct, out float aStart, out float aEnd, out float bStart, out float bEnd);

        Assert.That(aStart, Is.EqualTo(0f).Within(1e-6));
        Assert.That(bEnd, Is.EqualTo(1f).Within(1e-6));
        Assert.That(aEnd, Is.InRange(-1e-6f, 0.5f + 1e-6f));
        Assert.That(bStart, Is.InRange(0.5f - 1e-6f, 1f + 1e-6f));
    }

    // QUANTISATION. The endpoints must survive it exactly: a door that quantised to 0.99 instead of 1
    // would leave two hairline leaves permanently in the doorway, and the fully-open frame would stop
    // reproducing the bare-doorway measurement it is pinned against.
    [TestCase(0f,    ExpectedResult = 0f)]
    [TestCase(1f,    ExpectedResult = 1f)]
    [TestCase(0.5f,  ExpectedResult = 0.5f)]
    // 0.0625 is the midpoint of the first step, so 0.06 belongs to 0 and 0.07 to 0.125. Spelled out
    // as a pair because getting this backwards is how a "snaps to the nearest step" claim quietly
    // becomes "truncates", which would make the beam lag the door by up to a whole step all the way
    // open and never quite reach the closed frame on the way back.
    [TestCase(0.06f, ExpectedResult = 0f)]
    [TestCase(0.07f, ExpectedResult = 0.125f)]
    [TestCase(0.01f, ExpectedResult = 0f)]
    [TestCase(0.99f, ExpectedResult = 1f)]
    public float QuantisePreservesEndpointsAndSnapsBetween(float openPct) =>
        DoorApertureMath.Quantise(openPct, 8);

    // The bound that makes phase 2 affordable, stated as a property: however many distinct values a
    // door's OpenPct takes on the way open, the quantised sequence takes at most `steps + 1` of them.
    // That is the number of bakes per swing, and it is what stops a slow door costing more than a
    // fast one.
    [Test]
    public void QuantisationBoundsTheNumberOfDistinctApertures()
    {
        System.Collections.Generic.HashSet<float> distinct = new System.Collections.Generic.HashSet<float>();

        // 600 ticks is far slower than any vanilla door, so this is a worst case, not a typical one.
        for (int tick = 0; tick <= 600; tick++)
        {
            distinct.Add(DoorApertureMath.Quantise(tick / 600f, DoorApertureMath.DefaultQuantisationSteps));
        }

        Assert.That(distinct.Count, Is.LessThanOrEqualTo(DoorApertureMath.DefaultQuantisationSteps + 1),
            "quantisation must cap bakes per swing regardless of how slowly the door moves");
    }

    // Zero or negative steps means "do not quantise", the escape hatch the scenario uses to film the
    // unquantised comparison. It must pass the value through untouched rather than divide by zero.
    [TestCase(0)]
    [TestCase(-1)]
    public void NonPositiveStepsPassesThrough(int steps)
    {
        Assert.That(DoorApertureMath.Quantise(0.4321f, steps), Is.EqualTo(0.4321f).Within(1e-6));
    }
}
