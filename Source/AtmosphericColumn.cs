using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file (same discipline as
// Formulas.cs and SkyColorTemperature.cs). It is linked into the test project via <Compile Include>
// so the exact code that ships is the exact code under test. The live read that turns a map into a
// site altitude in metres lives in SiteAltitude.cs; nothing here knows what a Map or a Tile is.
//
// WHAT THIS MODELS (DESIGN.md §20). How much of the atmosphere is left ABOVE an observer standing at
// a given altitude. The standard one-constant answer is the barometric/exponential atmosphere: a
// species whose density falls off with a scale height H leaves the fraction
//
//     exp(-siteAltitudeMetres / H)
//
// of its total column overhead. That is the whole model. It is an isothermal approximation — the
// real troposphere has a lapse rate, so the true profile is slightly steeper low down — but the
// error over the 0–5000 m band any RimWorld tile occupies is a couple of percent, well inside the
// tolerance of a colour curve that is itself an artistic linear ramp.
//
// WHY THE SCALE HEIGHT IS A PARAMETER AND NOT BAKED IN. Different scatterers have wildly different
// scale heights and this file is deliberately shaped to hold all of them. Rayleigh scattering is
// the air molecules themselves, so it follows the bulk density profile (H ≈ 8500 m — the value §8's
// reddening uses). Aerosols (dust, smoke, sea salt, haze) are injected near the ground and settle
// out, so they hug the surface with H ≈ 1500 m — a *different* column that falls away nearly six
// times faster with altitude, which is exactly why mountain air looks clean rather than merely
// thin. Expressing this as "column fraction given a scale height", with the Rayleigh height as one
// named caller rather than as the only function, is what lets a second species be added here as a
// second named constant + accessor instead of as a second copy of the exponential.
public static class AtmosphericColumn
{
    // Bulk-air (Rayleigh) scale height in metres: the altitude interval over which atmospheric
    // pressure falls by a factor of e. 8500 m is the standard textbook value for Earth's lower
    // atmosphere (kT/mg for dry air near 250 K); RimWorld planets are Earth-analogues down to the
    // biome list, so we use Earth's without apology rather than inventing a per-world constant that
    // nothing in worldgen could source.
    //
    // Sanity anchors this constant has to reproduce, and does: Denver at 1600 m -> 0.83 of sea-level
    // pressure, Lhasa at 3650 m -> 0.65, Everest at 8850 m -> 0.35. Those are the real measured
    // numbers, which is the point of preferring one physical constant over a hand-tuned ramp.
    public const float RayleighScaleHeightMetres = 8500f;

    // The shared model. Fraction of a species' total vertical column that remains ABOVE an observer
    // at siteAltitudeMetres, in (0, 1]. Callers name the scale height they mean.
    //
    // Sub-sea-level sites are clamped to the sea-level answer rather than allowed to exceed 1. The
    // physics does keep going (the Dead Sea shore at -430 m genuinely sits under ~1.05 atmospheres),
    // but every consumer of this value treats 1 as "the full, unmodified sea-level effect" and is
    // tuned against that ceiling — §8's tint strength multiplies straight into per-channel blend
    // maxima, so a fraction above 1 would push the sky blend past the strength the constants were
    // chosen for. A 5% over-pressure that only exists on tiles no RimWorld worldgen produces is not
    // worth giving up a hard [0, 1] contract for.
    public static float ColumnFraction(float siteAltitudeMetres, float scaleHeightMetres)
    {
        float altitudeAboveSeaLevel = siteAltitudeMetres < 0f ? 0f : siteAltitudeMetres;
        return MathF.Exp(-altitudeAboveSeaLevel / scaleHeightMetres);
    }

    // The Rayleigh column specifically, which is what §8's reddening scales with: Rayleigh optical
    // depth is proportional to the number of air molecules in the path, and for a slant path that
    // starts in space and TERMINATES at the observer that is proportional to the surface pressure at
    // the observer. The dense air below a mountain base is never crossed at all — see DESIGN.md §20
    // for why this is the whole physical claim of the subsystem.
    public static float RayleighPressureFraction(float siteAltitudeMetres) =>
        ColumnFraction(siteAltitudeMetres, RayleighScaleHeightMetres);
}
