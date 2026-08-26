using RimWorldTestHarness.Mod.Probes;
using UnityEngine;
using Verse;
using Verse.Glow;

namespace CelestialLighting.Probes;

// How many cells the per-cell replacement actually claims, split by where they are.
//
// WHY A COUNT AND NOT A LEVEL. The rule is a decision rather than a quantity: every cell it claims
// gives up the whole of one emitter's vanilla light and gets our model in its place, so "how bright
// did it get" is the wrong question and "which cells changed hands" is the right one. A level probe
// over the same region would report the composition's output and could not tell a rule that fired on
// nothing from a rule that fired everywhere and happened to agree with vanilla.
//
// AND WHY IT IS SPLIT IN TWO, which is the part worth keeping. The rule has two failure modes and
// they are opposite. Claiming too little leaves an aperture's light split between two renderers,
// which is what it exists to fix; claiming too much takes the near field off every lamp, which is
// exactly what sank the global aperture beam (the lamp cell went 19.89 -> 17.87 L*). A single total
// cannot tell those apart — a rule that claimed the lamp's own room and nothing outside it would
// report a healthy-looking number. So Home counts the claimed cells under the lamp's own roof, where
// the answer has to be zero, and Beyond counts the claimed cells out from under it, where the answer
// has to be something.
//
// COUNTED OVER CELLS VANILLA ACTUALLY LIT. A cell vanilla never reached is claimed by the rule and
// changes nothing when it is: there is no vanilla light there either to keep or to take, and the
// composition has always drawn our whole model there. Including those would report the far side of
// every open door as a claim and drown the cells that genuinely changed hands.
public sealed class VectorLightBentProbe : IProbe, IProbeMetadata
{
    public enum Metric
    {
        // Claimed cells that are ROOFED — the lamp's own room and anything else under cover. This
        // is the one that must read zero: it is the near-field radiance, in cells.
        Home,

        // Claimed cells that are UNROOFED — the ground beyond an opening, which is the region the
        // whole subsystem is for. Zero here means the rule is inert on this scene.
        Beyond,

        // Cell-emitter pairs the mask ACTUALLY took vanilla's light off since the last rebake, out
        // of VectorLightMask's own telemetry. The other two describe the scene and read the same
        // with the flag on or off; this one is the outcome, and it is what separates "the rule has
        // nothing here to claim" from "the rule never ran".
        Applied,

        // Unroofed cells the VISIBILITY FLOOR claims, as opposed to the detour rule. Reported
        // separately because the two rules claim nearly disjoint sets and a combined count could
        // not say which one a frame moved.
        FloorBeyond,

        // Roofed cells the floor claims — the one that says whether it has reached into the lamp's
        // own room, which is what the near field is lost through.
        FloorHome,
    }

    private readonly Metric metric;

    public VectorLightBentProbe(string name, Metric metric)
    {
        Name = name;
        this.metric = metric;
    }

    public string Name { get; }

    public string Description => metric switch
    {
        Metric.Home =>
            "roofed cells the per-cell replacement claims — the near field, which must stay vanilla's",
        Metric.Beyond => "unroofed cells the detour rule claims from vanilla",
        Metric.FloorBeyond => "unroofed cells the visibility floor claims from vanilla",
        Metric.FloorHome => "roofed cells the visibility floor claims — the near-field risk, in cells",
        _ => "cell-emitter pairs the mask really took vanilla's light off, since the last rebake",
    };

    public string Unit => "cells";

    public float Read(Map map)
    {
        // Read before the map test, because it is a counter rather than a walk and has an answer
        // even on a frame with nothing to walk.
        if (metric == Metric.Applied)
            return VectorLightMask.BentSamples;

        if (map == null)
            return 0f;

        GlowGridPerLight.Reader reader = GlowGridPerLight.For(map);

        if (reader == null)
            return 0f;

        int claimed = 0;

        foreach (VectorLightField.LightEntry entry in VectorLightField.LightsFor(map))
            claimed += ClaimedBy(map, reader, entry);

        return claimed;
    }

    // REPORTS WHAT THE RULE WOULD CLAIM, not whether the flag is on, so an arm with the flag off
    // still reads the region's size. That makes a zero under the flag mean "there is nothing here
    // to claim" rather than "the feature is switched off", which are the two readings a scenario has
    // to be able to tell apart.
    private int ClaimedBy(
        Map map, GlowGridPerLight.Reader reader, VectorLightField.LightEntry entry)
    {
        if (!reader.TryResolveEmitter(entry.VanillaKey, out GlowLight light, out var colors))
            return 0;

        int claimed = 0;
        CellRect reach = light.AffectedRect;

        for (int z = reach.minZ; z <= reach.maxZ; z++)
        {
            for (int x = reach.minX; x <= reach.maxX; x++)
            {
                IntVec3 cell = new IntVec3(x, 0, z);

                bool wantRoofed = metric == Metric.Home || metric == Metric.FloorHome;

                if (!cell.InBounds(map) || map.roofGrid.Roofed(cell) != wantRoofed)
                    continue;

                int local = light.WorldToLocalIndex(cell);

                if (local < 0 || local >= colors.Length)
                    continue;

                Color32 own = colors[local];
                bool delivered = own.r != 0 || own.g != 0 || own.b != 0;

                // Cells vanilla never lit are excluded rather than counted as claims — see the
                // header. The predicate is still asked with `delivered: true` so that what is being
                // counted is the DETOUR test alone.
                if (!delivered)
                    continue;

                bool floorRule = metric == Metric.FloorBeyond || metric == Metric.FloorHome;

                bool takes = floorRule
                    ? VectorLightLiftMath.VanillaTooDimToKeep(
                        Mathf.Max(own.r, Mathf.Max(own.g, own.b)))
                    : VectorLightLiftMath.VanillaBentToArrive(
                        x - light.position.x, z - light.position.z, own.a, delivered: true);

                if (takes)
                    claimed++;
            }
        }

        return claimed;
    }
}
