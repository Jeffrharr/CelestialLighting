using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll and compiled into the probe bridge instead, on the
// same boundary as CloudOpacityOverride — and for the same two reasons. The harness can flip a bool
// and nothing else, so each non-default position of the "Clouds when zoomed in" radio gets a key of
// its own; and this writes the runtime static, never CelestialLightingSettings.cloudZoomMode, so a
// run that crashes between arms cannot leave a test value in a player's settings file.
public static class CloudZoomModeOverride
{
    public const string FadeFeatureKey = "cloud_zoom_fade";

    public const string FlyThroughFeatureKey = "cloud_zoom_flythrough";

    private static bool fade;
    private static bool flyThrough;

    public static void SetFade(bool enabled)
    {
        fade = enabled;
        Apply();
    }

    public static void SetFlyThrough(bool enabled)
    {
        flyThrough = enabled;
        Apply();
    }

    // FlyThrough wins when both are on, stated rather than left to which key a scenario set last.
    private static void Apply()
    {
        CloudZoomMath.Mode mode = flyThrough ? CloudZoomMath.Mode.FlyThrough
            : fade ? CloudZoomMath.Mode.Fade
            : CloudZoomMath.Mode.Off;
        CloudSheetSettings.ZoomMode = mode;

        // Logged on every flip: a silent test hook is indistinguishable from the feature not working.
        Log.Message($"[CelestialLighting.Probes] Cloud zoom mode override: {mode}.");
    }
}
