using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj —
// the shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool. See
// RimWorldTestHarness/DESIGN.md's "Where probe tests live" for the full reasoning.
//
// Reads back §24's additive glare alpha for the live map — the exact value SnowGlareOverlay writes
// into the material, via the same SnowGlare.AlphaFor call, so the probe cannot report a glare the
// screen did not get.
//
// THIS PROBE IS LOAD-BEARING FOR ISSUE #90 IN A WAY MOST ARE NOT. #90's open question is whether a
// static additive wash reads as brightness or as a washed-out screen, which is a judgement about the
// SCREENSHOT. That makes it uniquely easy to misread a null result: "the capture looks unchanged"
// could mean the effect is too subtle to matter (the answer #90 predicts) or that the quad never
// drew at all (a wiring bug). Those want opposite responses — abandon the subsystem, or fix it — and
// only a number distinguishes them.
public sealed class SnowGlareProbe : IProbe
{
    public string Name => "snow_glare_alpha";

    public float Read(Map map) => SnowGlare.AlphaFor(map);
}
