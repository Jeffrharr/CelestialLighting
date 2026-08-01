using RimWorldTestHarness.Mod.Probes;
using UnityEngine;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj —
// the shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// How bright the scene actually renders, as the greyscale of the composed light overlay. Nothing
// else measured this: sky_glow reads SkyManager.CurSkyGlow, which is the GAMEPLAY brightness (what
// GlowGrid and Dub's Skylights see), and the two deliberately diverge — §7a darkens the screen
// without touching glow, and §19's floor lifts the screen without touching glow. A scenario that
// probed sky_glow to check either would read a number that is correct and completely unrelated to
// what the player sees.
//
// Reads the finished material rather than recomputing, the same justification SkyOverlayWarmthProbe
// gives: this is the downstream "did every patch in the chain actually run" check, so recomputing
// would defeat its purpose. It therefore carries the same caveat — SkyManagerUpdate only writes
// MatBases.LightOverlay when map == Find.CurrentMap, so this reads the CURRENT map's overlay
// whatever map the harness hands it. On a single-map scenario that is the same thing.
public sealed class OverlayBrightnessProbe : IProbe
{
    public string Name => "overlay_brightness";

    public float Read(Map map) => MatBases.LightOverlay.color.grayscale;
}
