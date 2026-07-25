using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// Reads back the live §13 dimming fraction through the exact same WeatherDimming adapter the three
// consumers use (the sky tint in Patch_WeatherDimming, the shadow softening in Patch_ShadowStrength,
// and §9's apparent brightness in Patch_LowLightDesaturation), so a scenario pins the classifier
// end-to-end against a real running game: 0 under Clear, ~0.18 under a dry deck like Fog, ~0.255
// under Rain, ~0.2925 under a Blizzard, and blended values mid-transition.
//
// Deliberately reports the FRACTION rather than the resulting sky colour. The colour is the product
// of §2, §8, §11, §12 and §13 all lerping the same struct, so asserting on it would couple this
// scenario to every other subsystem's tuning; the fraction is §13's own contribution alone.
public sealed class WeatherDimmingProbe : IProbe
{
    public string Name => "weather_dimming";

    public float Read(Map map) => WeatherDimming.DimmingFor(map);
}
