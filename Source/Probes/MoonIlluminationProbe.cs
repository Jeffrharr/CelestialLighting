using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// Reads back MoonMath.IlluminatedFraction for the moon's live cycle position — the same pure value
// that scales moon-cast shadow strength (Patch_ShadowStrength/Direction via MoonPosition) and
// moonlight (MoonPosition.MoonlightBrightnessForMap). It re-derives the cycle position from the live
// game component and tick exactly as the shipped code does, so a scenario can pin the moon model
// end-to-end against a real running game rather than only via the offline MoonMathTests. Returns 0
// when no moon component is present (e.g. before a game is fully loaded).
public sealed class MoonIlluminationProbe : IProbe
{
    public string Name => "moon_illumination";

    public float Read(Map map)
    {
        GameComponent_MoonPhase moon = GameComponent_MoonPhase.Current;
        if (moon == null)
            return 0f;

        return moon.IlluminatedFraction;
    }
}
