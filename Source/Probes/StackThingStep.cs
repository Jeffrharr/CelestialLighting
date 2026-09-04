using System;
using System.Collections.Generic;
using RimWorldTestHarness.Mod;
using RimWorldTestHarness.Mod.Steps;
using RimWorldTestHarness.Shared;
using RimWorldTestHarness.Shared.Steps;
using Verse;

namespace CelestialLighting.Probes;

// A scenario step that spawns one building INTO a cell another building already occupies, in that
// order, the way a colonist finishing a blueprint does.
//
// WHY PlaceThings CANNOT DO THIS, and why the difference is the whole point. The harness's own
// PlaceThings guards every spawn with GenSpawn.CanSpawnAt, which refuses any cell that is not
// `c.Walkable(map)` -- and a wall cell never is. That guard is right for scene building (it is what
// turns "a wall asked to stand in deep water" into a named failure instead of a silent absence), and
// it makes the arrangement this step exists for unreachable: a building standing on a wall. The
// construction path does not go through CanSpawnAt at all, so the game reaches it every time a player
// builds one of Replace Stuff's over-wall coolers or vents.
//
// ORDER IS THE MEASUREMENT HERE. Verse.EdificeGrid.Register just writes the cell's array slot, so
// when two edifices share a cell the LAST one to spawn owns it, and Replace Stuff's over-wall vent is
// an edifice (its 1.6 def drops the isEdifice=false its pre-1.6 one carried). Building the vent onto
// an existing wall therefore evicts that wall from the edifice grid while it goes on standing there.
// A scenario that stacks them the other way round -- vent first, wall second, which PlaceThings CAN
// express -- leaves the wall owning the slot and reads perfectly clean, which is exactly the wrong
// answer arrived at convincingly. overwall_vent_sky_falloff.json measured both.
//
// Like SetDoorOpenStep, this lives in the probe bridge rather than in the harness: StepDiscovery
// scans every loaded mod assembly so a third-party step works by existing, and it is compiled out of
// the shipped DLL by the same <Compile Remove> that keeps the probes out.
public sealed class StackThingStepSpec : IStepSpec
{
    public string Type => "StackThing";

    // Spawns a building. A suite must reload the fixture before the next scenario rather than
    // inherit it.
    public ScenarioResidue Residue => ScenarioResidue.Map;

    // Never callable against a real colony: it builds things in someone's base, and it deliberately
    // skips the check that says whether they can go there.
    public bool LiveCallable => false;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string error)
    {
        if (!args.ContainsKey("def"))
        {
            error = "StackThing needs a 'def' argument, e.g. def=\"Vent_Over\".";
            return false;
        }

        if (!args.ContainsKey("offset"))
        {
            error = "StackThing needs an 'offset' argument, e.g. offset=\"0,100\".";
            return false;
        }

        if (!SetDoorOpenStepSpec.TryParseCell(args["offset"], out _))
        {
            error = $"StackThing could not parse offset '{args["offset"]}' — expected \"x,z\".";
            return false;
        }

        error = null;
        return true;
    }
}

public sealed class StackThingStepAction : IStepAction
{
    public string Type => "StackThing";

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        Map map = ctx.Map;
        if (map == null)
        {
            return StepOutcome.Fail("StackThing ran with no map loaded.");
        }

        if (!SetDoorOpenStepSpec.TryParseCell(args["offset"], out IntVec3 offset))
        {
            return StepOutcome.Fail($"StackThing could not parse offset '{args["offset"]}'.");
        }

        IntVec3 cell = map.Center + offset;
        if (!cell.InBounds(map))
        {
            return StepOutcome.Fail($"StackThing offset {offset} resolves to {cell}, off the map.");
        }

        // errorOnFail: false — an unknown def is a scenario typo and belongs in the report, not in a
        // Log.Error nobody reads. Naming the def that was asked for is what ends the investigation,
        // since the usual cause is the mod that defines it not being in requiredMods.
        ThingDef def = DefDatabase<ThingDef>.GetNamed(args["def"], errorOnFail: false);
        if (def == null)
        {
            return StepOutcome.Fail(
                $"StackThing found no ThingDef '{args["def"]}' — is the mod that defines it in requiredMods?");
        }

        ThingDef stuff = null;
        if (args.ContainsKey("stuff"))
        {
            stuff = DefDatabase<ThingDef>.GetNamed(args["stuff"], errorOnFail: false);
            if (stuff == null)
            {
                return StepOutcome.Fail($"StackThing found no stuff ThingDef '{args["stuff"]}'.");
            }
        }

        // WipeMode.Vanish is vanilla's own default, and it is deliberately NOT weakened here: what
        // must survive the spawn is whatever GenSpawn.SpawningWipes says survives it, mod patches
        // included. Replace Stuff's own postfix on that method is precisely what lets its over-wall
        // vent land on a wall without destroying it, so a step that suppressed wiping would be
        // testing the harness's opinion rather than the mod's.
        Thing thing = ThingMaker.MakeThing(def, stuff);
        Thing spawned = GenSpawn.Spawn(thing, cell, map, Rot4.North, WipeMode.Vanish);
        if (spawned == null)
        {
            return StepOutcome.Fail($"StackThing: spawn of {def.defName} at {cell} was refused.");
        }

        // The cell's occupants go to the log rather than the report, because "it stacked" and "it
        // replaced what was there" are the two outcomes this step exists to tell apart and they look
        // identical in a screenshot of a cell whose top building draws over the other. StepOutcome
        // carries no free-text field, and the scenario pins CellBlockerProbe on the same cell anyway
        // -- this is the line that says which building ended up owning the edifice slot when a pin
        // fails and the answer is needed before the next three-minute boot.
        var occupants = new List<string>();
        List<Thing> things = map.thingGrid.ThingsListAtFast(cell);
        for (int i = 0; i < things.Count; i++)
        {
            if (things[i] is Building building)
            {
                occupants.Add(building.def.defName);
            }
        }

        Log.Message(
            $"[CelestialLighting] StackThing: {def.defName} at {cell}; buildings now: " +
            $"{string.Join(", ", occupants.ToArray())}; edifice: " +
            $"{map.edificeGrid[cell]?.def?.defName ?? "none"}");

        return new StepOutcome();
    }
}
