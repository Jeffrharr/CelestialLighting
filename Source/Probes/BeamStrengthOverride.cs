using RimWorldTestHarness.Mod.Features;

namespace CelestialLighting.Probes;

// Forces §27 phase 3d's beam to a named strength, so one boot can photograph several and the level
// gets CHOSEN rather than guessed.
//
// WHY THIS EXISTS AT ALL. VectorLightSettings.OwedLayerStrength is a persisted setting, not a feature
// flag, and the harness's SetFeature step only speaks booleans. Without a seam like this a sweep costs
// one rebuild and one boot per value, which in practice means the value never gets swept — the first
// guess ships and is defended later. That is exactly how this layer arrived at 0.4 and read, in the
// reviewer's words, "WAYYY too bright".
//
// A KEY PER LEVEL RATHER THAN A NUMERIC ARG, matching CloudCoverFractionOverride, whose header gives
// the reasoning: a scenario pins values against a named constant, so moving what a key means silently
// rewrites what every arm using it measured. New level, new key.
//
// Excluded from the shipped DLL by the <Compile Remove> in CelestialLighting.csproj and compiled into
// TestMod/CelestialLighting.Probes.csproj instead. It must never reach a player's game.
public static class BeamStrengthOverride
{
    public const string QuarterKey = "vector_light_beam_strength_quarter";
    public const string HalfKey = "vector_light_beam_strength_half";
    public const string TenthKey = "vector_light_beam_strength_tenth";

    // Bracketing the shipped 0.4 on both sides. The reviewer's complaint was brightness, so the sweep
    // is weighted BELOW the current value -- two darker steps and one brighter -- rather than centred
    // on it. A sweep that only confirms the incumbent is not a sweep.
    public const float Quarter = 0.25f;
    public const float Half = 0.5f;
    public const float Tenth = 0.1f;

    // Restored rather than remembered: a scenario that turns an override on and never off would leave
    // every later arm at that level while its own comments claim the shipped default. Reading the
    // constant back is what makes each key's `false` a real reset.
    public static void Register()
    {
        FeatureRegistry.Register(TenthKey, on => Apply(on, Tenth), defaultEnabled: false);
        FeatureRegistry.Register(QuarterKey, on => Apply(on, Quarter), defaultEnabled: false);
        FeatureRegistry.Register(HalfKey, on => Apply(on, Half), defaultEnabled: false);
    }

    private static void Apply(bool enabled, float level)
    {
        VectorLightSettings.OwedLayerStrength =
            enabled ? level : VectorLightMath.DefaultOwedLayerStrength;

        // The owed mesh is cached per emitter and the strength rides in the property block rather than
        // the geometry -- but the redraw is kept anyway, because an arm that photographs before the
        // next natural rebuild is the failure mode every other feature key here already guards.
        VectorLightRedraw.ForceRebuild();
    }
}
