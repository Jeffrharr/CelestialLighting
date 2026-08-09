using System.Reflection;
using HarmonyLib;
using Verse;

namespace CelestialLighting.Probes;

// Vanilla Expanded Framework's GameComponentUtility.LoadedGame postfix opens a blocking
// Dialog_NewFactionSpawning whenever the loaded save is missing a FactionDef it tracks --
// minimal_colony.rws predates Royalty's Empire faction, so this fires on every scenario that adds
// VEF to the mod list (needed for interop scenarios like door_leak_by_type, which requires VEF as
// ReBuild: Doors and Corners' hard dependency). Nothing in the harness can close an arbitrary
// Window -- HarnessDebugActions.CloseDevWindows only targets the LudeonTK.EditWindow family -- so
// the dialog sits over every subsequent screenshot for the run's lifetime.
//
// Soft-patched by reflection, not a project reference: VEF is a third-party workshop mod with no
// assembly this repo can compile against, and this must no-op cleanly on every scenario that
// doesn't load it.
[StaticConstructorOnStartup]
public static class VefNewFactionDialogSuppressor
{
    static VefNewFactionDialogSuppressor()
    {
        System.Type? dialogType = AccessTools.TypeByName("VEF.Factions.Dialog_NewFactionSpawning");
        if (dialogType == null)
            return;

        MethodInfo? openDialog = AccessTools.Method(dialogType, "OpenDialog");
        if (openDialog == null)
            return;

        new Harmony("celestiallighting.probes.vefnewfactiondialogsuppressor").Patch(
            openDialog, prefix: new HarmonyMethod(typeof(VefNewFactionDialogSuppressor), nameof(Prefix)));
    }

    private static bool Prefix() => false;
}
