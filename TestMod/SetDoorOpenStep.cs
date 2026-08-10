using System.Collections.Generic;
using System.Globalization;
using RimWorld;
using RimWorldTestHarness.Mod;
using RimWorldTestHarness.Mod.Steps;
using RimWorldTestHarness.Shared;
using RimWorldTestHarness.Shared.Steps;
using Verse;

namespace CelestialLighting.Probes;

// Live A/B for §7b's isOpen branch (IndoorOcclusionMath.DoorSkyLeakFor) — see DESIGN.md and
// Patch_DoorOpenSkyLeak.cs's header. Nothing in the harness's own step vocabulary can put a door
// into the OPEN state: PlaceThings spawns it closed and there is no pawn AI running against a
// paused scenario clock to walk one through it. StepDiscovery finds both halves of this pair by
// reflection over every loaded mod assembly (see its own header), so defining a scenario-only step
// here needs no change to the harness itself — the same route AnimalAddonTestBridge's
// PrepAnimalAddonSubjects step already uses for its own mod-specific setup.
//
// Split into spec (this class, harness-vocabulary-only) and action (below, touches Verse) purely to
// match the harness's own IStepSpec/IStepAction convention; both live in this one assembly, same as
// PrepAnimalAddonSubjectsStep/Action do in TestBridge, since this mod (unlike the harness itself)
// has no reason to keep a Verse-free half.
public sealed class SetDoorOpenStep : IStepSpec
{
    public const string StepType = "SetDoorOpen";

    // Map-centre-relative, same grammar as PlaceThings' own cell offsets — a door placed at offset
    // "12,-23" is targeted the same way here.
    public const string OffsetArg = "offset";

    public string Type => StepType;

    // Flips Building_Door.Open and dirties the map mesh, the same shape of edit PlaceThings makes.
    public ScenarioResidue Residue => ScenarioResidue.Map;

    // Forces a door open outright with no pawn or power check — wrong to ever run against a real
    // colony's own doors over the live companion channel.
    public bool LiveCallable => false;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error)
    {
        if (!args.TryGetValue(OffsetArg, out string? raw))
        {
            error = $"'{OffsetArg}' is required (e.g. \"12,-23\")";
            return false;
        }

        if (!TryParseOffset(raw, out _, out _))
        {
            error = $"'{OffsetArg}' must be \"dx,dz\" (got '{raw}')";
            return false;
        }

        error = null;
        return true;
    }

    internal static bool TryParseOffset(string raw, out int dx, out int dz)
    {
        dx = 0;
        dz = 0;
        string[] parts = raw.Split(',');
        if (parts.Length != 2)
            return false;

        return int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out dx) &&
               int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out dz);
    }
}

public sealed class SetDoorOpenAction : IStepAction
{
    public string Type => SetDoorOpenStep.StepType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        string raw = args[SetDoorOpenStep.OffsetArg];
        if (!SetDoorOpenStep.TryParseOffset(raw, out int dx, out int dz))
            return StepOutcome.Fail($"SetDoorOpen: '{SetDoorOpenStep.OffsetArg}' must be \"dx,dz\" (got '{raw}')");

        IntVec3 cell = ctx.Map.Center + new IntVec3(dx, 0, dz);
        Building_Door door = cell.GetFirstBuilding(ctx.Map) as Building_Door;
        if (door == null)
            return StepOutcome.Fail($"SetDoorOpen: no Building_Door at offset {raw} (cell {cell})");

        // Ignores its own `opener` parameter (confirmed by decompile — DoorOpen() takes no pawn
        // argument) and calls the same DoorOpen() a pawn's job driver would, which is what fires
        // Verse.MapEvents.Notify_DoorOpened and, through it, Patch_DoorOpenedSkyLeak's mesh redraw.
        // A null opener is therefore exactly as real a transition as a colonist's, not a shortcut
        // around one.
        door.StartManualOpenBy(null);

        return new StepOutcome { WaitFrames = StepHelpers.SceneSettleFrames };
    }
}
