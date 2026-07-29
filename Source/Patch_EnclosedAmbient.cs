using HarmonyLib;
using RimWorld;
using Verse;

namespace CelestialLighting;

// §17b enclosed ambient — thin adapter over MapSkyMath.AmbientGlow, which carries the full "why",
// including the measured 1.00-at-noon / 0.00-at-midnight swing that motivated it and the reason
// raising glow here is gameplay-inert on a cavern.
//
// A cave has no day. Every other patch in §17 stands an effect DOWN on an enclosed map; this one is
// the single place that puts something back, because gating alone left caves following the sun —
// legible at noon, black at midnight — which is the one shape a cave must not have.
//
// WHY THIS IS NOT "UNDOING" §17's GATES. The gates remove effects that need a sky: sunset warmth,
// colour temperature, aurora, blood moon, and §5's night floor built from starlight and moonlight.
// None of those describe a cave. This supplies the thing that does — a constant, sourceless ambient
// — and it is deliberately a flat constant rather than any of the celestial models, because nothing
// celestial reaches down here.
//
// ORDERING. Postfixes on WeatherWorker.CurSkyTarget compose by assignment, and this one assigns
// glow outright rather than nudging it, so it must not be raced by another writer. In practice it
// cannot be: §5 (Patch_NightRadiance) is the only other patch that writes SkyTarget.glow, and §17
// already returns it early on exactly the maps this one acts on, so the two are disjoint by
// construction rather than by priority. Every remaining CurSkyTarget postfix writes colours only.
//
// Not separately toggleable, on purpose. This is not a new effect with its own taste dial — it is
// the correction that makes "Minimum indoor brightness" mean one thing at all hours instead of
// silently decaying to black overnight. Its level comes entirely from that existing slider by way
// of §7b's cap, so a second switch would only let a user re-create the broken shape.
[HarmonyPatch(typeof(WeatherWorker), nameof(WeatherWorker.CurSkyTarget))]
public static class Patch_EnclosedAmbient
{
    static void Postfix(Map map, ref SkyTarget __result)
    {
        // Gated on the indoor-occlusion feature rather than on a flag of its own: §7b's cap is what
        // turns this constant into a visible brightness, so with that feature off this has nothing
        // to act through and holding glow up would only brighten a cave nothing is occluding.
        if (!CelestialLightingFeatures.IndoorSkyOcclusion)
            return;

        if (!MapSky.IsEnclosed(map))
            return;

        __result.glow = MapSkyMath.AmbientGlow(enclosed: true, diurnalGlow: __result.glow);
    }
}
