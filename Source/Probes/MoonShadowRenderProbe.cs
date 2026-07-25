using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// Reads the FINAL composed shadow colour the shader actually renders with: MatBases.SunShadow.color,
// which SkyManager rebuilds every frame as
//
//     Color.Lerp(Color.white, curSky.colors.shadow, GenCelestial.CurShadowStrength(map))
//
// This is the one measurement that proves §6a end-to-end, and it exists because the obvious test
// does not work. A screenshot A/B of moon shadows is close to worthless: the scene is a night, and a
// faint shadow on ground §7a has already pulled toward black moves pixels by ~1-3 values out of 255,
// which is indistinguishable from weather particles and pawn animation between two frames. Reading
// the composed colour instead sidesteps the renderer entirely and pins the number the fix is about.
//
// Expected readings on a clear full-moon night, both of which a scenario can assert exactly:
//   moon shadows OFF -> 1.0   (strength 0, so the lerp returns pure white: no shadow at all)
//   moon shadows ON  -> ~0.75 (strength ~0.277 toward colour ~0.107 == the 25% darkening §6a targets)
// Before §6a the ON case read ~0.958 — a 4% darkening, which is what "computed correctly and rendered
// invisibly" looked like as a number.
//
// Greyscale, so .r is the whole story: §6a writes a neutral grey and vanilla's night shadow colours
// are neutral too.
public sealed class MoonShadowRenderProbe : IProbe
{
    public string Name => "moon_shadow_render";

    public float Read(Map map) => MatBases.SunShadow.color.r;
}
