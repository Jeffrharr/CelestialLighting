using RimWorld;
using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll and compiled into the dev-only probes assembly — see
// the <Compile Remove> in CelestialLighting.csproj.
//
// Reports §14's contract as a single number: 1 when vanilla's sky and our sun DISAGREE about whether
// it is day, 0 when they agree.
//
// "Disagree" is the artifact §14 fixes, in either direction: vanilla's sky lit while our sun is below
// the horizon (bright ground casting no shadows — the one that shipped, 3-6 h a day at ordinary
// latitudes), or our sun up while vanilla calls it night (shadows on dark ground). A scenario can
// therefore assert the whole subsystem with expectedValue 0 at a sweep of hours, instead of trying to
// see a shadow in a screenshot.
public sealed class SunClockDisagreementProbe : IProbe
{
    public string Name => "sun_clock_disagreement";

    public float Read(Map map)
    {
        bool vanillaLit = GenCelestial.CurCelestialSunGlow(map) > 0f;
        bool ourSunUp = SolarPosition.ElevationForMap(map) > Formulas.AtmosphericRefractionDegrees;
        return vanillaLit == ourSunUp ? 0f : 1f;
    }
}

// Our solar model's elevation for the live tile and tick, in degrees — the value every visual
// subsystem keys on, and (in locked mode) the warped one. Diagnostic partner to the probe above: when
// a disagreement shows up, this says which side of the horizon we were on.
public sealed class SunElevationProbe : IProbe
{
    public string Name => "sun_elevation";

    public float Read(Map map) => SolarPosition.ElevationForMap(map);
}
