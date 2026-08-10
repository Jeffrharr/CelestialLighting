namespace CelestialLighting;

// §24 (prototype), issue #90: the part of §21's daytime cavity that the multiplicative sky lane
// physically cannot draw, expressed as an ADDITIVE overlay strength instead.
//
// THE PROBLEM THIS EXISTS TO SOLVE, restated from AlbedoCavityMath.RecoveredDimming's own header so
// this file stands alone. §21 says a snowy overcast is brighter than a snowy CLEAR sky — the
// counterintuitive inversion that motivated the whole subsystem. It cannot be rendered through
// SkyColorSet.sky, because that value is a MULTIPLY into MatBases.LightOverlay.color and vanilla's
// brightest palette is Clear's (1,1,1), i.e. "do not darken". RecoveredDimming therefore clamps at
// zero dimming, and the ordering the physics predicts collapses to a tie:
//
//     condition           multiply lane can render      physics says
//     snowy clear sky     ~clear-day baseline           1.07x
//     snowy overcast      capped at clear-day baseline  1.92x
//
// An additive pass has headroom above (1,1,1) because it adds rather than scales. This file turns
// "how much amplification got clamped away" into "how bright an additive quad to draw".
//
// WHY THE RESIDUAL, AND NOT THE GAIN ITSELF. The obvious implementation — draw glare proportional to
// cavityGain — would double-count everything §21 ALREADY delivers through the multiply lane, and
// would fire on maps where that lane has plenty of headroom left. Deriving the overlay from the part
// that overflowed instead means:
//
//   * bare ground is exactly 0, because gain is exactly 1 and nothing overflows;
//   * a thin deck overflows only slightly, so glare ramps with the deck rather than switching on;
//   * a snowy OVERCAST is where it blooms, which is precisely the inversion #90 says is missing.
//
// A SNOWY CLEAR SKY IS ALSO EXACTLY 0, BUT NOT FOR THE REASON IT FIRST APPEARS TO BE — worth stating
// because the plausible-sounding version is wrong and was written down here before it was checked.
// It is NOT that a clear sky's 1.07x cavity fits under the ceiling: with no dimming to spend, 1.07x
// would overflow and this function would return 0.071. It is that §13's classifier scores Clear as no
// cloud deck at all, so WeatherDimming returns before the cavity is ever consulted (see
// WeatherDimming.UndrawableExcessFor). §24 is therefore a cloud-deck effect end to end, and that
// holds even for a partly-cloudy Clear carrying §22's fraction, because §13 still scores Clear as
// zero on both its axes. The live scenario measures the 0 either way; only the explanation differed.
//
// So the effect appears only in the case the multiplicative lane could not express, and the two
// lanes partition the amplification between them rather than both rendering it. Nothing needs to
// know the other exists at draw time.
//
// Pure by the repo's convention: no UnityEngine, no Verse, primitives in and out. SnowGlare.cs is
// the adapter that reads live state, SnowGlareOverlay.cs is the only file that touches a Material.
public static class SnowGlareMath
{
    // Additive units of overlay brightness per unit of overflowed amplification, at full daylight.
    //
    // A CALIBRATED PROTOTYPE KNOB, not a derived quantity, and the honest reason is that the two
    // sides of the conversion are not commensurable: the residual is a ratio of DIFFUSE ILLUMINANCE,
    // while the overlay's alpha lands in RimWorld's already-tonemapped sRGB framebuffer, which has no
    // documented transfer curve for us to invert. So it is set by measurement instead, the same way
    // §22's WobbleAmplitude and §20c's DriftAmplitude were.
    //
    // THE FIRST VALUE WAS 0.22 AND IT WAS FAR TOO STRONG — worth recording, because the failure was
    // the one issue #90 predicted and it does not look like a bug from inside the code. On the
    // strongest case the game can produce (full-map fresh snow, thick overcast, noon) it measured
    // median CIELAB ΔE 19.79, against a mod whose largest shipped effect to date is §20b pollution at
    // 6.79. On screen that read as a milky haze washing the whole map rather than as glare: the
    // brightness claim landed, but so much of it that terrain contrast went with it.
    //
    // This value puts that same worst case in the neighbourhood of §21's own overcast-noon 6.06 —
    // deliberately calibrated against a sibling subsystem rather than against taste, so the strength
    // claim stays comparable to the rest of DESIGN.md's measured set.
    public const float DefaultIntensityScale = 0.06f;

    // Hard ceiling on the additive alpha, whatever the residual and daylight say.
    //
    // Exists because the residual is UNBOUNDED ABOVE in a way the cavity gain is not: gain is capped
    // by MaxCavityProduct, but the residual also scales with (1 - dimming), and a future weather with
    // heavier dimming could push the product further than anything vanilla currently reaches. A
    // whited-out screen is a far worse failure than an under-bright one, and this is a visual effect
    // with no gameplay reading attached, so it clips rather than being allowed to run.
    public const float MaxIntensity = 0.12f;

    // How much of §21's daytime amplification overflowed the multiply lane's ceiling, in additive
    // fractions of the scene's own brightness. 0 whenever the multiply lane could express the whole
    // thing — which is every bare-ground map, every clear sky, and every map with the cavity off.
    //
    // The algebra is RecoveredDimming's, continued past the point where it clamps. §13's surviving
    // light fraction is (1 - dimming); the cavity multiplies diffuse light by cavityGain; so the
    // composed surviving fraction is (1 - dimming) * cavityGain. RecoveredDimming returns
    // Clamp01(1 - that), which discards everything above 1. This returns exactly what that clamp
    // discarded, so the two functions partition the same product between them with nothing invented
    // and nothing counted twice:
    //
    //     surviving <= 1  ->  RecoveredDimming renders all of it, this returns 0
    //     surviving >  1  ->  RecoveredDimming renders up to the ceiling, this returns the remainder
    //
    // VACUUM (§18): 0. Not because of the ceiling but because there is no cavity to overflow it —
    // no atmosphere, no cloud base, no second wall. Sits as the last required parameter and returns
    // before any other term is read, per Source/Vacuum.cs's convention, even though CavityGain would
    // already have handed us exactly 1 here: a second place that can disagree about what vacuum means
    // is worth more to close structurally than the redundant branch costs.
    public static float UndrawableExcess(float dimming, float cavityGain, bool inVacuum)
    {
        if (inVacuum)
            return 0f;

        // Same guard and the same reasoning as RecoveredDimming's: CavityGain is monotone above 1 by
        // construction, so this reads as "no cavity worth the arithmetic" rather than as a float
        // equality test, and it keeps the overwhelmingly common bare-ground path at an exact 0 with
        // no dependence on how (1 - d) * 1 happens to round.
        if (cavityGain <= 1f)
            return 0f;

        float surviving = (1f - Clamp01(dimming)) * cavityGain;
        return surviving > 1f ? surviving - 1f : 0f;
    }

    // How much of the map's current brightness is DAYLIGHT, as opposed to §7's night floor: the part
    // of skyGlow sitting above the floor, in [0,1].
    //
    // THIS EXISTS BECAUSE THE OBVIOUS VERSION WAS MEASURED WRONG. The first cut scaled glare by
    // skyGlow alone, on the reasoning that sky glow goes to zero after dusk and would gate the effect
    // off at night for free. It does not: §7 deliberately holds a floor of starlight, airglow and
    // moonlight, and §21 AMPLIFIES that floor over snow (NightRadiance.FloorGlowFor multiplies it by
    // the same cavity gain this file reads). On a snowed-in overcast night the live harness read
    // skyGlow well above zero and this function returned a visible alpha — which would have paid for
    // the same snow twice, once through §21's multiplicative night arm and again through this
    // additive one, in exactly the conditions §21's night arm was built for.
    //
    // Subtracting the floor is the fix, and it is the RIGHT quantity rather than a convenient one:
    // the floor is precisely the light §21 has already amplified, so what remains above it is
    // precisely the light it has not. The two arms partition the brightness the same way this file's
    // residual partitions the amplification — no daytime/night branch, no second copy of §6b's
    // LightingSun -> LightingMoon handover threshold to keep in sync, and a continuous ramp through
    // dusk rather than a step.
    public static float DaylightAboveNightFloor(float skyGlow, float nightFloorGlow)
    {
        float daylight = Clamp01(skyGlow) - Clamp01(nightFloorGlow);
        return daylight > 0f ? daylight : 0f;
    }

    // The additive overlay alpha to draw this frame, in [0, maxIntensity].
    //
    // WHY IT SCALES WITH DAYLIGHT AT ALL. An additive quad adds a CONSTANT to the framebuffer, but
    // what the physics owes is a FRACTION of whatever light is already there — 97% more light is a
    // large absolute amount at noon and a negligible one at dusk. Multiplying by the map's own
    // daylight converts the ratio the cavity produces into the absolute quantity the overlay applies,
    // using the same 0..1 brightness scalar the rest of the mod already keys on.
    //
    // The clamp order matters: scale first, then clamp, so MaxIntensity is a ceiling on what reaches
    // the screen rather than a ceiling on the residual (which would silently change the shape of the
    // ramp below it).
    public static float GlareAlpha(
        float excess,
        float skyGlow,
        float nightFloorGlow,
        float intensityScale,
        float maxIntensity,
        bool inVacuum)
    {
        if (inVacuum)
            return 0f;

        if (excess <= 0f)
            return 0f;

        float alpha = excess * DaylightAboveNightFloor(skyGlow, nightFloorGlow) * intensityScale;
        if (alpha <= 0f)
            return 0f;

        return alpha > maxIntensity ? maxIntensity : alpha;
    }

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
}
