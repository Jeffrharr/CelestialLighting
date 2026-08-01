using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead.
//
// 1 when Planetsmith is installed, its per-world tilt is readable, and the feature is on.
//
// Separate from planetsmith_tilt because the tilt alone cannot distinguish "not active" from
// "active on a world generated at Planetsmith's 23.4 default" — those read 23.44 and 23.4, a
// difference smaller than any tolerance worth writing. This probe answers the binding question and
// that one answers the geometry question.
public sealed class PlanetsmithActiveProbe : IProbe
{
    public string Name => "planetsmith_active";

    public float Read(Map map) => PlanetsmithCompat.Active ? 1f : 0f;
}
