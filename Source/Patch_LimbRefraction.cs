using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// Subsystem 18d (DESIGN.md §18d). The live-game half of the limb-refraction flash — the deep-red
// sliver an orbital platform gets at sunset in place of the ground twilight §18a removed.
//
// Thin by construction: read the sun's elevation, read the §18 gate once, hand both to
// LimbRefractionMath and write back what it returns. Every number, every curve and every constant
// lives in the pure core with offline [TestCase] coverage; nothing is decided here.
//
// WHY CurSkyTarget AND NOT SkyManagerUpdate. Same reason §2 and §7 sit here: CurSkyTarget is the one
// funnel where the sky's glow and its colours are both still separable values, before SkyManagerUpdate
// bakes them into MatBases.LightOverlay. Writing here means glow-reading mods (Dub's Skylights) see a
// consistent value, and it means we compose with §2/§7 by field rather than by draw order.
//
// NOT A GameCondition. This fires every game day on any vacuum map, purely as a function of sun
// elevation. It is ordinary orbital night. §10a owns eclipses (moon transits the sun, once every few
// game years) and the two never share a code path — see LimbRefractionMath's header for the boundary.
[HarmonyPatch(typeof(WeatherWorker), nameof(WeatherWorker.CurSkyTarget))]
public static class Patch_LimbRefraction
{
    // The floor the ramp steps down to when the sun finally goes behind the solid limb.
    //
    // BINDING POINT FOR #30 (§18b, branch `vacuum-night-budget`). The vacuum night-light budget —
    // airglow to zero, starlight unextinguished, moonlight replaced by planetshine — is that issue's
    // to define, and defining a second copy of it here is exactly the collision the epic is trying to
    // avoid. So the pure core takes the floor as a parameter and this one constant is the whole
    // binding surface: when #30 lands, this line becomes a call to its function and nothing else in
    // §18d changes.
    //
    // Zero until then, which is the safe direction to be wrong in. LimbRefractionMath.VacuumSkyGlow
    // takes max(sunlight, floor), so a floor of 0 can only ever leave the map darker than the real
    // floor would — never brighter — and #8's own conclusion is that orbital night should be the
    // darkest state the mod can produce anyway.
    public const float PlanetshineFloorPlaceholder = 0f;

    static void Postfix(Map map, ref SkyTarget __result)
    {
        // One read of map.Biome.inVacuum for the whole subsystem, per Vacuum.cs's convention, handed
        // down as a primitive. Deliberately NOT an early-out: the "what does an orbital sunset look
        // like" decision belongs next to the limb math and its unit tests, so the shipped behaviour
        // and the pinned behaviour are literally the same code. Same shape §18a's Patch_TwilightColor
        // uses.
        bool inVacuum = Vacuum.InVacuumForMap(map);

        // The shared solar-position simulator every other subsystem reads (§2 twilight, §7 night
        // radiance, the shadow patches), so the terminator can never disagree with them about where
        // the sun is. Memoized per (map, frame) — see GeometryMemo.cs — so this costs nothing beyond
        // the first caller in the frame.
        float sunElevation = SolarPosition.ElevationForMap(map);

        __result.glow = LimbRefractionMath.VacuumSkyGlow(
            sunElevation, __result.glow, PlanetshineFloorPlaceholder, inVacuum);

        ApplyLimbTint(ref __result, sunElevation, inVacuum);
    }

    // The colour half. Split out so the glow write above reads as one statement and so the "is there
    // anything to tint" question is answered by one named predicate rather than a branch buried in
    // the middle of the postfix.
    private static void ApplyLimbTint(ref SkyTarget target, float sunElevation, bool inVacuum)
    {
        float strength = LimbRefractionMath.TintStrength(sunElevation, inVacuum);
        if (strength <= 0f)
            return;

        LimbRefractionMath.Rgb tint = LimbRefractionMath.LimbTint(sunElevation, inVacuum);
        Color limb = new Color(tint.R, tint.G, tint.B);

        // Lerped at full strength rather than §2's damped 0.35/0.25, because the two are saying
        // different things. §2 NUDGES the sky warm while a weather palette still has to read as rain
        // or fog; this is the only light there is — the sun has physically gone monochromatic, and
        // there is no unreddened component left to preserve. Damping it would be inventing a light
        // source to blend against.
        target.colors.sky = Color.Lerp(target.colors.sky, limb, strength);
        target.colors.overlay = Color.Lerp(target.colors.overlay, limb, strength);

        // Saturation rides up with the same factor. The band's spectrum genuinely narrows to a single
        // band of wavelengths, so this is the one place in the mod where pushing saturation is
        // describing the light rather than stylising it.
        target.colors.saturation = Mathf.Lerp(
            target.colors.saturation, target.colors.saturation * 1.4f, strength);
    }
}
