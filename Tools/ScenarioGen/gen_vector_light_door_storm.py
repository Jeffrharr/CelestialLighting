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
# EIGHT RATHER THAN THE ORIGINAL TWELVE, because the arm count went from four to six when the glow
# texture hold arrived and the run has to stay inside a few minutes. The quantity is a duration on a
# contended box, so sample size is the only defence against noise -- but six arms of eight swings
# samples the comparison more times than four arms of twelve did.
SWINGS = 8

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


def arm(cache_on, hold_on, index):
    """One arm: state the flags, drop everything, zero the counters, then storm the door."""
    tag = f"{index}_{ARM_NAMES[(cache_on, hold_on)]}"

    steps = [step("SetFeature", featureName=k, enabled=v) for k, v in BASE_FLAGS]

    # LAST OF THE FLAGS, so their ForceRebuild is what lands immediately before the reset. Every
    # SetFeature above also rebuilds, and the field has to be dropped AFTER the arm's configuration is
    # complete rather than in the middle of it, or the arm's first gathers bake against a half-applied
    # scene -- the harness runs one step per frame, so a frame really is rendered between two flags.
    steps.append(step("SetFeature", featureName="vector_light_silhouette_cache",
                      enabled="true" if cache_on else "false"))
    steps.append(step("SetFeature", featureName="vector_light_glow_texture_hold",
                      enabled="true" if hold_on else "false"))

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

    steps += arm_probes(cache_on, hold_on)
    steps.append(step("Screenshot", fileName=f"doorstorm_{tag}.png"))
    return steps


def arm_probes(cache_on, hold_on):
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

    # EVERY COUNT PIN BELOW IS DELIBERATELY LOOSE, and one of them was a notch too tight on its
    # second run: vector_light_field_texture_uploads read 888 against a band that stopped at 880.
    # The counts are a function of how many quantisation steps eight swings crossed, which is a
    # function of how fast the machine was running, so a band fitted to one run's observations is a
    # band that fails on the next. Observed maxima over two runs: hits 784, rebuilds 921, texture
    # uploads 888, UV-only 880, marks 1016. The numbers to actually READ here are the durations and
    # the two exact-zero pins; these exist to catch a run where the storm did not storm.
    #
    # RECORDED, NOT ASSERTED, and read as a PAIR. A hit count alone cannot separate a working memo
    # from a scene where nothing ever asks twice; a rebuild count alone cannot separate a memo that
    # never helps from one correctly refusing because walls really are going up. Their sum is how many
    # occluder sets were assembled, which is what turns either into a share -- and the share is the
    # number that survives the swing count drifting between arms.
    steps.append(probe("vector_light_silhouette_hits", "550", "550"))
    steps.append(probe("vector_light_silhouette_rebuilds", "550", "550"))

    # THE MEASUREMENT. Milliseconds the calling thread spent reading occluder sets off the map.
    #
    # ONE PIN SPANNING BOTH ARMS, deliberately, which is why the tolerance is absurd -- exactly as
    # vector_light_bake_wall_ms is written in the bake storm, and for the same reason: a pin tight
    # enough to be interesting on one arm fails on the other. Divide it by hits plus rebuilds before
    # comparing anything; the totals are only comparable if the swings landed the same way, and they
    # will not have.
    # SPANS BOTH SIDES OF ITS OWN FLAG, so the tolerance looks loose. Measured on the 2x2:
    # 37.97 / 35.21 / 37.01 with the memo off against 7.75 / 7.17 / 8.14 with it on -- 4.79x, and
    # the hold-only arm sits with the other memo-off arms, which is what says the two flags are
    # independent rather than merely believed to be.
    steps.append(probe("vector_light_gather_wall_ms", "24", "24"))

    # ---- the other headline, and the other flag ------------------------------------------------
    #
    # The glow-texture hold moves the FIELD half of the upload clock and nothing else. With the hold
    # off every refresh refills the texture and pushes it to the GPU; with it on, a refresh provoked
    # only by our own geometry rewrites UV1 and leaves the texture alone -- which is legitimate
    # precisely because the shipped mod never writes vanilla's glow grid when a door opens.
    if not hold_on:
        # EXACT, and the mirror of the silhouette pin above: with the hold off there is no path that
        # can skip a texture upload, so a single UV-only refresh would mean the flag does not reach
        # the uploader and every ratio below compares an arm against itself.
        steps.append(probe("vector_light_field_uv_only_uploads", "0", "0"))

    steps.append(probe("vector_light_field_texture_uploads", "550", "550"))
    steps.append(probe("vector_light_field_uv_only_uploads", "550", "550"))
    # The same shape for the other flag. Measured: 13.32 / 12.82 / 12.49 with the hold off against
    # 3.33 / 3.25 / 3.32 with it on -- 3.90x -- and the memo-only arm sits with the hold-off arms.
    steps.append(probe("vector_light_upload_field_ms", "9", "9"))

    # THE CONTROL FOR THE HOLD. Mesh channel writes happen on every rebuild whatever the texture
    # does, so this must not move with either flag.
    # Measured 11.10-12.32 across all six arms, no pattern in the flags. Pinned TIGHTER than the two
    # above precisely because it is a control: a control with a tolerance wide enough to swallow the
    # effect it is controlling for is not a control.
    steps.append(probe("vector_light_upload_mesh_ms", "12", "10"))

    # THE CONTROL, and the reason both clocks exist. The memo removes work from the gather and changes
    # nothing about the bake, so this should read the same in every arm. If it moves with the flag,
    # something other than the gather changed and the gather number is not what it claims to be.
    steps.append(probe("vector_light_bake_wall_ms", "180", "180"))

    # How much work there was to do, so the durations above can be read against a population rather
    # than in the abstract. vector_light_bakes is the denominator of the bake clock and
    # invalidation_marks is roughly the denominator of the gather clock.
    steps.append(probe("vector_light_bakes", "600", "600"))
    steps.append(probe("vector_light_invalidations", "90", "90"))
    steps.append(probe("vector_light_invalidation_marks", "700", "700"))
    steps.append(probe("door_aperture_bakes", "90", "90"))
    return steps


steps = setup()

# A 2x2 WITH REPEATS, ONE FACTOR MOVING AT A TIME, rather than sweeping both flags together.
#
# The two changes are orthogonal BY CONSTRUCTION -- the silhouette memo touches the gather and the
# glow-texture hold touches the upload, and each has its own clock -- so sweeping them together
# would probably have been fine. Probably is not a measurement. Arms 2 and 3 move one factor each
# against arm 1, which is what turns "each flag moves only its own clock" from an argument about the
# code into something the report either shows or does not.
#
# Arm 1 is the full baseline: both flags off, i.e. the shape that shipped before this branch. Arm 4
# is both on, i.e. what ships after it. Arms 5 and 6 repeat 1 and 4 at the end of the run, so the
# headline comparison is bracketed and a machine that got slower or quieter partway through is
# visible rather than absorbed.
ARM_NAMES = {
    (False, False): "baseline",
    (True, False): "memo",
    (False, True): "hold",
    (True, True): "both",
}

ARMS = [
    (False, False),   # 1  baseline
    (True, False),    # 2  silhouette memo only
    (False, True),    # 3  glow texture hold only
    (True, True),     # 4  both
    (False, False),   # 5  baseline again
    (True, True),     # 6  both again
]

for index, (cache_on, hold_on) in enumerate(ARMS, start=1):
    steps += arm(cache_on, hold_on, index)

out = {
    "name": "vector_light_door_storm",
    "saveFile": "minimal_colony.rws",
    "description": (
        "Issue #188 item C and the glow-texture hold, measured together on the provocation they both "
        "exist for: door swings. One wooden door in a room's east wall, eight TorchLamps all within "
        "its reach, and " + str(SWINGS) + " open/close swings per arm, over SIX arms in ONE "
        "process -- a 2x2 of "
        "vector_light_silhouette_cache and vector_light_glow_texture_hold, one factor moving at a "
        "time, with the baseline and the both-on arm repeated at the end. Interleaved rather than "
        "blocked because this box has measured one unchanged binary spanning 37-85 ms on "
        "frame_max_ms across three runs, and vector_light_bake_wall_ms moving 40% between two runs "
        "of the same build. "
        "THREE CLOCKS, AND EACH FLAG SHOULD MOVE EXACTLY ONE. The memo touches the gather "
        "(vector_light_gather_wall_ms); the hold touches the texture half of the upload "
        "(vector_light_upload_field_ms). vector_light_bake_wall_ms and vector_light_upload_mesh_ms "
        "must not move with either, and are the controls. "
        "READ THE RATIOS, NOT THE TOTALS. A door animates on the tick counter while the harness "
        "renders frames, so how many quantisation steps a swing crosses is machine-dependent and the "
        "counts drift between arms. "
        "The two exact pins are that an off arm reports exactly zero silhouette hits and exactly "
        "zero UV-only uploads, because those are statements about code paths rather than durations, "
        "and an off arm reporting one would mean every ratio here compares an arm against itself. "
        "WHY THE HOLD IS LEGITIMATE: the shipped mod never writes vanilla's glow grid when a door "
        "opens -- the only two writes are behind vector_light_door_glow_blocker, which ships off -- "
        "so the per-emitter glow texture is byte-for-byte unchanged across a swing. "
        "Clouds are off: the sheet drifts on the tick counter and would put the arms' screenshots "
        "under different weather."
    ),
    "steps": steps,
}

with open(TARGET, "w") as f:
    json.dump(out, f, indent=2)
    f.write("\n")

print(f"wrote {TARGET}: {len(steps)} steps, {SWINGS} swings x {len(ARMS)} arms")
