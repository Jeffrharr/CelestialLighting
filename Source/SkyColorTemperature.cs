using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file (same discipline as
// Formulas.cs). It is linked into the test project via <Compile Include> so the exact code that
// ships is the exact code under test. If a function here ever needs Mathf/Color/Map, it belongs in
// the Patch_SkyColorTemperature adapter instead — pass primitives in from there.
//
// Subsystem 8 (DESIGN.md §8): a continuous colour-temperature curve keyed on sun ALTITUDE. The sky
// shifts from a warm, low-colour-temperature glow near the horizon (~2000 K) up to a neutral
// daylight white near the zenith (~5772 K, the Sun's real effective temperature), passing through
// golden-hour warmth on the way. This generalizes §2's single fixed twilight hue: because day
// length and peak sun altitude already vary with latitude/season (vanilla GenCelestial + our own
// SolarPosition simulator), a high-latitude winter day whose sun never climbs far *stays* warm all
// day for free — the effect is a function of altitude alone, so low sun == warm sky wherever and
// whenever that happens.
//
// The curve's second input is where the observer stands in the atmosphere (DESIGN.md §20): the warm
// endpoint is a *sea-level* endpoint, and a map 4000 m up has ~38% less air overhead to redden
// through, so it gets a whiter sun and a subdued sunset. That arrives as a pressureFraction
// primitive from AtmosphericColumn, keeping this file free of any notion of where the map is.
//
// The curve's third input is what ELSE is in that air (DESIGN.md §20b): a boundary-layer aerosol
// column, scaled by the tile's Biotech pollution. It arrives as a second fraction from the same
// AtmosphericColumn model at a much shorter scale height (1500 m against 8500 m), which is why it
// points the opposite way to the altitude term and dies off nearly six times faster: a polluted
// lowland and a clean mountain base end up at opposite ends of one continuous curve, and a high
// enough map is simply above the haze.
//
// The curve's fourth input is the SHAPE of that aerosol's extinction (DESIGN.md §20c), and it is the
// one input that does not live on this curve at all. Everything above is a one-parameter family — a
// ramp along the Planckian locus — so altitude and pollution only ever change how far along ONE hue
// path the sky travels. Aerosol's Angstrom exponent changes the path itself, which cannot be said in
// Kelvin, so it is said per channel in AerosolSpectrum.cs instead and multiplied into the colour
// this file produces. Note what that means for the division of labour here: the Kelvin ramp below is
// now honestly the CLEAN-AIR curve, and the aerosol's colour effect lives entirely in the spectrum
// file. §20b's second Kelvin endpoint is gone rather than composed with it — see AerosolSpectrum's
// CalibrationAnchorKelvin for why applying both would have double-counted, and why the locus
// representation had to be the one that gave way.
//
// Critically this is a COLOUR-ONLY transform: the adapter blends WeatherWorker.CurSkyTarget's
// colours and never its .glow, so it does not disturb the brightness value other mods read (see
// DESIGN.md "Conflict risk"). Nothing in this file has any concept of brightness/glow at all.
public static class SkyColorTemperature
{
    // Endpoints of the altitude → colour-temperature curve, in Kelvin.
    //   HorizonKelvin: a warm ~2000 K (deep sunrise/sunset orange) at/below the horizon.
    //   ZenithKelvin:  5772 K, the Sun's actual effective (photospheric) temperature — the neutral
    //                  daylight white we ramp toward as the sun climbs. Not a round number by
    //                  accident: it's the real physical anchor the whole curve is built around.
    public const float HorizonKelvin = 2000f;
    public const float ZenithKelvin = 5772f;

    // §20b's AerosolHorizonKelvin, the second Kelvin endpoint pollution used to drag the warm end
    // down to, is GONE from this file rather than sitting alongside the spectral model. It has moved
    // to AerosolSpectrum.CalibrationAnchorKelvin, which records both the number and the reason: two
    // representations of one physical effect cannot both be applied without double-counting it, and
    // the locus representation is the lossy one (the Helland fit pins blue at 0 below 1900 K, so
    // anything downstream of §20b's endpoint is multiplying zero and can never produce the pale,
    // blue-retaining sun that a large-particle aerosol actually gives). The full argument is written
    // out there, next to the optical depth that was fitted to it, together with §20b's own reasons
    // for putting the endpoint where it is.
    //
    // What did NOT move with it is §20b's finding about tint STRENGTH: TintStrength still takes no
    // aerosol term, because strengthening a reddening tint models Mie scattering backwards (see
    // TintStrength's own note and the structural test pinning it). §20b's answer to "a maximally
    // polluted sky must be visibly different" was to deepen the endpoint COLOUR and to open the
    // adapter's per-channel blend, never to make the tint stronger. §20c changes only how that
    // colour is represented, so both halves of that answer survive unchanged.

    // At/above this solar elevation the sky has reached full daylight temperature and no further
    // warming is applied. 60° is high enough that only tropical/summer midday sun exceeds it, so
    // temperate and high-latitude skies stay perceptibly warm through most of their (lower) day —
    // which is exactly the "seasonal twilight stays warm all day up north" behaviour §8 wants.
    public const float DaylightAltitudeDegrees = 60f;

    // Standard atmospheric refraction at the horizon (matches Formulas.AtmosphericRefractionDegrees,
    // referenced directly so there is a single source of truth for "where the sun sets"): the sun's
    // last light lingers a fraction of a degree below geometric zero, so the warm tint should too.
    private const float HorizonElevationDegrees = Formulas.AtmosphericRefractionDegrees;

    // Below this the sun is far enough down that there is no direct sunlight left to colour, so the
    // tint fades out entirely and night (subsystem 7's domain) takes over. -6° is the end of civil
    // twilight — a standard, physically-meaningful choice, not a tuned magic number.
    private const float NightFadeFloorDegrees = -6f;

    public readonly struct Rgb
    {
        public readonly float R;
        public readonly float G;
        public readonly float B;

        public Rgb(float r, float g, float b)
        {
            R = r;
            G = g;
            B = b;
        }
    }

    // The warm end of the ramp, slid toward neutral by how much air the observer actually has above
    // them (DESIGN.md §20). pressureFraction comes from AtmosphericColumn.RayleighPressureFraction
    // and is 1 at sea level, ~0.62 on a 4000 m mountain, 0 in the h -> infinity limit.
    //
    // WHY THE HORIZON ENDPOINT AND NOT THE WHOLE CURVE. HorizonKelvin is the *reddened* end: the
    // 2000 K is what a full sea-level air column does to sunlight crossing tens of air masses.
    // ZenithKelvin is the *unreddened* photospheric anchor — sunlight before the atmosphere touched
    // it — so there is nothing for a thinner column to move it toward. Thinning the air can only
    // ever walk the warm endpoint back up toward the anchor, which is why this interpolates between
    // exactly those two constants and adds no third one.
    //
    // WHY IT IS LINEAR IN MIREDS AND NOT IN KELVIN. A mired is 10^6/K, and the reason to work there
    // is not the usual perceptual one (equal mired steps look like equal shifts, which is why
    // photographic filters are graded in mireds). It is that a mired shift is approximately linear
    // in OPTICAL DEPTH — and optical depth is exactly what pressureFraction scales, since Rayleigh
    // optical depth is proportional to the air column overhead.
    //
    // So the whole model falls out of one statement, "reddening is proportional to column":
    //
    //   mired shift at sea level   = 10^6/2000 - 10^6/5772 = 500.0 - 173.2 = 326.8
    //   mired shift at 4000 m      = 0.6247 * 326.8        = 204.2
    //   horizon endpoint at 4000 m = 10^6 / (173.2 + 204.2) = 2650 K
    //
    // Linear-in-Kelvin has no comparable derivation — it was a first-order artistic choice, and it
    // put 4000 m at ~3415 K, walking the warm end back nearly twice as far for no stated physical
    // reason. Both spaces agree EXACTLY at both endpoints (p = 1 -> HorizonKelvin, p = 0 ->
    // ZenithKelvin), so every invariant that lives at an endpoint — including §18's requirement that
    // p -> 0 reproduce the vacuum value — is untouched by this choice. Only the interior moves.
    //
    // The ramp this feeds is still a linear Kelvin lerp on ELEVATION, so the two spaces do coexist
    // in the file. That is deliberate rather than sloppy: elevation moves the sun through the
    // column, which is a geometric path-length effect with its own airmass curve, while this moves
    // the column's density. They are different physical quantities and there is no reason to expect
    // one interpolation space to serve both.
    //
    // §20b stacked a SECOND mired lerp on the end of this one, carrying the endpoint on past the
    // sea-level 2000 K toward an AerosolHorizonKelvin. §20c removed that stage outright: aerosol's
    // colour effect is a spectral SHAPE, and a point on the Planckian locus cannot carry one. What
    // is left is exactly what §20 introduced — the endpoint an UNPOLLUTED column of this thickness
    // reaches — and AerosolSpectrum then multiplies its per-channel transmission into the colour
    // that comes out of it.
    //
    // The mired argument above is not lost in the handover, it is generalised. Beer-Lambert
    // transmission composes additively in log space, which is the same "filter strengths add"
    // property that made mireds the right space for the clean-air stage; §20b's mired reading of how
    // fast pollution's effect collapses with altitude therefore survives the move exactly, and the
    // test that pinned it still holds. The one thing that does NOT survive is treating the aerosol
    // stage as a lerp toward a fixed endpoint colour, which is the lossy step §20c exists to undo.
    public static float HorizonKelvinForPressure(float pressureFraction)
    {
        float mired = Lerp(
            MiredOf(ZenithKelvin),
            MiredOf(HorizonKelvin),
            Clamp01(pressureFraction));

        return MiredToKelvin(mired);
    }

    // Both directions of 10^6/K, named rather than inlined so the conversion reads once and the
    // callers read as interpolation. Guarded against a zero denominator only in the sense that
    // neither endpoint constant is ever zero or near it: HorizonKelvin is 2000 and ZenithKelvin is
    // 5772, so mired stays in 173..500 and Kelvin never approaches the singularity at either end.
    private const float MiredScale = 1e6f;

    private static float MiredOf(float kelvin) => MiredScale / kelvin;

    private static float MiredToKelvin(float mired) => MiredScale / mired;

    // How horizon-like the sun is, in [0, 1]: 1 at/below the horizon, falling linearly to 0 at
    // DaylightAltitudeDegrees. It is the complement of the Kelvin ramp's own interpolant, and the
    // stand-in this subsystem uses for slant path length — a low sun's light crosses far more of
    // everything (air, haze, ozone) than a high sun's does.
    //
    // Extracted because THREE things now need the same ramp and they must not drift apart: the
    // Kelvin ramp (as 1 - t), TintStrength's lowSunRamp, and §20c's aerosol optical depth. The last
    // is the new one, and using this rather than a fresh ramp is what makes the aerosol's colour
    // fade out with sun altitude at exactly the rate §20b's Kelvin endpoint did — the spectral model
    // is a change of representation, not a change of when the effect applies.
    public static float LowSunFraction(float elevationDegrees) =>
        InverseLerpClamped(DaylightAltitudeDegrees, 0f, elevationDegrees);

    // Linear ramp from HorizonKelvin (at/below the horizon) to ZenithKelvin (at/above
    // DaylightAltitudeDegrees), clamped flat past both ends. Monotonic in elevation, so a lower sun
    // is always at least as warm as a higher one — the property the whole subsystem depends on.
    //
    // pressureFraction (DESIGN.md §20) moves only the horizon endpoint, per HorizonKelvinForPressure
    // above. Note that pressureFraction -> 0 reproduces the inVacuum return value exactly rather than
    // approximately: vacuum is the h -> infinity limit of this same curve, so the discrete §18 gate
    // and the continuous atmospheric model agree at the endpoint by construction. They stay separate
    // concepts anyway — see the inVacuum note below and §20.
    //
    // This is the CLEAN-AIR colour temperature. It is still the honest answer to "what colour
    // temperature is the sunlight reaching this map", and it is still what the sky_color_temperature
    // probe reports — but since §20c it is no longer the whole of the sky colour, because a notched
    // or sloped aerosol spectrum is not a blackbody at any temperature. SkyColorForElevation below is
    // the composition that is; anything that needs the colour rather than the temperature must go
    // through that rather than calling BlackbodyToRgb on this directly.
    //
    // inVacuum (DESIGN.md §18, the shared gate in Vacuum.cs): the entire ramp is a Rayleigh
    // reddening model. Low sun looks warm on the ground because its light crosses tens of air masses
    // on the way in and the short wavelengths scatter out of the beam; the sun's own emitted spectrum
    // does not change as it descends. With no air there is no reddening at any altitude, so the curve
    // pins flat to ZenithKelvin — the unreddened photospheric anchor the whole curve was built
    // around, and now the only value it can return. Flat, not merely shallower: this is what "the
    // spectrum does not vary with sun altitude" means as a function.
    public static float ColorTemperatureKelvin(
        float elevationDegrees, float pressureFraction, bool inVacuum)
    {
        if (inVacuum)
            return ZenithKelvin;

        float t = InverseLerpClamped(0f, DaylightAltitudeDegrees, elevationDegrees);
        return Lerp(HorizonKelvinForPressure(pressureFraction), ZenithKelvin, t);
    }

    // How strongly the warm tint should be applied at a given sun altitude, in [0, 1]. It is the
    // product of two ramps:
    //   * lowSunRamp — 1 at the horizon, falling to 0 by DaylightAltitudeDegrees: the tint is
    //     strongest when the sun is low and vanishes once it's high (where the sky is neutral
    //     anyway, so blending toward ~5772 K would do almost nothing regardless).
    //   * daylightGate — 1 while the sun is up, ramping to 0 between the refraction-adjusted horizon
    //     and the end of civil twilight: below the horizon there is no direct sunlight left to
    //     colour, so we hand off to the night-radiance subsystem instead of tinting darkness warm.
    // The adapter multiplies this geometric factor by its own per-channel blend strengths, exactly
    // the way Patch_TwilightColor multiplies Formulas.TwilightFactor by 0.35/0.25.
    //
    // inVacuum (DESIGN.md §18): zero, so the adapter applies no tint at all and each WeatherDef's
    // palette passes through untouched. Both of the factors below are atmospheric and neither
    // survives: lowSunRamp is a stand-in for air-mass path length (there is none), and daylightGate's
    // -6°..horizon fade is civil twilight, which §18a removes outright (see Formulas.TwilightFactor).
    //
    // Note this is a stronger statement than "pin the colour": ColorTemperatureKelvin pinning to
    // ZenithKelvin does NOT by itself make the effect flat, because the Helland fit puts 5772 K at
    // roughly (1.00, 0.95, 0.90) rather than at pure white. Blending toward that with an
    // elevation-dependent strength would keep amber creeping into the sky as the sun dropped — the
    // exact artefact §18a exists to remove. Zeroing the strength is what makes it flat; pinning the
    // Kelvin keeps every other consumer of the curve (the sky_color_temperature probe, and #32 when
    // it needs an unreddened reference to redden away from) reading the honest value.
    //
    // pressureFraction (DESIGN.md §20) enters as a third multiplicative factor, and for the same
    // reason that argument gives for needing both halves in vacuum: walking the Kelvin endpoint
    // toward neutral does not by itself weaken the tint, because the Helland fit puts ZenithKelvin
    // near (1.00, 0.95, 0.90) rather than at white, so a thin-air sky would still be blended toward
    // a faintly amber target at full strength. Scaling the strength is what actually produces the
    // famously *subdued* high-altitude sunset. Physically both factors are the same optical depth:
    // there is less air to redden the beam AND less air to carry the reddened colour into the sky.
    // The linear scaling is the small-tau reading of that — exact enough over the 0.6–1.0 band real
    // tiles occupy, and it is what makes pressureFraction -> 0 reproduce the vacuum value exactly.
    //
    // aerosolFraction (DESIGN.md §20b) is DELIBERATELY ABSENT, and the absence is the design rather
    // than an oversight — it is not a parameter here so that no call site can quietly start feeding
    // one. §20 needed both halves because thinning the air removes reddening AND removes the medium
    // that carries the reddened colour into the sky, and both point the same way. Aerosol does not
    // give the same clean answer twice:
    //   * Where it exists at all, the strength factor is already saturated. The aerosol column is
    //     above half only below ~1040 m, where pressureFraction is >= 0.88 and the horizon-time tint
    //     is at or near its 1.0 ceiling anyway — so an additive strength term would be clamped away
    //     over most of the band it was added for, leaving a knob that mostly does nothing.
    //   * Where it would not be clamped (mid-elevation sun, mid-altitude tiles) it would push the
    //     tint STRONGER, and that is the one direction the physics says is wrong. Mie scattering is
    //     nearly wavelength-flat next to Rayleigh's λ^-4, so heavy aerosol greys and mutes a sunset
    //     rather than intensifying it. Adding strength here would model the effect backwards.
    // The muting itself belongs to §9's desaturation lane (Patch_LowLightDesaturation / PurkinjeMath)
    // and is deliberately not reached for from here: §8 is colour-only and never writes .saturation
    // or .glow, and two subsystems independently pulling saturation down is exactly the failure #78
    // is about. If the mute reads as missing in a live A/B, it is a §9 ticket keyed on this same
    // aerosol fraction, filed once #78 settles.
    //
    // §20c's angstromExponent is absent for the SAME reason, and it is worth saying explicitly
    // because the temptation there is different and more plausible: low alpha does mean weaker
    // colour, so scaling strength by alpha would look like the obvious way to express "grey aerosol
    // barely tints". It would be wrong twice over. The spectral model already delivers that — at
    // alpha 0 its hue multiplier is exactly (1, 1, 1), so the colour is untouched with no help from
    // the strength factor — and doing it here as well would double-count it AND would weaken the
    // clean-air Rayleigh tint that has nothing to do with aerosol particle size. Strength stays a
    // function of sun geometry and air column alone; every aerosol question is answered in colour.
    public static float TintStrength(float elevationDegrees, float pressureFraction, bool inVacuum)
    {
        if (inVacuum)
            return 0f;

        float lowSunRamp = LowSunFraction(elevationDegrees);
        float daylightGate = InverseLerpClamped(NightFadeFloorDegrees, HorizonElevationDegrees, elevationDegrees);
        return lowSunRamp * daylightGate * Clamp01(pressureFraction);
    }

    // Convenience composition used by both the adapter and the live probe so they can never derive a
    // different colour from the same inputs: elevation + site air column + aerosol load + aerosol
    // particle size → RGB. Carries inVacuum straight through, so in vacuum this is the constant
    // unreddened anchor colour at every elevation.
    //
    // TWO STAGES, AND THE ORDER IS THE PHYSICAL CLAIM (DESIGN.md §20c). First the clean-air colour:
    // what the Rayleigh column at this altitude does to sunlight at this sun angle, which a single
    // colour temperature genuinely can describe because Rayleigh has one fixed spectral shape. Then
    // the aerosol, applied as a per-channel multiplication on top of that, because its shape varies
    // and no colour temperature can carry a varying shape. Sunlight really does cross the bulk
    // atmosphere before it reaches the boundary layer the observer is standing in, so the ordering
    // is not merely convenient.
    //
    // WHY THE MULTIPLY IS ON THE CLEAN COLOUR AND NOT ON §20b's POLLUTED ONE. Because §20b's polluted
    // endpoint has already thrown away the information the multiply needs — the Helland fit pins blue
    // at 0 below 1900 K, so multiplying a 1500 K colour can never restore blue no matter what the
    // aerosol's actual particle size is. The whole grey/large-particle half of the range would be
    // unreachable. See AerosolSpectrum.CalibrationAnchorKelvin.
    //
    // The aerosol load is scaled by LowSunFraction because optical depth is a path length: a sun at
    // 60° is barely looking through the boundary layer at all, while a sun on the horizon is looking
    // along it. Using this subsystem's existing ramp rather than a new one is what makes the aerosol
    // effect fade with sun altitude at exactly the rate §20b's Kelvin endpoint faded at.
    public static Rgb SkyColorForElevation(
        float elevationDegrees, float pressureFraction, float aerosolFraction,
        float angstromExponent, bool inVacuum)
    {
        Rgb cleanAir = BlackbodyToRgb(
            ColorTemperatureKelvin(elevationDegrees, pressureFraction, inVacuum));

        // §18's gate, honoured here as well as inside the ramp: no air means no aerosol either, and
        // the aerosolFraction a vacuum map's tile produces is not a number worth trusting. One early
        // return rather than relying on the caller's fraction happening to be zero.
        if (inVacuum)
            return cleanAir;

        float slantLoad = aerosolFraction * LowSunFraction(elevationDegrees);
        return AerosolSpectrum.Apply(cleanAir, slantLoad, angstromExponent);
    }

    // Blackbody colour temperature → linear-ish sRGB in [0, 1] per channel, via the widely published
    // (public-domain) Tanner Helland approximation of the Planckian locus. This is a standard
    // tabulated/curve-fit conversion used across countless colour tools — textbook, not mod-specific
    // (see DESIGN.md "Clean-room provenance"). Valid roughly 1000–40000 K; since §20c retired §20b's
    // 1500 K endpoint, our curve only ever feeds it HorizonKelvin..ZenithKelvin — a narrower band
    // sitting further inside the fit's validity range than before, because the aerosol's extra
    // reddening is now applied per channel afterwards rather than by asking the fit for a colder
    // temperature than it was ever calibrated for.
    //
    // Each channel is its own small named function (below) rather than one deeply-nested branch, so
    // the piecewise structure reads top-to-bottom instead of forcing the reader to hold the other
    // two channels' cases in mind.
    public static Rgb BlackbodyToRgb(float kelvin)
    {
        float temp = kelvin / 100f;
        return new Rgb(RedChannel(temp), GreenChannel(temp), BlueChannel(temp));
    }

    // temp is kelvin/100. Below ~6600 K the red channel is fully saturated; above it, it rolls off.
    private static float RedChannel(float temp)
    {
        if (temp <= 66f)
            return 1f;
        return Clamp01(329.698727446f * MathF.Pow(temp - 60f, -0.1332047592f) / 255f);
    }

    private static float GreenChannel(float temp)
    {
        if (temp <= 66f)
            return Clamp01((99.4708025861f * MathF.Log(temp) - 161.1195681661f) / 255f);
        return Clamp01(288.1221695283f * MathF.Pow(temp - 60f, -0.0755148492f) / 255f);
    }

    private static float BlueChannel(float temp)
    {
        if (temp >= 66f)
            return 1f;
        if (temp <= 19f)
            return 0f;
        return Clamp01((138.5177312231f * MathF.Log(temp - 10f) - 305.0447927307f) / 255f);
    }

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float InverseLerpClamped(float a, float b, float v) => Clamp01((v - a) / (b - a));
}
