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
//   moon shadows ON  -> ~0.80 (alpha ~0.222 toward colour ~0.107 == a 19.9% darkening)
// Before §6a the ON case read ~0.958 — a 4% darkening, which is what "computed correctly and rendered
// invisibly" looked like as a number.
//
// That ~0.80 tracks the SHIPPED PRESET, so recompute it whenever the preset moves. §6a itself targets
// a 25% darkening at peak (MoonShadowPeakDarkening against MoonShadowMaxStrength 0.28, which is what
// fixes the colour at ~0.107), but Patch_ShadowStrength then multiplies the alpha by the player's
// "Shadow strength" slider — 0.8 on Cinematic, the shipped default — so the alpha that reaches the
// lerp is 0.28 * 0.8 = 0.222 and the darkening a new install actually sees is 19.9%. On Realistic
// (slider 1.0) the same night reads the ~0.75 this comment used to document. Recompute:
//
//     rendered = 1 - MoonShadowStrength(...) * ShadowSettings.Strength * (1 - 0.107)
//
// Both terms are worth naming because a drift in this number is ambiguous otherwise: the same
// reading moves whether the night's shadow COLOUR changed or only the alpha did. Issue #1 was read as
// a colour drift (0.107 -> 0.286) when the colour had not moved at all — only the slider had gone live.
//
// Greyscale, so .r is the whole story: §6a writes a neutral grey and vanilla's night shadow colours
// are neutral too.
public sealed class MoonShadowRenderProbe : IProbe
{
    public string Name => "moon_shadow_render";

    public float Read(Map map) => MatBases.SunShadow.color.r;
}
