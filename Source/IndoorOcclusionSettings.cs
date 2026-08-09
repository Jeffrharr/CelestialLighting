namespace CelestialLighting;

// Runtime-tunable knobs for §7b indoor sky occlusion, mirroring NightRadianceSettings' shape: a plain
// struct of primitives with a live `Current`, written wholesale by the settings screen
// (CelestialLightingSettings.ApplyToRuntime) and read by the adapter. Deliberately Verse-free so the
// pure core and its offline tests never need a game reference.
public struct IndoorOcclusionSettings
{
    // How much sky a baseline (vanilla-tier) door lets past — see IndoorOcclusionMath.DefaultDoorSkyLeak
    // for why doors are special-cased at all. Per-door leak (IndoorOcclusionMath.DoorSkyLeakFor) scales
    // this down for tougher opaque doors and ignores it entirely for a door with blockLight false, so
    // this slider is best read as "how much a plain wood/metal door leaks," not a global door leak.
    public float DoorSkyLeak;

    // The fraction of sky a roofed cell keeps no matter how sealed it is — the one floor that reaches
    // interiors. 0 (the default) lets sealed rooms go fully black; see
    // IndoorOcclusionMath.DefaultMinIndoorBrightness for why it is a knob. CapOcclusion clamps it, so
    // no reconciliation layer sits between this and the pure core.
    public float MinIndoorBrightness;

    public static IndoorOcclusionSettings Current = Defaults;

    public static IndoorOcclusionSettings Defaults => new IndoorOcclusionSettings
    {
        DoorSkyLeak = IndoorOcclusionMath.DefaultDoorSkyLeak,
        MinIndoorBrightness = IndoorOcclusionMath.DefaultMinIndoorBrightness,
    };
}
