using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using RimWorldTestHarness.Mod;
using RimWorldTestHarness.Mod.Steps;
using RimWorldTestHarness.Shared;
using RimWorldTestHarness.Shared.Steps;
using Verse;

namespace CelestialLighting.Probes;

// A scenario step that opens or shuts a door on demand, so §27e's open-door behaviour can be shot
// deterministically instead of by spawning a pawn and hoping it walks through on the right tick.
//
// WHY THIS LIVES HERE AND NOT IN THE HARNESS. It is a step only this mod needs, and StepDiscovery
// scans every loaded mod assembly precisely so a third-party step works by existing. It is compiled
// into CelestialLighting.Probes (the dev-only bridge), never into the shipped DLL — same
// <Compile Remove> that keeps the probes out.
//
// WHY IT DRIVES VANILLA'S OWN METHODS BY REFLECTION rather than setting `openInt` directly. The
// whole point of the feature under test is a notification: Building_Door.DoorOpen is what raises
// Map.events.Notify_DoorOpened, which is what Patch_VectorLightDoorOpened hooks. Poking the backing
// field would leave the door looking open while nothing told §27 to rebake, which is exactly the bug
// this step exists to prove is absent — the test would then pass by reproducing the failure.
// Reflection is the cost of DoorOpen/DoorTryClose being protected; it is confined to this dev-only
// file rather than leaking a public shim into the shipped mod.
public sealed class SetDoorOpenStepSpec : IStepSpec
{
    public string Type => "SetDoorOpen";

    // Opening a door mutates the map, so a suite must reload the fixture before the next scenario
    // rather than inheriting a door this one left standing open. Map residue is the honest answer
    // even though the change is one cell: the whole point of the step is that §27's geometry reacts
    // to it, so a later scenario inheriting it would measure a shape nobody asked for.
    public ScenarioResidue Residue => ScenarioResidue.Map;

    // Never callable against a real colony. It drives a door on someone's base and holds it open.
    public bool LiveCallable => false;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string error)
    {
        if (!args.ContainsKey("offset"))
        {
            error = "SetDoorOpen needs an 'offset' argument, e.g. offset=\"0,45\".";
            return false;
        }

        if (!TryParseCell(args["offset"], out _))
        {
            error = $"SetDoorOpen could not parse offset '{args["offset"]}' — expected \"x,z\".";
            return false;
        }

        error = null;
        return true;
    }

    // Parsed here rather than in the action so an unparseable offset fails at load, alongside every
    // other scenario typo, instead of two minutes into a boot.
    //
    // OFFSET FROM MAP CENTRE, not an absolute cell — the same convention PlaceThings and LookAt use,
    // and the same one the §27e probes use. A step that took absolute cells while the fixture around
    // it was placed relative to centre would silently address open ground, find no door there, and
    // fail with "no Building_Door" pointing at the door that is plainly right there in the frame.
    internal static bool TryParseCell(string raw, out IntVec3 cell)
    {
        cell = IntVec3.Invalid;
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        string[] parts = raw.Split(',');
        if (parts.Length != 2
            || !int.TryParse(parts[0].Trim(), out int x)
            || !int.TryParse(parts[1].Trim(), out int z))
        {
            return false;
        }

        cell = new IntVec3(x, 0, z);
        return true;
    }
}

public sealed class SetDoorOpenStepAction : IStepAction
{
    public string Type => "SetDoorOpen";

    private static readonly MethodInfo DoorOpenMethod = typeof(Building_Door).GetMethod(
        "DoorOpen", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly MethodInfo DoorTryCloseMethod = typeof(Building_Door).GetMethod(
        "DoorTryClose", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo HoldOpenField = typeof(Building_Door).GetField(
        "holdOpenInt", BindingFlags.Instance | BindingFlags.NonPublic);

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        if (!SetDoorOpenStepSpec.TryParseCell(args["offset"], out IntVec3 offset))
        {
            return StepOutcome.Fail($"SetDoorOpen could not parse offset '{args["offset"]}'.");
        }

        Map map = ctx.Map;
        if (map == null)
        {
            return StepOutcome.Fail("SetDoorOpen ran with no map loaded.");
        }

        IntVec3 cell = map.Center + offset;
        if (!cell.InBounds(map))
        {
            return StepOutcome.Fail($"SetDoorOpen offset {offset} resolves to {cell}, off the map.");
        }

        Building_Door door = cell.GetEdifice(map) as Building_Door;
        if (door == null)
        {
            return StepOutcome.Fail(
                $"SetDoorOpen found no Building_Door at {cell} — the edifice there is " +
                $"{cell.GetEdifice(map)?.def?.defName ?? "nothing"}.");
        }

        bool open = !args.ContainsKey("open")
            || args["open"].Equals("true", System.StringComparison.OrdinalIgnoreCase);

        if (DoorOpenMethod == null || DoorTryCloseMethod == null || HoldOpenField == null)
        {
            return StepOutcome.Fail(
                "SetDoorOpen could not resolve Building_Door.DoorOpen/DoorTryClose/holdOpenInt — " +
                "a RimWorld update renamed them.");
        }

        // Hold-open is set BEFORE opening and cleared BEFORE closing, because it is what
        // Building_Door.Tick consults to decide whether to shut again. A scenario that jumps the
        // clock or profiles a window would otherwise find the door closed itself partway through the
        // captures, and the frames would disagree with the probes for no visible reason.
        if (open)
        {
            HoldOpenField.SetValue(door, true);
            DoorOpenMethod.Invoke(door, new object[] { 110 });
            return new StepOutcome();
        }

        HoldOpenField.SetValue(door, false);
        bool closed = (bool)DoorTryCloseMethod.Invoke(door, new object[0]);
        return closed
            ? new StepOutcome()
            : StepOutcome.Fail($"door {door.def.defName} at {cell} refused to close (blocked?)");
    }
}
