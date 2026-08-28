#!/usr/bin/env python3
"""Generate Tests/Scenarios/vector_light_indoor_multiply.json.

THE SCENE IS vector_light_surface_lift's, DELIBERATELY AND UNCHANGED — one torch four cells from
each of two doors, the east one opening into a second roofed room and the west one opening onto open
sky, gravel laid everywhere so the ground has its own speckle to keep or lose. It is reused rather
than rewritten because the indoor multiply layer is the surface lift drawn ON TOP of the additive
beam instead of instead of it, and the only honest way to say what layering buys is to photograph it
in the frame the un-layered version was measured in.

It is also the one scene that can ask both halves of the roof gate at once. The emitter is roofed, so
the layer applies to its whole fan — including the part of that fan that reaches through the west
door onto open ground. Whether the outdoor beam moves is therefore a QUESTION this scenario answers
rather than an assumption it makes, and the west-door column is in the report for that reason.

Every cloud flag is off and stated: half this scene is open ground directly under a measured beam,
and the cloud sheet drifts on the tick counter, so leaving them on would put run-to-run noise on
exactly the pixels being compared.
"""

import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
BASE = os.path.join(ROOT, "Tests", "Scenarios", "vector_light_surface_lift.json")
OUT = os.path.join(ROOT, "Tests", "Scenarios", "vector_light_indoor_multiply.json")

# The scene: everything up to and including the two doors being opened. Taken from the base scenario
# verbatim so the two files cannot drift into photographing different rooms.
SCENE_STEPS = 12

# The flag block every arm states in full. Copied from the base scenario's own, which is where the
# rest of §27's settings for this scene were decided; only the two lift flags vary per arm, and they
# are appended after this so an arm reads as "the shared bed, then my two answers".
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
    ("cloud_cover", False),
    ("cloud_sheet", False),
    ("cloud_presence", False),
    ("cloud_deck_varieties", False),
    ("cloud_volume", False),
]


def feature(name, enabled):
    return {"type": "SetFeature", "args": {"featureName": name, "enabled": "true" if enabled else "false"}}


def arm(vector_lights, surface_lift, indoor_multiply):
    """One arm's flags, stated in full.

    STATED IN FULL RATHER THAN INHERITED, because a flag left unstated is whatever the arm before it
    happened to leave behind — and a committed capture in this repo has already read 17.08 instead of
    20.23 that way while its scenario still passed green.
    """
    steps = [feature("vector_lights", vector_lights)]
    steps += [feature(name, enabled) for name, enabled in SHARED]
    steps.append(feature("vector_light_surface_lift", surface_lift))
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


def capture(name, vector_lights, surface_lift, indoor_multiply, pin_shader=True):
    steps = arm(vector_lights, surface_lift, indoor_multiply)
    if pin_shader:
        steps += shader_pins()
    steps.append({"type": "Screenshot", "args": {"fileName": name}})
    return steps


def main():
    with open(BASE) as handle:
        base = json.load(handle)

    steps = list(base["steps"][:SCENE_STEPS])

    # DISCARDED, and it has to exist. hideUi is honoured from the SECOND capture onward, so the first
    # frame of any scenario carries the HUD — whose message log differs run to run — and an A/B whose
    # A is that frame is partly a measurement of UI pixels.
    steps += capture("indoormultiply_warmup_discard.png", True, False, False)

    # Vanilla, for the level everything else is read against.
    steps += capture("indoormultiply_vanilla.png", False, False, False, pin_shader=False)

    # THE BASELINE: §27 exactly as it ships today, the additive beam alone.
    steps += capture("indoormultiply_shipped.png", True, False, False)

    # THE FEATURE: the same beam, with the surface lift drawn over it under the roof.
    steps += capture("indoormultiply_layered.png", True, False, True)

    # The surface lift ALONE, which is the other delivery of the same excess — the layer's own two
    # halves are in the frame separately so "layering buys something" is a comparison rather than a
    # claim. It also stands as the check that the two flags are exclusive: with the lift on, the
    # layer flag is refused (VectorLightShader.IndoorMultiplyActive), so this arm and a both-on arm
    # would be the same picture.
    steps += capture("indoormultiply_lift.png", True, True, False)

    # THE SAME-BUILD CONTROL, repeating `shipped` after the arms being judged. Two runs of ONE build
    # already differ on a share of the frame; without this the floor reads as the feature.
    steps += capture("indoormultiply_shipped_control.png", True, False, False)

    # Noon, where the roofed floor is already brighter and the layer must therefore be SMALLER, and
    # where the outdoor half of the fan is under a daylight scale rather than over black. The pin is
    # the base scenario's own measured value at this tile and hour — RimWorld's clock does not put
    # noon where a calculator would.
    steps.append({"type": "SetTime", "args": {"hour": "12"}})
    steps.append(probe("sun_elevation", "56.72", "0.5"))
    steps += capture("indoormultiply_shipped_noon.png", True, False, False)
    steps += capture("indoormultiply_layered_noon.png", True, False, True)

    scenario = {
        "name": "vector_light_indoor_multiply",
        "saveFile": base["saveFile"],
        "description": (
            "The indoor multiply layer: the surface lift drawn ON TOP of the additive beam under a "
            "roof instead of in place of it. Every other composition in §27 delivers "
            "max(0, ours - vanilla) exactly once and is self-limiting by construction; this one "
            "delivers it twice, once as light added beside the surface and once as a scaling of the "
            "surface itself, which is why it reads richer indoors and why it ships off. The scene is "
            "vector_light_surface_lift's own, unchanged — one torch four cells from each of two "
            "doors, the east into a second roofed room and the west onto open sky, gravel everywhere "
            "— because the only honest way to say what LAYERING buys is to photograph it where "
            "the un-layered version was measured. The emitter is roofed, so the layer applies to its "
            "whole fan including the part that reaches outdoors through the west door; whether that "
            "outdoor beam moves is a question this scenario answers rather than one it assumes, and "
            "the west column is reported for that reason. Arms at midnight: vanilla, §27 as "
            "shipped, the layer, the surface lift alone, and §27 as shipped again as a "
            "same-build control for the run-to-run pixel floor; then the shipped/layered pair "
            "repeated at noon, where the roofed floor is already brighter and the layer must "
            "therefore be smaller. Every cloud flag is off and stated: half this scene is open "
            "ground directly under a measured beam."
        ),
        "steps": steps,
    }

    with open(OUT, "w") as handle:
        json.dump(scenario, handle, indent=2)
        handle.write("\n")

    print("wrote %s (%d steps)" % (OUT, len(steps)))


if __name__ == "__main__":
    main()
