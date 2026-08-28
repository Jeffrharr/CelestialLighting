using System.Collections.Generic;
using RimWorldTestHarness.Mod;
using RimWorldTestHarness.Mod.Steps;
using RimWorldTestHarness.Shared;
using RimWorldTestHarness.Shared.Steps;
using Verse;

namespace CelestialLighting.Probes;

// Opens a sampling window for VectorLightSwingSampler over a box of cells, and closes whatever
// window was open. Compiled into CelestialLighting.Probes (the dev-only bridge), never into the
// shipped DLL — same <Compile Remove> that keeps the probes out.
//
// WHY A STEP AND NOT A SetFeature TOGGLE, which is how the other resettable probes here are armed.
// Those reset a counter and take no argument; this one has to be told WHERE to look, and the answer
// is a scene coordinate that belongs in the scenario next to the room it describes rather than baked
// into the probe registration where a scenario editing its own layout would not think to look.
//
// ARMING IS ALSO THE RESET, which is what makes two arms in one boot honest: without it the second
// arm inherits the first's minimum and reports the first arm's defect as its own.
public sealed class ArmSwingSampleStepSpec : IStepSpec
{
    public string Type => "ArmSwingSample";

    // Reads meshes and writes nothing on the map. The window it opens is torn down by the next arm,
    // and a stale window costs a few mesh reads per frame rather than any state a later scenario
    // could inherit — so no residue.
    public ScenarioResidue Residue => ScenarioResidue.None;

    // Harmless against a live colony: it observes. Left callable so the live channel can watch a
    // real base's doorway while somebody walks through it, which is how #218 was found in the first
    // place.
    public bool LiveCallable => true;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string error)
    {
        if (!args.ContainsKey("offset"))
        {
            error = "ArmSwingSample needs an 'offset' argument, e.g. offset=\"0,45\".";
            return false;
        }

        if (!SetDoorOpenStepSpec.TryParseCell(args["offset"], out _))
        {
            error = $"ArmSwingSample could not parse offset '{args["offset"]}' — expected \"x,z\".";
            return false;
        }

        if (args.ContainsKey("radius") && !int.TryParse(args["radius"], out int radius))
        {
            error = $"ArmSwingSample could not parse radius '{args["radius"]}' — expected an integer.";
            return false;
        }
        else if (args.ContainsKey("radius") && int.Parse(args["radius"]) < 0)
        {
            error = $"ArmSwingSample radius '{args["radius"]}' must not be negative.";
            return false;
        }

        error = null;
        return true;
    }
}

public sealed class ArmSwingSampleStepAction : IStepAction
{
    public string Type => "ArmSwingSample";

    // Wide enough to hold a doorway and the cells either side of it without a scenario having to
    // guess which one overshoots, small enough that the per-frame mesh read stays a rounding error
    // against the frame it is measuring.
    private const int DefaultRadius = 4;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        if (!SetDoorOpenStepSpec.TryParseCell(args["offset"], out IntVec3 offset))
        {
            return StepOutcome.Fail($"ArmSwingSample could not parse offset '{args["offset"]}'.");
        }

        int radius = args.ContainsKey("radius") ? int.Parse(args["radius"]) : DefaultRadius;

        Map map = ctx.Map;

        if (map == null)
        {
            return StepOutcome.Fail("ArmSwingSample ran with no map loaded.");
        }

        // Checked here rather than left to the sampler, because a box hanging off the map edge would
        // reject every out-of-bounds cell silently and hand the scenario a reading assembled from
        // whichever corner happened to be over ground.
        IntVec3 centre = map.Center + offset;

        if (!centre.InBounds(map))
        {
            return StepOutcome.Fail(
                $"ArmSwingSample offset {offset} resolves to {centre}, off the map.");
        }

        VectorLightSwingSampler.Arm(offset, radius);

        return new StepOutcome();
    }
}
