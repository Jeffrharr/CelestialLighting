using System.Collections.Generic;
using RimWorldTestHarness.Mod.Probes;
using UnityEngine;
using Verse;

namespace CelestialLighting.Probes;

// Reads back what SetGlowColors did, so a scenario can pin the palette instead of trusting it.
//
// WHY THIS EXISTS AT ALL. SetGlowColors fails when it matches no lamps, but it cannot fail on the
// case that actually costs a run: matching the lamps and having the change not reach §27's roster.
// The colour lives in two places once it is set — CompGlower's override, and the LightEntry the
// field holds — and only the second one is what gets composited. A stress scenario whose five
// hundred lamps are all still lamp-yellow on screen while the overrides are all set is a scenario
// that spent ten minutes measuring one colour, and nothing else in the report would say so.
//
// So the metrics come in pairs on purpose: Overrides counts the comps, DistinctEmitterColors counts
// the colours §27 is actually holding. Pinned together they separate "the step did nothing" from
// "the step worked and the roster did not hear about it", which read identically in a frame.
public sealed class GlowPaletteProbe : IProbe
{
    public enum Metric
    {
        // Lamps carrying a per-instance colour override, i.e. what SetGlowColors touched. Counts
        // every glower on the map, lit or not, matching the step's own target set — reading only lit
        // ones would turn an unpowered lamp into a missing repaint.
        Overrides,

        // Distinct colours among the emitters §27 is holding, quantised to whole 0-255 channels.
        // This is the far side of the pipe: it reads VectorLightField's own entries, not the comps,
        // so it is only high if the recolour travelled through GlowGrid's register/deregister pair
        // and into the roster.
        DistinctEmitterColors,

        // Distinct radii among those same emitters, counted in the units the material cache is
        // actually keyed in: VectorLightOverlay's MaterialFor keys on RoundToInt(radius * 4), i.e.
        // quarter cells, and builds a 256x32 gradient plus a material per key. So this is the
        // number of those caches a scenario is exercising, and a fixture reading 1 has however many
        // lamps and exactly one entry — which is the shape of fixture that let a per-emitter texture
        // overflow ship. Quarter cells rather than whole ones because rounding to whole cells would
        // report two radii as one where the cache holds two, i.e. it would understate the very thing
        // the metric exists to insist on.
        DistinctEmitterRadii,
    }

    private readonly Metric metric;

    public GlowPaletteProbe(string name, Metric metric)
    {
        Name = name;
        this.metric = metric;
    }

    public string Name { get; }

    public float Read(Map map)
    {
        if (map == null)
            return 0f;

        return metric == Metric.Overrides ? CountOverrides(map) : CountDistinct(map, metric);
    }

    private static float CountOverrides(Map map)
    {
        int overrides = 0;

        foreach (Thing thing in map.listerThings.AllThings)
        {
            CompGlower glower = (thing as ThingWithComps)?.GetComp<CompGlower>();
            if (glower != null && glower.HasGlowColorOverride)
            {
                overrides++;
            }
        }

        return overrides;
    }

    // Colours are keyed on whole 0-255 channels rather than the float triple the entry holds.
    // VectorLightField stores each channel as glow.r / 255f, so two lamps that vanilla would call
    // the same colour can differ in the last bit of a float and read as two colours — which would
    // make this metric count rounding rather than palette.
    private static float CountDistinct(Map map, Metric metric)
    {
        HashSet<long> seen = new HashSet<long>();

        foreach (VectorLightField.LightEntry entry in VectorLightField.LightsFor(map))
        {
            seen.Add(metric == Metric.DistinctEmitterRadii
                ? (long)Mathf.RoundToInt(entry.Radius * 4f)
                : ColorKey(entry));
        }

        return seen.Count;
    }

    // ELEVEN BITS A CHANNEL, NOT EIGHT. A glow colour is allowed past 255 to mean brighter than
    // white — SunLamp is (370,370,370) — and the field stores it as glow.r / 255f, so a channel
    // arrives here as 1.45 and comes back out of the round as 370. Packing at eight or ten bits
    // would wrap those into other colours' keys and undercount the palette, which is the one
    // direction this metric must not fail in: an undercount reads exactly like a recolour that
    // never reached the roster.
    private static long ColorKey(VectorLightField.LightEntry entry) =>
        ((long)Mathf.RoundToInt(entry.Color.r * 255f) << 22)
        | ((long)Mathf.RoundToInt(entry.Color.g * 255f) << 11)
        | (long)Mathf.RoundToInt(entry.Color.b * 255f);
}
