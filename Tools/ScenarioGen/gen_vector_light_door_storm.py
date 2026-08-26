#!/usr/bin/env python3
"""Door storm: many swings of one door watched by many lamps, so the silhouette memo can be scored.

WHAT IS BEING MEASURED. Issue #188 item C holds a light's whole-cell occluder silhouette across a
door swing instead of rescanning its window for it. The saving is per GATHER -- the pass that reads
the edifice grid -- and it happens only when something dirties a polygon WITHOUT moving a blocker,
which in practice means a door sliding. So the provocation has to be door swings, and there have to
be enough of them, and enough lamps watching, for a duration to rise out of the noise.

WHY NOT THE BAKE STORM. vector_light_bake_storm fires inert feature toggles, each of which calls
ForceRebuild -- and ForceRebuild drops every entry, so every memo goes with it and every gather is a
rebuild. It is the perfect provocation for the bake and the worst possible one for this: it would
report the memo never helping, correctly, and say nothing about the case it exists for.

WHY BOTH ARMS LIVE IN ONE FILE, INTERLEAVED. This box has measured `frame_max_ms` spanning 37 to 85
across three consecutive runs of ONE build, and vector_light_bake_wall_ms moving 40% between two runs
of an unchanged binary. A block design -- all of arm A, then all of arm B, or worse, two separate
runs -- hands whichever arm ran in the quiet half a win it did not earn. Four alternating arms in one
process is the cheapest way to make the comparison survive that, and the two off arms bracket the two
on arms so a drift in either direction is visible rather than absorbed.

WHY THE COUNTS ARE NOT EXACT HERE, unlike the bake storm's 880. A door animates on the tick counter
and the harness renders frames, so how many quantisation steps a swing crosses inside a fixed number
of frames depends on how fast the machine was running. TickLapse cannot fix it -- AdvanceTicks is a
JUMP, so a door's own Tick() never runs under it, and the first cut of the aperture film came back
with the aperture pinned at 0 for every frame while still passing. The counts are therefore RECORDED,
and the number to read is a RATIO that does not care how many swings happened: gather milliseconds
per gather, and hits as a share of hits plus rebuilds.

    python3 Tools/ScenarioGen/gen_vector_light_door_storm.py
"""

import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
SCEN = os.path.abspath(os.path.join(HERE, "..", "..", "Tests", "Scenarios"))
TARGET = os.path.join(SCEN, "vector_light_door_storm.json")

# Where the plate sits on the save's map, matching the other door scenarios so the terrain under it
# is the same concrete and the comparison with their numbers stays meaningful.
ORIGIN = "0,45"

# Swings per arm. Each is an open and a close, and each of those crosses up to nine quantisation
# steps, so an arm provokes on the order of 200 invalidation rounds.
#
# TWELVE RATHER THAN TWO, because the quantity is a duration on a contended box and the only defence
# against that is sample size; and rather than a hundred, because every swing is 50 frames of wall
# clock and four arms of a hundred swings is a twenty-minute run for numbers that stop moving.
SWINGS = 12

# Frames to hold after each SetDoorOpen. A wooden door takes tens of ticks to slide and the harness
# runs one step per frame, so this has to cover the whole animation -- a swing cut short mid-slide
# still counts its steps, but it stops the door reaching the end it was heading for, and
# GameComponent_DoorAperture then keeps watching it into the next command.
SETTLE_FRAMES = 25

# Frames to hold at the END of an arm, after the last close, before anything is read. Deliberately
# much longer than SETTLE_FRAMES: this one is not pacing the storm, it is making sure every arm reads
# its behaviour pins with the door in the SAME state. See the comment at its use for the tick-versus-
# frame reason a shorter hold left the four arms at two different apertures.
SHUT_FRAMES = 90

# A NOTE ON WHAT SETTLE_FRAMES DECIDES, because it decides more than pacing. A rebuild is provoked
# when a door crosses aperture zero, so how often the memo has to give up is a function of how many
# swings actually REACH an end. Before SHUT_FRAMES existed the doors here spent every swing in the
# middle of the range and almost never shut: run 1 recorded 33 rebuilds an arm and a 15.9x gather
# saving, which flattered the memo by measuring a colony whose doors never close. Run 2 shuts them
# and records 145-225 rebuilds and 5.3x. The second number is the one to quote.

# The lamps. TorchLamp rather than StandingLamp because it needs no power: this repo has a recorded
# trap where the nearest transmitter wins even when its net is dead, which presents as an unlit lamp
# and would empty the scenario of emitters without failing anything.
#
# EIGHT OF THEM, ALL WITHIN REACH OF THE DOOR. TorchLamp's glowRadius is 10, so
# MarkGeometryDirtyAround's test is squared distance against (10 + 1); every cell below is inside
# that, which is what makes one swing dirty eight emitters rather than one. Four of them are inside
# the room and four outside, so the two sides of the door are both represented -- an arrangement
# where every lamp saw the same face of the wall would be one scene tested eight times.
LAMPS = [
    (-3, 0), (-5, 3), (-5, -3), (-8, 0),
    (3, 0), (5, 3), (5, -3), (8, 0),
]

# A room with one door in its east wall, transcribed from vector_light_open_door so the geometry is
# one this repo has already looked at rather than a new one nobody has seen rendered.
WALL_CELLS = (
    "-13,-7; -13,-6; -13,-5; -13,-4; -13,-3; -13,-2; -13,-1; -13,0; -13,1; -13,2; -13,3; -13,4; "
    "-13,5; -13,6; -13,7; -12,-7; -12,7; -11,-7; -11,7; -10,-7; -10,7; -9,-7; -9,7; -8,-7; -8,7; "
    "-7,-7; -7,7; -6,-7; -6,7; -5,-7; -5,7; -4,-7; -4,7; -3,-7; -3,7; -2,-7; -2,7; -1,-7; -1,7; "
    "0,-7; 0,-6; 0,-5; 0,-4; 0,-3; 0,-2; 0,-1; 0,1; 0,2; 0,3; 0,4; 0,5; 0,6; 0,7"
)

# Every flag the arms depend on, stated rather than inherited. A defaulted-on flag would silently
# rewrite what the committed pins measure, and this repo has already shipped a committed frame that
# read 17.08 instead of 20.23 because an arm inherited a flag it did not state.
#
# THE CLOUD FLAGS ARE OFF AND THAT IS NOT INCIDENTAL. The cloud sheet drifts on the tick counter, so
# two arms of a fifty-frame swing put it in different places and every outdoor cell it shades differs
# between them. This scenario reads counters rather than pixels, so it would survive that -- but it
# also takes screenshots, and a reader comparing them would be looking at weather.
BASE_FLAGS = [
    ("vector_lights", "true"),
    ("vector_light_penumbra", "true"),
    ("vector_light_suppress", "true"),
    ("vector_light_blend", "true"),
    ("vector_light_mask", "true"),
    ("vector_light_mask_beam", "true"),
    ("vector_light_open_doors", "true"),
    ("vector_light_door_aperture", "true"),
    ("vector_light_door_glow_blocker", "false"),
    ("vector_light_view_cull", "true"),
    ("vector_light_section_dirty", "true"),
    ("vector_light_parallel_bake", "true"),
    ("cloud_cover", "false"),
    ("cloud_sheet", "false"),
    ("cloud_presence", "false"),
    ("cloud_deck_varieties", "false"),
    ("cloud_volume", "false"),
]


def step(t, **args):
    return {"type": t, "args": {k: str(v) for k, v in args.items()}}


def probe(name, expected, tolerance):
    return step("Probe", probeName=name, expectedValue=expected, tolerance=tolerance)


def setup():
    steps = [
        step("SetTile", latitude="45"),
        step("SetSeason", dayOfYear="40"),
        step("SetWeather", weatherDef="Clear", instant="true"),
        step("SetTerrain", **{"def": "Concrete", "width": "40", "height": "24",
                              "offset": ORIGIN, "clear": "true"}),
        step("PlaceThings", **{"def": "Wall", "stuff": "BlocksGranite", "offset": ORIGIN,
                               "layout": "cells", "clear": "true", "cells": WALL_CELLS}),
        step("PlaceThings", **{"def": "Door", "stuff": "WoodLog", "offset": ORIGIN,
                               "layout": "cells", "clear": "true", "cells": "0,0"}),
        step("PlaceThings", **{"def": "TorchLamp", "offset": ORIGIN, "layout": "cells",
                               "cells": "; ".join(f"{x},{z}" for x, z in LAMPS)}),
        # Midnight, so the lamps are the only thing lighting the plate and the sun cannot move under
        # the arms. A daylit version of this would have the sky changing between arm one and arm four.
        step("SetTime", hour="0"),
        step("LookAt", offset=ORIGIN, zoom="26"),
    ]
    steps += [step("SetFeature", featureName=k, enabled=v) for k, v in BASE_FLAGS]

    # The first capture of a scenario carries the HUD -- hideUi is honoured from the second onward --
    # so one is spent here rather than inside an arm, where it would put the message log into a frame
    # somebody is trying to compare.
    steps.append(step("Screenshot", fileName="warmup_discard.png"))
    return steps


def arm(cache_on, index):
    """One arm: state the flags, drop everything, zero the counters, then storm the door."""
    on = "true" if cache_on else "false"
    tag = ("on" if cache_on else "off") + str(index)

    steps = [step("SetFeature", featureName=k, enabled=v) for k, v in BASE_FLAGS]

    # LAST OF THE FLAGS, so its ForceRebuild is the one that lands immediately before the reset. Every
    # SetFeature above also rebuilds, and the field has to be dropped AFTER the arm's configuration is
    # complete rather than in the middle of it, or the arm's first gathers bake against a half-applied
    # scene -- the harness runs one step per frame, so a frame really is rendered between two flags.
    steps.append(step("SetFeature", featureName="vector_light_silhouette_cache", enabled=on))

    # Zeroes every counter this arm reports, including the two new ones. After the rebuild, so the
    # rebuild's own 8 gathers are not charged to the arm.
    steps.append(probe("vector_light_bake_reset", "0", "0"))

    # SetTimeSpeed normal is what makes the door actually move: AdvanceTicks is a jump and a door's
    # own Tick() never runs under it, which is how the first aperture film came back with every frame
    # at aperture 0 and still passed.
    steps.append(step("SetTimeSpeed", speed="normal"))

    for _ in range(SWINGS):
        steps.append(step("SetDoorOpen", offset=ORIGIN, open="true"))
        steps.append(step("Wait", frames=str(SETTLE_FRAMES)))
        steps.append(step("SetDoorOpen", offset=ORIGIN, open="false"))
        steps.append(step("Wait", frames=str(SETTLE_FRAMES)))

    # A LONG SETTLE BEFORE THE READ, AND THE FIRST RUN NEEDED IT. The arms are not equally fast, and
    # RimWorld's normal speed advances ticks against the wall clock rather than against frames -- so a
    # slower arm gets MORE ticks per frame and its door slides further inside the same
    # SETTLE_FRAMES. Run one ended every off arm at aperture 0.125 and every on arm at 0.25, which
    # made the behaviour pins below a comparison between two different door positions. Holding until
    # the door is definitely shut is what makes them a comparison between two builds.
    steps.append(step("Wait", frames=str(SHUT_FRAMES)))

    # Paused before reading, so the numbers below describe the storm and not the storm plus however
    # long the probes took, and so the door cannot move between one probe and the next.
    steps.append(step("SetTimeSpeed", speed="paused"))
    steps.append(step("Wait", frames="2"))

    steps += arm_probes(cache_on)
    steps.append(step("Screenshot", fileName=f"doorstorm_{tag}.png"))
    return steps


def arm_probes(cache_on):
    steps = []

    # ---- the behaviour half. IDENTICAL ACROSS ALL FOUR ARMS OR THE REST IS WORTHLESS ------------
    #
    # The memo is a pure performance change: it must produce the segments a rescan would have
    # produced, and the door ends every arm in the same state. Recorded on the first green run rather
    # than derived -- a pin computed from the arithmetic under test asserts that the arithmetic
    # equals itself.
    steps.append(probe("door_aperture", "0", "0.001"))

    # ELEVEN, NOT THE EIGHT THIS FILE PLACES. minimal_colony.rws arrives with three glowers of its
    # own, far enough from the door that they never dirty and therefore never gather. Pinned exactly
    # anyway, because it is the number that says the plate built: an arm that placed six lamps would
    # still produce a plausible ratio out of six lamps' worth of gathers.
    steps.append(probe("vector_light_count", "11", "0"))

    # THE CORRECTNESS HALF. With every arm ending on the same shut door, these describe the same
    # picture, so they must not move with the flag: the memo's whole claim is that a reused silhouette
    # is the array a rescan would have rebuilt. Recorded wide on the first green run and tightened
    # from what it measured -- a pin derived from the arithmetic under test asserts only that the
    # arithmetic equals itself.
    # MEASURED, AND MEASURED IDENTICAL: run 2 read 1907.62598 in all four arms, to the last digit,
    # off and on. That is the strongest single statement in this file -- the two paths do not merely
    # agree within a tolerance, they produce the same float. The tolerance exists for the door
    # settling a step short on a slower machine, not for the memo.
    steps.append(probe("vector_light_lit_area", "1907.62598", "2"))
    steps.append(probe("vector_light_shadow_fraction", "0.329346061", "0.005"))

    # ---- the headline. HITS ARE EXACTLY ZERO WITH THE FLAG OFF ---------------------------------
    #
    # The one exact pin in the file, and the only one that can be exact, because it is a statement
    # about a code path rather than about a duration. An off arm that reported a single hit would mean
    # the flag does not reach the gather at all -- and every ratio below would then be comparing an
    # arm against itself, which is the failure that looks most like a pass.
    if not cache_on:
        steps.append(probe("vector_light_silhouette_hits", "0", "0"))

    # RECORDED, NOT ASSERTED, and read as a PAIR. A hit count alone cannot separate a working memo
    # from a scene where nothing ever asks twice; a rebuild count alone cannot separate a memo that
    # never helps from one correctly refusing because walls really are going up. Their sum is how many
    # occluder sets were assembled, which is what turns either into a share -- and the share is the
    # number that survives the swing count drifting between arms.
    steps.append(probe("vector_light_silhouette_hits", "800", "800"))
    steps.append(probe("vector_light_silhouette_rebuilds", "800", "800"))

    # THE MEASUREMENT. Milliseconds the calling thread spent reading occluder sets off the map.
    #
    # ONE PIN SPANNING BOTH ARMS, deliberately, which is why the tolerance is absurd -- exactly as
    # vector_light_bake_wall_ms is written in the bake storm, and for the same reason: a pin tight
    # enough to be interesting on one arm fails on the other. Divide it by hits plus rebuilds before
    # comparing anything; the totals are only comparable if the swings landed the same way, and they
    # will not have.
    steps.append(probe("vector_light_gather_wall_ms", "40", "40"))

    # THE CONTROL, and the reason both clocks exist. The memo removes work from the gather and changes
    # nothing about the bake, so this should read the same in every arm. If it moves with the flag,
    # something other than the gather changed and the gather number is not what it claims to be.
    steps.append(probe("vector_light_bake_wall_ms", "260", "260"))

    # How much work there was to do, so the durations above can be read against a population rather
    # than in the abstract. vector_light_bakes is the denominator of the bake clock and
    # invalidation_marks is roughly the denominator of the gather clock.
    steps.append(probe("vector_light_bakes", "1600", "1600"))
    steps.append(probe("vector_light_invalidations", "220", "220"))
    steps.append(probe("vector_light_invalidation_marks", "1700", "1700"))
    steps.append(probe("door_aperture_bakes", "220", "220"))
    return steps


steps = setup()

# off, on, off, on -- the off arms bracket the on arms. See the module docstring for why a block
# design would not survive this machine.
for index, cache_on in enumerate([False, True, False, True], start=1):
    steps += arm(cache_on, index)

out = {
    "name": "vector_light_door_storm",
    "saveFile": "minimal_colony.rws",
    "description": (
        "Issue #188 item C: does holding a light's silhouette across a door swing actually remove "
        f"work. One wooden door in a room's east wall, eight TorchLamps all within its reach, and "
        f"{SWINGS} open/close swings per arm, over four arms alternating "
        "vector_light_silhouette_cache off/on/off/on in ONE process. Interleaved rather than blocked "
        "because this box has measured one unchanged binary spanning 37-85 ms on frame_max_ms across "
        "three runs, and vector_light_bake_wall_ms moving 40% between two runs of the same build -- a "
        "block design would hand whichever arm ran in the quiet half a win it did not earn. "
        "READ THE RATIOS, NOT THE TOTALS. A door animates on the tick counter while the harness "
        "renders frames, so how many quantisation steps a swing crosses is machine-dependent and the "
        "counts drift between arms; vector_light_gather_wall_ms divided by (hits + rebuilds) does "
        "not, and neither does hits as a share of that sum. vector_light_bake_wall_ms is the CONTROL "
        "-- the memo touches the gather and not the bake, so a bake clock that moves with the flag "
        "means the gather number is measuring something else. The one exact pin is that an off arm "
        "reports exactly zero hits, because that is a statement about a code path rather than about a "
        "duration, and an off arm that reported one would mean every ratio here compares an arm "
        "against itself. Clouds are off: the sheet drifts on the tick counter and would put the two "
        "arms' screenshots under different weather."
    ),
    "steps": steps,
}

with open(TARGET, "w") as f:
    json.dump(out, f, indent=2)
    f.write("\n")

print(f"wrote {TARGET}: {len(steps)} steps, {SWINGS} swings x 4 arms")
