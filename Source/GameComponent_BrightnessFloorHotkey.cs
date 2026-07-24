using RimWorld;
using Verse;

namespace CelestialLighting;

// DefOf handle for our keybinding so the hotkey poll below is a compile-checked field reference
// rather than a stringly-typed lookup. RimWorld resolves this from Defs/KeyBindings/*.xml at load.
[DefOf]
public static class CelestialLightingKeyDefOf
{
    public static KeyBindingDef CelestialLighting_ToggleBrightnessFloor;

    static CelestialLightingKeyDefOf()
    {
        DefOfHelper.EnsureInitializedInCtor(typeof(CelestialLightingKeyDefOf));
    }
}

// Polls the "toggle minimum brightness" keybinding and flips the accessibility floor on/off. A
// GameComponent is the right host: RimWorld auto-instantiates every GameComponent subclass with a
// (Game) constructor when a game loads, and drives GameComponentOnGUI each frame the game is running
// — which is exactly (and only) when a "let me see to play right now" toggle is useful.
//
// This is a thin adapter with no math: the actual floor logic lives in the pure
// BrightnessFloorMath, and this only flips a persisted boolean. Kept out of Formulas/pure files
// because it necessarily touches live input (KeyBindingDef, Messages).
public class GameComponent_BrightnessFloorHotkey : GameComponent
{
    // Required (Game) constructor: RimWorld's component filler calls it via reflection. Nothing to
    // store — the toggle target is the mod's static settings.
    public GameComponent_BrightnessFloorHotkey(Game game)
    {
    }

    public override void GameComponentOnGUI()
    {
        CelestialLightingSettings settings = CelestialLightingSettingsMod.Settings;
        if (settings == null)
            return;

        // KeyDownEvent is true on exactly the frame the key goes down and consumes the event, so this
        // fires once per press rather than every OnGUI pass while held.
        if (!CelestialLightingKeyDefOf.CelestialLighting_ToggleBrightnessFloor.KeyDownEvent)
            return;

        settings.brightnessFloorEnabled = !settings.brightnessFloorEnabled;
        CelestialLightingSettingsMod.Save();
        AnnounceToggle(settings.brightnessFloorEnabled);
    }

    private static void AnnounceToggle(bool enabled)
    {
        string state = enabled ? "on" : "off";
        // SilentInput: a quiet confirmation toast, no alert sound — this is a UI convenience, not an
        // in-world event worth logging to the message history.
        Messages.Message($"Minimum brightness floor: {state}", MessageTypeDefOf.SilentInput, historical: false);
    }
}
