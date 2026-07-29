using RimWorld;
using Verse;

namespace CelestialLighting;

// The impure boundary for §2's warm-tint factor: lifts latitude, sun glow, sun elevation and the
// §18 vacuum flag off live state and hands them to the pure Formulas layer.
//
// Extracted so Patch_TwilightColor and the twilight_warmth live probe read ONE value instead of two
// independently-derived ones — the same discipline AuroraConditions.CurrentSkyTintStrength enforces
// for §11 and SolarPosition.cs enforces between the shadow patches.
//
// This was not merely tidiness. Before §18a the only twilight probe was `civil_twilight`, which
// reads Formulas.CivilTwilightPersistence — a *component* of the factor, and one deliberately left
// ungated because it is a shape parameter rather than a contribution. Once the vacuum gate landed,
// that probe would happily report a healthy below-horizon twilight pulse on an orbital map where
// the patch was applying no tint at all: a probe reporting something nothing renders, which is
// exactly the failure DESIGN.md §18 warns about. This adapter is what the probe should have been
// reading all along, and it now cannot disagree with the patch because it IS the patch's input.
public static class TwilightWarmth
{
    // The exact factor Patch_TwilightColor blends with, in [0, 1]. 0 means no warm nudge is applied
    // at all — on the equator (no latitude strength), outside the dusk/dawn band, or in vacuum.
    public static float ForMap(Map map)
    {
        // §17's enclosed-map gate, mirrored from Patch_TwilightColor. The patch early-outs on this
        // too (before the latitude lookup, so it stays a true no-op), but it has to be here as well
        // or the twilight_warmth probe reports a warm band on a cavern map where the patch applied
        // nothing — the same patch/probe divergence this adapter exists to prevent, arriving from
        // §17's direction instead of §18's.
        if (MapSky.IsEnclosed(map))
            return 0f;

        float strength = LatitudeEffect.StrengthForMap(map);
        if (strength <= 0f)
            return 0f;

        // Deliberately re-derive sun glow from GenCelestial.CurCelestialSunGlow rather than reading
        // the SkyTarget's own glow, so twilight timing is anchored to where the sun actually is
        // rather than to what the sky currently looks like — §7 rewrites that value below the
        // horizon with its night floor, which would make the band track moonlight instead of the sun.
        float sunGlow = GenCelestial.CurCelestialSunGlow(map);

        // Solar elevation from the same shared simulator the shadow patches use, so twilight timing
        // and shadow timing can never derive a different sun position from each other.
        float elevation = SolarPosition.ElevationForMap(map);

        // The §18 vacuum gate (Vacuum.cs). Threaded into the pure layer as a primitive rather than
        // early-returned here: the "twilight is zero without air" decision belongs next to the
        // twilight math and its unit tests, so the shipped behaviour and the pinned behaviour are
        // literally the same code.
        bool inVacuum = Vacuum.InVacuumForMap(map);

        return WarmthFactor(sunGlow, elevation, strength, inVacuum);
    }

    // Honours the CivilTwilightPersistence feature switch. On (the shipped default) folds in the
    // below-horizon civil-twilight linger; off falls back to the pre-feature glow-keyed-only factor,
    // so the warm tint snaps off at geometric sunset exactly as it did before that feature — a
    // faithful "before" the harness can screenshot against the "after".
    //
    // Both branches take the vacuum gate, so turning the feature off cannot smuggle ground twilight
    // back onto a space map through the legacy path.
    private static float WarmthFactor(float sunGlow, float elevation, float strength, bool inVacuum) =>
        CelestialLightingFeatures.CivilTwilightPersistence
            ? Formulas.TwilightWarmthFactor(sunGlow, elevation, strength, inVacuum)
            : Formulas.TwilightFactor(sunGlow, strength, inVacuum);
}
