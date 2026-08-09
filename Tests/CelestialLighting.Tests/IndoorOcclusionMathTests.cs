namespace CelestialLighting.Tests;

// Offline unit tests for §7b's pure core (Source/IndoorOcclusionMath.cs, linked into this project so
// these run against the exact shipped file). These cover the formulas; ApiCompatibilityTests covers
// that the vanilla members the adapter touches still exist.
[TestFixture]
public class IndoorOcclusionMathTests
{
    private const float Tolerance = 1e-5f;

    // --- BlocksSky: which cells are *interior* ---

    [Test]
    public void BlocksSky_OpenGround_IsNotInterior()
    {
        // The sky genuinely is overhead, so vanilla's own cover stands — this is what keeps the feature
        // from darkening the outdoors at all.
        Assert.That(IndoorOcclusionMath.BlocksSky(roofed: false, thickRoof: false, holdsRoof: false, isDoor: false),
            Is.False);
    }

    [Test]
    public void BlocksSky_RoofedFloor_IsInterior()
    {
        // The headline case: a sealed room tile takes no sky at all, so it renders from its artificial
        // glow alone (black when unlit) instead of vanilla's ~61%-of-sky.
        Assert.That(IndoorOcclusionMath.BlocksSky(roofed: true, thickRoof: false, holdsRoof: false, isDoor: false),
            Is.True);
    }

    [Test]
    public void BlocksSky_RoofedWall_IsNotInterior()
    {
        // Regression: a wall is roofed (its own cell carries the roof it holds up), and treating that as
        // interior blacked out every exterior wall and pushed the darkness a cell past it onto open
        // ground. Vanilla excludes roof-holders from cover in *both* of its vertex passes; so do we.
        Assert.That(IndoorOcclusionMath.BlocksSky(roofed: true, thickRoof: false, holdsRoof: true, isDoor: false),
            Is.False);
    }

    [Test]
    public void BlocksSky_WallUnderThickRoof_IsInterior()
    {
        // A mountain buries whatever is under it, wall included — vanilla's `roofDef.isThickRoof`
        // disjunct short-circuits its holdsRoof test for exactly this reason.
        Assert.That(IndoorOcclusionMath.BlocksSky(roofed: true, thickRoof: true, holdsRoof: true, isDoor: false),
            Is.True);
    }

    [Test]
    public void BlocksSky_Door_IsNeverInterior()
    {
        // A doorway is the boundary itself, so it reads exactly like open ground here — including under
        // thick roof, where the door rule deliberately wins — and can never propagate blackness outward
        // through the wall line. Any brightening at the threshold now comes from §7c's distance-graded
        // sky falloff, not from this classification.
        Assert.That(IndoorOcclusionMath.BlocksSky(roofed: true, thickRoof: false, holdsRoof: true, isDoor: true),
            Is.False);
        Assert.That(IndoorOcclusionMath.BlocksSky(roofed: true, thickRoof: true, holdsRoof: true, isDoor: true),
            Is.False);
    }

    [Test]
    public void BlocksSky_UnroofedCellIsNeverInterior_WhateverElseIsTrue()
    {
        // Defensive: thick roof cannot exist without a roof, but the flags arrive as independent
        // primitives here, so the unroofed case must dominate rather than fall through.
        Assert.That(IndoorOcclusionMath.BlocksSky(roofed: false, thickRoof: true, holdsRoof: false, isDoor: false),
            Is.False);
        Assert.That(IndoorOcclusionMath.BlocksSky(roofed: false, thickRoof: false, holdsRoof: true, isDoor: false),
            Is.False);
    }

    // --- CornerOcclusion: the OR over the (up to) four cells sharing a lattice point ---

    [Test]
    public void CornerOcclusion_TouchingAnyInterior_IsFull()
    {
        // OR, not a mean. This is what makes a room read *flat*: every corner inside it, and every
        // corner on its inner wall face, lands on exactly 1.0, so there is nothing to interpolate.
        Assert.That(IndoorOcclusionMath.CornerOcclusion(anyNeighbourBlocksSky: true),
            Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void CornerOcclusion_TouchingNoInterior_IsZero()
    {
        // The outer face of an exterior wall: no interior cell touches it, so the ground beyond the
        // building is untouched. Averaging here instead is what used to smear black onto the outdoors.
        Assert.That(IndoorOcclusionMath.CornerOcclusion(anyNeighbourBlocksSky: false),
            Is.EqualTo(0f).Within(Tolerance));
    }

    // --- CentreOcclusion: interior cells are flat, everything else is the mean of its own corners ---

    [Test]
    public void CentreOcclusion_InteriorCell_IsFullRegardlessOfItsCorners()
    {
        // Deep inside a room every corner is 1.0 anyway, so the centre agreeing at 1.0 is what makes the
        // tile shade flat — the mesh fans four triangles out of this vertex, and a centre that disagrees
        // with its corners is exactly the diamond-shaped bloom this replaced.
        Assert.That(IndoorOcclusionMath.CentreOcclusion(blocksSky: true, cornerOcclusionSum: 4f),
            Is.EqualTo(1f).Within(Tolerance));
        Assert.That(IndoorOcclusionMath.CentreOcclusion(blocksSky: true, cornerOcclusionSum: 0f),
            Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void CentreOcclusion_ExteriorWall_IsTheMidpointOfItsRamp()
    {
        // Inner corners 1.0 (they touch the room), outer corners 0.0 — so the wall tile carries a
        // straight gradient from black on its inner face to nothing on its outer one. Vanilla resolves
        // an uncovered cell's centre the same way, by averaging these same four vertices.
        Assert.That(IndoorOcclusionMath.CentreOcclusion(blocksSky: false, cornerOcclusionSum: 2f),
            Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void CentreOcclusion_OpenGroundBeyondTheWall_IsZero()
    {
        // None of its corners touch an interior cell, so nothing outside a properly walled building is
        // darkened at all — the fade is spent entirely on the wall tile.
        Assert.That(IndoorOcclusionMath.CentreOcclusion(blocksSky: false, cornerOcclusionSum: 0f),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void CentreOcclusion_BuildingCornerWall_TakesOnlyItsOneInteriorCorner()
    {
        // The wall block at a building's outside corner touches the room diagonally, at one lattice
        // point out of four: a quarter-strength tile, which is what bilinear interpolation across the
        // quad would have produced anyway.
        Assert.That(IndoorOcclusionMath.CentreOcclusion(blocksSky: false, cornerOcclusionSum: 1f),
            Is.EqualTo(0.25f).Within(Tolerance));
    }

    [Test]
    public void CentreOcclusion_ClampsAnOutOfRangeCornerSum()
    {
        Assert.That(IndoorOcclusionMath.CentreOcclusion(blocksSky: false, cornerOcclusionSum: 40f),
            Is.EqualTo(1f).Within(Tolerance));
        Assert.That(IndoorOcclusionMath.CentreOcclusion(blocksSky: false, cornerOcclusionSum: -4f),
            Is.EqualTo(0f).Within(Tolerance));
    }

    // --- The two together: an interior must be uniform, and a boundary must never darken outward ---

    [Test]
    public void RoomInterior_IsUniformlyFullyOccluded()
    {
        // Walk the vertices of an interior cell against a wall — the case the diamond bloom showed up
        // on. Its wall-side corners are shared with the wall cells, but each of those lattice points
        // still touches this interior cell, so they are 1.0 like the rest and the tile is flat.
        float wallSideCorner = IndoorOcclusionMath.CornerOcclusion(anyNeighbourBlocksSky: true);
        float innerCorner = IndoorOcclusionMath.CornerOcclusion(anyNeighbourBlocksSky: true);
        float centre = IndoorOcclusionMath.CentreOcclusion(blocksSky: true,
            cornerOcclusionSum: 2f * wallSideCorner + 2f * innerCorner);

        Assert.That(wallSideCorner, Is.EqualTo(1f).Within(Tolerance));
        Assert.That(centre, Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void GroundOutsideAWall_IsCompletelyUntouched()
    {
        // The regression this fix is about, end to end: the wall is not interior, so the lattice on its
        // outer face is 0, so the open cell beyond it averages 0 and takes vanilla's own alpha.
        bool wallIsInterior = IndoorOcclusionMath.BlocksSky(roofed: true, thickRoof: false, holdsRoof: true, isDoor: false);
        float outerCorner = IndoorOcclusionMath.CornerOcclusion(wallIsInterior);
        float outdoorCentre = IndoorOcclusionMath.CentreOcclusion(blocksSky: false, cornerOcclusionSum: 4f * outerCorner);

        Assert.That(outerCorner, Is.EqualTo(0f).Within(Tolerance));
        Assert.That(IndoorOcclusionMath.CoverAlpha(outdoorCentre, vanillaAlpha: 0), Is.EqualTo(0));
    }

    // --- CapOcclusion (the one path that can hold an interior above black) ---
    //
    // ambientLightSkyFraction defaults to 0f in every case below that isn't specifically exercising
    // it — 0 is what AmbientLightCompat.SkyFractionAt returns for every ordinary "no" (mod absent,
    // compat flag off, cell unreached), so these are exactly the pre-compat CapOcclusion(occlusion,
    // minIndoorBrightness) cases the two-argument signature used to cover.

    [Test]
    public void CapOcclusion_FloorOff_IsIdentity()
    {
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0f, ambientLightSkyFraction: 0f),
            Is.EqualTo(1f).Within(Tolerance));
        Assert.That(IndoorOcclusionMath.CapOcclusion(0.85f, minIndoorBrightness: 0f, ambientLightSkyFraction: 0f),
            Is.EqualTo(0.85f).Within(Tolerance));
    }

    [Test]
    public void CapOcclusion_LeavesExactlyTheFloorWorthOfSky()
    {
        // With the indoor floor at 0.15, a sealed cave keeps 15% of the sky — the thing that makes
        // the in-game "toggle minimum brightness" hotkey work indoors, where lifting CurSkyGlow cannot.
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0.15f, ambientLightSkyFraction: 0f),
            Is.EqualTo(0.85f).Within(Tolerance));
    }

    [Test]
    public void CapOcclusion_NeverRaisesOcclusion()
    {
        // A doorway already leaking more than the floor asks for is left alone — the cap is a ceiling only.
        Assert.That(IndoorOcclusionMath.CapOcclusion(0.5f, minIndoorBrightness: 0.15f, ambientLightSkyFraction: 0f),
            Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void CapOcclusion_FullFloor_DisablesOcclusionEntirely()
    {
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 1f, ambientLightSkyFraction: 0f),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void CapOcclusion_AppliedToCornersFirst_StillRampsAcrossTheWall()
    {
        // The adapter caps corners *before* averaging them into a boundary cell's centre, which keeps the
        // wall a gradient under a floor rather than flattening it out at the floor value.
        float cappedInner = IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0.5f, ambientLightSkyFraction: 0f);
        float wall = IndoorOcclusionMath.CentreOcclusion(blocksSky: false, cornerOcclusionSum: 2f * cappedInner);

        Assert.That(cappedInner, Is.EqualTo(0.5f).Within(Tolerance));
        Assert.That(wall, Is.EqualTo(0.25f).Within(Tolerance));
    }

    // --- CapOcclusion: the Ambient Light term (issue #80) ---

    [Test]
    public void CapOcclusion_AmbientLightFractionOff_IsIdentity()
    {
        // 0 is what SkyFractionAt returns when the mod is absent or the compat flag is off — this is
        // the faithful pre-compat baseline the harness A/B screenshots against.
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0f, ambientLightSkyFraction: 0f),
            Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void CapOcclusion_AmbientLightFraction_LeavesExactlyItsWorthOfSky()
    {
        // The bug this fixes, isolated: MinIndoorBrightness is 0 (the shipped Realistic default) but
        // Ambient Light's own readout says a cell is 46% lit by redistributed sky glow, so occlusion
        // must cap at 0.54 rather than staying at 1 (flat black).
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0f, ambientLightSkyFraction: 0.46f),
            Is.EqualTo(0.54f).Within(Tolerance));
    }

    [Test]
    public void CapOcclusion_TwoFloors_TheMoreGenerousOneWins()
    {
        // A player who raised MinIndoorBrightness for legibility never gets LESS sky than that setting
        // already guarantees just because Ambient Light's graded value happens to be lower at this
        // cell — and symmetrically, a cell Ambient Light lights up more than the flat floor keeps the
        // larger of the two. Min composes on the ceiling (1 - floor) side, so the smaller ceiling —
        // i.e. the larger underlying floor — is the one that actually governs.
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0.5f, ambientLightSkyFraction: 0.2f),
            Is.EqualTo(0.5f).Within(Tolerance));
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0.2f, ambientLightSkyFraction: 0.5f),
            Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void CapOcclusion_AmbientLightFraction_NeverRaisesOcclusion()
    {
        // Ceiling only, same as MinIndoorBrightness: a doorway already leaking more than the AL floor's
        // ceiling (here 1 - 0.1 = 0.9) asks for is left alone, since 0.5 is already below 0.9.
        Assert.That(IndoorOcclusionMath.CapOcclusion(0.5f, minIndoorBrightness: 0f, ambientLightSkyFraction: 0.1f),
            Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void CapOcclusion_AmbientLightFraction_ClampsOutOfRangeInput()
    {
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0f, ambientLightSkyFraction: 1.5f),
            Is.EqualTo(0f).Within(Tolerance));
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0f, ambientLightSkyFraction: -0.5f),
            Is.EqualTo(1f).Within(Tolerance));
    }

    // --- AmbientLightSkyFraction / the re-derived ComputeUnderRoofSkyLight / ComputeFalloffNoSky ---

    [Test]
    public void AmbientLightSkyFraction_NoSkyGlow_IsZero()
    {
        // Their own design intent, stated directly in their mod name: a genuinely pitch-black night
        // (curSkyGlow == 0) yields zero redistributed light regardless of depth, matching
        // ALFUtils.ComputeUnderRoofSkyLight's own early-out.
        Assert.That(IndoorOcclusionMath.AmbientLightSkyFraction(depth: 1, curSkyGlow: 0f, maxDepth: 12, passThroughPercent: 55f),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void AmbientLightSkyFraction_UnreachedCell_IsZero()
    {
        // depth <= 0 means their BFS never reached this cell (unroofed, or beyond maxDepth) — no
        // fraction to redistribute there.
        Assert.That(IndoorOcclusionMath.AmbientLightSkyFraction(depth: 0, curSkyGlow: 1f, maxDepth: 12, passThroughPercent: 55f),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void AmbientLightSkyFraction_AtTheOpening_IsPassThroughTimesSkyGlow()
    {
        // depth 1, the cell right beside the opening: falloff's depth term is 1/maxDepth, close to but
        // not quite 1, so the fraction sits just under curSkyGlow * passThrough.
        float maxDepth = 12f;
        float passThrough = 0.55f;
        float expected = 1f * passThrough * (1f - 1f / maxDepth);
        Assert.That(IndoorOcclusionMath.AmbientLightSkyFraction(depth: 1, curSkyGlow: 1f, maxDepth: 12, passThroughPercent: 55f),
            Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void AmbientLightSkyFraction_AtMaxDepth_IsZero()
    {
        // depthFraction clamps to exactly 1 at maxDepth, zeroing the falloff term — their BFS's reach
        // has a hard edge, not an asymptote.
        Assert.That(IndoorOcclusionMath.AmbientLightSkyFraction(depth: 12, curSkyGlow: 1f, maxDepth: 12, passThroughPercent: 55f),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void AmbientLightSkyFraction_BeyondMaxDepth_ClampsRatherThanGoingNegative()
    {
        Assert.That(IndoorOcclusionMath.AmbientLightSkyFraction(depth: 20, curSkyGlow: 1f, maxDepth: 12, passThroughPercent: 55f),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void AmbientLightSkyFraction_ScalesLinearlyWithCurSkyGlow()
    {
        float atFullGlow = IndoorOcclusionMath.AmbientLightSkyFraction(depth: 3, curSkyGlow: 1f, maxDepth: 12, passThroughPercent: 55f);
        float atHalfGlow = IndoorOcclusionMath.AmbientLightSkyFraction(depth: 3, curSkyGlow: 0.5f, maxDepth: 12, passThroughPercent: 55f);

        Assert.That(atHalfGlow, Is.EqualTo(atFullGlow * 0.5f).Within(Tolerance));
    }

    [Test]
    public void AmbientLightSkyFraction_DegenerateMaxDepth_ClampsToOneRatherThanDividingByZero()
    {
        // AmbientLightSettings.maxDepth is player-tunable on their settings screen; a value below 1
        // must not divide by zero or invert the falloff direction.
        Assert.That(
            () => IndoorOcclusionMath.AmbientLightSkyFraction(depth: 1, curSkyGlow: 1f, maxDepth: 0, passThroughPercent: 55f),
            Throws.Nothing);
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
    public void CoverAlpha_ClampsOutOfRangeOcclusion()
    {
        Assert.That(IndoorOcclusionMath.CoverAlpha(5f, vanillaAlpha: 0), Is.EqualTo(IndoorOcclusionMath.FullSkyCover));
        Assert.That(IndoorOcclusionMath.CoverAlpha(-5f, vanillaAlpha: 0), Is.EqualTo(0));
    }
}
