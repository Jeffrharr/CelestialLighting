#!/usr/bin/env python3
"""Generate Tests/Scenarios/vector_light_reach.json.

LAMP GLOW REACH AGAINST THE INDOOR MULTIPLY LAYER, IN ONE FRAME. Both are answers to the same want —
a lit room that reads richer — and they disagree about how, so the only useful question is which
looks better at what cost, and that is a question about one scene photographed several ways rather
than about either of them alone. Reach draws the lamp with vanilla's own falloff curve stretched over
a longer radius, so the excess our max composition delivers is larger; the multiply layer keeps the
excess where it is and DELIVERS it twice, once additively and once as a scaling of the surface.

THE SCENE IS vector_light_surface_lift's, UNCHANGED, for the third time in this repo — one torch four
cells from each of two doors, the east into a second roofed room and the west onto open sky, gravel
everywhere so the ground has its own speckle to keep or lose. Reused rather than rewritten because
vector_light_indoor_multiply is measured in it, and the incumbent's numbers are only a baseline if
the challenger is shot in the same room.

THREE PROBES CARRY THE COST ARGUMENT AND THE THIRD IS THE ONE THAT SETTLES IT.
`vector_light_lit_area` is the visibility polygon's own area, which must GROW with reach — that is the
feature. `vector_light_coverage_cells` is the coverage grid's allocated SIZE, which must NOT, because
the grid is capped at vanilla's own radius (VectorLightReachMath.CoverageRadius) on the grounds that
its only use is scaling vanilla's light and vanilla delivers nothing past its cutoff.

`vector_light_coverage_lit_cells` is carried alongside them and is NOT evidence for the cap, which is
worth saying because the first run of this scenario read it as though it were. It counts bytes equal
to 255, so it moves when a grid SATURATES as well as when a grid grows — and reach saturates one, by
pushing the polygon past vanilla's rim and filling in the partly-covered discretisation cells there.
It duly climbed 329 -> 467 from reach 1 to reach 1.5 while the allocation did not move at all. It is
in the report as the description of that side effect, not as a cost measurement.

Every cloud flag is off and stated: half this scene is open ground directly under a measured beam,
and the cloud sheet drifts on the tick counter, so leaving them on would put run-to-run noise on
exactly the pixels being compared.
"""

import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
BASE = os.path.join(ROOT, "Tests", "Scenarios", "vector_light_surface_lift.json")
OUT = os.path.join(ROOT, "Tests", "Scenarios", "vector_light_reach.json")

# The scene: everything up to and including the two doors being opened. Taken from the base scenario
# verbatim so the three files sharing this room cannot drift into photographing different ones.
SCENE_STEPS = 12

# The flag bed every arm states in full, copied from vector_light_indoor_multiply's own so the two
# scenarios are comparable line by line. Only the three taste flags vary per arm.
SHARED = [
    ("vector_light_penumbra", True),
    ("vector_light_suppress", True),
    ("vector_light_blend", False),
    ("vector_light_mask", True),
    ("vector_light_mask_beam", False),
    ("vector_light_mask_max", False),
    ("vector_light_mask_max_lift", True),
    ("vector_light_mask_max_seed", True),
    ("vector_light_shader_max", True),
    ("vector_light_shader_max_subtract", True),
    ("vector_light_open_doors", True),
    ("vector_light_door_glow_blocker", False),
    ("vector_light_door_aperture", False),
    ("vector_light_surface_lift", False),
    ("cloud_cover", False),
    ("cloud_sheet", False),
    ("cloud_presence", False),
    ("cloud_deck_varieties", False),
    ("cloud_volume", False),
]


def feature(name, enabled):
    return {"type": "SetFeature", "args": {"featureName": name, "enabled": "true" if enabled else "false"}}


def arm(vector_lights, reach, indoor_multiply):
    """One arm's flags, stated in full.

    STATED IN FULL RATHER THAN INHERITED, because a flag left unstated is whatever the arm before it
    happened to leave behind — and a committed capture in this repo has already read 17.08 instead of
    20.23 that way while its scenario still passed green. The two reach keys are stated as a PAIR on
    every arm for the same reason and one more: they are positions of one slider, so an arm that set
    the vibrant key without clearing the max key would be measuring the max.
    """
    steps = [feature("vector_lights", vector_lights)]
    steps += [feature(name, enabled) for name, enabled in SHARED]
    steps.append(feature("vector_light_reach_vibrant", reach == "vibrant"))
    steps.append(feature("vector_light_reach_max", reach == "max"))
    steps.append(feature("vector_light_indoor_multiply", indoor_multiply))
    return steps


def probe(name, value, tolerance="0"):
    return {
        "type": "Probe",
        "args": {"probeName": name, "expectedValue": value, "tolerance": tolerance},
    }


def shader_pins():
    """The two pins that make a forgotten --install fail loudly instead of photographing the old
    renderer. Every arm that claims the shader carries them; the vanilla arm does not, because with
    vector_lights off the answer says nothing about the arm."""
    return [
        probe("vector_light_shader_max_available", "1"),
        probe("vector_light_mask_available", "1"),
    ]


def capture(name, vector_lights, reach, indoor_multiply, pin_shader=True, pins=None):
    steps = arm(vector_lights, reach, indoor_multiply)
    if pin_shader:
        steps += shader_pins()
    if pins:
        steps += pins
    steps.append({"type": "Screenshot", "args": {"fileName": name}})
    return steps


# LIVE MEASUREMENTS from the run of 2026-08-28, not computed values. If the arithmetic moves, the fix
# is to re-run this scenario and re-measure — never to re-derive a pin and edit it in.
#
# The polygon area grows with reach and the coverage allocation does not, which is the whole cost
# argument in four numbers: 374 -> 604 -> 664 cells of lit area across reach 1 / 1.5 / 2, against a
# coverage grid that sits at 948 cells throughout — the same number three times, to the byte.
#
# 948 is the map's FOUR emitters summed, not the torch's own square, which is why it is not a
# perfect square. The emitter count is pinned beside it so the total means something.
#
# WHY THE AREA IS NOT QUADRATIC IN REACH, since 2.25x and 4x are what an open-ground lamp would give:
# this lamp is in a room. Its polygon is clipped by the walls long before it reaches its own radius
# in most directions, so the extra reach only buys area through the two doorways and along the
# corridor beyond them. That is the feature working as designed rather than a shortfall — reach
# lengthens what a lamp can SEE, and a wall is still a wall.
LIT_AREA_OFF = "374.18"
LIT_AREA_VIBRANT = "604.06"
LIT_AREA_MAX = "663.66"
COVERAGE_CELLS = "948"
LIT_CELLS_OFF = "329"
LIT_CELLS_REACHED = "467"
EMITTERS = "4"

# Wide enough to absorb the polygon's own 48-gon discretisation and no wider.
AREA_TOLERANCE = "8"

# EXACT. The allocation is an integer array length derived from a def's radius and a ceiling; it has
# no noise to absorb, and a tolerance here would let the cap regress by a cell without failing.
CELLS_TOLERANCE = "0"


def geometry_pins(lit_area, lit_cells):
    """The three that separate "the lamp got bigger" from "the bake got bigger".

    The coverage ALLOCATION is pinned to one constant across every arm, on purpose: that single
    unchanging number against a lit area that nearly doubles is the entire claim this scenario makes
    about cost, and stating it as one shared constant is what makes a regression read as a
    contradiction rather than as three numbers that all moved a bit.
    """
    return [
        probe("vector_light_lit_area", lit_area, AREA_TOLERANCE),
        probe("vector_light_coverage_cells", COVERAGE_CELLS, CELLS_TOLERANCE),
        probe("vector_light_coverage_lit_cells", lit_cells, "8"),
        # Pinned so the allocation above is interpretable rather than a bare total: 948 cells is
        # every emitter on the map summed, and a reader cannot tell a stable per-lamp grid from a
        # stable emitter roster without both. It also catches the failure that would make the whole
        # comparison meaningless — an arm where a lamp stopped being registered at all would hold
        # the allocation constant for entirely the wrong reason.
        probe("vector_light_emitters", EMITTERS, "0"),
    ]


def main():
    with open(BASE) as handle:
        base = json.load(handle)

    steps = list(base["steps"][:SCENE_STEPS])

    # DISCARDED, and it has to exist. hideUi is honoured from the SECOND capture onward, so the first
    # frame of any scenario carries the HUD — whose message log differs run to run — and an A/B whose
    # A is that frame is partly a measurement of UI pixels.
    steps += capture("reach_warmup_discard.png", True, "off", False)

    # Vanilla, for the level everything else is read against.
    steps += capture("reach_vanilla.png", False, "off", False, pin_shader=False)

    # THE BASELINE: §27 exactly as it ships today. Reach at its off position must reproduce this
    # frame bit for bit, which is what makes every other arm a measurement rather than a picture.
    steps += capture(
        "reach_shipped.png", True, "off", False,
        pins=geometry_pins(LIT_AREA_OFF, LIT_CELLS_OFF))

    # THE FEATURE at the position it is proposed at.
    steps += capture(
        "reach_vibrant.png", True, "vibrant", False,
        pins=geometry_pins(LIT_AREA_VIBRANT, LIT_CELLS_REACHED))

    # THE TOP OF THE SLIDER, which is where the cost is worst and where the mid-field lift is most
    # likely to read as the map being washed out rather than as a warmer room. An arm nobody looks at
    # is how a slider ships with an unusable top end.
    steps += capture(
        "reach_max.png", True, "max", False,
        pins=geometry_pins(LIT_AREA_MAX, LIT_CELLS_REACHED))

    # THE INCUMBENT, which is the whole reason this scenario exists. Same room, same hour, same
    # renderer: the question on the table is whether reach is a better answer to the want the
    # multiply layer was built for, and that is decided here or nowhere.
    steps += capture("reach_multiply.png", True, "off", True)

    # BOTH AT ONCE, because a player can set both and nothing stops them. Neither is self-limiting
    # against the other — reach enlarges the excess and the layer delivers it twice — so this is the
    # arm that says whether the combination is merely brighter or actually unusable.
    steps += capture("reach_vibrant_multiply.png", True, "vibrant", True)

    # THE SAME-BUILD CONTROL, repeating `shipped` after the arms being judged. Two runs of ONE build
    # already differ on a share of the frame; without this the floor reads as the feature.
    steps += capture("reach_shipped_control.png", True, "off", False)

    # Noon, where the roofed floor is already brighter and any extra light must therefore read
    # SMALLER, and where the outdoor half of the fan is under a daylight scale rather than over
    # black. The pin is the base scenario's own measured value at this tile and hour — RimWorld's
    # clock does not put noon where a calculator would.
    steps.append({"type": "SetTime", "args": {"hour": "12"}})
    steps.append(probe("sun_elevation", "56.72", "0.5"))
    steps += capture("reach_shipped_noon.png", True, "off", False)
    steps += capture("reach_vibrant_noon.png", True, "vibrant", False)

    scenario = {
        "name": "vector_light_reach",
        "saveFile": base["saveFile"],
        "description": (
            "Lamp glow reach against the indoor multiply layer, in one frame. Both answer the same "
            "want — a lit room that reads richer — and they disagree about how: reach draws the lamp "
            "with vanilla's own falloff curve stretched over a longer radius so the excess the max "
            "composition delivers is larger, while the multiply layer keeps the excess where it is "
            "and delivers it twice. The scene is vector_light_surface_lift's own, unchanged, because "
            "the incumbent is measured in it and a challenger shot in a different room is not a "
            "comparison. Arms at midnight: vanilla, §27 as shipped, reach at the proposed position, "
            "reach at the top of the slider, the multiply layer alone, both at once, and §27 as "
            "shipped again as a same-build control for the run-to-run pixel floor; then the "
            "shipped/reach pair repeated at noon, where the floor is already brighter and any extra "
            "light must read smaller. Two probes carry the cost argument as a pair: "
            "vector_light_lit_area is the polygon's own area and MUST grow with reach, while "
            "vector_light_coverage_lit_cells counts the coverage grid and must NOT, because the grid "
            "is capped at vanilla's radius on the grounds that its only use is scaling vanilla's "
            "light and vanilla delivers nothing past its cutoff. A run where both move is a run "
            "where that optimisation did not land, and no pixel measurement would say so. Every "
            "cloud flag is off and stated: half this scene is open ground under a measured beam."
        ),
        "steps": steps,
    }

    with open(OUT, "w") as handle:
        json.dump(scenario, handle, indent=2)
        handle.write("\n")

    print("wrote %s (%d steps)" % (OUT, len(steps)))


if __name__ == "__main__":
    main()
