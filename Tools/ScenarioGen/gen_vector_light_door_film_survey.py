#!/usr/bin/env python3
"""Which torch distance photographs, for the showcase clip. Three doorways, one frame.

WHY A SURVEY EXISTS AT ALL. The first cut of vector_light_door_film.json inherited the gate
scenarios' torch at three cells inside the door, which is a placement chosen so a probe cell lands
somewhere useful and not because it looks like anything. It measured: masked median dE 1.58 over
5.62% of the frame, the yard going L* 10.4 -> 15.9 at the doorway. That is a real effect and a
weak picture -- "visible on close inspection" is the wrong band for a Workshop listing, where the
image has about a second to say what the mod does.

Distance is the lever, and it moves TWO things at once, which is why it needed photographing rather
than deriving: closer to the aperture the emitter is brighter at the doorway (inverse falloff), AND
the doorway subtends a much wider angle from it, so the fan outside goes from a narrow shaft to
most of a half-disc. Those pull in opposite aesthetic directions -- brighter but blobbier -- and no
number decides between them.

WHY THREE DOORS RATHER THAN THREE RUNS. Moving one torch between shots means bulldozing it, which
means leaving a wall stub in the room, which changes the interior between arms; and three separate
runs means three boots plus a torch flicker that is at a different phase in each. Three doorways in
one east wall, four cells apart, each with its own torch at its own distance, puts all three fans in
a single capture under a single sky at a single instant. The comparison is then between pixels in
one frame rather than between frames.

The second capture adds granite chunks in the middle doorway's yard, testing a separate question:
whether the beam reads better falling across OBJECTS than across bare concrete. Flat ground shows
only the gradient; a lit chunk with a dark side is a shape, and shapes survive being scaled down to
a listing thumbnail in a way gradients do not.

THE THIRD SECTION IS ABOUT THE CLOCK, and it is here because the first properly-lit cut of the film
came back with a broken loop. A door has to be filmed with the clock RUNNING (AdvanceTicks is a jump
and Building_Door.Tick never runs under it), and over the ~250 ticks that costs, the whole frame at
hour 0 brightened by dE 2.17 -- "visible at a glance" -- with the far sky, nowhere near the door,
rising by exactly as much as the lit yard. Nothing about the door: it is ambient drift. On a LOOP
that lands entirely in the wrap, where the last frame cuts back to a first frame two L* darker, and
it reads as a flash. So the sweep below holds the same scene unpaused for the film's own duration at
each candidate hour and reports the drift, and the film is shot wherever the night is flattest
rather than at the roundest number.

    python3 Tools/ScenarioGen/gen_vector_light_door_film_survey.py
"""

import json
import os

CENTRE = "0,45"

# z of each doorway, and how many cells inside it that doorway's torch sits. Four cells apart so a
# fan from one cannot reach the next -- at one cell the fan is nearly a half-disc and two doors any
# closer together would light each other's yards, which would make the frame unreadable as a
# comparison even though every individual fan was correct.
DOORS = [(4, 3), (0, 2), (-4, 1)]

FLAGS_ON = [
    "vector_lights",
    "vector_light_penumbra",
    "vector_light_suppress",
    "vector_light_blend",
    "vector_light_mask",
    "vector_light_mask_beam",
    "vector_light_shader_max",
    "vector_light_open_doors",
    "vector_light_door_glow_blocker",
    "vector_light_door_aperture",
]


def step(t, **args):
    return {"type": t, "args": {k: str(v) for k, v in args.items()}}


def wall_cells():
    """The room's ring, minus the three door cells. Built as a set so a doorway can be removed by
    name rather than by editing a literal -- PlaceThings rejects the whole step on a duplicate
    offset, and a hand-maintained cell list is exactly where a duplicate creeps in."""
    cells = set()
    for z in range(-7, 8):
        cells.add((-13, z))
        cells.add((0, z))
    for x in range(-12, 0):
        cells.add((x, -7))
        cells.add((x, 7))
    for z, _ in DOORS:
        cells.discard((0, z))
    return "; ".join(f"{x},{z}" for x, z in sorted(cells))


def main():
    steps = [
        step("SetTile", latitude=45),
        step("SetSeason", dayOfYear=40),
        step("SetWeather", weatherDef="Clear", instant="true"),
        step("SetTerrain", **{"def": "Concrete", "width": 40, "height": 24,
                              "offset": CENTRE, "clear": "true"}),
        step("PlaceThings", **{"def": "Wall", "stuff": "BlocksGranite", "offset": CENTRE,
                               "layout": "cells", "clear": "true", "cells": wall_cells()}),
        step("PlaceThings", **{"def": "Door", "stuff": "WoodLog", "offset": CENTRE,
                               "layout": "cells", "clear": "true",
                               "cells": "; ".join(f"0,{z}" for z, _ in DOORS)}),
        step("PlaceThings", **{"def": "TorchLamp", "offset": CENTRE, "layout": "cells",
                               "cells": "; ".join(f"-{d},{z}" for z, d in DOORS)}),
        step("SetTime", hour=0),
        step("LookAt", offset=CENTRE, zoom=11),
    ]

    steps += [step("SetFeature", featureName=f, enabled="true") for f in FLAGS_ON]
    steps.append(step("Screenshot", fileName="doorsurvey_hud_discard.png"))

    # All three doors driven at normal speed and settled together, so the three fans in the capture
    # are at the same point in their own animations as well as under the same sky.
    steps.append(step("SetTimeSpeed", speed="normal"))
    for z, _ in DOORS:
        steps.append(step("SetDoorOpen", offset=f"0,{45 + z}", open="true"))
    steps += [
        step("Wait", frames=90),
        step("SetTimeSpeed", speed="paused"),
        {"type": "Probe",
         "args": {"probeName": "door_aperture", "expectedValue": "1", "tolerance": "0"},
         "description":
             "The MIDDLE door (the probe is registered at map centre + 0,45) is fully open. The "
             "other two are driven identically one step apart, so this stands for all three -- a "
             "survey of fans photographed mid-slide would rank the doors by how far each had got."},
        step("Screenshot", fileName="doorsurvey_three_z11.png"),
        step("LookAt", offset=CENTRE, zoom=8),
        step("Screenshot", fileName="doorsurvey_three_z8.png"),
        step("LookAt", offset=CENTRE, zoom=11),
    ]

    # Second question: does the beam read better across objects than across bare concrete? Chunks
    # only, and only in the middle yard, so the same frame still answers the distance question --
    # scattering them across all three would confound the two comparisons.
    steps += [
        step("PlaceThings", **{"def": "ChunkGranite", "offset": CENTRE, "layout": "cells",
                               "clear": "true",
                               "cells": "2,1; 3,-1; 4,2; 5,0; 3,2; 6,-1"}),
        step("Screenshot", fileName="doorsurvey_chunks_z11.png"),
        step("LookAt", offset=CENTRE, zoom=8),
        step("Screenshot", fileName="doorsurvey_chunks_z8.png"),
    ]

    # Ambient drift per candidate hour. 260 ticks is the film's own span (90 captures at two ticks
    # each, plus its settles), so the number this reports IS the drift the clip would carry rather
    # than a rate that has to be scaled. Every hour is sampled from the same scene with the doors
    # already open and settled, so the pair differs by elapsed time and nothing else.
    for hour in (18, 20, 22, 0, 2, 4):
        steps += [
            step("SetTime", hour=hour),
            step("Wait", frames=2),
            step("Screenshot", fileName=f"doorsurvey_hour{hour:02d}_a.png"),
            step("SetTimeSpeed", speed="normal"),
            step("Wait", frames=260),
            step("SetTimeSpeed", speed="paused"),
            step("Screenshot", fileName=f"doorsurvey_hour{hour:02d}_b.png"),
        ]

    scenario = {
        "name": "vector_light_door_film_survey",
        "saveFile": "minimal_colony.rws",
        "description":
            "Framing and emitter-distance survey for the door-beam showcase clip. Three doorways in "
            "one east wall, four cells apart, with their torches three / two / one cell inside, so "
            "the fans are compared as pixels in a single capture rather than across boots with a "
            "flickering torch at a different phase in each. A second capture adds granite chunks to "
            "the middle yard, asking whether the beam reads better across objects than across bare "
            "concrete. A third section holds the same scene unpaused for the film's own 260 ticks at "
            "six candidate hours and captures each end, because a door must be filmed with the clock "
            "running and at hour 0 that costs dE 2.17 of ambient drift -- which on a loop lands "
            "entirely in the wrap. Asserts nothing beyond the middle door being fully open: it is a "
            "survey, not a gate, and exists so a re-shoot after the effect changes costs one boot "
            "rather than three. Generated by "
            "Tools/ScenarioGen/gen_vector_light_door_film_survey.py.",
        "steps": steps,
    }

    out = os.path.join(os.path.dirname(__file__), "..", "..",
                       "Tests", "Scenarios", "vector_light_door_film_survey.json")
    with open(os.path.normpath(out), "w") as f:
        json.dump(scenario, f, indent=2)
        f.write("\n")
    print(f"{os.path.normpath(out)}: {len(steps)} steps")


if __name__ == "__main__":
    main()
