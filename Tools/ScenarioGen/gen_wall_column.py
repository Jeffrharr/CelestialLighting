#!/usr/bin/env python3
"""Generates Tests/Scenarios/vector_light_column.json — §27 phase 5b's monotonicity sweep.

ONE WALL COLUMN, AND TORCHES ADDED AROUND IT ONE ARM AT A TIME. A single free-standing wall cell
sits on bare concrete at midnight. Six torches are placed around it four cells out, in four stages —
one, two, four, six — and the rendered light level is read at four cells beside the column after
every stage. The stages are NESTED: each one keeps the torches the previous one placed and adds to
them, so the sweep really is "another lamp was switched on" rather than four different scenes.

THE PROPERTY, WHICH IS A DIRECTION AND NOT A NUMBER:

    adding a torch never lowers a cell's level

Reported from play the other way round — ring lamps around a column and the shadows get DEEPER as
lamps are added, which is backwards: every lamp you add fills in part of the region the others
cannot see. See DESIGN.md §27 phase 5b for the arithmetic, and VectorLightSaturationMathTests for
the same sweep run offline against a transcription of vanilla's own flood.

VANILLA IS THE ORACLE AND IS ONE OF THE ARMS. Its glow grid is unambiguously monotone here — the raw
sum can only grow — so the vanilla column of this table is what "monotone" means, measured rather
than argued. Three arms per stage:

    vanilla     §27 off entirely. The oracle.
    old         §27 with vector_light_mask_saturation OFF: the composition as it shipped, which
                subtracts each blocked torch's raw contribution out of a byte vanilla has already
                scaled down. This is the arm that must run the wrong way.
    corrected   the same with the flag on.

The `old` arm is not decoration. A monotonicity scenario in which every arm passes is a scenario
whose scene never saturated, and that is precisely how a direction-shaped bug survives a green run.

THE LEVEL IS THE MAX CHANNEL, not luminance. ColorInt.ProjectToColor32 normalises the three channels
against their shared peak, so the property is FALSE per channel for vanilla itself — add a green
lamp to a red-lit cell and the red channel genuinely falls. Only the peak is monotone, and it is
also exactly what GlowGrid.GroundGlowAt reads.
"""

import json
import os

# Scene anchor, matching every other §27 scenario so the fixture sits clear of the save's colony.
ANCHOR = "0,45"
ANCHOR_Z = 45

# Six torch positions in a hexagon TWO cells out from the column, ORDERED so the first, the first
# two, the first four and all six are each spread around it. The offline test in
# Tests/CelestialLighting.Tests/VectorLightSaturationMathTests.cs builds the same six in the same
# order and reads the same vertex, which is what lets its table predict this one.
#
# TWO CELLS OUT AND NOT FOUR, and the first draft of this file learned the difference the expensive
# way. Torches four cells out saturate the ground between them perfectly well, and the per-cell
# arithmetic underneath was plainly non-monotone there — but the lighting overlay carries one vertex
# per cell corner and one per centre, so a probe and a pixel both read a 3x3 tent filter over the
# cells. A one-cell column's shadow is one cell wide, and at four cells out the blur took the whole
# of the over-subtraction: the run came back monotone at every probed cell while the defect was
# still there underneath. A defect the render swallows is not one a player can see, so the ring came
# in until the defect survives its own render.
RING = [(2, 0), (-2, 0), (1, 2), (-1, -2), (-1, 2), (1, -2)]

STAGES = [1, 2, 4, 6]

# Emitters minimal_colony.rws already has, 45 cells away at map centre. `vector_light_count` is a
# map-wide count, so the pin has to carry them or every stage fails by a constant three — which reads
# like torches that were never built. They are far outside a radius-10 torch's reach, so they touch
# no probe cell; the offline sweep in VectorLightSaturationMathTests correspondingly has none, which
# is why its `torches` column and this file's `vector_light_count` differ by exactly this.
FIXTURE_EMITTERS = 3

# The four probe cells, local to the anchor. `behind` is the one the phase is about: one step west of
# the column, which the torch at (2, 0) can never see, in every stage. `far` is three cells west,
# outside the saturated core, and is the control — the old composition is already monotone there, so
# a run in which it moves is a run measuring something other than saturation.
PROBE_CELLS = {
    "column_behind": (-1, 0),
    "column_behind_far": (-3, 0),
    "column_north": (0, 1),
    "column_northwest": (-1, 1),
}

# EVERY ARM STATES EVERY FLAG. A SetFeature left out of an arm inherits whatever the previous arm
# set, which is how an arm ends up measuring a composition nobody chose; the harness has no notion of
# a default between arms.
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

ARMS = {
    "vanilla": {
        "vector_lights": False,
        "vector_light_penumbra": True,
        "vector_light_suppress": True,
        "vector_light_blend": True,
        "vector_light_mask": True,
        "vector_light_mask_beam": True,
        "vector_light_mask_max": False,
        "vector_light_shader_max": True,
        "vector_light_mask_saturation": True,
        "vector_light_open_doors": False,
    },
    "old": {
        "vector_lights": True,
        "vector_light_penumbra": True,
        "vector_light_suppress": True,
        "vector_light_blend": True,
        "vector_light_mask": True,
        "vector_light_mask_beam": True,
        "vector_light_mask_max": False,
        "vector_light_shader_max": True,
        "vector_light_mask_saturation": False,
        "vector_light_open_doors": False,
    },
    "corrected": {
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
    },
}

ARM_ORDER = ["vanilla", "old", "corrected"]

# Rendered bytes at the end of vanilla's Dijkstra, our coverage bake, two integer projections and the
# overlay's own vertex averaging. There is no closed form to predict them from, so they are read off a
# run and written back — the order DESIGN.md's §20 colour-temperature pins were established in, and
# for the same reason. A pin that moves is re-measured, never re-derived.
#
# `None` means "do not pin yet": the generator still emits the Probe step (against 0, which fails and
# therefore records) so a harvesting run has something to read.
#
# WHAT THE TABLE SAYS, at column_behind — one cell west of the column, permanently invisible to the
# torch two cells east of it:
#
#     torches         1      2      4      6
#     vanilla        62    168    255    255     the oracle: monotone, as it must be
#     old            20    123    172    134     falls 38 when two more torches come on
#     corrected      20    123    229    255
#
# The offline sweep in VectorLightSaturationMathTests predicted 19 / 123 / 171 / 133 and
# 19 / 123 / 228 / 255 for those two rows, from vanilla's transcribed flood and the overlay's
# transcribed averaging. Agreement to within one level across the whole table is what makes the two
# instruments one measurement rather than two opinions.
#
#   READINGS[stage][arm][probe] = value
READINGS = {
    1: {
        "vanilla": {
            "column_behind": 62,
            "column_behind_far": 38,
            "column_north": 77,
            "column_northwest": 62,
        },
        "old": {
            "column_behind": 20,
            "column_behind_far": 0,
            "column_north": 63,
            "column_northwest": 33,
        },
        # Identical to `old`, to the byte, and that is an assertion rather than a coincidence: one
        # torch's own light is a Color32 and cannot exceed the ceiling, so the correction is a
        # provable no-op and §27's one-lamp shadow is the one it already shipped.
        "corrected": {
            "column_behind": 20,
            "column_behind_far": 0,
            "column_north": 63,
            "column_northwest": 33,
            "vector_light_mask_saturated_samples": 0,
            "vector_light_mask_saturation_skipped": 0,
            "vector_light_mask_saturation_relief": 0,
        },
    },
    2: {
        "vanilla": {
            "column_behind": 168,
            "column_behind_far": 144,
            "column_north": 156,
            "column_northwest": 159,
        },
        "old": {
            "column_behind": 123,
            "column_behind_far": 106,
            "column_north": 126,
            "column_northwest": 127,
        },
        "corrected": {
            "column_behind": 123,
            "column_behind_far": 106,
            "column_north": 126,
            "column_northwest": 127,
            "vector_light_mask_saturated_samples": 0,
            "vector_light_mask_saturation_skipped": 0,
            "vector_light_mask_saturation_relief": 0,
        },
    },
    4: {
        "vanilla": {
            "column_behind": 255,
            "column_behind_far": 241,
            "column_north": 255,
            "column_northwest": 255,
        },
        "old": {
            "column_behind": 172,
            "column_behind_far": 201,
            "column_north": 182,
            "column_northwest": 204,
        },
        "corrected": {
            "column_behind": 229,
            "column_behind_far": 215,
            "column_north": 234,
            "column_northwest": 241,
            "vector_light_mask_saturated_samples": 40,
            "vector_light_mask_saturation_skipped": 0,
            "vector_light_mask_saturation_relief": 72,
        },
    },
    6: {
        "vanilla": {
            "column_behind": 255,
            "column_behind_far": 255,
            "column_north": 255,
            "column_northwest": 255,
        },
        "old": {
            "column_behind": 134,
            "column_behind_far": 212,
            "column_north": 138,
            "column_northwest": 154,
        },
        # Every probe cell back at vanilla's own level, which is the correct answer and not a
        # disabled feature: five of the six torches can see each of these cells, and five torches two
        # cells away saturate them on their own. The shadow that survives at six torches is the one
        # the geometry actually supports, which is none.
        "corrected": {
            "column_behind": 255,
            "column_behind_far": 255,
            "column_north": 255,
            "column_northwest": 255,
            "vector_light_mask_saturated_samples": 85,
            "vector_light_mask_saturation_skipped": 0,
            "vector_light_mask_saturation_relief": 168,
        },
    },
}

# How tight. The scene is static, nothing animates the overlay mesh between arms, and the value is an
# integer byte off a deterministic bake, so anything looser than half a level would let the
# composition drift silently — which is the one thing this file exists to prevent.
LEVEL_TOL = 0.5


def step(t, **args):
    return {"type": t, "args": {k: str(v) for k, v in args.items()}}


def probe(name, expected, tol):
    return step("Probe", probeName=name, expectedValue=expected, tolerance=tol)


def cells_arg(cells):
    return "; ".join(f"{x},{z}" for x, z in cells)


def arm(name):
    return [step("SetFeature", featureName=flag, enabled="true" if ARMS[name][flag] else "false")
            for flag in FLAGS]


def pin(stage, name, probe_name, tolerance):
    """One Probe step, pinned if the table has been harvested and unpinned if it has not.

    ALWAYS EMITTED, even before the table exists. An unpinned run has to record the value or there is
    nothing to harvest from — the harness writes ActualValue into the report whether the check passed
    or not, and carries on past a failure, so a first run against expected 0 is exactly a listing of
    what the scene reads. That is why this emits a deliberately-failing pin rather than no pin: a
    skipped probe leaves a hole in the report that a later edit cannot tell from a probe that read 0.
    """
    value = READINGS.get(stage, {}).get(name, {}).get(probe_name)

    if value is None:
        return probe(probe_name, 0, 0)

    return probe(probe_name, value, tolerance)


def readings_for(stage, name):
    return [pin(stage, name, probe_name, LEVEL_TOL) for probe_name in PROBE_CELLS]


def telemetry(stage, name):
    """Whether the correction ran at all, and how much of the shadow it took back.

    Zero in the vanilla and `old` arms by construction — vanilla never reaches the mask and `old` has
    the flag down — which is worth stating rather than assuming, because it is what separates "the
    corrected arm found saturation" from "the flag never reached the mesh builder in any arm".
    """
    names = ("vector_light_mask_saturated_samples",
             "vector_light_mask_saturation_skipped",
             "vector_light_mask_saturation_relief")

    if name != "corrected":
        return [probe(probe_name, 0, 0) for probe_name in names]

    return [pin(stage, name, probe_name, 0) for probe_name in names]


def build():
    steps = [
        # Explicit rather than assumed. run_test.sh does not reset the mod's persisted settings XML,
        # so a preset somebody chose in-game months ago is still in force; Realistic zeroes the
        # brightness floors and renders a midnight plate literally black. Stated first so every
        # number below is against the shipped preset.
        step("SetFeature", featureName="realistic_preset", enabled="false"),
        step("SetTile", latitude=45),
        step("SetSeason", dayOfYear=40),
        step("SetWeather", weatherDef="Clear", instant="true"),
        step("SetTerrain", def_="Concrete", width=24, height=24, offset=ANCHOR, clear="true"),
        # The column: one wall cell, free-standing, with nothing else on the plate. Everything the
        # scenario measures is this one cell's shadow.
        step("PlaceThings", def_="Wall", stuff="BlocksGranite", offset=ANCHOR,
             layout="cells", clear="true", cells=cells_arg([(0, 0)])),
        step("SetTime", hour=0),
        step("LookAt", offset=ANCHOR, zoom=16),
    ]

    # The mask reads vanilla's per-emitter arrays by reflection and stands down silently if it
    # cannot, so an unpinned run photographs the crossfade while every other number stays healthy.
    steps.append(probe("vector_light_mask_available", 1, 0))

    # The first capture of a run carries the HUD, so it is thrown away rather than compared.
    steps.extend(arm("vanilla"))
    steps.append(step("Screenshot", fileName="column_warmup_discard.png"))

    placed = 0

    for stage in STAGES:
        steps.append(step("PlaceThings", def_="TorchLamp", offset=ANCHOR, layout="cells",
                          cells=cells_arg(RING[placed:stage])))
        placed = stage

        # Pinned per stage rather than assumed: a torch that failed to build takes every level in the
        # stage with it, and the failure would read as the composition being wrong.
        steps.append(probe("vector_light_count", FIXTURE_EMITTERS + stage, 0))

        for name in ARM_ORDER:
            steps.extend(arm(name))
            steps.extend(readings_for(stage, name))
            steps.extend(telemetry(stage, name))
            steps.append(step("Screenshot", fileName=f"column_{stage}_{name}.png"))

    return steps


def main():
    root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    out = os.path.join(root, "Tests", "Scenarios", "vector_light_column.json")

    scenario = {
        "name": "vector_light_column",
        "saveFile": "minimal_colony.rws",
        "description": __doc__.split("\n\n", 1)[1].strip().replace("\n", " "),
        "steps": build(),
    }

    # `def` is a Python keyword, so the generator spells it `def_` and it is fixed up here rather
    # than quoted at every call site.
    text = json.dumps(scenario, indent=2).replace('"def_"', '"def"')

    with open(out, "w") as handle:
        handle.write(text + "\n")

    print(f"wrote {out} ({len(scenario['steps'])} steps)")


if __name__ == "__main__":
    main()
