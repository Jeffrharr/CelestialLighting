using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file (the same discipline as
// Formulas.cs, SkyColorTemperature.cs, AtmosphericColumn.cs and OzoneTwilightMath.cs). It is linked
// into the test project via <Compile Include> so the exact code that ships is the exact code under
// test. The live read that turns a map into an Angstrom exponent lives in SiteAltitude.cs; nothing
// here knows what a Map or a Tile is.
//
// WHAT THIS MODELS (DESIGN.md §20c, issue #86). The SHAPE of aerosol extinction, as opposed to its
// magnitude (§20b's aerosol column) or its altitude falloff (§20's scale-height model).
//
// THE PROBLEM IT EXISTS TO SOLVE. Everything §8 had before this file was a ONE-PARAMETER FAMILY: a
// ramp along the Planckian locus from ~5772 K down to a warm endpoint. §20 (site altitude) and §20b
// (pollution) each added an input, but both of them only changed HOW FAR ALONG that single curve the
// sky travelled. The hue PATH never changed, so every sunset on every map was the same march toward
// the same orange, only more or less of it. Real sunsets are yellow, salmon, brick, brown and
// crimson, and reaching any of those means leaving the locus entirely.
//
// THE MECHANISM THAT PRODUCES THE VARIETY. Rayleigh scattering has one fixed spectral shape, lambda^-4,
// which is exactly why it can be summarised by a single colour temperature at all. Aerosol has no
// fixed shape. Its wavelength dependence is described by the ANGSTROM EXPONENT alpha in
//
//     tau(lambda) = tau_ref * (lambda / lambda_ref)^-alpha
//
// and alpha varies enormously with PARTICLE SIZE, because the size parameter (2*pi*r/lambda) is what
// decides whether a particle scatters selectively at all:
//
//   alpha ~0        large particles — fog droplets, sea spray, thick blowing dust. The particles are
//                   far bigger than every visible wavelength, so they scatter all of them alike:
//                   GREY extinction, a white sun that merely dims. This is the case the locus model
//                   could not produce AT ALL, because on the locus "more aerosol" necessarily means
//                   "more reddening".
//   alpha 0.5-1.0   coarse mineral dust — the brown/ochre Saharan-dust sunset.
//   alpha 1.2-1.7   urban/industrial haze — the classic deep orange-red. This band is where §20b's
//                   single shipped colour implicitly sat (see ReferenceAngstromExponent below).
//   alpha 2.0+      fine smoke, stratospheric sulfate — strong short-wavelength removal, a deep
//                   saturated red.
//   alpha 4         Rayleigh itself, the clean-air limit. Included as the reference case that lets
//                   this model be cross-checked against §8's own curve rather than only against
//                   itself (see the tests).
//
// Because the extinction SHAPE differs from lambda^-4, the transmitted spectrum is not a blackbody at
// any temperature, which is the whole point: one extra scalar takes the model off the locus.
//
// THE FILE PRECEDENT THIS FOLLOWS is OzoneTwilightMath.cs, which is this codebase's existing example
// of the same structural move — modelling a species by PER-CHANNEL TRANSMISSION rather than by a
// colour temperature, because its spectrum is not one a single temperature can summarise. That file
// reached for it because ozone cuts a notch; this one reaches for it because aerosol's slope varies.
// The two files deliberately sample the SAME three wavelengths so the two species are described in
// one consistent basis (see the wavelength constants below).
public static class AerosolSpectrum
{
    // --- the spectral basis ---
    //
    // The three sRGB primary sample wavelengths, in nanometres. These are deliberately the SAME three
    // OzoneTwilightMath samples its Chappuis cross-sections at, and for the same reason: three
    // samples is what an RGB pipeline can carry, and using one basis for both species means the two
    // are directly comparable rather than each being right in its own private units.
    //
    // They are channel CENTRES rather than the sRGB primaries' peak wavelengths, which is the honest
    // reading of "evaluate tau at the channel" — a channel integrates a band, and its centre is the
    // single-sample stand-in for that integral.
    public const float RedWavelengthNm = 600f;
    public const float GreenWavelengthNm = 550f;
    public const float BlueWavelengthNm = 450f;

    // The wavelength tau is quoted AT. Green, for two reasons: it is the channel the eye is most
    // sensitive to, so the optical depth number means roughly "how much the scene dims"; and it puts
    // the red and blue exponents either side of 1, so the (lambda/lambda_ref)^-alpha factors read as
    // "less than green" and "more than green" rather than all pointing one way.
    //
    // The choice is not physically load-bearing — moving lambda_ref only rescales tau_ref by a
    // constant, and HorizonOpticalDepth below is calibrated against whatever it is set to.
    public const float ReferenceWavelengthNm = GreenWavelengthNm;

    // --- named particle regimes ---
    //
    // These are not tuning knobs; they are the published bands the table in this file's header lists,
    // named so that call sites and tests can say which physical regime they mean. Only the two
    // endpoints of the rainfall keying below are actually reached by a live map — the rest exist as
    // reference points the tests pin the model's behaviour at.

    // The grey limit. Particles far larger than every visible wavelength: fog, sea spray, a thick
    // dust storm. tau is then wavelength-INDEPENDENT, so all three channels are attenuated equally
    // and the sun dims without shifting hue at all. This is the case the Planckian-locus model was
    // structurally incapable of producing, and it is the headline invariant of this subsystem.
    public const float GreyExtinctionExponent = 0f;

    // Coarse mineral dust close to its source — a haboob, a dust outbreak over a desert. Measured
    // Angstrom exponents for freshly-lofted desert dust really do sit near zero (0.0-0.5); the
    // classic "brown ochre sunset" band of 0.5-1.0 is aged, size-sorted dust that has travelled.
    // This is the driest-tile endpoint of the rainfall keying.
    public const float ThickDustExponent = 0.2f;

    // Aged coarse mineral dust — the middle of the published 0.5-1.0 band, the Saharan-dust sunset
    // seen from a long way downwind. A reference point rather than a keyed endpoint.
    public const float CoarseDustExponent = 0.7f;

    // Urban / industrial haze: the classic deep orange-red, and the middle of the published 1.2-1.7
    // band. See ReferenceAngstromExponent below for why this particular value is load-bearing.
    public const float UrbanHazeExponent = 1.3f;

    // Fine smoke and secondary sulfate — small particles, strong short-wavelength removal, a deep
    // saturated red. This is the wettest-tile endpoint of the rainfall keying.
    public const float FineSmokeExponent = 2f;

    // Rayleigh's own exponent. Not an aerosol at all — it is the clean-air limit, included because it
    // is what makes this model checkable: setting alpha to 4 must reproduce the spectral shape §8's
    // Kelvin ramp was built on, using a completely different representation. That cross-check is the
    // strongest evidence available offline that the per-channel model and the locus model describe
    // the same physics.
    public const float RayleighExponent = 4f;

    // No real aerosol is MORE wavelength-selective than the molecules themselves, so alpha is clamped
    // to [0, 4] rather than trusted. The floor matters more than the ceiling: a negative alpha would
    // attenuate blue LESS than red, inverting the hue shift, and nothing downstream would notice.
    private const float MinAngstromExponent = GreyExtinctionExponent;
    private const float MaxAngstromExponent = RayleighExponent;

    // --- magnitude, and where it was calibrated from ---

    // The exponent §20b's single shipped colour was implicitly calibrated at. §20b's own DESIGN.md
    // text names it outright when it justifies AerosolHorizonKelvin: "Angstrom exponent ~1.3 against
    // Rayleigh's 4, so roughly a third of the reddening per unit depth". That is the tell that §20b
    // had already chosen an alpha — it just baked it into a constant instead of exposing it.
    //
    // Keeping that value as the reference is what makes this subsystem a GENERALISATION of §20b
    // rather than a retune of it: at alpha = 1.3 the per-channel model reproduces the colour §20b
    // shipped (see HorizonOpticalDepth), and the new variety appears only as alpha moves away from
    // it. Nobody's existing colony sunset moves unless its tile is genuinely dry or genuinely wet.
    public const float ReferenceAngstromExponent = UrbanHazeExponent;

    // §20b's AerosolHorizonKelvin, which no longer exists as a live code path. It is retained here
    // because it is the number HorizonOpticalDepth below was fitted to, and a calibration whose
    // anchor has been deleted is a magic number one commit later.
    //
    // WHY IT IS NO LONGER LIVE — the decision this whole subsystem turns on. §20b expressed the
    // aerosol's colour effect as a second lerp along the Planckian locus, from the clean-air endpoint
    // down to 1500 K. This file expresses the SAME physical effect as per-channel transmission. They
    // are two representations of one thing, so applying both would double-count the reddening, and
    // the ticket's warning about leaving two uncoordinated aerosol colour paths is exactly right.
    //
    // One of them therefore had to subsume the other, and the direction is forced rather than chosen:
    // THE LOCUS REPRESENTATION IS LOSSY IN PRECISELY THE DIRECTION #86 NEEDS. The Helland fit pins
    // its blue channel at 0 below 1900 K, so §20b's 1500 K endpoint has blue EXACTLY zero. Any
    // alpha-dependent correction applied downstream of that endpoint is multiplying zero, and can
    // never recover the pale, blue-retaining sun that alpha ~0 is supposed to produce. Composition in
    // that order cannot express the headline case at all; the per-channel model composed the other
    // way (applied to the CLEAN-air colour) can express every case including §20b's own. So the
    // per-channel model owns the aerosol's colour outright, and the locus endpoint is retired to
    // being the calibration anchor it is here.
    public const float CalibrationAnchorKelvin = 1500f;

    // Aerosol optical depth at ReferenceWavelengthNm, along the horizon slant path, at a full
    // sea-level aerosol load (§20b's aerosolFraction = 1).
    //
    // WHERE 2.1931 COMES FROM. It is fitted, not measured: it is the tau for which the per-channel
    // model at ReferenceAngstromExponent reproduces the GREEN channel of §20b's shipped horizon
    // colour exactly — i.e. the tau that makes this file a drop-in generalisation of the constant it
    // replaces. Solving exp(-tau * [(550/550)^-1.3 - (600/550)^-1.3]) = G(1500 K) / G(2000 K) gives
    // 2.19308, since red is the least-attenuated channel and therefore sets the normalisation.
    //
    // Red matches trivially (the Helland fit saturates it at 1.0 across this whole range) and blue
    // lands at 0.0224 against §20b's 0.0 — the residual is the fit's blue cliff, not a disagreement,
    // and 0.022 of one channel behind a <=0.35 blend strength is under 0.008 of final sky colour.
    // AtTheReferenceExponent_ReproducesTheColourTwentyBShipped pins all three.
    //
    // WHY IT IS NOT A LITERAL SLANT-PATH OPTICAL DEPTH. A heavy urban vertical AOD near 0.3 crossed
    // at a horizon airmass near 38 would give tau ~11, five times this. That is the right answer for
    // a radiative-transfer renderer and the wrong one here, because the thing being reproduced is
    // §8's 2000 K sea-level endpoint, which is itself a first-order artistic anchor rather than a
    // computed radiance. Calibrating against the shipped look keeps the two consistent; calibrating
    // against the literal physics would silently move every existing sunset.
    public const float HorizonOpticalDepth = 2.1931f;

    // --- keying alpha to the map ---
    //
    // Vanilla worldgen assigns biomes by SCORING tile.rainfall and tile.temperature: BiomeWorker_Desert
    // gates on `tile.rainfall >= 600f`, BiomeWorker_ExtremeDesert on `>= 340f`, and
    // BiomeWorker_TropicalRainforest on `< 2000f` (verified by decompiling 1.6 Assembly-CSharp). So
    // keying alpha on rainfall keys it on the SAME axis the biome label is derived from — but
    // continuously, and without a defName table that every modded biome falls off the end of. A
    // Biomes! or Alpha Biomes tile lands somewhere sensible on the ramp for free rather than hitting
    // a default it was never considered for.
    //
    // The two breakpoints below are vanilla's own, which is the point: they are where the game itself
    // already decides "this is desert" and "this is rainforest".

    // Vanilla's ExtremeDesert cutoff. At and below it the tile is genuinely arid, bare mineral soil
    // with nothing binding it, so whatever aerosol it carries is lofted coarse dust: near-grey.
    public const float DriestRainfallMillimetres = 340f;

    // Vanilla's TropicalRainforest cutoff. At and above it there is no exposed dust to loft and
    // washout removes the coarse mode continuously, leaving the fine secondary/biogenic particles
    // that make up a wet-climate haze.
    public const float WettestRainfallMillimetres = 2000f;

    // Angstrom exponent for a tile, from its annual rainfall in millimetres.
    //
    // Linear between the two vanilla breakpoints and clamped flat outside them, in the same spirit as
    // every other ramp in this subsystem: a first-order mapping stated openly rather than a curve
    // fitted to data RimWorld does not have. The DIRECTION is the physically defensible part — arid
    // ground makes coarse, near-grey aerosol and wet ground makes fine, strongly-selective aerosol —
    // and the direction is what the tests pin.
    //
    // Note where the common case lands. The reference exponent 1.3 is reached at about 1354 mm, which
    // sits inside the temperate/boreal forest band most colonies are founded in, so the sunset those
    // maps get is the one §20b already shipped. The new colours appear at the extremes, which is the
    // correct place for a variety feature to spend its budget.
    //
    // Note also that alpha only ever matters where there IS aerosol. On any tile with pollution 0 the
    // §20b column is zero, the optical depth is zero, and every alpha produces an identical (empty)
    // transmission — so a cold desert being scored as "dust" despite being under snow costs nothing,
    // because it has no aerosol for the shape to shape.
    public static float AngstromExponentForRainfall(float rainfallMillimetres)
    {
        float wetness = InverseLerpClamped(
            DriestRainfallMillimetres, WettestRainfallMillimetres, rainfallMillimetres);
        return Lerp(ThickDustExponent, FineSmokeExponent, wetness);
    }

    // --- the model ---

    // Aerosol optical depth at one wavelength, for an aerosol load in [0, 1].
    //
    // aerosolLoad is §20b's aerosolFraction already scaled by however low the sun is — the caller
    // owns that, because the elevation ramp belongs to §8's curve rather than to the spectrum.
    public static float OpticalDepth(float aerosolLoad, float angstromExponent, float wavelengthNm)
    {
        float alpha = ClampAngstrom(angstromExponent);
        float spectralShape = MathF.Pow(wavelengthNm / ReferenceWavelengthNm, -alpha);
        return HorizonOpticalDepth * Clamp01(aerosolLoad) * spectralShape;
    }

    // Beer-Lambert transmission through the aerosol at each of the three channel wavelengths, RAW —
    // i.e. carrying both the hue shift and the overall dimming.
    //
    // This is exposed separately from HueMultiplier below because it is what the grey-extinction
    // invariant is actually about: at alpha = 0 the three values are EQUAL, which is the statement
    // "the sun dims without shifting hue". Normalising first would make that statement unfalsifiable
    // by construction, so the test needs the un-normalised form to assert against.
    public static SkyColorTemperature.Rgb SpectralTransmission(float aerosolLoad, float angstromExponent)
    {
        float red = Transmission(aerosolLoad, angstromExponent, RedWavelengthNm);
        float green = Transmission(aerosolLoad, angstromExponent, GreenWavelengthNm);
        float blue = Transmission(aerosolLoad, angstromExponent, BlueWavelengthNm);
        return new SkyColorTemperature.Rgb(red, green, blue);
    }

    // The same transmission normalised so its largest channel is exactly 1 — a pure hue shift with
    // the dimming divided out.
    //
    // WHY IT IS NORMALISED, which is OzoneTwilightMath's argument repeated for the same reason. §8 is
    // a COLOUR-ONLY lane: the adapter blends WeatherWorker.CurSkyTarget's colours and never its
    // .glow, so that the brightness value other mods read is undisturbed. But a sky COLOUR carries
    // brightness of its own, and an un-normalised transmission would darken it — smuggling a
    // brightness change into a patch that calls itself colour-only.
    //
    // Dropping the dimming here is also exactly consistent with what §20b already decided and wrote
    // down: the wavelength-FLAT half of aerosol extinction (the muting and greying) belongs to §9's
    // saturation lane and is blocked behind #78. §20b shipped the wavelength-selective half and
    // skipped the flat half; this file changes the SHAPE of the selective half and leaves that split
    // exactly where it found it. At alpha = 0 the selective half is empty, so the aerosol correctly
    // does nothing to the colour — the dimming it would really cause is #78's to deliver, not ours.
    //
    // The max is taken rather than assumed to be red, so that a future edit to the wavelengths or to
    // the exponent clamp cannot silently produce a channel above 1.
    public static SkyColorTemperature.Rgb HueMultiplier(float aerosolLoad, float angstromExponent)
    {
        SkyColorTemperature.Rgb raw = SpectralTransmission(aerosolLoad, angstromExponent);
        float brightest = Max(raw.R, Max(raw.G, raw.B));
        return new SkyColorTemperature.Rgb(raw.R / brightest, raw.G / brightest, raw.B / brightest);
    }

    // Apply the aerosol's spectral shape to a clean-air sky colour.
    //
    // Multiplication, not a lerp: extinction IS multiplicative, and it is the multiplication that
    // takes the result off the Planckian locus. A lerp toward some target colour would just be the
    // locus model again with extra steps.
    //
    // At aerosolLoad 0 every channel of the multiplier is exactly 1.0f, so this returns cleanColour
    // BIT-IDENTICALLY rather than approximately. That is what makes the "unpolluted tiles are
    // untouched" regression pin assertable as exact equality, which matters because every tile in a
    // game without Biotech takes this path.
    //
    // No clamping is needed on the way out: cleanColour is already in [0, 1] per channel and every
    // multiplier channel is in (0, 1], so the product cannot leave the range.
    public static SkyColorTemperature.Rgb Apply(
        SkyColorTemperature.Rgb cleanColour, float aerosolLoad, float angstromExponent)
    {
        SkyColorTemperature.Rgb hue = HueMultiplier(aerosolLoad, angstromExponent);
        return new SkyColorTemperature.Rgb(
            cleanColour.R * hue.R, cleanColour.G * hue.G, cleanColour.B * hue.B);
    }

    private static float Transmission(float aerosolLoad, float angstromExponent, float wavelengthNm) =>
        MathF.Exp(-OpticalDepth(aerosolLoad, angstromExponent, wavelengthNm));

    private static float ClampAngstrom(float v) =>
        v < MinAngstromExponent ? MinAngstromExponent : (v > MaxAngstromExponent ? MaxAngstromExponent : v);

    private static float Max(float a, float b) => a > b ? a : b;
    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float InverseLerpClamped(float a, float b, float v) => Clamp01((v - a) / (b - a));
}
