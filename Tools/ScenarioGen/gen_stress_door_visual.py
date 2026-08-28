#!/usr/bin/env python3
"""The door storm again, asking whether the door-swing effect has to touch gameplay light at all.

THE QUESTION. Light through open doors ships as three flags driven by one settings switch. Two of
them -- `vector_light_open_doors` and `vector_light_door_aperture` -- decide whether OUR polygon sees
an open door as a hole, and cost a polygon rebake. The third, `vector_light_door_glow_blocker`, also
clears vanilla's own light-blocker bit for that cell so vanilla's flood arrives through the doorway
too. That third one is the only one that writes gameplay light, and it is the only one that provokes
work in vanilla: `GlowGrid.LightBlockerRemoved` dirties the cell, re-floods every light whose window
covers it, and dirties the lighting overlay section -- where our mask then costs ~1.2 ms to rebake.

So the door-swing cost may not be intrinsic to drawing a beam through a door. It may be the price of
ALSO telling the glow grid, which nothing in vanilla ever does: `Building_Door.DoorOpen` sets
`openInt`, clears the reachability cache, raises a notification, and touches the glow grid not at all.

`vector_light_open_door.json`'s arm 3 already showed the drawn-only composition renders correctly --
polygon identical to a bare doorway, `glow_out` left at vanilla's 0.095. What nobody has measured is
what it COSTS, because that scenario is one door and eight lamps.

FIVE ARMS, and the pairing is the point:

    gated       the subsystem off AND the glow blocker off. The untouched game.
    full        the shipped arrangement: beam drawn, glow grid told.
    visual      the same, with the glow blocker off. The beam is drawn and gameplay light is vanilla's.
    suppressed  the write still happens and gameplay light is unchanged, but it is not allowed to
                flag sections for redraw. The third offer, and the one that gives up the least.
    full_b      'full' again, after the arms being judged, so drift shows up as the two baselines
                disagreeing with each other rather than as a result.

WHAT 'suppressed' RISKS, because it is the arm that can be wrong quietly. Vanilla's flood is geodesic
and keeps bending past the doorway, so a cell lit only by a path wrapping around a corner beyond the
door has its glow changed by the re-flood while our straight-line coverage never moved -- and that
section is flagged by nobody and holds a stale overlay. Nothing throws, nothing logs, and no other
probe moves. vector_light_suppressed_dirty_sections counts what was declined; it is a measure of
exposure, not of correctness, and the frames are what have to be looked at.

WHY `gated` IS NOT THE SAME `gated` stress_door_colony HAS. That scenario never stated the three door
flags, so it inherited them at their true defaults -- meaning its "subsystem off" arm was still
writing vanilla's light-blocker bit on all 240 swings. It was vanilla plus our glow-grid writes. Here
the flag is stated off, so the arm is the game. Expect this arm to read LOWER than the one in the
committed table, and treat the gap as the measurement of what the glow-blocker half costs even with
the renderer switched off entirely.

WHAT WOULD MAKE THE ANSWER 'NO'. `visual` has to keep drawing the beam -- if `vector_light_bakes`
collapses along with the section dirties, the arm turned the feature off rather than moving it off
vanilla's grid, and the saving is a feature-absent picture. The bake row is what separates those.

    python3 Tools/ScenarioGen/gen_stress_door_visual.py
"""

import json
import os

import gen_stress_door_colony as gd
import stress_colony as sc

TARGET = os.path.join(gd.SCEN, "stress_door_visual.json")


def perf_asserts():
    """This scenario's own gates, because the door colony's name arms that do not exist here.

    Reusing gd.perf_asserts() was the first cut and it failed loudly rather than quietly, which is the
    harness working: an assert naming a missing table is an error, not a silent pass. Recorded because
    the failure mode of the opposite arrangement -- a gate that matches nothing and reports green -- is
    the one this repo keeps being bitten by.

    BOUNDS FROM THIS RUN, generous rather than tight, on sc.perf_assert's own argument: the box moves
    12% between the two baseline arms of THIS run, so a gate set near a measured value is a gate that
    fails for weather and then gets switched off. Measured here: gated 0.45 ms/frame, full 15.82,
    visual 7.61, full_b 17.80, with Patch_VectorLightSuppress at 9.45 / 2.82 / 9.52 in the three lit
    arms.

    THE VISUAL ARM'S GATE IS THE TIGHT ONE, deliberately. It is the only number here that is a claim
    rather than a record: if the saving evaporates, this is what says so, and a bound set at the
    baseline's level would let it evaporate silently.
    """
    return [
        sc.perf_assert("gated", "avgMsPerFrame", 8.0),
        sc.perf_assert("full", "avgMsPerFrame", 60.0),
        sc.perf_assert("full_b", "avgMsPerFrame", 60.0),
        sc.perf_assert("visual", "avgMsPerFrame", 20.0),
        sc.perf_assert("full", "maxMsPerFrame", 400.0),
        # The call RATE beside the duration, on stress_door_colony's rule: a change that halves the
        # per-call cost while doubling the count is a wash, and only both numbers say so. This arm's
        # whole claim is that the RATE falls, so bounding it is bounding the claim.
        sc.perf_assert("visual", "callsPerFrame", 4.0, label="Patch_VectorLightSuppress"),
        sc.perf_assert("full", "callsPerFrame", 20.0, label="Patch_VectorLightSuppress"),
        # The suppression arm's claim is the same one, reached without giving up vanilla's wash, so it
        # carries the same gate. Recorded rather than tight on the duration, because it is new and a
        # bound nobody has measured is a prediction.
        sc.perf_assert("suppressed", "avgMsPerFrame", 60.0),
        sc.perf_assert("suppressed", "callsPerFrame", 20.0, label="Patch_VectorLightSuppress"),
    ]


def build():
    colony = sc.build()
    doors, interior_count = gd.driven_doors(colony)

    steps = sc.setup_steps(colony)
    steps += sc.palette_steps()
    steps += sc.establish_steps()
    steps += sc.population_probes()
    steps.append(sc.step("Probe", probeName="vector_light_bake_reset",
                         expectedValue=0, tolerance=sc.RECORD_TOLERANCE))

    # Gated first, on stress_light_colony's rule: the control must not be the arm that ran on caches
    # the expensive arm warmed. Then the shipped arrangement, then the one being judged, then the
    # baseline again.
    steps += gd.arm("gated", doors, vector_lights=False)
    steps += gd.arm("full", doors, vector_lights=True, changed_dirty=True, glow_blocker=True)
    steps += gd.arm("visual", doors, vector_lights=True, changed_dirty=True, glow_blocker=False)
    # The third offer, and the one that gives up the least. 'visual' buys its saving by giving up
    # vanilla's wash and reverting a gameplay-light rule; this keeps BOTH — the write still happens,
    # the flood still recomputes, GroundGlowAt still answers what it answers today — and declines only
    # the section flagging the write provokes. If it lands near 'visual' on cost it is strictly the
    # better trade, and the thing to check is not the cost but the staleness.
    steps += gd.arm(
        "suppressed", doors, vector_lights=True, changed_dirty=True, glow_blocker=True,
        dirty_suppress=True)
    steps += gd.arm("full_b", doors, vector_lights=True, changed_dirty=True, glow_blocker=True)
    steps += gd.storm_probes()
    steps += perf_asserts()

    return {
        "name": "stress_door_visual",
        "saveFile": "minimal_colony.rws",
        "description": (
            f"stress_door_colony's map and storm exactly — 500 lamps, {gd.DRIVEN_DOORS} doors driven "
            f"for {gd.WAVES} waves, i.e. {gd.DRIVEN_DOORS * 2 * gd.WAVES} swings an arm — asking one "
            f"question the door colony cannot: how much of a door swing's cost is the BEAM, and how "
            f"much is telling vanilla's glow grid about it. "
            f"{interior_count} of the driven doors lead into roofed interiors."
            "\n\n"
            "Light through open doors is three flags. Two decide whether our polygon sees a hole and "
            "cost a polygon rebake; the third clears vanilla's light-blocker bit so vanilla's flood "
            "arrives too. Only the third writes gameplay light, and only the third provokes vanilla "
            "work — LightBlockerRemoved dirties the cell, re-floods every light that can see it, and "
            "regenerates the lighting overlay section, which is where the mask's ~1.2 ms goes. "
            "Vanilla itself never does this: Building_Door.DoorOpen touches the glow grid not at all."
            "\n\n"
            "FOUR ARMS. 'gated' is the subsystem off AND the glow blocker off — the untouched game, "
            "which stress_door_colony's own gated arm is NOT, because it never stated the door flags "
            "and so inherited them on. 'full' is the shipped arrangement. 'visual' draws the same "
            "beam and leaves gameplay light vanilla's. 'full_b' repeats the baseline after the arm "
            "being judged, because a call count taken once measures the machine as much as the build."
            "\n\n"
            "READ THE BAKE ROW FIRST. If vector_light_bakes falls in 'visual' along with the section "
            "dirties, the arm switched the feature off rather than moving it off vanilla's grid, and "
            "the saving is a picture of an absent feature. The bakes standing still is what makes the "
            "rest of the table mean what it says."
            "\n\n"
            "The cloud flags are off throughout, per CLAUDE.md: the sheet drifts on the tick counter "
            "and these arms run their clock, so it would shade different outdoor cells every arm."
        ),
        "steps": steps,
    }


def main():
    scenario = build()
    with open(TARGET, "w") as handle:
        json.dump(scenario, handle, indent=2)
        handle.write("\n")
    print(f"wrote {TARGET} ({len(scenario['steps'])} steps)")


if __name__ == "__main__":
    main()
