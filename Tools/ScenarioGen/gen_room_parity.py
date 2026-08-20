#!/usr/bin/env python3
"""Generates Tests/Scenarios/room_parity.json — the indoor/outdoor lighting baseline.

TWO IDENTICAL BUILDINGS, differing by one cell. Both are nine by nine, roofed edge to edge, with a
torch three cells back from an opening in the south wall. In the west building that opening holds a
DOOR, standing open; in the east building the wall cell was simply never built, so the opening is a
GAP. Nothing else about them differs — same size, same roof, same torch, same distance.

WHY ONE CELL IS THE WHOLE EXPERIMENT. RimWorld classifies rooms topologically. A door is a room
boundary whether it is open or shut, so the west building is an enclosed Room; a gap is not, so the
east building's interior joins the outdoors and comes back UsesOutdoorTemperature. Every "is this
cell inside" question in this mod is asked through that flag — §7b's sky occlusion via
EaveCells.Encloses, §15's eave shade, §15b's shading — so a single missing wall cell flips a roofed
interior from "enclosed" to "porch". Vanilla asks the roof grid instead and lights both alike.

And §27 has the mirror of the same asymmetry from the other side: vanilla's glow flood pours through
a gap and NEVER through a door, open or shut, because its lightBlockers bit is written on spawn and
the door moving never updates it. So the two openings start from opposite errors — the gap is
classified outdoors and lit like it, the door is classified indoors and gets no light through it.

THE BASELINE THIS FILE IS FOR: those two buildings should render alike. Not identical — a door leaf
is a real object and a gap is a hole — but alike enough that a player cannot tell which building the
mod thinks is indoors. Every probe here is a PAIR, one cell per building at the mirror position, and
the interesting number is always the difference between the two rather than either one.

FOUR ARMS, TWO HOURS:

  midnight, vanilla            §27 off entirely — what the game does, and the reference for "alike"
  midnight, flat beam          §27 as it ships today: mask plus the flat additive beam
  midnight, max                §27 composing max(vanilla, ours) with the mask after
  noon, vanilla / noon, max    the sky half, where the indoor/outdoor classification actually bites

The noon arms exist because the midnight ones cannot see the classification at all: at midnight
there is no sky light to occlude, so §7b's blackout has nothing to remove and both buildings read
their torch alone. A parity claim tested only at midnight would be silent about the larger of the
two asymmetries.
"""

import json
import os

# Scene anchor. Every offset in this file is local to map centre plus (0, 45), matching the other
# §27 scenarios so the room sits clear of the save's own colony.
ANCHOR = "0,45"
ANCHOR_Z = 45

# Building centres, 32 cells apart. A torch reaches 12, so 32 keeps each building's light entirely
# its own — at 24 or less the two lamps would overlap in the middle and every "difference between
# the pair" reading would carry the other building's light in it.
WEST = -16
EAST = 16

# Nine by nine outer ring, so the interior is seven by seven and the torch has three cells of run
# to the opening. Small enough that one screenshot holds both buildings legibly at zoom 30.
HALF = 4

# The torch sits three cells north of the opening, on the building's axis.
TORCH_Z = -1

# Probe cells, as local (x, z) relative to each building's centre. Mirrored exactly, so the pair
# reads the same geometry in both buildings.
INSIDE = (0, 2)        # deep inside, opposite the opening: the "is this room blacked out" cell
BESIDE = (2, 0)        # off-axis interior, where the lamp's light is not aimed at the opening
THRESHOLD = (0, -5)    # first cell outside the opening
BEAM = (0, -7)         # three cells out, where a beam has separated from the building


def step(t, **args):
    return {"type": t, "args": {k: str(v) for k, v in args.items()}}


def probe(name, expected, tol):
    return step("Probe", probeName=name, expectedValue=expected, tolerance=tol)


def wall_cells(cx):
    """The nine-by-nine ring around cx, with the south-centre cell left out.

    Left out of BOTH buildings, not just the gapped one: the west building's opening is filled by a
    Door in a later step, and PlaceThings refuses to put a door on a cell that already holds a wall
    ("terrain or an indestructible blocker refuses it"). That refusal does not fail the run at the
    point it happens — the scenario carries on and the west building simply has a solid south wall,
    which reads as a feature that draws no beam rather than as a fixture that was never built.
    """
    cells = []

    for x in range(cx - HALF, cx + HALF + 1):
        if x != cx:
            cells.append((x, -HALF))
        cells.append((x, HALF))

    for z in range(-HALF + 1, HALF):
        cells.append((cx - HALF, z))
        cells.append((cx + HALF, z))

    return cells


def cells_arg(cells):
    return "; ".join(f"{x},{z}" for x, z in cells)


# ---- the feature arms ---------------------------------------------------------------------
#
# EVERY ARM STATES EVERY FLAG. A SetFeature left out of an arm inherits whatever the previous arm
# set, which is how an arm ends up measuring a composition nobody chose; the harness has no notion
# of a default between arms.

FLAGS = [
    "vector_lights",
    "vector_light_penumbra",
    "vector_light_suppress",
    "vector_light_blend",
    "vector_light_mask",
    "vector_light_mask_beam",
    "vector_light_max",
    "vector_light_open_doors",
    "vector_light_door_aperture",
]

ARMS = {
    # §27 off entirely. Not "the mod off" — every other subsystem is still running, so this is the
    # frame a player sees today with the experimental toggle left alone.
    "vanilla": {
        "vector_lights": False,
        "vector_light_penumbra": True,
        "vector_light_suppress": True,
        "vector_light_blend": True,
        "vector_light_mask": True,
        "vector_light_mask_beam": True,
        "vector_light_max": False,
        "vector_light_open_doors": False,
        "vector_light_door_aperture": False,
    },
    # What ships today with §27 turned on: the mask, plus the flat additive beam over the whole
    # visibility polygon. Open doors on, or the west building has no beam to compare at all.
    "flat": {
        "vector_lights": True,
        "vector_light_penumbra": True,
        "vector_light_suppress": True,
        "vector_light_blend": True,
        "vector_light_mask": True,
        "vector_light_mask_beam": True,
        "vector_light_max": False,
        "vector_light_open_doors": True,
        "vector_light_door_aperture": True,
    },
    # The composition under test: max(vanilla, ours) per cell, mask after, no flat term.
    "max": {
        "vector_lights": True,
        "vector_light_penumbra": True,
        "vector_light_suppress": True,
        "vector_light_blend": True,
        "vector_light_mask": True,
        "vector_light_mask_beam": False,
        "vector_light_max": True,
        "vector_light_open_doors": True,
        "vector_light_door_aperture": True,
    },
}


def arm(name):
    return [step("SetFeature", featureName=flag, enabled="true" if ARMS[name][flag] else "false")
            for flag in FLAGS]


# ---- probe pins ---------------------------------------------------------------------------
#
# MEASURED, NOT DERIVED. These are rendered bytes at the end of vanilla's flood, our mask, the
# overlay's corner averaging and ColorInt's projection, so there is no closed form to predict them
# from — they were read off the first run and written back, which is the order DESIGN.md's §20
# colour-temperature pins were established in and for the same reason. Pinned TIGHT (0.01 on a
# 0..255 luminance) because every one of them is deterministic: the scene is static, no pawn is in
# view, and a repeat run reproduces them exactly. A wide tolerance here would let the composition
# drift silently, which is the one thing this file exists to prevent.
LUM_TOL = 0.01

# What each arm reads at each of the four cells, per building. The interesting content of this table
# is its COLUMN structure rather than any single number:
#
#   - `inside` and `beside` are identical in every arm and in both buildings. That is the headline:
#     the max composes to exactly vanilla inside the lit room, so the room's brightness does not move
#     at all — the complaint that started this was that it did.
#   - the door building reads 0.00 at both outside cells for vanilla AND for the flat beam, and
#     21.12 / 16.77 under the max. Vanilla's glow grid never learns a door opened, so it delivers
#     nothing past one; the max is what puts light there.
#   - the gap building loses light going the other way, 41.75 -> 27.12, because the mask takes back
#     the share of an aperture cell our polygon cannot see. That is §27 working, and it is the level
#     the two buildings have to meet at.
#
# THE FLAT BEAM READS 0.00 OUTSIDE THE DOOR AND IS NOT ABSENT. It draws its own additive fan at
# AltitudeLayer.VisEffects, above this mesh entirely, so a vertex-colour probe cannot see it at all.
# Where each arm puts its light is a real difference between them and not an artefact — but it does
# mean the brightness comparison BETWEEN arms has to be made on pixels, which DESIGN.md carries.
READINGS = {
    #            door                                gap
    #            inside   beside   thresh   beam     inside   beside   thresh   beam
    "vanilla": (52.1702, 60.5914,  0.0000,  0.0000, 52.1702, 60.5914, 41.7490, 23.9788),
    "flat":    (52.1702, 60.5914,  0.0000,  0.0000, 52.1702, 60.5914, 27.1192, 17.7702),
    "max":     (52.1702, 60.5914, 21.1232, 16.7702, 52.1702, 60.5914, 27.1192, 17.7702),
}

CELL_NAMES = ["inside", "beside", "threshold", "beam"]


# What §27's DRAWN pass did in each arm, observed at the Graphics.DrawMesh call rather than derived
# from the flags that were set. `meshes` is 0 or 2 — one fan per lamp, or the pass standing down —
# and `queue` is vanilla MoteGlow's, which is the pin that would have caught phase 2b's shader bundle
# declaring the default Transparent (3000) and landing on the wrong side of the lighting overlay's
# multiply. Triangles are deliberately NOT pinned: that count is a function of how many corners each
# polygon found and a fixture edit legitimately moves it, where a mesh count of 0 against 2 is a
# statement about the composition itself. Both values measured, not predicted.
DRAWN = {
    "vanilla": (0, 0),
    "flat": (2, 3151),
    "max": (0, 0),
}


def read_pairs(name):
    steps = []
    values = READINGS[name]
    meshes, queue = DRAWN[name]

    steps.append(probe("vector_light_drawn_meshes", meshes, 0))
    steps.append(probe("vector_light_draw_queue", queue, 0))

    for index, cell in enumerate(CELL_NAMES):
        steps.append(probe(f"parity_door_{cell}_lum", values[index], LUM_TOL))
        steps.append(probe(f"parity_gap_{cell}_lum", values[index + 4], LUM_TOL))

    # The classification itself, read as §7b's own output rather than inferred from brightness: the
    # sky-cover alpha the shader samples.
    #
    # 128 AGAINST 100 IS THE SECOND ASYMMETRY, and §27 does not touch it — it reads the same in every
    # arm, which is exactly why it is pinned in every arm. The doored building is an enclosed Room,
    # so §7b treats it as inside; the gapped one merges with the outdoors, comes back
    # UsesOutdoorTemperature, and is therefore classified an EAVE — a porch — and left at vanilla's
    # own RoofedAreaMinSkyCover of 100. One missing wall cell, 28/255 more sky admitted to an
    # otherwise identical roofed interior. Pinned rather than fixed: the fix is a change to what
    # "inside" MEANS, which is §7b's and §15's business and a larger call than a composition.
    steps.append(probe("parity_door_sky_cover", 128, 0.5))
    steps.append(probe("parity_gap_sky_cover", 100, 0.5))

    # Vanilla's own gameplay light at the same two interior cells. §27 never writes it, so this pair
    # is the fixture's control: if it is ever unequal the two buildings are not mirror images and
    # every other difference in the file is measuring the fixture rather than the composition. Both
    # read vanilla's artificial cap of 0.5, which is also what says the two torches sit at the same
    # distance from the cell being read.
    steps.append(probe("parity_door_ground_glow", 0.5, 0.005))
    steps.append(probe("parity_gap_ground_glow", 0.5, 0.005))

    return steps



def build():
    steps = [
        step("SetTile", latitude=45),
        step("SetSeason", dayOfYear=40),
        step("SetWeather", weatherDef="Clear", instant="true"),
        step("SetTerrain", def_="Concrete", width=70, height=30, offset=ANCHOR, clear="true"),
    ]

    # Both rings in one PlaceThings so the two buildings cannot drift apart under an edit.
    walls = wall_cells(WEST) + wall_cells(EAST)
    steps.append(step("PlaceThings", def_="Wall", stuff="BlocksGranite", offset=ANCHOR,
                      layout="cells", clear="true", cells=cells_arg(walls)))

    steps.append(step("PlaceThings", def_="Door", stuff="WoodLog", offset=ANCHOR,
                      layout="cells", cells=cells_arg([(WEST, -HALF)])))

    steps.append(step("PlaceThings", def_="TorchLamp", offset=ANCHOR, layout="cells",
                      cells=cells_arg([(WEST, TORCH_Z), (EAST, TORCH_Z)])))

    # Roofed edge to edge, both of them, which is the premise: the buildings differ in ROOM, not in
    # roof. Painted after the walls so the roof grid is written over cells that already hold them.
    for cx in (WEST, EAST):
        steps.append(step("SetRoof", def_="RoofConstructed", width=2 * HALF + 1, height=2 * HALF + 1,
                          offset=f"{cx},{ANCHOR_Z}"))

    # Held open for every arm including vanilla's, so "the door is open" is never one of the things
    # that differs between arms.
    steps.append(step("SetDoorOpen", offset=f"{WEST},{ANCHOR_Z - HALF}", open="true"))

    steps.append(step("LookAt", offset=ANCHOR, zoom=34))

    # ---- midnight ---------------------------------------------------------------------------
    steps.append(step("SetTime", hour=0))

    # The first capture of a run carries the HUD, so it is thrown away rather than compared.
    steps.extend(arm("vanilla"))
    steps.append(step("Screenshot", fileName="warmup_discard.png"))

    for name in ("vanilla", "flat", "max"):
        steps.extend(arm(name))
        steps.extend(read_pairs(name))
        steps.append(step("Screenshot", fileName=f"parity_midnight_{name}.png"))

    # ---- noon -------------------------------------------------------------------------------
    #
    # Pinned next to the sky-cover probes rather than assumed: RimWorld's clock does not put the sun
    # anywhere in particular at a given hour, and an arm that quietly ran at dusk would report the
    # classification asymmetry as small for a reason that has nothing to do with the classification.
    steps.append(step("SetTime", hour=12))
    steps.append(probe("sun_elevation", 56.72, 0.5))

    for name in ("vanilla", "max"):
        steps.extend(arm(name))
        steps.extend(read_pairs(name))
        steps.append(step("Screenshot", fileName=f"parity_noon_{name}.png"))

    return steps


def main():
    root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    out = os.path.join(root, "Tests", "Scenarios", "room_parity.json")

    scenario = {
        "name": "room_parity",
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
