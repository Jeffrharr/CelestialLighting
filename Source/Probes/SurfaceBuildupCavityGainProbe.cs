using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// Reads §21's cavity gain through the exact same SurfaceBuildup.CavityGainFor(Map) entry point
// NightRadiance.FloorGlowFor consumes, so a scenario can pin the number the night floor is actually
// multiplied by rather than inferring it from a handful of screen pixels.
//
// Added for issue #100: before that fix, this value was insensitive to §22's continuous cloud-cover
// fraction on a Clear map — it read exactly the ClearSkyAlbedo backscatter (gain ~1.07 at full snow)
// no matter how cloudy §22's drift said the Clear sky currently was. After the fix, on a Clear map
// this tracks SurfaceBuildup.CloudOpacityOrClear's substitution of cloud_cover_fraction for §13's
// (always-0-on-Clear) classifier, so a scenario can pair this probe with cloud_cover_fraction and
// show the two moving together rather than the cavity sitting flat while the cloud fraction drifts
// underneath it.
public sealed class SurfaceBuildupCavityGainProbe : IProbe
{
    public string Name => "cavity_gain";

    public float Read(Map map) => SurfaceBuildup.CavityGainFor(map);
}
