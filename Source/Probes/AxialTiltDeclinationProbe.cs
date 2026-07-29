using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead.
//
// Reads the declination the mod is ACTUALLY running on, off SolarPosition.Inputs — the memoized
// struct every sun-derived effect resolves through — rather than recomputing a formula.
//
// This is deliberately not ShadowLeanProbe. That probe calls Formulas.SolarDeclinationDegrees
// directly, so it reports our own model no matter what, and is blind to the Realistic Axial Tilt
// seam by construction: with RAT driving the sun it would keep happily reporting our number while
// the game rendered theirs. Reading Inputs.Declination is the only way to assert end-to-end that
// the handover actually took effect.
//
// The interop is easy to measure because the two models disagree in PHASE, not just magnitude: at
// day-of-year 15 ours (-cos) gives 0 while RAT's (sin) gives its full tilt. See
// Tests/Scenarios/axial_tilt_interop.json.
public sealed class AxialTiltDeclinationProbe : IProbe
{
    public string Name => "axial_tilt_declination";

    public float Read(Map map) => SolarPosition.InputsForMap(map).Declination;
}
