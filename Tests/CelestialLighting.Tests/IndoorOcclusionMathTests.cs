namespace CelestialLighting.Tests;

// Offline unit tests for §7b's pure core (Source/IndoorOcclusionMath.cs, linked into this project so
// these run against the exact shipped file). These cover the formulas; ApiCompatibilityTests covers
// that the vanilla members the adapter touches still exist.
[TestFixture]
public class IndoorOcclusionMathTests
{
    private const float Tolerance = 1e-5f;

    // --- CellOcclusion ---

    [Test]
    public void CellOcclusion_UnroofedCell_IsUntouched()
    {
        // The sky genuinely is overhead, so vanilla's own cover stands — this is what keeps the feature
        // from darkening the outdoors at all.
        Assert.That(IndoorOcclusionMath.CellOcclusion(roofed: false, isDoor: false, doorSkyLeak: 0.15f),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void CellOcclusion_UnroofedDoor_IsStillUntouched()
    {
        // An unroofed door (a gate in a wall with no roof over it) must not become *more* occluded than
        // the open ground it sits in.
        Assert.That(IndoorOcclusionMath.CellOcclusion(roofed: false, isDoor: true, doorSkyLeak: 0.15f),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void CellOcclusion_RoofedSolidCell_IsFullyOccluded()
    {
        // The headline case: a sealed cave tile takes no sky at all, so it renders from its artificial
        // glow alone (black when unlit) instead of vanilla's ~61%-of-sky.
        Assert.That(IndoorOcclusionMath.CellOcclusion(roofed: true, isDoor: false, doorSkyLeak: 0.15f),
            Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void CellOcclusion_RoofedDoor_LeaksExactlyTheConfiguredFraction()
    {
        Assert.That(IndoorOcclusionMath.CellOcclusion(roofed: true, isDoor: true, doorSkyLeak: 0.15f),
            Is.EqualTo(0.85f).Within(Tolerance));
    }

    [TestCase(-0.5f, 1f)]  // a negative leak cannot make a door MORE than fully occluded
    [TestCase(0f, 1f)]     // leak 0 == a door occludes exactly like solid roof
    [TestCase(1f, 0f)]     // leak 1 == a doorway is as open as the sky
    [TestCase(2f, 0f)]     // and an over-1 leak clamps rather than inverting
    public void CellOcclusion_ClampsDoorLeak(float doorSkyLeak, float expected)
    {
        Assert.That(IndoorOcclusionMath.CellOcclusion(roofed: true, isDoor: true, doorSkyLeak),
            Is.EqualTo(expected).Within(Tolerance));
    }

    // --- CornerOcclusion ---

    [Test]
    public void CornerOcclusion_DeepInsideBuilding_IsFull()
    {
        // All four cells touching the corner are roofed -> no sky reaches it.
        Assert.That(IndoorOcclusionMath.CornerOcclusion(occlusionSum: 4f, validCells: 4),
            Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void CornerOcclusion_OnExteriorWall_IsHalf()
    {
        // Two roofed, two open: the value that makes the interior blackness fade out across the wall
        // line instead of printing a hard black edge on the ground outside.
        Assert.That(IndoorOcclusionMath.CornerOcclusion(occlusionSum: 2f, validCells: 4),
            Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void CornerOcclusion_OnMapEdge_AveragesOnlyRealCells()
    {
        // A corner on the map boundary touches fewer cells; dividing by the real count (not a hard 4)
        // is also what makes the two sections sharing a boundary vertex agree, so no seam appears.
        Assert.That(IndoorOcclusionMath.CornerOcclusion(occlusionSum: 2f, validCells: 2),
            Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void CornerOcclusion_NoValidCells_IsZero()
    {
        // Degenerate guard: never divide by zero on a clipped rect.
        Assert.That(IndoorOcclusionMath.CornerOcclusion(occlusionSum: 0f, validCells: 0),
            Is.EqualTo(0f).Within(Tolerance));
    }

    // --- CapOcclusion (the accessibility escape hatch) ---

    [Test]
    public void CapOcclusion_FloorOff_IsIdentity()
    {
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, brightnessFloor: 0f), Is.EqualTo(1f).Within(Tolerance));
        Assert.That(IndoorOcclusionMath.CapOcclusion(0.85f, brightnessFloor: 0f), Is.EqualTo(0.85f).Within(Tolerance));
    }

    [Test]
    public void CapOcclusion_LeavesExactlyTheFloorWorthOfSky()
    {
        // With the accessibility floor at 0.15, a sealed cave keeps 15% of the sky — the thing that makes
        // the in-game "toggle minimum brightness" hotkey work indoors, where lifting CurSkyGlow cannot.
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, brightnessFloor: 0.15f),
            Is.EqualTo(0.85f).Within(Tolerance));
    }

    [Test]
    public void CapOcclusion_NeverRaisesOcclusion()
    {
        // A door already leaking more than the floor asks for is left alone — the cap is a ceiling only.
        Assert.That(IndoorOcclusionMath.CapOcclusion(0.5f, brightnessFloor: 0.15f),
            Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void CapOcclusion_FullFloor_DisablesOcclusionEntirely()
    {
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, brightnessFloor: 1f), Is.EqualTo(0f).Within(Tolerance));
    }

    // --- CoverAlpha ---

    [Test]
    public void CoverAlpha_FullOcclusion_IsFullCover()
    {
        Assert.That(IndoorOcclusionMath.CoverAlpha(1f, vanillaAlpha: 100),
            Is.EqualTo(IndoorOcclusionMath.FullSkyCover));
    }

    [Test]
    public void CoverAlpha_NeverLowersVanillasBakedValue()
    {
        // Only-ever-raising is what makes this safe to compose with other mods that write this alpha
        // (Dub's Skylights unroofs skylit cells; Biomes! Caverns reclassifies cavern roofs): the worst
        // case is that we leave their decision to let light in untouched.
        Assert.That(IndoorOcclusionMath.CoverAlpha(0f, vanillaAlpha: 100), Is.EqualTo(100));
        Assert.That(IndoorOcclusionMath.CoverAlpha(0.1f, vanillaAlpha: 200), Is.EqualTo(200));
    }

    [Test]
    public void CoverAlpha_UnroofedCellKeepsZeroCover()
    {
        // The common outdoor case: occlusion 0 over vanilla's own 0 must stay 0, or the whole map would
        // pick up a veil.
        Assert.That(IndoorOcclusionMath.CoverAlpha(0f, vanillaAlpha: 0), Is.EqualTo(0));
    }

    [Test]
    public void CoverAlpha_DoorLeak_LandsAboveVanillaButBelowFull()
    {
        // A default-leak door: still much darker than vanilla's 100, still visibly not sealed-black.
        byte alpha = IndoorOcclusionMath.CoverAlpha(1f - IndoorOcclusionMath.DefaultDoorSkyLeak, vanillaAlpha: 100);
        Assert.That(alpha, Is.GreaterThan(IndoorOcclusionMath.VanillaRoofedMinSkyCover));
        Assert.That(alpha, Is.LessThan(IndoorOcclusionMath.FullSkyCover));
    }

    [Test]
    public void CoverAlpha_ClampsOutOfRangeOcclusion()
    {
        Assert.That(IndoorOcclusionMath.CoverAlpha(5f, vanillaAlpha: 0), Is.EqualTo(IndoorOcclusionMath.FullSkyCover));
        Assert.That(IndoorOcclusionMath.CoverAlpha(-5f, vanillaAlpha: 0), Is.EqualTo(0));
    }
}
