using CelestialLighting;

namespace CelestialLighting.Tests;

// Offline unit tests for §15's pure core. The whole subsystem hinges on one three-input predicate,
// so these are mostly a truth table — but the truth table is exactly the thing that would be wrong
// if a porch went black or a sealed room started casting a second shadow, and neither of those is
// visible from a Mono.Cecil API-existence check.
[TestFixture]
public class EavesMathTests
{
    // --- IsEave / IsEnclosed: the full truth table over (roofed, hasRoom, usesOutdoorTemperature) ---

    // An unroofed cell is neither, whatever room it belongs to — open ground inside a colony's
    // "outdoor room" must not start casting a roofline shadow.
    [TestCase(false, false, false)]
    [TestCase(false, false, true)]
    [TestCase(false, true, false)]
    [TestCase(false, true, true)]
    public void UnroofedCellIsNeitherEaveNorEnclosed(bool roofed, bool hasRoom, bool outdoorTemp)
    {
        Assert.That(EavesMath.IsEave(roofed, hasRoom, outdoorTemp), Is.False);
        Assert.That(EavesMath.IsEnclosed(roofed, hasRoom, outdoorTemp), Is.False);
    }

    // The case the subsystem exists for: roofed, in a room, and that room breathes outdoor air.
    [Test]
    public void RoofedOutdoorTemperatureRoomIsAnEave()
    {
        Assert.That(EavesMath.IsEave(roofed: true, hasRoom: true, usesOutdoorTemperature: true), Is.True);
        Assert.That(EavesMath.IsEnclosed(roofed: true, hasRoom: true, usesOutdoorTemperature: true), Is.False);
    }

    // A sealed room: roofed and temperature-holding. This is what §7b is allowed to black out.
    [Test]
    public void RoofedTemperatureHoldingRoomIsEnclosed()
    {
        Assert.That(EavesMath.IsEnclosed(roofed: true, hasRoom: true, usesOutdoorTemperature: false), Is.True);
        Assert.That(EavesMath.IsEave(roofed: true, hasRoom: true, usesOutdoorTemperature: false), Is.False);
    }

    // The deliberately asymmetric case (see EavesMath's comment): a roofed cell RimWorld hands back
    // no Room for adds no shadow, but still counts as covered for occlusion — each caller gets the
    // conservative answer for its own direction rather than one shared guess.
    [TestCase(false)]
    [TestCase(true)]
    public void RoofedCellWithNoRoomIsEnclosedButNotAnEave(bool outdoorTemp)
    {
        Assert.That(EavesMath.IsEave(roofed: true, hasRoom: false, usesOutdoorTemperature: outdoorTemp), Is.False);
        Assert.That(EavesMath.IsEnclosed(roofed: true, hasRoom: false, usesOutdoorTemperature: outdoorTemp), Is.True);
    }

    // Within roofed cells the two predicates must partition exactly — no cell may be both, and none
    // may be neither. A gap here would mean a cell that neither casts nor occludes (or does both).
    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void EaveAndEnclosedPartitionRoofedCells(bool hasRoom, bool outdoorTemp)
    {
        bool eave = EavesMath.IsEave(roofed: true, hasRoom: hasRoom, usesOutdoorTemperature: outdoorTemp);
        bool enclosed = EavesMath.IsEnclosed(roofed: true, hasRoom: hasRoom, usesOutdoorTemperature: outdoorTemp);
        Assert.That(eave ^ enclosed, Is.True, "exactly one of IsEave/IsEnclosed must hold for a roofed cell");
    }

    // --- CasterHeight ---

    // Empty porch floor: nothing standing there, so the roofline is the only caster, at wall height.
    [Test]
    public void EmptyEaveCellCastsAtRoofHeight()
    {
        float height = EavesMath.CasterHeight(0f, roofed: true, hasRoom: true, usesOutdoorTemperature: true);
        Assert.That(height, Is.EqualTo(EavesMath.RoofCasterHeight));
    }

    // Vanilla's Wall and Door both declare exactly 1.0, so a roofed wall is unchanged — this is what
    // keeps a normal building's outline identical with the feature on.
    [Test]
    public void WallHeightCasterUnderARoofIsUnchanged()
    {
        float height = EavesMath.CasterHeight(1f, roofed: true, hasRoom: true, usesOutdoorTemperature: true);
        Assert.That(height, Is.EqualTo(1f));
    }

    // The divergence from Perspective: Eaves. It substitutes a fixed 1.0 dummy into any cell whose
    // edifice is not exactly 1.0, which shortens a taller modded caster back to wall height wherever
    // a roof covers it; taking the max means roofing something can only ever add shadow.
    [Test]
    public void TallerThanWallCasterIsNotShortenedByARoof()
    {
        float height = EavesMath.CasterHeight(2.5f, roofed: true, hasRoom: true, usesOutdoorTemperature: true);
        Assert.That(height, Is.EqualTo(2.5f));
    }

    // A knee-high object on a porch is raised to the roofline, not left at its own height: the roof
    // above it is what is casting, and it happens to be the taller of the two.
    [Test]
    public void ShortCasterOnAPorchIsRaisedToRoofHeight()
    {
        float height = EavesMath.CasterHeight(0.2f, roofed: true, hasRoom: true, usesOutdoorTemperature: true);
        Assert.That(height, Is.EqualTo(EavesMath.RoofCasterHeight));
    }

    // Every non-eave case passes the edifice height straight through untouched — this is the
    // property that makes the feature-off path in EaveShadowGrid provably identical to vanilla's
    // lookup, including the 0 that stands in for "no building here".
    [TestCase(0f, false, true, true)]
    [TestCase(0.35f, false, true, true)]
    [TestCase(1f, true, true, false)]
    [TestCase(0f, true, true, false)]
    [TestCase(0.5f, true, false, true)]
    public void NonEaveCellsPassTheEdificeHeightThrough(
        float edificeHeight, bool roofed, bool hasRoom, bool outdoorTemp)
    {
        float height = EavesMath.CasterHeight(edificeHeight, roofed, hasRoom, outdoorTemp);
        Assert.That(height, Is.EqualTo(edificeHeight));
    }
}
