namespace CelestialLighting.Tests;

// Offline unit tests for §7d's pure core (Source/DoorLeakMath.cs, linked so this runs against the
// exact shipped file). Reference values are decompiled ThingDef.BaseMaxHitPoints (stuff-free — see
// DoorLeakMath's own header for why): vanilla Door/Autodoor 160 (DoorBase's own statBases override,
// not the 100 StatDef default), OrnateDoor 250, Anomaly SecurityDoor 800, Odyssey AncientBlastDoor
// 6000, Odyssey VacBarrier 100.
[TestFixture]
public class DoorLeakMathTests
{
    private const float WoodDoor = 160f;
    private const float Tolerance = 1e-4f;

    [Test]
    public void CrossingMultiplier_WoodDoorAgainstItself_IsExactlyOne()
    {
        // "Our default should be wooden doors" — an all-wood-door playthrough must see the exact
        // pre-§7d multiplier of 1 (no dimming), not a fractional discount for reading its own
        // reference value.
        Assert.That(DoorLeakMath.CrossingMultiplier(WoodDoor, WoodDoor, DoorLeakMath.DefaultSensitivity),
            Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void CrossingMultiplier_WeakerThanReference_StaysAtOne_NeverBrightens()
    {
        // Odyssey's VacBarrier (100) reads WEAKER than a wood Door under BaseMaxHitPoints — this knob
        // only ever dims the flood, so a door it doesn't recognise as "stronger" leaves it at the
        // ordinary 1, not a bonus brightening for being flimsy.
        Assert.That(DoorLeakMath.CrossingMultiplier(100f, WoodDoor, DoorLeakMath.DefaultSensitivity),
            Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void CrossingMultiplier_OrnateDoor_DimsMildly()
    {
        // 250 / 160 = 1.5625, extraRatio 0.5625, exp(-0.5 * 0.5625) ~= 0.7548.
        Assert.That(DoorLeakMath.CrossingMultiplier(250f, WoodDoor, DoorLeakMath.DefaultSensitivity),
            Is.EqualTo(0.75485f).Within(1e-3f));
    }

    [Test]
    public void CrossingMultiplier_SecurityDoor_LeaksAlmostNothing()
    {
        // 800 / 160 = 5.0, extraRatio 4.0, exp(-0.5 * 4.0) ~= 0.1353.
        Assert.That(DoorLeakMath.CrossingMultiplier(800f, WoodDoor, DoorLeakMath.DefaultSensitivity),
            Is.EqualTo(0.13534f).Within(1e-3f));
    }

    [Test]
    public void CrossingMultiplier_AncientBlastDoor_DecaysToEffectivelyZero()
    {
        // 6000 / 160 = 37.5, extraRatio 36.5 -- exp(-0.5 * 36.5) underflows to (effectively) zero
        // rather than throwing or producing NaN/Infinity.
        Assert.That(DoorLeakMath.CrossingMultiplier(6000f, WoodDoor, DoorLeakMath.DefaultSensitivity),
            Is.EqualTo(0f).Within(1e-6f));
    }

    [Test]
    public void CrossingMultiplier_ZeroSensitivity_ReproducesFlatPreFeatureMultiplier()
    {
        // The slider's own floor: even Anomaly's SecurityDoor leaves the flood at the ordinary 1 when
        // the player has turned door-strength weighting off entirely.
        Assert.That(DoorLeakMath.CrossingMultiplier(800f, WoodDoor, sensitivity: 0f),
            Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void CrossingMultiplier_NoReferenceAvailable_ReproducesFlatPreFeatureMultiplier()
    {
        // A non-positive reference means "couldn't resolve ThingDefOf.Door.BaseMaxHitPoints" — degrade
        // to the pre-feature flat multiplier of 1 rather than dividing by zero.
        Assert.That(DoorLeakMath.CrossingMultiplier(800f, 0f, DoorLeakMath.DefaultSensitivity),
            Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void CrossingMultiplier_TwoDoorsInSeries_CompoundMultiplicatively()
    {
        // Nested airlocks: NativeSkyFalloffGrid propagates strength forward as a running product, so
        // this pure core only needs to guarantee a single crossing's multiplier composes by
        // multiplication -- two OrnateDoor crossings in a row should read as that multiplier squared.
        float single = DoorLeakMath.CrossingMultiplier(250f, WoodDoor, DoorLeakMath.DefaultSensitivity);
        float twice = single * single;
        Assert.That(twice, Is.EqualTo(0.56980f).Within(1e-3f));
    }
}
