#!/usr/bin/env python3
"""Generates Tests/Scenarios/aurora_frame_cost.json — issue #60's measurement scenario.

Generated rather than hand-written because the whole design is three phases that differ ONLY in
how many game ticks elapse per rendered frame, and expressing a tick:frame ratio in the harness's
vocabulary takes an AdvanceTicks/Wait pair per tick. That is ~750 steps of JSON whose only
interesting property is a ratio, which is exactly the kind of file nobody should be diffing.

The three phases ARE the measurement:

  A  clock frozen       0 ticks/frame  — the tick gate blocks every bake, so this is the fixed
                                         per-frame cost on its own
  B  1 tick / ~5 frames                — roughly normal play at a high framerate
  C  1 tick / ~3 frames                — the same path, baking more often

Cost is linear in bakes-per-frame, so three points on that line separate the fixed per-frame cost
(the intercept) from the bake (the slope) without having to trust any single reading. Every probe
here is a measurement rather than a gate — the tolerance is deliberately enormous — except
aurora_path_missing_stages, which asserts every stage really was patched, because an unpatched
stage reports zero and a zero would read as "free".
"""

import json
import os

READ = [
    "aurora_path_frames",
    "aurora_path_total_us",
    "aurora_path_strength_us",
    "aurora_path_driver_us",
    "aurora_path_advance_us",
    "aurora_path_bake_us",
    "aurora_path_upload_us",
    "aurora_path_place_us",
    "aurora_path_draw_us",
    "aurora_path_setsheet_us",
    "aurora_path_drawsheet_us",
    "aurora_path_driver_per_frame",
    "aurora_path_bake_per_frame",
    "aurora_path_upload_per_frame",
    "aurora_path_setsheet_per_frame",
    "aurora_path_drawsheet_per_frame",
    "aurora_path_bake_us_per_call",
    "aurora_path_table_us_per_call",
    "aurora_path_table_per_frame",
    "aurora_path_fillrows_us_per_call",
    "aurora_path_upload_us_per_call",
    "aurora_path_total_us_max",
    "aurora_path_bake_us_max",
    "aurora_path_table_us_max",
    "aurora_path_upload_us_max",
    "aurora_path_overhead_us",
]


def step(t, **args):
    return {"type": t, "args": {k: str(v) for k, v in args.items()}}


def probe(name, expected=0, tol=1_000_000_000):
    return step("Probe", probeName=name, expectedValue=expected, tolerance=tol)


def read_all():
    return [probe(n) for n in READ]


def ticked(ticks, frames_per_tick):
    out = []
    for _ in range(ticks):
        out.append(step("AdvanceTicks", ticks=1))
        out.append(step("Wait", frames=frames_per_tick))
    return out


steps = [
    step("SetTile", latitude=20),
    step("SetSeason", dayOfYear=40),
    step("SetTime", hour=1),
    step("StartCondition", conditionDef="Aurora", durationHours=24, agedHours=2),
    # Warm: allocate the textures, JIT every stage, and fill the field so no phase below is
    # measuring a first-time allocation or a cold sweep.
    step("Wait", frames=120),
]
steps += ticked(40, 3)
# Every stage must actually be patched, or a zero below would read as "free" when it means
# "never measured". The one real assertion in this scenario.
steps.append(probe("aurora_path_missing_stages", expected=0, tol=0.5))

# Phase A: 300 rendered frames with the clock frozen. The tick gate blocks every bake, so this is
# the ungated per-frame path on its own - the cost issue #60 says is paid on every frame.
steps.append(probe("aurora_path_reset"))
steps.append(step("Wait", frames=300))
steps += read_all()

# Phase B: 100 ticks, three Wait frames each. The AdvanceTicks step renders frames of its own, so
# this lands at ~5 frames per tick in practice rather than 3 — near the ~196 fps / TPS 59 ratio the
# Dubs profile in issue #60 was taken at. The ratio is measured (aurora_path_bake_per_frame), not
# assumed, precisely because the step vocabulary does not let it be dialled in exactly.
steps.append(probe("aurora_path_reset"))
steps += ticked(100, 3)
steps += read_all()

# Phase C: the same ticks with one Wait frame each, landing at ~3 frames per tick. Baking is thus
# ~1.7x as frequent as in phase B, which is what turns two points into a line and lets the bake be
# separated from the fixed cost by regression rather than by assertion.
steps.append(probe("aurora_path_reset"))
steps += ticked(200, 1)
steps += read_all()

scenario = {
    "name": "aurora_frame_cost",
    "saveFile": "minimal_colony.rws",
    "steps": steps,
}

path = (os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..") + "/"
        "Tests/Scenarios/aurora_frame_cost.json")
with open(path, "w") as f:
    json.dump(scenario, f, indent=2)
print(path, len(steps), "steps")
