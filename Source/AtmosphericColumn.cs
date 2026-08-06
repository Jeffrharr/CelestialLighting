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
// predicted. The two species (Rayleigh, aerosol) are only ever combined by the consumer that knows
// what it wants them for — §8's colour curve — never here, because "how much of species X is
// overhead" is a question with an answer per species and no meaningful sum ACROSS species.
//
// Aerosol itself later split into two SOURCES sharing one species (DESIGN.md §20e, issue #92):
// Biotech pollution and a biome-keyed natural background (sea salt, dust, biogenic organics). Unlike
// Rayleigh vs. aerosol, these two share both a scale height and a physical unit, so — unlike the
// cross-species case above — they ARE summed here, inside AerosolLoadFraction, before the ceiling
// clamp and the altitude falloff. See that method for why.
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
    // BACKGROUND AEROSOL (DESIGN.md §20e, issue #92) IS ADDED HERE, NOT MULTIPLIED IN LATER. It is
    // summed with pollution BEFORE the ceiling clamp and BEFORE the altitude falloff is applied, for
    // two reasons that both follow from what background aerosol physically is:
    //
    //   1. It is measured in the SAME units as pollution loading — a sea-level optical-depth fraction
    //      — because both are boundary-layer species being asked the same question ("how much of this
    //      is in the air"), just from different sources (industrial haze vs. sea salt/dust/biogenic
    //      particles). Optical depths from independent sources in the same layer add; that is Beer-
    //      Lambert linearity, the same property the mired composition elsewhere in this subsystem
    //      leans on.
    //   2. It shares pollution's scale height. Natural background aerosol is exactly as much a
    //      boundary-layer phenomenon as industrial haze — sea salt, lofted dust and biogenic organics
    //      all settle out within a few kilometres of the surface — so it belongs inside the SAME
    //      exp(-h/1500) falloff rather than a second one. A mountain base sits above natural haze for
    //      the identical reason it sits above polluted haze.
    //
    // Clamped rather than trusted: pollution is a saved float, and worldgen-overhaul mods write it.
    // Background is derived rather than saved, so it cannot itself go out of range, but it is clamped
    // on the same footing for symmetry and because a future edit to BackgroundAerosolFraction should
    // not have to remember this file's invariant. Everything downstream treats 1 as "the most aerosol
    // this model knows how to mean" and is tuned against that ceiling, exactly as ColumnFraction's own
    // [0, 1] contract is — so the SUM is what gets clamped to it, not each term separately, which is
    // what lets a lightly polluted tile with a high natural background still read as more aerosol than
    // either term alone.
    //
    // backgroundLoadFraction is REQUIRED rather than defaulted to 0, on the same reasoning §18's
    // inVacuum parameter is never defaulted: a silent 0 here would quietly reintroduce the exact bug
    // issue #92 exists to fix at the next call site someone adds, with nothing to catch it.
    public static float AerosolLoadFraction(
        float siteAltitudeMetres, float tilePollution, float backgroundLoadFraction)
    {
        float seaLevelLoad = Clamp01(tilePollution) + Clamp01(backgroundLoadFraction);
        return AerosolColumnFraction(siteAltitudeMetres) * Clamp01(seaLevelLoad);
    }

    // --- background aerosol: natural sources with no Biotech gate (DESIGN.md §20e, issue #92) ---
    //
    // THE PROBLEM THIS SOLVES. AerosolLoadFraction above keyed the ENTIRE aerosol column on
    // Tile.pollution, so pollution = 0 meant aerosolFraction = 0 — a completely aerosol-free
    // atmosphere. That is every tile in a game without Biotech, and most tiles in a game with it.
    // Physically wrong (background aerosol optical depth at 550 nm runs ~0.02-0.05 in genuinely
    // pristine air and ~0.05-0.15 in ordinary continental air, from sea salt, mineral dust, biogenic
    // organics and seasonal wildfire smoke — never zero over land), and it also meant every subsystem
    // keyed off this same fraction (§20c's day-to-day drift, §20d's Angstrom hue shape) was multiplying
    // by zero on the map most players actually have: a multiplier on nothing is nothing, however
    // interesting the multiplier's own math is.
    //
    // WHY IT IS DERIVED FROM RAINFALL, REUSING §20D'S BREAKPOINTS RATHER THAN NAMING NEW ONES. The
    // issue that motivated this asked for a biome-keyed background term that also supplies §20d's
    // Angstrom exponent with its size classification — "one lookup, two outputs" — written when §20d
    // had not yet shipped. §20d landed first (issue #86, as AerosolSpectrum.AngstromExponentForRainfall)
    // and already made that lookup: Tile.rainfall against vanilla's own ExtremeDesert (340 mm) and
    // TropicalRainforest (2000 mm) cutoffs, the same axis vanilla's own BiomeWorkers score biomes on.
    // Reusing AerosolSpectrum.DriestRainfallMillimetres/WettestRainfallMillimetres here — rather than
    // pasting a second copy of 340/2000 — is what makes this the "two outputs" half of that one lookup
    // rather than a second, independently-tunable classification that could drift out of step with it.
    //
    // THE SHAPE: a shallow valley, lowest at the rainfall midpoint and rising toward both breakpoints.
    // The direction is the physically defensible part, in the same spirit AerosolSpectrum states its
    // own rainfall ramp's direction is: arid ground with nothing binding it is a natural dust SOURCE
    // (the same Saharan/Arabian-belt dust that supplies real-world background AOD), and wet, densely
    // vegetated ground is a natural biogenic-organic SOURCE (forest-emitted secondary organic aerosol
    // is a measurable contributor to real background haze). Land in between, source of neither, carries
    // the lower "ordinary continental" figure with no strong local source topping it up. The exact
    // curve SHAPE is a first-order approximation, like every other ramp in this file family — nothing
    // in RimWorld worldgen supplies the data a fitted curve would need — but the direction is the claim
    // this section stands on, and it is what the tests pin.
    //
    // WHERE THE MAGNITUDES COME FROM. Both AOD figures are the issue's own cited ranges, converted to
    // this model's [0, 1] scale by dividing by BackgroundAerosolAodAnchor — the same "~0.3" heavy-urban
    // vertical AOD that §20b's own DESIGN.md text uses as its anchor for what aerosolFraction = 1
    // physically means. That anchor was always approximate ("an AOD that can exceed 0.3") and this
    // reuses it rather than inventing a second one, so background and pollution loading are read off
    // the same physical yardstick even though neither is a literal radiative-transfer quantity.
    public const float BackgroundAerosolAodAnchor = 0.3f;

    // Midpoint of the issue's quoted 0.02-0.05 pristine-air range, at 550 nm.
    private const float PristineBackgroundAod = 0.035f;

    // Midpoint of the issue's quoted 0.05-0.15 ordinary-continental-air range, at 550 nm.
    private const float ContinentalBackgroundAod = 0.10f;

    // The valley floor: the rainfall midpoint between the two vanilla breakpoints, where neither a
    // dust source nor a biogenic source dominates.
    public const float PristineBackgroundFraction = PristineBackgroundAod / BackgroundAerosolAodAnchor;

    // The valley rim: reached at EITHER vanilla breakpoint, where one source or the other dominates.
    public const float ContinentalBackgroundFraction = ContinentalBackgroundAod / BackgroundAerosolAodAnchor;

    // Sea-level background aerosol loading for a tile, from its annual rainfall in millimetres, before
    // the altitude falloff AerosolLoadFraction applies. In [PristineBackgroundFraction,
    // ContinentalBackgroundFraction] — never 0, which is the entire point: this is what makes
    // "clean air" (pollution = 0) stop meaning "aerosol-free air".
    public static float BackgroundAerosolFraction(float rainfallMillimetres)
    {
        float wetness = InverseLerpClamped(
            AerosolSpectrum.DriestRainfallMillimetres, AerosolSpectrum.WettestRainfallMillimetres,
            rainfallMillimetres);

        // Distance from the rainfall midpoint, in [0, 1]: 0 exactly halfway between the two vanilla
        // breakpoints, 1 at either breakpoint itself. This is what turns one monotonic ramp (wetness)
        // into the valley shape described above, reusing the same two named endpoints §20d already
        // reads rather than adding a third.
        float distanceFromMidpoint = MathF.Abs(2f * wetness - 1f);

        return Lerp(PristineBackgroundFraction, ContinentalBackgroundFraction, distanceFromMidpoint);
    }

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float InverseLerpClamped(float a, float b, float v) => Clamp01((v - a) / (b - a));
}
