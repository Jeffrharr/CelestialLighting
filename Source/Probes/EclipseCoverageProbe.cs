using RimWorld;
using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool. See
// RimWorldTestHarness/DESIGN.md's "Where probe tests live" for the full reasoning.
//
// Reads back the exact sky-lerp factor Patch_EclipseDarkening writes for the eclipse currently active
// on this map (DESIGN.md §10) — re-derived through the same shared EclipseMath.SkyLerpFactorAtProgress
// selector, from the same EclipseIntegration.ProgressOf progress and the same EclipseSettings mode
// flag the patch uses — so a live scenario can pin the coverage ramp end-to-end against a running
// game rather than only the offline unit tests. Returns 0 when no Eclipse condition is active.
public sealed class EclipseCoverageProbe : IProbe
{
    public string Name => "eclipse_coverage";

    public float Read(Map map)
    {
        GameCondition eclipse = map.gameConditionManager.GetActiveCondition(GameConditionDefOf.Eclipse);
        if (eclipse == null)
            return 0f;

        float progress = EclipseIntegration.ProgressOf(eclipse);
        // Mirror the darkening patch exactly, magnitude included, so the probe reads back the same
        // central-or-partial value the sky is actually being lerped to.
        return (float)EclipseMath.SkyLerpFactorAtProgress(
            progress, EclipseSettings.NaturalEclipseEnabled, EclipseIntegration.ActiveNaturalMagnitude);
    }
}
