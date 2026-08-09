namespace CelestialLighting;

// Runtime-tunable knobs for §7b indoor sky occlusion, mirroring NightRadianceSettings' shape: a plain
// struct of primitives with a live `Current`, written wholesale by the settings screen
// (CelestialLightingSettings.ApplyToRuntime) and read by the adapter. Deliberately Verse-free so the
// pure core and its offline tests never need a game reference.
public struct IndoorOcclusionSettings
{
    // The fraction of sky a roofed cell keeps no matter how sealed it is — the one floor that reaches
    // interiors. 0 (the default) lets sealed rooms go fully black; see
    // IndoorOcclusionMath.DefaultMinIndoorBrightness for why it is a knob. CapOcclusion clamps it, so
    // no reconciliation layer sits between this and the pure core.
    public float MinIndoorBrightness;

    public static IndoorOcclusionSettings Current = Defaults;

    public static IndoorOcclusionSettings Defaults => new IndoorOcclusionSettings
    {
        MinIndoorBrightness = IndoorOcclusionMath.DefaultMinIndoorBrightness,
    };
}
