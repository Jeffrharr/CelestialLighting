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
//
// That second species has now landed (DESIGN.md §20b, issue #83): AerosolScaleHeightMetres plus two
// accessors, no new exponential and no new <Compile Include> entry, exactly as the paragraph above
// predicted. The two species are only ever combined by the consumer that knows what it wants them
// for — §8's colour curve — never here, because "how much of species X is overhead" is a question
// with an answer per species and no meaningful sum.
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

    // Aerosol (dust, smoke, sea salt, industrial haze) scale height in metres — the second species
    // this file was shaped to hold, and the whole reason ColumnFraction takes the scale height as a
    // parameter rather than baking 8500 in. See DESIGN.md §20b.
    //
    // 1500 m is the standard boundary-layer figure. Aerosol is not a component of the air the way
    // nitrogen is: it is INJECTED at the ground (fires, dust, sea spray, industry) and continuously
    // removed by gravitational settling and rain-out, so its profile is set by that source/sink
    // balance near the surface rather than by the hydrostatic balance that gives bulk air its 8500 m.
    // Measured continental aerosol scale heights cluster around 1–2 km; 1500 m is the middle of that
    // band and is what atmospheric-optics texts use when they need one number.
    //
    // THE 5.7x IS THE POINT, NOT A SIDE EFFECT. 8500 / 1500 = 5.67, so the aerosol column decays
    // with altitude nearly six times faster than the bulk-air column does. That single ratio is what
    // makes this subsystem worth having rather than being "a second warm knob": at 4000 m the
    // Rayleigh column still has 0.62 of its sea-level value while the aerosol column is down to
    // 0.069, so a mountain base literally sits ABOVE the smog. Clean thin mountain air and hazy
    // polluted lowland end up at opposite ends of one continuous curve, with no threshold anywhere.
    public const float AerosolScaleHeightMetres = 1500f;

    // The aerosol column overhead as a fraction of the sea-level column, before any account of how
    // much aerosol the tile actually carries. Split out from AerosolLoadFraction below so the pure
    // altitude falloff — the half that owns the 5.7x divergence — can be reasoned about and pinned
    // on its own, without a pollution value confusing what is being measured.
    public static float AerosolColumnFraction(float siteAltitudeMetres) =>
        ColumnFraction(siteAltitudeMetres, AerosolScaleHeightMetres);

    // How much aerosol is actually overhead, in [0, 1]: the sea-level loading the tile carries,
    // thinned by however much of the boundary layer the observer has already climbed above.
    //
    // WHY POLLUTION MULTIPLIES RATHER THAN SETTING A SCALE HEIGHT. Biotech's Tile.pollution says how
    // much junk is in the air, not how it is distributed vertically — a lightly and a heavily
    // polluted tile both have their haze sitting in the same boundary layer. So loading scales the
    // column's magnitude and leaves its shape alone, which is also what keeps the altitude falloff
    // above independent of pollution and therefore separately testable.
    //
    // Clamped rather than trusted: pollution is a saved float, and worldgen-overhaul mods write it.
    // Everything downstream treats 1 as "the most aerosol this model knows how to mean" and is tuned
    // against that ceiling, exactly as ColumnFraction's own [0, 1] contract is.
    public static float AerosolLoadFraction(float siteAltitudeMetres, float tilePollution) =>
        AerosolColumnFraction(siteAltitudeMetres) * Clamp01(tilePollution);

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
}
