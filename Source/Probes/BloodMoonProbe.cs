using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj —
// the shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// Reads back BloodMoon.TintStrengthForMap — the exact value Patch_BloodMoon uses to drive the
// crimson recolour — for the live map's current condition state and sun glow. This is what lets a
// scenario pin the blood-moon render end-to-end against a real running game: with no blood-moon
// condition active it returns 0 (soft dependency absent, or condition simply not running); once the
// VRE condition is active on a night-time map it rises toward BloodMoon.MaxTint, mirroring how
// ShadowLeanProbe re-derives Formulas.SolarDeclinationDegrees for the live day-of-year.
public sealed class BloodMoonProbe : IProbe
{
    public string Name => "blood_moon";

    public float Read(Map map)
    {
        return BloodMoon.TintStrengthForMap(map);
    }
}
