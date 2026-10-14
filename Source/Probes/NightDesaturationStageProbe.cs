using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Night desaturation's section rebuild, split into its three stages: the cell resolve, the per-cell
// colour writes and the mesh upload. Milliseconds since the last Reset read, summed across every
// rebuild in the window; Count is how many rebuilds that was.
public sealed class NightDesaturationStageProbe : IProbe
{
    public enum Stage { Resolve, Colors, Finalize, Count, Reset }

    private readonly Stage stage;

    public NightDesaturationStageProbe(string name, Stage stage)
    {
        Name = name;
        this.stage = stage;
    }

    public string Name { get; }

    public float Read(Map map)
    {
        switch (stage)
        {
            case Stage.Resolve:
                return SectionLayer_NightDesaturation.StageMs(SectionLayer_NightDesaturation.ResolveTicks);
            case Stage.Colors:
                return SectionLayer_NightDesaturation.StageMs(SectionLayer_NightDesaturation.ColorTicks);
            case Stage.Finalize:
                return SectionLayer_NightDesaturation.StageMs(SectionLayer_NightDesaturation.FinalizeTicks);
            case Stage.Count:
                return SectionLayer_NightDesaturation.Regenerations;
            default:
                SectionLayer_NightDesaturation.ResetStageClocks();
                return 0f;
        }
    }
}
