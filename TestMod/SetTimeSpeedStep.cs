using System;
using System.Collections.Generic;
using RimWorldTestHarness.Mod;
using RimWorldTestHarness.Mod.Steps;
using RimWorldTestHarness.Shared;
using RimWorldTestHarness.Shared.Steps;
using Verse;

namespace CelestialLighting.Probes;

// A scenario step that sets the game speed, and in particular that PAUSES it.
//
// WHY THIS LIVES HERE RATHER THAN IN THE HARNESS. The harness discovers steps by reflecting over
// every loaded mod assembly, precisely so a third-party step works by existing — see its
// StepDiscovery header. This is the first one we have needed, and it is needed because holding a
// scene still is not something the harness can otherwise express.
//
// WHY IT IS NEEDED AT ALL. Anything measuring a pawn has to stop the pawn moving. A colonist with no
// orders wanders, and a scenario's arms are tens of frames apart, so an A/B across them compares two
// different scenes: vector_light_shadow_ground_shares lost three runs to a colonist who walked to a
// torch between captures, changing the shadow LENGTH — a quantity that change could not touch — and
// every alpha with it. The numbers came back plausible, in the predicted direction, and wrong.
//
// WHY NOT THE `Profile` STEP, which can already do this. It sets `timeSpeed` and deliberately holds
// it rather than restoring it, so a one-frame paused window does pause the rest of the scenario —
// and that is exactly what this scenario tried first. It is a trap in two ways. It needs `name` and
// `prefix` or it is rejected at load, and a rejected step does not stop the run: the scenario carries
// on unpaused and reports pins that look fine. And under `--no-profiler` the step SKIPS before it
// ever reaches the speed, which abandons the whole scenario — reported as a PASS, with every probe
// after it silently unrun. A pause should not require a profiler to be installed.
public sealed class SetTimeSpeedStep : IStepSpec
{
    public const string TypeName = "SetTimeSpeed";

    public string Type => TypeName;

    // Exactly what FastForward and Profile declare, and for the same reason their comments give: the
    // speed is left where it was put, so a following scenario in the same load would inherit it.
    public ScenarioResidue Residue => ScenarioResidue.TimeSpeed;

    // Pausing someone's real colony through the live companion channel is a visible, unasked-for
    // intervention in a game they are playing. Scenario runs load a throwaway fixture; that is where
    // this belongs.
    public bool LiveCallable => false;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string error)
    {
        return TryReadSpeed(args, out _, out error);
    }

    // Shared/ArgReader is internal to the harness, so the parse is spelled out here. Kept in the spec
    // and reused by the action so the two cannot disagree about what a valid value is — the load-time
    // check passing and the execution then failing is the confusing half-failure IStepAction's header
    // warns about.
    internal static bool TryReadSpeed(
        IReadOnlyDictionary<string, string> args, out TimeSpeed speed, out string error)
    {
        speed = TimeSpeed.Normal;

        if (!args.TryGetValue("speed", out string raw) || string.IsNullOrWhiteSpace(raw))
        {
            error = "'speed' is required — one of paused, normal, fast, superfast, ultrafast";
            return false;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "paused": speed = TimeSpeed.Paused; break;
            case "normal": speed = TimeSpeed.Normal; break;
            case "fast": speed = TimeSpeed.Fast; break;
            case "superfast": speed = TimeSpeed.Superfast; break;
            case "ultrafast": speed = TimeSpeed.Ultrafast; break;
            default:
                error = $"unknown 'speed' value '{raw}' — expected one of " +
                        "paused, normal, fast, superfast, ultrafast";
                return false;
        }

        error = null;
        return true;
    }
}

// The game-touching half. Deliberately trivial: everything that can be got wrong offline is in the
// spec above, which is the split the two interfaces exist to make.
public sealed class SetTimeSpeedAction : IStepAction
{
    public string Type => SetTimeSpeedStep.TypeName;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        if (!SetTimeSpeedStep.TryReadSpeed(args, out TimeSpeed speed, out string error))
            return StepOutcome.Fail(error);

        if (Find.TickManager == null)
            return StepOutcome.Fail("no TickManager — SetTimeSpeed needs a game in progress");

        Find.TickManager.CurTimeSpeed = speed;
        return new StepOutcome();
    }
}
