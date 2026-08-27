#!/usr/bin/env python3
"""Generates Tests/Scenarios/vector_light_sun_lamp.json — the mixed-hue saturation sweep.

A SUN LAMP AND A LOT OF ORDINARY LIGHTS, which is the scene the bug was reported from: "shadows get
calculated oddly when there's a lot of regular lights next to a lit sunlamp -- the shadows from the
sunlamp can fall directly on the other lit lights."

Same fixture as vector_light_column.json — one free-standing wall cell on bare concrete at midnight,
torches ringed two cells out and added in nested stages of one, two, four and six — with ONE SUN LAMP
four cells the other side of the column. So the cells the torch ring lights brightest are exactly the
cells the column hides from the sun lamp, and the sun lamp's shadow lands on lit lamps.

WHY THIS SCENE EXISTS WHEN THE COLUMN SWEEP ALREADY DID. Every emitter in every §27 fixture, offline
and live, was a TorchLamp: glowColor (184,136,83), one hue, every emitter on the same ray out of
black. The saturation correction reconstructed what vanilla displayed as a single projection of the
emitters' true sum, and a single projection agrees with vanilla's own fold — which projects after
EVERY addition — precisely when the emitters share a hue. So the fixture could not see the
assumption it was resting on.

A sun lamp is white: glowColor (370,370,370), over the ceiling before its own flood has projected.
Mixed with warm lamps the fold and the single projection part by up to 28 levels, the correction's
self-check reads that as "we do not understand this cell", declines it, and the frame keeps the raw
over-subtraction the correction exists to remove. Offline that shows up as a cliff — the cell one
east of the column reads 241 at four torches and 116 at six, against vanilla's 255.

THE ARMS, three per stage, exactly as the column sweep's:

    vanilla     §27 off entirely. The oracle: vanilla's glow grid is monotone in lamp count.
    old         §27 with vector_light_mask_saturation OFF -- the composition before the correction.
    corrected   the same with the flag on.

TWO THINGS ABOUT A SUN LAMP THAT THE FIXTURE IS BUILT AROUND, both of which cost a run to learn:

    IT IS ONLY ON DURING THE DAY. CompProperties_Schedule runs it from 0.25 to 0.8 of the day —
    06:00 to 19:12 — to match plant resting periods. At the midnight every other §27 scenario uses it
    registers no glower at all, and the run stays green while every probe reads the torch ring alone:
    the first attempt at this file pinned vector_light_count at 4 where it expected 5 and that was
    the only sign. So the scene is at noon, and the room is ROOFED, which is where a sun lamp lives
    anyway.

    IT DRAWS 2900 W, which is more than one generator makes. The scene chains three toxifier
    generators (1400 W each, no fuel, no glower of their own) onto one hidden conduit run. An
    underpowered sun lamp fails exactly the same silent way a scheduled-off one does.
"""

import json
import os

# Scene anchor, matching every other §27 scenario so the fixture sits clear of the save's colony.
ANCHOR = "0,45"

# The torch ring, two cells out and in the same order as vector_light_column.json and the offline
# sweep in VectorLightSaturationMathTests, so all three instruments describe one scene.
RING = [(2, 0), (-2, 0), (1, 2), (-1, -2), (-1, 2), (1, -2)]

STAGES = [1, 2, 4, 6]

# Four cells WEST of the column, so the column blocks it from the ring's own cells to the east.
SUN_LAMP = (-4, 0)

# A sealed, roofed room around the whole fixture — a grow room, which is where a sun lamp is found.
# SEALED RATHER THAN WALLED: one gap makes the interior an OUTDOOR room, and at noon that admits the
# sky over every cell the scene is trying to measure lamp light on.
ROOM_MIN = (-9, -7)
ROOM_MAX = (9, 7)

# Three of them, because one sun lamp is 2900 W against a toxifier generator's 1400 W. Inside the
# room, in the far east corner: they are 2x2, so each occupies its own cell and the one north-east of
# it, and the easternmost torch is nine cells away from the nearest of them.
GENERATORS = [(7, 4), (7, 1), (7, -2)]

# One run down the column of cells west of the generators — adjacent to all three, so all three
# transmit — then west along the room's south row and north to the cell below the lamp. It stops one
# short of the lamp because a consumer connects to any adjacent transmitter, and GenSpawn refuses a
# conduit on a cell something already occupies.
CONDUIT = list(
    dict.fromkeys(
        [(6, z) for z in range(-6, 6)]
        + [(x, -6) for x in range(6, -5, -1)]
        + [(-4, z) for z in range(-6, 0)]
    )
)

# Emitters minimal_colony.rws already carries at map centre, 45 cells away and outside every radius
# here. vector_light_count is map-wide, so the pins have to include them.
FIXTURE_EMITTERS = 3

# THE CELL THE REPORT IS ABOUT is `sunlamp_lit`: one step east of the column, hidden from the sun
# lamp by it and lit by the torch two cells further east. `sunlamp_lit_far` is three east, past that
# torch and still in the column's shadow. `sunlamp_open` is four cells NORTH of the column, which the
# sun lamp can see perfectly well — the control, which no arm may move.
PROBE_CELLS = {
    "sunlamp_lit": (1, 0),
    "sunlamp_lit_far": (3, 0),
    "sunlamp_open": (0, 4),
}

TELEMETRY = [
    "vector_light_mask_saturated_samples",
    "vector_light_mask_saturation_skipped",
    "vector_light_mask_saturation_relief",
]

# EVERY ARM STATES EVERY FLAG. A SetFeature left out of an arm inherits whatever the previous arm
# set, which is how an arm ends up measuring a composition nobody chose.
FLAGS = [
    "vector_lights",
    "vector_light_penumbra",
    "vector_light_suppress",
    "vector_light_blend",
    "vector_light_mask",
    "vector_light_mask_beam",
    "vector_light_mask_max",
    "vector_light_shader_max",
    "vector_light_mask_saturation",
    "vector_light_open_doors",
]

BASE_ARM = {
    "vector_lights": True,
    "vector_light_penumbra": True,
    "vector_light_suppress": True,
    "vector_light_blend": True,
    "vector_light_mask": True,
    "vector_light_mask_beam": True,
    "vector_light_mask_max": False,
    "vector_light_shader_max": True,
    "vector_light_mask_saturation": True,
    "vector_light_open_doors": False,
}

ARMS = {
    "vanilla": dict(BASE_ARM, vector_lights=False),
    "old": dict(BASE_ARM, vector_light_mask_saturation=False),
    "corrected": dict(BASE_ARM),
}

ARM_ORDER = ["vanilla", "old", "corrected"]

# Read off a run and written back, never derived — the same rule the column sweep's table carries.
# `None` emits the Probe step against 0 so a harvesting run records the real value in its report.
#
#   READINGS[stage][arm][probe] = value
#
# WHAT THE TABLE SAYS at `sunlamp_lit`, one cell east of the column, hidden from the sun lamp and lit
# by the torch two cells further east:
#
#     torches         1      2      4      6
#     vanilla       223    255    255    255     the oracle
#     old           158    148    110     72     deepens with every torch, as it must
#     corrected     161    180    242    255     lands on vanilla, which is where a lit cell belongs
#
# The same scenario run against `main` reads 161 / 180 / 242 / **109** in the corrected arm — the
# cliff this branch removes — with `vector_light_mask_saturation_skipped` going 0 / 0 / 8 / 44 as the
# self-check declines more and more of the room. Here it is 0 throughout: replaying vanilla's fold
# reconstructs every one of these cells exactly.
READINGS = {
    1: {
        "vanilla": {
            "sunlamp_lit": 223,
            "sunlamp_lit_far": 190,
            "sunlamp_open": 164,
        },
        "old": {
            "sunlamp_lit": 158,
            "sunlamp_lit_far": 127,
            "sunlamp_open": 164,
            "vector_light_mask_saturated_samples": 0,
            "vector_light_mask_saturation_skipped": 0,
            "vector_light_mask_saturation_relief": 0,
        },
        "corrected": {
            "sunlamp_lit": 161,
            "sunlamp_lit_far": 129,
            "sunlamp_open": 164,
            "vector_light_mask_saturated_samples": 9,
            "vector_light_mask_saturation_skipped": 0,
            "vector_light_mask_saturation_relief": 25,
        },
    },
    2: {
        "vanilla": {
            "sunlamp_lit": 255,
            "sunlamp_lit_far": 221,
            "sunlamp_open": 211,
        },
        "old": {
            "sunlamp_lit": 148,
            "sunlamp_lit_far": 119,
            "sunlamp_open": 211,
            "vector_light_mask_saturated_samples": 0,
            "vector_light_mask_saturation_skipped": 0,
            "vector_light_mask_saturation_relief": 0,
        },
        "corrected": {
            "sunlamp_lit": 180,
            "sunlamp_lit_far": 129,
            "sunlamp_open": 211,
            "vector_light_mask_saturated_samples": 42,
            "vector_light_mask_saturation_skipped": 0,
            "vector_light_mask_saturation_relief": 65,
        },
    },
    4: {
        "vanilla": {
            "sunlamp_lit": 255,
            "sunlamp_lit_far": 255,
            "sunlamp_open": 254,
        },
        "old": {
            "sunlamp_lit": 110,
            "sunlamp_lit_far": 151,
            "sunlamp_open": 244,
            "vector_light_mask_saturated_samples": 0,
            "vector_light_mask_saturation_skipped": 0,
            "vector_light_mask_saturation_relief": 0,
        },
        "corrected": {
            "sunlamp_lit": 242,
            "sunlamp_lit_far": 234,
            "sunlamp_open": 254,
            "vector_light_mask_saturated_samples": 96,
            "vector_light_mask_saturation_skipped": 0,
            "vector_light_mask_saturation_relief": 186,
        },
    },
    6: {
        "vanilla": {
            "sunlamp_lit": 255,
            "sunlamp_lit_far": 255,
            "sunlamp_open": 255,
        },
        "old": {
            "sunlamp_lit": 72,
            "sunlamp_lit_far": 148,
            "sunlamp_open": 233,
            "vector_light_mask_saturated_samples": 0,
            "vector_light_mask_saturation_skipped": 0,
            "vector_light_mask_saturation_relief": 0,
        },
        "corrected": {
            "sunlamp_lit": 255,
            "sunlamp_lit_far": 255,
            "sunlamp_open": 255,
            "vector_light_mask_saturated_samples": 137,
            "vector_light_mask_saturation_skipped": 0,
            "vector_light_mask_saturation_relief": 285,
        },
    },
}

def flags(arm):
    return [
        {
            "type": "SetFeature",
            "args": {"featureName": flag, "enabled": "true" if ARMS[arm][flag] else "false"},
        }
        for flag in FLAGS
    ]


def probe(name, value, tolerance):
    return {
        "type": "Probe",
        "args": {
            "probeName": name,
            "expectedValue": str(value if value is not None else 0),
            "tolerance": str(tolerance),
        },
    }


def cells(offsets):
    return "; ".join(f"{x},{z}" for x, z in offsets)


def place(def_name, offsets, extra=None):
    args = {
        "def": def_name,
        "offset": ANCHOR,
        "layout": "cells",
        "cells": cells(offsets),
    }

    if extra:
        args.update(extra)

    return {"type": "PlaceThings", "args": args}


def walls():
    (min_x, min_z), (max_x, max_z) = ROOM_MIN, ROOM_MAX

    perimeter = [(x, min_z) for x in range(min_x, max_x + 1)]
    perimeter += [(x, max_z) for x in range(min_x, max_x + 1)]
    perimeter += [(min_x, z) for z in range(min_z + 1, max_z)]
    perimeter += [(max_x, z) for z in range(min_z + 1, max_z)]

    return perimeter


def build():
    (min_x, min_z), (max_x, max_z) = ROOM_MIN, ROOM_MAX

    steps = [
        {"type": "SetFeature", "args": {"featureName": "realistic_preset", "enabled": "false"}},
        {"type": "SetTile", "args": {"latitude": "45"}},
        {"type": "SetSeason", "args": {"dayOfYear": "40"}},
        {"type": "SetWeather", "args": {"weatherDef": "Clear", "instant": "true"}},
        {
            "type": "SetTerrain",
            "args": {
                "def": "Concrete", "width": "30", "height": "30", "offset": ANCHOR, "clear": "true",
            },
        },
        place("Wall", walls(), {"stuff": "BlocksGranite", "clear": "true"}),
        # Roofed over the walls as well as the interior, because the game only ever roofs a cell as
        # a consequence of play and a scenario's walls otherwise enclose open sky.
        {
            "type": "SetRoof",
            "args": {
                "def": "RoofConstructed",
                "width": str(max_x - min_x + 3),
                "height": str(max_z - min_z + 3),
                "offset": ANCHOR,
            },
        },
        place("Wall", [(0, 0)], {"stuff": "BlocksGranite", "clear": "true"}),
        place("ToxifierGenerator", GENERATORS, {"clear": "true"}),
        place("HiddenConduit", CONDUIT),
        place("SunLamp", [SUN_LAMP]),
        # NOON, because the lamp's schedule is what decides whether it emits at all — and then real
        # ticks, because both the schedule and the power net are read on a Rare tick (every 250) and
        # neither has run at the instant the clock is moved.
        {"type": "SetTime", "args": {"hour": "12"}},
        {"type": "FastForward", "args": {"ticks": "600"}},
        {"type": "SetTimeSpeed", "args": {"speed": "paused"}},
        {"type": "LookAt", "args": {"offset": ANCHOR, "zoom": "16"}},
        probe("vector_light_mask_available", 1, 0),
    ]

    steps += flags("vanilla")

    # The first capture of a run carries the HUD whatever hideUi says, so one frame is spent and
    # thrown away rather than quietly measuring the message log.
    steps.append({"type": "Screenshot", "args": {"fileName": "sunlamp_warmup_discard.png"}})

    placed = 0

    for stage in STAGES:
        steps.append(place("TorchLamp", RING[placed:stage]))
        placed = stage

        # The sun lamp, the torches placed so far, and the save's own three.
        steps.append(probe("vector_light_count", FIXTURE_EMITTERS + 1 + stage, 0))

        for arm in ARM_ORDER:
            steps += flags(arm)

            for name in PROBE_CELLS:
                steps.append(probe(name, READINGS.get(stage, {}).get(arm, {}).get(name), 0.5))

            if arm != "vanilla":
                for name in TELEMETRY:
                    value = READINGS.get(stage, {}).get(arm, {}).get(name)
                    steps.append(probe(name, value, max(1, int((value or 0) * 0.25))))

            if stage == STAGES[-1]:
                steps.append(
                    {"type": "Screenshot", "args": {"fileName": f"sunlamp_6_{arm}.png"}})

    return steps


def main():
    root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    out = os.path.join(root, "Tests", "Scenarios", "vector_light_sun_lamp.json")

    scenario = {
        "name": "vector_light_sun_lamp",
        "saveFile": "minimal_colony.rws",
        "description": __doc__.split("\n", 2)[2].strip().replace("\n", " "),
        "steps": build(),
    }

    with open(out, "w") as handle:
        json.dump(scenario, handle, indent=2)
        handle.write("\n")

    print(f"wrote {out} ({len(scenario['steps'])} steps)")


if __name__ == "__main__":
    main()
