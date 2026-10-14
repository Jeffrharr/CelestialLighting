using RimWorldTestHarness.Mod.Probes;
using RimWorld;
using Verse;

namespace CelestialLighting.Probes;

// Reading this marks every section's ground-glow layers dirty, so night desaturation rebuilds the
// whole map on the next frame. Returns the section count. For A/B arms that must prove the rebuilt
// mesh reaches the screen: a clock jump does not do it, because the wash ignores the sky.
public sealed class NightDesaturationDirtyProbe : IProbe
{
    public string Name => "desat_dirty_all";

    public float Read(Map map)
    {
        int sections = 0;

        foreach (IntVec3 cell in map.AllCells)
        {
            if (cell.x % 17 == 0 && cell.z % 17 == 0)
            {
                map.mapDrawer.MapMeshDirty(cell, MapMeshFlagDefOf.GroundGlow);
                sections++;
            }
        }

        return sections;
    }
}
