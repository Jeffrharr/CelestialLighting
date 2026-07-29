using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll — see AxialTiltDeclinationProbe's header.
//
// 1 when Realistic Axial Tilt is present, exposes an interop API we understand, AND has seeded its
// geometry; 0 otherwise. This is the guard the declination probe's expected value depends on, and
// it is asserted first so a scenario failure says which of the two things broke: the binding never
// happened, or it happened and produced the wrong number.
//
// Worth probing separately because every failure mode of the reflection binding is silent by
// design — RAT absent, RAT too old, RAT present but its world comp not yet seeded — and all three
// degrade to "CelestialLighting's own geometry", which looks exactly like a healthy single-mod
// game. Without this, a scenario that asserted only declination could pass for the wrong reason
// after a rename upstream.
public sealed class AxialTiltActiveProbe : IProbe
{
    public string Name => "axial_tilt_active";

    public float Read(Map map) => AxialTiltCompat.Active ? 1f : 0f;
}
