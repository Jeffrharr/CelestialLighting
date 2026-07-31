using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll — see AxialTiltDeclinationProbe's header.
//
// The moon's twin of axial_tilt_declination, and read the same way: off the memoized Sky the render
// path itself consumed, never recomputed. Recomputing would defeat the purpose — the whole question
// this probe answers is WHICH model produced the moon, and a probe that called MoonMath directly
// would answer "ours" no matter who the game asked.
//
// Three answers are distinguishable in one number, which is why it is worth pinning rather than
// asserting a boolean: our own model (no RAT), RAT's ecliptic moon (an older RAT, reached through
// the sun-at-a-shifted-day fallback), and RAT's inclined moon. The first two differ by a quarter
// year of phase, the last two by up to the player's moonInclinationDeg.
public sealed class MoonDeclinationProbe : IProbe
{
    public string Name => "moon_declination";

    public float Read(Map map) => MoonPosition.SkyForMap(map).DeclinationDegrees;
}
