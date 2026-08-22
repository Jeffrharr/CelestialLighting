using HarmonyLib;
using Verse;

namespace CelestialLighting;

// Raises the one-time "what's new" notice (UpdateNotice / Dialog_UpdateNotice) at the main menu.
//
// WHY `UIRoot_Entry.Init` AND NOT THE STATIC CONSTRUCTOR. CelestialLightingMod's
// [StaticConstructorOnStartup] runs during the loading screen, before the entry UI root exists —
// there is no window stack to add to yet. Init is the first point where there is, and vanilla
// itself adds a Dialog_MessageBox from inside this very method (the missing-Steam-client warning),
// so this is a proven place to put a window rather than a plausible one. It also runs after
// VersionUpdateDialogMaker, which puts our notice on top of RimWorld's own version dialog rather
// than under it.
//
// IT RUNS MORE THAN ONCE — returning to the main menu from a game re-inits the entry root — which is
// why "only once" is a persisted acknowledgement (CelestialLightingSettings.updateNoticeVersion)
// rather than a static bool here. A static bool would also be correct for one session and wrong the
// moment a player quit before answering.
//
// Postfix rather than Prefix so nothing about the entry UI depends on our timing: if this throws,
// the menu has already been built. UpdateNotice.ShowIfDue swallows anything that escapes anyway.
[HarmonyPatch(typeof(UIRoot_Entry), nameof(UIRoot_Entry.Init))]
public static class Patch_UpdateNotice
{
    static void Postfix()
    {
        UpdateNotice.ShowIfDue();
    }
}
