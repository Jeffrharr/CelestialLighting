namespace CelestialLighting;

// Runtime-tunable knobs for §7c native sky falloff, mirroring IndoorOcclusionSettings' shape: a plain
// struct of primitives with a live `Current`, written wholesale by the settings screen
// (CelestialLightingSettings.ApplyToRuntime) and read by SkyFalloffSource. Deliberately Verse-free so
// the pure core and its offline tests never need a game reference.
//
// Both fields only matter for cells no other mod has already lit — SkyFalloffSource defers to
// IndoorGlowPassthrough outright wherever it answers, so these never feed such a cell.
public struct NativeSkyFalloffSettings
{
    // How far (in BFS-layer cells) the gradient reaches from an opening before it hits zero. Higher =
    // slower falloff, reaching further into a room; see NativeSkyFalloffGrid's own header for why this
    // is a whole-map BFS cap rather than a per-cell radius.
    public int MaxDepth;

    // Sky brightness right at the opening (depth 1), as a percentage of curSkyGlow, before the
    // depth/maxDepth taper is applied. See NativeSkyFalloffMath.DefaultPassThroughPercent for why the
    // shipped default (25) is lower than Ambient Light's own (55) — the higher figure read as lighting
    // up the whole room rather than grading near the door.
    public float PassThroughPercent;

    public static NativeSkyFalloffSettings Current = Defaults;

    public static NativeSkyFalloffSettings Defaults => new NativeSkyFalloffSettings
    {
        MaxDepth = NativeSkyFalloffMath.DefaultMaxDepth,
        PassThroughPercent = NativeSkyFalloffMath.DefaultPassThroughPercent,
    };
}
