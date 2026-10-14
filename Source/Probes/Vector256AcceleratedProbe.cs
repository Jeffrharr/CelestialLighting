using System.Runtime.Intrinsics;
using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Whether the eight-wide ray cast in VectorLightMath.Build is actually the path taking the frame.
// Build picks it from Vector256.IsHardwareAccelerated, which is a property of the RUNTIME, not of the
// machine: RimWorld 1.7 runs Ludeon's own CoreCLR fork, and a fork that reports false would fall back
// to the scalar cast with every offline benchmark still showing the vector speedup. 1 = vectorised.
public sealed class Vector256AcceleratedProbe : IProbe
{
    public string Name => "vector256_accelerated";

    public float Read(Map map) => Vector256.IsHardwareAccelerated ? 1f : 0f;
}
