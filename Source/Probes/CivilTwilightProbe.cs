using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj —
// the shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool. See
// RimWorldTestHarness/DESIGN.md's "Where probe tests live" for the full reasoning.
//
// Reads back Formulas.CivilTwilightPersistence — the new below-horizon lingering-twilight pulse
// Patch_TwilightColor now folds into its warm-tint factor — for the sun's current elevation on the
// live tile/tick, using the exact same SolarPosition.ElevationForMap adapter the patch uses. So a
// scenario can pin the linger end-to-end against a real running game: 0 while the sun is up, rising
// to 1 a couple degrees below the horizon, back to 0 once civil twilight ends. FormulasTests.cs
// already covers the pure pulse shape offline; this probe is what lets a save-game fixture assert
// that same behaviour in-engine at a chosen time of day.
public sealed class CivilTwilightProbe : IProbe
{
    public string Name => "civil_twilight";

    public float Read(Map map)
    {
        float elevation = SolarPosition.ElevationForMap(map);
        return Formulas.CivilTwilightPersistence(elevation);
    }
}
