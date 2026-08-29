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
        Assert.That(
            IndoorOcclusionMath.BlocksSky(
                roofed: false, thickRoof: false, holdsRoof: false, isDoor: false, naturalRock: false),
            Is.False);
    }

    [Test]
    public void BlocksSky_RoofedFloor_IsInterior()
    {
        // The headline case: a sealed room tile takes no sky at all, so it renders from its artificial
        // glow alone (black when unlit) instead of vanilla's ~61%-of-sky.
        Assert.That(
            IndoorOcclusionMath.BlocksSky(
                roofed: true, thickRoof: false, holdsRoof: false, isDoor: false, naturalRock: false),
            Is.True);
    }

    [Test]
    public void BlocksSky_RoofedWall_IsNotInterior()
    {
        // Regression: a wall is roofed (its own cell carries the roof it holds up), and treating that as
        // interior blacked out every exterior wall and pushed the darkness a cell past it onto open
        // ground. Vanilla excludes roof-holders from cover in *both* of its vertex passes; so do we.
        Assert.That(
            IndoorOcclusionMath.BlocksSky(
                roofed: true, thickRoof: false, holdsRoof: true, isDoor: false, naturalRock: false),
            Is.False);
    }

    [Test]
    public void BlocksSky_UnminedRockUnderThickRoof_IsInterior()
    {
        // The mountain itself: solid stone with no wall face to catch light, so it is buried whole.
        Assert.That(
            IndoorOcclusionMath.BlocksSky(
                roofed: true, thickRoof: true, holdsRoof: true, isDoor: false, naturalRock: true),
            Is.True);
    }

    [Test]
    public void BlocksSky_BuiltWallUnderThickRoof_IsNotInterior()
    {
        // #129. This read True until the thickness term was narrowed to natural rock, and it swallowed
        // the whole wall ring of a mountain room into the same black square as its floor. A built wall
        // under a mined-out mountain roof is the same wall it would be under a constructed one, so it
        // gets the same corner ramp — what is overhead is not what decides that.
        Assert.That(
            IndoorOcclusionMath.BlocksSky(
                roofed: true, thickRoof: true, holdsRoof: true, isDoor: false, naturalRock: false),
            Is.False);
    }

    [Test]
    public void BlocksSky_NaturalRockWithoutAThickRoof_IsNotInterior()
    {
        // The two halves are an AND, not either alone: rock left standing under a *constructed* roof
        // has a face like any other wall. Rare, and pinned so the clause cannot quietly decay back into
        // "natural rock is always buried".
        Assert.That(
            IndoorOcclusionMath.BlocksSky(
                roofed: true, thickRoof: false, holdsRoof: true, isDoor: false, naturalRock: true),
            Is.False);
    }

    [Test]
    public void BlocksSky_Door_IsNeverInterior()
    {
        // A doorway is the boundary itself, so it reads exactly like open ground here — including under
        // thick roof, where the door rule deliberately wins — and can never propagate blackness outward
        // through the wall line. Any brightening at the threshold now comes from §7c's distance-graded
        // sky falloff, not from this classification.
        Assert.That(
            IndoorOcclusionMath.BlocksSky(
                roofed: true, thickRoof: false, holdsRoof: true, isDoor: true, naturalRock: false),
            Is.False);
        Assert.That(
            IndoorOcclusionMath.BlocksSky(
                roofed: true, thickRoof: true, holdsRoof: true, isDoor: true, naturalRock: false),
            Is.False);
    }

    [Test]
    public void BlocksSky_UnroofedCellIsNeverInterior_WhateverElseIsTrue()
    {
        // Defensive: thick roof cannot exist without a roof, but the flags arrive as independent
        // primitives here, so the unroofed case must dominate rather than fall through.
        Assert.That(
            IndoorOcclusionMath.BlocksSky(
                roofed: false, thickRoof: true, holdsRoof: false, isDoor: false, naturalRock: false),
            Is.False);
        Assert.That(
            IndoorOcclusionMath.BlocksSky(
                roofed: false, thickRoof: false, holdsRoof: true, isDoor: false, naturalRock: false),
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

    // --- CentreOcclusion: always the mean of the cell's own four corners ---

    [Test]
    public void CentreOcclusion_SealedInteriorCell_IsFullBecauseItsCornersAre()
    {
        // Deep inside a room every corner is 1.0 anyway, so the mean is 1.0 and the tile shades flat —
        // the mesh fans four triangles out of this vertex, and a centre that disagrees with its corners
        // is exactly the diamond-shaped bloom this arithmetic exists to avoid.
        Assert.That(IndoorOcclusionMath.CentreOcclusion(cornerOcclusionSum: 4f),
            Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void CentreOcclusion_InteriorCellWithALeakedCorner_FollowsTheCornersDown()
    {
        // The defect the blocksSky force used to cause, stated as the property that rules it out. A
        // doorway or a sky-falloff gradient caps two of an interior cell's corners below 1.0; the
        // centre must come down with them. Forcing it to a flat 1.0 instead pinned a black hub inside a
        // lit-cornered tile, and four triangles fan out of that hub — a row of dark spikes down the
        // inside of every wall carrying a door.
        //
        // 1.0 + 1.0 + 0.65 + 0.65 == 3.3: two sealed corners, two capped by a 0.35 leak.
        Assert.That(IndoorOcclusionMath.CentreOcclusion(cornerOcclusionSum: 3.3f),
            Is.EqualTo(0.825f).Within(Tolerance));
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
        float wallSideCorner = IndoorOcclusionMath.CornerOcclusion(anyNeighbourBlocksSky: true);
        float innerCorner = IndoorOcclusionMath.CornerOcclusion(anyNeighbourBlocksSky: true);
        float centre = IndoorOcclusionMath.CentreOcclusion(
            cornerOcclusionSum: 2f * wallSideCorner + 2f * innerCorner);

        Assert.That(wallSideCorner, Is.EqualTo(1f).Within(Tolerance));
        Assert.That(centre, Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void GroundOutsideAWall_IsCompletelyUntouched()
    {
        // The regression this fix is about, end to end: the wall is not interior, so the lattice on its
        // outer face is 0, so the open cell beyond it averages 0 and takes vanilla's own alpha.
        bool wallIsInterior = IndoorOcclusionMath.BlocksSky(roofed: true, thickRoof: false, holdsRoof: true, isDoor: false, naturalRock: false);
        float outerCorner = IndoorOcclusionMath.CornerOcclusion(wallIsInterior);
        float outdoorCentre = IndoorOcclusionMath.CentreOcclusion(cornerOcclusionSum: 4f * outerCorner);

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
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0f, skyFalloffFraction: 0f),
            Is.EqualTo(1f).Within(Tolerance));
        Assert.That(IndoorOcclusionMath.CapOcclusion(0.85f, minIndoorBrightness: 0f, skyFalloffFraction: 0f),
            Is.EqualTo(0.85f).Within(Tolerance));
    }

    [Test]
    public void CapOcclusion_LeavesExactlyTheFloorWorthOfSky()
    {
        // With the indoor floor at 0.15, a sealed cave keeps 15% of the sky — the thing that makes
        // the in-game "toggle minimum brightness" hotkey work indoors, where lifting CurSkyGlow cannot.
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0.15f, skyFalloffFraction: 0f),
            Is.EqualTo(0.85f).Within(Tolerance));
    }

    [Test]
    public void CapOcclusion_NeverRaisesOcclusion()
    {
        // A doorway already leaking more than the floor asks for is left alone — the cap is a ceiling only.
        Assert.That(IndoorOcclusionMath.CapOcclusion(0.5f, minIndoorBrightness: 0.15f, skyFalloffFraction: 0f),
            Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void CapOcclusion_FullFloor_DisablesOcclusionEntirely()
    {
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 1f, skyFalloffFraction: 0f),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void CapOcclusion_AppliedToCornersFirst_StillRampsAcrossTheWall()
    {
        // The adapter caps corners *before* averaging them into a boundary cell's centre, which keeps the
        // wall a gradient under a floor rather than flattening it out at the floor value.
        float cappedInner = IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0.5f, skyFalloffFraction: 0f);
        float wall = IndoorOcclusionMath.CentreOcclusion(cornerOcclusionSum: 2f * cappedInner);

        Assert.That(cappedInner, Is.EqualTo(0.5f).Within(Tolerance));
        Assert.That(wall, Is.EqualTo(0.25f).Within(Tolerance));
    }

    // --- CapOcclusion: the Ambient Light term (issue #80) ---

    [Test]
    public void CapOcclusion_SkyFalloffFractionOff_IsIdentity()
    {
        // 0 is what SkyFractionAt returns when the mod is absent or the compat flag is off — this is
        // the faithful pre-compat baseline the harness A/B screenshots against.
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0f, skyFalloffFraction: 0f),
            Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void CapOcclusion_SkyFalloffFraction_LeavesExactlyItsWorthOfSky()
    {
        // The bug this fixes, isolated: MinIndoorBrightness is 0 (the shipped Realistic default) but
        // Ambient Light's own readout says a cell is 46% lit by redistributed sky glow, so occlusion
        // must cap at 0.54 rather than staying at 1 (flat black).
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0f, skyFalloffFraction: 0.46f),
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
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0.5f, skyFalloffFraction: 0.2f),
            Is.EqualTo(0.5f).Within(Tolerance));
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0.2f, skyFalloffFraction: 0.5f),
            Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void CapOcclusion_SkyFalloffFraction_NeverRaisesOcclusion()
    {
        // Ceiling only, same as MinIndoorBrightness: a doorway already leaking more than the AL floor's
        // ceiling (here 1 - 0.1 = 0.9) asks for is left alone, since 0.5 is already below 0.9.
        Assert.That(IndoorOcclusionMath.CapOcclusion(0.5f, minIndoorBrightness: 0f, skyFalloffFraction: 0.1f),
            Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void CapOcclusion_SkyFalloffFraction_ClampsOutOfRangeInput()
    {
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0f, skyFalloffFraction: 1.5f),
            Is.EqualTo(0f).Within(Tolerance));
        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0f, skyFalloffFraction: -0.5f),
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
        // Vanilla's GroundGlowAt checks `accumulatedGlowAt.a == 1` and returns 1f before it looks at the
        // colour channels. Transcribed rather than inferred: without it, an a==1 cell with dim channels
        // would read as nearly unlit and we would report vanilla's own light as another mod's.
        Assert.That(IndoorOcclusionMath.ArtificialGlow(0, 0, 0, 1), Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void ArtificialGlow_TakesTheBrightestChannel()
    {
        // max(r,g,b) / 255 * 3.6, straight out of vanilla. Asserted on each channel in turn so a
        // transcription slip that always read `r` cannot pass. Channel 30 deliberately: 30/255*3.6 is
        // 0.4235, under vanilla's 0.5 ceiling, so this measures the formula rather than the clamp.
        // Anything above 35 saturates and would pass whatever the channel maths did.
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
        // Vanilla caps artificial ground glow at 0.5 — which is why a lamp never reads as bright as open
        // daylight. Missing the cap would subtract more than vanilla ever added and under-report another
        // mod's contribution at exactly the bright cells where it matters most.
        Assert.That(IndoorOcclusionMath.ArtificialGlow(255, 255, 255, 0), Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void IndoorSkyGlowFraction_VanillaRoofedCell_IsZero()
    {
        // THE property the mechanism rests on. On an unmodded install a roofed cell's ground glow IS its
        // artificial glow — vanilla gates the sky term on `!Roofed` — so the difference is 0 and this
        // term cannot move a vertex without another mod present.
        Assert.That(IndoorOcclusionMath.IndoorSkyGlowFraction(groundGlow: 0.3f, artificialGlow: 0.3f, roofed: true),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void IndoorSkyGlowFraction_ExcessOverTheLamps_IsSkySourced()
    {
        // A mod raised GroundGlowAt above what the glow grid accounts for. The excess is sky by
        // elimination: vanilla suppressed its own sky term here, so nothing else could have produced it.
        Assert.That(IndoorOcclusionMath.IndoorSkyGlowFraction(groundGlow: 0.46f, artificialGlow: 0.1f, roofed: true),
            Is.EqualTo(0.36f).Within(Tolerance));
    }

    [Test]
    public void IndoorSkyGlowFraction_LampsBrighterThanTheSkyTerm_IsZero()
    {
        // Vanilla composes the two with Max, so once the lamps dominate there is no sky "beyond" them to
        // report. Returning the total here instead is the bug that would put dawn pink on a windowless,
        // well-lit workshop. Live-verified in Tests/Scenarios/indoor_glow_lamp.json.
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
        // being painted to full occlusion. Issue #80's reported symptom as arithmetic — "46% lit by its
        // own readout, rendering flat black".
        float fraction = IndoorOcclusionMath.IndoorSkyGlowFraction(
            groundGlow: 0.46f, artificialGlow: 0f, roofed: true);

        Assert.That(IndoorOcclusionMath.CapOcclusion(1f, minIndoorBrightness: 0f, skyFalloffFraction: fraction),
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
    public void CoverAlpha_ClampsOutOfRangeOcclusion()
    {
        Assert.That(IndoorOcclusionMath.CoverAlpha(5f, vanillaAlpha: 0), Is.EqualTo(IndoorOcclusionMath.FullSkyCover));
        Assert.That(IndoorOcclusionMath.CoverAlpha(-5f, vanillaAlpha: 0), Is.EqualTo(0));
    }

    // --- EffectiveIndoorFloor: the two brightness floors stop compounding ---
    //
    // What these assert is the COMPOSED value, not the cap, and that distinction is the whole point of the
    // subsystem. `SealedRoomBrightness` below is the renderer's own arithmetic written out independently —
    // the sky the material still carries after §7a (`keep`), times the share of it a sealed cell admits —
    // so a test that passes here is a statement about what lands on screen rather than a restatement of
    // the formula under test. Writing it the other way round (assert the cap equals floor/keep) would
    // compute both sides with the code under test and assert x == x.
    //
    // Note it runs the cap through CoverAlpha against vanilla's own baked 100 rather than using the cap
    // directly. That is not incidental precision: the max in CoverAlpha is what makes 0.608 the real
    // ceiling on an interior, and an oracle that skipped it would have "proved" a parity the renderer
    // never delivers.

    // Byte quantisation only. The cap becomes one of 256 alpha values on its way through the mesh, so an
    // exact float comparison of the composed result is wrong by up to half a step (1/510) times keep.
    private const float AlphaQuantisation = 0.003f;

    // The brightest a roofed cell can render, as a fraction of the sky the material still carries:
    // vanilla's RoofedAreaMinSkyCover is a floor CoverAlpha will not go under, so 100/255 of the sky is
    // always covered no matter what any floor asks for.
    private const float VanillaAdmittanceCeiling =
        1f - IndoorOcclusionMath.VanillaRoofedMinSkyCover / 255f;

    // The renderer's composition for a sealed interior cell, from vanilla's side of the contract:
    // MatBases.LightOverlay.color is the sky lerped toward black by 1-keep, and
    // SectionLayer_LightingOverlay's vertex alpha admits (1 - cover/255) of it. Expressed as a fraction of
    // the undarkened sky, which is the unit MinNightBrightness is already in.
    private static float SealedRoomBrightness(float minIndoorBrightness, float overlayKeep, bool decoupled)
    {
        float floor = decoupled
            ? IndoorOcclusionMath.EffectiveIndoorFloor(minIndoorBrightness, overlayKeep)
            : minIndoorBrightness;

        float cover = IndoorOcclusionMath.CapOcclusion(1f, floor, skyFalloffFraction: 0f);
        byte alpha = IndoorOcclusionMath.CoverAlpha(cover, IndoorOcclusionMath.VanillaRoofedMinSkyCover);
        return overlayKeep * (1f - alpha / 255f);
    }

    [TestCase(0.50f, 0.50f)]   // the shipped Cinematic pair at the night floor
    [TestCase(0.50f, 0.75f)]   // mid-dusk, §7a partway into its ramp
    [TestCase(0.15f, 0.21f)]   // a low floor against the moonless-night keep (0.04/0.19) DESIGN quotes
    [TestCase(0.30f, 0.60f)]
    [TestCase(0.50f, 1.00f)]   // daylight: the two arms must agree exactly
    public void EffectiveIndoorFloor_SealedRoomFloorsAtTheSettingOrTheCeiling(float minIndoor, float keep)
    {
        // The fix: an interior floors at MinIndoorBrightness of the SKY — the same quantity
        // MinNightBrightness is a fraction of, so the two sliders can finally be compared — or at
        // vanilla's admittance ceiling, whichever it reaches first.
        float ceiling = keep * VanillaAdmittanceCeiling;
        float expected = minIndoor < ceiling ? minIndoor : ceiling;
        Assert.That(SealedRoomBrightness(minIndoor, keep, decoupled: true),
            Is.EqualTo(expected).Within(AlphaQuantisation));
    }

    [TestCase(0.50f, 0.50f, 0.2490f, 0.3039f)]
    [TestCase(0.50f, 0.75f, 0.3735f, 0.4559f)]
    [TestCase(0.15f, 0.21f, 0.0313f, 0.1276f)]
    [TestCase(0.30f, 0.60f, 0.1788f, 0.2988f)]
    public void EffectiveIndoorFloor_ClosesTheGapItWasBuiltToClose(
        float minIndoor, float keep, float coupled, float decoupled)
    {
        // The bug proved red on the same inputs rather than asserted in prose: the two floors used to
        // multiply, so a sealed room rendered at keep x MinIndoorBrightness. On the shipped Cinematic pair
        // that is 0.249 while the settings screen shows 0.50 for both knobs and the ground outside renders
        // at 0.50.
        Assert.That(SealedRoomBrightness(minIndoor, keep, decoupled: false),
            Is.EqualTo(coupled).Within(AlphaQuantisation));

        // And the values it moves to. Spelled out as constants rather than recomputed so a change to
        // either formula has to come here and restate what it now costs a player, instead of quietly
        // agreeing with itself.
        Assert.That(SealedRoomBrightness(minIndoor, keep, decoupled: true),
            Is.EqualTo(decoupled).Within(AlphaQuantisation));
    }

    [Test]
    public void EffectiveIndoorFloor_DaylightIsTheIdentity()
    {
        // keep == 1 is every hour §7a is not darkening anything, which is the majority of the day. The
        // decoupling must be provably invisible there or it is not a night fix, it is a retune.
        Assert.That(IndoorOcclusionMath.EffectiveIndoorFloor(0.5f, overlayKeep: 1f),
            Is.EqualTo(0.5f).Within(Tolerance));
        Assert.That(IndoorOcclusionMath.EffectiveIndoorFloor(0.15f, overlayKeep: 1f),
            Is.EqualTo(0.15f).Within(Tolerance));
        Assert.That(SealedRoomBrightness(0.5f, 1f, decoupled: true),
            Is.EqualTo(SealedRoomBrightness(0.5f, 1f, decoupled: false)).Within(Tolerance));
    }

    [Test]
    public void EffectiveIndoorFloor_FloorOffStaysOff()
    {
        // The Realistic preset. A 0 floor is "interiors may go genuinely black", and no amount of sky
        // darkening may turn that into a lift — 0/keep is still 0, but the early return says so without
        // relying on the division.
        Assert.That(IndoorOcclusionMath.EffectiveIndoorFloor(0f, overlayKeep: 0.5f), Is.EqualTo(0f));
        Assert.That(IndoorOcclusionMath.EffectiveIndoorFloor(0f, overlayKeep: 0f), Is.EqualTo(0f));
    }

    [TestCase(0.50f, 0.50f)]
    [TestCase(0.50f, 0.25f)]
    [TestCase(0.50f, 0f)]
    public void EffectiveIndoorFloor_SaturatesWhenTheSkyCannotDeliverTheFloor(float minIndoor, float keep)
    {
        // No headroom (issue #103): once §7a has darkened the sky to the floor or below, admitting all the
        // sky vanilla will let through is the brightest an interior can be. The cap saturates at 1 rather
        // than dividing past it — keep == 0 is the degenerate member of the family, and is here to pin
        // that the divide is never reached rather than guarded after the fact.
        Assert.That(IndoorOcclusionMath.EffectiveIndoorFloor(minIndoor, keep), Is.EqualTo(1f));

        // What saturation actually delivers, which is NOT parity with the ground outside: vanilla's own
        // roofed clamp still covers 100/255 of the sky, so the room lands at 0.608 of the open ground.
        // Reaching the rest would mean lowering an alpha CoverAlpha deliberately never lowers.
        Assert.That(SealedRoomBrightness(minIndoor, keep, decoupled: true),
            Is.EqualTo(keep * VanillaAdmittanceCeiling).Within(AlphaQuantisation));
    }

    [Test]
    public void EffectiveIndoorFloor_IndoorsCanStillBeSetDarkerThanOutdoors()
    {
        // With both floors finally in one unit, MinIndoorBrightness BELOW MinNightBrightness becomes a
        // meaningful setting: it buys back the distinction between inside and outside at the floor, which
        // is what a player who dislikes the parity would reach for. At 0.15 against an outdoor 0.50 the
        // room renders at 0.15 — under the 0.304 ceiling, so the setting is honoured exactly.
        Assert.That(SealedRoomBrightness(0.15f, overlayKeep: 0.5f, decoupled: true),
            Is.EqualTo(0.15f).Within(AlphaQuantisation));
    }

    [Test]
    public void EffectiveIndoorFloor_ClampsOutOfRangeInputs()
    {
        Assert.That(IndoorOcclusionMath.EffectiveIndoorFloor(5f, overlayKeep: 1f), Is.EqualTo(1f));
        Assert.That(IndoorOcclusionMath.EffectiveIndoorFloor(-5f, overlayKeep: 1f), Is.EqualTo(0f));
        Assert.That(IndoorOcclusionMath.EffectiveIndoorFloor(0.5f, overlayKeep: 5f),
            Is.EqualTo(0.5f).Within(Tolerance));
        Assert.That(IndoorOcclusionMath.EffectiveIndoorFloor(0.5f, overlayKeep: -5f), Is.EqualTo(1f));
    }
}
