namespace CelestialLighting;

// How opaque the drawn cloud sheets are, in one tiny static — the same shape as PurpleLightSettings,
// OzoneTwilightSettings and PurkinjeSettings, so the draw path and the probe read one value and
// cannot disagree about what is on screen.
//
// SEPARATE FROM THE PRESET BUNDLE, for the reason polarNightBlueStrength and purpleLightStrength are:
// this is "how strong should this one effect be", not one of the taste axes Cinematic/Realistic own,
// so moving it must not flip the preset radio to Custom.
//
// NOT THE SAME KNOB AS CloudLayers.SheetAmplitudeScale, which sits beside it in the same
// multiplication. That one is a dev seam nothing the player can reach ever writes — it exists so a
// harness sweep can compare several calibrations of the constant inside one boot. This one is the
// player's, and it scales whichever calibration the build is shipping rather than replacing it.
public static class CloudSheetSettings
{
    // 1 == the shipped look, 0 == no drawn cloud at all. See CloudSheetMath.AmplitudeAtOpacity for
    // what it multiplies, why the range does not go above 1, and why the bottom end is a true no-op.
    public static float OpacityScale = CloudSheetMath.DefaultOpacityScale;
}
