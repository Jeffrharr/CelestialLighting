using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// Reads the alpha actually sitting on §9's wash material — the number the renderer will use this
// frame — in the same spirit as MoonShadowRenderProbe.
//
// This exists because of how §9 failed twice. PurkinjeProbe reported a perfectly healthy rod-vision
// factor (0.567 at hour 2) through two consecutive versions whose visible effect was nil and then
// backwards, because a factor is a claim about how dark the sky is and says nothing about whether
// anything reached the screen. A scenario that pins only the factor passes either way. This pins the
// applied strength instead: PurkinjeFactor * TintStrength * MaxWash, straight off the material.
//
// It deliberately does NOT read the per-cell mesh alphas. Those are baked per section and vary by
// cell, so no single number describes them; the campfire A/B screenshots in night_fire_colour.json
// are what verify the per-cell half.
public sealed class NightDesaturationProbe : IProbe
{
    public string Name => "night_desaturation_wash";

    public float Read(Map map) => NightDesaturationOverlay.Material.color.a;
}
