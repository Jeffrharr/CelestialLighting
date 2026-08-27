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

    // ---- ISSUE #174 PHASE 2: the swing as the LIGHT sees it -----------------------------------
    //
    // The two pure cores are correct apart and were wrong together. Quantise and LeafSpans place the
    // leaves, DoorOcclusionMath decides whether the cell is a wall, and neither can see the composed
    // result: the width of the gap a ray can actually pass through. That composition is what the
    // player watches, so it is what this pins.
    //
    // Stated as a DIRECTION rather than a value list, the same way QuantisationBoundsTheNumberOf-
    // DistinctApertures states a bound: the bug was a gap that got NARROWER as the door opened, and
    // no list of expected widths catches a re-ordering that reintroduces one. A door opening is a
    // monotone act; the light it admits has to be monotone too.
    private static float GapSeenByLight(float rawOpenPct, bool doorOpen, int steps)
    {
        float openPct = DoorApertureMath.Quantise(rawOpenPct, steps);

        bool blocked = DoorOcclusionMath.Occludes(
            blocksLight: true, isDoor: true, doorOpen: doorOpen, openDoorsPassLight: true,
            doorAperture: openPct, apertureTracked: true);

        if (blocked)
        {
            return 0f;
        }

        // No leaves are emitted at either end, so the cell is a bare doorway -- correct at 1, and the
        // phase 2 bug at 0.
        if (openPct <= 0f || openPct >= 1f)
        {
            return 1f;
        }

        DoorApertureMath.LeafSpans(
            0f, openPct, out float _, out float aEnd, out float bStart, out float _);
        return bStart - aEnd;
    }

    // A vanilla wooden door is 45 ticks; 7 and 600 bracket a powered door and an absurdly slow modded
    // one, because the defect's width in ticks depends on the ratio of swing length to step count and
    // a single speed would only prove the one case.
    [TestCase(7)]
    [TestCase(45)]
    [TestCase(600)]
    public void GapSeenByLightNeverNarrowsWhileADoorOpens(int ticksToOpen)
    {
        float previous = GapSeenByLight(0f, doorOpen: false, DoorApertureMath.DefaultQuantisationSteps);

        for (int tick = 0; tick <= ticksToOpen; tick++)
        {
            // `Open` is true from the first tick of the slide while OpenPct is still 0 -- vanilla sets
            // openInt in DoorOpen and only then starts incrementing ticksSinceOpen.
            float gap = GapSeenByLight(
                (float)tick / ticksToOpen, doorOpen: true, DoorApertureMath.DefaultQuantisationSteps);

            Assert.That(gap, Is.GreaterThanOrEqualTo(previous - 1e-6),
                $"gap narrowed at tick {tick} of {ticksToOpen}: {previous:F3} -> {gap:F3}");
            previous = gap;
        }

        Assert.That(previous, Is.EqualTo(1f).Within(1e-6), "a fully open door is a bare doorway");
    }

    // The same property for a close, which phase 1 fixed and phase 2 must not disturb: `Open` is
    // false throughout while OpenPct ramps down, and the gap must shrink monotonically to a wall.
    [Test]
    public void GapSeenByLightNeverWidensWhileADoorCloses()
    {
        float previous = 1f;

        for (int tick = 45; tick >= 0; tick--)
        {
            float gap = GapSeenByLight(
                tick / 45f, doorOpen: false, DoorApertureMath.DefaultQuantisationSteps);

            Assert.That(gap, Is.LessThanOrEqualTo(previous + 1e-6),
                $"gap widened at tick {tick} of a close: {previous:F3} -> {gap:F3}");
            previous = gap;
        }

        Assert.That(previous, Is.EqualTo(0f), "a shut door is a wall");
    }

    // Quantisation is not what caused the phase 2 defect, and this is where that is recorded: the
    // sweep is monotone at every step count, including none at all. Before the fix, turning
    // quantisation off narrowed the bad window from three ticks to one but made the collapse larger
    // (a full cell to 0.022) -- so a fix that only retuned the step count would have looked like
    // progress while leaving the defect in place.
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(8)]
    [TestCase(64)]
    public void TheSweepIsMonotoneAtEveryStepCount(int steps)
    {
        float previous = 0f;

        for (int tick = 0; tick <= 45; tick++)
        {
            float gap = GapSeenByLight(tick / 45f, doorOpen: true, steps);
            Assert.That(gap, Is.GreaterThanOrEqualTo(previous - 1e-6),
                $"gap narrowed at tick {tick} with steps={steps}: {previous:F3} -> {gap:F3}");
            previous = gap;
        }
    }

    // ---- an open door is a hole in vanilla's glow grid too ------------------------------------
    //
    // The rule these pin is one line long, and every one of them is a case that was WRONG in some
    // version of this feature. That is the argument for testing it at all: it is trivial to state and
    // its inputs are three booleans-worth of live door state that only a running game produces.

    // THE POINT OF THE WHOLE FEATURE, stated as the two ends. A door with its leaves apart is a hole,
    // a shut one is not, and everything else is a question about which end it is heading for.
    [TestCase(true, 1f, true)]
    [TestCase(true, 0f, false)]
    [TestCase(false, 0f, false)]
    [TestCase(false, 1f, false)]
    public void ADoorWithItsLeavesApartIsAHole(bool headingOpen, float fraction, bool expected)
    {
        Assert.That(
            DoorApertureMath.GlowGridHoleWanted(
                blocksLightWhenShut: true, headingOpen, fraction),
            Is.EqualTo(expected));
    }

    // MID-SWING IS A HOLE TOO, AND THIS TEST USED TO ASSERT THE OPPOSITE. The old rule waited for a
    // fully slid door, so for the whole slide vanilla's grid said `blocked` while our polygon was
    // already drawing a gap -- and the composition reads exactly that difference to pick a renderer
    // PER CELL. With vanilla delivering nothing, VanillaBentToArrive answers true, SurvivingShare
    // returns 0, and the fragment program subtracts nothing and multiplies the whole beam; the
    // instant the bit moved it began subtracting vanilla's share instead. The beam changed renderer
    // one frame from the end of its own animation, which is the most conspicuous moment there is to
    // do it -- the same charge phase 2 levelled at phase 1's popping aperture.
    [TestCase(0.125f)]
    [TestCase(0.25f)]
    [TestCase(0.5f)]
    [TestCase(0.875f)]
    [TestCase(1f)]
    public void ADoorPartWayThroughItsSwingIsAHole(float fraction)
    {
        Assert.That(
            DoorApertureMath.GlowGridHoleWanted(
                blocksLightWhenShut: true, headingOpen: true, fraction),
            Is.True);
    }

    // AND A CLOSING DOOR STOPS BEING ONE AT ONCE, rather than at the end of its slide. This is the
    // asymmetry, and it is untouched by the reversal above: `Building_Door.DoorTryClose` sets openInt
    // false on the first tick while OpenPct is still at its maximum, so a door that is visually wide
    // open but has been told to shut answers false here. The open end moved; the close end did not.
    [TestCase(1f)]
    [TestCase(0.75f)]
    [TestCase(0.125f)]
    public void ADoorThatHasBeenToldToShutIsNotAHoleEvenWhileStillWideOpen(float fraction)
    {
        Assert.That(
            DoorApertureMath.GlowGridHoleWanted(
                blocksLightWhenShut: true, headingOpen: false, fraction),
            Is.False);
    }

    // A SEE-THROUGH DOOR IS NEVER OURS TO WRITE, and this is the case that would have been a
    // regression rather than a missing feature. Building.SpawnSetup only writes lightBlockers when
    // def.blockLight is true, so a glass door has no bit set -- and a rule that cleared it on open
    // and SET it on close would make glass doors begin blocking gameplay light the first time anyone
    // shut one, which is behaviour vanilla does not have.
    [TestCase(true, 1f)]
    [TestCase(true, 0.5f)]
    [TestCase(false, 0f)]
    public void ASeeThroughDoorIsNeverAHoleWhateverItIsDoing(bool headingOpen, float fraction)
    {
        Assert.That(
            DoorApertureMath.GlowGridHoleWanted(
                blocksLightWhenShut: false, headingOpen, fraction),
            Is.False);
    }

    // OpenPct is `protected virtual`, so a modded door class is free to return anything at all and
    // this reads whatever it returns. Out-of-range values must not make a shut door a hole.
    [TestCase(-1f, true, false)]
    [TestCase(1.5f, true, true)]
    [TestCase(float.NaN, true, false)]
    [TestCase(1.5f, false, false)]
    public void AnOutOfRangeOpenFractionCannotOpenAShutDoor(
        float fraction, bool headingOpen, bool expected)
    {
        Assert.That(
            DoorApertureMath.GlowGridHoleWanted(
                blocksLightWhenShut: true, headingOpen, fraction),
            Is.EqualTo(expected));
    }

    // WITH LEAF TRACKING OFF THE DOOR IS DRAWN FULLY OPEN AT ONCE, so the grid must say so too. This
    // is the case that makes the two halves agree: phase 1's polygon treats an open door as a bare
    // doorway the instant `Open` goes true and never looks at OpenPct, so a grid that waited for the
    // slide would hold the cell blocked underneath a beam we are already drawing.
    [TestCase(0f)]
    [TestCase(0.5f)]
    [TestCase(1f)]
    public void WithoutLeafTrackingTheRenderedApertureIsAlwaysFullyOpen(float openFraction)
    {
        Assert.That(
            DoorApertureMath.RenderedOpenFraction(
                trackingLeaves: false, openFraction, DoorApertureMath.DefaultQuantisationSteps),
            Is.EqualTo(DoorApertureMath.FullyOpen));
    }

    // AND WITH TRACKING ON IT IS THE STEPPED APERTURE, NOT THE DOOR'S RAW SLIDE.
    // VectorLightBlockers.ApertureOf quantises before it places the leaves, so the fan is drawn at
    // k/8 and never at the raw ratio -- which means the raw ratio is not "what the renderer is
    // showing" and never was.
    [TestCase(0f,      0f)]
    [TestCase(0.06f,   0f)]        // rounds down: the leaves have not left the jamb yet
    [TestCase(0.375f,  0.375f)]
    [TestCase(0.4f,    0.375f)]
    [TestCase(0.9f,    0.875f)]
    [TestCase(0.9375f, 1f)]
    [TestCase(1f,      1f)]
    public void WithLeafTrackingTheRenderedApertureIsTheSteppedOneTheFanIsDrawing(
        float openFraction, float expected)
    {
        Assert.That(
            DoorApertureMath.RenderedOpenFraction(
                trackingLeaves: true, openFraction, DoorApertureMath.DefaultQuantisationSteps),
            Is.EqualTo(expected).Within(1e-6));
    }

    // THE TWO SETTINGS COMPOSED, which is the statement the feature actually makes: an open door is a
    // hole exactly when our renderer is drawing one. Under BOTH settings that is now true mid-slide,
    // which is the whole of the renderer-handover fix -- with tracking off the fan draws a full
    // doorway at once, and with it on the fan draws a partial gap, and a partial gap is still a gap.
    [TestCase(true, true, 0.5f, true)]
    [TestCase(false, true, 0.5f, true)]
    [TestCase(true, true, 1f, true)]
    [TestCase(true, true, 0.06f, false)]  // quantises to 0: the leaves still meet, so it is a wall
    [TestCase(false, false, 1f, false)]   // shutting: not a hole under either setting
    [TestCase(true, false, 1f, false)]
    [TestCase(true, true, float.NaN, false)]
    public void TheHoleFollowsWhicheverApertureTheRendererIsDrawing(
        bool trackingLeaves, bool headingOpen, float openFraction, bool expected)
    {
        float rendered = DoorApertureMath.RenderedOpenFraction(
            trackingLeaves, openFraction, DoorApertureMath.DefaultQuantisationSteps);

        Assert.That(
            DoorApertureMath.GlowGridHoleWanted(
                blocksLightWhenShut: true, headingOpen, rendered),
            Is.EqualTo(expected));
    }

    // THE INVARIANT THE WHOLE FIX RESTS ON, and it replaces a test that pinned the opposite one.
    //
    // The composition picks a renderer per cell from whether VANILLA delivered there, so the beam
    // only stays one beam if vanilla's grid and our polygon agree about the doorway at EVERY step of
    // the slide -- not merely at its ends. Our polygon draws a gap exactly when LeafSpans leaves the
    // leaves far enough apart to emit, so that is the quantity the grid has to match, and this walks
    // a whole swing at five door speeds asserting they never disagree on a single tick.
    //
    // Walking five speeds is load-bearing rather than thorough. A powered door takes 11 ticks and an
    // unpowered wooden one 45, and the step boundaries land on completely different ticks in each --
    // a test written against one speed can agree by coincidence.
    [TestCase(45)]    // unpowered wooden: vanilla's 45 / DoorOpenSpeed 1
    [TestCase(20)]
    [TestCase(11)]    // powered, 45 * 0.25 rounded -- the fastest door vanilla builds
    [TestCase(160)]   // slow enough that the steps are 20 ticks apart
    [TestCase(3)]     // fewer ticks than steps, so several steps are skipped in one tick
    public void TheGridIsAHoleExactlyWhileOurPolygonDrawsAGap(int ticksToOpen)
    {
        for (int tick = 0; tick <= ticksToOpen; tick++)
        {
            float rendered = DoorApertureMath.RenderedOpenFraction(
                trackingLeaves: true, (float)tick / ticksToOpen,
                DoorApertureMath.DefaultQuantisationSteps);

            // What our own renderer is doing at this step, asked of LeafSpans rather than of the
            // aperture, so the two sides of this comparison come from different functions. The gap
            // BETWEEN the leaves is the aperture the fan fires rays through -- not whether a leaf is
            // long enough to emit, which is a different question and the one the OLD rule answered:
            // leaves vanish only at a fully slid door, which is precisely the late handover being
            // fixed. A door at 0.125 still has two substantial leaves AND an eighth-cell gap.
            DoorApertureMath.LeafSpans(0f, rendered, out _, out float aEnd, out float bStart, out _);
            bool polygonDrawsAGap = bStart - aEnd > 0f;

            bool hole = DoorApertureMath.GlowGridHoleWanted(
                blocksLightWhenShut: true, headingOpen: true, rendered);

            Assert.That(hole, Is.EqualTo(polygonDrawsAGap),
                $"tick {tick}/{ticksToOpen} (stepped aperture {rendered:F3}): the polygon "
                + $"{(polygonDrawsAGap ? "drew a gap" : "drew a wall")} while the grid "
                + $"{(hole ? "was a hole" : "stayed blocked")}");
        }
    }

    // AND THE HANDOVER HAPPENS ONCE, AT THE START. Pinning WHERE it happens, not just that it
    // happens: a rule that opened the grid halfway through would satisfy "one write per swing" while
    // leaving half the animation in the old renderer.
    [Test]
    public void TheGridOpensOnTheFirstStepTheLeavesPart()
    {
        const int ticks = 45;
        int firstHoleTick = -1;

        for (int tick = 0; tick <= ticks; tick++)
        {
            float rendered = DoorApertureMath.RenderedOpenFraction(
                trackingLeaves: true, (float)tick / ticks,
                DoorApertureMath.DefaultQuantisationSteps);

            if (DoorApertureMath.GlowGridHoleWanted(true, headingOpen: true, rendered)
                && firstHoleTick < 0)
            {
                firstHoleTick = tick;
            }
        }

        // Eight steps round up from half a step, so the first non-zero aperture is reached at
        // 1/16 of the slide -- tick 3 of 45. Stated as the arithmetic rather than as "3" so a change
        // to DefaultQuantisationSteps moves the expectation with it instead of failing opaquely.
        int expected = (int)System.Math.Ceiling(
            ticks * (0.5f / DoorApertureMath.DefaultQuantisationSteps));

        Assert.That(firstHoleTick, Is.EqualTo(expected),
            "the grid must open on the first step our polygon parts the leaves");
        Assert.That(firstHoleTick, Is.LessThan(ticks / 4),
            "the handover has to be near the START of the swing, not partway through it");
    }
    // ---- and the bit is only written when it MOVED --------------------------------------------

    // THE FOUR STATES, AND ONLY TWO OF THEM ARE WRITES. The grid does not care -- both vanilla calls
    // are a plain Set -- but our own Patch_VectorLightBlockerAdded postfixes them into
    // MarkGeometryDirtyAround with `blockerMoved: true`, which discards the silhouette memo every
    // light near the door was reusing. So a write we did not need is a window rescan we did not need.
    [TestCase(true,  true,  true)]    // want a hole, currently blocked -> open it
    [TestCase(true,  false, false)]   // want a hole, already a hole    -> nothing to do
    [TestCase(false, true,  false)]   // want it blocked, already is    -> nothing to do
    [TestCase(false, false, true)]    // want it blocked, currently open -> restore it
    public void TheBlockerBitIsWrittenOnlyWhenItMoves(
        bool holeWanted, bool currentlyBlocked, bool expected)
    {
        Assert.That(
            DoorApertureMath.GlowGridWriteNeeded(
                holeWanted, blockerStateKnown: true, currentlyBlocked),
            Is.EqualTo(expected));
    }

    // AN UNREADABLE BIT WRITES ANYWAY, and the direction is chosen rather than defaulted. A RimWorld
    // rename or an uncreated array during map setup costs the rescan the skip was saving; guessing
    // the other way would leave a door lighting a room it does not open onto, which has no symptom
    // until somebody looks at the right wall.
    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void AnUnreadableBlockerBitIsAlwaysWritten(bool holeWanted, bool currentlyBlocked)
    {
        Assert.That(
            DoorApertureMath.GlowGridWriteNeeded(
                holeWanted, blockerStateKnown: false, currentlyBlocked),
            Is.True);
    }

    // WITH THE FEATURE OFF, NOTHING IS EVER WRITTEN -- which is the claim that is NOT about doors.
    // Every door answers `holeWanted` false with the flag down, and a door's cell starts blocked and
    // is only ever unblocked by us, so the skip is total: no writes, therefore no invalidations,
    // therefore the silhouette memo behaves exactly as it did before this feature existed. This
    // repo's rule that a flag turned off reproduces the pre-feature behaviour is usually a statement
    // about what is on screen; here it has to hold for the invalidation CADENCE too, and only a
    // performance scenario can see the difference.
    [Test]
    public void WithTheFeatureOffAShutMapNeverWritesTheGrid()
    {
        // A door that was never opened: blocked, and no hole wanted.
        Assert.That(
            DoorApertureMath.GlowGridWriteNeeded(
                holeWanted: false, blockerStateKnown: true, currentlyBlocked: true),
            Is.False);

        // And a full swing under the flag, tick by tick, asks for nothing at any point in it. The
        // fraction is swept because the flag is folded into `holeWanted` at the call site rather
        // than guarding it -- so this is the composed statement, not a restatement of the case above.
        for (int tick = 0; tick <= 45; tick++)
        {
            float rendered = DoorApertureMath.RenderedOpenFraction(
                trackingLeaves: true, tick / 45f, DoorApertureMath.DefaultQuantisationSteps);

            // `false &&` is what CelestialLightingFeatures.VectorLightDoorGlowBlocker contributes
            // when it is off, spelled out rather than referenced so this file stays Verse-free.
            bool holeWanted = false && DoorApertureMath.GlowGridHoleWanted(
                blocksLightWhenShut: true, headingOpen: true, rendered);

            Assert.That(
                DoorApertureMath.GlowGridWriteNeeded(holeWanted, true, currentlyBlocked: true),
                Is.False, $"tick {tick} wrote the grid with the feature off");
        }
    }

    // AND WITH IT ON, A SWING WRITES EXACTLY ONCE. Not zero -- the hole has to open -- and not the
    // four the unconditional reconcile cost. Counted over a whole swing rather than asserted at one
    // tick, because the count is the quantity the memo's measurement is sensitive to.
    [Test]
    public void AnOpenSwingWritesTheGridExactlyOnce()
    {
        bool blocked = true;
        int writes = 0;

        for (int tick = 0; tick <= 45; tick++)
        {
            float rendered = DoorApertureMath.RenderedOpenFraction(
                trackingLeaves: true, tick / 45f, DoorApertureMath.DefaultQuantisationSteps);
            bool holeWanted = DoorApertureMath.GlowGridHoleWanted(
                blocksLightWhenShut: true, headingOpen: true, rendered);

            if (DoorApertureMath.GlowGridWriteNeeded(holeWanted, true, blocked))
            {
                writes++;
                blocked = !holeWanted;
            }
        }

        Assert.That(writes, Is.EqualTo(1));
        Assert.That(blocked, Is.False, "the swing has to leave the cell a hole");
    }
}
