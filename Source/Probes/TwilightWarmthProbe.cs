using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj —
// the shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// Reads back TwilightWarmth.ForMap — the exact factor Patch_TwilightColor blends the warm dusk hue
// with — so a scenario can pin the applied twilight end-to-end against a running game.
//
// Distinct from the older `civil_twilight` probe, and added because that one could not answer the
// §18a question. `civil_twilight` reads Formulas.CivilTwilightPersistence, one *component* of the
// factor, deliberately left ungated because it is a shape parameter rather than a contribution. On
// an orbital map it therefore still reports a healthy below-horizon pulse while the patch applies
// no tint at all. This probe reads the composed, vacuum-gated value the renderer actually uses, so
// "twilight is zero in vacuum" is measurable live rather than only offline.
public sealed class TwilightWarmthProbe : IProbe
{
    public string Name => "twilight_warmth";

    public float Read(Map map) => TwilightWarmth.ForMap(map);
}
