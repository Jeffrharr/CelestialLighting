namespace CelestialLighting;

// Lamp glow reach: how far past its own glowRadius a light is DRAWN, as a multiplier.
//
// WHERE THE IDEA CAME FROM. Astryls' fork of this mod (github.com/Astryls/CelestialLighting, "Soft
// vectorized shadows") arrived at it independently while building a rival vector-shadow pass, and
// the observation it rests on is theirs: vanilla's falloff reaches literally zero at glowRadius —
// 0.4/R² at the rim, under one part in 255 for a torch — so a lamp lights a small circle WELL and a
// large circle NOT AT ALL, where a real one lights a small circle well and a large circle faintly.
// The missing thing is not brightness near the lamp, it is the long dim tail. Their own pass is not
// what we adopted; the curve argument is.
//
// WHAT IT ACTUALLY DOES HERE, which is different from what it did there. Their build had no
// composition against vanilla, so reach was the whole feature and a multiplier of 1 made it a
// no-op — which cost them four rounds of debugging a pass that could not by construction change a
// pixel, and which is why their version carries a hard floor above 1. Ours composes
// max(0, ours - vanilla) per fragment in VectorLightMax.shader, so at reach 1 our model IS vanilla's
// and the excess is the geometry difference alone: the open door, the octile residue, the rim. That
// is the shipped renderer, working, and it is exactly what this must reproduce at 1.
//
// SO 1 IS THE OFF POSITION AND MUST STAY EXACT. Do not port their MinimumReach floor across — it
// solves a problem this architecture does not have, and it would break the flag rule this repo
// works to: a feature turned off has to reproduce the pre-feature behaviour precisely, not
// approximately, or the live A/B has no baseline to measure against.
//
// WHAT A PLAYER IS BUYING. Substituting the extended radius into vanilla's own curve raises the
// mid-field as well as lengthening the tail, so lamps get bigger AND softer, not merely longer.
// That is the honest description and it is why this is a taste option rather than a correction: it
// is the one thing in §27 that deliberately breaks the subsystem's standing rule that we change
// WHERE light reaches and never how bright a lamp is.
//
// The peak of the effect is worth knowing because it is not obvious. At the old rim (d = R) the
// excess over vanilla is exactly
//
//     0.6 * (reach - 1) / reach
//
// with no R in it at all — both curves carry the same 0.4/R² inverse-square term at that distance,
// so it cancels out of the difference and only the linear term survives. REACH ALONE SETS HOW
// BRIGHT the new light is, about 51 of 255 levels at reach 1.5, and the lamp's own radius decides
// only how many cells that brightness is spread over. A tester reporting "it does nothing on my sun
// lamp" is describing the NEAR field, where the inverse-square term dominates and a large R has
// already flattened the curve. VectorLightReachMathTests pins both halves.
public static class VectorLightReachMath
{
    // The off position. Named rather than written as a literal at each site because three separate
    // places have to agree that this exact value means "render precisely what shipped".
    public const float NoReach = 1f;

    // What the SIZE slider sits at when a player first ticks the feature on.
    //
    // NOT the resting value, and that is the point of having both. A checkbox whose sliders start at
    // their own off positions does nothing when ticked, which reads as a broken control — so the box
    // turns the feature on at a value somebody has actually looked at, and the sliders are then there
    // to move away from it. 1.2 was picked by eye from the live captures across 1.0 / 1.5 / 2.0 — a
    // restrained setting rather than the strongest one, on this repo's standing preference for
    // landing an effect where it reads as the room being warmer instead of as the mod announcing
    // itself, with the louder values left one drag away.
    //
    // It is also, by coincidence worth noting so nobody reads significance into it, the number
    // Astryl's fork uses as its hard FLOOR. Their 1.2 is the point below which their pass could not
    // work at all; ours is a starting position on a slider whose floor is 1. The two mean nothing to
    // each other.
    public const float DefaultReach = 1.2f;

    // Top of the slider. Beyond 2 the mid-field lift stops reading as a warmer room and starts
    // reading as the map being washed out, and the cost grows as the square of it — see the reach
    // notes in VectorLightField.BakeGathered for which parts of the bake actually scale and which
    // were made not to.
    public const float MaxReach = 2f;

    // Hard ceiling on the result, in cells: Verse.GlowGrid.MaxLightRadius.
    //
    // VANILLA'S OWN NUMBER, and taken from there rather than invented, because the question it
    // answers is vanilla's question — how wide is any light in this game allowed to be. Two defs
    // ship above radius 20 (24 and 30), and at reach 2 they would ask for 48 and 60 cells, wider
    // than the engine permits any glower to be and spread thin enough that the excess near the lamp
    // is a couple of levels for four times the silhouette scan.
    public const float MaxRadiusCells = 40f;

    // How far §27 draws a lamp, given the def's own glowRadius.
    //
    // Total, not the extra: callers substitute this for glowRadius wherever they were using it, so
    // the curve is vanilla's evaluated over a longer span rather than vanilla's with something
    // bolted onto the end. Astryls' notes record both of the alternatives failing, and failing the
    // same way — a (1-d/R)² tail and a capped max(vanillaCurve, halo) each computed to nothing
    // inside vanilla's radius, because that is precisely where a curve reshaped to agree with
    // vanilla agrees with vanilla.
    public static float ExtendedRadius(float glowRadius, float reach)
    {
        if (glowRadius <= 0f)
            return 0f;

        // Below 1 would DIM a lamp below what vanilla delivers, which the max composition cannot
        // express in any case — it can only ever raise a channel — so the pass would silently
        // become the reach-1 render while the slider claimed otherwise. Clamped rather than
        // rejected because a settings file written against a future range must not throw.
        float clamped = reach < NoReach ? NoReach : reach;
        float radius = glowRadius * clamped;

        return radius > MaxRadiusCells ? MaxRadiusCells : radius;
    }

    // --- BRIGHTNESS, the other half of the pair ---
    //
    // Astryl's build separates these two and is right to: reach decides how BIG a lamp is and
    // strength decides how MUCH of the extra to accept, and a player who finds the result too bright
    // should be able to say so without making their lamps small again. Their own note puts it
    // plainly — "Reach sets the SIZE, FillStrength sets the brightness, and they are now
    // independent; if this is too bright, turn strength down rather than reach" — after two rounds
    // in which shortening reach was the only dial available and produced lamps that were small AND
    // dim.
    //
    // IT COSTS NOTHING, unlike reach, and the asymmetry is worth knowing before designing UI around
    // the pair. Reach is GEOMETRY — it changes the radius every visibility polygon is cast to, so
    // moving it rebakes the map. Brightness is a MATERIAL PROPERTY: VectorLightOverlay.StrengthFor
    // recomputes the scalar per emitter per frame in the draw, so this slider is live, free, and
    // needs no invalidation path at all.

    // The value that leaves the renderer bit-identical to the shipped one, and what the master
    // switch pushes whenever it is off.
    //
    // NOT THE SAME AS THE FLOOR, and separating the two is the whole of what this file learned when
    // Astryl's own default was transferred across. It is the RESTING value: the multiplier at which
    // our pass delivers exactly VectorLightMath.DefaultStrength, the fitted constant that holds the
    // line that vector lighting changes shape and not how bright a lamp is. The slider is free to
    // range either side of it; the FEATURE's off state is what has to land on it exactly, and the
    // checkbox owns that.
    public const float NoBrightness = 1f;

    // What the switch starts the brightness slider at — Astryl's own shipped FillStrength.
    //
    // WHY IT IS BELOW THE RESTING VALUE, which is the opposite of what "extra vibrant" suggests and
    // is the interesting part. Their FillStrength scales a model that REPLACES vanilla's own
    // contribution, so 1 is its natural ceiling and 0.85 is a step back from it. Ours scales the
    // EXCESS over vanilla, and that excess is already delivered at DefaultStrength = 0.35 — a value
    // fitted against a measured vanilla arm precisely so that our additive pass reads at vanilla's
    // own brightness. So 0.35 is ≈unity in vanilla's units, our multiplier of 1 is ≈their 1, and
    // their 0.85 lands at ≈0.85 of ours.
    //
    // Their slider only ever reduces and ours ranges both ways: same name, and for a long time the
    // same assumed direction, which is why this is written down. Taking their number literally means
    // the switch starts a little UNDER the level the size slider alone would deliver — lamps bigger,
    // beams slightly softer — which is their calibration and is the point of adopting it.
    public const float DefaultBrightness = 0.85f;

    // Floor of the slider. Half, which is a round number rather than a measured one, and is meant as
    // headroom under the default rather than as a position anybody is expected to want: a control
    // whose default sits ON its floor cannot be turned down, and the whole argument for splitting
    // the axes was that "too bright" needs an answer other than shortening the light.
    //
    // IT IS DELIBERATELY BELOW NoBrightness, so this slider CAN dim the beams below what §27 draws
    // on its own. That is safe now in a way it would not have been before the master switch existed:
    // "feature off" is the checkbox's job and the checkbox pushes NoBrightness regardless of where
    // this sits, so the shipped-renderer baseline the flag rule needs is untouched by the range.
    public const float MinBrightness = 0.5f;

    // Two, and the ceiling is the blend's rather than a taste call — on the surface-lift path the
    // frame becomes dst * (1 + output) against a UNORM target, so the pass can at most double what
    // it lands on however much is asked for. Offering more than the composition can deliver would be
    // a slider with a dead top half.
    public const float MaxBrightness = 2f;

    // How much of the modelled excess to deliver, given the player's setting.
    //
    // Clamped rather than trusted for the reason ExtendedRadius clamps: a settings file written
    // against some other range must not be able to push the pass somewhere the composition was never
    // measured at.
    public static float Brightness(float brightness)
    {
        if (brightness < MinBrightness)
            return MinBrightness;

        return brightness > MaxBrightness ? MaxBrightness : brightness;
    }

    // Whether a given brightness moves the renderer off the shipped one AT ALL — in either
    // direction, which is why it is not named for brightening. Below the resting value it dims the
    // beams and above it lifts them, and both are changes a caller may need to know about.
    public static bool AltersBrightness(float brightness)
        => Brightness(brightness) != NoBrightness;

    // Whether a given reach leaves the renderer bit-identical to the shipped one.
    //
    // Asked rather than compared inline so the OFF path is one named decision instead of an equality
    // test repeated at each call site, and so the ceiling above cannot quietly turn a large lamp's
    // reach into "on" while delivering the same radius it had at 1. A radius-40 lamp is already at
    // the ceiling, so no reach setting moves it and none of the extra work should be done for it.
    public static bool Extends(float glowRadius, float reach)
    {
        return ExtendedRadius(glowRadius, reach) > glowRadius;
    }

    // The radius the COVERAGE GRID is baked at, which is not the radius the light is drawn at.
    //
    // THIS IS THE OPTIMISATION THAT MAKES REACH AFFORDABLE, so it is worth stating what it rests on
    // rather than leaving it as a min() somebody later "corrects". The coverage grid exists for one
    // purpose: to tell the mask how much of VANILLA's light at a cell our polygon says could not
    // have arrived. Past vanilla's own glowRadius its flood delivers exactly nothing — the cutoff in
    // SetGlowFromDist is hard — so there is nothing to scale, and a coverage byte out there can only
    // ever be multiplied by zero.
    //
    // Capping here therefore costs no accuracy at all, and it takes the whole coverage bake OFF the
    // reach cost curve: a lamp at reach 2 bakes exactly the grid it baked at reach 1, not four times
    // it. VectorLightMath.CoverageAt already answers 255 — fully lit, subtract nothing — for a cell
    // outside the grid, which is the correct answer for the annulus and was already documented as
    // the deliberate choice there, so no consumer needs to learn about this.
    //
    // The drawn half of the light needs no grid: the extended region is delivered by the fan and
    // composed per fragment in the shader, which reads vanilla out of a texture sized to vanilla's
    // own square (GlowLight.diameter = ceil(2*glowRadius + 1)) and clamps to its border beyond it.
    // That border is the right value to clamp to rather than merely a safe one — a texel on the
    // square's edge is glowRadius cells out, where vanilla's own cutoff (octile + 1 > glowRadius)
    // has already put zero — so every fragment in the annulus composes against a vanilla of zero
    // and the excess is our whole model, which is exactly what reach is for.
    //
    // WHAT THIS DOES NOT CAP, so that nobody completes the pattern by mistake. The four reach tests
    // that drive invalidation, polygon culling and section dirtying stay on the DRAWN radius, and
    // VectorLightMask.CollectReaching's admission test stays in step with them by construction —
    // its own header records that the two ends of that question have to agree or a cell whose
    // coverage moved stops dirtying the section that reads it. Over-admitting an emitter to a far
    // section costs its per-emitter setup and no per-cell work at all, since the accumulation
    // clamps to vanilla's square; splitting that quartet would trade a real invariant for it.
    public static float CoverageRadius(float glowRadius, float drawnRadius)
    {
        if (glowRadius <= 0f)
            return 0f;

        return drawnRadius < glowRadius ? drawnRadius : glowRadius;
    }
}
