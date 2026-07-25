using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// The raw gameplay lighting value: SkyManager.CurSkyGlow, exactly as GlowGrid.GroundGlowAt reads it
// for any unroofed cell, and therefore exactly what drives plant growth (PlantProperties.growMinGlow),
// solar panel output (CompPowerPlantSolar) and pawn psych-glow.
//
// This exists specifically to let a scenario assert a NEGATIVE, which is §13's central design claim:
// weather changes what the player sees but must not change what the colony gets. The weather_dimming
// scenario reads this under Clear and again under Rain at the same instant and requires the two to
// be identical, while the weather_dimming and purkinje probes both move. No other probe can state
// that — night_radiance returns the computed night floor rather than the composed glow, and
// brightness_floor returns the post-clamp value, which only coincides with the raw glow while the
// accessibility floor happens to be disabled.
public sealed class SkyGlowProbe : IProbe
{
    public string Name => "sky_glow";

    public float Read(Map map) => map.skyManager.CurSkyGlow;
}
