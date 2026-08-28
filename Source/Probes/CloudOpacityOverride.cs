using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead — the
// same boundary CloudCoverFractionOverride and PlanetsmithTiltOverride draw, and for the same
// reason: this must never reach a player's game.
//
// WHY THIS EXISTS. The harness can flip a bool (SetFeature) and nothing else, so a knob that is a
// FLOAT has no way to move inside one boot — and a slider compared across two boots is compared
// against a different cloud layout, a different tick, and a different frame, which is exactly the
// comparison this repo's live A/B discipline exists to avoid. So each position of the slider worth
// pinning gets a key of its own, and a scenario steps through them without the game restarting.
//
// NO HARMONY PATCH, unlike its sibling: the value the mod reads is a plain static this can simply
// write. What it must NOT do is write CelestialLightingSettings.cloudOpacity — that is persisted,
// and a run that crashed between arms would leave a player's own settings file holding a test value
// (see the harness's asset ledger for why this codebase is careful about exactly that). The runtime
// holder is rewritten from the persisted field on the next ApplyToRuntime either way.
public static class CloudOpacityOverride
{
    // Quarter opacity: plainly thinner than shipped without being a clamp boundary, the same reason
    // CloudCoverFractionOverride.ForcedFraction is 0.35 rather than 0 or 1. A midpoint would be the
    // obvious pick and is the worse one — half of the shipped amplitude still lands inside the range
    // a frame's own noise covers at low sun, so an arm that measured nothing would be ambiguous
    // between "the slider does nothing" and "this hour cannot show it".
    public const string ReducedFeatureKey = "cloud_opacity_reduced";

    public const float ReducedOpacity = 0.25f;

    // Zero, which the pure core promises is a TRUE no-op — CloudSheetMath.AmplitudeAtOpacity returns
    // a zero amplitude, SheetAlphaWithAmplitude returns zero alpha, and CloudSheetOverlay makes no
    // draw call at all. That promise is worth a live pin rather than only an offline one: "the
    // bottom of the slider is identical to unticking the feature" is a claim about the SCREEN, and
    // the offline test can only show it is a claim about the arithmetic.
    public const string ZeroFeatureKey = "cloud_opacity_zero";

    private static bool reduced;
    private static bool zero;

    public static void SetReduced(bool enabled)
    {
        reduced = enabled;
        Apply();
    }

    public static void SetZero(bool enabled)
    {
        zero = enabled;
        Apply();
    }

    private static void Apply()
    {
        // Zero wins when both are on, stated rather than left to argument order — the same ruling
        // CloudCoverFractionOverride makes between its own two keys, for the same reason: a scenario
        // that set both would otherwise depend on which line this method happens to read first.
        float opacity = OpacityForFlags();
        CloudSheetSettings.OpacityScale = opacity;

        // Logged on every flip, not only on failure. PlanetsmithTiltOverride's rule: a silent test
        // hook is indistinguishable from the feature under test not working, and this one writes a
        // value whose whole visible effect is "there is less cloud than you expected".
        Log.Message($"[CelestialLighting.Probes] Cloud opacity override: {opacity}.");
    }

    private static float OpacityForFlags()
    {
        if (zero)
            return 0f;

        return reduced ? ReducedOpacity : CloudSheetMath.DefaultOpacityScale;
    }
}
