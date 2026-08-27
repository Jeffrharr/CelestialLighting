#!/usr/bin/env python3
"""Five hundred coloured, powered lamps on one screen: the emitter-population stress case.

WHAT IS BEING STRESSED, AND WHY IT IS NOT THE SAME AS vector_light_perf. That scenario profiles
twenty torches in twenty identical rooms and is the right shape for asking what soft edges cost. It
cannot say anything about POPULATION, because twenty emitters at one radius in one colour exercise
one entry of every per-emitter cache the subsystem has. This one puts five hundred emitters, in
eleven colours and nine radii, all inside the view frustum at once -- so the material cache, the
gradient cache, the per-emitter glow texture, the roster resync and the draw's own per-emitter loop
are all being asked to hold a real colony's worth of work rather than a fixture's.

WHY EVERY LAMP IS ON SCREEN. VectorLightOverlay culls against the view. Profile at a tight zoom and
the numbers describe the culling, not the composition, while looking exactly like a cheap subsystem.
The camera is therefore at rootSize 60, the game's own zoom-out limit, and the plate is sized to fit
inside it.

WHAT THE CONDUIT IS DOING TO THE MEASUREMENT, stated because it would otherwise be a hidden variable.
The brief asks for the whole test area underlaid with hidden conduit and powered by twenty toxifier
generators, and that is what is built -- 11,868 conduits in one power net. That is a real per-tick
cost and it IS in these frames. It is not a confound, because it is in BOTH arms: the gated arm
carries the identical carpet with vector lighting switched off, so the arm-to-arm difference is
still purely §27. It does mean the absolute frame times here are not comparable to
vector_light_perf's, and the difference between arms is the number to quote.

    python3 Tools/ScenarioGen/gen_stress_light_colony.py
"""

import json
import os

import stress_colony as sc

HERE = os.path.dirname(os.path.abspath(__file__))
SCEN = os.path.abspath(os.path.join(HERE, "..", "..", "Tests", "Scenarios"))
TARGET = os.path.join(SCEN, "stress_light_colony.json")
HOLD_TARGET = os.path.join(SCEN, "stress_light_colony_hold.json")

# Frames per profiling window. Long, and deliberately longer than vector_light_perf's 600: this box
# has measured frame_max_ms spanning 37 to 85 across three consecutive runs of ONE build, and a
# population this size makes every frame slower and the sampling noisier with it. Sample size is the
# only defence.
PROFILE_FRAMES = 900

# Frames to hold after a flag flip before anything is measured. The harness runs one step per frame,
# so an arm that sets ten flags renders nine frames of half-applied state; a cache built lazily
# during those frames then holds the half-applied version for the whole arm.
SETTLE_FRAMES = 30


def arm(name, vector_lights, screenshot=True):
    """One measured arm: state every flag, let it settle, photograph it, then profile it."""
    steps = sc.feature_steps(vector_lights=vector_lights)
    steps.append(sc.step("Wait", frames=SETTLE_FRAMES))

    if screenshot:
        steps.append(sc.step("Screenshot", fileName=f"stress_light_colony_{name}", hideUi="true"))
        # The first capture of a run carries the HUD whatever hideUi says -- it is honoured from the
        # second capture on. Both arms therefore shoot twice and the SECOND frame of each is the one
        # to compare; the first is kept because throwing it away would make the asymmetry invisible
        # to whoever reads the report next.
        steps.append(sc.step("Wait", frames=4))
        steps.append(sc.step("Screenshot", fileName=f"stress_light_colony_{name}_clean", hideUi="true"))

    steps.append(sc.step("Profile", **{
        "name": name,
        "prefix": "CelestialLighting",
        "frames": PROFILE_FRAMES,
        "timeSpeed": "superfast",
    }))
    return steps


def workload_probes():
    """What the population cost, read as counts rather than durations.

    COUNTS, NOT MILLISECONDS, and that is the point of reading them next to the profile table. A
    per-call timer cannot see a hook that starts firing twice as often, and every one of these is a
    quantity that scales with the emitter population -- which is exactly the axis this scenario
    moves. They are read UNPINNED (wide bounds) rather than fixed: they are being established here
    for the first time, and inventing an expected value for a number nobody has measured is how a
    pin ends up being a prediction the code is then bent to match.
    """
    return [
        # Emitters §27 is holding, and the number every other count here has to be read against.
        sc.step("Probe", probeName="vector_light_emitters",
                expectedValue=sc.TOTAL_GLOWERS, tolerance=0),
        # Polygon bakes and the segment population they scanned. A bake count is only interpretable
        # against the wall population it was measured over, which is why the wall count is in the
        # description rather than left to be counted off a screenshot.
        sc.record("vector_light_bakes"),
        sc.record("vector_light_bake_segments"),
        sc.record("vector_light_segments_per_bake"),
        # The invalidation radius, measured. Read against the emitter count above: this says whether
        # "only the lights that can see the cell" holds at five hundred lamps or only at twenty.
        sc.record("vector_light_marks_per_call"),
        sc.record("vector_light_roster_resyncs"),
        # Uploads. The per-emitter glow texture is the one allocation that scales one-to-one with the
        # population, so this is where a five-hundred-lamp colony diverges from a twenty-lamp one.
        sc.record("vector_light_field_texture_uploads"),
        sc.record("vector_light_field_uv_only_uploads"),
        # Mesh size. Vertices are what the draw actually hands Unity every frame.
        sc.record("vector_light_verts"),
        # The defect counter, not a workload figure: sections that baked with an emitter reaching
        # them and no polygon to use, i.e. frames that rendered a shadow short. Pinned at zero,
        # because a stress scenario finding this non-zero has found a real bug and not a cost.
        sc.step("Probe", probeName="vector_light_mask_stale_polys",
                expectedValue=0, tolerance=0),
    ]


def perf_asserts():
    """The headline cost figures, lifted out of the two tables and into the results block.

    THE PAIR TO READ IS 'full' AGAINST 'gated', not either alone. Both arms carry the identical
    9,620-conduit power net, the identical 1,504 walls and the identical 500 lamps; the only
    difference between them is whether §27 is running. So the subsystem's cost at this population is
    full minus gated, and the absolute figures are dominated by things this mod did not put there.

    Measured on the first passing run of this file, and quoted in the scenario description:
    gated 0.474 ms/frame, full 0.773 ms/frame — i.e. roughly 0.30 ms/frame for five hundred
    emitters, or under 2% of a 60 fps budget.

    MAX IS ASSERTED SEPARATELY AND LOOSELY, because it is the number that matters most and the one
    this scenario found something in: the worst frame of the 'full' arm was 64 ms against an average
    of 0.77, all of it in Patch_VectorLightSuppress on 112 calls across 901 frames. An average hides
    that completely. The bound here is deliberately above the observed value rather than below it —
    the point is to notice it getting worse, not to fail the run over a defect that is already
    recorded and is not this scenario's to fix.
    """
    return [
        sc.perf_assert("gated", "avgMsPerFrame", 3.0),
        sc.perf_assert("gated", "maxMsPerFrame", 120.0),
        sc.perf_assert("full", "avgMsPerFrame", 4.0),
        sc.perf_assert("full", "maxMsPerFrame", 250.0),
        # Per-patch, so a regression can be attributed rather than merely noticed. The draw is the
        # steady per-emitter cost (235 us a call at 500 emitters, twice a frame); the suppress pass is
        # the spiky one.
        sc.perf_assert("full", "avgMsPerFrame", 3.0, label="Patch_VectorLightDraw"),
        sc.perf_assert("full", "maxMsPerFrame", 250.0, label="Patch_VectorLightSuppress"),
    ]


def build():
    colony = sc.build()

    steps = sc.setup_steps(colony)
    steps += sc.palette_steps()
    # Establish BEFORE probing the population: the roster the pins read is only maintained while the
    # subsystem is on, so probing ahead of the first arm reads a roster nobody was keeping.
    steps += sc.establish_steps()
    steps += sc.population_probes()

    # Bake counters are reset before anything is read: nothing is baked at map load, the counts start
    # high while the colony builds itself, and a workload probe read without a reset measures the
    # setup rather than the arm. Read through `record` because the reset probe's return value is the
    # act of resetting, not a quantity worth gating -- and the Probe step has no unpinned form.
    steps.append(sc.record("vector_light_bake_reset"))

    # GATED FIRST, and the order is not arbitrary. The gated arm is the control -- it carries the
    # same 11,868 conduits, the same 1,874 walls and the same 500 lamps with §27 switched off -- and
    # putting it first means the expensive arm cannot be the one that warmed the caches for it.
    steps += arm("gated", vector_lights=False)
    steps += arm("full", vector_lights=True)
    steps += workload_probes()
    steps += perf_asserts()

    return {
        "name": "stress_light_colony",
        "saveFile": "minimal_colony.rws",
        "description": (
            "Five hundred lamps on one screen, in eleven colours and nine radii, every powered one of "
            "them fed by a carpet of 11,868 hidden conduits and twenty toxifier generators, standing "
            "in a randomised colony of 28 rooms (19 roofed) and 120 free-standing wall stubs -- 1,874 "
            "wall cells and 35 doors in total. The emitter-population stress case, which "
            "vector_light_perf cannot be: twenty torches at one radius in one colour exercise one "
            "entry of every per-emitter cache the subsystem has, and this exercises five hundred. "
            "\n\n"
            "THE LAYOUT IS RANDOM BUT NOT RANDOMISED. Tools/ScenarioGen/stress_colony.py throws the "
            "dice once, under a fixed seed, and emits explicit cell lists -- so the walls are "
            "irregular in the way a real colony's are, and yet two runs of this file cannot differ by "
            "a single cell. Re-running the generator reproduces it byte for byte. "
            "\n\n"
            "READ THE ARM DIFFERENCE, NOT THE ABSOLUTE FRAME TIME. The conduit carpet is a real "
            "per-tick cost and it is in these frames, as briefed. It is not a confound because it is "
            "in both arms -- 'gated' is the identical colony with vector_lights off -- but it does "
            "mean these millisecond figures are not comparable with vector_light_perf's. The number "
            "to quote is full minus gated. "
            "\n\n"
            "WHAT WOULD SILENTLY EMPTY THIS SCENARIO, and what stops it. An unpowered StandingLamp "
            "does not glow, because CompPowerTrader is an IThingGlower, so a power net that failed to "
            "solve would remove 400 of the 500 emitters while still photographing as a lit colony. "
            "vector_light_count is therefore pinned at 500 before anything is measured. Likewise a "
            "palette that reached the comps but not §27's roster renders as one colour and reads as "
            "success everywhere else, so glow_emitter_colours is pinned at 11 alongside "
            "glow_colour_overrides at 500 -- the pair separates 'the step did nothing' from 'the step "
            "worked and the roster did not hear about it'. glow_emitter_radii pins the count of "
            "distinct material-cache keys, which is what a single-radius fixture reads 1 on; this "
            "repo has already shipped a per-emitter texture overflow that such a fixture could not "
            "have caught. "
            "\n\n"
            "Clouds are off explicitly. The cloud sheet drifts on the tick counter, this scenario "
            "runs the clock on purpose, and the drifting shade lands on exactly the terrain the lamps "
            "are lighting."
        ),
        "steps": steps,
    }


def build_hold():
    """The same colony, established and handed over -- no profiling windows, no arms.

    WHY IT CANNOT JUST BE THE MEASURED SCENARIO WITH --hold. Profiling is what that one exists for,
    and a Profile step SKIPS THE WHOLE SCENARIO when the analyzer is absent -- reporting PASS while
    building nothing. So running it with --no-profiler, which is what using Circinus instead of Dubs
    requires, hands over an empty map and a green result. This variant has no Profile step at all,
    so it is indifferent to which profiler is loaded.

    WHY IT ENDS ON THE SHIPPED FLAGS rather than sweeping arms. The point of holding is to stand in
    the colony the mod actually ships, so there is exactly one arm and it is the full one. The
    measured scenario's gated arm is a control for a comparison nobody is making here, and leaving
    it last would hand over the colony with the subsystem switched off -- which looks like the mod
    failing to load.

    The population pins are kept. They are what separate "five hundred lamps" from "a power net that
    did not solve", and holding a colony whose lamps never lit would waste the session rather than
    fail it.
    """
    colony = sc.build()

    steps = sc.setup_steps(colony)
    steps += sc.palette_steps()
    steps += sc.establish_steps()
    steps += sc.population_probes()
    steps += sc.feature_steps(vector_lights=True)
    steps.append(sc.step("Wait", frames=SETTLE_FRAMES))
    steps.append(sc.record("vector_light_bake_reset"))

    # Unpaused on the way out. --hold restores the UI and the clock anyway, and leaving the scenario
    # paused would hand over a colony that looks frozen for the first second.
    steps.append(sc.step("SetTimeSpeed", speed="normal"))

    return {
        "name": "stress_light_colony_hold",
        "saveFile": "minimal_colony.rws",
        "description": (
            "stress_light_colony's colony exactly -- 500 lamps in 11 colours and 9 radii, 11,868 "
            "hidden conduits, 20 toxifier generators, 1,874 wall cells across 28 rooms and 120 "
            "free-standing stubs -- built, switched to the shipped vector-lighting configuration, "
            "and handed over. It measures nothing. "
            "\n\n"
            "FOR --hold, AND FOR PROFILERS THAT ARE DRIVEN BY HAND. A Profile step skips the whole "
            "scenario when Dubs Performance Analyzer is absent and still reports PASS, so the "
            "measured scenario cannot be run under --no-profiler -- it hands over an empty map and a "
            "green result. Circinus is instrumented from inside the game rather than by a scenario "
            "step, so using it means --no-profiler, which means this file. It carries no Profile "
            "step and is indifferent to which profiler is loaded. "
            "\n\n"
            "ONE ARM, AND IT IS THE FULL ONE. The measured scenario ends on whichever arm it "
            "happened to order last; handing over a colony with the subsystem switched off would "
            "look exactly like the mod failing to load. The population pins are kept, because a "
            "power net that did not solve removes 400 of the 500 emitters while still photographing "
            "as a lit colony, and holding that would waste a session rather than fail it."
        ),
        "steps": steps,
    }


def write(spec, target):
    with open(target, "w") as handle:
        json.dump(spec, handle, indent=2)
        handle.write("\n")
    print(f"wrote {target} ({len(spec['steps'])} steps)")


def main():
    write(build(), TARGET)
    write(build_hold(), HOLD_TARGET)


if __name__ == "__main__":
    main()
