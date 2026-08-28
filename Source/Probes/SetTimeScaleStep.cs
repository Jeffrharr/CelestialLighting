using System.Collections.Generic;
using System.Globalization;
using RimWorldTestHarness.Mod;
using RimWorldTestHarness.Mod.Steps;
using RimWorldTestHarness.Shared;
using RimWorldTestHarness.Shared.Steps;
using UnityEngine;

namespace CelestialLighting.Probes;

// A scenario step that scales how fast the game ticks relative to WALL-CLOCK time, so a short
// animation can be filmed as a continuous take instead of sampled.
//
// WHY THIS EXISTS. The harness captures by taking a screenshot on a rendered frame, and encoding a
// 1920x1080 PNG makes that frame long. Verse.TickManager.TickManagerUpdate accumulates
// `Time.deltaTime` and ticks the game through it, so a long frame is a frame in which a LOT of game
// time passes -- measured here at roughly twenty to fifty ticks per capture. A door's slide is 45
// ticks (100 for stone), so the entire animation falls between two consecutive captures and the clip
// shows a door that teleports. No playback rate fixes that: the intermediate positions were never
// rendered.
//
// Unity computes `Time.deltaTime` as `min(unscaledDeltaTime, maximumDeltaTime) * timeScale`, and
// TickManager reads the SCALED value. So dropping timeScale drops the ticks-per-frame in exact
// proportion, without touching the frame rate, the render path, or anything the mod under test does.
// At 0.05 a rendered frame advances at most one tick however long it takes to write, which turns the
// same Wait/Screenshot loop into a slow-motion camera: every frame is a real render of a real
// intermediate state, one tick apart, and the clip is a continuous take that can be played back at
// any speed including the true one.
//
// WHY NOT RimWorld's own TimeSpeed. Its slowest non-paused setting is Normal, which IS 60 ticks a
// second; there is nothing below it. TimeSpeed picks how many ticks to run per unit of game time,
// this picks how fast that time passes, and only the second one can be less than 1.
//
// WHY NOT SAMPLE THE ANIMATION INSTEAD, which is what this replaced. Stopping the door at a known
// phase, freezing, and shooting does produce evenly spaced frames of the door -- but a scene is not
// only the thing being filmed. Every OTHER animation is then sampled at whatever phase its own pass
// happened to leave it in, so a torch flame strobes between unrelated frames instead of flickering.
// A continuous take has no such problem because there is only one take.
//
// THE RESIDUE DECLARATION IS DELIBERATELY IMPERFECT AND THIS COMMENT IS THE MITIGATION.
// `Time.timeScale` is process-global Unity state, not game state: no save reload restores it, and
// Mod/WorldStateReset does not know it exists, so NOTHING in the harness's isolation machinery puts
// it back. TimeSpeed is the nearest honest category -- this is a change to how fast time passes --
// but a soft reset that claims to undo TimeSpeed will not undo this. A scenario that uses this step
// is therefore responsible for setting the scale back to 1 itself, and
// Tools/ScenarioGen/gen_vector_light_door_film.py always emits that reset as its last step.
public sealed class SetTimeScaleStepSpec : IStepSpec
{
    public string Type => "SetTimeScale";

    public ScenarioResidue Residue => ScenarioResidue.TimeSpeed;

    // Never callable against a real colony: it would put someone's game into slow motion with no
    // in-game control that puts it back.
    public bool LiveCallable => false;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string error)
    {
        if (!args.ContainsKey("scale"))
        {
            error = "SetTimeScale needs a 'scale' argument, e.g. scale=\"0.05\".";
            return false;
        }

        if (!TryParseScale(args["scale"], out float scale))
        {
            error = $"SetTimeScale could not parse scale '{args["scale"]}' — expected a number.";
            return false;
        }

        // Zero would be a pause that SetTimeSpeed already expresses and that nothing here would ever
        // undo; above 1 is a fast-forward, which FastForward expresses and which would make a
        // capture WORSE by putting more ticks in every frame. Bounded at load so a typo'd exponent
        // fails alongside the rest of the scenario's typos rather than two minutes into a boot.
        if (scale <= 0f || scale > 1f)
        {
            error = $"SetTimeScale scale must be in (0, 1] (got {args["scale"]}). " +
                    "Use SetTimeSpeed paused for a stop and FastForward to go faster.";
            return false;
        }

        error = null;
        return true;
    }

    // InvariantCulture explicitly: scenario JSON is written with a decimal point, and a machine whose
    // locale wants a comma would otherwise read "0.05" as 5 and film at five times normal speed --
    // which is a plausible-looking clip of a door that snaps, i.e. exactly the failure this step
    // exists to remove.
    internal static bool TryParseScale(string raw, out float scale) =>
        float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out scale);
}

public sealed class SetTimeScaleStepAction : IStepAction
{
    public string Type => "SetTimeScale";

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        if (!SetTimeScaleStepSpec.TryParseScale(args["scale"], out float scale))
        {
            return StepOutcome.Fail($"SetTimeScale could not parse scale '{args["scale"]}'.");
        }

        Time.timeScale = scale;

        // One settle frame, because the step's whole effect is on the NEXT frame's deltaTime. Without
        // it the first captured frame after the change is still a full-speed frame, which on the
        // opening of a slide is the one frame the clip can least afford to lose.
        return new StepOutcome { WaitFrames = 1 };
    }
}
