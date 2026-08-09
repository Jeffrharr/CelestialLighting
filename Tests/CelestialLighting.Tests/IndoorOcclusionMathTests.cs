namespace CelestialLighting.Tests;

// Offline unit tests for §7b's pure core (Source/IndoorOcclusionMath.cs, linked into this project so
// these run against the exact shipped file). These cover the formulas; ApiCompatibilityTests covers
// that the vanilla members the adapter touches still exist.
[TestFixture]
public class IndoorOcclusionMathTests
{
    private const float Tolerance = 1e-5f;
    private const float Leak = IndoorOcclusionMath.DefaultDoorSkyLeak;

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
        // A doorway is the boundary itself. Its leak is applied at the vertices (CornerOcclusion) rather
        // than by making it interior, so it can never propagate blackness outward through the wall line
        // — including under thick roof, where the door rule deliberately wins.
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
        Assert.That(IndoorOcclusionMath.CornerOcclusion(anyNeighbourBlocksSky: true, skyLeak: 0f),
            Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void CornerOcclusion_TouchingNoInterior_IsZero()
    {
        // The outer face of an exterior wall: no interior cell touches it, so the ground beyond the
        // building is untouched. Averaging here instead is what used to smear black onto the outdoors.
        Assert.That(IndoorOcclusionMath.CornerOcclusion(anyNeighbourBlocksSky: false, skyLeak: 0f),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void CornerOcclusion_DoorCapsTheInteriorCorner()
    {
        // A corner shared by a doorway and the room behind it keeps `doorSkyLeak` worth of sky, so the
        // threshold sits a shade brighter than the rest of that room's edge.
        Assert.That(IndoorOcclusionMath.CornerOcclusion(anyNeighbourBlocksSky: true, skyLeak: Leak),
            Is.EqualTo(1f - Leak).Within(Tolerance));
    }

    [Test]
    public void CornerOcclusion_DoorCapNeverRaisesAnOpenCorner()
    {
        // The cap is a ceiling only — the outer corners of a door, or a freestanding gate in open
        // ground, stay fully open rather than picking up occlusion from the door itself.
        Assert.That(IndoorOcclusionMath.CornerOcclusion(anyNeighbourBlocksSky: false, skyLeak: Leak),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [TestCase(-0.5f, 1f)]  // a negative leak cannot make a doorway MORE than fully occluded
    [TestCase(0f, 1f)]     // leak 0 == a door occludes exactly like the room behind it
    [TestCase(1f, 0f)]     // leak 1 == a doorway is as open as the sky
    [TestCase(2f, 0f)]     // and an over-1 leak clamps rather than inverting
    public void CornerOcclusion_ClampsDoorLeak(float doorSkyLeak, float expected)
    {
        Assert.That(IndoorOcclusionMath.CornerOcclusion(anyNeighbourBlocksSky: true, skyLeak: doorSkyLeak),
            Is.EqualTo(expected).Within(Tolerance));
    }

    // --- CentreOcclusion: interior cells are flat, everything else is the mean of its own corners ---

    [Test]
    public void CentreOcclusion_SealedInteriorCell_IsFullWithoutNeedingToBeForced()
    {
        // Deep inside a room every corner is 1.0 anyway — each of them ORs over four cells, and this
        // cell is one of those four — so the mean lands on 1.0 by itself and the tile shades flat. The
        // mesh fans four triangles out of this vertex, and a centre that disagrees with its corners is
        // exactly the diamond-shaped bloom this replaced.
        //
        // This used to be enforced with an explicit `blocksSky ? 1f` short-circuit. It was removed
        // because it was a no-op here (as this assertion shows) and actively wrong on a cell a leak
        // reached: see CentreOcclusion_CellBesideALeakyBoundary_RampsInsteadOfSpiking.
        Assert.That(IndoorOcclusionMath.CentreOcclusion(cornerOcclusionSum: 4f),
            Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void CentreOcclusion_CellBesideALeakyBoundary_RampsInsteadOfSpiking()
    {
        // The sawtooth regression, pinned. An interior cell whose two boundary-side corners are fully
        // open (0) and whose two inward corners are closed (1) — what any leak, or another mod's indoor
        // brightening reaching CapOcclusion, produces at a room's edge. Forcing the centre to 1.0 while
        // the corners sat at 0 made the four triangles fan from a black hub out to lit corners, which
        // renders as a row of dark spikes — visible in
        // Tests/Screenshots/glass_wall_noon_leak_on_sawtooth.png. The mean gives 0.5 and a smooth ramp.
        Assert.That(IndoorOcclusionMath.CentreOcclusion(cornerOcclusionSum: 0f + 0f + 1f + 1f),
            Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void CentreOcclusion_ExteriorWall_IsTheMidpointOfItsRamp()
    {
        // Inner corners 1.0 (they touch the room), outer corners 0.0 — so the wall tile carries a
        // straight gradient from black on its inner face to nothing on its outer one. Vanilla resolves
        // an uncovered cell's centre the same way, by averaging these same four vertices.
        Assert.That(IndoorOcclusionMath.CentreOcclusion(cornerOcclusionSum: 2f),
            Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void CentreOcclusion_OpenGroundBeyondTheWall_IsZero()
    {
        // None of its corners touch an interior cell, so nothing outside a properly walled building is
        // darkened at all — the fade is spent entirely on the wall tile.
        Assert.That(IndoorOcclusionMath.CentreOcclusion(cornerOcclusionSum: 0f),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void CentreOcclusion_BuildingCornerWall_TakesOnlyItsOneInteriorCorner()
    {
        // The wall block at a building's outside corner touches the room diagonally, at one lattice
        // point out of four: a quarter-strength tile, which is what bilinear interpolation across the
        // quad would have produced anyway.
        Assert.That(IndoorOcclusionMath.CentreOcclusion(cornerOcclusionSum: 1f),
            Is.EqualTo(0.25f).Within(Tolerance));
    }

    [Test]
    public void CentreOcclusion_Doorway_IsBrighterThanTheWallItSitsIn()
    {
        // Same geometry as a wall (two interior corners, two open ones) but with the door cap applied to
        // the inner pair, so a threshold reads lighter than the wall either side of it. That difference
        // is the only thing DoorSkyLeak buys once doors stopped being interior cells.
        float wall = IndoorOcclusionMath.CentreOcclusion(cornerOcclusionSum: 2f);
        float innerCorner = IndoorOcclusionMath.CornerOcclusion(anyNeighbourBlocksSky: true, skyLeak: Leak);
        float doorway = IndoorOcclusionMath.CentreOcclusion(cornerOcclusionSum: 2f * innerCorner);

        Assert.That(doorway, Is.LessThan(wall));
        Assert.That(doorway, Is.EqualTo((1f - Leak) / 2f).Within(Tolerance));
    }

    [Test]
    public void CentreOcclusion_ClampsAnOutOfRangeCornerSum()
    {
        Assert.That(IndoorOcclusionMath.CentreOcclusion(cornerOcclusionSum: 40f),
            Is.EqualTo(1f).Within(Tolerance));
        Assert.That(IndoorOcclusionMath.CentreOcclusion(cornerOcclusionSum: -4f),
            Is.EqualTo(0f).Within(Tolerance));
    }

    // --- The two together: an interior must be uniform, and a boundary must never darken outward ---

    [Test]
    public void RoomInterior_IsUniformlyFullyOccluded()
    {
        // Walk the vertices of an interior cell against a wall — the case the diamond bloom showed up
        // on. Its wall-side corners are shared with the wall cells, but each of those lattice points
        // still touches this interior cell, so they are 1.0 like the rest and the tile is flat.
        float wallSideCorner = IndoorOcclusionMath.CornerOcclusion(anyNeighbourBlocksSky: true, skyLeak: 0f);
        float innerCorner = IndoorOcclusionMath.CornerOcclusion(anyNeighbourBlocksSky: true, skyLeak: 0f);
        float centre = IndoorOcclusionMath.CentreOcclusion(cornerOcclusionSum: 2f * wallSideCorner + 2f * innerCorner);

        Assert.That(wallSideCorner, Is.EqualTo(1f).Within(Tolerance));
        Assert.That(centre, Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void GroundOutsideAWall_IsCompletelyUntouched()
    {
        // The regression this fix is about, end to end: the wall is not interior, so the lattice on its
        // outer face is 0, so the open cell beyond it averages 0 and takes vanilla's own alpha.
        bool wallIsInterior = IndoorOcclusionMath.BlocksSky(roofed: true, thickRoof: false, holdsRoof: true, isDoor: false);
        float outerCorner = IndoorOcclusionMath.CornerOcclusion(wallIsInterior, skyLeak: 0f);
        float outdoorCentre = IndoorOcclusionMath.CentreOcclusion(cornerOcclusionSum: 4f * outerCorner);

        Assert.That(outerCorner, Is.EqualTo(0f).Within(Tolerance));
        Assert.That(IndoorOcclusionMath.CoverAlpha(outdoorCentre, vanillaAlpha: 0), Is.EqualTo(0));
    }

    // --- CapOcclusion (the one path that can hold an interior above black) ---
    //
    // ambientLightSkyFraction defaults to 0f in every case below that isn't specifically exercising
    // it — 0 is what IndoorGlowPassthrough.SkyFractionAt returns for every ordinary "no" (mod absent,
    // compat flag off, cell unreached), so these are exactly the pre-compat CapOcclusion(occlusion,
    // minIndoorBrightness) cases the two-argument signature used to cover.

    [Test]
    public void CapOcclusion_FloorOff_IsIdentity()
    {
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0f, indoorSkyGlowFraction: 0f),
            Is.EqualTo(1f).Within(Tolerance));
        Assert.That(IndoorOcclusionMath.CapOcclusion(0.85f, minIndoorBrightness: 0f, indoorSkyGlowFraction: 0f),
            Is.EqualTo(0.85f).Within(Tolerance));
    }

    [Test]
    public void CapOcclusion_LeavesExactlyTheFloorWorthOfSky()
    {
        // With the indoor floor at 0.15, a sealed cave keeps 15% of the sky — the thing that makes
        // the in-game "toggle minimum brightness" hotkey work indoors, where lifting CurSkyGlow cannot.
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0.15f, indoorSkyGlowFraction: 0f),
            Is.EqualTo(0.85f).Within(Tolerance));
    }

    [Test]
    public void CapOcclusion_NeverRaisesOcclusion()
    {
        // A doorway already leaking more than the floor asks for is left alone — the cap is a ceiling only.
        Assert.That(IndoorOcclusionMath.CapOcclusion(0.5f, minIndoorBrightness: 0.15f, indoorSkyGlowFraction: 0f),
            Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void CapOcclusion_FullFloor_DisablesOcclusionEntirely()
    {
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 1f, indoorSkyGlowFraction: 0f),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void CapOcclusion_AppliedToCornersFirst_StillRampsAcrossTheWall()
    {
        // The adapter caps corners *before* averaging them into a boundary cell's centre, which keeps the
        // wall a gradient under a floor rather than flattening it out at the floor value.
        float cappedInner = IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0.5f, indoorSkyGlowFraction: 0f);
        float wall = IndoorOcclusionMath.CentreOcclusion(cornerOcclusionSum: 2f * cappedInner);

        Assert.That(cappedInner, Is.EqualTo(0.5f).Within(Tolerance));
        Assert.That(wall, Is.EqualTo(0.25f).Within(Tolerance));
    }

    // --- CapOcclusion: the Ambient Light term (issue #80) ---

    [Test]
    public void CapOcclusion_IndoorSkyGlowFractionOff_IsIdentity()
    {
        // 0 is what SkyFractionAt returns when the mod is absent or the compat flag is off — this is
        // the faithful pre-compat baseline the harness A/B screenshots against.
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0f, indoorSkyGlowFraction: 0f),
            Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void CapOcclusion_IndoorSkyGlowFraction_LeavesExactlyItsWorthOfSky()
    {
        // The bug this fixes, isolated: MinIndoorBrightness is 0 (the shipped Realistic default) but
        // Ambient Light's own readout says a cell is 46% lit by redistributed sky glow, so occlusion
        // must cap at 0.54 rather than staying at 1 (flat black).
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0f, indoorSkyGlowFraction: 0.46f),
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
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0.5f, indoorSkyGlowFraction: 0.2f),
            Is.EqualTo(0.5f).Within(Tolerance));
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0.2f, indoorSkyGlowFraction: 0.5f),
            Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void CapOcclusion_IndoorSkyGlowFraction_NeverRaisesOcclusion()
    {
        // Ceiling only, same as MinIndoorBrightness: a doorway already leaking more than the AL floor's
        // ceiling (here 1 - 0.1 = 0.9) asks for is left alone, since 0.5 is already below 0.9.
        Assert.That(IndoorOcclusionMath.CapOcclusion(0.5f, minIndoorBrightness: 0f, indoorSkyGlowFraction: 0.1f),
            Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void CapOcclusion_IndoorSkyGlowFraction_ClampsOutOfRangeInput()
    {
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0f, indoorSkyGlowFraction: 1.5f),
            Is.EqualTo(0f).Within(Tolerance));
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0f, indoorSkyGlowFraction: -0.5f),
            Is.EqualTo(1f).Within(Tolerance));
    }

    // --- ArtificialGlow / IndoorSkyGlowFraction: the general "somebody else lit this" term ---

    [Test]
    public void ArtificialGlow_Unlit_IsZero()
    {
        Assert.That(IndoorOcclusionMath.ArtificialGlow(0, 0, 0, 0), Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void ArtificialGlow_FullyLitSentinel_ShortCircuitsToOne()
    {
        // Vanilla's GroundGlowAt checks `accumulatedGlowAt.a == 1` and returns 1f before it ever looks
        // at the colour channels. Transcribed rather than inferred: without it, an a==1 cell with dim
        // channels would read as nearly unlit and we would report vanilla's own light as another mod's.
        Assert.That(IndoorOcclusionMath.ArtificialGlow(0, 0, 0, 1), Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void ArtificialGlow_TakesTheBrightestChannel()
    {
        // max(r,g,b) / 255 * 3.6, straight out of vanilla. Asserted on each channel in turn so a
        // transcription slip that always read `r` cannot pass. Channel 30 deliberately: 30/255*3.6 is
        // 0.4235, comfortably under vanilla's 0.5 ceiling, so this measures the formula rather than the
        // clamp (which has its own test below). Anything above 35 saturates and would pass whatever the
        // channel maths did.
        float expected = 30f / 255f * 3.6f;
        Assert.Multiple(() =>
        {
            Assert.That(IndoorOcclusionMath.ArtificialGlow(30, 0, 0, 0), Is.EqualTo(expected).Within(Tolerance));
            Assert.That(IndoorOcclusionMath.ArtificialGlow(0, 30, 0, 0), Is.EqualTo(expected).Within(Tolerance));
            Assert.That(IndoorOcclusionMath.ArtificialGlow(0, 0, 30, 0), Is.EqualTo(expected).Within(Tolerance));
        });
    }

    [Test]
    public void ArtificialGlow_ClampsAtVanillasHalfCeiling()
    {
        // Vanilla caps artificial ground glow at 0.5 — which is why a lamp never reads as bright as
        // open daylight. Missing this cap would make us subtract more than vanilla ever added and
        // under-report another mod's contribution at exactly the bright cells it matters most.
        Assert.That(IndoorOcclusionMath.ArtificialGlow(255, 255, 255, 0), Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void IndoorSkyGlowFraction_VanillaRoofedCell_IsZero()
    {
        // THE property the whole mechanism rests on. On an unmodded install a roofed cell's ground glow
        // IS its artificial glow — vanilla gates the sky term on `!Roofed` — so the difference is 0 and
        // this term cannot move a single vertex without another mod present.
        Assert.That(IndoorOcclusionMath.IndoorSkyGlowFraction(groundGlow: 0.3f, artificialGlow: 0.3f, roofed: true),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void IndoorSkyGlowFraction_ExcessOverTheLamps_IsSkySourced()
    {
        // A mod raised GroundGlowAt above what the glow grid accounts for. The excess is sky by
        // elimination: vanilla suppressed its own sky term for this cell, so nothing else could have
        // produced it.
        Assert.That(IndoorOcclusionMath.IndoorSkyGlowFraction(groundGlow: 0.46f, artificialGlow: 0.1f, roofed: true),
            Is.EqualTo(0.36f).Within(Tolerance));
    }

    [Test]
    public void IndoorSkyGlowFraction_LampsBrighterThanTheSkyTerm_IsZero()
    {
        // Vanilla composes the two with Max, so once the lamps dominate there is no sky "beyond" them
        // to report. Returning the total here instead is the bug that would put dawn pink on a
        // windowless, well-lit workshop.
        Assert.That(IndoorOcclusionMath.IndoorSkyGlowFraction(groundGlow: 0.5f, artificialGlow: 0.5f, roofed: true),
            Is.EqualTo(0f).Within(Tolerance));
        Assert.That(IndoorOcclusionMath.IndoorSkyGlowFraction(groundGlow: 0.4f, artificialGlow: 0.5f, roofed: true),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void IndoorSkyGlowFraction_UnroofedCell_IsZeroHoweverBrightItIs()
    {
        // Outdoors vanilla puts CurSkyGlow into groundGlow itself, so the difference would report the
        // ordinary daylit sky as though a mod had injected it and cap occlusion across the whole map.
        // §7b never occludes an unroofed cell anyway, so 0 is both safe and correct.
        Assert.That(IndoorOcclusionMath.IndoorSkyGlowFraction(groundGlow: 1f, artificialGlow: 0f, roofed: false),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void IndoorSkyGlowFraction_ClampsIntoUnitRange()
    {
        // groundGlow arrives from a patched method, so an over-1 value is a third-party mod's business
        // and must clamp rather than propagate into CapOcclusion as a negative cap.
        Assert.That(IndoorOcclusionMath.IndoorSkyGlowFraction(groundGlow: 4f, artificialGlow: 0f, roofed: true),
            Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void IndoorSkyGlowFraction_ComposesIntoCapOcclusionAsAFloor()
    {
        // End to end: a cell another mod has lit to 0.46 keeps 0.46 worth of sky on screen instead of
        // being painted to full occlusion. This is issue #80's reported symptom stated as arithmetic —
        // "46% lit by its own readout, rendering flat black".
        float fraction = IndoorOcclusionMath.IndoorSkyGlowFraction(
            groundGlow: 0.46f, artificialGlow: 0f, roofed: true);

        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0f, indoorSkyGlowFraction: fraction),
            Is.EqualTo(1f - 0.46f).Within(Tolerance));
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
        // A default-leak door corner: still much darker than vanilla's 100, still visibly not sealed-black.
        byte alpha = IndoorOcclusionMath.CoverAlpha(1f - Leak, vanillaAlpha: 100);
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
