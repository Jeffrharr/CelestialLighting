namespace CelestialLighting;

// The three ways the eclipse feature (DESIGN.md §10) can behave, chosen by a radio in settings.
// Natural (§10a) fires real geometric eclipse *events*; unnatural (§10b) only reshapes the darkening
// of the storyteller's own random eclipse. They are independent, so all three combinations are valid:
public enum EclipseMode
{
    // Only reshape the vanilla/storyteller eclipse with the scripted fly-in/park/fly-out darkening.
    // No geometric events are fired. This was the mod's original visual-only default.
    UnnaturalOnly,

    // Fire real short eclipses from the modeled moon's geometry (natural darkening ramp), and suppress
    // the storyteller's random eclipse so the two don't double up. Purely astronomical timing.
    NaturalOnly,

    // Both at once (default): geometric eclipses fire and render natural, AND the storyteller's random
    // eclipses still fire and render unnatural. Each active eclipse is darkened by whichever kind it
    // is (see EclipseModeRules.RendersNatural). The player sees both flavours over a long game.
    Both,
}

// Pure decision rules for the eclipse mode — System-only, no Verse, so they link into the offline
// test project and the exact branching that runs in-game is the exact branching under test. The thin
// Verse adapters (GameComponent_NaturalEclipse, the darkening patch, the suppression patch, the probe)
// all route their mode questions through here so they can never disagree about what a mode means.
public static class EclipseModeRules
{
    // Whether the geometric trigger should fire real eclipse events. True for the two modes that
    // include natural eclipses; false for unnatural-only.
    public static bool NaturalTriggerActive(EclipseMode mode) =>
        mode == EclipseMode.NaturalOnly || mode == EclipseMode.Both;

    // Whether the storyteller's random Eclipse incident should be stood down. Only in NaturalOnly:
    // there the geometric eclipses are meant to be the *sole* source, so a random one would be a
    // double-up. In Both we deliberately keep the random ones, and in UnnaturalOnly they are the whole
    // point.
    public static bool SuppressRandomEclipse(EclipseMode mode) =>
        mode == EclipseMode.NaturalOnly;

    // Which darkening ramp a given active eclipse should use. In the single-flavour modes every
    // eclipse renders that flavour regardless of origin (so a harness- or mod-injected Eclipse still
    // renders correctly). In Both, it depends on whether *we* fired this particular condition from
    // geometry (conditionIsNatural) — geometric ones render natural, the storyteller's render unnatural.
    public static bool RendersNatural(EclipseMode mode, bool conditionIsNatural) => mode switch
    {
        EclipseMode.NaturalOnly => true,
        EclipseMode.UnnaturalOnly => false,
        _ => conditionIsNatural,
    };
}
