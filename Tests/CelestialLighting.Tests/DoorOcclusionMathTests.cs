using CelestialLighting;
using NUnit.Framework;

namespace CelestialLighting.Tests;

// §27e: the occlusion rule VectorLightBlockers asks per cell. Four booleans in, one out, so the
// whole function is exhaustively testable — and it is worth exhausting, because this is the first
// time §27's blocker rule stops being a restatement of vanilla's own blockLight test.
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
        DoorOcclusionMath.Occludes(blocksLight, isDoor, doorOpen, openDoorsPassLight: false);

    // The feature itself: with the flag on, an open door is the ONLY case that changes.
    [TestCase(true,  true,  true,  ExpectedResult = false)] // open door -- the whole point
    [TestCase(true,  true,  false, ExpectedResult = true)]  // shut door still occludes
    [TestCase(true,  false, false, ExpectedResult = true)]  // wall unaffected
    [TestCase(true,  false, true,  ExpectedResult = true)]  // a wall is never "open"
    public bool FlagOnChangesOnlyTheOpenDoor(bool blocksLight, bool isDoor, bool doorOpen) =>
        DoorOcclusionMath.Occludes(blocksLight, isDoor, doorOpen, openDoorsPassLight: true);

    // A see-through door is transparent whether it is open or shut, and the flag must not disturb
    // that. This is what vector_light_glass_door pins live: a blockLight=false door reproduces a bare
    // doorway exactly. There is no state past "does not occlude", so opening one cannot make it more
    // transparent -- and, more usefully, turning the open-door feature ON must not accidentally make
    // a SHUT glass door start occluding by routing it through the door branch.
    [TestCase(true,  ExpectedResult = false)]
    [TestCase(false, ExpectedResult = false)]
    public bool TransparentDoorPassesLightRegardlessOfOpenState(bool doorOpen) =>
        DoorOcclusionMath.Occludes(
            blocksLight: false, isDoor: true, doorOpen: doorOpen, openDoorsPassLight: true);

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
                        DoorOcclusionMath.Occludes(false, isDoor, open, flag), Is.False,
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
                    bool off = DoorOcclusionMath.Occludes(blocks, isDoor, open, false);
                    bool on = DoorOcclusionMath.Occludes(blocks, isDoor, open, true);
                    Assert.That(on && !off, Is.False,
                        $"flag on occludes where off did not (blocks={blocks}, isDoor={isDoor}, open={open})");
                }
            }
        }
    }
}
