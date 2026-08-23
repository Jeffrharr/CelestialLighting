#!/usr/bin/env python3
"""Derives vector_light_frame_cost.json from vector_light_perf.json's scene.

Same 20-lamp, five-room plate every other vector-light cost figure in DESIGN.md was measured on,
because a different plate would make this table incomparable with those. Only the instrument
differs: vector_light_perf.json is a Dubs window on Patch_VectorLightDraw:Postfix, which reports a
duration for the whole subsystem and cannot say how many times anything inside it ran. This one
arms the path through Circinus and reads CALL COUNTS.

Run from anywhere; writes into the worktree this file lives in.
"""
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
SCEN = os.path.join(ROOT, "Tests", "Scenarios")

src = json.load(open(os.path.join(SCEN, "vector_light_perf.json")))

# Steps 0..8 are the terrain, the two wall passes, the lamps, the clock and the camera. Step 9 is
# that scenario's emitter-count pin, which is re-stated below next to the rest of ours.
scene = src["steps"][:9]


def step(t, **args):
    return {"type": t, "args": {k: str(v) for k, v in args.items()}}


def feature(name, on):
    return step("SetFeature", featureName=name, enabled="true" if on else "false")


def probe(name, expected, tolerance):
    return step("Probe", probeName=name, expectedValue=expected, tolerance=tolerance)


# Every cloud lane off. They ship on and the sheet drifts on the tick counter, so leaving them in
# would put a moving, expensive draw inside a window whose whole purpose is to attribute frame time
# to one subsystem. Stated rather than implied: partial cover during Clear weather is our own
# feature, so SetWeather Clear does not get a clear sky.
CLOUDS = [
    "cloud_cover", "cloud_sheet", "cloud_presence", "cloud_deck_varieties", "cloud_volume",
]

# The configuration a player who switches vector lighting on actually gets, stated in full rather
# than inherited from the defaults. The point of this scenario is the shipped path's cost, and a
# flag whose default moves later must not silently change what was measured here.
SHIPPED = [
    ("vector_lights", True),
    ("vector_light_penumbra", True),
    ("vector_light_suppress", True),
    ("vector_light_blend", True),
    ("vector_light_mask", True),
    ("vector_light_mask_beam", True),
    ("vector_light_mask_saturation", True),
    ("vector_light_shader_max", True),
    ("vector_light_shader_max_subtract", True),
    ("vector_light_mask_max", False),
    ("vector_light_pawn_shadows", True),
    ("vector_light_shadow_shares", True),
    ("vector_light_shadow_ground_shares", True),
    ("vector_light_shadow_shape", True),
    ("vector_light_shadow_clip", True),
    ("vector_light_shadow_feather", True),

    # OFF, AND STATED RATHER THAN ASSUMED. These three ship off, but the dev install's persisted
    # ModSettings XML carries vectorLightOpenDoors=True and survives every harness run untouched —
    # so a scenario that leaves them implicit measures whatever the last person to open the settings
    # screen left behind. They change the segment set a bake is handed, which is the quantity this
    # file exists to count.
    ("vector_light_open_doors", False),
    ("vector_light_door_aperture", False),
    ("vector_light_door_glow_blocker", False),
]

# Armed methods, in the order they are entered. Every one gets its `_patched` pin: an arm whose
# target failed to resolve reads zero calls, which is indistinguishable from a method that never
# ran, and that reading passes a recording tolerance without complaint.
ARMS = [
    "circ_vldraw",
    "circ_vlbuilddirty",
    "circ_vloverlay",
    "circ_vlpawnshadows",
    "circ_vlpolygon",
    "circ_vlsegments",
    "circ_vlcoverage",
    "circ_vlmask",
]

steps = list(scene)

for name in CLOUDS:
    steps.append(feature(name, False))

for name, on in SHIPPED:
    steps.append(feature(name, on))

# Colonists in three different rooms, so the pawn-shadow lane has casters under lamps that can
# actually see them rather than a colony's worth of pawns off the plate entirely. Two per room
# because a second caster in the same room is what makes the per-pawn loop cost more than one
# iteration. `clear` on the first only: it removes the save's own pawns, and repeating it would
# delete the ones the previous step just placed.
steps.append(step("SpawnPawn", kind="Colonist", faction="player", count="2",
                  offset="-29,26", clear="true"))
steps.append(step("SpawnPawn", kind="Colonist", faction="player", count="2", offset="-16,37"))
steps.append(step("SpawnPawn", kind="Colonist", faction="player", count="2", offset="10,48"))

# The scene, pinned before anything is timed, so a table taken against a different emitter
# population fails rather than being quietly incomparable.
#
# 22, NOT vector_light_perf's 23, AND THE PIN IS THE MEASUREMENT. The plate is that scenario's, but
# this one spawns colonists into it with `clear`, and one of minimal_colony.rws's three GlowPods does
# not survive that setup. Re-deriving the number from the plate rather than measuring it is exactly
# what this repo's pins must not be, so 22 is what was measured twice, on two builds, and 22 is what
# is pinned.
steps.append(probe("vector_light_count", "22", "0"))
steps.append(probe("circinus_available", "1", "0.001"))
steps.append(probe("vector_light_mask_available", "1", "0.001"))

# READING `_patched` IS WHAT ARMS THE METHOD. CircinusProbe arms on first read rather than at
# registration, so this block is not merely a set of assertions — it is the instrumentation being
# installed, and everything counted below is counted from here.
for arm in ARMS:
    steps.append(probe(arm + "_patched", "1", "0.001"))

# Both counters zeroed at the same instant, because the finding is a RATIO between them:
# vector_light_bakes counts the polygons VectorLightField built, circ_vlpolygon_calls counts every
# polygon built by anybody. A window in which those two disagree is a window in which one polygon
# was built more than once.
steps.append(probe("vector_light_bake_reset", "0", "0.001"))

# A full rebake, provoked the way the harness has always provoked one: any vector-light flag flip
# runs VectorLightRedraw.ForceRebuild, which drops every mesh and rebuilds every polygon on the map.
# Flipping penumbra off and straight back on leaves the shipped configuration in place and costs two
# whole-map rebakes, which is the event this table is trying to price.
steps.append(feature("vector_light_penumbra", False))
steps.append(feature("vector_light_penumbra", True))

# 60 frames with the clock running, so pawns move, the mask rebakes what they dirty, and the draw
# runs on a scene that is not frozen. Frames are discarded: this is a counter measurement, and
# nothing here is meant to be looked at.
steps.append(step("TickLapse", ticks="10", steps="60",
                  fileNamePrefix="vlframecost_discard", fps="20"))

# CALL COUNTS, PINNED TIGHT, because they are exact and they are the finding. `vector_light_bakes`
# counts what VectorLightField built; circ_vlpolygon_calls counts every polygon anybody built, and
# circ_vlsegments_calls every window scan. They come out 44 / 82 / 82, which says one polygon is
# built TWICE: once by the field for the mask, once again by the draw for the mesh. Pinned at what
# was measured rather than at what ought to be true, so the fix has a baseline to move.
EXACT = [
    ("vector_light_bakes", "44", "0"),
    ("circ_vlpolygon_calls", "82", "0"),
    ("circ_vlsegments_calls", "82", "0"),
    ("circ_vlcoverage_calls", "44", "0"),
]

for name, expected, tolerance in EXACT:
    steps.append(probe(name, expected, tolerance))

# RECORDED, NOT ASSERTED, exactly as perf_parents.json does it. These are timings on a contended
# machine — the untouched control arms moved by a factor of 1.4-2.0 between two runs an hour apart —
# so pinning them tightly would make the file fail for reasons that have nothing to do with the
# code. Centred on the measured value with a tolerance wide enough to survive the box.
RECORDED = [
    ("vector_light_bake_hits", "0", "400"),
    ("vector_light_bake_deferrals", "0", "100"),
    ("vector_light_bake_segments", "1872", "900"),
    ("vector_light_mask_applies", "224", "150"),
    ("vector_light_pawn_casters", "6", "4"),
    ("circ_vldraw_calls", "480", "240"),
    ("circ_vlbuilddirty_calls", "239", "120"),
    ("circ_vloverlay_calls", "238", "120"),
    ("circ_vlpawnshadows_calls", "237", "120"),
    ("circ_vlmask_calls", "224", "150"),
]

# Timings last, all on one wide recording tolerance: the point of having them in the report at all is
# that a later reader can see the shape, and a per-metric tolerance would imply a precision the box
# does not have.
for arm in ARMS:
    RECORDED.append((arm + "_total_ms", "30", "60"))
    RECORDED.append((arm + "_max_ms", "5", "15"))

for name, expected, tolerance in RECORDED:
    steps.append(probe(name, expected, tolerance))

out = {
    "name": "vector_light_frame_cost",
    "saveFile": src["saveFile"],
    "description": (
        "Where vector lighting's frame actually goes, by CALL COUNT rather than by duration, on the "
        "same 20-lamp five-room plate every other cost figure for this subsystem was measured on. "
        "vector_light_perf.json already reports what Patch_VectorLightDraw:Postfix costs; what no "
        "instrument in the repo could report is how many times each stage inside it runs, and that "
        "turns out to be the question. The visibility polygon is 83-94% of a bake in any cluttered "
        "scene (Tools/VectorLightBench) and is reached by two independent paths in the same frame - "
        "VectorLightField.EnsurePolygon bakes it for the mask, VectorLightOverlay.Rebuild bakes it "
        "again for the mesh - so circ_vlpolygon_calls read against vector_light_bakes is the whole "
        "finding: they are supposed to be equal, and they are 82 against 44. Pinned at tolerance 0 "
        "because a count is exact where a timing on this box is not - the untouched control arms "
        "moved by a factor of 1.4-2.0 between two runs an hour apart. Armed as a bank rather than "
        "one arm per run, which "
        "is sound here because these are distinct methods read at one instant to break down one "
        "frame budget, not one method compared across two feature states. Read TotalMs/Calls for a "
        "per-call cost, never AvgMs, which is per cycle. Every cloud lane is off: the sheet drifts "
        "on the tick counter and would put a moving, expensive draw inside a window whose job is to "
        "attribute time to this subsystem. The shipped-on configuration is stated flag by flag for "
        "the same reason. Timings are RECORDED with wide tolerances; the pins that bite are "
        "circinus_available, vector_light_count, every *_patched, the four call counts at tolerance "
        "0. THIS RUN REPORTS Pass=false WITH EVERY PROBE "
        "GREEN, and the three setup errors behind it are expected rather than tolerated. Two are "
        "the plate's own, inherited along with it from vector_light_mask_max_perf: the terrain rect "
        "straddles four SteamGeyser cells SetTerrain cannot clear. The third and fourth are "
        "SpawnPawn placing 1 of 2 in two of the three rooms, the second cell in each being occupied "
        "- which is why vector_light_pawn_casters is pinned at the 6 that were measured rather than "
        "at the 6 that were asked for, and the two numbers agreeing here is a coincidence worth "
        "naming rather than a check."
    ),
    "requiredMods": {"astryl.Circinus": "3773680130"},
    "steps": steps,
}

path = os.path.join(SCEN, "vector_light_frame_cost.json")

with open(path, "w") as f:
    json.dump(out, f, indent=2)
    f.write("\n")

print(f"wrote {path}: {len(steps)} steps")
