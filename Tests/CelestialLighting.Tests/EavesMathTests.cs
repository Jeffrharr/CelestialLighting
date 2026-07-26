namespace CelestialLighting.Tests;

// Offline unit tests for §15's pure core. The whole subsystem hinges on one four-input predicate,
// so these are mostly a truth table — but the truth table is exactly the thing that would be wrong
// if a porch went black, a cave stopped being occluded, or a sealed room started casting a second
// shadow, and none of those is visible from a Mono.Cecil API-existence check.
[TestFixture]
public class EavesMathTests
{
    // --- IsEave / IsEnclosed: the truth table over (roofed, thickRoof, hasRoom, usesOutdoorTemp) ---

    // An unroofed cell is neither, whatever room it belongs to — open ground inside a colony's
    // "outdoor room" must not start casting a roofline shadow.
    [TestCase(false, false, false)]
    [TestCase(false, false, true)]
    [TestCase(false, true, false)]
    [TestCase(false, true, true)]
    public void UnroofedCellIsNeitherEaveNorEnclosed(bool thickRoof, bool hasRoom, bool outdoorTemp)
    {
        Assert.That(EavesMath.IsEave(false, thickRoof, hasRoom, outdoorTemp), Is.False);
        Assert.That(EavesMath.IsEnclosed(false, thickRoof, hasRoom, outdoorTemp), Is.False);
    }

    // The case the subsystem exists for: roofed, in a room, and that room breathes outdoor air.
    [Test]
    public void RoofedOutdoorTemperatureRoomIsAnEave()
    {
        Assert.That(EavesMath.IsEave(true, thickRoof: false, hasRoom: true, usesOutdoorTemperature: true), Is.True);
        Assert.That(EavesMath.IsEnclosed(true, thickRoof: false, hasRoom: true, usesOutdoorTemperature: true), Is.False);
    }

    // A sealed room: roofed and temperature-holding. This is what §7b is allowed to black out.
    [Test]
    public void RoofedTemperatureHoldingRoomIsEnclosed()
    {
        Assert.That(EavesMath.IsEnclosed(true, thickRoof: false, hasRoom: true, usesOutdoorTemperature: false), Is.True);
        Assert.That(EavesMath.IsEave(true, thickRoof: false, hasRoom: true, usesOutdoorTemperature: false), Is.False);
    }

    // The regression this veto exists to prevent, and it is the common case rather than a corner one:
    // UsesOutdoorTemperature is true for any room that touches the map edge, which a cave system
    // reaching the outside does. Without the thick-roof veto every cell of that cave would classify
    // as an eave — un-occluded by §7b (a cavern lit at 61% of sky, the exact bug §7b exists to fix)
    // and casting a roofline shadow besides.
    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void ThickRoofIsNeverAnEave(bool hasRoom, bool outdoorTemp)
    {
        Assert.That(EavesMath.IsEave(true, thickRoof: true, hasRoom: hasRoom, usesOutdoorTemperature: outdoorTemp),
            Is.False);
        Assert.That(EavesMath.IsEnclosed(true, thickRoof: true, hasRoom: hasRoom, usesOutdoorTemperature: outdoorTemp),
            Is.True, "a mountain buries whatever is under it");
    }

    // The deliberately asymmetric case (see EavesMath's comment): a roofed cell RimWorld hands back
    // no Room for adds no shadow, but still counts as covered for occlusion — each caller gets the
    // conservative answer for its own direction rather than one shared guess.
    [TestCase(false)]
    [TestCase(true)]
    public void RoofedCellWithNoRoomIsEnclosedButNotAnEave(bool outdoorTemp)
    {
        Assert.That(EavesMath.IsEave(true, thickRoof: false, hasRoom: false, usesOutdoorTemperature: outdoorTemp),
            Is.False);
        Assert.That(EavesMath.IsEnclosed(true, thickRoof: false, hasRoom: false, usesOutdoorTemperature: outdoorTemp),
            Is.True);
    }

    // Within roofed cells the two predicates must partition exactly — no cell may be both, and none
    // may be neither. A gap here would mean a cell that neither casts nor occludes (or does both).
    [TestCase(true, true, true)]
    [TestCase(true, true, false)]
    [TestCase(true, false, true)]
    [TestCase(true, false, false)]
    [TestCase(false, true, true)]
    [TestCase(false, true, false)]
    [TestCase(false, false, true)]
    [TestCase(false, false, false)]
    public void EaveAndEnclosedPartitionRoofedCells(bool thickRoof, bool hasRoom, bool outdoorTemp)
    {
        bool eave = EavesMath.IsEave(true, thickRoof, hasRoom, outdoorTemp);
        bool enclosed = EavesMath.IsEnclosed(true, thickRoof, hasRoom, outdoorTemp);
        Assert.That(eave ^ enclosed, Is.True, "exactly one of IsEave/IsEnclosed must hold for a roofed cell");
    }

    // --- CasterHeight ---

    // Empty porch floor: nothing standing there, so the roofline is the only caster, at wall height.
    [Test]
    public void EmptyEaveCellCastsAtRoofHeight()
    {
        Assert.That(EavesMath.CasterHeight(0f, isEave: true), Is.EqualTo(EavesMath.RoofCasterHeight));
    }

    // Vanilla's Wall and Door both declare exactly 1.0, so a roofed wall is unchanged — this is what
    // keeps a normal building's outline identical with the feature on.
    [Test]
    public void WallHeightCasterUnderARoofIsUnchanged()
    {
        Assert.That(EavesMath.CasterHeight(1f, isEave: true), Is.EqualTo(1f));
    }

    // The divergence from Perspective: Eaves. It substitutes a fixed 1.0 dummy into any cell whose
    // edifice is not exactly 1.0, which shortens a taller modded caster back to wall height wherever
    // a roof covers it; taking the max means roofing something can only ever add shadow.
    [Test]
    public void TallerThanWallCasterIsNotShortenedByARoof()
    {
        Assert.That(EavesMath.CasterHeight(2.5f, isEave: true), Is.EqualTo(2.5f));
    }

    // A knee-high object on a porch is raised to the roofline, not left at its own height: the roof
    // above it is what is casting, and it happens to be the taller of the two.
    [Test]
    public void ShortCasterOnAPorchIsRaisedToRoofHeight()
    {
        Assert.That(EavesMath.CasterHeight(0.2f, isEave: true), Is.EqualTo(EavesMath.RoofCasterHeight));
    }

    // Every non-eave cell passes the edifice height straight through untouched — this is the property
    // that makes the feature-off path in EaveShadowGrid provably identical to vanilla's lookup,
    // including the 0 that stands in for "no building here".
    [TestCase(0f)]
    [TestCase(0.35f)]
    [TestCase(1f)]
    [TestCase(2.5f)]
    public void NonEaveCellsPassTheEdificeHeightThrough(float edificeHeight)
    {
        Assert.That(EavesMath.CasterHeight(edificeHeight, isEave: false), Is.EqualTo(edificeHeight));
    }
}
