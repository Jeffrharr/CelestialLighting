using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj —
// the shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool. See
// RimWorldTestHarness/DESIGN.md's "Where probe tests live" for the full reasoning.
//
// Reads back the exact §7 night-floor value Patch_NightRadiance computes for the live tile/tick:
// the same sun elevation (via the shared SolarPosition simulator), the same moon state (via the
// MoonSeam provider — MoonState.None until the moon-position subsystem is wired in, so moonlight is
// 0 by default), and the same NightRadianceMath source-sum, honoring the live NightRadianceSettings
// (so a scenario can flip AtmosphericGlowEnabled off and assert the floor collapses toward pitch
// black). This is what lets a live scenario pin the emergent night glow end-to-end against a real
// running game rather than only the offline NightRadianceMathTests.
public sealed class NightRadianceProbe : IProbe
{
    public string Name => "night_radiance";

    public float Read(Map map)
    {
        NightRadianceSettings settings = NightRadianceSettings.Current;

        // Mirror the adapter exactly: the atmospheric floors are gated by the master toggle, so with
        // it off only moonlight remains (0 while the moon seam still reports MoonState.None).
        float starlight = settings.AtmosphericGlowEnabled ? settings.StarlightGlow : 0f;
        float airglow = settings.AtmosphericGlowEnabled ? settings.AirglowGlow : 0f;

        MoonState moon = MoonSeam.Provider(map);
        float moonlight = NightRadianceMath.MoonlightGlow(
            moon.IlluminatedFraction, moon.ElevationDegrees, settings.MaxMoonlightGlow);

        return NightRadianceMath.NightSourceGlow(starlight, airglow, moonlight);
    }
}
