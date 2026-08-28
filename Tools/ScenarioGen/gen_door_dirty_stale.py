#!/usr/bin/env python3
"""Does declining a door swing's section flags leave anything on screen stale?

THE QUESTION, AND WHY NO EXISTING SCENARIO CAN ANSWER IT. vector_light_door_dirty_suppress lets a
door's blocker write change the glow grid without flagging sections for redraw, on the argument that
the sections which need to look different are already flagged from the coverage delta. That argument
has a hole in it, and the hole is geometric rather than hypothetical: vanilla's flood is geodesic and
keeps bending after the doorway, so a cell lit only by a path that WRAPS AROUND A CORNER has its glow
changed by the re-flood while our straight-line coverage never moved. Nobody flags that section. It
holds the overlay it had before the door opened, with no exception, no log line, and no other probe
moving.

stress_door_visual cannot see it. That scenario runs its clock, so a cross-arm frame diff selects
hours of sky -- measured, two runs of the SAME configuration differ on 80.03% of pixels at median
dE 8.84. A defect that lives in a pocket of one room is invisible underneath that.

SO THIS ONE IS PAUSED, AND THE SCENE IS BUILT AROUND THE HOLE. A torch in the left room, a door in
the dividing wall, and in the right room a wall stub reaching in from the south wall. The pocket
behind that stub is the whole point: our polygon cannot see into it from the doorway, because the stub
is in the way, while vanilla's flood walks around the stub's tip and lights it. That pocket is where a
stale section would sit, and if the suppression is safe it is where nothing will happen.

THREE ARMS, AND THE CONTROL IS NOT OPTIONAL:

    full        the shipped arrangement, suppression off. The correct reference.
    suppressed  the same, with the flag on. Must be pixel-identical to 'full'.
    full_b      'full' again. Paused and with an identical scene, this must read 0.00% of pixels
                against 'full' -- and if it does not, the comparison above is measuring the harness
                and not the flag.

THE ORDER INSIDE AN ARM IS LOAD-BEARING AND IS THE EASIEST THING TO GET WRONG HERE. Every feature flag
except the one under test calls VectorLightRedraw.ForceRebuild on flip, which is a whole-map rebake --
it HEALS exactly the staleness this scenario exists to catch. So each arm sets its flags with the door
already SHUT, and only then opens it. A scenario that flipped a flag after the swing would photograph
a freshly rebuilt map and report the feature as safe whatever it does.

vector_light_door_dirty_suppress deliberately does not rebuild on flip, for the same reason.

THE SWING NEEDS TICKS, NOT FRAMES. A door animates on the tick counter and GlowGrid's re-flood runs in
GlowGridUpdate, so the clock has to advance for the provocation to happen at all. It is unpaused for
exactly the swing and paused again before anything is measured, so the captures are of a settled map
rather than of a door caught mid-slide -- which is the other way this comparison could measure timing
instead of the flag.

    python3 Tools/ScenarioGen/gen_door_dirty_stale.py
"""

import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
SCEN = os.path.abspath(os.path.join(HERE, "..", "..", "Tests", "Scenarios"))
TARGET = os.path.join(SCEN, "door_dirty_stale.json")

# The plate, as offsets from map centre. Kept small: the defect is a pocket a few cells across and a
# wide shot would put it under a handful of pixels.
ORIGIN = "0,45"

# Room bounds. The dividing wall runs at x = 0 with the door in the middle of it.
MIN_X, MAX_X = -11, 11
MIN_Z, MAX_Z = -9, 9
DOOR = (0, 0)
TORCH = (-2, 0)

# The stub, reaching in from the south wall. Its tip is what vanilla's flood walks around and our
# polygon stops at, so the gap between its tip and the door's sight line is the whole experiment.
STUB_X = 4
STUB_MIN_Z, STUB_MAX_Z = -9, -3

# Frames the clock is allowed to run for the swing plus the re-flood. Generous rather than tight: a
# swing cut short leaves the door mid-slide and the two arms then differ by where the animation
# stopped, which is drift dressed up as a result.
SWING_FRAMES = 90

# Frames held after pausing, before the capture. The draw is what turns a dirty section into a
# regenerated one, so a capture on the pause frame itself can photograph work that had not happened.
SETTLE_FRAMES = 6


def step(kind, **args):
    return {"type": kind, "args": {k: str(v) for k, v in args.items()}}


def cells(pairs):
    return "; ".join(f"{x},{z}" for x, z in pairs)


def walls():
    """The outer rectangle, the dividing wall, and the stub -- minus the door's own cell."""
    out = []

    for x in range(MIN_X, MAX_X + 1):
        out.append((x, MIN_Z))
        out.append((x, MAX_Z))

    for z in range(MIN_Z + 1, MAX_Z):
        out.append((MIN_X, z))
        out.append((MAX_X, z))
        out.append((0, z))

    out.append((0, MIN_Z))
    out.append((0, MAX_Z))

    for z in range(STUB_MIN_Z, STUB_MAX_Z + 1):
        out.append((STUB_X, z))

    return [cell for cell in dict.fromkeys(out) if cell != DOOR]


def feature_steps(suppress):
    """Every flag this scenario depends on, stated, per the repo rule.

    The three door flags are ON in all three arms -- this is not the 'visual' comparison, and the
    whole point is that vanilla's wash IS arriving through the doorway. The only thing that moves
    between arms is whether that arrival is allowed to flag sections.

    The cloud flags are off per CLAUDE.md. This scenario is paused, so the sheet would not drift
    between captures within an arm -- but it would still shade different outdoor cells than a run
    without it, and the plate has open ground around it.
    """
    flags = [
        ("cloud_cover", False),
        ("cloud_sheet", False),
        ("cloud_presence", False),
        ("cloud_deck_varieties", False),
        ("cloud_volume", False),
        ("vector_lights", True),
        ("vector_light_penumbra", True),
        ("vector_light_suppress", True),
        ("vector_light_blend", True),
        ("vector_light_mask", True),
        ("vector_light_mask_beam", True),
        ("vector_light_changed_dirty", True),
        ("vector_light_stale_polygon", True),
        ("vector_light_open_doors", True),
        ("vector_light_door_aperture", True),
        ("vector_light_door_glow_blocker", True),
        ("vector_light_door_dirty_suppress", suppress),
    ]
    return [step("SetFeature", featureName=name, enabled="true" if on else "false")
            for name, on in flags]


def arm(name, suppress):
    """One arm: shut the door, set the flags, open it, settle, photograph.

    THE SHUT COMES FIRST AND IT IS NOT A TIDY-UP. The provocation under test is the TRANSITION from
    shut to open, so an arm that inherited an already-open door from its predecessor would set its
    flags, rebuild, and then photograph a map on which nothing had happened since.
    """
    steps = [
        step("SetTimeSpeed", speed="normal"),
        step("SetDoorOpen", offset=ORIGIN, open="false"),
        step("Wait", frames=SWING_FRAMES),
        step("SetTimeSpeed", speed="paused"),
    ]

    # Flags with the door shut, so the rebuild they provoke lands BEFORE the swing rather than after
    # it. See the module header: a rebuild after the swing heals the defect being looked for.
    steps += feature_steps(suppress)
    steps.append(step("Wait", frames=SETTLE_FRAMES))

    # Drained here so each arm's counters describe its own swing rather than the run so far.
    steps.append(step("Probe", probeName="vector_light_bake_reset",
                      expectedValue=0, tolerance=1000000000))

    steps += [
        step("SetTimeSpeed", speed="normal"),
        step("SetDoorOpen", offset=ORIGIN, open="true"),
        step("Wait", frames=SWING_FRAMES),
        step("SetTimeSpeed", speed="paused"),
        step("Wait", frames=SETTLE_FRAMES),
        step("Screenshot", fileName=f"door_dirty_stale_{name}", hideUi="true"),
    ]

    # RECORDED, NOT PINNED, on this repo's rule against pins nobody has measured. The pair that
    # matters is suppressed_dirty_sections (nonzero only in the arm that declines flags, which is how
    # the arm proves the flag reached the code) beside mask_applies (what actually regenerated).
    for probe in ("vector_light_section_dirties", "vector_light_mask_applies",
                  "vector_light_bakes", "vector_light_mask_skips_dirty",
                  "vector_light_suppressed_dirty_calls",
                  "vector_light_suppressed_dirty_sections"):
        steps.append(step("Probe", probeName=probe, expectedValue=0, tolerance=1000000000))

    return steps


def build():
    steps = [
        step("SetTile", latitude=45),
        step("SetSeason", dayOfYear=40),
        step("SetWeather", weatherDef="Clear", instant="true"),
        step("SetTerrain", **{
            "def": "Concrete", "width": MAX_X - MIN_X + 3, "height": MAX_Z - MIN_Z + 3,
            "offset": ORIGIN, "clear": "true"}),
        step("PlaceThings", **{
            "def": "Wall", "stuff": "BlocksGranite", "offset": ORIGIN, "layout": "cells",
            "clear": "true", "cells": cells(walls())}),
        step("PlaceThings", **{
            "def": "Door", "stuff": "WoodLog", "offset": ORIGIN, "layout": "cells",
            "clear": "true", "cells": cells([DOOR])}),
        step("PlaceThings", **{
            "def": "TorchLamp", "offset": ORIGIN, "layout": "cells",
            "cells": cells([TORCH])}),
        # width/height/offset, matching every other SetRoof in the suite. An arg name this step does
        # not recognise leaves the rooms UNROOFED and does not stop the run -- and an unroofed room is
        # an outdoor room, which would put sky light on the very cells the pocket is measured in.
        step("SetRoof", **{
            "def": "RoofConstructed", "width": MAX_X - MIN_X + 1, "height": MAX_Z - MIN_Z + 1,
            "offset": ORIGIN}),
        step("SetTime", hour=0),
        step("LookAt", offset=ORIGIN, zoom=17),
        # ESTABLISH BEFORE THE PALETTE. SetGlowColors drives CompGlower's setter, which deregisters
        # and re-registers the glower — and §27's roster is only maintained while the subsystem is
        # running, so a repaint applied before it is on is a repaint the field never hears about.
        # stress_light_colony orders these the same way and for the same reason.
        step("SetFeature", featureName="vector_lights", enabled="true"),
        step("SetFeature", featureName="vector_light_mask", enabled="true"),
        step("SetTimeSpeed", speed="normal"),
        step("Wait", frames=30),
        step("SetTimeSpeed", speed="paused"),
        # A torch's shipped glowRadius is 10, which does not reach the pocket by the geodesic route --
        # and a scene that starves the effect of range is indistinguishable from a working one. This
        # widens the emitter so vanilla's wrap-around light genuinely arrives there.
        step("SetGlowColors", colors="252,187,113", radii="17.25"),
        step("Wait", frames=4),
        # THE FIRST CAPTURE CARRIES THE HUD whatever hideUi says; it is honoured from the second on.
        # Discarded here so no arm's frame is the one holding a message log that differs run to run.
        step("Screenshot", fileName="door_dirty_stale_warmup_discard"),
    ]

    steps += arm("full", suppress=False)
    steps += arm("suppressed", suppress=True)
    steps += arm("full_b", suppress=False)

    return {
        "name": "door_dirty_stale",
        "saveFile": "minimal_colony.rws",
        "description": (
            "Whether declining a door swing's section flags leaves anything stale on screen. "
            "A torch in the left room, a door in the dividing wall, and a wall stub reaching in from "
            "the south wall of the right room. The pocket behind that stub is the experiment: our "
            "polygon cannot see into it from the doorway because the stub is in the way, while "
            "vanilla's geodesic flood walks around the stub's tip and lights it. So opening the door "
            "changes vanilla's glow there and does NOT change our coverage there — which is precisely "
            "the case in which vector_light_door_dirty_suppress flags nobody and a section is left "
            "holding the overlay it had before the door opened."
            "\n\n"
            "PAUSED, because stress_door_visual cannot see this: that scenario runs its clock, and two "
            "runs of the SAME configuration differ on 80.03% of pixels at median ΔE 8.84 purely from "
            "hours of sky passing between arms. A defect the size of one pocket is invisible under "
            "that."
            "\n\n"
            "THREE ARMS. 'full' is the shipped arrangement with the suppression off — the correct "
            "reference. 'suppressed' is the same with the flag on and must be pixel-identical to it. "
            "'full_b' repeats the reference, and being paused on an identical scene it must read "
            "0.00% of pixels against 'full'; if it does not, the comparison is measuring the harness "
            "rather than the flag and nothing else in the run means anything."
            "\n\n"
            "EACH ARM SHUTS THE DOOR, SETS ITS FLAGS, AND ONLY THEN OPENS IT. Every flag here except "
            "the one under test rebuilds the whole map on flip, which heals exactly the staleness "
            "being looked for — so an arm that set a flag after the swing would photograph a freshly "
            "rebuilt map and report the feature safe whatever it does. The clock runs for the swing "
            "and is paused again before any capture, because a door animates on ticks and the glow "
            "re-flood runs in GlowGridUpdate."
        ),
        "steps": steps,
    }


def main():
    scenario = build()
    with open(TARGET, "w") as handle:
        json.dump(scenario, handle, indent=2)
        handle.write("\n")
    print(f"wrote {TARGET} ({len(scenario['steps'])} steps)")


if __name__ == "__main__":
    main()
