using UnityEngine;
using Verse;

namespace CelestialLighting;

// Low-light desaturation / Purkinje shift (DESIGN.md §9). As the sky darkens toward night, human
// vision loses colour discrimination and the world drifts toward a dim, cool blue-grey — rods
// taking over from cones. This reproduces that so our nights read as *night*, not as a uniformly
// dimmed day.
//
// Like §2 (Patch_TwilightColor) and the planned §8 colour-temperature curve, this is a COLOUR-ONLY
// blend on WeatherWorker.CurSkyTarget — it touches SkyColorSet (sky/overlay tint + the saturation
// multiplier) and deliberately never writes target.glow. Keeping clear of glow is what lets it
// stack cleanly with those siblings and with anything downstream that reads brightness
// (SkyManager.CurSkyGlow, GlowGrid, Dub's Skylights): we change how the existing light *reads*, not
// how much of it there is.
//
// Multiple postfixes on CurSkyTarget coexist fine (Patch_TwilightColor already adds one); Harmony
// runs them in sequence and each nudges the same struct.
public static class Patch_LowLightDesaturation
{
    // The colour rod-dominated night vision biases toward — a desaturated cool blue-grey. Real
    // scotopic vision is achromatic but perceptually reads slightly blue (the Purkinje blue shift),
    // so this is a low-saturation grey pulled a touch toward blue rather than a neutral grey.
    private static readonly Color CoolNight = new Color(0.55f, 0.60f, 0.72f);

    // Peak blend fractions toward CoolNight at full rod vision (PurkinjeFactor == 1). The sky plane
    // carries the shift more than the overlay so the tint reads without washing the whole scene
    // flat. Both are scaled down by the factor, so at dusk (factor near 0) the nudge is negligible
    // and only deep night gets the full drift, and then by PurkinjeSettings.TintStrength — the
    // "Night desaturation" slider, which until now was persisted but wired to nothing.
    //
    // Back at 0.50/0.35, the values that shipped alongside the original whole-frame desaturation.
    // They were raised to 0.70/0.50 when that multiply was dropped, on the theory that the tint now
    // had to carry the night alone — but it never could: this colour lands on MatBases.LightOverlay,
    // which MULTIPLIES, and a multiply cannot pull channels toward each other (see
    // NightDesaturationMath's header for the live measurement). All the raise bought was a stronger
    // push toward a constant that sits slightly WARMER than vanilla's night sky, which measurably
    // raised saturation on partly-lit ground. Now that SectionLayer_NightDesaturation does the
    // desaturating, this is a secondary cue again and belongs where it was.
    private const float SkyBlendMax = 0.50f;
    private const float OverlayBlendMax = 0.35f;

    internal static void Apply(Map map, ref SkyInputs inputs, ref SkyTarget target)
    {
        // Feature gate (default on): when off, leave each WeatherDef's palette untouched — the
        // faithful pre-feature baseline. Sits before the glow read so "off" is a true no-op.
        if (!CelestialLightingFeatures.LowLightDesaturation)
            return;

        // Read the sky target's OWN glow, not GenCelestial.CurCelestialSunGlow. This is the opposite
        // choice from Patch_TwilightColor (which recomputes celestial glow because it wants twilight
        // timing anchored to true sun position): here we *want* the actual displayed brightness,
        // because a darker scene genuinely pushes vision further into rod territory.
        //
        // ORDER-DEPENDENT, AND THE DEPENDENCY IS ENFORCED BY Patch_SkyTargetComposite. By the time this
        // runs, §7 has already replaced the below-horizon glow with its starlight + airglow + moonlight
        // floor, so a full-moon night lands lower on the ramp (less shift) than a new-moon one. That is
        // true because the composite calls this stage directly after Patch_NightRadiance and says so —
        // it is NOT true of alphabetical order, which is what the fourteen separate CurSkyTarget
        // postfixes used to compose in, and under which this ran BEFORE §7 and read vanilla's raw
        // below-horizon glow. The moon-phase dependence described above did not exist in any shipped
        // build before that move; see DESIGN.md §29 for the measurement.
        //
        // Two other readers of this same quantity had always been on the correct side of §7 and so had
        // been reporting a value this patch was not using: PurkinjeProbe and §9's own wash
        // (Patch_NightDesaturationStrength) both read the FINAL composed glow off SkyManager. Sitting
        // after §7 is what makes the patch, the probe and the wash agree — the shared-read discipline
        // the rest of the mod uses, restored here by sequence because this one genuinely needs the
        // composed value rather than an adapter's.
        //
        // Then attenuate by §13's weather dimming to get the APPARENT brightness — the seam that
        // finally makes this patch's original promise true. It was written believing target.glow
        // was "already clamped by the active WeatherDef's maxGlow", so an overcast night would
        // desaturate more than a clear one for free. It never did: maxGlow defaults to 1.0 and is
        // set exactly once across all vanilla XML (Odyssey's Overcast, 0.95), and even that is inert
        // at night, where celestial glow is ~0 under every weather alike. A blizzard and a clear sky
        // desaturated identically — precisely the opposite of "strongest on the darkest nights".
        //
        // The weather term comes from the shared WeatherDimming adapter rather than from a value
        // §13's stage left behind on target, so this carries no ordering dependency on §13 at all —
        // a shared adapter read instead of a sequencing assumption, which is what makes that coupling
        // robust.
        //
        // Note §13 deliberately never writes .glow, so this stays purely perceptual: the gameplay
        // brightness driving plant growth and solar output is still the unweathered value.
        float glow = WeatherDimmingMath.ApparentGlow(target.glow, inputs.WeatherDimming);

        float factor = PurkinjeMath.PurkinjeFactor(glow);
        if (factor <= 0f)
            return;

        // SkyColorSet.saturation is deliberately NOT touched — see the header. That field lands on
        // Find.CameraColor (a ColorCorrectionCurves image effect), which processes the finished
        // frame and so cannot tell a campfire from the dark field around it.
        //
        // Bias the sky/overlay tint toward the cool blue-grey instead. Lerp (never overwrite) so each
        // WeatherDef's palette still shows through — it is just pulled toward night-blue as colour
        // discrimination fades. This rides the lighting overlay, which is baked per-cell and
        // interpolated across cell quads, so the drift lands on unlit ground and fades out smoothly
        // toward anything lit: the local behaviour the global multiplier could never express.
        float strength = PurkinjeSettings.TintStrength;
        target.colors.sky = Color.Lerp(target.colors.sky, CoolNight, factor * SkyBlendMax * strength);
        target.colors.overlay = Color.Lerp(target.colors.overlay, CoolNight, factor * OverlayBlendMax * strength);
    }
}
