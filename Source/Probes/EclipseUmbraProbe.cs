using RimWorld;
using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool. See
// RimWorldTestHarness/DESIGN.md's "Where probe tests live" for the full reasoning.
//
// §18e's instrument: the glow the sky is heading for at the deepest point of the currently-active
// eclipse — its umbral minimum. Reads it by CALLING the live condition's SkyTarget(map), which is the
// method Patch_EclipseVacuumSky postfixes, so what this reports is what SkyManager.CurrentSkyTarget
// will actually LerpDarken toward rather than a re-derivation that could drift from it.
//
// The pairing that makes a scenario meaningful is this against night_radiance: on a vacuum map they
// must read the SAME value, because the whole §18e claim is that totality in orbit is night. On a
// planet-surface map they must differ, because vanilla's umbral glow is a flat 0 and the night floor
// is not. Returns 0 when no Eclipse condition is active.
//
// eclipse_coverage says how far along the ramp we are; this says what the ramp is aimed at.
public sealed class EclipseUmbraProbe : IProbe
{
    public string Name => "eclipse_umbra_glow";

    public float Read(Map map)
    {
        GameCondition eclipse = map.gameConditionManager.GetActiveCondition(GameConditionDefOf.Eclipse);
        if (eclipse == null)
            return 0f;

        SkyTarget? target = eclipse.SkyTarget(map);
        return target.HasValue ? target.Value.glow : 0f;
    }
}
