namespace CelestialLighting;

// Runtime-tunable knobs for §7b indoor sky occlusion, mirroring NightRadianceSettings' shape: a plain
// struct of primitives with a live `Current`, written wholesale by the settings screen
// (CelestialLightingSettings.ApplyToRuntime) and read by the adapter. Deliberately Verse-free so the
// pure core and its offline tests never need a game reference.
public struct IndoorOcclusionSettings
{
    // How much sky a door lets past (see IndoorOcclusionMath.DefaultDoorSkyLeak for why doors are
    // special-cased at all).
    public float DoorSkyLeak;

    // The resolved accessibility brightness floor, already gated by its enable checkbox — 0 when the
    // floor is off. Copied in rather than read from CelestialLightingSettings directly so the adapter
    // has exactly one place to look and the pure core stays settings-object-agnostic.
    public float BrightnessFloor;

    public static IndoorOcclusionSettings Current = Defaults;

    public static IndoorOcclusionSettings Defaults => new IndoorOcclusionSettings
    {
        DoorSkyLeak = IndoorOcclusionMath.DefaultDoorSkyLeak,
        BrightnessFloor = 0f,
    };
}
