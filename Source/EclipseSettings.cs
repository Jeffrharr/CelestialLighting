namespace CelestialLighting;

// The single source of truth for which eclipse mode (DESIGN.md §10) is live. Pulled out into its own
// tiny static so every consumer — the darkening patch, the geometric trigger, the suppression patch,
// and the dev probe — reads exactly the same value and can never disagree. The decision *logic* for
// each mode lives in the pure, tested EclipseModeRules; this only holds the current selection.
public static class EclipseSettings
{
    // Defaults to Both: the mod ships with geometric (§10a) eclipses firing at real astronomical times
    // AND the storyteller's random (§10b) eclipses still occurring, each rendered with its own ramp.
    // Note this makes natural eclipses on by default, which does add rare (~one per few game years)
    // real Eclipse events on top of vanilla's — a deliberate choice, exposed as a radio in settings so
    // a player can pick NaturalOnly (suppress the random ones) or UnnaturalOnly (pure cosmetic, no
    // extra events). Backed by CelestialLightingSettings.eclipseMode via ApplyToRuntime, so the choice
    // survives a restart while this stays the one field everything consults.
    public static EclipseMode Mode = EclipseMode.Both;
}
