namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file, the same discipline
// Formulas.cs / PenumbraMath.cs / VacuumRadianceMath.cs follow. Linked into both the shipped mod
// (net481) and the test project (net8.0) by a single <Compile Include>, so the model that runs in
// RimWorld is the model under test.
//
// Subsystem 18c (DESIGN.md §18c): WHAT FILLS A SHADOW.
//
// A cast shadow is never black, and the reason is not geometry. Stand in one on the ground at noon
// and you are still lit — by the whole dome of scattered sky above you. That fill is the entire
// content of vanilla's `SkyTarget.colors.shadow`: Clear's daylight value is a faintly blue grey
// (0.718, 0.745, 0.757), i.e. a shadow keeps 74% of the lit ground's brightness, and the blue is
// Rayleigh scattering turning up in a colour channel.
//
// Take the air away and that number has no cause left. §18c is therefore a split, and the split is
// the whole subsystem:
//
//   KEPT, untouched   The geometric penumbra (PenumbraMath). The sun subtends ~0.53 degrees whether
//                     or not there is air in the way, so the widening of the penumbra as shadows
//                     lengthen is identical in vacuum. Nothing in this file takes an elevation and
//                     nothing in PenumbraMath takes an inVacuum — the two are orthogonal by
//                     construction, and ShadowFillMathTests pins that both ways round.
//
//   DROPPED           The umbra floor (skylight fill) and the sky-palette tint. Both are the
//                     atmosphere's contribution and both leave with it.
//
// WHAT ACTUALLY FILLS A VACUUM UMBRA — the question §18c had to answer rather than assume, because
// the intuitive answer is wrong. "Planetshine" is the obvious candidate: the lit planet fills 94% of
// the sky below an orbital platform, and during the DAY the term is enormous. §18b's PlanetshineLux
// is 33,000 lux under a zenith sun, 27.5% of the ground illuminance, which would put the vacuum
// umbra keep at ~0.27 and leave orbital shadows only modestly harsher than sea level's 0.74.
//
// It does not reach the deck, and the reason is the platform itself. RimWorld's orbits are
// stationary (epic #8), so a platform hangs over a fixed lat/long and its deck faces that tile's
// zenith — exactly the direction away from the planet; the sun rises and sets across that deck the
// way it does on the ground below. The planet is therefore under the FLOOR. Every cell on the deck
// has the platform's own structure between it and the planet, so planetshine lands on the underside
// and nowhere else. A shadow on the deck is filled by what an upward-facing surface in vacuum can
// actually see, which is the star field (unextinguished — §18b's term that goes UP) and the moon.
//
// Which is precisely §18b's published night floor, and that is why this file CONSUMES
// NightRadiance.FloorGlowFor(map) rather than deriving anything: "how dark can the sky over this map
// get" and "what does a shadow here bottom out at" are one physical question asked from two
// directions, and §18b already answers it. (§18b's floor does carry a planetshine term, evaluated at
// astronomical twilight where it is 0.00048 glow — 1.5% of the floor. The occlusion argument above
// applies to it too, but at that size it moves no digit anyone can see, and the floor is §18b's to
// define. DESIGN.md §18c records the finding for whoever evaluates PlanetshineLux above the horizon
// next.)
//
// AND SO THE VACUUM UMBRA IS NEUTRAL GREY. Not because grey is the safe choice, but because both
// surviving sources are neutral: an integrated star field is very nearly white, and moonlight is
// grey. There is no sky palette left to take a hue from, and the reflected-planet term that would
// have supplied one is the term the deck cannot see. §6a already writes a neutral grey at night for
// the adjacent reason (§9's low-light desaturation owns the night's colour cast), so this agrees
// with the mod's existing answer instead of inventing a second one.
//
// The visible result is a lit/shadow contrast an order of magnitude harsher than anything else the
// mod renders. That is correct and is deliberately not softened back for taste; DESIGN.md §18c
// states the one clamp that IS a softening and why it is a bound rather than a preference.
public static class ShadowFillMath
{
    // --- Sea level: the skylight fill, unchanged ---
    //
    // Vanilla Clear's shipped 1.6 `skyColorsDay.shadow`. It lives here rather than in the patch that
    // writes it so both regimes' anchors sit side by side in one pure file and the tests can pin them
    // as a diverging pair — a regression in either shows up against the other rather than in
    // isolation. Patch_WeatherShadowColor still prefers the LIVE WeatherDefOf.Clear value at runtime
    // (so a retuned vanilla Clear is followed rather than drifted away from); these are its fallback
    // for the window before defs load, and the fixed anchor the unit tests pin.
    public const float SeaLevelUmbraR = 0.718f;
    public const float SeaLevelUmbraG = 0.745f;
    public const float SeaLevelUmbraB = 0.757f;

    // The fraction of lit-ground brightness a sea-level daytime umbra keeps: 0.740, a 26% darkening.
    // Rec. 709 luminance of the tint above, computed through EaveShadeMath.ShadowKeep so there is
    // exactly one copy of those weights in the codebase and §15b's eave shade and §18c's umbra can
    // never disagree about what "how much light does this shadow leave" means.
    public static readonly float SeaLevelUmbraKeep =
        EaveShadeMath.ShadowKeep(SeaLevelUmbraR, SeaLevelUmbraG, SeaLevelUmbraB);

    // --- The gate ---

    // The colour to write into SkyTarget.colors.shadow while the sun is up: the sky's own fill where
    // there is a sky, the night light budget where there is not.
    //
    // Shaped to §18's convention (Source/Vacuum.cs): `inVacuum` is the LAST parameter, it is required
    // rather than defaulted, and the vacuum value returns before any atmospheric term is read. Note
    // what that shape buys here beyond consistency — the three skyFill arguments are simply NOT
    // CONSULTED in the vacuum arm, so "drop the sky tint" is expressed structurally rather than by a
    // comment promising it.
    public static UmbraFill DaytimeUmbraFill(
        float skyFillR, float skyFillG, float skyFillB,
        float nightFloorGlow, float litGlow, bool inVacuum)
    {
        if (inVacuum)
        {
            float keep = VacuumUmbraKeep(nightFloorGlow, litGlow);
            return new UmbraFill(keep, keep, keep);
        }

        return new UmbraFill(skyFillR, skyFillG, skyFillB);
    }

    // --- Vacuum: no fill but the night budget ---

    // How much of the lit ground's brightness a vacuum umbra keeps, in [0, 1].
    //
    // `nightFloorGlow` is NOT defined here and must not be. It is §18b's published floor, read live
    // through NightRadiance.FloorGlowFor(map) the same way §9 reads WeatherDimming.DimmingFor(map):
    // one value, several consumers, and deliberately not one patch leaving a number behind for
    // another patch to find. If this file grew its own vacuum floor constant, "what a shadow bottoms
    // out at" and "how bright is night" would be free to drift apart, and they are physically the
    // same quantity.
    //
    // WHY THE DIVIDE. colors.shadow is a MULTIPLY on ground the sky has already dimmed — SkyManager
    // renders `Color.Lerp(white, colors.shadow, strength)` over an already-lit scene. So writing the
    // floor straight in would not mean "the umbra bottoms out at the night floor"; it would mean "the
    // umbra is the night floor TIMES the current daylight", which at a low sun lands several times
    // darker than an actual vacuum night — a shadow darker than the darkness. Dividing by the lit
    // glow converts the absolute floor into the relative multiply the renderer wants, and does it
    // with no tuned constant: at full daylight the umbra keeps exactly the floor, and as the lit
    // ground dims toward the floor the ratio climbs toward 1 on its own.
    //
    // THE CLAMP AT 1 is the one place §18c softens anything, and it is a bound rather than a taste
    // call: with the fill at or above the direct beam there is no contrast left to draw, and a keep
    // above 1 would render a shadow BRIGHTER than the ground beside it.
    public static float VacuumUmbraKeep(float nightFloorGlow, float litGlow)
    {
        float floor = nightFloorGlow < 0f ? 0f : nightFloorGlow;

        // Ambient >= direct: no shadow to draw. Also the guard that keeps the divide safe — a litGlow
        // of 0 is only reachable from here with a floor of 0 too, and 0 >= 0 returns first.
        if (litGlow <= floor)
            return 1f;

        return Clamp01(floor / litGlow);
    }

    // The fraction of lit-ground brightness the deepest part of a shadow actually RENDERS at, given
    // the umbra keep above and the shadow strength Patch_ShadowStrength hands the shader. This is
    // SkyManager's own `Color.Lerp(Color.white, colors.shadow, strength)` written as a scalar, and it
    // exists so the tests can assert the visible outcome of the two halves together rather than each
    // in isolation — the same reason EaveShadeMath.EaveBrightness exists.
    //
    // Note what is NOT an argument: elevation. Everything elevation-dependent about a shadow lives in
    // `shadowStrength` (Formulas.ShadowIntensityFromElevation times PenumbraMath's contrast factor),
    // and that term is identical in vacuum and at sea level. The whole of §18c is in the other one.
    public static float RenderedUmbraKeep(float umbraKeep, float shadowStrength) =>
        Clamp01(1f - Clamp01(shadowStrength) * (1f - Clamp01(umbraKeep)));

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
}

// The RGB a daytime umbra is drawn at. A plain value type rather than three out-parameters, so the
// one gated entry point above can return a colour without this file taking the UnityEngine
// dependency it is defined by not having.
public readonly struct UmbraFill
{
    public readonly float R;
    public readonly float G;
    public readonly float B;

    public UmbraFill(float r, float g, float b)
    {
        R = r;
        G = g;
        B = b;
    }
}
