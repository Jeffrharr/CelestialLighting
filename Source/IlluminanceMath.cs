using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file (System only), same discipline
// as Formulas.cs and MoonMath.cs. Linked into both the shipped mod (net481) and the test project
// (net8.0) by a single <Compile Include>, so the model that runs in RimWorld is the model under test.
//
// This is the pure core of subsystem 6b (DESIGN.md §6b): absolute ground illuminance in lux, and the
// shadow contrast that follows from it.
//
// WHY THIS EXISTS. Every other brightness quantity in this mod is a normalized 0..1 scalar — sky
// glow, moonlight brightness, cloud opacity. That works fine while each subsystem only ever compares
// a value to itself, and breaks the moment two *different* light sources have to be compared to each
// other, because 0..1 throws away the four orders of magnitude that separate them. §6b needs exactly
// that comparison: whether a moon shadow is visible is not a fact about the moon, it is the ratio of
// the moon's light to everything else falling on the ground.
//
// Concretely, the numbers this file encodes (see the anchor table below): direct sun is ~100,000 lux,
// the sky at sunset ~400 lux, the end of civil twilight ~3.4 lux, and a full moon at zenith ~0.27 lux.
// A full moon's shadow at midday is therefore a 0.0003% dip in ground brightness — four orders of
// magnitude under the ~1% contrast a human eye resolves. The moon never stops casting a shadow; the
// sun drowns it. That is a statement about ratios, and it cannot be made in normalized units.
//
// The payoff is that a whole class of gating disappears. §6 used to decide "is there a moon shadow"
// with a hard branch on the sun being below the refraction horizon, which meant the moon shadow
// switched on at sun elevation -0.83 degrees, in a sky still ~1600x brighter than the moon. With a
// ratio there is no gate to place: the shadow is always computed, and it is invisible in daylight for
// the same reason it is invisible in reality.
public static class IlluminanceMath
{
    // --- Ambient ground illuminance from the sky, as a function of sun elevation ---
    //
    // Horizontal illuminance at ground level in lux, sun + sky combined, for a clear atmosphere.
    // Anchors are the standard published values for the named lighting conditions; everything between
    // them is interpolated in LOG space, because that is the shape twilight actually has (brightness
    // falls roughly a decade per 3-4 degrees of solar depression through the twilight band, not
    // linearly). Interpolating these linearly would put the -3 degree sky at ~200 lux instead of ~37,
    // and the moon shadow would fade in visibly too late as a result.
    //
    // Clear-sky only, deliberately. Cloud does not belong in this curve: a cloud deck darkens the
    // ground AND destroys the direct beam that casts the shadow in the first place, and those are two
    // different effects that would partially cancel if both were folded in here. §13's
    // WeatherDimmingMath.ShadowContrastFactor already owns the beam half and is applied by
    // Patch_ShadowStrength to whatever this file returns, so the split stays clean: this file answers
    // "how bright is a clear sky", §13 answers "how much of the beam survives the clouds".
    //
    // The anchors, and where they come from:
    //   +90  120000  sun at zenith, clear
    //   +30   60000  mid-morning
    //   +10   18000  sun well up, an hour or so after sunrise
    //    +5    8000  low sun, strong atmospheric extinction setting in
    //     0     400  sun geometrically at the horizon — sunrise/sunset
    //    -6     3.4  end of CIVIL twilight (the standard 3.4 lux definition)
    //   -12   0.008  end of NAUTICAL twilight
    //   -18   0.001  end of ASTRONOMICAL twilight: the natural moonless night sky
    //                (starlight + airglow). This is a FLOOR, not a slope — the sky stops getting
    //                darker once the sun's contribution is gone, so anything below -18 clamps here.
    private static readonly float[] AnchorElevationDegrees = { -18f, -12f, -6f, 0f, 5f, 10f, 30f, 90f };
    private static readonly float[] AnchorLux = { 0.001f, 0.008f, 3.4f, 400f, 8000f, 18000f, 60000f, 120000f };

    // The moonless night-sky floor, exposed by name because two other things are stated against it:
    // the contrast a given moon achieves in full darkness, and the fact that a thin crescent is
    // genuinely dimmer than starlight and so casts nothing at all.
    public static float NightSkyFloorLux => AnchorLux[0];

    // Illuminance a clear sky puts on the ground for this sun elevation, in lux. Clamps to the night
    // floor below the last anchor and to full midday sun above the first, so no caller has to bounds
    // check a sun that has gone round the far side of the planet.
    public static float AmbientSkyLux(float sunElevationDegrees)
    {
        int last = AnchorElevationDegrees.Length - 1;
        if (sunElevationDegrees <= AnchorElevationDegrees[0])
            return AnchorLux[0];
        if (sunElevationDegrees >= AnchorElevationDegrees[last])
            return AnchorLux[last];

        int i = SegmentIndexFor(sunElevationDegrees);
        float span = AnchorElevationDegrees[i + 1] - AnchorElevationDegrees[i];
        float t = (sunElevationDegrees - AnchorElevationDegrees[i]) / span;
        float logLux = Lerp(MathF.Log10(AnchorLux[i]), MathF.Log10(AnchorLux[i + 1]), t);
        return MathF.Pow(10f, logLux);
    }

    // Index of the anchor segment containing this elevation. Written as a scan that keeps the last
    // qualifying index rather than a scan that breaks out, per the house rule on loop exits — the
    // table has eight entries and is read a handful of times per frame, so the cost of not stopping
    // early is nil and the reader has no mid-loop exit to hold in their head.
    private static int SegmentIndexFor(float elevationDegrees)
    {
        int index = 0;
        for (int i = 0; i < AnchorElevationDegrees.Length - 1; i++)
        {
            if (AnchorElevationDegrees[i] <= elevationDegrees)
                index = i;
        }
        return index;
    }

    // --- Moonlight ---

    // Ground illuminance from a full moon at zenith, in lux. Published values span 0.05-0.36 depending
    // on lunar distance, altitude and atmospheric clarity; 0.267 is the usual clear-sky, mean-distance
    // figure and sits in the middle of that range.
    public const float FullMoonZenithLux = 0.267f;

    // How moon brightness falls off with phase. NOT linear in illuminated fraction, which is the
    // single most common way to get moonlight wrong: a first-quarter moon shows half a disc but is
    // roughly a TWELFTH as bright as a full one, not half. Two real effects stack to produce that —
    // the opposition surge (the moon is retroreflective, so it brightens sharply as the phase angle
    // approaches zero) and shadowing between surface relief at oblique illumination.
    //
    // The exponent is derived, not tuned: full moon is apparent magnitude -12.74 and first quarter
    // -10.0, a difference of 2.74 magnitudes == a factor of 10^(2.74/2.5) == 12.4x. Solving
    // 0.5^k == 1/12.4 gives k == 3.63; 3.5 is that rounded, which puts first quarter at 1/11.3 of
    // full. Anything in the 3.3-3.7 range reproduces the published magnitudes about equally well.
    public const float MoonPhaseExponent = 3.5f;

    // Ground illuminance from the moon, in lux. Phase falls off by the exponent above; altitude
    // scales by sin(elevation), the standard cosine-of-incidence-angle term for light landing on a
    // horizontal surface. Zero once the moon is below the same refraction-adjusted horizon the sun
    // uses, so the moon and sun can never disagree about where the horizon is.
    public static float MoonLux(float illuminatedFraction, float moonElevationDegrees)
    {
        if (moonElevationDegrees <= Formulas.AtmosphericRefractionDegrees)
            return 0f;

        float phase = MathF.Pow(Clamp01(illuminatedFraction), MoonPhaseExponent);
        float altitude = Clamp01(MathF.Sin(ToRadians(moonElevationDegrees)));
        return FullMoonZenithLux * phase * altitude;
    }

    // --- Contrast ---

    // The fraction by which a caster's shadow darkens the ground, in [0,1]: 0 is invisible, 1 is a
    // shadow that removes all the light there is.
    //
    // Shadowed ground receives the ambient (everything except this caster's direct beam); lit ground
    // receives ambient + caster. The ratio between them is caster / (caster + ambient), which is the
    // whole physical content of §6b. It has the two limits you want for free: a caster far brighter
    // than the ambient approaches 1 (a hard shadow), and a caster far dimmer approaches 0 (washed
    // out) without any threshold or gate deciding where that happens.
    //
    // Returns 0 when there is no light at all, which is the honest answer — you cannot see a shadow
    // in a room with the lights off either.
    public static float ShadowContrast(float casterLux, float ambientLux)
    {
        float total = casterLux + ambientLux;
        if (total <= 0f)
            return 0f;

        return Clamp01(casterLux / total);
    }

    private static float ToRadians(float degrees) => degrees * MathF.PI / 180f;
    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
