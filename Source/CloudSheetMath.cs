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
    // degrees of solar elevation. Above the upper bound the deck is topped by direct sun and reads
    // white-grey; below the lower one the sun is under the horizon and only the underside is lit,
    // which is §23b's regime and the same warm/anti-solar colours it uses. In between the two mix,
    // which is what stops a hard recolour at the moment of sunset.
    public const float UnderlitFullDegrees = -1.5f;
    public const float UnderlitNoneDegrees = 6f;

    // How much of the sheet's colour comes from the UNDERSIDE being lit rather than the top, in
    // [0, 1]. 1 well below the horizon, 0 with the sun well up, smoothstepped between so neither end
    // has a crease.
    public static float UnderlitFraction(float elevationDegrees)
    {
        if (elevationDegrees <= UnderlitFullDegrees)
            return 1f;

        if (elevationDegrees >= UnderlitNoneDegrees)
            return 0f;

        float t = (UnderlitNoneDegrees - elevationDegrees) / (UnderlitNoneDegrees - UnderlitFullDegrees);
        return t * t * (3f - 2f * t);
    }

    // How bright the sheet is overall, in [NightBrightness, 1], as a function of how much light there
    // is to light it with. Driven by the map's own sky glow rather than by elevation, so it tracks
    // whatever §7/§13/§21 have already decided about how bright the world is — including an eclipse,
    // which should darken the clouds along with everything else and would not if this keyed on
    // geometry.
    public static float SheetBrightness(float skyGlow) =>
        NightBrightness + (1f - NightBrightness) * Clamp01(skyGlow);

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
    public static float SheetAlpha(float cloudFraction, float skyGlow, bool inVacuum) =>
        SheetAlphaWithAmplitude(cloudFraction, skyGlow, SheetAmplitude, inVacuum);

    public static float SheetAlphaWithAmplitude(
        float cloudFraction, float skyGlow, float amplitude, bool inVacuum)
    {
        if (inVacuum)
            return 0f;

        if (!(amplitude > 0f))
            return 0f;

        if (Clamp01(cloudFraction) <= 0f)
            return 0f;

        // Brightness scales the alpha as well as the colour, so a night sheet is both darker AND
        // sheerer. Drawing a dark sheet at full alpha over a night map would black the colony out
        // wholesale, which is a lighting change rather than a cloud.
        return amplitude * SheetBrightness(skyGlow);
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
