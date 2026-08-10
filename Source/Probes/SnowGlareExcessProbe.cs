using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) — see SnowGlareProbe's header for the full note.
//
// Reads §24's INPUT rather than its output: how much of §21's daytime amplification overflowed the
// multiply lane, before SnowGlareMath.GlareAlpha converts it into an overlay alpha.
//
// Separate from snow_glare_alpha because the two fail differently, and issue #90's prototype has to
// tell those failures apart. The alpha folds in CurSkyGlow and the eyeballed DefaultIntensityScale,
// so a small alpha could mean "the physics produced no overflow here" (the scenario is wrong — the
// map is not snowy enough, or the weather is Clear) or "the overflow is real but the scale factor is
// too timid" (the scenario is right and the knob wants moving). This probe answers the first
// question on its own, in the same units DESIGN.md §21 quotes for the cavity: 0 means the multiply
// lane rendered everything and there was nothing for glare to draw.
public sealed class SnowGlareExcessProbe : IProbe
{
    public string Name => "snow_glare_excess";

    public float Read(Map map) => WeatherDimming.UndrawableExcessFor(map);
}
