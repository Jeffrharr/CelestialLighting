using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead.
//
// Reports the obliquity the mod is running this world on — Planetsmith's when it is readable and the
// feature is on, ours otherwise. Paired with axial_tilt_declination (which reads the declination
// SolarPosition actually handed the renderer) it separates the two ways this interop can fail: a
// tilt that reads correctly but never reaches the sky, and a sky that moved for some other reason.
//
// Unlike the RAT interop, the two arms here do NOT differ in phase — Planetsmith has no seasonal
// model, so its tilt only scales our existing curve. That has a consequence for how scenarios
// measure it: at an equinox both arms give a declination of zero and the flag looks inert, so a
// scenario must sample a day where DeclinationSign is far from zero (day 0 or day 30) for the
// handover to be visible at all. See Tests/Scenarios/planetsmith_tilt.json.
public sealed class PlanetsmithTiltProbe : IProbe
{
    public string Name => "planetsmith_tilt";

    public float Read(Map map) => AxialTiltCompat.ObliquityDegrees;
}
