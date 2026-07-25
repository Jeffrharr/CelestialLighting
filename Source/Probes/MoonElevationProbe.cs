using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// The moon's altitude above the horizon, in degrees, from the same MoonPosition adapter every moon
// consumer uses. Negative means the moon is down.
//
// This exists to stop a moon scenario from silently testing nothing. moon_illumination reports PHASE
// only, so a 0.99 reading says "full moon" while the moon may be well below the horizon — which is
// exactly how the first §6a A/B produced two identical frames and looked like a failed fix rather
// than a badly-chosen night. A scenario that asserts elevation as well can only pass when there is
// genuinely a moon in the sky to cast the shadow under test.
public sealed class MoonElevationProbe : IProbe
{
    public string Name => "moon_elevation";

    public float Read(Map map) => MoonPosition.SkyForMap(map).ElevationDegrees;
}
