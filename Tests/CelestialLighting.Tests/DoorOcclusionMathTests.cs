using CelestialLighting;
using NUnit.Framework;

namespace CelestialLighting.Tests;

// §27e: the occlusion rule VectorLightBlockers asks per cell. Four booleans and one fraction in, one
// bool out, so the boolean half is still exhaustively testable — and it is worth exhausting, because
// this is the first time §27's blocker rule stops being a restatement of vanilla's own blockLight
// test.
//
// The fraction arrived with issue #174 phase 1 (a door closing tracks its leaves). Every case that
// predates it passes `doorAperture: 0f`, which is what VectorLightBlockers passes whenever the
// aperture flag is off — so the original table is not merely still true, it is still the SAME
// expression being tested.
[TestFixture]
public class DoorOcclusionMathTests
{
    // THE CONTRACT THAT MATTERS MOST: with the feature off, the answer is exactly `blocksLight` for
    // every combination of the other two inputs. Not "approximately the old behaviour" — the same
    // value, so a baseline arm in the harness renders the real pre-feature frame rather than a
    // picture of the feature being absent. All eight off-cases, spelled out rather than looped, so a
    // failure names the case.
    [TestCase(true,  false, false, ExpectedResult = true)]
    [TestCase(true,  false, true,  ExpectedResult = true)]
    [TestCase(true,  true,  false, ExpectedResult = true)]
    [TestCase(true,  true,  true,  ExpectedResult = true)]
    [TestCase(false, false, false, ExpectedResult = false)]
    [TestCase(false, false, true,  ExpectedResult = false)]
    [TestCase(false, true,  false, ExpectedResult = false)]
    [TestCase(false, true,  true,  ExpectedResult = false)]
    public bool FlagOffReproducesTheBareBlockLightTest(bool blocksLight, bool isDoor, bool doorOpen) =>
        DoorOcclusionMath.Occludes(
            blocksLight, isDoor, doorOpen, openDoorsPassLight: false, doorAperture: 0f,
            apertureTracked: true);

    // The feature itself: with the flag on, an open door is the ONLY case that changes.
    [TestCase(true,  true,  true,  ExpectedResult = false)] // open door -- the whole point
    [TestCase(true,  true,  false, ExpectedResult = true)]  // shut door still occludes
    [TestCase(true,  false, false, ExpectedResult = true)]  // wall unaffected
    [TestCase(true,  false, true,  ExpectedResult = true)]  // a wall is never "open"
    public bool FlagOnChangesOnlyTheOpenDoor(bool blocksLight, bool isDoor, bool doorOpen) =>
        DoorOcclusionMath.Occludes(
            blocksLight, isDoor, doorOpen, openDoorsPassLight: true, doorAperture: 0f,
            apertureTracked: false);

    // A see-through door is transparent whether it is open or shut, and the flag must not disturb
    // that. This is what vector_light_glass_door pins live: a blockLight=false door reproduces a bare
    // doorway exactly. There is no state past "does not occlude", so opening one cannot make it more
    // transparent -- and, more usefully, turning the open-door feature ON must not accidentally make
    // a SHUT glass door start occluding by routing it through the door branch.
    [TestCase(true,  ExpectedResult = false)]
    [TestCase(false, ExpectedResult = false)]
    public bool TransparentDoorPassesLightRegardlessOfOpenState(bool doorOpen) =>
        DoorOcclusionMath.Occludes(
            blocksLight: false, isDoor: true, doorOpen: doorOpen, openDoorsPassLight: true,
            doorAperture: 0f, apertureTracked: true);

    // Transparency wins over everything, flag included -- the blockLight test is deliberately first.
    [Test]
    public void TransparencyIsIndependentOfTheFeatureFlag()
    {
        foreach (bool flag in new[] { false, true })
        {
            foreach (bool isDoor in new[] { false, true })
            {
                foreach (bool open in new[] { false, true })
                {
                    Assert.That(
                        DoorOcclusionMath.Occludes(false, isDoor, open, flag, 0f, true), Is.False,
                        $"blockLight=false must never occlude (isDoor={isDoor}, open={open}, flag={flag})");
                }
            }
        }
    }

    // The feature can only ever REMOVE occlusion, never add it. Stated as a property over the whole
    // input space rather than as another case list, because the failure it guards against is a future
    // edit reordering the branches: any rearrangement that makes some input occlude with the flag on
    // but not off would turn a rendering feature into a source of new shadows, which is the one thing
    // an opt-in visual flag must not do.
    [Test]
    public void TurningTheFeatureOnNeverIntroducesOcclusion()
    {
        foreach (bool blocks in new[] { false, true })
        {
            foreach (bool isDoor in new[] { false, true })
            {
                foreach (bool open in new[] { false, true })
                {
                    foreach (float aperture in Apertures)
                    {
                        bool off = DoorOcclusionMath.Occludes(
                            blocks, isDoor, open, false, aperture, apertureTracked: true);
                        bool on = DoorOcclusionMath.Occludes(
                            blocks, isDoor, open, true, aperture, apertureTracked: true);
                        Assert.That(on && !off, Is.False,
                            $"flag on occludes where off did not (blocks={blocks}, isDoor={isDoor}, "
                            + $"open={open}, aperture={aperture})");
                    }
                }
            }
        }
    }
    // Every aperture a quantised swing can produce, plus the two ends and the two ways a modded
    // OpenPct can be wrong. Shared by the property tests below.
    private static readonly float[] Apertures =
        { 0f, 0.125f, 0.25f, 0.375f, 0.5f, 0.625f, 0.75f, 0.875f, 1f };

    // ISSUE #174 PHASE 1, THE WHOLE OF IT. A door told to shut has `Open` false from the first tick of
    // a slide that lasts tens of ticks, so these are the cases that were wrong: doorOpen=false with a
    // gap still drawn in the cell. Occluding here is what snapped the beam shut in one frame.
    [TestCase(0f,     ExpectedResult = true)]  // leaves met -- an ordinary blocker again
    [TestCase(0.125f, ExpectedResult = false)] // one step from shut, and still a gap
    [TestCase(0.5f,   ExpectedResult = false)]
    [TestCase(0.875f, ExpectedResult = false)]
    [TestCase(1f,     ExpectedResult = false)]
    public bool ClosingDoorStopsOccludingUntilItsLeavesMeet(float doorAperture) =>
        DoorOcclusionMath.Occludes(
            blocksLight: true, isDoor: true, doorOpen: false, openDoorsPassLight: true,
            doorAperture: doorAperture, apertureTracked: true);

    // The other half of phase 1's claim, NOW SCOPED TO THE UNTRACKED RULE. This test used to assert
    // that an open door reads the same for every aperture, and its comment said that if a later edit
    // made the aperture replace `doorOpen` rather than join it, this is what would say so. Issue #174
    // phase 2 is that edit, and it said so: the tripwire fired.
    //
    // It is re-derived rather than deleted, because the claim is still exactly true where it was
    // meant to apply -- with the aperture NOT tracked, `doorOpen` is the whole rule and no fraction
    // can disturb it. That is the flag-off arm's contract, and it is the part that had to survive.
    [TestCase(0f)]
    [TestCase(0.125f)]
    [TestCase(1f)]
    public void UntrackedOpenDoorIsUnaffectedByTheAperture(float doorAperture)
    {
        Assert.That(
            DoorOcclusionMath.Occludes(
                blocksLight: true, isDoor: true, doorOpen: true, openDoorsPassLight: true,
                doorAperture: doorAperture, apertureTracked: false),
            Is.False);
    }

    // ISSUE #174 PHASE 2, THE WHOLE OF IT: the tracked aperture outranks `Open`.
    //
    // A door is told to open on one tick and its leaves do not move until the next -- `Open` is true
    // while OpenPct is still 0, and vanilla draws the leaves shut throughout. OR-ing the two terms
    // made that cell a bare, FULL-WIDTH doorway for a tick or two, so the room caught a full-brightness
    // beam that then collapsed to one-eighth width. Measured offline as a gap of 1.000 on the tick the
    // door is told to open, against 0.125 once the leaves actually move.
    //
    // A door drawn shut occludes, whatever its state flag says.
    [Test]
    public void TrackedApertureOutranksTheOpenFlag()
    {
        Assert.That(
            DoorOcclusionMath.Occludes(
                blocksLight: true, isDoor: true, doorOpen: true, openDoorsPassLight: true,
                doorAperture: 0f, apertureTracked: true),
            Is.True,
            "a door drawn shut must occlude on the tick it is told to open");

        // ...and the moment the leaves part, it stops. The two ends are unchanged from phase 1.
        foreach (float ajar in new[] { 0.125f, 0.5f, 1f })
        {
            Assert.That(
                DoorOcclusionMath.Occludes(
                    blocksLight: true, isDoor: true, doorOpen: true, openDoorsPassLight: true,
                    doorAperture: ajar, apertureTracked: true),
                Is.False,
                $"a door {ajar} ajar must pass light");
        }
    }

    // A wall does not acquire a gap because a caller passed one. The aperture is gated behind the same
    // isDoor branch the open state is, for the same reason: passing a non-door an open state is a
    // caller bug, not a request to delete a wall.
    [TestCase(0.5f)]
    [TestCase(1f)]
    public void ApertureCannotUnblockAWall(float doorAperture)
    {
        Assert.That(
            DoorOcclusionMath.Occludes(
                blocksLight: true, isDoor: false, doorOpen: false, openDoorsPassLight: true,
                doorAperture: doorAperture, apertureTracked: true),
            Is.True);
    }

    // With the feature off, the aperture is inert for every input -- the flag-off arm has to render
    // the pre-feature frame, and a fraction leaking past the flag would make a shut door pass light in
    // an arm whose whole job is to prove it does not.
    [Test]
    public void ApertureIsInertWithTheFeatureOff()
    {
        foreach (bool blocks in new[] { false, true })
        {
            foreach (bool isDoor in new[] { false, true })
            {
                foreach (bool open in new[] { false, true })
                {
                    foreach (float aperture in Apertures)
                    {
                        Assert.That(
                            DoorOcclusionMath.Occludes(
                                blocks, isDoor, open, false, aperture, apertureTracked: true),
                            Is.EqualTo(blocks),
                            $"flag off must be exactly blocksLight (isDoor={isDoor}, open={open}, "
                            + $"aperture={aperture})");
                    }
                }
            }
        }
    }

    // Occlusion is MONOTONE in the aperture: as the gap widens the cell can stop occluding, never
    // start. Stated as a property because the bug this replaces was a direction rather than a value --
    // a beam that got narrower as the door opened, or re-blocked partway through a close, would be
    // this test failing, and no single case list would catch a reordering that produced it.
    [Test]
    public void WideningTheApertureNeverAddsOcclusion()
    {
        foreach (bool blocks in new[] { false, true })
        {
            foreach (bool isDoor in new[] { false, true })
            {
                foreach (bool open in new[] { false, true })
                {
                    for (int i = 1; i < Apertures.Length; i++)
                    {
                        bool narrower = DoorOcclusionMath.Occludes(
                            blocks, isDoor, open, true, Apertures[i - 1], apertureTracked: true);
                        bool wider = DoorOcclusionMath.Occludes(
                            blocks, isDoor, open, true, Apertures[i], apertureTracked: true);
                        Assert.That(wider && !narrower, Is.False,
                            $"aperture {Apertures[i]} occludes where {Apertures[i - 1]} did not "
                            + $"(blocks={blocks}, isDoor={isDoor}, open={open})");
                    }
                }
            }
        }
    }

    // A nonsense aperture must fail SHUT, not open. `OpenPct` is protected virtual and TicksToOpenNow
    // can in principle be zero, so NaN is reachable from a modded door; a rule written as
    // `aperture <= 0f` would compare false against NaN and quietly unblock a closed door on someone's
    // base. DoorAccess.OpenFraction clamps upstream as well -- both, because this is a wall.
    [TestCase(float.NaN)]
    [TestCase(-1f)]
    public void NonsenseApertureLeavesTheDoorOccluding(float doorAperture)
    {
        Assert.That(
            DoorOcclusionMath.Occludes(
                blocksLight: true, isDoor: true, doorOpen: false, openDoorsPassLight: true,
                doorAperture: doorAperture, apertureTracked: true),
            Is.True);
    }
}
