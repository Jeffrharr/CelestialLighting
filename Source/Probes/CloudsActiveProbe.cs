using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead.
//
// 1 when the Clouds interop considers Clouds present — which is the real load-order read, or
// whatever CloudsCompatOverride has forced for an A/B.
//
// WHY A SEPARATE PROBE WHEN THE ALPHA PINS ALREADY IMPLY IT. Because they only imply it in one
// direction. A scenario pinning cloud_sheet_alpha to 0 with Clouds loaded fails loudly if the
// interop is broken — but it passes just as happily if Clouds never loaded at all and some
// unrelated gate zeroed the sheet, which is exactly the "confident, wrong" run the harness notes
// warn about for a missing --mod flag. This probe separates "Clouds is here" from "our clouds
// stood down", so a run can assert both rather than inferring one from the other.
public sealed class CloudsActiveProbe : IProbe
{
    public string Name => "clouds_mod_active";

    public float Read(Map map) => CloudsCompat.ModIsInstalled ? 1f : 0f;
}
