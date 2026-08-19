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
// GLOW OWNERSHIP ON A VACUUM MAP. This patch owns SkyTarget.glow there outright, and §7's
// Patch_NightRadiance stands down (see the gate in that file for the measured reason). VacuumSkyGlow
// is a total answer at every elevation — it builds the sunlit term from scratch rather than scaling
// whatever arrived, and max()es it against §18b's floor — so nothing is lost by there being one
// writer instead of two postfixes racing on the same field. On a surface map this patch is a strict
// no-op at every elevation and §7 is unchanged.
//
// NOT A GameCondition. This fires every game day on any vacuum map, purely as a function of sun
// elevation. It is ordinary orbital night. §10a owns eclipses (moon transits the sun, once every few
// game years) and the two never share a code path — see LimbRefractionMath's header for the boundary.
public static class Patch_LimbRefraction
{
    internal static void Apply(Map map, ref SkyTarget target)
    {
        // One read of map.Biome.inVacuum for the whole subsystem, per Vacuum.cs's convention, handed
        // down as a primitive. The "what does an orbital sunset look like" decision still belongs next
        // to the limb math and its unit tests, so the shipped behaviour and the pinned behaviour stay
        // literally the same code — which is why the early-out below asks the math rather than testing
        // this bool itself. Same shape §18a's Patch_TwilightColor uses.
        bool inVacuum = Vacuum.InVacuumForMap(map);

        // Issue #64. Ask before gathering. This postfix runs on every map on every frame — twice over
        // while a weather transition blends — and on a surface map every input collected below is
        // thrown away unread: VacuumSkyGlow hands back the glow it was given and TintStrength returns
        // 0. The read that motivated this is NightRadiance.FloorGlowFor, which, unlike the elevation
        // read under it, has no per-frame memo of its own.
        //
        // The predicate is still LimbRefractionMath's and is pinned by the same tests as everything
        // else here, so this is a cheaper route to the answer the math already gives, not a local copy
        // of the answer.
        if (!LimbRefractionMath.AffectsSky(inVacuum))
            return;

        // The shared solar-position simulator every other subsystem reads (§2 twilight, §7 night
        // radiance, the shadow patches), so the terminator can never disagree with them about where
        // the sun is. Memoized per (map, frame) — see GeometryMemo.cs — so this costs nothing beyond
        // the first caller in the frame.
        float sunElevation = SolarPosition.ElevationForMap(map);

        // §18b's shared night floor, read rather than re-derived. This is the value the ramp steps
        // down to once the solid limb covers the disc, and it is deliberately somebody else's number:
        // airglow gone, starlight unextinguished, planetshine standing in for the moon as the
        // dominant reflector is all §18b's model, and a second copy here is exactly the collision the
        // epic has been untangling. NightRadiance.FloorGlowFor is a shared READ (§7, §18c and §18e
        // consume the same function), so there is no ordering to get wrong and no staleness.
        //
        // Note it is moon-dependent by design — 0.0317 on a new moon, and materially higher under a
        // full one — so the handover elevation MOVES with the lunar cycle. That is intended emergent
        // behaviour rather than a wobble to be pinned out: under a bright moon the last of the limb
        // light is genuinely outshone before it reaches its reddest, and the spike becomes a dark-moon
        // spectacle. The same way §7's floor swallows the tail of ground twilight on a surface map.
        float planetshineFloor = NightRadiance.FloorGlowFor(map);

        // One evaluation of the band for this elevation, shared by the glow write and the tint write
        // below (issue #64). Both questions are functions of the same limb transmission and the same
        // fraction of the solar disc still clear of the planet; asking them separately meant computing
        // the transmission three times and the disc fraction twice per postfix, per map, per frame,
        // with identical arguments. The sample is a plain struct of five fields — no cache, no key,
        // nothing to go stale — so the sharing lasts exactly as long as it is provably valid: this
        // call.
        LimbRefractionMath.BandSample band = LimbRefractionMath.SampleBand(sunElevation, inVacuum);

        target.glow = LimbRefractionMath.VacuumSkyGlow(band, target.glow, planetshineFloor);

        ApplyLimbTint(ref target, band);
    }

    // The colour half. Split out so the glow write above reads as one statement and so the "is there
    // anything to tint" question is answered by one named predicate rather than a branch buried in
    // the middle of the postfix.
    private static void ApplyLimbTint(ref SkyTarget target, in LimbRefractionMath.BandSample band)
    {
        float strength = LimbRefractionMath.TintStrength(band);
        if (strength <= 0f)
            return;

        // Reads the same sampled transmission TintStrength just keyed its green channel on, so the
        // strength and the hue can never describe two different moments of the band.
        LimbRefractionMath.Rgb tint = LimbRefractionMath.LimbTint(band);
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
