#!/usr/bin/env python3
"""A loopable showcase clip of §27e's open-door beam, for the Workshop listing.

This is a MARKETING shot, not a gate. vector_light_door_aperture.json already films the same swing
and asserts things about it; what it cannot do is loop, because it only films the door opening. A
listing GIF that ends with the door open and cuts back to it shut reads as a glitch, so this one
films shut -> open -> held -> closing -> shut and comes back to its own first frame.

WHAT IS DELIBERATELY DIFFERENT FROM THE GATE SCENARIOS, and why each choice is not free:

  - EVERY flag is stated at its SHIPPED DEFAULT, glow blocker included. The gate scenarios hold
    vector_light_door_glow_blocker false because they predate it being on by default, and an arm
    that inherits a flag is an arm measuring something nobody chose (see the 17.08-vs-20.23 note in
    gen_vector_light_door_handover.py). A listing asset has the stricter version of that duty: it
    must show what a default install does, or it is an advertisement for a configuration.

  - The clock RUNS, at normal speed, for the whole clip. AdvanceTicks is a jump and a door's own
    Tick() never runs under it, so TickLapse -- the obvious tool for a tick-driven film -- would
    produce ninety frames of a door that never moves, and pass. Each captured frame therefore costs
    two ticks (the Wait and the Screenshot are one step each, one step per frame), which is what
    sizes the phases below.

  - Zoom 11 rather than the gate scenarios' 14. Nothing about the effect changes; the crop the GIF
    is cut from gets 49 px per cell instead of 39, and GIF is the one format where the difference
    between resampling and not is visible in the flat areas.

The three door_aperture probes are the only assertions here, and they exist because this film's
predecessor came back with the aperture pinned at 0 for all thirty frames while still reporting
green -- a clip of a door that never opened looks exactly like a clip of an effect that does
nothing.

    python3 Tools/ScenarioGen/gen_vector_light_door_film.py
"""

import json
import os

DOOR = "0,45"

# How far inside the door the torch stands, and this is the one number in the file that was
# photographed rather than chosen. vector_light_door_film_survey.json puts three doorways in one
# frame with their torches three, two and one cell inside, and the yard just outside each reads
# L* 12.79 / 14.78 / 16.74 against a control yard of 8.17 -- so one cell is roughly double the lift
# of three, at both the near and the far sample. The gate scenarios' three cells is a placement
# chosen so a probe lands somewhere useful, and filming it cost the first cut of this clip half its
# contrast (masked median dE 1.58, "visible on close inspection", which is the wrong band for a
# listing image that has about a second to make its point).
#
# It moves two things at once, which is why it was photographed rather than derived: the emitter is
# brighter at the aperture, AND the aperture subtends a far wider angle from it, so the fan outside
# goes from a narrow shaft to most of a half-disc. Brighter but blobbier -- no number decides that,
# and the survey frame is what did.
#
# Note that a SECOND torch beside the first would not help: §27 composes emitters with a per-fragment
# max, not a sum, so two lamps either side of a doorway raise the fan's width and not its level.
TORCH_DISTANCE = 1

# THE HOUR IS MEASURED, NOT CHOSEN, and it is the difference between this clip looping and flashing.
# A door can only be filmed with the clock running, which costs ~260 ticks, and over that span the
# ambient is not still: vector_light_door_film_survey.json holds this scene unpaused for exactly that
# long at six candidate hours and reports whole-frame dE 1.55 / 3.46 / 0.00 / 0.93 / 0.86 / 3.24 at
# 18 / 20 / 22 / 00 / 02 / 04. Hour 22 is a genuine plateau -- far sky L* 8.40 at both ends, not
# 8.40 to 8.38 -- and is as dark as midnight, so it costs nothing in contrast.
#
# The first cut of this clip ran at hour 0 and drifted dE 2.17 across its ninety frames, brightening
# the far sky by as much as the lit yard. On a loop every bit of that lands in the wrap, where the
# last frame cuts back to a first frame two L* darker and it reads as a flash. Nothing in the
# scenario's probes could see it: door_aperture was 0 at both ends, correctly, and the defect was in
# the sky.
HOUR = 22

# Phase lengths in CAPTURED FRAMES; each costs two ticks. A wooden door's swing is ~45 ticks, so 30
# frames (60 ticks) covers it with a moment of stillness at the end -- the clip needs the door to
# have visibly ARRIVED before the hold, or the hold reads as the animation stalling.
SHUT_LEAD = 8
OPENING = 30
OPEN_HOLD = 14
CLOSING = 30
SHUT_TAIL = 8

# Stated at their shipped defaults, in full, every one of them. See the module docstring.
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


def probe(name, expected, tolerance, description=None):
    s = {"type": "Probe", "args": {
        "probeName": name, "expectedValue": str(expected), "tolerance": str(tolerance)}}
    if description:
        s["description"] = description
    return s


def scene():
    """vector_light_door_aperture's plate, with the zoom and the torch's distance changed.

    The room, the wall material, the door def, the latitude and the hour are kept identical on
    purpose: it is the one arrangement in this repo whose doorway beam is known to be a real render
    rather than a doorway the composition owns outright. The torch moves, and only because
    vector_light_door_film_survey.json measured where it should stand -- see TORCH_DISTANCE."""
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
                # PlaceThings rejects the WHOLE step on a duplicate offset, so a duplicate here
                # builds no room at all and the film comes back as ninety frames of open ground.
                + [f"{x},-7" for x in range(-12, 0)]
                + [f"{x},7" for x in range(-12, 0)]
                + [f"0,{z}" for z in list(range(-7, 0)) + list(range(1, 8))])}),
        step("PlaceThings", **{"def": "Door", "stuff": "WoodLog", "offset": DOOR,
                               "layout": "cells", "clear": "true", "cells": "0,0"}),
        step("PlaceThings", **{"def": "TorchLamp", "offset": DOOR, "layout": "cells",
                               "cells": f"-{TORCH_DISTANCE},0"}),
        step("SetTime", hour=HOUR),
        step("LookAt", offset=DOOR, zoom=11),
    ]


def flags():
    return [step("SetFeature", featureName=f, enabled="true") for f in FLAGS_ON]


def film():
    """The clip. One continuous frame numbering across all five phases, because the encoder reads
    doorfilm_%04d.png as a single sequence -- per-phase prefixes would need stitching in post and
    would put the phase boundaries where a dropped frame is invisible."""
    out = []
    n = 0

    def capture(count):
        nonlocal n
        for _ in range(count):
            n += 1
            out.append(step("Wait", frames=1))
            out.append(step("Screenshot", fileName=f"doorfilm_{n:04d}.png"))

    # Shut and settled before frame 1, so the lead-in is a real closed door rather than the tail of
    # whatever the fixture build left behind.
    out += [
        step("SetDoorOpen", offset=DOOR, open="false"),
        step("SetTimeSpeed", speed="normal"),
        step("Wait", frames=60),
        step("SetTimeSpeed", speed="paused"),
        probe("door_aperture", 0, 0,
              "Shut before the film starts. At tolerance 0 -- a door already part open makes the "
              "loop's first and last frames disagree, which is the one defect a looping clip "
              "cannot hide."),
        step("Screenshot", fileName="doorfilm_warmup_discard.png"),
        step("SetTimeSpeed", speed="normal"),
    ]

    capture(SHUT_LEAD)
    out.append(step("SetDoorOpen", offset=DOOR, open="true"))
    capture(OPENING)
    capture(OPEN_HOLD)
    out.append(step("SetDoorOpen", offset=DOOR, open="false"))
    capture(CLOSING)
    capture(SHUT_TAIL)

    out += [
        step("SetTimeSpeed", speed="paused"),
        probe("door_aperture", 0, 0,
              "And shut again by the last frame. Together with the pin above this is what makes the "
              "clip loop: frame 90 and frame 1 are the same state, so the wrap is a cut and not a "
              "jump."),
    ]
    return out


def reference_stills():
    """Three stills the clip itself cannot supply, taken after the film so nothing they change can
    reach it: the fully-open beam with the feature OFF (what a subscriber sees without the mod --
    RimWorld's glow grid never learns a door opened, so this is a flat dark yard), the same frame
    with it back on, and two alternate framings in case the crop wants re-cutting without a
    re-boot."""
    out = [
        step("SetTimeSpeed", speed="normal"),
        step("SetDoorOpen", offset=DOOR, open="true"),
        step("Wait", frames=60),
        step("SetTimeSpeed", speed="paused"),
        probe("door_aperture", 1, 0,
              "The reference stills are of a FULLY open door. Anything less and the off/on pair "
              "below is comparing two different apertures rather than two renderers."),
        step("Screenshot", fileName="doorfilm_ref_on_z11.png"),
        step("LookAt", offset=DOOR, zoom=8),
        step("Screenshot", fileName="doorfilm_ref_on_z8.png"),
        step("LookAt", offset=DOOR, zoom=14),
        step("Screenshot", fileName="doorfilm_ref_on_z14.png"),
        step("LookAt", offset=DOOR, zoom=11),
    ]

    # Vanilla's answer to the same open door. Only the open-door flags come off: leaving the rest of
    # vector lighting on keeps the lamp itself identical between the two, so the difference in the
    # pair is the doorway and nothing else.
    out += [
        step("SetFeature", featureName="vector_light_open_doors", enabled="false"),
        step("SetFeature", featureName="vector_light_door_glow_blocker", enabled="false"),
        step("SetFeature", featureName="vector_light_door_aperture", enabled="false"),
        step("Screenshot", fileName="doorfilm_ref_off_z11.png"),
    ]
    return out


def main():
    scenario = {
        "name": "vector_light_door_film",
        "saveFile": "minimal_colony.rws",
        "description":
            "Showcase clip for the Workshop listing: §27e's open-door beam over one full swing, "
            "shut -> open -> held -> closing -> shut, so the ninety frames loop back onto their own "
            "first frame. Every flag stated at its SHIPPED DEFAULT (glow blocker included, unlike "
            "the gate scenarios, which predate it), because a listing asset that inherits a flag is "
            "advertising a configuration rather than the mod. The clock runs at normal speed "
            "throughout -- AdvanceTicks is a jump and a door's Tick() never runs under it, so "
            "TickLapse would film a door that never moves and pass. Four reference stills follow "
            "the film: the open beam at three zooms, and the same frame with the open-door flags "
            "off, which is the flat dark yard vanilla delivers because its glow grid never learns a "
            "door opened. The torch stands ONE cell inside the door rather than the gate scenarios' "
            "three: vector_light_door_film_survey.json photographed all three distances in one frame "
            "and one cell is roughly double the yard lift. The hour is 22 for the same reason: the "
            "clip must run the clock, and 22 is the one candidate night hour whose ambient does not "
            "drift across the film's own 260 ticks, so the loop's wrap is a cut rather than a flash. "
            "Generated by "
            "Tools/ScenarioGen/gen_vector_light_door_film.py; see Tools/DoorCapture/README.md for "
            "the encode.",
        "steps": scene() + flags() + [
            step("Screenshot", fileName="doorfilm_hud_discard.png"),
        ] + film() + reference_stills(),
    }

    out = os.path.join(os.path.dirname(__file__), "..", "..",
                       "Tests", "Scenarios", "vector_light_door_film.json")
    with open(os.path.normpath(out), "w") as f:
        json.dump(scenario, f, indent=2)
        f.write("\n")
    print(f"{os.path.normpath(out)}: {len(scenario['steps'])} steps, "
          f"{SHUT_LEAD + OPENING + OPEN_HOLD + CLOSING + SHUT_TAIL} film frames")


if __name__ == "__main__":
    main()
