#!/usr/bin/env python3
"""Derives vector_light_column_perf.json from vector_light_mask_max_perf.json's scene.

The scene is the same 20-lamp, five-room plate phase 5's cost was measured on; only the arms differ.
Copied rather than re-authored so the two cost figures are directly comparable — a different plate
would make phase 5b's number incomparable with the one already in DESIGN.md.
"""
import json, os

W = "/home/deck/Developer/RimWorldMods/.worktrees/CelestialLighting-vl-column"
src = json.load(open(f"{W}/Tests/Scenarios/vector_light_mask_max_perf.json"))

# Steps 0..9 are the scene, the clock, the camera and the emitter-count pin.
scene = src["steps"][:10]


def step(t, **args):
    return {"type": t, "args": {k: str(v) for k, v in args.items()}}


BASE = [
    ("vector_light_blend", False),
    ("vector_light_penumbra", True),
    ("vector_light_suppress", True),
    ("vector_lights", True),
    ("vector_light_mask", True),
    ("vector_light_mask_beam", False),
    ("vector_light_mask_max", False),
    ("vector_light_shader_max", False),
]

steps = list(scene)
for name, on in BASE:
    steps.append(step("SetFeature", featureName=name, enabled="true" if on else "false"))

# ALTERNATED, NOT BLOCKED. The two arms take turns so a drift over the run — a background job, the
# save autosaving, thermal throttle — cannot be read as the flag's cost.
for i in (1, 2):
    for arm, on in (("plain", False), ("corrected", True)):
        steps.append(step("SetFeature", featureName="vector_light_mask_saturation",
                          enabled="true" if on else "false"))
        steps.append(step("Profile", name=f"rebake_{arm}_{i}", prefix="CelestialLighting",
                          frames=240, timeSpeed="superfast"))

out = {
    "name": "vector_light_column_perf",
    "saveFile": src["saveFile"],
    "description": (
        "§27 phase 5b's cost, on the plate phase 5's cost was measured on. The correction runs "
        "during a SECTION REGENERATE and not per frame, so each window opens immediately after the "
        "SetFeature that dirties the map and therefore contains exactly one whole-map rebake of the "
        "same 20-lamp scene. Arms alternate so drift over the run cannot be read as the flag's cost. "
        "What is being paid for: a second walk over every emitter on the map per section that "
        "carries an edit, plus a fix-up pass over the section's cells - both skipped outright on a "
        "section fewer than two emitters reach, since one emitter's own light is a Color32 and "
        "cannot saturate anything. This plate reaches every section with at least two lamps, so that "
        "short-circuit never fires here and the numbers are the worst case rather than the typical "
        "one. Inherits vector_light_mask_max_perf.json's own wart: the terrain rect straddles four "
        "SteamGeyser cells that SetTerrain cannot clear, which makes the run report Pass=false while "
        "changing nothing about the lighting - kept identical to that scenario's rect on purpose, so "
        "the two cost figures are comparable."
    ),
    "steps": steps,
}

path = f"{W}/Tests/Scenarios/vector_light_column_perf.json"
open(path, "w").write(json.dumps(out, indent=2) + "\n")
print("wrote", path, len(steps), "steps")
