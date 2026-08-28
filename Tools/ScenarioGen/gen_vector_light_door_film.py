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

  - IT IS NOT FILMED IN REAL TIME. It cannot be: a Screenshot step costs about fifty game ticks
    while a 1920x1080 PNG is encoded and written, so a door's whole slide lands inside two
    consecutive captures and no playback rate can recover the positions that were never
    photographed. Each frame here is its own pass -- shut the door, run the clock forward exactly N
    ticks with cheap Wait frames, FREEZE, then shoot -- so the sweep can be as fine as it likes. See
    sample(). TickLapse is not the alternative either: AdvanceTicks is a jump and a door's own
    Tick() never runs under it, so it would produce a whole clip of a door that never moves, and
    pass.

  - The DOOR IS STONE, which is about the slide being long enough to sample rather than about looks
    (100 ticks against wood's 45). See DOOR_STUFF.

  - Zoom 11 rather than the gate scenarios' 14. Nothing about the effect changes; the crop the GIF
    is cut from gets 49 px per cell instead of 39, and GIF is the one format where the difference
    between resampling and not is visible in the flat areas.

The door_aperture probes are the only assertions here, and they exist because this film's predecessor
came back with the aperture pinned at 0 for all thirty frames while still reporting green -- a clip
of a door that never opened looks exactly like a clip of an effect that does nothing. They earn their
place twice over: the stone door's longer slide outran a settle that had been sized for a wooden one,
and the probe caught it at 0.625 rather than the reference still quietly photographing a door that
was five-eighths open.

AUTOSAVE HAS TO BE TURNED OFF BEFORE THE RUN, in Prefs.xml. Autosaver.ticksSinceSave is scribed into
the fixture save, so the autosave fires at the SAME point every run rather than randomly, and it
draws an "Autosaving..." box over the middle of the frame that screenshot mode does not hide. It cost
one capture here -- frame 11, in the middle of the slide, where it cannot be dropped without a jump.
See Tools/DoorCapture/README.md.

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

# THE DOOR IS STONE, AND THAT IS ABOUT HOW LONG THE SLIDE IS RATHER THAN ABOUT LOOKS.
#
# Building_Door.TicksToOpenNow is 45 / DoorOpenSpeed. Stone's DoorOpenSpeed stuff factor is 0.45, so
# a granite door takes 45 / 0.45 = 100 ticks against wood's 45 -- twice as many phases for the sweep
# below to sample, at the same tick spacing. It is also the slowest a plain vanilla door gets: the
# stat floors at 0.2, no Core stuff goes below stone, and unpoweredDoorOpenSpeedFactor defaults to 1
# on a door with no power comp. It matches the granite walls, which is a coincidence and a welcome
# one.
DOOR_STUFF = "BlocksGranite"

# The slide's length in ticks, and how finely the sweep samples it. See sample() for why the clip is
# built one frozen pass per frame rather than filmed in real time.
#
# SLIDE_TICKS is Building_Door.TicksToOpenNow for this door: 45 / 0.45 = 100. It is written here as a
# number rather than probed because the sweep has to be laid out before the run; the door_aperture
# pins at the end of each sweep are what catch it being wrong, and a stone door that stopped taking
# 100 ticks would fail them rather than quietly producing a sweep that stops half way.
SLIDE_TICKS = 100

# Four ticks per frame: 26 frames across the slide, which is enough that the leaves read as sliding
# rather than stepping. Finer costs run time and buys nothing -- the LIGHT through the aperture is
# quantised into eight steps by DoorApertureMath regardless, so past about eight frames the extra
# resolution is showing the leaves move, not the beam grow.
PHASE_STEP = 4

# Three frames past the end of the slide, held. The door has to visibly ARRIVE before the clip holds,
# or the hold reads as the animation stalling.
OVERRUN_FRAMES = 3

# How long to wait out a swing that is NOT being filmed. Sized in ticks, not in captures, and the two
# are nowhere near each other: a bare Wait frame costs about ONE tick because nothing is being
# written to disk, while a capture frame costs about eight because a 1920x1080 PNG is. So the 60 that
# waits out a wooden door comfortably is not enough for a stone one -- the first stone cut failed its
# reference-stills probe at door_aperture 0.625, five of the quantiser's eight steps, with the film
# itself perfectly fine because twenty captures is 160 ticks. 160 gives the 100-tick slide half as
# much again.
SETTLE_FRAMES = 160


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
        step("PlaceThings", **{"def": "Door", "stuff": DOOR_STUFF, "offset": DOOR,
                               "layout": "cells", "clear": "true", "cells": "0,0"}),
        step("PlaceThings", **{"def": "TorchLamp", "offset": DOOR, "layout": "cells",
                               "cells": f"-{TORCH_DISTANCE},0"}),
        step("SetTime", hour=HOUR),
        step("LookAt", offset=DOOR, zoom=11),
    ]


def flags():
    return [step("SetFeature", featureName=f, enabled="true") for f in FLAGS_ON]


def sample(n, opening, phase_ticks):
    """One frame of the slide, captured STROBOSCOPICALLY: the door is returned to a known state, run
    forward exactly `phase_ticks`, then FROZEN before the shutter opens.

    This is the whole technique, and it exists because filming the swing in real time cannot work on
    this harness at any playback rate. A Screenshot step costs about FIFTY GAME TICKS -- the game
    keeps ticking at 60/s while a 1920x1080 PNG is encoded and written -- so a door slide lands
    entirely inside two consecutive captures. Measured on the granite door: the doorway reads L* 8.15
    shut, 14.55 on the next capture, 15.22 on the one after. Slowing that down in the encoder just
    holds two stills for longer; the intermediate positions were never photographed, and no frame
    rate can invent them.

    A bare Wait, though, costs about ONE tick, because nothing is written to disk. (Measured: a
    `Wait frames=60` left the 100-tick granite door at door_aperture 0.625, i.e. 62.5 ticks in 60
    frames.) So the clock can be positioned to roughly single-tick precision as long as no screenshot
    is taken while it runs -- which is exactly what this does. Pause first, then shoot, and the
    screenshot's fifty ticks are spent on a frozen scene where they cost nothing.

    The result is a real render of a real door at a real intermediate position, one per pass. Nothing
    is interpolated or reversed; the clip is 22 separate openings of the same door, each stopped a
    few ticks later than the last."""
    out = [
        # Back to a known state first. Every sample starts from the same place rather than continuing
        # the last one, because a slide resumed from wherever the previous screenshot left it would
        # accumulate that screenshot's fifty ticks into the phase and the sweep would not be even.
        step("SetTimeSpeed", speed="normal"),
        step("SetDoorOpen", offset=DOOR, open="false" if opening else "true"),
        step("Wait", frames=SETTLE_FRAMES),
        step("SetDoorOpen", offset=DOOR, open="true" if opening else "false"),
    ]
    # phase 0 is the untouched end state, so it must not run the clock at all.
    if phase_ticks > 0:
        out.append(step("Wait", frames=phase_ticks))
    out += [
        step("SetTimeSpeed", speed="paused"),
        # THE SKY IS RE-PINNED FOR EVERY FRAME. Each pass costs a couple of hundred ticks of settle,
        # so across the whole sweep the clock advances a few game HOURS -- far more than the drift
        # that broke the first cut's loop, and it would show as the clip steadily brightening. SetTime
        # jumps the calendar without ticking anything, so the door's own ticksSinceOpen is untouched
        # and every frame is photographed under an identical sky.
        step("SetTime", hour=HOUR),
        step("Wait", frames=2),
        step("Screenshot", fileName=f"doorfilm_{n:04d}.png"),
    ]
    return out


def film():
    """The clip: the opening slide sampled tick by tick, a held beat, the closing slide, a held beat.

    One continuous frame numbering across all four phases, because the encoder reads
    doorfilm_%04d.png as a single sequence."""
    out = []
    n = 0

    def sweep(opening, phases):
        nonlocal n
        for p in phases:
            n += 1
            out.extend(sample(n, opening, p))

    # The slide is ~100 ticks (45 / stone's DoorOpenSpeed of 0.45). Sampling every PHASE_STEP ticks
    # to a little past the end gives the sweep a couple of frames of stillness at the far end, which
    # the clip needs -- the door has to visibly ARRIVE before the hold, or the hold reads as the
    # animation stalling.
    phases = list(range(0, SLIDE_TICKS + PHASE_STEP * (OVERRUN_FRAMES + 1), PHASE_STEP))

    out += [
        step("SetTimeSpeed", speed="paused"),
        step("Screenshot", fileName="doorfilm_warmup_discard.png"),
    ]

    sweep(opening=True, phases=phases)
    out.append(probe("door_aperture", 1, 0,
                     "The opening sweep ended on a FULLY open door. If the last phases fell short "
                     "the clip would read as a door that jams part way, and every frame in the sweep "
                     "would still be a valid render of a real state -- so nothing else here could "
                     "tell."))
    sweep(opening=False, phases=phases)
    out.append(probe("door_aperture", 0, 0,
                     "And the closing sweep ended shut, which is what makes the loop wrap: the last "
                     "frame and the first are the same state."))
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
        step("Wait", frames=SETTLE_FRAMES),
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
          f"{2 * (SLIDE_TICKS // PHASE_STEP + OVERRUN_FRAMES + 1)} film frames")


if __name__ == "__main__":
    main()
