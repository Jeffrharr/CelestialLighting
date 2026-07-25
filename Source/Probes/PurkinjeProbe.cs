using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj —
// the shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool. See
// RimWorldTestHarness/DESIGN.md's "Where probe tests live" for the full reasoning.
//
// Re-derives the exact §9 rod-vision factor Patch_LowLightDesaturation keys the shift off, from the
// live displayed glow. The patch reads WeatherWorker.CurSkyTarget's own weather-clamped glow;
// SkyManager.CurSkyGlow is that same value after SkyManagerUpdate (curSkyGlowInt = curSky.glow, and
// curSky comes from the patched CurSkyTarget), and it is the already-API-tested public accessor. So
// this pins the 0..1 Purkinje factor end-to-end against a running game: ~0 in daylight, rising
// toward 1 on a deep or overcast night — and because the night floor from §7 (night radiance) feeds
// that same glow, the factor a scenario reads at night already reflects moon phase for free.
public sealed class PurkinjeProbe : IProbe
{
    public string Name => "purkinje";

    public float Read(Map map)
    {
        float glow = map.skyManager.CurSkyGlow;
        return PurkinjeMath.PurkinjeFactor(glow);
    }
}
