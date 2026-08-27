#!/usr/bin/env python3
"""The renderer handover across a door's swing, filmed under the shipped default.

WHY THE EXISTING FILM CANNOT SEE THIS. vector_light_door_aperture.json films the same swing and is
the right scene for it, but BOTH of its arms set vector_light_door_glow_blocker false -- it predates
the flag being on by default. So it has never had vanilla's grid moving in it at all, and the grid
moving is the entire subject here.

WHAT IS BEING LOOKED FOR. The composition picks a renderer PER CELL from whether vanilla delivered
there: VectorLightLiftMath.VanillaBentToArrive answers true wherever vanilla's flood never arrived,
SurvivingShare then returns 0, and the fragment program subtracts nothing and multiplies the whole
beam. That is correct for a cell vanilla genuinely cannot reach. It was ALSO what happened for the
whole of a door's slide, because the grid only opened on the last quantisation step -- so the beam
was drawn by our model alone for forty ticks and then, one frame from the end of its own animation,
began having vanilla's share subtracted from it. Not brighter, not wider: a different renderer.

The fix opens the grid on the FIRST step the leaves part, so one regime covers the swing. It is not
flag-gated -- there is no arrangement of flags that reproduces the old behaviour, because the old
behaviour was the two halves disagreeing rather than a feature -- so this is an A/B across BUILDS,
not across arms. Run it on the branch and on the commit before it and diff the per-frame curves.

WHY TWO ARMS ANYWAY. Arm B holds the glow blocker off, which is the composition owning the render
outright for the whole swing AND at the end of it. It is the shape a single-renderer curve has, and
it is the control that says a step in arm A is a handover rather than something the scene does on its
own -- a torch flickers, and 36 frames of flicker is a lot of chances to look like a step.

    python3 Tools/ScenarioGen/gen_vector_light_door_handover.py
"""

import json
import os

FRAMES = 36          # x2 ticks per frame (Wait + Screenshot each cost one), so ~72 ticks of a 45-tick swing
DOOR = "0,45"

def step(t, **args):
    return {"type": t, "args": {k: str(v) for k, v in args.items()}}

def probe(name, expected, tolerance, description=None):
    s = {"type": "Probe", "args": {
        "probeName": name, "expectedValue": str(expected), "tolerance": str(tolerance)}}
    if description:
        s["description"] = description
    return s

def scene():
    """Identical to vector_light_door_aperture's plate, so the two films are comparable frame for
    frame. Zoom 14 puts the doorway and the yard beyond it across most of the frame."""
    return [
        step("SetTile", latitude=45),
        step("SetSeason", dayOfYear=40),
        step("SetWeather", weatherDef="Clear", instant="true"),
        step("SetTerrain", **{"def": "Concrete", "width": 40, "height": 24,
                              "offset": DOOR, "clear": "true"}),
        step("PlaceThings", **{
            "def": "Wall", "stuff": "BlocksGranite", "offset": DOOR, "layout": "cells",
            "clear": "true",
            "cells": "; ".join(
                [f"-13,{z}" for z in range(-7, 8)]
                # -12..-1, NOT through 0: the x=0 column below supplies its own (0,-7) and (0,7).
                # Running these to 0 duplicates both, and PlaceThings REJECTS THE WHOLE STEP on a
                # duplicate offset -- so the room is never built, the run continues against open
                # ground, and 74 perfectly plausible frames come back with no walls in them.
                + [f"{x},-7" for x in range(-12, 0)]
                + [f"{x},7" for x in range(-12, 0)]
                + [f"0,{z}" for z in list(range(-7, 0)) + list(range(1, 8))])}),
        step("PlaceThings", **{"def": "Door", "stuff": "WoodLog", "offset": DOOR,
                               "layout": "cells", "clear": "true", "cells": "0,0"}),
        step("PlaceThings", **{"def": "TorchLamp", "offset": DOOR, "layout": "cells",
                               "cells": "-3,0"}),
        step("SetTime", hour=0),
        step("LookAt", offset=DOOR, zoom=14),
    ]

def flags(glow_blocker):
    """STATED IN FULL IN EVERY ARM, never inherited. A committed frame in this repo once measured
    17.08 instead of 20.23 because an arm inherited a flag it did not state."""
    on = ["vector_lights", "vector_light_penumbra", "vector_light_suppress", "vector_light_blend",
          "vector_light_mask", "vector_light_mask_beam", "vector_light_open_doors",
          "vector_light_door_aperture"]
    out = [step("SetFeature", featureName=f, enabled="true") for f in on]
    out.append(step("SetFeature", featureName="vector_light_door_glow_blocker",
                    enabled="true" if glow_blocker else "false"))
    return out

def arm(tag, glow_blocker, description):
    """One filmed swing. Hand-rolled Wait/Screenshot pairs rather than TickLapse, because
    AdvanceTicks is a JUMP and a door's own Tick() never runs under it -- the first cut of the phase 2
    film came back with the aperture pinned at 0 for all thirty frames and the scenario passing."""
    out = flags(glow_blocker)
    out[0]["description"] = description

    # Shut and settled first, so the swing filmed below is a real one from zero. A paused scenario
    # cannot advance OpenPct at all, which is why this arm runs the clock rather than jumping it.
    out += [
        step("SetDoorOpen", offset=DOOR, open="false"),
        step("SetTimeSpeed", speed="normal"),
        step("Wait", frames=60),
        step("SetTimeSpeed", speed="paused"),
        probe("door_aperture", 0, 0,
              "Shut before the film starts. Pinned at tolerance 0 -- if the door is already part "
              "way open the whole curve below is measuring the wrong half of a swing."),
        step("Screenshot", fileName=f"handover_{tag}_warmup.png"),
        step("SetTimeSpeed", speed="normal"),
        step("SetDoorOpen", offset=DOOR, open="true"),
    ]

    for i in range(1, FRAMES + 1):
        out.append(step("Wait", frames=1))
        out.append(step("Screenshot", fileName=f"handover_{tag}_{i:04d}.png"))

    out += [
        step("SetTimeSpeed", speed="paused"),
        probe("door_aperture", 1, 0, "The swing completed inside the film rather than after it."),
        probe("door_aperture_watched", 0, 0),
        probe("vector_light_lit_area", 468.896484, 2,
              "The bare-doorway polygon, identical in both arms: the glow blocker moves VANILLA's "
              "light and must not move ours. An arm that differs here is measuring two changes."),
    ]

    # AND THE CLOSE, which carries the same defect mirrored and over more of the animation. The bit
    # used to be restored on the FIRST tick of a close while our polygon kept drawing a doorway and
    # ramped down over the whole slide -- so vanilla stopped delivering, VanillaBentToArrive went true
    # again, and the beam spent the entire close in the our-model-owns-it renderer. Filming only the
    # open would have verified half a fix.
    out += [
        step("SetTimeSpeed", speed="normal"),
        step("SetDoorOpen", offset=DOOR, open="false"),
    ]
    for i in range(1, FRAMES + 1):
        out.append(step("Wait", frames=1))
        out.append(step("Screenshot", fileName=f"handover_{tag}_shut_{i:04d}.png"))
    out += [
        step("SetTimeSpeed", speed="paused"),
        probe("door_aperture", 0, 0, "The close completed inside the film too."),
        probe("door_aperture_watched", 0, 0),
    ]
    return out

def main():
    steps = scene()
    steps += arm(
        "on", True,
        "ARM A -- THE SHIPPED DEFAULT, all three door flags on. Vanilla's grid opens during this "
        "swing, so this is the arm the renderer handover happens in. On the build before the fix the "
        "grid opened on the LAST quantisation step and the per-frame brightness curve steps once, "
        "near the end; with the fix it opens on the first and the curve is smooth.")
    steps += arm(
        "off", False,
        "ARM B -- THE CONTROL, glow blocker off. Vanilla never delivers past this door at all, so "
        "our model owns the render for the whole swing AND at the end of it: one renderer by "
        "construction. This is the shape a curve with no handover in it has, which is what makes a "
        "step in arm A a handover rather than the torch flickering.")

    doc = {
        "name": "vector_light_door_handover",
        "saveFile": "minimal_colony.rws",
        "description": __doc__.strip().split("\n\n")[1].replace("\n", " "),
        "steps": steps,
    }

    out = os.path.join(os.path.dirname(__file__), "..", "..",
                       "Tests", "Scenarios", "vector_light_door_handover.json")
    with open(os.path.abspath(out), "w") as f:
        json.dump(doc, f, indent=2)
        f.write("\n")
    print(f"wrote {os.path.abspath(out)}  ({len(steps)} steps, {FRAMES} frames per arm)")

if __name__ == "__main__":
    main()
