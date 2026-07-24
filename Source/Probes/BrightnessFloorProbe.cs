using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// Reads back BrightnessFloorMath.Apply — the exact pure clamp Patch_BrightnessFloor applies — for
// the live map's current sky glow and the live floor settings. This lets a scenario pin the
// accessibility floor end-to-end against a real running game: with the floor enabled and set above
// the current glow, this returns the floor; disabled, it returns the untouched live glow.
public sealed class BrightnessFloorProbe : IProbe
{
    public string Name => "brightness_floor";

    public float Read(Map map)
    {
        float glow = map.skyManager.CurSkyGlow;

        CelestialLightingSettings settings = CelestialLightingSettingsMod.Settings;
        // Settings are constructed during mod loading, so they are present by the time any map ticks;
        // fall back to the raw glow only for total defensiveness.
        if (settings == null)
            return glow;

        return BrightnessFloorMath.Apply(glow, settings.brightnessFloor, settings.brightnessFloorEnabled);
    }
}
