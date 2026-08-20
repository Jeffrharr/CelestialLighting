namespace CelestialLighting;

// §27's one per-effect intensity, on the same shape as PurpleLightSettings / IndoorOcclusionSettings
// — a static holder the settings screen writes and the draw path reads, deliberately the same rather
// than a third variation.
//
// SEPARATE FROM THE PRESET BUNDLE, for the reason polarNightBlueStrength and purpleLightStrength are:
// this is "how strong should this one effect be", not one of the taste axes Cinematic/Realistic own,
// so moving it must not flip the preset radio to Custom.
public static class VectorLightSettings
{
    // Scales the additive beam that rides on top of phase 3's mask, 0 to 1. See
    // VectorLightMath.MaskBeamStrengthFor for what it multiplies and why the crossfade is not scaled
    // alongside it.
    public static float BeamStrength = VectorLightMath.DefaultBeamStrengthScale;

    // §27 phase 3c. Separate from BeamStrength above and deliberately NOT folded into it: that one
    // scales a term which also lifts the room, so it is capped at 1 and its whole purpose is
    // restraint. This one scales a term that is provably zero in the room, so it is allowed above 1
    // and its purpose is to undo a resolution loss. Same word, opposite jobs.
    public static float OwedBeamGain = VectorLightMath.DefaultOwedBeamGain;

    // §27 phase 3d. How brightly the drawn beam adds, in the additive pass's own units -- NOT in the
    // 0-255 glow space OwedBeamGain works in. The two are different lanes and are deliberately not
    // shared: the gain compensates a resolution loss in vanilla's mesh, this one calibrates an
    // additive draw that sits above the sky's multiply, and folding them together is what made
    // DefaultStrength unlandable across phases 1 and 2.
    public static float OwedLayerStrength = VectorLightMath.DefaultOwedLayerStrength;
}
