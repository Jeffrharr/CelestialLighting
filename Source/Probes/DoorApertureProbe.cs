using RimWorld;
using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// §27e phase 2's three readings: where a door's leaves are, how many doors are being watched, and how
// many rebakes the watching has cost.
//
// The last is the one the feature lives or dies on. "The beam tracks the door" is easy; doing it
// without turning one bake per door use into one per tick is the whole engineering claim, and a
// timing probe cannot see it — a per-call timer measures one call and never asks how often it
// happens, so a version that baked every tick would report identical per-call costs and look free.
// Counting is the only instrument that catches it.
public sealed class DoorApertureProbe : IProbe
{
    public enum Metric
    {
        // The door's own quantised aperture, 0 shut and 1 fully open. Reads the same value the
        // geometry was built from, not OpenPct directly, so a quantisation change shows up here.
        Aperture,

        // How many doors the component is currently sweeping. Must fall back to 0 once a swing ends:
        // if it does not, the sweep has become per-door-per-tick over the whole base.
        Watched,

        // Rebakes requested since the last reset -- bakes per swing.
        DirtyRequests,
    }

    private readonly Metric metric;
    private readonly IntVec3 offsetFromCentre;

    public string Name { get; }

    public DoorApertureProbe(string name, Metric metric, IntVec3 offsetFromCentre)
    {
        Name = name;
        this.metric = metric;
        this.offsetFromCentre = offsetFromCentre;
    }

    public float Read(Map map)
    {
        if (metric == Metric.Watched)
        {
            return GameComponent_DoorAperture.WatchedCount;
        }

        if (metric == Metric.DirtyRequests)
        {
            return GameComponent_DoorAperture.DirtyRequests;
        }

        if (map == null)
        {
            return 0f;
        }

        IntVec3 cell = map.Center + offsetFromCentre;
        if (!cell.InBounds(map))
        {
            return 0f;
        }

        Building_Door door = cell.GetEdifice(map) as Building_Door;
        if (door == null)
        {
            return 0f;
        }

        return DoorApertureMath.Quantise(
            DoorAccess.OpenFraction(door), DoorApertureMath.DefaultQuantisationSteps);
    }
}
