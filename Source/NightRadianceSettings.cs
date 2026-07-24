namespace CelestialLighting;

// Runtime-tunable knobs for §7 night radiance. Each source is independently adjustable, per the
// issue: turning AtmosphericGlowEnabled off (or sliding the two floors to zero) delivers the user's
// original "true pitch-black unlit nights" ask with no special-case code path — pitch-black is just
// the floors at zero on a moonless night.
//
// TODO(integration): back this with the mod's ModSettings screen once the settings/presets
// subsystem (DESIGN.md "Settings, presets, and the brightness floor") exists. For now it holds the
// DESIGN defaults so the feature is fully functional standalone; the settings UI, when it lands,
// only needs to write these same fields (and persist them via Scribe).
public struct NightRadianceSettings
{
    // Master toggle for the two constant atmospheric floors (starlight + airglow). Default on gives
    // the atmospheric look; off leaves only moonlight, so a new-moon night is genuinely black.
    public bool AtmosphericGlowEnabled;

    public float StarlightGlow;
    public float AirglowGlow;
    public float MaxMoonlightGlow;

    // The live settings the patch reads. Reassigned wholesale by the settings UI (see TODO above).
    public static NightRadianceSettings Current = Defaults;

    public static NightRadianceSettings Defaults => new NightRadianceSettings
    {
        AtmosphericGlowEnabled = true,
        StarlightGlow = NightRadianceMath.DefaultStarlightGlow,
        AirglowGlow = NightRadianceMath.DefaultAirglowGlow,
        MaxMoonlightGlow = NightRadianceMath.DefaultMaxMoonlightGlow,
    };
}
