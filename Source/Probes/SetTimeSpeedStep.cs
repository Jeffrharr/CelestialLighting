using System;
using System.Collections.Generic;
using RimWorldTestHarness.Mod;
using RimWorldTestHarness.Mod.Steps;
using RimWorldTestHarness.Shared;
using RimWorldTestHarness.Shared.Steps;
using Verse;

namespace CelestialLighting.Probes;

// Lets a scenario run the colony instead of holding it still.
//
// WHY THIS IS NEEDED AND WHY TickLapse IS NOT THE ANSWER. Scenarios are paused throughout so a
// step's effects are read at a known tick, and TickLapse's AdvanceTicks is a JUMP — it moves
// TicksGame without simulating, which is exactly right for an effect that reads the tick counter and
// exactly wrong for one driven by a Thing's own Tick(). A door's slide is the second kind:
// Building_Door.Tick increments ticksSinceOpen, and OpenPct is a ratio of it. Under TickLapse that
// counter never moves, so the first cut of §27e's film came back with the aperture pinned at 0 for
// all thirty frames while the probes still read a fully-open doorway — a film of nothing, that
// passed.
//
// FastForward does live through its ticks, but it raises the game to Superfast and never pauses
// again, so a 45-tick door swing is over in a handful of rendered frames. Normal speed plus Wait
// frames is the only combination that films a sub-second animation at the speed a player sees it.
//
// Dev-only, like every other file in this folder: it is compiled into CelestialLighting.Probes and
// never into the shipped DLL.
public sealed class SetTimeSpeedStepSpec : IStepSpec
{
    public string Type => "SetTimeSpeed";

    // Leaves the clock running, which is precisely what the next scenario in a suite must not
    // inherit — it would live through its own setup steps.
    public ScenarioResidue Residue => ScenarioResidue.TimeSpeed;

    // Never against a real colony: unpausing someone's game from a test harness is not minimally
    // invasive, whatever it was about to do next.
    public bool LiveCallable => false;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string error)
    {
        if (!args.ContainsKey("speed"))
        {
            error = "SetTimeSpeed needs a 'speed' argument: paused | normal | fast | superfast | ultrafast.";
            return false;
        }

        if (!TryParse(args["speed"], out _))
        {
            error = $"SetTimeSpeed did not recognise speed '{args["speed"]}' — "
                  + "expected paused | normal | fast | superfast | ultrafast.";
            return false;
        }

        error = null;
        return true;
    }

    // Same vocabulary as the Profile step's timeSpeed arg, deliberately: two spellings for one
    // concept in the same scenario file is how an author ends up filming at a speed they did not ask
    // for.
    internal static bool TryParse(string raw, out TimeSpeed speed)
    {
        switch ((raw ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "paused": speed = TimeSpeed.Paused; return true;
            case "normal": speed = TimeSpeed.Normal; return true;
            case "fast": speed = TimeSpeed.Fast; return true;
            case "superfast": speed = TimeSpeed.Superfast; return true;
            case "ultrafast": speed = TimeSpeed.Ultrafast; return true;
            default: speed = TimeSpeed.Paused; return false;
        }
    }
}

public sealed class SetTimeSpeedStepAction : IStepAction
{
    public string Type => "SetTimeSpeed";

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        if (!SetTimeSpeedStepSpec.TryParse(args["speed"], out TimeSpeed speed))
        {
            return StepOutcome.Fail($"SetTimeSpeed did not recognise speed '{args["speed"]}'.");
        }

        if (Find.TickManager == null)
        {
            return StepOutcome.Fail("SetTimeSpeed ran with no TickManager.");
        }

        Find.TickManager.CurTimeSpeed = speed;
        return new StepOutcome();
    }
}
