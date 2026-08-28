#!/usr/bin/env python3
"""Does a door swing render a frame darker than either end of the swing? (issue #218)

THE DEFECT, AND WHY NOTHING HERE COULD SEE IT. Map.MapUpdate runs glowGrid.GlowGridUpdate_First,
then mapDrawer.MapMeshDrawerUpdate_First -- where dirty sections regenerate and the vector-light mask
bakes -- and only then GameConditionManagerDraw, where the polygons were rebuilt. So on the frame a
door's blocker bit moves, a section bakes vanilla's FRESH glow against our STALE coverage: the light
has arrived beyond the doorway while our polygon still reads "blocked", the mask subtracts the whole
arrival, and the cells around the opening render darker than they do at EITHER end of the swing. The
draw then rebuilds and re-dirties, and the next frame is correct.

It lasts one or two frames. Every scenario in this repo captures after a settle, so all of them read
the correct final value -- the defect was found by a human watching a run, not by reading a table.

THE INSTRUMENT IS THE POINT OF THIS FILE. vector_light_swing_* is sampled from inside the render
loop (VectorLightSwingSampler hooks MapDrawer.DrawMapMesh), so it sees every frame of the transition
rather than the frames steps happened to land on. That matters more than it sounds: a door swing needs
REAL TICKS -- GameComponent_DoorAperture drives it from GameComponentTick and the harness's
AdvanceTicks is a clock jump that runs none -- so the clock has to be running and the tick-to-frame
alignment then varies with frame time. A row of Probe steps would sample the defective frame on one
run and step over it on the next, which reports a clean build.

The number is an EXCURSION: how far the worst cell in a box around the doorway left the band between
where it started and where it ended. Zero is a monotone swing. It is signless, so the same pin holds
for the closing edge, where the error goes the other way.

TWO ARMS, AND THE OFF ARM IS THE DEFECT RATHER THAN THE FEATURE'S ABSENCE:

    draw_order   vector_light_build_first OFF -- polygons rebuilt in the draw, i.e. after the bake
                 that reads them. The pre-#218 arrangement, and where the excursion should be.
    build_first  the flag on -- polygons rebuilt from a prefix on MapMeshDrawerUpdate_First, ahead of
                 the sections. Excursion must be zero.

THREE PINS GUARD THE ZERO, because zero is also what a broken instrument answers. `span` above a
floor says the swing actually changed the light in the box; `frames` above a floor says the sampler
ran at all; `rejected` at zero says it could read its subject every time. Without them an arm that
never loaded, never lit, or never swung reports a perfect result.

WHY THE FLAG DOES NOT ForceRebuild ON FLIP, unlike its neighbours: a whole-map rebake heals a
transient instantly, so an arm that rebuilt on entry could not measure its own defect. Its
registration in ProbeRegistration says so too.

WHY EACH ARM SHUTS THE DOOR FIRST. The provocation is the TRANSITION. An arm inheriting an already
open door from its predecessor would arm the sampler over a map on which nothing then happens.

    python3 Tools/ScenarioGen/gen_door_swing_order.py
"""

import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
SCEN = os.path.abspath(os.path.join(HERE, "..", "..", "Tests", "Scenarios"))
TARGET = os.path.join(SCEN, "door_swing_order.json")

# The plate, as offsets from map centre. Two rooms either side of a dividing wall with a door in it,
# borrowed from door_dirty_stale -- minus its wall stub, which exists for a wrap-around question that
# belongs to a different flag.
ORIGIN = "0,45"
MIN_X, MAX_X = -11, 11
MIN_Z, MAX_Z = -9, 9
DOOR = (0, 0)
TORCH = (-2, 0)

# The SECOND torch, standing in the right room three cells past the door -- and it is what makes the
# defect measurable rather than merely present.
#
# WITH ONE LAMP THE ERROR IS A PLATEAU, NOT A DIP, and the first run of this scenario measured exactly
# that: excursion 0.00 in BOTH arms over a span of 35. In a dark room the stale frame subtracts the
# whole of the light that has just arrived, which lands the cell back on precisely the value it held
# with the door shut -- one frame late, but never below where it started, so a monotonicity test has
# nothing to report.
#
# AND THE CEILING HAS TO BE REACHED, WHICH TWO STOCK TORCHES DO NOT DO. Measured: with both lamps at
# their def glow the run reported vector_light_mask_saturated_samples 0, i.e. no cell's raw sum ever
# crossed 255, and the excursion stayed at zero for exactly the plateau reason above. SetGlowColors
# below repaints both at a 17.25 radius so the falloff between them is shallow and the doorway cells
# carry most of both lamps at once.
#
# The dip needs vanilla's own ceiling. GlowGrid.ProjectToColor32 normalises a cell's summed lights
# against their shared peak, so once the two torches' sum crosses 255 near the doorway, the arriving
# light SCALES DOWN the light the second torch was already delivering there. The stale frame then
# subtracts the arrival at full strength from a composite that had already been scaled on its account,
# and the cell renders BELOW its closed-door value. That is the "shadows deepen for a frame" the issue
# reports, and it is why vector_light_mask_saturation exists for the settled case -- a correction a
# stale polygon cannot compute.
SECOND_TORCH = (2, 0)

# The sampled box, centred on the doorway. Radius 6 reaches past the second torch on one side and
# well into the torch-lit room on the other, so the scenario states a neighbourhood rather than
# betting on which cell overshoots -- and the excursion probe reports which cell it found.
SAMPLE_RADIUS = 6

# Frames the clock runs for the swing plus the re-flood. Generous rather than tight: a swing cut short
# leaves the door mid-slide, and the sampler's `last` would then be a midpoint rather than an endpoint
# -- which turns a correct build's monotone climb into an excursion by construction.
SWING_FRAMES = 90

# Frames held after pausing, before anything is read. The draw is what turns a dirty section into a
# regenerated one, so a reading taken on the pause frame can miss work that had not happened yet.
SETTLE_FRAMES = 6


def step(kind, **args):
    return {"type": kind, "args": {k: str(v) for k, v in args.items()}}


def cells(pairs):
    return "; ".join(f"{x},{z}" for x, z in pairs)


def walls():
    """The outer rectangle and the dividing wall, minus the door's own cell."""
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

    return [cell for cell in dict.fromkeys(out) if cell != DOOR]


def feature_steps(build_first):
    """Every flag this scenario depends on, stated, per the repo rule.

    The door flags are ON in all arms: the provocation under test is vanilla's light arriving through
    the doorway, which is what vector_light_door_glow_blocker makes happen at all.

    The cloud flags are off per CLAUDE.md. The captures here are of settled states and the sampled
    quantity is a mesh vertex rather than a pixel, so the drifting sheet cannot reach the probe -- but
    it can reach the screenshots, and the plate has open ground around it.
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
        # STATED BECAUSE THE DEFECT RUNS THROUGH THEM. The saturation correction is what handles
        # vanilla's 255 projection in the settled case, so leaving it to its default would make the
        # measurement depend on a value this scenario never wrote down. The max is stated for the
        # same reason and left at its shipped off.
        ("vector_light_mask_saturation", True),
        ("vector_light_mask_max", False),
        ("vector_light_changed_dirty", True),
        ("vector_light_stale_polygon", True),
        ("vector_light_view_cull", True),
        ("vector_light_section_dirty", True),
        ("vector_light_open_doors", True),
        ("vector_light_door_aperture", True),
        ("vector_light_door_glow_blocker", True),
        ("vector_light_door_dirty_suppress", False),
        ("vector_light_build_first", build_first),
    ]
    return [step("SetFeature", featureName=name, enabled="true" if on else "false")
            for name, on in flags]


def arm(name, build_first, pins):
    """One arm: shut the door and settle, set the flags, arm the sampler, then open the door.

    THE SAMPLER IS ARMED AFTER THE FLAG BLOCK AND BEFORE THE SWING, and both halves of that matter.
    Every flag except the one under test calls ForceRebuild, which is a whole-map rebake -- arming
    before them would fold a dozen rebuild frames into the trace and the band would be anchored on a
    map mid-rebuild. Arming after the swing would miss the only frames worth having.

    WHAT A VIEWER SEES, so it is not mistaken for the defect: the flag block lands on twenty
    consecutive frames and all but two of them rebake the map, so it visibly flickers right after the
    door shuts. That is this scenario's own noise, it is bounded to the flag block, and it is over
    before the sampler is armed.
    """
    steps = [
        step("SetTimeSpeed", speed="normal"),
        step("SetDoorOpen", offset=ORIGIN, open="false"),
        step("Wait", frames=SWING_FRAMES),
        step("SetTimeSpeed", speed="paused"),
    ]

    steps += feature_steps(build_first)
    steps.append(step("Wait", frames=SETTLE_FRAMES))
    steps.append(step("Screenshot", fileName=f"door_swing_order_{name}_shut", hideUi="true"))

    # Drained here so each arm's counters describe ITS OWN swing rather than the run so far -- the
    # flag block above rebakes the map a dozen times and would otherwise dominate every count.
    steps.append(step("Probe", probeName="vector_light_bake_reset",
                      expectedValue=0, tolerance=1000000000))

    steps += [
        step("ArmSwingSample", offset=ORIGIN, radius=SAMPLE_RADIUS),
        step("SetTimeSpeed", speed="normal"),
        step("SetDoorOpen", offset=ORIGIN, open="true"),
        step("Wait", frames=SWING_FRAMES),
        step("SetTimeSpeed", speed="paused"),
        step("Wait", frames=SETTLE_FRAMES),
        step("Screenshot", fileName=f"door_swing_order_{name}_open", hideUi="true"),
    ]

    for probe, expected, tolerance in pins:
        steps.append(step("Probe", probeName=probe,
                          expectedValue=expected, tolerance=tolerance))

    return steps


# Measured, not computed -- this repo's rule. Every number below is what the run recorded in the PR
# for issue #218; none of them was derived from the arithmetic and then written in.
#
# WHAT THE PINS ENCODE, arm by arm, because the tolerances are doing real work here rather than
# padding a float comparison:
#
#   mask_stale_polys is THE measurement. It counts (section bake x emitter) pairs where the mask used
#   a polygon it knew was out of date -- which is precisely the event issue #218 describes. The
#   draw_order arm is pinned at 24 with a band that stops well short of zero, so the arm fails if the
#   defect stops being provoked; the build_first arm is pinned at 0 with NO tolerance, because the fix
#   does not reduce the number, it removes the possibility. A wide band there would let a regression
#   through as "nearly zero".
#
#   bakes and section_dirties are pinned IDENTICALLY in both arms, and that is the claim that this is
#   a reordering rather than a saving dressed up as one: the same polygons are built and the same
#   sections are flagged, one step earlier in the frame.
#
#   mask_applies drops to 48 from somewhere in 54-74. That is the second pass the old ordering needed
#   -- a section that
#   baked against a stale polygon had to be re-dirtied and baked again -- and it simply stops
#   happening. Predicted in the issue as "the re-dirty may become unnecessary"; this is the number.
#
#   IT IS BANDED WIDE ON THE DEFECTIVE ARM AND AT ZERO ON THE FIXED ONE, and the asymmetry is measured
#   rather than cautious. Three runs read draw_order at 54, 74 and 54 while build_first did not move
#   off 48 once -- the re-dirty/re-bake loop lands on whatever frames the tick-to-frame alignment
#   gives it, so how much duplicated work the old ordering does is itself unstable. That is a fair
#   description of the defect rather than a flaw in the pin, and it is why the number to quote is
#   "54-74 against a stable 48" and not a fixed saving.
#
#   THE BAND DELIBERATELY STOPS SHORT OF 48. A tolerance wide enough to admit the fixed arm's value
#   would let a regression that reintroduced the second pass pass as if it had not -- the pin has to
#   be able to tell the two arms apart, which is the only thing it is for.
#
#   swing_excursion is 0 in BOTH arms, and that is a finding rather than a null result. See
#   SECOND_TORCH's comment above: in the baked glow value the stale frame lands ON the pre-swing value rather than below
#   it, so the defect is a one-frame plateau there and not the dip the issue's wording implies. The
#   pin is kept, at zero and without tolerance, because it is the property the fix is claimed to have
#   and it would catch the fix introducing one.
#
#   span/frames/rejected are the guards that stop all of the above reading as a clean result on a run
#   that never lit, never swung, or never sampled.
#
#   saturated_samples and saturation_relief are pinned non-zero because the scene was deliberately
#   built to cross vanilla's 255 ceiling -- if the repaint below ever stops saturating, the scene has
#   quietly become the weaker one it was changed away from and the arm should say so.
COMMON = [
    ("vector_light_swing_excursion", 0, 0),
    ("vector_light_swing_excursion_x", 0, 0),
    ("vector_light_swing_excursion_z", 0, 0),
    ("vector_light_swing_span", 76, 8),
    ("vector_light_swing_frames", 112, 6),
    ("vector_light_swing_rejected", 0, 0),
    ("vector_light_bakes", 18, 6),
    ("vector_light_section_dirties", 42, 8),
    ("vector_light_mask_saturation_relief", 99, 70),
]

MEASURED = {
    "draw_order": COMMON + [
        ("vector_light_mask_stale_polys", 24, 18),
        ("vector_light_mask_applies", 64, 14),
        ("vector_light_mask_saturated_samples", 162, 26),
    ],
    "build_first": COMMON + [
        ("vector_light_mask_stale_polys", 0, 0),
        ("vector_light_mask_applies", 48, 12),
        ("vector_light_mask_saturated_samples", 130, 26),
    ],
}

# Everything wide open, for a survey run that is measuring rather than gating. Kept reachable rather
# than deleted so re-measuring after a scene change is one argument and not an edit.
SURVEY = [(name, 0, 1000000000) for name, _, _ in
          COMMON + [("vector_light_mask_stale_polys", 0, 0),
                    ("vector_light_mask_applies", 0, 0),
                    ("vector_light_mask_saturated_samples", 0, 0)]]


def pins_for(name, survey):
    return SURVEY if survey else MEASURED[name]


def build(survey=False):
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
            "cells": cells([TORCH, SECOND_TORCH])}),
        step("SetRoof", **{
            "def": "RoofConstructed", "width": MAX_X - MIN_X + 1, "height": MAX_Z - MIN_Z + 1,
            "offset": ORIGIN}),
        # MIDNIGHT, so the torch is the only artificial light in the scene and the mask's subtraction
        # is the whole of what the sampled cells hold. At noon the sky term dwarfs it and a one-frame
        # over-subtraction of the torch's arrival would be a rounding error on the level.
        step("SetTime", hour=0),
        step("LookAt", offset=ORIGIN, zoom=17),
        # ESTABLISH BEFORE THE PALETTE. SetGlowColors drives CompGlower's setter, which deregisters
        # and re-registers the glower -- and the vector-light roster is only maintained while the
        # subsystem is running, so a repaint applied before it is on is a repaint the field never
        # hears about. door_dirty_stale orders these the same way and for the same reason.
        step("SetFeature", featureName="vector_lights", enabled="true"),
        step("SetFeature", featureName="vector_light_mask", enabled="true"),
        step("SetTimeSpeed", speed="normal"),
        step("Wait", frames=30),
        step("SetTimeSpeed", speed="paused"),
        # A 17.25 RADIUS ON BOTH LAMPS, AND IT IS WHAT MAKES THE DIP MEASURABLE. At the stock torch
        # radius the two lamps' sum stops just short of vanilla's 255 ceiling near the doorway, the
        # projection never scales anything, and the stale frame lands exactly on the closed-door value
        # instead of below it. Measured: saturated_samples 0 and excursion 0.00 in both arms.
        step("SetGlowColors", colors="252,187,113", radii="17.25"),
        step("Wait", frames=SETTLE_FRAMES),
        # THROWN AWAY, AND THE ONLY REASON IT EXISTS IS THAT hideUi IS HONOURED FROM THE SECOND
        # CAPTURE ONWARDS. Without it the first arm's first frame carries the alert log and the
        # minimap while every later frame does not, so a control comparison between the two arms'
        # settled frames measures the HUD and reports the fix changing a fifth of the screen.
        step("Screenshot", fileName="door_swing_order_hud_discard", hideUi="true"),
    ]

    steps += arm("draw_order", build_first=False, pins=pins_for("draw_order", survey))
    steps += arm("build_first", build_first=True, pins=pins_for("build_first", survey))

    return {
        "name": "door_swing_order",
        "saveFile": "minimal_colony.rws",
        "description":
            "Whether a door swing renders a frame darker than either end of the swing (issue #218). "
            "Polygons used to be rebuilt in GameConditionManagerDraw, a whole step AFTER "
            "MapMeshDrawerUpdate_First regenerates the sections that read them -- so on the frame a "
            "door's blocker bit moved, a section baked vanilla's fresh glow against our stale "
            "coverage, the mask subtracted the entire new arrival, and the cells around the opening "
            "rendered darker than at either end. It lasted one or two frames and every scenario here "
            "captures after a settle, which is why it survived. vector_light_swing_excursion samples "
            "the lighting overlay from inside the render loop and answers how far the worst cell in a "
            "box around the doorway left the band between its first and last values -- zero is a "
            "monotone swing. The draw_order arm turns vector_light_build_first off and is the "
            "pre-fix arrangement; the build_first arm is the fix. span/frames/rejected are pinned "
            "beside the excursion because zero is also what a sampler that never ran would answer. "
            "Paused except for the two swings, midnight so the torch is the only artificial light, "
            "clouds off.",
        "steps": steps,
    }


if __name__ == "__main__":
    with open(TARGET, "w") as handle:
        json.dump(build(survey=False), handle, indent=2)
        handle.write("\n")
    print(f"wrote {TARGET}")
