using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll — see AxialTiltDeclinationProbe's header.
//
// 1 when the moon the game just drew came from Realistic Axial Tilt — feature on, RAT active, and
// their build carrying lunar geometry we bound to; 0 otherwise. A strictly narrower claim than
// axial_tilt_active, and the gap between the two is a real mod list rather than a hypothetical: RAT
// shipped the interop API before it shipped a moon, additively, and additive changes leave their
// ApiVersion alone by contract, so the only way to know the moon is there is to have found the
// method. A scenario asserting only axial_tilt_active would pass identically against a RAT with no
// moon at all, silently measuring our fallback and calling it the handover.
//
// Reads the same LunarGeometryActive the seam itself branches on, so it also reports the
// axial_tilt_lunar_geometry feature — which is what lets one scenario assert that a SetFeature flip
// actually reached the moon, rather than inferring it from the declination alone.
public sealed class AxialTiltLunarProbe : IProbe
{
    public string Name => "axial_tilt_lunar";

    public float Read(Map map) => AxialTiltCompat.LunarGeometryActive ? 1f : 0f;
}
