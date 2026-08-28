#!/usr/bin/env python3
"""The same colony, with thirty doors swinging under five hundred lamps.

WHAT THIS ADDS OVER stress_light_colony. That scenario measures a large STATIC population: five
hundred emitters that never move, so after the first frame every polygon is cached and what is left
is composition and upload. This one keeps the identical map and makes the geometry move -- thirty
doors opening and closing continuously, each swing dirtying every lamp within reach of it. That is
the invalidation path, and it is the one that scales badly in the wrong design: a swing that dirties
one emitter and a swing that dirties the whole map look identical on screen and differ by two orders
of magnitude in cost.

WHY THIRTY DOORS RATHER THAN ONE, WHICH IS WHAT vector_light_door_storm USES. That scenario swings a
single door with eight lamps around it, which is the right shape for scoring the silhouette memo per
gather. It cannot say what happens when invalidations OVERLAP -- when a lamp sits inside the reach of
three doors that are all moving, and its polygon is dirtied again before the rebuild it already owed
has happened. Thirty doors over a plate with lamps every few cells guarantees that overlap.

BOTH KINDS OF DOOR, DELIBERATELY. Nineteen of the colony's twenty-eight rooms are roofed and nine are
open-air courtyards. The thirty driven doors are drawn from both populations, interiors first: a door
into a roofed room and a door into an unroofed enclosure are genuinely different cases, because the
roofed one moves what the indoor gates and the sky occlusion see as well as what the polygon does.
The exact split is asserted below and stated in the description, so a later layout change that
quietly emptied one population fails here rather than halving the scenario in silence.

THE SWINGS ARE STAGGERED, NOT SYNCHRONISED, and that is the harder case on purpose. The harness runs
one step per frame, so thirty consecutive SetDoorOpen steps start thirty swings on thirty successive
frames -- the first door is already moving while the thirtieth is still shut. A colony where every
door moved in lockstep would hand the invalidation path one big batch per wave to coalesce;
staggering gives it a continuous trickle, which is what a colony with pawns walking through it
actually produces.

WHY THE COUNTS ARE RECORDED AND NOT PINNED. A door animates on the tick counter while the harness
renders frames, so how many quantisation steps a swing crosses inside a window depends on how fast
the machine was running -- vector_light_door_storm records the same caveat and for the same reason.
The number to read is a RATIO that does not care: silhouette hits as a share of hits plus rebuilds.

THIS SCENARIO CANNOT RUN WITHOUT THE ANALYZER, and that is the harness's design rather than an
oversight. ProfileStart skips the whole scenario when Dubs Performance Analyzer is absent, which is
the loud no-op the feature wants. Run it with profiling on; --no-profiler will report it skipped.

    python3 Tools/ScenarioGen/gen_stress_door_colony.py
"""

import json
import os

import stress_colony as sc

HERE = os.path.dirname(os.path.abspath(__file__))
SCEN = os.path.abspath(os.path.join(HERE, "..", "..", "Tests", "Scenarios"))
TARGET = os.path.join(SCEN, "stress_door_colony.json")

# Doors driven, as briefed.
DRIVEN_DOORS = 30

# Open-close cycles per arm. Each wave is thirty opens and thirty closes, so an arm provokes
# 60 * WAVES swings -- at four waves, 240, against vector_light_door_storm's eight.
WAVES = 4

# Frames held after a wave of thirty commands, before the opposite wave starts. The thirty commands
# already span thirty frames on their own; this is the tail that lets the LAST door started reach the
# end it was heading for. A swing cut short still counts its quantisation steps, but it leaves
# GameComponent_DoorAperture watching it into the next command.
SETTLE_FRAMES = 40

# Frames held at the END of an arm, after the last close wave. Much longer than SETTLE_FRAMES, and
# not for pacing: it is what makes both arms read their behaviour probes with the doors in the SAME
# state. Without it an arm ends at whatever aperture the frame boundary happened to fall on, and the
# two arms then disagree for a reason that has nothing to do with the flag between them.
SHUT_FRAMES = 120

SETTLE_AFTER_FLAGS = 30

# Superfast, not normal. The stress is invalidation churn PER RENDERED FRAME, and the clock speed is
# what decides how many ticks -- and therefore how much door movement -- happens between two frames.
# Normal speed spreads a 45-tick swing over roughly 45 frames and measures a gentle trickle; superfast
# packs the same swing into a handful and is the case worth knowing about. It is also what
# vector_light_perf profiles at, so the frame costs are at least on the same footing.
TIME_SPEED = "superfast"


def driven_doors(colony):
    """Thirty doors, interiors first, so both populations are represented and the split is stated.

    Sorted before slicing. The colony's door lists come out in room-generation order, which is stable
    today but is exactly the kind of thing an unrelated edit to the layout reorders -- and a slice off
    an unsorted list would then drive a different thirty doors and move every number here with it.
    """
    interiors = sorted(colony.interior_doors)
    courtyards = sorted(colony.courtyard_doors)
    chosen = (interiors + courtyards)[:DRIVEN_DOORS]

    if len(chosen) < DRIVEN_DOORS:
        raise RuntimeError(
            f"the colony has {len(chosen)} doors, fewer than the {DRIVEN_DOORS} this scenario drives")

    interior_set = set(interiors)
    interior_count = sum(1 for door in chosen if door in interior_set)

    if interior_count == 0 or interior_count == len(chosen):
        raise RuntimeError(
            f"all {len(chosen)} driven doors are the same kind (interiors: {interior_count}) — "
            "the scenario claims to exercise both and would not be")

    return chosen, interior_count


def door_offset(cell):
    """SetDoorOpen takes an offset from MAP CENTRE, not from the plate's origin.

    Every other step here names a cell relative to the plate (their `offset` arg carries the plate
    origin and the cell list is relative to that), but SetDoorOpen has no anchor arg -- it resolves
    map.Center + offset directly. Passing a plate-local cell would address a door fifty cells south of
    the real one, find open ground, and fail with "no Building_Door" pointing at a door that is
    plainly there in the frame.
    """
    x, z = cell
    return f"{sc.ORIGIN_X + x},{sc.ORIGIN_Z + z}"


def swing_steps(doors):
    """The storm: WAVES rounds of open-everything, then close-everything."""
    steps = []
    for _ in range(WAVES):
        for cell in doors:
            steps.append(sc.step("SetDoorOpen", offset=door_offset(cell), open="true"))
        steps.append(sc.step("Wait", frames=SETTLE_FRAMES))

        for cell in doors:
            steps.append(sc.step("SetDoorOpen", offset=door_offset(cell), open="false"))
        steps.append(sc.step("Wait", frames=SETTLE_FRAMES))

    steps.append(sc.step("Wait", frames=SHUT_FRAMES))
    return steps


def arm(name, doors, vector_lights, changed_dirty=None, glow_blocker=None, dirty_suppress=False):
    """One measured arm: flags, a photograph, then the storm inside a profiling window.

    ProfileStart/ProfileMeasure/ProfileStop rather than the composite Profile step, because the window
    here is "whatever these steps do" and not a fixed frame count -- the storm's length is decided by
    the door commands, not by a number this file could name. ProfileMeasure with frames=0 is the
    documented form for that.

    THE COUNTERS ARE DRAINED PER ARM, not once for the scenario. Every workload probe in this file
    used to read a total accumulated since the establish block, which is the right shape when the
    arms differ by whether the subsystem is on at all -- one of them contributes nothing. It is the
    wrong shape the moment two arms both run the subsystem and differ in how much work it asks for,
    because the second arm's reading contains the first arm's storm. The reset here costs one step
    and makes vector_light_section_dirties an arm-local number.
    """
    steps = sc.feature_steps(vector_lights=vector_lights, changed_dirty=changed_dirty,
                             glow_blocker=glow_blocker, dirty_suppress=dirty_suppress)
    steps.append(sc.step("Wait", frames=SETTLE_AFTER_FLAGS))
    steps.append(sc.step("Probe", probeName="vector_light_bake_reset",
                         expectedValue=0, tolerance=sc.RECORD_TOLERANCE))

    # Shot with the doors shut, before the storm, so the two arms' stills are comparable to each
    # other and to stress_light_colony's. A frame taken mid-storm would differ between arms by which
    # doors happened to be open on that frame, which is drift rather than effect.
    steps.append(sc.step("Screenshot", fileName=f"stress_door_colony_{name}", hideUi="true"))
    steps.append(sc.step("Wait", frames=4))
    steps.append(sc.step("Screenshot", fileName=f"stress_door_colony_{name}_clean", hideUi="true"))

    steps.append(sc.step("SetTimeSpeed", speed=TIME_SPEED))
    steps.append(sc.step("ProfileStart", timeSpeed=TIME_SPEED, warmupFrames=30))
    steps.append(sc.step("ProfileMeasure", frames=0))
    steps += swing_steps(doors)
    steps.append(sc.step("ProfileStop", **{"name": name, "prefix": "CelestialLighting"}))
    steps.append(sc.step("SetTimeSpeed", speed="paused"))
    steps += arm_probes(name)

    return steps


def arm_probes(name):
    """What this arm's storm asked the map to do, drained per arm rather than for the run.

    THE FOUR THAT SCORE THE CHANGED-DIRTY ARM, and they only mean anything together:

      section_dirties   what the draw flagged. The number the feature moves directly.
      mask_applies      what actually regenerated through the mask. THE ONE TO BELIEVE -- vanilla
                        regenerates only what is on screen, so flags can fall a long way while the
                        work does not move at all, and that outcome would mean the saving was on
                        sections nobody was looking at.
      bakes             polygons rebuilt. Has to stand STILL between the two arms. If it falls, the
                        saving came from baking less, which is a different change from this one and
                        would mean the arm is measuring something it did not set.
      unchanged_bakes   bakes whose coverage grid came out byte-identical. Zero by construction with
                        the flag off, so the pair also says the flag reached the code.

    Recorded rather than pinned, per this scenario's own rule: these are workload counts on a
    population nobody had measured before, and a pin derived by prediction is what this repo has a
    rule against. The defect counter beside them is pinned, because zero is not a prediction.
    """
    return [
        sc.record("vector_light_section_dirties"),
        sc.record("vector_light_section_dirty_passes"),
        sc.record("vector_light_sections_per_pass"),
        sc.record("vector_light_mask_applies"),
        sc.record("vector_light_bakes"),
        sc.record("vector_light_unchanged_bakes"),
        # THE DEFECT WITNESS, and the one to read first when this arm looks too good. Dirtying FEWER
        # sections is the direction that goes quietly wrong in this subsystem: a section left holding
        # an emitter's previous shape logs nothing, throws nothing and moves no other probe. This
        # counts sections that baked with an emitter reaching them and no polygon to use, which is
        # the closest live witness the repo has to that failure, and a door storm is the provocation
        # most likely to produce one.
        #
        # RECORDED AND NOT PINNED, on this scenario's own rule -- nobody has yet measured it per arm
        # on this colony, and a pin nobody has measured is a prediction. Pin it at whatever the first
        # run reads, and treat a later rise as the finding.
        sc.record("vector_light_mask_skips_dirty"),
        # What the glow-blocker write would have flagged, for the arms that decline it. Zero in every
        # other arm, which is what makes the pair readable: calls is how often a door swing provokes
        # vanilla, sections is what that provocation costs, and the quotient is the fan-out nobody had
        # counted -- MapMeshDirty puts Roofs in its adjacency set, so one cell becomes nine, and a cell
        # near a section corner turns those nine into as many as four sections.
        sc.record("vector_light_suppressed_dirty_calls"),
        sc.record("vector_light_suppressed_dirty_sections"),
    ]


def storm_probes():
    """What the storm cost, in counts. See the header for why none of these is pinned.

    The silhouette pair is the one to read as a ratio: the memo exists to hold a light's whole-cell
    occluder outline across a door swing instead of rescanning its window for it, and the saving is
    per gather. hits / (hits + rebuilds) is the same number whatever the machine's frame rate did to
    the swing count, which is what makes it quotable at all.
    """
    return [
        sc.record("vector_light_silhouette_hits"),
        sc.record("vector_light_silhouette_rebuilds"),
        sc.record("vector_light_gather_wall_ms"),
        # Rebakes provoked, and the invalidation radius that produced them. Read together: a rebake
        # count on its own cannot separate "many doors moved" from "one door dirtied everything".
        sc.record("vector_light_bakes"),
        sc.record("vector_light_invalidations"),
        sc.record("vector_light_invalidation_marks"),
        sc.record("vector_light_marks_per_call"),
        # Door aperture accounting: how many doors are being watched, and rebakes per swing. A timing
        # probe cannot see a call count change, which is the whole reason this one exists.
        sc.record("door_aperture_bakes"),
        # Deferrals: attempts the view cull turned away. Meaningful here only because every lamp is on
        # screen, so a high count is coalescing rather than culling.
        sc.record("vector_light_bake_deferrals"),
        # RECORDED, NOT PINNED AT ZERO, and the first live run is what settled that. It read 87,696
        # — sections that baked with an emitter reaching them and no polygon ready to use. Under 240
        # staggered swings that is the stale-polygon path doing the job it was added for (see
        # b5695e0, "Draw a dirty light's last shadow, instead of dropping it for a frame"), not
        # evidence of a fault. stress_light_colony holds its geometry still, measures zero, and keeps
        # the zero pin; pinning zero HERE would be pinning a wish about the busiest scenario in the
        # repo.
        sc.record("vector_light_mask_stale_polys"),
    ]


def perf_asserts():
    """The storm's cost, lifted into the results block rather than left in the JSON.

    Read 'full' against 'gated': both arms drive the identical thirty doors through the identical
    number of swings over the identical colony, so the difference is what the invalidation path costs
    and not what RimWorld charges for moving a door.

    BOUNDS SET FROM THE FIRST LIVE RUN, not carried over from stress_light_colony -- which is where
    they started, and why this scenario came back red on its own gates the first time it was run. A
    door storm costs an order of magnitude more than a static colony, and a bound copied from the
    quiet scenario says only that the loud one is loud. Measured here: gated 2.05 ms/frame, full
    24.27, of which Patch_VectorLightSuppress is 15.19 and Patch_VectorLightDraw 6.97.

    THAT MEASUREMENT WAS TAKEN ON WHAT IS NOW 'wide', which is why the two baselines share the arm
    bound rather than getting a looser one. The run that set these numbers predates the bake learning
    to report what it changed, so 24.27 is the wide behaviour's own figure -- and a control with a
    slacker gate than the arm it controls for is a place a regression can sit unnoticed.

    THE SUPPRESS BOUND IS THE ONE THAT MATTERS, and it is asserted per patch rather than only in the
    total. At 13.88 calls a frame and 1,095 us a call it is 63% of everything this mod does here --
    and that per-call figure is close to the 1,100-1,155 us the pawn scenario measured and the 694 us
    the static one did. The cost is not the mask getting slower under load; it is the mask being asked
    more often. So the CALL RATE is bounded alongside the duration: a change that halves the per-call
    cost while doubling the call count is a wash, and only two numbers side by side can say so.

    Everything is generous rather than tight, for the reason sc.perf_assert records: this box moves
    40% between two runs of an unchanged binary, and a gate that fails for weather gets switched off.
    """
    return [
        sc.perf_assert("gated", "avgMsPerFrame", 8.0),
        # 'wide' and its repeat carry the SAME bound as 'full' and are not given one of their own.
        # The measured 24.27 was taken on the wide behaviour -- it is what the mod did when the run
        # that set these numbers happened -- so the bound already fits it, and giving the baseline a
        # looser gate than the arm it is the baseline for would let a regression hide in the control.
        sc.perf_assert("wide", "avgMsPerFrame", 60.0),
        sc.perf_assert("full", "avgMsPerFrame", 60.0),
        sc.perf_assert("wide_b", "avgMsPerFrame", 60.0),
        sc.perf_assert("full", "maxMsPerFrame", 400.0),
        sc.perf_assert("full", "avgMsPerFrame", 25.0, label="Patch_VectorLightDraw"),
        sc.perf_assert("full", "avgMsPerFrame", 40.0, label="Patch_VectorLightSuppress"),
        sc.perf_assert("full", "callsPerFrame", 40.0, label="Patch_VectorLightSuppress"),
    ]


def build():
    colony = sc.build()
    doors, interior_count = driven_doors(colony)

    steps = sc.setup_steps(colony)
    steps += sc.palette_steps()
    steps += sc.establish_steps()
    steps += sc.population_probes()
    steps.append(sc.step("Probe", probeName="vector_light_bake_reset",
                         expectedValue=0, tolerance=sc.RECORD_TOLERANCE))

    # Gated first, for the same reason as in stress_light_colony: the control must not be the arm that
    # ran on caches the expensive arm warmed.
    #
    # FOUR ARMS, WITH THE BASELINE REPEATED AROUND THE ONE BEING JUDGED. 'wide' is the full subsystem
    # dirtying every section a rebuilt emitter reaches, which is what the mod did before the bake
    # learned to report what it changed; 'full' is the shipped behaviour. They render the identical
    # frame and differ only in how often a section is asked to rebake, so the whole comparison rests
    # on the profiler's call counts -- and a call count taken once is a measurement of the machine as
    # much as of the build. 'wide_b' is the same arm again after 'full', so a drift large enough to
    # explain the difference shows up as the two baselines disagreeing with each other. This colony
    # is deterministic where stress_pawn_colony's is not, so the repeat is cheaper insurance here,
    # but the door storm is long and the box's thermal state over four arms is not a constant.
    steps += arm("gated", doors, vector_lights=False)
    steps += arm("wide", doors, vector_lights=True, changed_dirty=False)
    steps += arm("full", doors, vector_lights=True, changed_dirty=True)
    steps += arm("wide_b", doors, vector_lights=True, changed_dirty=False)
    steps += storm_probes()
    steps += perf_asserts()

    return {
        "name": "stress_door_colony",
        "saveFile": "minimal_colony.rws",
        "description": (
            f"stress_light_colony's map exactly — 500 lamps in 11 colours and 9 radii, 11,868 hidden "
            f"conduits, 20 toxifier generators, 1,874 wall cells across 28 rooms and 120 free-standing "
            f"stubs — with {DRIVEN_DOORS} of its 35 doors driven open and shut for {WAVES} waves an "
            f"arm, i.e. {DRIVEN_DOORS * 2 * WAVES} swings. {interior_count} of the driven doors lead "
            f"into roofed interiors and {DRIVEN_DOORS - interior_count} into open-air courtyards, "
            f"which are different cases: the roofed ones move what the indoor gates and sky occlusion "
            f"see as well as what the polygon does. "
            "\n\n"
            "WHAT THIS MEASURES THAT stress_light_colony CANNOT. That one holds five hundred emitters "
            "still, so every polygon is cached after the first frame and the cost is composition and "
            "upload. Here the geometry moves continuously and the invalidation path is what is under "
            "load — and unlike vector_light_door_storm's single door, the invalidations OVERLAP: a "
            "lamp inside the reach of three moving doors is dirtied again before the rebuild it "
            "already owed has happened. "
            "\n\n"
            "FOUR ARMS: gated, wide, full, wide_b. The first live run of this scenario established "
            "that the mask's per-call cost is flat across every load in the suite — 694 us static, "
            "1,100-1,155 us under fifty walkers, 1,095 us here — while the call RATE goes 0.40 to "
            "6.5-9.8 to 13.88 per frame, and that switching the subsystem on nearly doubles that "
            "rate (7.82 sections a frame gated against 13.88 full). Neither of the two arms it had "
            "then could say how much of that self-inflicted half was avoidable, because both "
            "answered the question the same way. 'wide' dirties every section a rebuilt emitter "
            "REACHES, which is what the mod did before the bake learned to report what it changed; "
            "'full' is the shipped behaviour. The two render the identical frame — the flag decides "
            "how often a section is asked to rebake, not what the rebake produces — so the whole "
            "comparison rests on the profiler's call counts, and a call count taken once measures "
            "this box as much as the build. wide_b repeats the baseline after the arm being judged, "
            "so a drift big enough to explain the difference appears as the two baselines "
            "disagreeing with each other rather than as a result. "
            "\n\n"
            "THE COUNTERS DRAIN PER ARM, which they did not before. A total accumulated since the "
            "establish block is the right shape while the arms differ by whether the subsystem runs "
            "at all, and the wrong shape the moment two of them run it and differ only in how much "
            "work it asks for — the later arm's reading would contain the earlier arm's storm. Read "
            "section_dirties with mask_applies beside it: flags are work REQUESTED and vanilla "
            "regenerates only what is on screen, so a fall in the first without a fall in the second "
            "would mean the saving landed on sections nobody was looking at. Read bakes beside them "
            "too — it has to stand STILL between wide and full, or the saving came from baking less, "
            "which is a different change. "
            "\n\n"
            "THE SWINGS ARE STAGGERED ON PURPOSE. The harness runs one step per frame, so thirty "
            "consecutive SetDoorOpen steps start thirty swings on thirty successive frames rather "
            "than in lockstep. That denies the invalidation path a single coalescible batch per wave "
            "and gives it the continuous trickle a colony with pawns in it actually produces. "
            "\n\n"
            "READ THE SILHOUETTE PAIR AS A RATIO, NOT AS COUNTS. A door animates on the tick counter "
            "while the harness renders frames, so how many quantisation steps a swing crosses depends "
            "on how fast this box was running — the counts move between runs of one build. "
            "hits / (hits + rebuilds) does not. Every count here is recorded rather than pinned for "
            "that reason; the two things that ARE pinned are the population (500 emitters, 11 "
            "colours, 9 radii, before anything is measured) and vector_light_mask_stale_polys at "
            "zero, which is a defect count and not a cost. "
            "\n\n"
            "Profiled with ProfileStart/ProfileStop rather than a fixed-frame Profile, because the "
            "window is the storm and the storm's length is decided by the door commands. That means "
            "the scenario SKIPS entirely without Dubs Performance Analyzer loaded — run it with "
            "profiling on. Clouds are off explicitly; the clock runs here on purpose and a drifting "
            "cloud sheet would shade the very terrain the lamps are lighting."
        ),
        "steps": steps,
    }


def main():
    spec = build()
    with open(TARGET, "w") as handle:
        json.dump(spec, handle, indent=2)
        handle.write("\n")
    print(f"wrote {TARGET} ({len(spec['steps'])} steps)")


if __name__ == "__main__":
    main()
