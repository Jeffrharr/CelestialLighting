using System;
using RimWorldTestHarness.Mod.Probes;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using Verse;
using Verse.Glow;

namespace CelestialLighting.Probes;

// §27 phase 3c: what vanilla DELIVERED to one cell and what our straight-line model says it should
// have, side by side, read out of the live game.
//
// WHY IT EXISTS. The owed-light term is project(C * F(d_straight)) - delivered, and in open space
// those two have to be the same number or the whole room lifts. The offline test can only ever
// compare our model against an oracle we also wrote; this compares it against the array vanilla's own
// Burst job filled in, on the map the scenario built, for a lamp the scenario placed.
//
// IT IS ALSO THE PROBE THAT SHOULD HAVE COME FIRST. Three formulations of phase 3c were judged from
// pixels — a frame-diff centroid, a brightness-band histogram, a lit-area fraction — and two of the
// three read as plausible while being wrong, because every one of them measures the term AFTER the
// mesh has averaged it over four cells and the sky has multiplied it. Reading the two integers being
// differenced is the only measurement with nothing between it and the arithmetic. The bug it found
// was a one-cell sampling offset: ComputeGlowGridsJob.PrepareFill seeds the emitter's own cell at
// intDist 100 rather than 0, so vanilla's curve is evaluated at octile + 1 and the raw octile
// distance samples it a cell too close, everywhere, with nothing in the way.
//
// READS THE RED CHANNEL. Every glowColor in the game is warmest on red, so red is the channel
// ProjectLikeVanilla normalises the triple by, which makes it both the largest number and the one a
// projection error shows up in first. Green and blue follow it by construction.
//
// OFFSET FROM MAP CENTRE, matching GlowGridCellProbe: the fixtures are built relative to centre, so
// an absolute cell would silently read open ground if a scenario's map size ever changed.
public sealed class VectorLightOwedProbe : IProbe
{
    public enum Metric
    {
        // What vanilla's flood actually put in this cell, red channel, 0-255.
        Delivered,

        // What project(C * F(d_straight)) says a straight line should have put here, same units.
        // In open space this MUST equal Delivered; that is the property the room rests on.
        Ours,

        // The claim itself: max(Ours - Delivered, 0), scaled by coverage exactly as the mask scales
        // it. Zero in the open room, positive only through the doorway.
        Owed,

        // The polygon's verdict on the cell, 0-255. Pin it beside the others: an Owed of zero means
        // something entirely different at coverage 0 (we cannot see the cell) than at 255 (we can,
        // and vanilla already paid).
        Coverage,

        // The distance our model sampled the falloff at, in vanilla's own octile-plus-one units.
        // Pinned so a later change to the metric fails loudly rather than quietly re-introducing the
        // one-cell offset this probe was built to find.
        Distance,
    }

    private readonly IntVec3 offsetFromCentre;
    private readonly Metric metric;

    public string Name { get; }

    public VectorLightOwedProbe(string name, Metric metric, IntVec3 offsetFromCentre)
    {
        Name = name;
        this.metric = metric;
        this.offsetFromCentre = offsetFromCentre;
    }

    public float Read(Map map)
    {
        if (map == null)
            return 0f;

        IntVec3 cell = map.Center + offsetFromCentre;

        if (!cell.InBounds(map))
            return 0f;

        GlowGridPerLight.Reader reader = GlowGridPerLight.For(map);

        if (reader == null)
            return 0f;

        VectorLightField.LightEntry entry = NearestReaching(map, cell);

        if (entry == null)
            return 0f;

        if (!reader.TryResolveEmitter(entry.VanillaKey, out GlowLight light, out UnsafeList<Color32> colors))
            return 0f;

        return ReadFor(map, cell, entry, light, colors);
    }

    // Split from Read so the emitter-resolution failures above stay a flat list of guards rather
    // than nesting the measurement four levels deep.
    private float ReadFor(
        Map map, IntVec3 cell, VectorLightField.LightEntry entry, GlowLight light,
        UnsafeList<Color32> colors)
    {
        int dx = cell.x - light.position.x;
        int dz = cell.z - light.position.z;

        float straight = VectorLightMath.VanillaGlowDistance(dx, dz);

        if (metric == Metric.Distance)
            return straight;

        byte coverage = VectorLightMath.CoverageAt(
            entry.Coverage, entry.Cell.x, entry.Cell.z, entry.CoverageRadius, cell.x, cell.z);

        if (metric == Metric.Coverage)
            return coverage;

        // Outside the emitter's square there is nothing stored, which reads as delivered zero rather
        // than as an error — the same convention CoverageAt uses for the same reason.
        int local = light.WorldToLocalIndex(cell);
        int delivered = local >= 0 && local < colors.Length ? colors[local].r : 0;

        if (metric == Metric.Delivered)
            return delivered;

        // THE SAME CALLS THE MASK MAKES, IN THE SAME ORDER, deliberately duplicated rather than
        // refactored into a shared helper. A probe that shares a code path with the thing it is
        // measuring can only ever confirm that the path ran; the point here is to state the model
        // independently and let the numbers disagree if it has drifted.
        float falloff = VectorLightMath.VanillaFalloff(straight, light.glowRadius);

        VectorLightMath.OurLightAt(
            light.glowColor.r, light.glowColor.g, light.glowColor.b, falloff,
            out int ours, out _, out _);

        if (metric == Metric.Ours)
            return ours;

        return VectorLightMath.OwedLightChannel(ours, delivered, coverage);
    }

    // The emitter whose square contains this cell, nearest first.
    //
    // NEAREST rather than first-found, because the fixtures are not guaranteed to hold exactly one
    // lamp forever and "whichever the dictionary happened to yield" is the kind of subject that reads
    // a confident wrong number for a whole run. Deliberately does NOT filter on Unobstructed or on
    // PolygonDirty the way the bake does: the probe wants the emitter's arithmetic whether or not the
    // bake would have skipped it that frame, so a scenario cannot silently read zero because a
    // polygon was one frame late.
    private static VectorLightField.LightEntry NearestReaching(Map map, IntVec3 cell)
    {
        VectorLightField.LightEntry best = null;
        float bestDistance = float.MaxValue;

        foreach (VectorLightField.LightEntry entry in VectorLightField.LightsFor(map))
        {
            float distance = VectorLightMath.OctileDistance(
                cell.x - entry.Cell.x, cell.z - entry.Cell.z);

            if (distance <= entry.Radius && distance < bestDistance)
            {
                bestDistance = distance;
                best = entry;
            }
        }

        return best;
    }
}
