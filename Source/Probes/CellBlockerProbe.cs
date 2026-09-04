using System;
using System.Collections.Generic;
using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead.
//
// What one cell looks like to the two DIFFERENT blocker tests vanilla itself keeps, so a scenario can
// say which of them a mod's building agrees with:
//
//   Verse.SectionLayer_LightingOverlay and SectionLayer_Darkness ask the EDIFICE GRID
//   (`map.edificeGrid[c]?.def.blockLight`) -- one building per cell, whoever registered last.
//   Verse.Building.SpawnSetup writes `def.blockLight` into GlowGrid's own lightBlockers bit for
//   EVERY building, edifice or not, so vanilla's glow flood answers PER BUILDING IN THE CELL.
//
// The two agree on every vanilla scene, because vanilla never stands two buildings in one cell. Mods
// do -- Replace Stuff's over-wall cooler/vent share a cell with the wall they sit on -- and there the
// answers come apart, silently and in our favour only by luck. This probe reports each answer
// separately at one cell so a scenario can pin the disagreement itself rather than its consequences.
//
// Offset from map centre, matching GlowGridCellProbe: fixtures are built relative to centre.
public sealed class CellBlockerProbe : IProbe
{
    public enum Metric
    {
        // `map.edificeGrid[c]?.def.blockLight ?? false` -- what our own flood used to ask, and what
        // vanilla's two rendering section layers still ask.
        EdificeBlocksLight,

        // Any Building standing in the cell with def.blockLight -- the set vanilla's glow grid
        // actually holds, since every one of them called GlowGrid.LightBlockerAdded on spawn.
        AnyBuildingBlocksLight,

        // How many Buildings stand in the cell. 2 is the whole point of an over-wall vent scenario:
        // it proves the wall really is still there under the vent rather than having been wiped,
        // which is the alternative explanation for any light that gets through.
        BuildingCount,

        // roofGrid.Roofed. A cell that lost its roof is a BFS *seed*, not a blocker, so a leak with
        // this reading 0 has nothing to do with the blocker set at all.
        Roofed,

        // Whether the cell has an edifice AT ALL, which EdificeBlocksLight cannot tell you: a
        // blockLight=false edifice and no edifice both read 0 there. It is the question that decides
        // whether the ORDER two buildings were spawned in could matter -- EdificeGrid.Register just
        // overwrites the slot -- so a scenario that has to stack them in the opposite order from the
        // game can say that the order was irrelevant rather than hoping so.
        EdificePresent,
    }

    private readonly Metric metric;
    private readonly IntVec3 offsetFromCentre;

    public string Name { get; }

    public CellBlockerProbe(string name, Metric metric, IntVec3 offsetFromCentre)
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

        switch (metric)
        {
            case Metric.EdificeBlocksLight:
                return EdificeBlocksLight(map, cell) ? 1f : 0f;
            case Metric.AnyBuildingBlocksLight:
                return AnyBuildingBlocksLight(map, cell) ? 1f : 0f;
            case Metric.BuildingCount:
                return BuildingCount(map, cell);
            case Metric.Roofed:
                return map.roofGrid.Roofed(cell) ? 1f : 0f;
            case Metric.EdificePresent:
                return map.edificeGrid[cell] != null ? 1f : 0f;
            default:
                throw new ArgumentOutOfRangeException(nameof(metric), metric, null);
        }
    }

    private static bool EdificeBlocksLight(Map map, IntVec3 cell)
    {
        Building edifice = map.edificeGrid[cell];
        return edifice != null && edifice.def.blockLight;
    }

    private static bool AnyBuildingBlocksLight(Map map, IntVec3 cell)
    {
        List<Thing> things = map.thingGrid.ThingsListAtFast(cell);
        for (int i = 0; i < things.Count; i++)
        {
            if (things[i] is Building building && building.def.blockLight)
                return true;
        }

        return false;
    }

    private static int BuildingCount(Map map, IntVec3 cell)
    {
        List<Thing> things = map.thingGrid.ThingsListAtFast(cell);
        int count = 0;
        for (int i = 0; i < things.Count; i++)
        {
            if (things[i] is Building)
                count++;
        }

        return count;
    }
}
