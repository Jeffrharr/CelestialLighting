using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types, linked into the offline tests so the shipped code is
// the tested code.
//
// Subsystem 25 (DESIGN.md §25, issue #138): THE DRAWN CLOUD SHEET — actual cloud on screen, above the
// map, rather than only its effect on the ground.
//
// WHY IT IS A SEPARATE SUBSYSTEM FROM §23b/§23c RATHER THAN A SETTING ON THEM. Those two draw
// ILLUMINATION: light added or removed at ground level, below FogOfWar, because that is where the
// light lands. This draws SKY — geometry between the camera and the map, above FogOfWar for the same
// reason §11a's aurora is (a cloud is not hidden by a player's ignorance of the terrain beneath it).
// Different altitude, different blend, different question. What they share is the field, and that
// sharing is the entire point: the bright ground §23b draws has to be under §25's gaps, or the two
// contradict each other on screen.
//
// WHAT IT DELIBERATELY IS NOT, YET. A believable cloud is a volume with depth, self-shadowing and
// parallax against the camera. This is a flat, single-layer, top-down field with one colour ramp —
// enough to answer "does drawn cloud read at all from this camera", which is the question issue #138
// says to answer before designing anything larger. Issue #138 also records the particle-based
// alternative, which fails and succeeds in completely different places and should be prototyped
// separately rather than merged into this one up front.
public static class CloudSheetMath
{
    // Peak alpha of the sheet over a half-covered sky in full daylight. Low, and it has to be: this
    // draws OVER the colony, so anything approaching opacity stops the player reading their own base.
    // The trade this constant is making is exactly the one #138 asks to have measured — visible enough
    // to be cloud, sheer enough to play under.
    public const float SheetAmplitude = 0.35f;

    // How dark the sheet goes at night, as a fraction of its daylight colour. Not zero: cloud is
    // visible on a moonlit night as a darker mass against the sky, and a sheet that vanished entirely
    // at dusk would pop out of existence at the exact moment §23b starts drawing. Kept low so night
    // clouds read as an absence of stars rather than as a grey slab.
    public const float NightBrightness = 0.12f;

    // Where the sheet's own lighting hands over from "lit from above" to "lit from beneath", in
    // degrees of solar elevation. Above this the deck is topped by direct sun and reads white-grey;
    // by the time the sun reaches the horizon the light is arriving flat and the underside has it.
    //
    // THE HANDOVER IS AT THE HORIZON, NOT ABOVE IT, and this constant is only the width of the band
    // it is softened over. The geometry is not subtle — a deck's top is lit while the sun is above
    // the plane the deck sits in, its base once the sun is below — so the crossing is at elevation 0
    // and the only question is how abruptly. 6 degrees is wide enough that the recolour reads as the
    // golden hour arriving rather than as a switch being thrown at sunset.
    public const float UnderlitNoneDegrees = 6f;

    // How far past its own shadow entry a deck's underlighting is faded out over, in degrees. Not
    // zero, for three reasons that all point the same way: the sun is a 0.53-degree disc rather than
    // a point, refraction holds it visible for a few tenths past where the geometry says it has
    // gone, and a cloud deck is a ragged volume hundreds of metres thick rather than a plane. One
    // degree covers all three and, more to the point, stops the deck going out like a light.
    public const float ShadowFadeDegrees = 1f;

    // How much of the sheet's colour comes from the UNDERSIDE being lit rather than the top, in
    // [0, 1], for a deck that enters Earth's shadow at `shadowEntryDegrees` below the horizon
    // (CloudDeckMath.ShadowEntryDegrees).
    //
    // THIS USED TO BE ONE FIXED WINDOW FOR EVERY CLOUD, [-1.5, 6] degrees, and the old lower bound
    // was the crudest number in the subsystem: it said every deck stays underlit forever once the
    // sun is 1.5 degrees down, which is wrong at both ends. A low deck has been in Earth's shadow
    // since about 1.0 degrees and should already be a grey mass; cirrus at 9.5 km is still in direct
    // sun at 3.1 and should still be burning. Holding both at 1 meant the whole sky recoloured
    // together and then stayed recoloured — a sunset with no sequence in it, which is a sunset with
    // the interesting part removed.
    //
    // So it is now TWO ramps multiplied, one at each end, and only the top one is a taste call:
    //
    //   rise   0 at UnderlitNoneDegrees, 1 at the horizon. The handover.
    //   fall   1 down to the deck's own shadow entry, 0 a further ShadowFadeDegrees below it. The
    //          deck losing the sun — derived per deck, not tuned.
    //
    // Smoothstepped at both ends so neither has a crease. The product peaks at exactly 1 over the
    // deck's whole lit-from-beneath window, which for the low deck is a sliver (0 to -1.0 degrees)
    // and for cirrus is three times that — which IS issue #88's table, finally drawn instead of
    // described.
    public static float UnderlitFraction(float elevationDegrees, float shadowEntryDegrees)
    {
        if (float.IsNaN(elevationDegrees))
            return 0f;

        return RiseTowardSunset(elevationDegrees) * FallIntoShadow(elevationDegrees, shadowEntryDegrees);
    }

    // The daytime end: how far through the top-lit-to-bottom-lit handover the sun has got.
    private static float RiseTowardSunset(float elevationDegrees)
    {
        if (elevationDegrees >= UnderlitNoneDegrees)
            return 0f;

        if (elevationDegrees <= 0f)
            return 1f;

        return Smoothstep((UnderlitNoneDegrees - elevationDegrees) / UnderlitNoneDegrees);
    }

    // The night end: how much of the deck is still catching direct sun from beneath at all.
    //
    // A non-positive shadowEntryDegrees is a deck sitting on the ground, which loses the sun at the
    // same instant the ground does — CloudUnderlightMath.ShadowEntryDepressionDegrees's own
    // degenerate case — so the fade starts at the horizon rather than being treated as a zero-width
    // window and snapping.
    private static float FallIntoShadow(float elevationDegrees, float shadowEntryDegrees)
    {
        float entry = shadowEntryDegrees > 0f ? shadowEntryDegrees : 0f;
        float belowHorizon = -elevationDegrees;

        if (belowHorizon <= entry)
            return 1f;

        if (belowHorizon >= entry + ShadowFadeDegrees)
            return 0f;

        return Smoothstep(1f - (belowHorizon - entry) / ShadowFadeDegrees);
    }

    private static float Smoothstep(float t)
    {
        float clamped = Clamp01(t);
        return clamped * clamped * (3f - 2f * clamped);
    }

    // How bright the sheet is overall, in [NightBrightness, 1], as a function of how much light there
    // is to light it with. Driven by the map's own sky glow rather than by elevation, so it tracks
    // whatever §7/§13/§21 have already decided about how bright the world is — including an eclipse,
    // which should darken the clouds along with everything else and would not if this keyed on
    // geometry.
    public static float SheetBrightness(float skyGlow) =>
        NightBrightness + (1f - NightBrightness) * Clamp01(skyGlow);

    // How bright a deck lit ONLY from beneath is, at the peak of its own underlit window.
    //
    // Well above the sky glow it replaces, and that is the whole point: a deck catching direct
    // sunlight after the ground has fallen into shadow IS brighter than the ground. It is the
    // brightest thing in a sunset sky, and it is the entire reason anybody looks at one.
    public const float UnderlitDeckFloor = 0.55f;

    // How lit the DECK is, as opposed to how lit the GROUND is — and the distinction is the fix for a
    // measured failure rather than a refinement.
    //
    // WHAT WENT WRONG. §25 scaled both the sheet's colour and its alpha by SheetBrightness(skyGlow),
    // i.e. by how bright the world below is. That is right for a cloud in shadow and exactly backwards
    // for one in sunlight: sky glow collapses the moment the sun crosses the horizon, so the sheet
    // went dark and sheer at precisely the elevations §25b's per-deck sunset windows live in. Measured
    // — the whole four-frame sunset A/B came out at median ΔE 0.00 with under 1% of pixels changed,
    // while the probes proved the colour arithmetic underneath was running correctly. A subsystem
    // computing the right answer onto an invisible surface.
    //
    // So the deck takes whichever is brighter: the ambient light everything gets, or the direct light
    // only it is getting.
    //
    // ECLIPSES STILL DARKEN THE CLOUDS, which was the stated reason SheetBrightness keys on glow
    // rather than on geometry and is not given up here. The floor is proportional to the underlit
    // fraction, which is zero above 6 degrees of elevation — so through a daylight eclipse, the case
    // that matters, the max is sky glow exactly as before and this term contributes nothing.
    //
    // The narrow case it does get wrong, named rather than left to be found: an eclipse DURING civil
    // twilight would keep the deck lit, because the sun's occlusion reaches this only through glow
    // and the floor has overridden it. Wrong, rare, and much the lesser of the two errors — the
    // alternative is the sunset being invisible every single evening to avoid being wrong on the few
    // minutes an eclipse and a sunset ever coincide.
    public static float DeckIllumination(float skyGlow, float underlitFraction)
    {
        float ambient = SheetBrightness(skyGlow);
        float direct = UnderlitDeckFloor * Clamp01(underlitFraction);
        return direct > ambient ? direct : ambient;
    }

    // The sheet's alpha. Zero is a true no-op: the overlay makes no draw call at all.
    //
    // UNLIKE §23b AND §23c THIS IS NOT A RESIDUAL, and the difference is the point. Those two draw
    // what a flat sky colour cannot express, so they subtract the mean and leave it to §13/§22. A
    // drawn cloud is not an adjustment to anything — it is the object itself, and an overcast sky
    // should be covered rather than uniform-and-therefore-invisible.
    //
    // IT DOES NOT SCALE WITH THE CLOUD FRACTION, AND THAT IS A CORRECTION RATHER THAN AN OMISSION.
    // It did in the tiled version, correctly: one stretched field had to express "how cloudy" as
    // opacity because it covered the whole map either way. With bounded sheets, coverage is a COUNT
    // (CloudSheetLayout.SheetCount) — more cloud is more clouds — so multiplying opacity by it as well
    // counts coverage twice, which is exactly the double-count §23b's mean subtraction exists to
    // avoid one lane over. Measured: with the fraction still in, a 0.35-covered noon sky rendered at
    // median ΔE 0.00, visible only as a 1-in-255 lift of the frame mean.
    //
    // The fraction survives as a GATE (no cloud, no sheets) and nowhere else.
    //
    // The honest cost, recorded rather than hidden: over a solid overcast the sheets and §13's flat
    // dimming are both rendering the same deck, so the map is darker than either alone intends. That
    // is a real double-count and the first thing to fix if §25 goes past prototype — most likely by
    // feeding §13 a reduced opacity while the sheets draw, so the two partition the deck the way §23b
    // and §23 partition the underlight.
    // THE ILLUMINATION TERM IS NOT HERE, and moved out for §25b. It used to be — this returned
    // `amplitude * SheetBrightness(skyGlow)` — which was only correct while every cloud on the map
    // was equally lit. With decks, they are not: at 2.4 degrees below the horizon the low deck is in
    // shadow and the cirrus above it is in direct sun, so how bright a sheet is became a per-SHEET
    // question and cannot be answered by a per-map function. The overlay multiplies each sheet by
    // DeckIllumination for its own deck; what this returns is the lane's ceiling.
    public static float SheetAlpha(float cloudFraction, bool inVacuum) =>
        SheetAlphaWithAmplitude(cloudFraction, SheetAmplitude, inVacuum);

    public static float SheetAlphaWithAmplitude(float cloudFraction, float amplitude, bool inVacuum)
    {
        if (inVacuum)
            return 0f;

        if (!(amplitude > 0f))
            return 0f;

        return Clamp01(cloudFraction) <= 0f ? 0f : amplitude;
    }

    // How much a sheet's alpha and brightness are raised by other cloud stacked on it, and the ceiling
    // that raise is capped at. See CloudSheetLayout.OverlapDepth for where the depth comes from.
    public const float OverlapGain = 0.35f;

    public const float MaxOverlapBoost = 1.6f;

    // Where sheets overlap there is more cloud in the column, and more cloud seen FROM ABOVE is
    // brighter and denser — a thick cumulus top is the brightest thing in a daytime sky. So overlap
    // raises both the alpha and the brightness, which is the "partially additive" behaviour ordinary
    // alpha blending cannot produce on its own: blending converges on the sheet's own colour, so two
    // stacked sheets would otherwise look exactly like one slightly more opaque one.
    //
    // CAPPED, AND THE CAP IS THE POINT. Unbounded accumulation would make a busy sky a white slab —
    // the exact failure the tiled version had at full cover, arrived at from the other direction. 1.6
    // lets a genuine stack read as noticeably thicker while keeping the ceiling well inside what the
    // frame can show.
    public static float OverlapBoost(float overlapDepth)
    {
        if (overlapDepth <= 0f || float.IsNaN(overlapDepth))
            return 1f;

        float boost = 1f + OverlapGain * overlapDepth;
        return boost > MaxOverlapBoost ? MaxOverlapBoost : boost;
    }

    private static float Clamp01(float v)
    {
        if (float.IsNaN(v))
            return 0f;

        return v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
