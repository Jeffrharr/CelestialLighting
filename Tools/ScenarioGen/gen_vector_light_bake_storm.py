#!/usr/bin/env python3
"""Bake storm: the same plate as vector_light_frame_cost, provoked into baking on every frame.

WHY THIS EXISTS. vector_light_frame_cost spans about 480 frames and bakes on TWO of them, which is
the honest steady state of a colony and the wrong shape for measuring a bake. A saving of a few
milliseconds on two frames out of 480 cannot be seen in any total, and the repo has the scar to prove
it: the coverage work landed offline at 1.21x and was unmeasurable live, and the threaded bake had to
grow its own stopwatch (vector_light_bake_wall_ms) because no arm in the Circinus bank could see a
join. This file is the other half of that fix -- rather than a better instrument, more of the event
worth instrumenting.

HOW IT PROVOKES A BAKE. FeatureRegistry's callbacks call VectorLightRedraw.ForceRebuild, which calls
VectorLightField.ClearAll -- so every emitter is dropped and rebuilt on the next draw. The toggle used
is `vector_light_mask_max`, set to the value it ALREADY HAS in the inherited setup, so the scene's
configuration never changes and only the rebuild fires. That matters: this repo has already shipped a
committed frame that measured 17.08 instead of 20.23 because an arm inherited a flag it did not state.

WHY IT IS DERIVED FROM THE OTHER SCENARIO RATHER THAN COPIED. Everything up to and including the
counter reset -- the tile, the plate, the twenty-odd feature flags, the colonists, the Circinus arming
block -- has to be IDENTICAL for the two files' numbers to be comparable, and a copy would drift the
first time somebody edits one. So this reads the generated frame_cost scenario and keeps its prefix
verbatim. Regenerate that one first.

    python3 Tools/ScenarioGen/gen_vector_light_frame_cost.py
    python3 Tools/ScenarioGen/gen_vector_light_bake_storm.py
"""

import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
SCEN = os.path.abspath(os.path.join(HERE, "..", "..", "Tests", "Scenarios"))

SOURCE = os.path.join(SCEN, "vector_light_frame_cost.json")
TARGET = os.path.join(SCEN, "vector_light_bake_storm.json")

# How many rebuilds to provoke. Each is one step and the harness runs one step per frame, so this is
# also how many bake-heavy frames the window contains.
#
# FORTY RATHER THAN FOUR HUNDRED. The quantity being sharpened is a MAXIMUM and a per-pass mean, not a
# total, so the return on more passes falls away quickly once the distribution is sampled -- and every
# pass drops and rebuilds twenty-two meshes, so a long storm turns a two-minute run into a slow one
# for numbers that stop moving. Forty puts twenty times more bake frames in the window than the
# scenario it derives from.
REBUILDS = 40

# The toggle to fire. Chosen because the inherited setup already sets it false, so re-setting it false
# changes nothing about what is being measured and only triggers the rebuild.
INERT_TOGGLE = "vector_light_mask_max"
INERT_VALUE = "false"


def step(t, **args):
    return {"type": t, "args": {k: str(v) for k, v in args.items()}}


def probe(name, expected, tolerance):
    return step("Probe", probeName=name, expectedValue=expected, tolerance=tolerance)


def is_reset_probe(s):
    return s.get("type") == "Probe" and (s.get("args") or {}).get("probeName") == "vector_light_bake_reset"


source = json.load(open(SOURCE))

# The prefix, up to and including the counter reset. Everything after it in the source file is that
# scenario's own two-toggle window and its pins, which this file replaces.
steps = []
for s in source["steps"]:
    steps.append(s)
    if is_reset_probe(s):
        break
else:
    raise SystemExit("no vector_light_bake_reset step in " + SOURCE)

# Assert the toggle really is inert here rather than trusting the comment: if somebody flips it on in
# the source scenario, this file would start changing configuration between frames and its numbers
# would quietly stop being comparable.
setting = [
    (s.get("args") or {}).get("enabled")
    for s in steps
    if s.get("type") == "SetFeature" and (s.get("args") or {}).get("featureName") == INERT_TOGGLE
]
if setting[-1:] != [INERT_VALUE]:
    raise SystemExit(
        f"{INERT_TOGGLE} is {setting[-1:]} in the source scenario, expected [{INERT_VALUE!r}] -- "
        "pick a different inert toggle or this storm changes the scene as it runs")

for _ in range(REBUILDS):
    steps.append(step("SetFeature", featureName=INERT_TOGGLE, enabled=INERT_VALUE))

# One tick-advancing window at the end so the final rebuild's bake actually happens inside the
# measured window rather than after the last probe reads.
#
# EVERY ARGUMENT SPELLED OUT, because they are all optional and the defaults are expensive: omitting
# `steps` expands this into 120 screenshot frames. The harness does not reject an argument it does not
# know, so a misspelling here is silently the default rather than an error -- `frames`/`stepTicks` was
# the first attempt and would have quietly filmed the whole storm.
steps.append(step("TickLapse", ticks="1", steps="8",
                  fileNamePrefix="vlstorm_discard", fps="20"))

# ---- what is asserted ------------------------------------------------------------------------
#
# COUNTS EXACT, DURATIONS RECORDED, exactly as the source scenario does it. The counts are what make
# the durations readable: a bake wall time is meaningless without knowing how many passes it covers,
# and a pass count that drifts between arms means the two arms did different amounts of work.
steps.append(probe("vector_light_count", "22", "0"))
steps.append(probe("vector_light_bake_batch_max", "22", "0"))

# The storm landed, and this is an EXACT pin because it measured exact: 880 in all four runs of the
# first A/B, both arms, no drift. A rebuild collapsing into its neighbour was the reason to expect
# slack here and it does not happen -- one step is one frame and one frame is one rebuild -- so the
# slack is not taken. If this ever fails, the storm is not storming and every duration below is
# measuring a different amount of work than it claims.
steps.append(probe("vector_light_bakes", str(REBUILDS * 22), "0"))

# WHICH PATH RAN. Pinned loosely and read as a pair rather than asserted exactly: the split depends on
# the flag, and this file is meant to be run on both sides of it.
steps.append(probe("vector_light_parallel_bakes", str(REBUILDS // 2), str(REBUILDS)))
steps.append(probe("vector_light_serial_bakes", str(REBUILDS // 2), str(REBUILDS)))

# THE HEADLINE. The calling thread's own time in the bake, summed over every pass -- the only number
# in the bank a threaded bake can move, because the Circinus arms report time exclusive of their armed
# children and therefore cannot see a join.
#
# ONE PIN SPANNING BOTH ARMS, DELIBERATELY, and it is why the tolerance looks absurd. Measured here:
# serial 403.1 and 288.5, threaded 72.4 and 73.3. This file is meant to be run on both sides of
# vector_light_parallel_bake, so a pin tight enough to be interesting on one arm fails on the other.
# The number that matters is the RATIO between two runs of this file, which no single pin can express
# -- read the two reports side by side. What the pin is for is catching a run where the bake did not
# happen at all.
steps.append(probe("vector_light_bake_wall_ms", "240", "220"))

# THE OTHER TWO THIRDS OF THE SAME FRAME, added when the storm was turned on the question PR #203
# left open: it found the worst frame unmoved by threading and named mesh upload as the suspect,
# which was an inference from a number that did not move rather than a measurement of anything.
# These three clocks partition the work -- read the map, do arithmetic on it, hand it to Unity -- so
# the suspect can be weighed rather than nominated.
#
# THE STORM IS THE RIGHT PROVOCATION FOR THE UPLOAD CLOCK AND THE WRONG ONE FOR THE GATHER CLOCK, and
# both matter. ForceRebuild drops every mesh, so every pass re-uploads all 22; it also drops every
# silhouette memo, so every gather is a rescan and the memo can never help here. The gather number
# below is therefore a WORST CASE for it, not a steady state -- vector_light_door_storm is where that
# is measured.
steps.append(probe("vector_light_gather_wall_ms", "150", "150"))
steps.append(probe("vector_light_upload_wall_ms", "150", "150"))
steps.append(probe("vector_light_upload_mesh_ms", "150", "150"))
steps.append(probe("vector_light_upload_field_ms", "150", "150"))

# THE FRAME-LEVEL QUESTION, and the reason this file exists rather than a longer run of the other one.
# A total over hundreds of frames cannot show a saving on the few that bake; a MAXIMUM can, because
# the frames that bake are the worst frames. With forty of them in the window the maximum is sampled
# repeatedly instead of twice.
for arm in ("circ_vldraw", "circ_vlbuilddirty", "circ_vloverlay",
            "circ_vlpolygon", "circ_vlcoverage", "circ_vlsegments"):
    steps.append(probe(arm + "_max_ms", "20", "80"))
    steps.append(probe(arm + "_total_ms", "200", "800"))

out = {
    "name": "vector_light_bake_storm",
    "saveFile": source["saveFile"],
    "description": (
        "The vector-light bake provoked on every frame, so a change to it can be measured. "
        f"Inherits vector_light_frame_cost's plate, flags and Circinus arming verbatim, then fires "
        f"{REBUILDS} inert {INERT_TOGGLE} toggles -- each calls ForceRebuild, which drops every "
        "emitter and rebakes all 22 on the next draw. The scenario it derives from spans ~480 frames "
        "and bakes on two of them, which is the right shape for a steady-state colony and the wrong "
        "one for measuring a bake: a saving on two frames in 480 disappears into any total. "
        "READ THE MAXIMA, NOT THE TOTALS. The frames that bake are the worst frames, so a maximum is "
        "where a bake change surfaces at frame level; totals here are dominated by the ~440 frames "
        "that bake nothing. vector_light_bake_wall_ms is the calling thread's own time in the bake "
        "and is the only number here a threaded bake can move -- the Circinus arms report time "
        "EXCLUSIVE of their armed children (measured: circ_vlbuilddirty reads 1.3-1.5 ms in a window "
        "where the Build and BuildCoverage arms inside it read 6-9 ms each), so they are blind to a "
        "join by construction. Run it on both sides of vector_light_parallel_bake; counts are exact "
        "and every duration is recorded rather than asserted, because this box moves untouched "
        "control arms by a factor of two between runs."
    ),
    "requiredMods": source["requiredMods"],
    "steps": steps,
}

with open(TARGET, "w") as f:
    json.dump(out, f, indent=2)
    f.write("\n")

print(f"wrote {TARGET}: {len(steps)} steps, {REBUILDS} provoked rebuilds")
