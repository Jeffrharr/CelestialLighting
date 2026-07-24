namespace CelestialLighting;

// The single toggle that selects between the two eclipse concepts in DESIGN.md §10. Pulled out into
// its own tiny static so both consumers — the darkening patch (which picks the natural vs unnatural
// ramp shape) and EclipseIntegration (which decides whether to fire/suppress the real event) — read
// exactly the same flag and can never disagree about which mode is live.
public static class EclipseSettings
{
    // OFF by default: the natural (§10a) astronomical eclipse changes *when* and *how long* a gameplay
    // event occurs, so it steps one notch outside this mod's visual-only remit and must be opt-in.
    // With this false the mod is purely cosmetic: the vanilla eclipse keeps its random timing and
    // duration, and we only reshape its darkening into the unnatural (§10b) fly-in/park/fly-out ramp.
    //
    // TODO(integration): once the mod gains its ModSettings screen (see DESIGN.md "Settings, presets,
    // brightness floor"), back this with a persisted user toggle instead of a plain static field so
    // the choice survives a restart. A static keeps the default unambiguously off without inventing a
    // settings-persistence layer inside this feature branch.
    public static bool NaturalEclipseEnabled = false;
}
