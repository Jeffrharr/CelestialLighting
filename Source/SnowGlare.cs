using Verse;

namespace CelestialLighting;

// Thin adapter for §24 (issue #90): pulls the three primitives SnowGlareMath needs off live state —
// how much of §21's cavity overflowed the multiply lane, how bright the map currently is, and
// whether there is any air here at all — and answers one question, "how strong is the glare quad
// this frame".
//
// SHARED READ, the same discipline as SurfaceBuildup.CavityGainFor and WeatherDimming.DimmingFor:
// the probe and the draw hook both come through here, so a harness reading can never disagree with
// what was actually drawn. That mattered immediately — issue #90's whole open question is whether
// the drawn result reads as bright or as washed out, and a probe measuring a different number than
// the screen shows would make that question unanswerable.
public static class SnowGlare
{
    // The strength knob actually used, seeded from the calibrated default. Mutable ONLY so the live
    // harness can sweep it within one RimWorld boot — the same dev seam SunClockAdapter.WarpEnabled
    // uses, and for the same reason: issue #90's open question is "at what strength does this stop
    // reading as glare and start reading as haze", which is a question about several values of one
    // constant, and rebuilding the mod per value would compare frames from different processes.
    //
    // Nothing in the shipped mod writes it, so a player's game always runs the calibrated default.
    public static float IntensityScale = SnowGlareMath.DefaultIntensityScale;

    // The ceiling, seeded the same way and mutable for the same one reason. It has to move WITH the
    // scale during a harness sweep or the sweep measures the clamp instead of the knob: at the strong
    // end the calibrated 0.12 ceiling clips before the scale has finished having an effect, which
    // would read as "raising the strength stopped doing anything" rather than as "the ceiling bit".
    public static float MaxIntensity = SnowGlareMath.MaxIntensity;

    // The additive overlay alpha for this map right now, in [0, SnowGlareMath.MaxIntensity]. Exactly
    // 0 when the feature is off, when there is no map or sky manager yet (map generation asks for
    // draws), when §21's cavity had no overflow — every bare-ground map and every cloudless sky — and
    // on a vacuum map.
    public static float AlphaFor(Map map)
    {
        // Feature gate first, so "off" costs one bool read and reproduces pre-§24 rendering exactly:
        // returning 0 here means SnowGlareOverlay draws nothing at all, not a transparent quad. The
        // difference is a draw call per frame per map, which is precisely what the harness A/B is
        // measuring, so "off" has to mean the draw does not happen.
        if (!CelestialLightingFeatures.SnowGlare)
            return 0f;

        if (map?.skyManager == null)
            return 0f;

        // THE GATES BELOW ARE ORDERED BY COST TIMES SELECTIVITY, NOT BY THE ORDER THE PHYSICS READS.
        // This runs once per frame on the visible map for as long as a save is loaded, so what matters
        // is what a map with NO SNOW pays — which is every map in most biomes, and every snowy map for
        // most of the year. Before these two checks existed that map still walked the full weather
        // classifier every frame to arrive at zero.
        //
        // (1) BUILDUP. One field read per grid (SnowGrid/SandGrid keep TotalDepth as a maintained
        // running total — see SurfaceBuildup's header), and by far the most selective thing we can
        // ask: no buildup means the cavity gain is exactly 1, which means the residual is exactly 0,
        // whatever the sky is doing. Nothing downstream can rescue a map that fails this.
        if (!SurfaceBuildup.HasBuildup(map))
            return 0f;

        // (2) DAYLIGHT. A cached float on SkyManager. Zero glow is night, and §24 does not fire at
        // night by construction (see SnowGlareMath.DaylightAboveNightFloor) — asking here skips the
        // weather walk for every night frame on a snowed-in map, which on a polar colony is most of
        // them.
        if (map.skyManager.CurSkyGlow <= 0f)
            return 0f;

        // (3) WEATHER, and only now. §21's residual is a property of the PAIR (dimming, gain) — see
        // UndrawableExcessFor's header for why the two halves of one product must come from one read.
        //
        // This is also where the WEATHER gate itself lives, and it is a CLOUD gate rather than a
        // weather-def one (issue #134): UndrawableExcessFor returns 0 when nothing is overhead, and on
        // Clear weather "what is overhead" is answered by §22's continuous cloud-cover fraction rather
        // than by §13's abstention. So a partly-cloudy Clear day does fire, with the whole cavity in
        // the additive lane because there is no dimming for the multiply lane to give back; a sky §22
        // reports as genuinely cloudless still returns 0 here. §24 is a cloud effect end to end, and
        // it now ramps with the cloud rather than with the weather def's name.
        float excess = WeatherDimming.UndrawableExcessFor(map);
        if (excess <= 0f)
            return 0f;

        // §7's floor is passed alongside the live glow so the pure core can subtract it — see
        // SnowGlareMath.DaylightAboveNightFloor for why that subtraction is what stops §21 being paid
        // for twice on a snowy night, and why the harness caught it when the offline tests could not.
        // FloorGlowFor is the ALREADY-AMPLIFIED floor (it multiplies by the same cavity gain), which
        // is exactly the quantity that has to come out.
        return SnowGlareMath.GlareAlpha(
            excess,
            map.skyManager.CurSkyGlow,
            NightRadiance.FloorGlowFor(map),
            IntensityScale,
            MaxIntensity,
            Vacuum.InVacuumForMap(map));
    }
}
