namespace CelestialLighting;

// Pure predicate for GameComponent_SkyFalloffRedraw: has a map's CurSkyGlow drifted far enough from
// what its lighting-overlay meshes were last baked against (Patch_IndoorSkyOcclusion, via
// SkyFalloffSource) to be worth another whole-map GroundGlow rebuild. See the GameComponent's own
// header for why this is safe to gate on drift rather than a fixed cadence, unlike the tombstoned
// MapComponent_SunShadowAxis this pattern superficially resembles.
public static class SkyFalloffRedrawMath
{
    // Below this, CapOcclusion's cell alpha has not moved far enough to read as a visible change —
    // see the live A/B this shipped with (Tests/Scenarios) for the measured CIELAB ΔE this threshold
    // was chosen against. Above it, paying one whole-map rebuild is worth staying correct.
    public const float DefaultThreshold = 0.05f;

    public static bool ShouldRedraw(float bakedGlow, float curGlow, float threshold) =>
        System.MathF.Abs(curGlow - bakedGlow) >= threshold;
}
