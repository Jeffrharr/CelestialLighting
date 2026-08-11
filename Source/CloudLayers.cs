using RimWorld;
using Verse;

namespace CelestialLighting;

// The thin adapter for §23b (issue #88 option 2): pulls the live values the pure layer needs — how
// much of the sky is cloud, how high its base is, where the sun is, and whether there is any air —
// and answers the two questions the overlay asks each frame: how strong is the underlit-cloud layer,
// and what colour is it.
//
// SHARED READ, the same discipline as SnowGlare.AlphaFor and WeatherDimming.DimmingFor: the probe and
// the draw hook both come through here, so a harness reading can never disagree with what was
// actually drawn. That matters more here than usual — issue #88's open question for option 2 is
// whether spatial warm patches read as sky drama or as stains on the ground, and a probe measuring a
// different number than the screen shows would make that question unanswerable.
public static class CloudLayers
{
    // The strength knob actually used, seeded from the pure core's starting guess. Mutable ONLY so a
    // live harness sweep can move it within one RimWorld boot, exactly the dev seam
    // SnowGlare.IntensityScale opened for the same kind of question: "at what strength does this stop
    // reading as underlit cloud and start reading as blotches" is a question about several values of
    // one constant, and rebuilding the mod per value would compare frames from different processes.
    //
    // Nothing in the shipped mod writes it, so a player's game always runs the calibrated default.
    public static float AmplitudeScale = CloudUnderlightMath.LayerAmplitude;

    // The same seam for the other two lanes, existing for the same one reason and written by nothing
    // the player can reach. All three are prototypes whose calibration is a taste call, and a taste
    // call is only honest if the alternatives came out of one process at one instant.
    public static float ShadowAmplitudeScale = CloudShadowMath.ShadowAmplitude;

    public static float SheetAmplitudeScale = CloudSheetMath.SheetAmplitude;

    // How much of this map's sky is cloud right now, in [0, 1] — the field's own input.
    //
    // TWO SOURCES, AND THEY CANNOT BOTH BE LIVE. §13 classifies a WeatherDef's deck opacity and scores
    // Clear as exactly 0 on both its axes; §22 invents a fractional cloud amount that only exists
    // WHILE the weather is Clear. So the two are mutually exclusive by construction — the same
    // property Patch_CloudCoverSky's header records for why it never fights Patch_WeatherDimming —
    // and taking the larger of them is a max over a pair that always has a zero in it, not a blend of
    // two competing opinions.
    //
    // Reading BOTH is the whole reason §23b can do something §23 cannot. §23 keys on §13's opacity
    // alone, so it is silent on a Clear day by construction; issue #88's most common real sunset — a
    // partly-cloudy clear evening — is exactly the case where structure is everything and the flat
    // lane has nothing to say.
    public static float CloudFractionFor(Map map)
    {
        float deck = WeatherDimming.CloudOpacityFor(map);
        if (deck > 0f)
            return deck;

        return map.weatherManager?.curWeather == WeatherDefOf.Clear
            ? CloudCoverClock.FractionForMap(map)
            : 0f;
    }

    // The additive layer's strength for this map right now, in [0, AmplitudeScale]. Exactly 0 whenever
    // the layer must not draw at all, which the overlay reads as "make no draw call" rather than as
    // "draw a transparent quad".
    //
    // THE GATES ARE ORDERED BY COST TIMES SELECTIVITY, NOT BY THE ORDER THE PHYSICS READS — the same
    // discipline §24 arrived at, and it matters more here. This runs once per frame for as long as a
    // save is loaded, and the honest thing to notice about §23b is that it can only ever draw during
    // the few minutes a day the sun spends inside the below-horizon glow window. Every other frame of
    // the year has to cost almost nothing.
    public static float StrengthFor(Map map)
    {
        // (1) Feature flag, and the Clouds interop riding on it. One bool, and "off" has to mean the
        // draw call does not happen — that is what makes the harness A/B a real baseline rather than
        // a picture of the mod being absent. The interop reuses that exact path rather than opening a
        // second one: with Clouds installed this lane is off, because it draws light at OUR sheets'
        // positions and theirs are somewhere else (CloudsCompat).
        if (!CloudsCompat.LaneDraws(CloudLane.UnderlightLayer, CelestialLightingFeatures.CloudUnderlightLayer))
            return 0f;

        if (map?.skyManager == null)
            return 0f;

        // (2) THE SUN'S ELEVATION, and it is by far the most selective question available. The glow
        // window runs from the horizon down to the deck's own shadow entry, which even a 10 km cirrus
        // caps at ~3.2 degrees and §23's own fade floor caps at 6 — so the layer is structurally dead
        // for all but a few minutes a day. Asking here, against the widest possible window, skips the
        // weather walk for every daytime and every night frame without needing to know the altitude
        // that would set the real window. GeometryMemo already has this value for the frame.
        float elevation = SolarPosition.ElevationForMap(map);
        if (elevation >= 0f || elevation <= SkyColorTemperature.NightFadeFloorDegrees)
            return 0f;

        // (3) Map kind. A cavern or a pocket map has no sky to put cloud in, and an unnatural darkness
        // has blacked out the one it does have — the same pair §8's own patch checks, for the same
        // reason (DESIGN.md §17).
        if (!MapSky.HasSky(map) || MapSky.SkyBlackedOut(map))
            return 0f;

        // (4) Cloud, and only now: the weather walk is the expensive read here (WeatherDimming's own
        // header records that CloudOpacityFor is deliberately un-memoized), and on a Clear map it is
        // followed by §22's hourly-cached fraction.
        float fraction = CloudFractionFor(map);
        if (fraction <= 0f)
            return 0f;

        return CloudUnderlightMath.LayerStrengthWithAmplitude(
            elevation,
            WeatherDimming.CloudAltitudeMetresFor(map),
            fraction,
            AmplitudeScale,
            Vacuum.InVacuumForMap(map));
    }

    // §23c: the alpha of the daylight cloud-shadow wash. Zero is a true no-op — the overlay returns
    // before its draw call.
    //
    // GATE ORDER IS THE MIRROR OF StrengthFor'S, and for the same reason. This lane's window is the
    // opposite one (sun UP rather than down), and it is far wider — most of the day, on most maps — so
    // the elevation check is much less selective here than it is there. It still goes first because it
    // is a memoised float against a constant, and it still saves the weather walk on every night frame.
    public static float ShadowAlphaFor(Map map)
    {
        // Same pairing as StrengthFor's opening gate, and the same reason for it: this wash lands
        // where WE put the cloud, so it stands down when another mod is drawing the clouds.
        if (!CloudsCompat.LaneDraws(CloudLane.GroundShadow, CelestialLightingFeatures.CloudShadow))
            return 0f;

        if (map?.skyManager == null)
            return 0f;

        float elevation = SolarPosition.ElevationForMap(map);
        if (elevation <= 0f)
            return 0f;

        if (!MapSky.HasSky(map) || MapSky.SkyBlackedOut(map))
            return 0f;

        float fraction = CloudFractionFor(map);
        if (fraction <= 0f)
            return 0f;

        return CloudShadowMath.ShadowAlphaWithAmplitude(
            elevation, fraction, ShadowAmplitudeScale, Vacuum.InVacuumForMap(map));
    }

    // §25: the alpha of the drawn cloud sheet.
    //
    // NO ELEVATION GATE, which is the one structural difference from the two illumination lanes and
    // the reason §25 is the expensive one. Cloud is there at night too — it is the only one of the
    // three that draws around the clock — so the cheap, highly selective question the others open with
    // simply does not exist here, and the cloud fraction is the first real gate. That is affordable
    // only because the fraction itself is cached (§22 hourly, §13 per weather) rather than walked.
    public static float SheetAlphaFor(Map map)
    {
        // TWO FLAGS, and CloudCover is the master of the pair (see CelestialLightingFeatures
        // .CloudSheet for why). Asked here rather than left to CloudFractionFor: that function's §22
        // arm already reads 0 with partial cover off, but its §13 arm does not, so a rainy day would
        // otherwise keep growing sheets for a player who switched the mod's cloud opinion off.
        //
        // The Clouds interop folds in as a third condition rather than a fourth line, because it is
        // the same question: whether this mod draws cloud at all. It is the strongest case of the
        // three lanes — ours and theirs would be two sets of cloud shapes over one map, not merely
        // two placements of one — so with Clouds installed the sheets are theirs (CloudsCompat).
        if (!CloudsCompat.LaneDraws(
                CloudLane.DrawnSheet,
                CelestialLightingFeatures.CloudCover && CelestialLightingFeatures.CloudSheet))
            return 0f;

        if (map?.skyManager == null)
            return 0f;

        if (!MapSky.HasSky(map) || MapSky.SkyBlackedOut(map))
            return 0f;

        float fraction = CloudFractionFor(map);
        if (fraction <= 0f)
            return 0f;

        // NO ILLUMINATION TERM HERE ANY MORE (§25b). This used to fold SheetBrightness(CurSkyGlow) in,
        // which was fine while every cloud was equally lit and is wrong now that they are not: a sheet
        // is lit by whichever deck it is on, and at 2.4 degrees below the horizon those differ by the
        // entire range. The overlay applies CloudSheetMath.DeckIllumination per sheet; this is the
        // lane's ceiling, and the probe on it reports the gate rather than what reaches a pixel.
        return CloudSheetMath.SheetAlphaWithAmplitude(
            fraction, SheetAmplitudeScale, Vacuum.InVacuumForMap(map));
    }

    // The SUNWARD end of the layer's colour: §8's own target colour at this elevation, not a second
    // reddening model of our own.
    //
    // This is the same discipline §23 kept when it chose to modulate §8's tint rather than introduce a
    // colour target — see DESIGN.md §23. Light reaching a cloud base from below has grazed a very long
    // atmospheric path, and §8's curve at a below-horizon elevation is already this codebase's one
    // canonical answer for what a path that long does to sunlight. A private "even redder than §8"
    // curve here would be a second opinion about the same physics, exactly the drift DESIGN.md
    // §20/§20d warns about for mired space.
    public static SkyColorTemperature.Rgb HotTintFor(Map map)
    {
        float elevation = SolarPosition.ElevationForMap(map);
        return SkyColorTemperature.SkyColorForElevation(
            elevation,
            SiteAltitude.PressureFractionForMap(map),
            SiteAltitude.AerosolFractionForMap(map),
            SiteAltitude.AngstromExponentForMap(map),
            Vacuum.InVacuumForMap(map));
    }

    // The ANTI-SOLAR end: §19c's composed twilight hue, this codebase's existing answer for the
    // purple/magenta a twilight sky carries away from the sun (DESIGN.md §19c).
    //
    // WHY A SECOND COLOUR AT ALL, when §23 was so careful to have only one. Because one colour is
    // precisely what the flat lane already is. A single tint spread over the whole field adds warm
    // light everywhere, which reads as the map being turned up rather than as a sunset; what makes a
    // real one dramatic is that the light arriving at the GROUND differs by direction — deep orange
    // bounced off deck lit through the reddest path, pink off deck lit by the anti-solar sky. Two
    // ends is the minimum that can express that, and both ends are borrowed rather than invented, so
    // this is still not a new colour authority.
    //
    // Delegates to §19c's own adapter rather than calling PurpleLightMath directly, so the five
    // inputs it composes (elevation, latitude, pressure, aerosol, Angstrom) come from the same
    // memoised reads §19c uses — see PurpleLight.ComposedHueFor's note on why an independently-read
    // latitude drifts from the elevation it is meant to pair with. §23b gets §19c's hue, not a second
    // computation of it, which also means the two cannot disagree about what twilight purple is.
    public static SkyColorTemperature.Rgb CoolTintFor(Map map) => PurpleLight.ComposedHueFor(map);

    // Which way the sun lies, as the tiling axis CloudField's colour gradient runs along.
    // Read through the same SolarPosition.InputsForMap the shadow direction uses (§1), so the colour
    // gradient and the shadows on the ground agree about where the sun is.
    public static void GradientAxisFor(Map map, out int axisU, out int axisV)
    {
        SolarPosition.Inputs inputs = SolarPosition.InputsForMap(map);
        float elevation = SolarPosition.ElevationForMap(map);
        float azimuth = Formulas.SolarAzimuthDegrees(
            inputs.Latitude, inputs.Declination, elevation, inputs.DayPercent);

        CloudField.GradientAxis(azimuth, out axisU, out axisV);
    }
}
