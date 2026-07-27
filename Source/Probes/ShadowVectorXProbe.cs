using RimWorld;
using RimWorldTestHarness.Mod.Probes;
using UnityEngine;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj —
// the shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool. See
// RimWorldTestHarness/DESIGN.md's "Where probe tests live" for the full reasoning.
//
// The handedness gate. ShadowLeanProbe reads a pure formula back out (declination for today), which
// proves the math but says nothing about which way the result points once it reaches world space —
// and pointing the wrong way is exactly the bug this probe exists because of: we shipped a sun that
// rose in the west, and every offline test stayed green because they were all symmetric about noon.
//
// So this deliberately reads the FINAL value, through the real patched vanilla call, not through
// Formulas: whatever GenCelestial.GetLightSourceInfo returns after Patch_ShadowDirection's postfix
// has run is the vector the sun-shadow shader draws with. If any layer between the formula and the
// renderer flips an axis — the azimuth sign, ShadowVectorFromSunPosition, the Vector2 handoff in
// Apply, or a RimWorld world-space convention we guessed wrong about — it shows up here and nowhere
// else. X is world east-west, so morning must be negative (shadows thrown west, away from an
// eastern sun) and afternoon positive.
public sealed class ShadowVectorXProbe : IProbe
{
    public string Name => "shadow_vector_x";

    public float Read(Map map)
    {
        GenCelestial.LightInfo info = GenCelestial.GetLightSourceInfo(map, GenCelestial.LightType.Shadow);
        return info.vector.x;
    }
}
