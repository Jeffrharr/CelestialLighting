#!/usr/bin/env python3
"""Mesh storm: vector_light_bake_storm's provocation, run as a 2x2 over the mesh upload's two flags.

WHY A SEPARATE FILE RATHER THAN RUNNING THE BAKE STORM TWICE. This box has measured one unchanged
binary spanning 37 to 85 ms on frame_max_ms across three runs an hour apart, and PR #204's arms moved
untouched controls by a factor of two between runs while staying flat to ~1.6% WITHIN one run. So a
number from run A and a number from run B are not comparable at the scale being measured here, and
this scale is small: the whole quantity under test is 14 ms spread over 880 uploads. Every arm has to
live in one process, interleaved, with a repeat of the baseline and of the treatment so the drift
across the run is visible rather than assumed.

WHAT IT PROVOKES. Identical to vector_light_bake_storm -- ForceRebuild drops all 22 meshes, so every
pass re-uploads every one of them. That is a WORST CASE for the upload clock and is exactly what is
wanted: a steady-state colony uploads on two frames in 480, where a saving of any size is
unmeasurable.

THE 2x2 IS 2x2 BECAUSE THE TWO FLAGS ARE NOT OBVIOUSLY SEPARABLE. Both change the same two Unity
calls -- vector_light_upload_bounds changes whether they recalculate the bounding box,
vector_light_upload_direct changes whether the triangles arrive as an array or a List. If the bounds
scan dominates, the triangle copy will read as nothing; if the two interact through the same native
entry point, a diagonal (off/off vs on/on) could not tell which. Arms 2 and 3 are what make the
answer attributable rather than a bundle.

READ THE SCREENSHOTS FOR ONE SPECIFIC FAILURE. Graphics.DrawMesh frustum-culls against the bounds
this change now states rather than measures, so the way a wrong box presents is a light VANISHING,
not a light drawn wrongly. No probe here can see that -- vector_light_verts and vector_light_lit_area
both describe geometry that was built, not geometry that was drawn -- so the frames are the only
instrument for it, and they are load-bearing rather than decorative.

WHAT IT ANSWERED, SO NOBODY RE-RUNS IT EXPECTING A WIN. Both flags ship OFF. Pooled over two runs:
baseline 13.03 ms, both flags on 13.10 ms, over 880 uploads each -- +0.6%, the wrong sign, against a
baseline spread of 12% between arms of IDENTICAL configuration. The meshes are ~172 vertices and an
upload costs ~15.9 us, so the four native call transitions are the cost and the per-vertex work
inside them is not. This file is kept as the instrument that established that, and as the thing to
re-run on a quieter machine before anyone believes otherwise.

    python3 Tools/ScenarioGen/gen_vector_light_frame_cost.py
    python3 Tools/ScenarioGen/gen_vector_light_mesh_storm.py
"""

import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
SCEN = os.path.abspath(os.path.join(HERE, "..", "..", "Tests", "Scenarios"))

SOURCE = os.path.join(SCEN, "vector_light_frame_cost.json")
TARGET = os.path.join(SCEN, "vector_light_mesh_storm.json")

# Matched to vector_light_bake_storm rather than chosen afresh, so arm 1 here is directly comparable
# with that scenario's single arm -- which is where the 14.04 ms this change targets was measured.
# An arm that does not reproduce it is an arm measuring something else.
REBUILDS = 40

INERT_TOGGLE = "vector_light_mask_max"
INERT_VALUE = "false"

BOUNDS_FLAG = "vector_light_upload_bounds"
DIRECT_FLAG = "vector_light_upload_direct"

# MEASURED, NOT DERIVED. Both read these exact values in all six arms of both runs -- they do not
# agree within a tolerance, they are the same float -- which is the strongest statement available
# that the flags change only how the geometry is handed over and not what it is. The tolerances are
# zero for the same reason: anything that moves them is a behaviour change, not drift.
VERTS = 3775
VERTS_TOLERANCE = "0"
LIT_AREA = 2829.1687
LIT_AREA_TOLERANCE = "0.001"

ARM_NAMES = {
    (False, False): "baseline",
    (True, False): "bounds",
    (False, True): "direct",
    (True, True): "both",
}

ARMS = [
    (False, False),   # 1  baseline
    (True, False),    # 2  stated bounds only
    (False, True),    # 3  direct triangle upload only
    (True, True),     # 4  both
    (False, False),   # 5  baseline again -- the drift control
    (True, True),     # 6  both again
]


def step(t, **args):
    return {"type": t, "args": {k: str(v) for k, v in args.items()}}


def probe(name, expected, tolerance):
    return step("Probe", probeName=name, expectedValue=expected, tolerance=tolerance)


def is_reset_probe(s):
    return s.get("type") == "Probe" and (s.get("args") or {}).get("probeName") == "vector_light_bake_reset"


source = json.load(open(SOURCE))

# The prefix, up to and including the counter reset: the tile, the plate, the flags, the colonists and
# the Circinus arming block. Read from the generated file rather than copied for the same reason
# vector_light_bake_storm reads it -- a copy drifts the first time somebody edits the other one.
prefix = []
for s in source["steps"]:
    prefix.append(s)
    if is_reset_probe(s):
        break
else:
    raise SystemExit("no vector_light_bake_reset step in " + SOURCE)

# The inert toggle has to be genuinely inert or the storm changes the scene as it runs. Asserted
# rather than trusted, exactly as the bake storm asserts it.
setting = [
    (s.get("args") or {}).get("enabled")
    for s in prefix
    if s.get("type") == "SetFeature" and (s.get("args") or {}).get("featureName") == INERT_TOGGLE
]
if setting[-1:] != [INERT_VALUE]:
    raise SystemExit(
        f"{INERT_TOGGLE} is {setting[-1:]} in the source scenario, expected [{INERT_VALUE!r}] -- "
        "pick a different inert toggle or this storm changes the scene as it runs")

# NEITHER FLAG MAY APPEAR IN THE INHERITED PREFIX. If the source scenario ever starts setting one of
# them, every arm below would be overridden by whatever it set and the 2x2 would silently become six
# copies of one configuration -- passing, and measuring nothing. This repo has already committed a
# frame that read 17.08 instead of 20.23 because an arm inherited a flag it did not state.
for flag in (BOUNDS_FLAG, DIRECT_FLAG):
    if any(s.get("type") == "SetFeature" and (s.get("args") or {}).get("featureName") == flag
           for s in prefix):
        raise SystemExit(f"{flag} is set in {SOURCE}; this scenario's arms would not control it")

steps = list(prefix)

# The first capture of a scenario carries the HUD -- hideUi is honoured from the second onward -- so
# one is spent here rather than inside an arm, where it would put the message log into a frame
# somebody is trying to compare.
steps.append(step("Screenshot", fileName="meshstorm_warmup_discard.png"))


def arm(bounds_on, direct_on, index):
    """One arm: state both flags, storm the rebuild, then read the clocks."""
    tag = f"{index}_{ARM_NAMES[(bounds_on, direct_on)]}"
    out = []

    # BOTH FLAGS STATED IN EVERY ARM, including the ones where a flag is set to what it already is.
    # An arm that only names the flag it is varying inherits the other from its predecessor, which
    # makes arm order part of the measurement.
    out.append(step("SetFeature", featureName=BOUNDS_FLAG,
                    enabled="true" if bounds_on else "false"))
    out.append(step("SetFeature", featureName=DIRECT_FLAG,
                    enabled="true" if direct_on else "false"))

    # After both flags, so the rebuild each of them fires is charged to nobody. The harness runs one
    # step per frame, so a frame really is rendered between the two SetFeatures above and the second
    # one's ForceRebuild is what lands immediately before this.
    out.append(probe("vector_light_bake_reset", "0", "0"))

    for _ in range(REBUILDS):
        out.append(step("SetFeature", featureName=INERT_TOGGLE, enabled=INERT_VALUE))

    # One tick-advancing window so the final rebuild's upload happens inside the measured window
    # rather than after the probes read. Every argument spelled out: they are all optional, the
    # defaults are expensive (omitting `steps` expands this into 120 screenshot frames), and the
    # harness does not reject an argument it does not know -- so a misspelling is silently the
    # default rather than an error.
    out.append(step("TickLapse", ticks="1", steps="8",
                    fileNamePrefix=f"meshstorm_{tag}_discard", fps="20"))

    out += arm_probes()
    out.append(step("Screenshot", fileName=f"meshstorm_{tag}.png"))
    return out


def arm_probes():
    out = []

    # ---- the behaviour half. IDENTICAL ACROSS ALL SIX ARMS OR THE REST IS WORTHLESS -------------
    #
    # This is a pure performance change: the same vertices, the same indices and the same polygon
    # must reach Unity whichever way they get there. vector_light_verts is the one that speaks to the
    # mesh specifically -- a triangle array uploaded through the wrong overload, or a channel written
    # with the wrong length, shows up here as a different vertex count.
    #
    # RECORDED ON THE FIRST GREEN RUN, NOT DERIVED. A pin computed from the arithmetic under test
    # asserts that the arithmetic equals itself.
    out.append(probe("vector_light_count", "22", "0"))
    out.append(probe("vector_light_bake_batch_max", "22", "0"))

    # The storm landed. Exact because it measures exact: one step is one frame and one frame is one
    # rebuild. If this fails the storm is not storming and every duration below covers a different
    # amount of work than it claims.
    out.append(probe("vector_light_bakes", str(REBUILDS * 22), "0"))

    # The geometry itself, which must not move at all -- the pins that would catch a triangle array
    # uploaded through the wrong overload, or a channel written with the wrong length. Exact,
    # because they measured exact across twelve arms.
    out.append(probe("vector_light_verts", str(VERTS), VERTS_TOLERANCE))
    out.append(probe("vector_light_lit_area", str(LIT_AREA), LIT_AREA_TOLERANCE))

    # ---- the measurement half. RECORDED, NOT ASSERTED -------------------------------------------
    #
    # Every duration here is pinned on a tolerance wide enough that no arm can fail on speed alone,
    # because the answer this scenario exists to produce is the RATIO between arms in one report and
    # no single pin can express that. What the pins are for is catching a run where the work did not
    # happen at all. Read the six reports' upload_mesh_ms side by side.
    #
    # THE HEADLINE IS upload_mesh_ms. The other three are controls: nothing in this change touches
    # the gather, the bake, or the per-emitter glow texture, so an arm that moves them is measuring
    # the machine rather than the flags -- which is the whole reason arm 5 repeats arm 1.
    out.append(probe("vector_light_upload_mesh_ms", "150", "150"))
    out.append(probe("vector_light_upload_field_ms", "150", "150"))
    out.append(probe("vector_light_upload_wall_ms", "150", "150"))
    out.append(probe("vector_light_gather_wall_ms", "150", "150"))
    out.append(probe("vector_light_bake_wall_ms", "240", "220"))

    # THE FRAME-LEVEL QUESTION. A total cannot show a saving on the frames that rebuild; a maximum
    # can, because the frames that rebuild are the worst frames. circ_vldraw_max_ms is the number
    # this change would have to move to be worth shipping -- 8.36 ms on the bake storm, against
    # which the whole mesh upload is about 4% of a worst frame.
    for name in ("circ_vldraw", "circ_vlbuilddirty", "circ_vloverlay",
                 "circ_vlpolygon", "circ_vlcoverage", "circ_vlsegments"):
        out.append(probe(name + "_max_ms", "20", "80"))
        out.append(probe(name + "_total_ms", "200", "800"))

    return out


for index, (bounds_on, direct_on) in enumerate(ARMS, start=1):
    steps += arm(bounds_on, direct_on, index)

out = {
    "name": "vector_light_mesh_storm",
    "saveFile": source["saveFile"],
    "description": (
        "The mesh half of the vector-light geometry upload, measured as an interleaved 2x2 over "
        f"{BOUNDS_FLAG} and {DIRECT_FLAG}. Inherits vector_light_frame_cost's plate, flags and "
        f"Circinus arming verbatim, then runs six arms of {REBUILDS} forced rebuilds each -- "
        "baseline, bounds only, direct only, both, baseline again, both again. Every rebuild drops "
        "all 22 meshes and re-uploads them, which is a worst case for the upload clock and "
        "deliberately so: a steady-state colony uploads on two frames in 480. "
        "ALL SIX ARMS LIVE IN ONE PROCESS BECAUSE THIS BOX MOVES BETWEEN RUNS -- one unchanged "
        "binary has spanned 37 to 85 ms on frame_max_ms across three runs, while staying flat to "
        "~1.6% within a single run, so arms 5 and 6 repeat arms 1 and 4 to make that drift visible "
        "rather than assumed. THE HEADLINE IS vector_light_upload_mesh_ms; gather, bake and "
        "upload_field are controls that this change does not touch and an arm that moves them is "
        "measuring the machine. Durations are recorded on wide tolerances rather than asserted, "
        "because the answer is a ratio between arms in one report and no single pin can express it. "
        "THE SCREENSHOTS ARE LOAD-BEARING: stated bounds are what Graphics.DrawMesh frustum-culls "
        "against, so a wrong box makes a light VANISH, and no probe here can see that -- "
        "vector_light_verts and vector_light_lit_area both describe geometry that was built rather "
        "than geometry that was drawn. "
        "WHAT IT ANSWERED: both flags ship OFF. Pooled over two runs, baseline 13.03 ms against "
        "13.10 ms with both on -- +0.6%, the wrong sign, against a 12% spread between arms of "
        "identical configuration. Re-run it before believing otherwise; do not expect a win."
    ),
    "requiredMods": source["requiredMods"],
    "steps": steps,
}

with open(TARGET, "w") as f:
    json.dump(out, f, indent=2)
    f.write("\n")

print(f"wrote {TARGET}: {len(steps)} steps, {len(ARMS)} arms x {REBUILDS} rebuilds")
