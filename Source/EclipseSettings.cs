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
    // This static is the single source of truth the patches/trigger read; it is backed by the mod's
    // persisted settings — CelestialLightingSettings.naturalEclipse is pushed here by ApplyToRuntime
    // at startup and whenever the settings window changes, so the choice survives a restart while this
    // stays the one flag everything consults. When on, GameComponent_NaturalEclipse fires the real
    // short Eclipse from the modeled moon's geometry and Patch_SuppressRandomEclipse stands the random
    // Eclipse incident down so the two never double-fire.
    public static bool NaturalEclipseEnabled = false;
}
