namespace CelestialLighting;

// The single source of truth for which eclipse mode (DESIGN.md §10) is live. Pulled out into its own
// tiny static so every consumer — the darkening patch, the geometric trigger, the suppression patch,
// and the dev probe — reads exactly the same value and can never disagree. The decision *logic* for
// each mode lives in the pure, tested EclipseModeRules; this only holds the current selection.
public static class EclipseSettings
{
    // Defaults to UnnaturalOnly: out of the box we only *reshape* the darkening of the storyteller's
    // own eclipse (§10b) and fire no events of our own. This is the mod's shipped contract — no default
    // setting alters gameplay — and eclipse mode is the one place that contract could have been broken,
    // because natural (§10a) eclipses are real GameConditions and so move solar power and mood however
    // astronomically honest their timing is. Rare (~one per few game years) is still not never. So they
    // are opt-in: a player who wants them picks Both (geometric events on top of the storyteller's) or
    // NaturalOnly (geometric events instead of them). Backed by CelestialLightingSettings.eclipseMode
    // via ApplyToRuntime, so the choice survives a restart while this stays the one field everything
    // consults.
    public static EclipseMode Mode = EclipseMode.UnnaturalOnly;
}
