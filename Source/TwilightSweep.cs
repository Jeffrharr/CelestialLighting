using Verse;

namespace CelestialLighting;

// The thin adapter for §26 (issue #140): pulls the live values the pure sweep needs — where the sun
// is, which way it lies, whether this map has a sky and whether there is any air — and answers the
// three questions the overlay asks each frame: how far has the boundary crossed, which way does it
// run, and what colour are its two ends.
//
// SHARED READ, the same discipline as CloudLayers.StrengthFor and SnowGlare.AlphaFor: the probe and
// the draw hook both come through here, so a harness reading can never disagree with what was
// actually drawn. That matters more for §26 than for most, because the thing under test is a
// POSITION over time — a probe reading a different sweep than the frame shows would make a filmed
// sweep unfalsifiable.
public static class TwilightSweep
{
    // The strength knob actually used, seeded from the pure core's calibrated default. Mutable ONLY
    // so a live harness sweep can move it within one RimWorld boot — the same dev seam
    // CloudLayers.AmplitudeScale and SnowGlare.IntensityScale opened, and for the same reason: "at
    // what strength does this stop reading as dusk and start reading as a wipe transition" is a
    // question about several values of one constant, and rebuilding the mod per value would compare
    // frames from different processes.
    //
    // Nothing in the shipped mod writes it, so a player's game always runs the calibrated default.
    public static float AmplitudeScale = TwilightSweepMath.SweepAmplitude;

    // How far the shadow boundary has crossed the map, in [0, 1] along the sun axis. Exactly 0
    // whenever the sweep must not draw at all, which the overlay reads as "make no draw call" rather
    // than as "draw a transparent quad".
    //
    // A ZERO IS AMBIGUOUS HERE IN A WAY IT IS NOT FOR THE OTHER LANES, and that is deliberate rather
    // than sloppy. 0 means both "the sun is still up" and "the boundary is at the anti-solar edge",
    // and the second of those is a real drawable state — but its intensity is zero anyway, because
    // WindowEnvelope is zero at sweep 0. So the two collapse to the same rendering, and collapsing
    // them here keeps the overlay's gate a single float comparison.
    public static float PositionFor(Map map)
    {
        // (1) Feature flag first. "Off" has to mean the draw call does not happen — that is what makes
        // the harness A/B a real baseline rather than a picture of the mod being absent.
        if (!CelestialLightingFeatures.TwilightSweep)
            return 0f;

        if (map?.skyManager == null)
            return 0f;

        // (2) THE SUN'S ELEVATION, and it is by far the most selective question available — the same
        // gate order CloudLayers.StrengthFor arrived at, for the same reason. §26 is structurally dead
        // for every daytime and every night frame, which is the overwhelming majority of them, and
        // GeometryMemo already holds this value for the frame.
        float elevation = SolarPosition.ElevationForMap(map);
        if (elevation >= 0f || elevation <= TwilightSweepMath.SweepFloorDegrees)
            return 0f;

        // (3) Map kind. A cavern or a pocket map has no sky for a shadow to rise into, and an
        // unnatural darkness has blacked out the one it does have — the same pair §8's own patch
        // checks (DESIGN.md §17).
        if (!MapSky.HasSky(map) || MapSky.SkyBlackedOut(map))
            return 0f;

        return TwilightSweepMath.SweepPosition(elevation, Vacuum.InVacuumForMap(map));
    }

    // This frame's peak additive alpha, i.e. the envelope times the amplitude. Separate from the
    // position because they answer different questions — "where is it" and "how strong is it" — and
    // the probe wants both: a sweep that is in the right place at zero strength and one that is at
    // full strength in the wrong place fail identically if only one number is pinned.
    public static float AmplitudeFor(Map map)
    {
        float sweep = PositionFor(map);
        if (sweep <= 0f)
            return 0f;

        return TwilightSweepMath.WindowEnvelope(sweep) * AmplitudeScale;
    }

    // Which way the sun lies, as the unit vector in map UV the band runs along.
    //
    // Read through the same SolarPosition.InputsForMap the shadow direction uses (§1), so the sweep
    // and the shadows on the ground agree about where the sun is. NOT quantised to CloudField's eight
    // lattice directions: §26 draws one clamped quad rather than a tiling field, so it has no
    // periodicity to preserve and can use the true azimuth — see TwilightSweepField's header on why
    // that is the whole reason §26 sidesteps issue #139.
    public static void AxisFor(Map map, out float axisU, out float axisV)
    {
        SolarPosition.Inputs inputs = SolarPosition.InputsForMap(map);
        float elevation = SolarPosition.ElevationForMap(map);
        float azimuth = Formulas.SolarAzimuthDegrees(
            inputs.Latitude, inputs.Declination, elevation, inputs.DayPercent);

        TwilightSweepField.SunwardAxis(azimuth, out axisU, out axisV);
    }

    // Where the boundary sits for §25's cloud deck rather than for the ground — the depth half of
    // issue #140.
    //
    // The deck is ABOVE the ground, so Earth's shadow reaches it later: it behaves as if the sun were
    // still up by the deck's own shadow-entry angle, which §23 already computes from the altitude §13
    // classifies. Reusing both means the parallax between the ground's edge and the clouds' edge is a
    // consequence of the weather the player can already see, not a tuned offset.
    public static float DeckPositionFor(Map map)
    {
        float ground = PositionFor(map);
        if (ground <= 0f)
            return 0f;

        float entry = CloudUnderlightMath.ShadowEntryDepressionDegrees(
            WeatherDimming.CloudAltitudeMetresFor(map));

        return TwilightSweepMath.DeckSweepPosition(
            SolarPosition.ElevationForMap(map), entry, Vacuum.InVacuumForMap(map));
    }

    // The SUNWARD end of the band's colour: §8's own target colour at this elevation, and the
    // ANTI-SOLAR end: §19c's composed twilight hue. Both borrowed rather than invented, exactly as
    // CloudLayers does — see its CoolTintFor note on why a private palette here would be a second
    // opinion about what twilight looks like.
    public static SkyColorTemperature.Rgb HotTintFor(Map map) => CloudLayers.HotTintFor(map);

    public static SkyColorTemperature.Rgb CoolTintFor(Map map) => CloudLayers.CoolTintFor(map);
}
