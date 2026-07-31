using Verse;

namespace CelestialLighting;

// The impure boundary for §19 (polar night blue): lifts latitude, declination, sun elevation and the
// §18 vacuum flag off live state and hands them to the pure OzoneTwilightMath layer.
//
// Extracted so that BOTH consumers read ONE value rather than two independently-derived ones — the
// same discipline TwilightWarmth enforces for §2 and SolarPosition enforces between the shadow
// patches. §19 is the first subsystem with two genuinely different consumers of the same band
// strength (Patch_PolarNightBlue tints the sky, Patch_PitchBlackOverlay floors the screen
// brightness), so the risk this guards against is not hypothetical: if those two derived the band
// independently, a gate added to one and forgotten in the other would leave the nights lifted on a
// map with no visible blue, which is precisely the patch/probe divergence DESIGN.md §18 warns about.
//
// DELIBERATE DEVIATION from Patch_TwilightColor's arrangement, which keeps its blackout gate OUTSIDE
// TwilightWarmth.ForMap so the twilight_warmth probe reports the curve while map_sky_blacked_out
// reports the gate. Here the gates live INSIDE, because with two consumers duplicating three gates
// across them is exactly how they drift apart. Nothing is lost: the map_sky_blacked_out probe still
// exists to explain a zero reading.
public static class OzoneTwilight
{
    // The exact band strength both arms of §19 use, in [0, 1]. 0 means no blue is applied at all —
    // feature off, sun outside the -4..-18 band, no sky overhead, or in vacuum.
    public static float BandStrengthFor(Map map)
    {
        // When off, this is the faithful pre-feature baseline: the colour patch early-outs and the
        // overlay floor collapses to the caller's own minBrightness, so the sky is exactly what
        // vanilla plus §2/§8/§9 render.
        if (!CelestialLightingFeatures.PolarNightBlue)
            return 0f;

        if (HasNoSky(map))
            return 0f;

        // One memo lookup for all three inputs. InputsForMap is memoized per (map, frame) — see
        // GeometryMemo.cs — and every sun-reading patch funnels through it, so on a frame where §1,
        // §2 or §8 has already run this costs a dictionary hit and nothing else. Reading the struct
        // once rather than calling ElevationForMap separately is what makes the reachability gate
        // below genuinely free: latitude and declination are already in hand.
        SolarPosition.Inputs inputs = SolarPosition.InputsForMap(map);

        // The gate (DESIGN.md §19). Two subtractions and two comparisons that skip the three exp()
        // calls in ChappuisTransmission on any day the band cannot open at all — midnight sun, or
        // true polar night above ~84.5 degrees. Note this is NOT a latitude threshold: every
        // latitude crosses the band twice a day, and gating on latitude alone would delete the
        // equator's blue hour. See OzoneTwilightMath.CanReachBandToday.
        if (!OzoneTwilightMath.CanReachBandToday(inputs.Latitude, inputs.Declination))
            return 0f;

        float elevation = Formulas.SolarElevationDegrees(inputs.Latitude, inputs.Declination, inputs.DayPercent);

        // §18 vacuum gate (Vacuum.cs). Threaded into the pure layer as a primitive rather than
        // early-returned here, per the house rule: "ozone twilight is zero without an atmosphere"
        // is a decision that belongs next to the twilight math and its unit tests, so the shipped
        // behaviour and the pinned behaviour are literally the same code.
        bool inVacuum = Vacuum.InVacuumForMap(map);

        return OzoneTwilightMath.BandStrength(elevation, inVacuum);
    }

    // The minimum overlay brightness §7a should honour right now: the caller's own setting, raised
    // while the sun sits in the ozone band so the blue is not multiplied into black.
    //
    // Returns baseMinBrightness unchanged whenever §19 is inactive, which is what keeps this a pure
    // addition to Patch_PitchBlackOverlay rather than a change to its existing behaviour.
    public static float OverlayFloorFor(Map map, float baseMinBrightness) =>
        OzoneTwilightMath.OverlayFloor(baseMinBrightness, BandStrengthFor(map), OzoneTwilightSettings.TintStrength);

    // §17's enclosed-map gate plus issue #35's blacked-out sky, mirrored from the other CurSkyTarget
    // postfixes. A cavern has no sky to scatter through, and an overhead opaque sulfur cloud means
    // no sunlight is entering the ozone path for the band to colour — in both cases the WeatherDef's
    // own palette should stand as authored.
    private static bool HasNoSky(Map map) => MapSky.IsEnclosed(map) || MapSky.SkyBlackedOut(map);
}
