using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped DLL — see TwilightSweepProbe's header.
//
// §26's peak additive alpha this frame: the window envelope times the amplitude scale.
//
// WHY BOTH THIS AND THE POSITION, when one number would look like enough. They fail in opposite
// directions and a single pin cannot tell them apart. A sweep in exactly the right place at zero
// strength and a sweep at full strength that never moves both produce "the capture looks off" — and
// because §26's envelope is deliberately zero at BOTH ends of the window (TwilightSweepMath.
// WindowEnvelope), the position alone is guaranteed to read as a healthy non-zero at the two hours a
// careless scenario is most likely to pick, while nothing at all is on screen.
public sealed class TwilightSweepAmplitudeProbe : IProbe
{
    public string Name => "twilight_sweep_amplitude";

    public float Read(Map map) => TwilightSweep.AmplitudeFor(map);
}
