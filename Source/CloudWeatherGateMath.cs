namespace CelestialLighting;

// DESIGN.md §25f: which weathers this mod draws cloud in, and what a change of weather does to the
// cloud already on screen. Pure — no `Verse`, no `UnityEngine`, primitives in and out — so the one
// rule that decides whether a sky has clouds at all can be tested without booting a game.
//
// THE RULE IS "OUR CLOUD IS A CLEAR-SKY PHENOMENON". §25 draws bounded sheets: discrete objects with
// edges and gaps between them, which is what a partly-cloudy sky looks like out of a window. An
// overcast sky is not that — it is a lid, with no edges to see — and §13 already renders it, by
// dimming and desaturating the whole map. So a weather that carries a deck hands the sky to §13 and
// this lane draws nothing in it.
//
// WHAT THAT REPLACES, AND WHY IT IS NOT A LOSS. The deck used to feed §25 as well: cover was
// `deckOpacity + clearShare x clearCover`, so settled Rain placed the full cap of sheets at cover
// 1.0. §25's own section already recorded what that cost — over a solid overcast the sheets and
// §13's flat dimming are both rendering the same deck, so the map came out darker than either
// subsystem intends, and that double-count is why `SheetAmplitude` had to be held down to 0.35. This
// resolves it by partition rather than by attenuation: below a deck, exactly one of the two lanes is
// live, so neither has to be detuned to make room for the other.
//
// THE FADE IS APPLIED TO EVERY SHEET'S ALPHA, NOT TO THE COVER. Vanilla cross-fades one weather into
// the next over 4,000 ticks (`WeatherManager.TransitionLerpFactor`, ~1.6 in-game hours) and
// `ClearShare` below rides that same factor, so there is a single number per tick saying how much of
// the sky is still the clear one. Where that number is applied is the whole of whether the result
// reads as a fade, and the first cut applied it in the wrong place — see `FadedCoverage` for the
// measurement. Scaling the COVER fades the sky by shedding clouds one at a time off the top of the
// count, because cover is a count (`CloudSheetLayout.CoverageAlpha`): the marginal sheet thins out
// while the first few sit at full opacity until the very end of the transition, so some of the
// clouds visibly fade and the rest just wait their turn. Multiplying every placed sheet's alpha by
// the one share instead fades the whole sky at one rate, which is what a sky handing over to a front
// actually does. Clearing up runs the same line backwards and fades them in.
//
// WHY THIS IS NOT LATCHED PER SHEET, unlike §22's cover. See CloudSheetDraw's header for the general
// rule: a sheet reads §22's drift at the tick it entered the map, so a drifting cover cannot add or
// remove a cloud in view. Weather is the deliberate exception, because it is global and abrupt — a
// sheet that latched "it was Clear when I arrived" would go on being a fair-weather cloud in the
// middle of a storm, for up to a whole crossing. So the share below is always read live, and a front
// reaches every sheet at once.
public static class CloudWeatherGateMath
{
    // How much of vanilla's current weather cross-fade is a Clear sky, in [0, 1] — and therefore how
    // much of this mod's cloud lane is allowed to exist right now.
    //
    // 1 in settled Clear weather, 0 in any settled weather that is not Clear, and the transition
    // lerp (or its complement) while vanilla is between the two. The two booleans are asked by the
    // caller rather than derived here because "is this def Clear" is a `WeatherDefOf` comparison,
    // which is exactly the live-state read this file exists not to contain.
    //
    // BOTH ARMS ARE SUMMED RATHER THAN PICKED, which matters for the Clear-to-Clear case vanilla
    // reaches whenever it re-rolls the same weather: both booleans are true, the two terms are
    // `1 - lerp` and `lerp`, and they add to exactly 1 at every point of the transition. Selecting
    // one arm would have made a re-roll of Clear dip the sky to `lerp` and back for no reason a
    // player could see a cause for.
    public static float ClearShare(float transitionLerp, bool lastIsClear, bool curIsClear)
    {
        float lerp = Clamp01(transitionLerp);

        float share = 0f;
        if (lastIsClear)
            share += 1f - lerp;

        if (curIsClear)
            share += lerp;

        return share;
    }

    // How much cloud is effectively up over this map: §22's cover for the air, scaled by how much of
    // the sky is currently a Clear sky at all. This is the scalar the LANE-level gates and the probes
    // read — "is there cloud at all, and how much" — not the per-sheet weight, which is FadedCoverage
    // below.
    //
    // A PRODUCT RATHER THAN A MINIMUM OR A GATE, because the share is a fraction of the *sky*, not a
    // threshold on it. Halfway into a front, half the sky's character is still the clear day that is
    // leaving. A gate (`share > 0.5 ? cover : 0`) would put back the very pop §25 spent a section
    // removing, one step later than where it was found.
    public static float CoverFromShare(float clearShare, float clearCover) =>
        Clamp01(Clamp01(clearShare) * Clamp01(clearCover));

    // What one placed sheet is actually worth this frame: its own coverage weight — decided once, at
    // the tick it came over the edge of the map, and constant for as long as it is visible — times
    // the live Clear share.
    //
    // THIS IS WHERE THE FADE LIVES, AND IT IS NOT WHERE IT STARTED. §25f's first cut multiplied the
    // share into the cover and let `CoverageAlpha` decompose the result, which is the natural-looking
    // thing to do and is wrong for a reason only a measurement shows. Cover is a COUNT: sheet i is
    // present in proportion to `cover x cap - i`, so scaling cover walks the population down one
    // sheet at a time from the top while sheet 0 sits pinned at full opacity until the share has
    // fallen below `1 / (cap x cover)` — the last tenth of the transition on a cloudy day. Measured
    // over a Clear-to-Rain front at cover 0.39, the placed count went 5, 5, 5, 5, 5, 4, 4, 4, 3, 3,
    // 3, 3, 2, 2, 2, 1, 1, 1, 1, 0: a staircase, in which the clouds nearest the top of the count
    // faded and the rest held station and then left. "They do fade now, but maybe not all of them" is
    // exactly what that looks like from the camera, and it is not what a front does.
    //
    // Applied to the alpha, the same share fades every sheet on the map at the same rate at the same
    // time, and the population is left to the entry latch alone — which is also what that latch was
    // for. Nothing appears or disappears in view; the whole sky just thins out and hands over.
    //
    // WHY THE SHARE IS NOT ALSO REMOVED FROM CoverFromShare ABOVE. The two answer different
    // questions. This one is a per-sheet weight, and the scalar is a lane gate — "does this lane draw
    // at all" — which has to read zero in settled rain or the gate stops being a gate. §23b and §23c
    // therefore see the share in both their lane ceiling and their per-sheet weight, and so fade
    // quadratically where §25 fades linearly. Accepted rather than plumbed around: both ship off,
    // both are light cast BY these sheets rather than the sheets themselves, and light that fades a
    // little ahead of the cloud casting it is not a thing anybody can point at.
    public static float FadedCoverage(float coverageAlpha, float clearShare) =>
        Clamp01(Clamp01(coverageAlpha) * Clamp01(clearShare));

    private static float Clamp01(float value)
    {
        if (!(value > 0f))
            return 0f;

        return value < 1f ? value : 1f;
    }
}
