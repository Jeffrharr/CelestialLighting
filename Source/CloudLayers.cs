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
    // ONE SOURCE, AND IT IS §22's. §25f (DESIGN.md) settled which weathers this mod draws cloud in:
    // the answer is Clear ones, because the thing all three lanes render is BOUNDED SHEETS — discrete
    // objects with edges and gaps between them — and that is what a partly-cloudy sky looks like, not
    // what an overcast one does. A weather that carries a deck is §13's to render, by dimming the whole
    // map, and this lane stays out of it.
    //
    // §13'S DECK USED TO BE ADDED TO THIS, and removing it is the point of §25f rather than a
    // simplification along the way. Cover was `deck + clearShare x clearCover`, so settled Rain placed
    // the full cap of sheets at cover 1.0 — and §25's own section already recorded the bill for that:
    // over a solid overcast the sheets and §13's flat dimming both render the same deck, the map comes
    // out darker than either intends, and `SheetAmplitude` was held down to 0.35 to make room. Below a
    // deck exactly one lane is now live, so neither has to be detuned for the other.
    //
    // WHAT §23b LOSES BY IT, stated rather than discovered later. §23b's whole claim over §23 was that
    // it reads both sources and so has something to say on a Clear day; it keeps that, since the Clear
    // day is the case it was built for (issue #88's partly-cloudy evening). What it gives up is the
    // overcast sunset, where it now draws nothing — which is the same ruling as above, applied to the
    // lane that renders the light under a sheet rather than the sheet: there is no sheet there to
    // underlight.
    //
    // THE TRANSITION IS A CROSS-FADE, NOT A SWITCH. RimWorld cross-fades the outgoing weather into the
    // incoming one over 4,000 ticks, and the Clear share below rides that same factor, so a Clear day
    // turning to rain walks this fraction down to zero over that hour and a half and the clouds fade
    // off the screen where they are. The earlier arrangement — deck first, §22 only when the deck was
    // exactly zero — got that instant badly wrong in the one direction that shows: at the tick a Clear
    // day turns to rain, curWeather is already Rain and the lerp factor is still 0, so the fraction
    // fell from §22's cover to nothing in a single tick and every sheet on the map vanished at once.
    public static float CloudFractionFor(Map map) =>
        CloudFractionAtTick(map, Find.TickManager?.TicksAbs ?? 0);

    // The same fraction with §22's COVER READ AT A GIVEN TICK, which is the seam §25 places its sheets
    // through — each one asking "how cloudy was it when I came over the edge" (CloudSheetDraw), so its
    // existence is settled before it is visible and cannot change while it is.
    //
    // ONLY THE COVER IS TIME-SHIFTED, NOT THE WEATHER SHARE, and the asymmetry is the point rather than
    // an omission. §22's cover is a property of the AIR — how much cloud is drifting about over this
    // tile — so asking what it was when a particular cloud arrived is a sensible question with a stable
    // answer. The weather is a global state vanilla cross-fades on its own clock; a sheet that latched
    // "it was Clear when I arrived" would go on being a fair-weather cloud in the middle of a storm for
    // up to a whole crossing. So the share is always read live, which means a weather change reaches
    // every sheet at once and fades them together.
    public static float CloudFractionAtTick(Map map, int absTick) =>
        CoverFrom(map, ClearShareFor(map), absTick);

    // How much of this map's sky is a Clear sky right now, in [0, 1] — 1 in settled Clear weather, 0 in
    // any settled weather that is not, and vanilla's own transition lerp in between.
    //
    // PULLED OUT AS ITS OWN READ so a caller placing a dozen sheets pays for it once instead of a dozen
    // times. The arithmetic is CloudWeatherGateMath.ClearShare's, which is where the Clear-to-Clear
    // re-roll case and the reason the two arms are summed rather than picked are recorded; this half
    // only reads the three live values it takes.
    public static float ClearShareFor(Map map)
    {
        WeatherManager weather = map?.weatherManager;
        if (weather == null)
            return 0f;

        return CloudWeatherGateMath.ClearShare(
            weather.TransitionLerpFactor,
            weather.lastWeather == WeatherDefOf.Clear,
            weather.curWeather == WeatherDefOf.Clear);
    }

    // The cover a sheet that entered at `absTick` is holding, given how much of the sky is Clear now.
    // §22 is read only when the Clear share is non-zero — a cheap per-tile lookup, but there is no
    // reason to touch it (or seed its cache) mid-storm, the same restraint WeatherDimming.DeckOpacityFor
    // keeps.
    public static float CoverFrom(Map map, float clearShare, int absTick)
    {
        if (!(clearShare > 0f))
            return 0f;

        return CloudWeatherGateMath.CoverFromShare(
            clearShare, CloudCoverClock.FractionForTick(map, absTick));
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

        // GATED ON WHAT IS PLACED, NOT ON THE COVER RIGHT NOW. Those were the same question until
        // sheets started latching their cover at the tick they entered the map: a sky whose cover has
        // since drifted to zero can still have a cloud finishing its crossing, and asking the live
        // cover here would blank it mid-screen — the exact pop the latch exists to remove, moved from
        // the layout into the lane's own gate. PlaceSheets is memoised per tick, so this is a
        // comparison rather than a second placement pass.
        int count = CloudSheetDraw.PlaceSheets(map, out _, out float cover);
        if (count <= 0)
            return 0f;

        // NO ILLUMINATION TERM HERE ANY MORE (§25b). This used to fold SheetBrightness(CurSkyGlow) in,
        // which was fine while every cloud was equally lit and is wrong now that they are not: a sheet
        // is lit by whichever deck it is on, and at 2.4 degrees below the horizon those differ by the
        // entire range. The overlay applies CloudSheetMath.DeckIllumination per sheet; this is the
        // lane's ceiling, and the probe on it reports the gate rather than what reaches a pixel.
        // §25d raises the lane's amplitude; see CloudSheetMath.PresentSheetAmplitude for the
        // measurement that says 0.35 was too low to see and 0.80 too high to play under.
        // SheetAmplitudeScale stays the §25b value AND the dev knob for that path, so turning the
        // feature off reproduces the shipped lane exactly rather than approximately.
        float amplitude = CelestialLightingFeatures.CloudPresence
            ? CloudSheetMath.PresentSheetAmplitude
            : SheetAmplitudeScale;

        // The player's opacity slider, applied HERE rather than at the draw call, so the probe on
        // this function reports the alpha that actually reaches a pixel. That is the shared-read
        // discipline this whole file exists for: a scenario measuring the slider must not be able to
        // read a number the screen disagrees with.
        //
        // It scales the amplitude rather than the finished alpha, which is the same value at this
        // point and not the same statement: everything downstream — the deck's opacity, the overlap
        // boost, a sheet's own edge falloff — is a fact about a particular cloud, and the slider is
        // a fact about how thick this player wants cloud drawn.
        amplitude = CloudSheetMath.AmplitudeAtOpacity(amplitude, CloudSheetSettings.OpacityScale);

        // The cover handed to the pure core is the heaviest any PLACED sheet is holding, not the live
        // one, for the same reason the gate above changed: those two are the same number until a sheet
        // outlives the cover it entered under, and on that frame the live one says "no cloud" about a
        // sky that visibly has one.
        return CloudSheetMath.SheetAlphaWithAmplitude(
            cover, amplitude, Vacuum.InVacuumForMap(map));
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
    public static void GradientAxisFor(Map map, out int axisU, out int axisV) =>
        CloudField.GradientAxis(SunAzimuthFor(map), out axisU, out axisV);

    // Where the sun is, in degrees clockwise from north. Read through the same
    // SolarPosition.InputsForMap the shadow direction uses (§1), so everything this file hands out —
    // the colour gradient's axis, and §25c's light direction — agrees with the shadows on the ground
    // about where the sun is. Both callers go through here rather than deriving it twice: two
    // derivations is two chances for the cloud tops and the shadows under them to disagree.
    public static float SunAzimuthFor(Map map)
    {
        SolarPosition.Inputs inputs = SolarPosition.InputsForMap(map);
        float elevation = SolarPosition.ElevationForMap(map);

        return Formulas.SolarAzimuthDegrees(
            inputs.Latitude, inputs.Declination, elevation, inputs.DayPercent);
    }
}
