using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj —
// the shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// Reads back §18d's limb-refraction ramp on the live tile/tick, through the same
// SolarPosition.ElevationForMap simulator and the same §18 vacuum gate Patch_LimbRefraction uses, so
// a scenario pins what the patch actually did rather than a parallel re-derivation.
//
// FOUR metrics rather than one, because this is a TEMPORAL effect and a single number cannot show
// its shape. The whole claim of §18d is that the platform stays fully lit ~14 degrees past the
// ground's sunset and then loses the sun over ~2.4 degrees, so a scenario has to sample the sun
// elevation alongside the ramp to demonstrate anything: `limb_sun_elevation` is the x-axis and the
// other three are what happens on it. Reading only "the sky went red" would pass equally well for a
// ramp of the wrong width in the wrong place.
//
// Note every metric reports its SEA-LEVEL value on a surface map (fraction 1, strength 0, tint 1),
// which is the point — the same scenario run on a planet tile is the "before" half of the A/B and
// must show a flat line.
public sealed class LimbRefractionProbe : IProbe
{
    public enum Metric
    {
        // Sun elevation in degrees. Not itself a §18d quantity — it is the axis the other three are
        // read against, and it is what lets a scenario assert WHERE the band sits rather than only
        // that something happened.
        SunElevation,

        // Fraction of the sun's light still reaching the platform: 1 in full sun, 0 once the solid
        // limb has covered the disc. The band's brightness half.
        SunlightFraction,

        // How hard the limb tint is being driven, 0 at the top of the band to 1 at the bottom. The
        // band's colour half.
        TintStrength,

        // Green channel of the normalised limb tint. Chosen over red because red is pinned at 1 by
        // the normalisation and so carries no signal at all; green falling from ~1 to ~0.02 across
        // the band IS the reddening, expressed as one number a scenario can pin.
        TintGreen,
    }

    private readonly Metric metric;

    public LimbRefractionProbe(string name, Metric metric)
    {
        Name = name;
        this.metric = metric;
    }

    public string Name { get; }

    public float Read(Map map)
    {
        float elevation = SolarPosition.ElevationForMap(map);
        bool inVacuum = Vacuum.InVacuumForMap(map);
        return ReadMetric(elevation, inVacuum);
    }

    private float ReadMetric(float elevation, bool inVacuum)
    {
        if (metric == Metric.SunElevation)
            return elevation;
        if (metric == Metric.SunlightFraction)
            return LimbRefractionMath.SunlightFraction(elevation, inVacuum);
        if (metric == Metric.TintStrength)
            return LimbRefractionMath.TintStrength(elevation, inVacuum);

        return LimbRefractionMath.LimbTint(elevation, inVacuum).G;
    }
}
