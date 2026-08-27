using System.Reflection;
using UnityEngine;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see <Compile Remove> in CelestialLighting.csproj)
// and compiled into TestMod/CelestialLighting.Probes.csproj instead, the same boundary every other
// file in this folder draws: this must never reach a player's game.
//
// WHY IT EXISTS. The settings screen is the mod's largest piece of UI and, until this, the only one
// that could not be photographed. RimWorld's own Dialog_ModSettings takes the Mod instance in its
// constructor, and the harness's RaiseWindow step can only construct a Window that has a public
// parameterless one — so there was no way to get the settings page in front of the camera at all,
// and a change to how a control DRAWS could only ever be argued for rather than shown.
//
// It is a shell, deliberately: it forwards straight to CelestialLightingSettingsMod
// .DoSettingsWindowContents, so what it photographs is the real page rather than a reconstruction of
// it. The one thing it adds is the scroll position, because the page is several screens long and the
// interesting part is never the top of it.
public class Dialog_SettingsShot : Window
{
    // Where to park the settings page's scroll view before drawing. Chosen to put the vector-light
    // block and its three sub-options in frame; adjust it and re-shoot when the block moves, which is
    // cheaper than any mechanism for finding a control by name would be.
    private const float ScrollTo = 470f;

    // CelestialLightingSettingsMod.scrollPosition is private, and this file compiles into a different
    // assembly, so there is no way to reach it but reflection. Resolved once and null-checked at the
    // call rather than asserted here: a rename should cost a shot that is scrolled to the wrong place,
    // not a NullReferenceException inside a Window's constructor.
    private static readonly FieldInfo ScrollField = typeof(CelestialLightingSettingsMod)
        .GetField("scrollPosition", BindingFlags.Instance | BindingFlags.NonPublic);

    // Big enough that a whole section of the page fits in one frame, and short of 1080 so the window
    // frame itself is visible and it is obvious the shot is of a window rather than of the screen.
    public override Vector2 InitialSize => new Vector2(900f, 900f);

    public Dialog_SettingsShot()
    {
        forcePause = true;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = false;
        doCloseX = false;
        doCloseButton = false;
    }

    public override void DoWindowContents(Rect inRect)
    {
        CelestialLightingSettingsMod mod = LoadedModManager.GetMod<CelestialLightingSettingsMod>();
        if (mod == null)
            return;

        // Re-applied every frame rather than once in the constructor: the page writes its own scroll
        // position back through the ref parameter each time it draws, so a one-off assignment would
        // last exactly one frame and the capture would arrive at the top of the list.
        ScrollField?.SetValue(mod, new Vector2(0f, ScrollTo));

        mod.DoSettingsWindowContents(inRect);
    }
}
