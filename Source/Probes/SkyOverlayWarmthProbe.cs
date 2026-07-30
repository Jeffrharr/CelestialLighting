using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// How WARM the composed sky is, as red minus blue of MatBases.LightOverlay.color — the material
// SkyManager tints the whole map through, and therefore the last word on what the player sees. This is
// the colour lane's counterpart to MoonShadowRenderProbe: a reading of the finished value rather than a
// re-derivation of an input.
//
// WHY THE MOD'S OTHER SKY PROBES CANNOT DO THIS JOB. sky_color_temperature and aurora_tint both
// recompute their patch's INPUT (elevation -> Kelvin, condition -> ramp) from the live clock. That is
// what makes them good at pinning a curve, and it also makes them completely blind to whether the
// patch that consumes them ran at all — neither consults MapSky, so neither moves when a map-kind gate
// fires. Issue #35's whole failure mode is a gate not firing, so it needed a probe downstream of the
// gate.
//
// Red minus blue rather than a single channel because that is the axis every warm/cool write in this
// mod moves along and vanilla's own day/dusk/night palette moves along too: §2's twilight target
// (1, 0.45, 0.15) and §8's low-sun blackbody both raise red while dropping blue, so a difference
// isolates their contribution from how BRIGHT the sky happens to be. A single channel would confound
// the two, and under a blackout — where brightness collapses to near zero — that is exactly the
// confusion to avoid.
//
// Signed on purpose: vanilla's night sky (0.482, 0.603, 0.682) is genuinely cool, so a negative
// reading is meaningful rather than an error, and "did our warmth survive a gate it should not have"
// is legible directly in the sign and magnitude.
public sealed class SkyOverlayWarmthProbe : IProbe
{
    public string Name => "sky_overlay_warmth";

    // Reads the material, not map.skyManager.CurSky.colors.sky, deliberately: SkyManager only assigns
    // this for map == Find.CurrentMap, so the material is the value that has actually been composed AND
    // shipped to the renderer for the map on screen. The scenario runs on the current map, which is the
    // only map a screenshot beside the probe could be about anyway.
    public float Read(Map map) =>
        MatBases.LightOverlay.color.r - MatBases.LightOverlay.color.b;
}
