using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead.
//
// 1 when Realistic Planets 2 is installed, a game is loaded, its per-world tilt step is readable,
// and the feature is on.
//
// Separate from the declination probe for the same reason PlanetsmithActiveProbe is separate from
// planetsmith_tilt: a declination alone cannot tell "the interop is off" from "the interop is on and
// this world happens to sit at the tilt we would have used anyway", and RP2's default step (Normal,
// 22.5 degrees) is within a degree of our own 23.44. This probe answers the binding question so the
// other one is free to answer the geometry question.
public sealed class RealisticPlanetsActiveProbe : IProbe
{
    public string Name => "realistic_planets_active";

    public float Read(Map map) => RealisticPlanetsCompat.Active ? 1f : 0f;
}
