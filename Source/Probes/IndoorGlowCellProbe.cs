using System;
using RimWorldTestHarness.Mod.Probes;
using UnityEngine;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// The three-way split behind IndoorGlowPassthrough, at any cell a scenario cares to name. Where
// AmbientLightDoorCellProbe reads one fixed cell for issue #80's specific reproduction, this one takes
// its offset so a scenario can probe several cells in the same room and compare them.
//
// WHY ALL THREE METRICS EXIST, AND WHY READING ONE ALONE IS MISLEADING. The passthrough computes
// `skySourced = max(0, ground - artificial)` on a roofed cell. Every failure mode of that expression
// looks like a plausible number in isolation:
//
//   - GroundGlow alone can't tell "no mod is brightening this cell" from "a lamp is lighting it".
//   - SkyFraction alone reading 0 can't tell "correctly suppressed because the lamp dominates" from
//     "the passthrough is broken and reports nothing".
//   - ArtificialGlow is the term that makes the other two legible: it is what we subtract, so a
//     scenario asserting `sky == ground - artificial` is checking the actual arithmetic rather than a
//     single value that happens to look reasonable.
//
// The lamp case is the one that needs all three. A sealed, lamp-lit room must keep SkyFraction at 0 —
// otherwise a windowless workshop starts taking the sky's colour, going pink at dawn — while
// GroundGlow stays high, because the lamp must obviously still light the room. Those two facts
// together are the assertion; either on its own is satisfied by a bug.
public sealed class IndoorGlowCellProbe : IProbe
{
    public enum Metric
    {
        // Verse.GlowGrid.GroundGlowAt(cell) — the gameplay value, after every mod in the load order
        // has had its say. This is what the game itself considers the cell's brightness, and what a
        // lamp raises.
        GroundGlow,

        // Vanilla's artificial-only share, recomputed by IndoorOcclusionMath.ArtificialGlow from the
        // raw accumulated glow colour. This is the quantity subtracted out, and it is deliberately
        // read the same way IndoorGlowPassthrough reads it rather than re-derived differently here —
        // a probe that computed it its own way could agree with the formula while disagreeing with
        // the code under test.
        ArtificialGlow,

        // IndoorGlowPassthrough.SkyFractionAt(cell) — the sky-sourced remainder that actually caps
        // §7b's occlusion. 0 on any unmodded install, and 0 whenever the lamps dominate.
        SkyFraction,
    }

    private readonly IntVec3 cellOffset;
    private readonly Metric metric;

    public string Name { get; }

    // cellOffset is relative to map.Center, matching how the scene-setup steps address cells.
    public IndoorGlowCellProbe(string name, IntVec3 cellOffset, Metric metric)
    {
        Name = name;
        this.cellOffset = cellOffset;
        this.metric = metric;
    }

    public float Read(Map map)
    {
        IntVec3 cell = map.Center + cellOffset;
        if (!cell.InBounds(map))
            return -1f;

        switch (metric)
        {
            case Metric.GroundGlow:
                return map.glowGrid.GroundGlowAt(cell);
            case Metric.ArtificialGlow:
                Color32 accumulated = map.glowGrid.VisualGlowAt(cell);
                return IndoorOcclusionMath.ArtificialGlow(
                    accumulated.r, accumulated.g, accumulated.b, accumulated.a);
            case Metric.SkyFraction:
                return IndoorGlowPassthrough.SkyFractionAt(map, cell);
            default:
                throw new ArgumentOutOfRangeException(nameof(metric), metric, null);
        }
    }
}
