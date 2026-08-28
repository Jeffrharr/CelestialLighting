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

  - IT IS SHOT IN SLOW MOTION AND PLAYED BACK AT SPEED. A Screenshot's frame is long -- encoding a
    1920x1080 PNG takes the better part of a second -- and TickManager ticks the game through
    Time.deltaTime, so at normal speed a captured frame swallows twenty to fifty game ticks and a
    door's whole slide lands between two consecutive captures. SetTimeScale drops Time.deltaTime in
    proportion, so at 0.05 a rendered frame advances at most one tick however long it takes to
    write, and the ordinary capture loop becomes a slow-motion camera over a CONTINUOUS take. See
    film() and TIME_SCALE. TickLapse is not the alternative: AdvanceTicks is a jump and a door's own
    Tick() never runs under it, so it would produce a whole clip of a door that never moves, and
    pass.

  - The DOOR IS STONE, which is about the slide being long enough to film rather than about looks
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

# HOW FAR THE GAME IS SLOWED WHILE FILMING, and this one number is what makes the clip possible.
#
# A Screenshot's frame is long -- encoding a 1920x1080 PNG takes the better part of a second -- and
# TickManager ticks the game through Time.deltaTime, so at normal speed a captured frame swallows
# twenty to fifty game ticks and a 100-tick door slide falls between two of them. Unity computes
# Time.deltaTime as min(unscaledDeltaTime, maximumDeltaTime) * timeScale, so at 0.05 a rendered frame
# advances at most ONE tick however long it takes to write. The capture loop becomes a slow-motion
# camera and the take is continuous.
#
# 0.05 rather than something smaller because there is nothing to gain below one tick per frame: the
# frames cannot be closer together than the game's own resolution, and every step of the scenario --
# including the settles -- gets twenty times longer in wall clock.
TIME_SCALE = 0.05

# Phase lengths in CAPTURED FRAMES, and the conversion to ticks is MEASURED rather than assumed.
# At TIME_SCALE 0.05 a captured frame advances about 0.52 ticks: the aperture quantiser's eight steps
# land 24 frames apart in the doorway trace, and 100 ticks / 8 steps = 12.5 ticks per step. So the
# ~100-tick slide wants roughly 190 frames, and OPENING is set well past that.
#
# The first cut of this take used 120 and the door reached seven eighths and no further before the
# close began -- the doorway plateaued at L* 14.87 where fully open is 15.22. Nothing failed. That is
# what the door_aperture pin at the top of the hold is now for; see film().
#
# Frames are cheap here (a captured frame is ~0.17 s of wall clock) and the encoder decimates to
# taste afterwards, so the bias is deliberately towards too many: a frame not shot cannot be
# recovered, and a frame not used costs nothing.
SHUT_LEAD = 15
OPENING = 230
OPEN_HOLD = 30
CLOSING = 230
SHUT_TAIL = 15

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
        # THE ROOF, and it is load-bearing rather than set dressing. PlaceThings never roofs, so
        # without this the "room" is an OUTDOOR room -- and the vibrant arm below would be inert,
        # because vector_light_indoor_multiply gates per EMITTER on the roof grid asked at the lamp's
        # own cell. An unroofed fixture would have produced two identical clips and a confident claim
        # that the setting does nothing.
        #
        # The rect is exactly the room's footprint, walls included, and the arithmetic matters: the
        # harness builds CellRect.CenteredOn(anchor, w, h), which is minX = anchor.x - w/2 with
        # maxX = minX + w - 1. Anchored at x -6 with width 14 that is x -13..0; at z 45 with height
        # 15 it is z -7..+7 about the room's centre line. One cell wider in either direction would
        # roof open ground OUTSIDE the wall, i.e. put an eave over the very yard the beam is filmed
        # falling on.
        step("SetRoof", **{"def": "RoofConstructed", "offset": "-6,45",
                           "width": 14, "height": 15}),
        step("SetTime", hour=HOUR),
        step("LookAt", offset=DOOR, zoom=11),
    ]


def flags(vibrant):
    """The preset FIRST, then the effect flags.

    realistic_preset is not one of the mod's effects -- it rewrites the whole settings bundle,
    including minNightBrightness, which is 0.5 under Cinematic and 0 under Realistic. That is the
    difference between a yard the night floor keeps readable and a yard that is genuinely black, and
    it changes this clip more than anything else in the file: the beam looks far more dramatic
    against black, which is exactly why it has to be pinned rather than left to whatever the box was
    last set to.

    It is registered with defaultEnabled FALSE, so FeatureRegistry.ResetAll SHOULD leave Cinematic
    standing. It does not reliably: two runs of this scenario differing only in phase lengths came
    back one Cinematic (yard L* 8.15) and one Realistic (2.81), and the persisted settings XML is
    outside the harness's rollback ledger, so whatever a previous run left is what the next one
    starts from. snow_glare.json states it explicitly for the same reason. State it, do not inherit
    it."""
    out = [step("SetFeature", featureName="realistic_preset", enabled="false")]
    out += [step("SetFeature", featureName=f, enabled="true") for f in FLAGS_ON]

    # THE ONE FLAG THE TWO CLIPS DIFFER IN, stated in both arms rather than inherited. Its shipped
    # value is FALSE: it is a taste option rather than a correction, because the additive beam and
    # the surface lift are two DELIVERIES of the same quantity and running both lifts the lit region
    # twice. Off is the shipped frame byte for byte -- the whole feature is one extra
    # Graphics.DrawMesh issued after the existing one -- so the off arm is a real baseline and not a
    # picture of the feature being absent.
    out.append(step("SetFeature", featureName="vector_light_indoor_multiply",
                    enabled="true" if vibrant else "false"))

    # Its stand-down partner, pinned OFF in both arms. vector_light_indoor_multiply stands down while
    # vector_light_surface_lift is on -- there the primary pass already IS the multiply -- so an arm
    # that inherited surface lift true would report the surface lift's numbers under the vibrant
    # flag's name and the two clips would differ by nothing at all. It ships false; say so.
    out.append(step("SetFeature", featureName="vector_light_surface_lift", enabled="false"))
    return out


def film(prefix, vibrant):
    """The clip: ONE CONTINUOUS TAKE of the door opening, held, closing and held, shot in slow motion.

    The problem this solves is that a screenshot's frame is long -- encoding a 1920x1080 PNG takes
    the better part of a second -- and Verse.TickManager.TickManagerUpdate accumulates
    `Time.deltaTime` and ticks the game through it. So at normal speed a captured frame advances
    twenty to fifty game ticks, a door's slide is 45 (100 for stone), and the whole animation falls
    between two consecutive captures. Measured on the granite door, filmed the obvious way: the
    doorway reads L* 8.15 shut, 14.55 on the very next capture, 15.22 on the one after. No playback
    rate recovers positions that were never rendered.

    SetTimeScale fixes it at the source. Unity computes `Time.deltaTime` as
    `min(unscaledDeltaTime, maximumDeltaTime) * timeScale` and TickManager reads the scaled value, so
    at 0.05 a rendered frame advances at most one tick however long it takes to write. The same
    Wait/Screenshot loop then behaves as a slow-motion camera: the game is filmed continuously, the
    clip's frames are about a tick apart, and it can be played back at any rate including the true
    one.

    WHAT THIS REPLACED, and why. The previous cut sampled the animation instead: stop the door at a
    known phase, freeze, shoot, repeat. That produces evenly spaced frames of the DOOR and it is
    wrong about everything else, because a scene is not only the thing being filmed. Every other
    animation gets sampled at whatever phase its own pass happened to end on, so the torch flame
    strobed between unrelated frames rather than flickering. A continuous take cannot have that
    problem, because there is only one take.

    Called once per arm. The flag block is re-stated at the top of each rather than set once before
    both, so the two takes differ in exactly the flag named in the arm and nothing can drift between
    them -- the pair is the whole point of the second arm existing."""
    out = flags(vibrant)
    n = 0

    def capture(count):
        nonlocal n
        for _ in range(count):
            n += 1
            # Consecutive Screenshot steps, no settle Wait between them. The harness runs one step
            # per rendered frame, so a Wait/Screenshot pair would cost two frames and halve the
            # capture rate for nothing -- there is no state here that needs settling, only a clock
            # that needs to be caught in motion.
            out.append(step("Screenshot", fileName=f"{prefix}{n:04d}.png"))

    # Shut and settled before frame 1, at FULL speed -- this is dead time the clip never sees, and
    # running it in slow motion would multiply it by twenty for no benefit.
    out += [
        step("SetTimeSpeed", speed="normal"),
        step("SetDoorOpen", offset=DOOR, open="false"),
        step("Wait", frames=SETTLE_FRAMES),
        probe("door_aperture", 0, 0,
              "Shut before the film starts. At tolerance 0 -- a door already part open makes the "
              "loop's first and last frames disagree, which is the one defect a looping clip "
              "cannot hide."),
        step("Screenshot", fileName=f"{prefix}settled_discard.png"),
        step("SetTimeScale", scale=TIME_SCALE),
    ]

    capture(SHUT_LEAD)
    out.append(step("SetDoorOpen", offset=DOOR, open="true"))
    capture(OPENING)

    # THE SLIDE FINISHED INSIDE THE FRAMES THAT FILMED IT. This is the pin the first cut of the
    # continuous take was missing, and it cost a run: with OPENING too short the door reached seven
    # eighths, the close began, and every probe in the scenario still passed -- the end-of-film pin
    # asks whether the door is SHUT, which it duly was, and the reference stills open it again
    # afterwards from a clean settle. So nothing anywhere said that the clip's own door never
    # finished opening. It is placed before the hold rather than after it so a failure names the
    # right phase: OPENING is too short, not OPEN_HOLD.
    out.append(probe("door_aperture", 1, 0,
                     "Fully open before the hold begins. If OPENING is too short for the slide the "
                     "clip cuts to the close part way through the animation, which looks like a "
                     "door that jams and passes every other check in this file."))

    capture(OPEN_HOLD)
    out.append(step("SetDoorOpen", offset=DOOR, open="false"))
    capture(CLOSING)
    capture(SHUT_TAIL)

    out += [
        # BACK TO FULL SPEED, always, and this is not tidiness. Time.timeScale is process-global Unity
        # state: no save reload restores it and the harness's WorldStateReset has never heard of it,
        # so a scenario that leaves it at 0.05 hands the next one a game running twenty times slow.
        # See Source/Probes/SetTimeScaleStep.cs, which says the same thing from the other end.
        step("SetTimeScale", scale=1),
        probe("door_aperture", 0, 0,
              "And shut again by the last frame. Together with the pin above this is what makes the "
              "clip loop: the last frame and the first are the same state, so the wrap is a cut and "
              "not a jump."),
    ]
    return out


def reference_stills():
    """Three stills the clip itself cannot supply, taken after the film so nothing they change can
    reach it: the fully-open beam with the feature OFF (what a subscriber sees without the mod --
    RimWorld's glow grid never learns a door opened, so this is a flat dark yard), the same frame
    with it back on, and two alternate framings in case the crop wants re-cutting without a re-boot.

    RE-STATES THE FLAGS AT SHIPPED DEFAULT FIRST, and this is not belt-and-braces. These stills run
    after the VIBRANT arm, which leaves vector_light_indoor_multiply on; inheriting that would
    measure the off/on pair with a taste option enabled and quote the number as the shipped
    default's. The pair is what every dE in the README and the PR body is measured against, so it
    has to be the frame a default install produces."""
    out = flags(vibrant=False) + [
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
        # TWO ARMS, ONE BOOT, and the shipped-default one goes FIRST on purpose: it is the clip the
        # listing leads with, and a run that dies half way should have taken that one rather than
        # only the taste option. The vibrant arm re-shoots the same take with the one flag flipped,
        # so the pair is a like-for-like A/B rather than two clips of two scenes.
        #
        # The discard shot sits before either of them because the FIRST capture of a run still
        # carries the HUD -- screenshot mode is set in the same frame it shoots, and is honoured from
        # the second capture on.
        "steps": scene() + [
            step("Screenshot", fileName="doorfilm_hud_discard.png"),
        ] + film("doorfilm_", vibrant=False)
          + film("doorvibrant_", vibrant=True)
          + reference_stills(),
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
