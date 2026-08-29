using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead —
// the same boundary CloudOpacityOverride draws, and for the same reason: this must never reach a
// player's game.
//
// WHY IT EXISTS. SetFeature can flip a bool and nothing else, and lamp glow reach is a float. A
// slider compared across two BOOTS is compared against a different pawn layout, a different tick
// and a different frame, which is exactly the comparison this repo's A/B discipline exists to
// avoid — and for this feature it would be worse than usual, because the thing being measured is a
// broad dim halo whose amplitude is the same order as the frame's own run-to-run noise. So each
// position worth pinning gets a key, and a scenario walks them inside one boot.
//
// IT MUST NOT WRITE CelestialLightingSettings.vectorLightReach, which is persisted: a run that
// crashed between arms would leave a player's own settings file holding a test value. The runtime
// holder is rewritten from the persisted field on the next ApplyToRuntime either way.
//
// AND IT MUST REBUILD, which is the difference from CloudOpacityOverride and the trap worth naming.
// Cloud opacity is read per frame by the draw, so writing the static is the whole job. Reach is
// BAKED: it decides the radius every visibility polygon on the map was cast to, and nothing about
// writing a static provokes a rebake. An arm that set the key and screenshotted would photograph
// the previous arm's polygons while every probe reported the new value — a frame that is confidently
// wrong rather than obviously broken.
public static class VectorLightReachOverride
{
    // THE SHIPPED DEFAULT, taken from the constant rather than repeated as a literal so this arm
    // cannot drift away from what a player gets by ticking the box — which is the one position in
    // the range most people will ever see, and therefore the one an A/B has to cover.
    public const string VibrantFeatureKey = "vector_light_reach_vibrant";

    public const float VibrantReach = VectorLightReachMath.DefaultReach;

    // The top of the slider. Pinned as its own arm rather than trusted, because the cost story and
    // the look story diverge here: this is where a lamp's silhouette scan is at its most expensive
    // and where the mid-field lift is most likely to read as the map being washed out rather than
    // as a warmer room. An arm nobody looks at is how a slider ships with an unusable top end.
    public const string MaxFeatureKey = "vector_light_reach_max";

    // The brightness axis, which is a different kind of knob and gets its own key for that reason:
    // it scales the excess rather than enlarging it, and it takes effect without a rebake. An arm
    // that moved both at once could not say which one the frame is showing.
    public const string BrightFeatureKey = "vector_light_brightness_max";

    // ASTRYL'S OWN CALIBRATION, which is what the checkbox actually starts at and therefore the one
    // brightness a live A/B has to cover. It sits BELOW the resting value, so this key dims where
    // the one above it lifts — an arm that assumed a single direction would read one of them as
    // broken.
    public const string DefaultBrightFeatureKey = "vector_light_brightness_default";

    private static bool vibrant;
    private static bool max;
    private static bool bright;
    private static bool defaultBright;

    public static void SetVibrant(bool enabled)
    {
        vibrant = enabled;
        Apply();
    }

    public static void SetMax(bool enabled)
    {
        max = enabled;
        Apply();
    }

    public static void SetBright(bool enabled)
    {
        bright = enabled;
        Apply();
    }

    public static void SetDefaultBright(bool enabled)
    {
        defaultBright = enabled;
        Apply();
    }

    private static void Apply()
    {
        // Max wins when both are on, stated rather than left to argument order — the same ruling
        // CloudOpacityOverride makes between its own two keys, and for the same reason: a scenario
        // that set both would otherwise depend on which line this method happens to read first.
        // WRITTEN UNCONDITIONALLY AND WITHOUT A REBUILD, because it is a material property the draw
        // recomputes every frame — the exact asymmetry the settings screen exposes as "free" against
        // the size slider's deferred rebake.
        VectorLightSettings.Brightness = BrightnessForFlags();

        float reach = ReachForFlags();

        // Nothing to rebuild if the value did not move. A ResetAll between scenarios in a suite
        // clears both keys, which lands here with the resting value already in place, and a
        // whole-map polygon rebuild per scenario boundary is a stutter for no changed pixel.
        if (reach == VectorLightSettings.Reach)
            return;

        VectorLightSettings.Reach = reach;

        // RebuildForReach, NOT ForceRebuild, and the choice is what makes this scenario worth
        // running. A settings screen moving this slider takes the light path — mark the rosters,
        // rebake — and ForceRebuild is the heavier one that also drops every mesh and texture. Both
        // ought to land on the same frame; an arm that used the thorough one could not tell you
        // whether the one that ships does, which is exactly the gap a live A/B exists to close.
        //
        // Synchronous either way, and it has to be: the harness runs one step per rendered frame, so
        // a step that queued the rebuild for later would let the next frame be captured against the
        // old bake. Both paths build the polygons before dirtying the sections for that reason.
        VectorLightRedraw.RebuildForReach();

        // Logged on every flip, not only on failure. PlanetsmithTiltOverride's rule: a silent test
        // hook is indistinguishable from the feature under test not working, and this one's visible
        // effect is a dim halo that a reader is entitled to doubt.
        Log.Message(
            "[CelestialLighting.Probes] Vector light override: reach " + reach
            + ", brightness " + VectorLightSettings.Brightness + ".");
    }

    // Max wins over the shipped default when both are set, stated rather than left to argument
    // order, for the reason the reach keys make the same ruling: a scenario that set both would
    // otherwise depend on which line this method happens to read first.
    private static float BrightnessForFlags()
    {
        if (bright)
            return VectorLightReachMath.MaxBrightness;

        return defaultBright
            ? VectorLightReachMath.DefaultBrightness
            : VectorLightReachMath.NoBrightness;
    }

    private static float ReachForFlags()
    {
        if (max)
            return VectorLightReachMath.MaxReach;

        return vibrant ? VibrantReach : VectorLightReachMath.NoReach;
    }
}
