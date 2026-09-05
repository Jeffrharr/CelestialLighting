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
MASK_TARGET = os.path.join(SCEN, "stress_light_mask.json")

# The mask's parent arm and its four population-scaled stages. Order is parent first, because that
# is the one a build-to-build comparison is read on and the four below it are the finding.
ARMS = ("circ_vlmask", "circ_vlmaskcollect", "circ_vlmaskshadow",
        "circ_vlmasksat", "circ_vlmaskfold")

# Whole-map rebakes to provoke inside the measured window. See build_mask for why a static colony
# needs any, and why this number is small.
REBUILDS = 20

# The flag re-set to provoke them. Chosen because feature_steps already states it FALSE and it ships
# false, so writing it again changes nothing except that the write goes through ForceRebuild.
INERT_TOGGLE = "vector_light_door_dirty_suppress"
INERT_VALUE = "false"

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

    # A SECOND SETTLE, WHICH THE MEASURED SCENARIO DOES NOT NEED, and the reason is the whole point
    # of this file. settle_steps counts FRAMES, and frames are not ticks: this variant exists to be
    # run under --no-profiler, which drops Dubs Performance Analyzer, which makes the game render
    # faster -- so the same 150 frames buy fewer ticks and the rare-tick cycle has not finished
    # bringing every lamp up. The first hold run read 485 emitters of 503 for exactly that reason.
    #
    # Added here rather than by widening settle_steps, because the measured scenarios' pins were
    # taken under the analyzer and re-timing their settle would move numbers that are baselines.
    # This file has no measured numbers to protect.
    steps += sc.settle_steps()

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


def build_mask():
    """The same colony, with the lighting-overlay mask decomposed into its stages by Circinus.

    WHAT THIS ASKS THAT NEITHER SIBLING DOES. stress_light_colony's own first run found the
    subsystem's worst frame -- 64 ms against a 0.77 ms average, all of it in
    Patch_VectorLightSuppress across 112 calls -- and left it recorded rather than explained,
    because a Dubs row is one duration for the whole postfix. That is roughly 2.4 ms per section
    regenerate at five hundred emitters, and the whole question here is which stage inside it that
    is. VectorLightMask.Apply has four that scale with the emitter POPULATION rather than with the
    section: the roster scan that decides which emitters reach it, the per-emitter shadow
    accumulation, the saturation reconstruction, and the fold that reconstruction is made of.

    WHY IT IS A THIRD FILE RATHER THAN A FLAG ON THE FIRST. Circinus is instrumented from inside the
    game rather than by a scenario step, so reading it means --no-profiler, and a Profile step under
    --no-profiler SKIPS THE WHOLE SCENARIO while reporting PASS. stress_light_colony has two of
    them. This one has none, for the same reason the hold variant has none, and it inherits that
    variant's second settle for the same reason too: frames are not ticks, and without the analyzer
    the same frame count buys fewer of them than the rare-tick cycle needs to bring 500 lamps up.

    READ THE CHILDREN TO FIND, THE PARENT TO JUDGE. Circinus totals are exclusive of armed children,
    so the four stage arms change what circ_vlmask reports and the per-call overhead lands hardest
    on the most frequently entered of them. The split is the finding; a comparison between two
    builds is read on circ_vlmask and on the frame, not on a child.
    """
    colony = sc.build()

    steps = sc.setup_steps(colony)
    steps += sc.palette_steps()
    steps += sc.establish_steps()
    steps += sc.settle_steps()
    steps += sc.population_probes()

    # The shipped configuration but for ONE flag, and there is no gated arm. An arm with the
    # subsystem off does not enter Apply at all, so its stage arms would read zero calls -- which is
    # not a control, it is an empty document. The control for this run is the same file on another
    # build.
    #
    # vector_light_changed_dirty IS OFF, AND THE FIRST RUN OF THIS FILE IS WHY. With it on, a
    # section is flagged only where a rebake CHANGED an emitter's coverage -- and the storm below
    # rebakes every emitter to the shape it already had, so nothing changes, nothing is flagged,
    # nothing regenerates and every arm in this file reads zero calls against a perfectly correct
    # 500-lamp frame. That is the "zeros are not a measurement" failure with the scene photographing
    # as fine, and it cost one run to find.
    #
    # TURNING IT OFF IS SOUND FOR THIS QUESTION AND WOULD NOT BE FOR MOST. The flag decides HOW OFTEN
    # Apply is asked, never what Apply does -- it is the one flag in feature_steps whose two settings
    # render the identical frame. So the per-call stage split measured here is the shipped split; what
    # is not shipped is the call RATE, and no number in this file is a rate. A comparison of totals
    # between two builds stays valid for the same reason, provided both carry this flag the same way,
    # which they do because it is stated here rather than inherited.
    steps += sc.feature_steps(vector_lights=True, changed_dirty=False)
    steps.append(sc.step("Wait", frames=SETTLE_FRAMES))

    steps.append(sc.step("Probe", probeName="circinus_available", expectedValue=1, tolerance=0))

    # PINNED, BECAUSE ITS FAILURE MODE IS SILENCE. VectorLightMask.Active is the feature flag AND
    # GlowGridPerLight.Available, and that second half is reflection over two private fields on a
    # Burst-adjacent vanilla type. When it fails the mask stands down to the crossfade, Apply is
    # never entered, and every arm in this file reads zero calls -- indistinguishable from a window
    # that regenerated no sections. Two different causes of the same zero is one too many.
    steps.append(sc.step("Probe", probeName="vector_light_mask_available",
                         expectedValue=1, tolerance=0))
    steps.append(sc.record("vector_light_bake_reset"))

    # Checked against what the steps ABOVE actually emitted rather than trusting the constant's
    # comment. If somebody changes this flag's setting in stress_colony.feature_steps, the storm
    # below would start flipping configuration between frames and its stage split would quietly stop
    # describing the shipped composition. Failing the generator is the cheap place to find that.
    stated = [(s.get("args") or {}).get("enabled") for s in steps
              if s.get("type") == "SetFeature"
              and (s.get("args") or {}).get("featureName") == INERT_TOGGLE]

    if stated[-1:] != [INERT_VALUE]:
        raise SystemExit(
            f"{INERT_TOGGLE} is {stated[-1:]} in this scenario, expected [{INERT_VALUE!r}] -- "
            "pick a different inert toggle or the mask storm changes the scene as it runs")

    # THE WINDOW OPENS AND THE COUNTERS RESET AT ONE STEP. Opening the Circinus run several steps
    # away from the counter reset is what once reported 161 bakes against 46 calls to the method
    # that makes them -- two true numbers over two different windows, which is exactly the pair that
    # gets quoted as a ratio by mistake.
    steps.append(sc.step("Probe", probeName="circinus_run_start_maskscale",
                         expectedValue=1, tolerance=0))

    # ARMED IS PINNED, NOT RECORDED. Circinus sheds instrumentation on its own schedule and an arm
    # that shed reads zero calls -- indistinguishable from a stage that never ran, which on this
    # scenario would read as the mask having become free.
    for armed in ARMS:
        steps.append(sc.step("Probe", probeName=armed + "_patched",
                             expectedValue=1, tolerance=0))

    # THE PROVOCATION, AND WHY A STATIC COLONY NEEDS ONE. Five hundred lamps that are not moving
    # dirty almost nothing: a settled colony regenerates the lighting overlay only where the glow
    # grid changed, so a window that merely lets the clock run measures the mask on a handful of
    # sections and reports the stage split of a scene nobody is looking at. That is the same shape
    # of error as profiling a paused colony.
    #
    # Every SetFeature goes through VectorLightRedraw.ForceRebuild, which drops every emitter and
    # rebakes it on the next draw -- and the draw then flags every section those reaches touch. So
    # re-setting an already-stated flag to the value it already holds changes nothing about the
    # scene and buys one whole-map rebake, which is exactly the 64 ms frame this file is here to
    # decompose. vector_light_bake_storm establishes the technique on the twenty-two-lamp plate.
    #
    # The count is deliberately modest. Each pass rebakes all five hundred emitters and re-masks
    # every visible section, so this is not a cheap step to repeat, and the quantity wanted is a
    # per-call split rather than a total.
    for _ in range(REBUILDS):
        steps.append(sc.step("SetFeature", featureName=INERT_TOGGLE, enabled=INERT_VALUE))

    # One tick-advancing window so the LAST toggle's rebake lands inside the measured run rather
    # than after the probes have read. Ticks rather than frames, because a section is regenerated
    # from the map's own update and a frame that advances no tick does not run one.
    steps.append(sc.step("TickLapse", ticks=20, steps=20, fps=20,
                         fileNamePrefix="maskscale_discard"))

    # Counts first, then durations. Per-call cost is total/calls and never AvgMs -- AvgMs is per
    # CYCLE, and a frame that regenerated eleven sections divides by the wrong thing.
    #
    # circ_vlmask_calls IS THE REGENERATE COUNT, and it is what the stage durations are read
    # against. vector_light_mask_applies would be the natural probe for that and is deliberately
    # not used: every SetFeature above resets our own telemetry through ForceRebuild, so it would
    # report only the sections regenerated since the last toggle while the Circinus totals span the
    # whole window -- two true numbers over two different windows, which is exactly the pair that
    # gets quoted as a ratio by mistake.
    for armed in ARMS:
        steps.append(sc.record(armed + "_calls"))
        steps.append(sc.record(armed + "_total_ms"))
        steps.append(sc.record(armed + "_max_ms"))

    # THE MEASUREMENT. Everything above this is the Circinus bank, kept because its ZEROS are the
    # finding rather than a failure: it is armed on all five methods, patched reads 1 on every one,
    # and calls reads 0 on a build that entered Apply on every section of the colony. A section
    # regenerate is not a rendered frame and both frame-based analyzers are blind to it -- the same
    # misreport the sky-falloff rebuild hit. Leaving the arms in place means the next reader finds
    # that written down instead of spending the run rediscovering it.
    #
    # These four are calling-thread stopwatches inside Apply itself, and they are what the stage
    # split is actually read from. The three children partition the parent: collect + shadow +
    # saturation should sum to close to wall, and a gap is Apply's own mesh round trip.
    #
    # ONE WHOLE-MAP REBAKE, NOT TWENTY. Every SetFeature above goes through ForceRebuild, which calls
    # VectorLightMask.ResetTelemetry -- so these clocks hold the LAST toggle's rebake only, and
    # vector_light_mask_applies resets with them so the pair still divides. The other nineteen are
    # not wasted: they are what warms the JIT and the scratch buffers, so the measured one is a
    # steady-state rebake rather than a cold one.
    steps.append(sc.record("vector_light_mask_applies"))
    steps.append(sc.record("vector_light_mask_applies_clocked"))
    steps.append(sc.record("vector_light_mask_lights_scanned"))
    steps.append(sc.record("vector_light_mask_lights_folded"))
    steps.append(sc.record("vector_light_mask_emitters_scanned"))
    steps.append(sc.record("vector_light_mask_emitters_reaching"))
    steps.append(sc.record("vector_light_mask_fold_cells"))
    steps.append(sc.record("vector_light_mask_saturated_samples"))
    steps.append(sc.record("vector_light_mask_saturation_skipped"))
    steps.append(sc.record("vector_light_mask_wall_ms"))
    steps.append(sc.record("vector_light_mask_collect_ms"))
    steps.append(sc.record("vector_light_mask_shadow_ms"))
    steps.append(sc.record("vector_light_mask_saturation_ms"))

    steps.append(sc.record("vector_light_emitters"))
    steps.append(sc.step("Probe", probeName="vector_light_mask_stale_polys",
                         expectedValue=0, tolerance=0))

    steps.append(sc.step("Probe", probeName="circinus_run_stop", expectedValue=1, tolerance=0))

    return {
        "name": "stress_light_mask",
        "saveFile": "minimal_colony.rws",
        # Circinus is the instrument, not a convenience: every duration in this file comes from it,
        # so a run without it reads zero calls on every arm and fails the patched pins rather than
        # reporting a mask that costs nothing. The value is a STRING -- a bare workshop id as a
        # number boots the game and then silently writes no report at all.
        "requiredMods": {"astryl.Circinus": "3773680130"},
        "description": (
            "stress_light_colony's colony -- 500 lamps in 11 colours and 9 radii, 11,868 hidden "
            "conduits, 20 toxifier generators, 1,874 wall cells across 28 rooms and 120 stubs -- "
            "with VectorLightMask.Apply decomposed into its four population-scaled stages by "
            "Circinus. "
            "\n\n"
            "WHY IT EXISTS. stress_light_colony measured the subsystem's worst frame at 64 ms "
            "against a 0.77 ms average, all of it in Patch_VectorLightSuppress over 112 calls -- "
            "about 2.4 ms per section regenerate at this population -- and could say no more than "
            "that, because a Dubs row is one duration for the whole postfix. Every stage armed here "
            "costs a function of the EMITTER COUNT rather than of the section, which is the axis "
            "this colony moves and no other scenario in the repo does. "
            "\n\n"
            "RUN IT WITH --no-profiler. Circinus is instrumented from inside the game, and a Profile "
            "step under --no-profiler skips the whole scenario while still reporting PASS. This file "
            "carries no Profile step so it is indifferent to which analyzer is loaded. "
            "\n\n"
            "READ THE CHILDREN TO FIND AND THE PARENT TO JUDGE. Circinus totals are exclusive of "
            "armed children, so these four arms change what circ_vlmask reports and inflate the "
            "smallest of them most. The split is the finding; a build-to-build comparison belongs on "
            "circ_vlmask and on the frame. "
            "\n\n"
            "Every duration here is RECORDED, not pinned. What is pinned is the scene, the "
            "instrumentation and the one defect counter: 500 emitters, every arm patched, and "
            "vector_light_mask_stale_polys at zero."
        ),
        "steps": steps,
    }


def main():
    write(build(), TARGET)
    write(build_hold(), HOLD_TARGET)
    write(build_mask(), MASK_TARGET)


if __name__ == "__main__":
    main()
