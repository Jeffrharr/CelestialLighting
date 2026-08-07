using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// Reads the exact multiplier Patch_SkyColorTemperature applies to §8's tint, through the same
// WeatherDimming.CloudOpacityFor / CloudAltitudeMetresFor calls that patch makes, so a scenario pins
// §23 end-to-end against a real running game rather than only offline. Two metrics off one class,
// same pattern as EaveCellProbe/LimbRefractionProbe: the headline claim is a COMPARISON (a high deck
// reads warmer than baseline at some elevation, a low deck reads cooler at the same one), and a
// single probe reporting only the multiplier could not carry the altitude half of that comparison —
// a scenario would have no way to confirm which deck it actually measured.
public sealed class CloudUnderlightProbe : IProbe
{
    public enum Metric
    {
        Multiplier,
        AltitudeMetres,
    }

    private readonly string _name;
    private readonly Metric _metric;

    public CloudUnderlightProbe(string name, Metric metric)
    {
        _name = name;
        _metric = metric;
    }

    public string Name => _name;

    public float Read(Map map)
    {
        // Mirrors Patch_SkyColorTemperature's own gate exactly (see that patch's header): 0 is a
        // legitimate real altitude, not a sentinel for "feature off", so the flag has to be checked
        // here directly rather than inferred from CloudAltitudeMetresFor's own internal zero return.
        if (!CelestialLightingFeatures.CloudUnderlight)
            return _metric == Metric.AltitudeMetres ? 0f : 1f;

        float cloudOpacity = WeatherDimming.CloudOpacityFor(map);
        float altitudeMetres = WeatherDimming.CloudAltitudeMetresFor(map);

        if (_metric == Metric.AltitudeMetres)
            return altitudeMetres;

        if (cloudOpacity <= 0f)
            return 1f;

        float elevation = SolarPosition.ElevationForMap(map);
        return CloudUnderlightMath.WarmthMultiplier(
            elevation, altitudeMetres, cloudOpacity, Vacuum.InVacuumForMap(map));
    }
}
