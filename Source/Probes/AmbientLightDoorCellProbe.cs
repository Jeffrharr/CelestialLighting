using System;
using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// Issue #80: the one cell the whole investigation was about — an interior cell
// one step in from the south-wall doorway gap in Tests/Scenarios/ambient_light_compat.json's 11x11
// roofed room (offset "0,45", gap at local x=0,z=-5). GroundGlow reads the GAMEPLAY value Ambient
// Light's own mouseover readout reports (Verse.GlowGrid.GroundGlowAt) — independent of whether §7b's
// occlusion painting lets any of it reach the screen. SkyFraction reads what IndoorGlowPassthrough
// derives for the SAME cell, which is the number §7b's CapOcclusion actually consumes. Pairing them
// is what lets a scenario tell "Ambient Light's own boost isn't real here" apart from "it's real but
// our compat failed to bind or mis-read it" — two failure modes that would otherwise both show up as
// nothing but a black screenshot.
public sealed class AmbientLightDoorCellProbe : IProbe
{
    public enum Metric
    {
        // Verse.GlowGrid.GroundGlowAt(cell) — gameplay light, Max()'d against ordinary artificial
        // light by the time it is read. Nonzero here with no lamp in the room is Ambient Light's own
        // redistribution at work, exactly what its "Overlay light: X%" tooltip reports.
        GroundGlow,

        // IndoorGlowPassthrough.SkyFractionAt(map, cell) — the SKY-ONLY term §7b's CapOcclusion
        // actually caps occlusion with: total ground glow minus the artificial share, so it can never
        // be confused with a lamp (see IndoorOcclusionMath.IndoorSkyGlowFraction for why that
        // separation matters, and why it is mod-agnostic rather than bound to Ambient Light).
        SkyFraction,
    }

    // One cell north of the doorway gap: local (0, -4) off the room's offset (0, 45), i.e. map centre
    // + (0, 41) — matches Tests/Scenarios/ambient_light_compat.json exactly. Not derived from the
    // scenario at runtime (the harness hands probes a Map, not the scene plan that built it), so a
    // scenario edit that moves the room must move this constant too — the alternative, resolving
    // "the interior cell nearest a door" generically, would have to reimplement the classification
    // Patch_IndoorSkyOcclusion already owns just to find a cell a fixed scenario placed on purpose.
    private static readonly IntVec3 RoomOffset = new IntVec3(0, 0, 45);
    private static readonly IntVec3 NearDoorLocalOffset = new IntVec3(0, 0, -4);

    private readonly Metric metric;

    public string Name { get; }

    public AmbientLightDoorCellProbe(string name, Metric metric)
    {
        Name = name;
        this.metric = metric;
    }

    public float Read(Map map)
    {
        IntVec3 cell = map.Center + RoomOffset + NearDoorLocalOffset;

        switch (metric)
        {
            case Metric.GroundGlow:
                return map.glowGrid.GroundGlowAt(cell);
            case Metric.SkyFraction:
                return IndoorGlowPassthrough.SkyFractionAt(map, cell);
            default:
                throw new ArgumentOutOfRangeException(nameof(metric), metric, null);
        }
    }
}
