#!/usr/bin/env python3
"""Generates the `twilight_sweep_*` scenarios in ../../Tests/Scenarios — §26, issue #140.

Generated rather than hand-written for the reason the parent CLAUDE.md gives for surveying at all:
§26's whole window is one pass of the sun from the horizon to §8's fade floor, and the only honest
way to find it is to walk the clock at a fine step with `sun_elevation` read at every stop. That is
one SetTime plus N Probe steps per sample, and at 0.05 h resolution over an hour it is several
hundred lines of JSON whose only interesting property is the step size.

Three scenarios come out of this file:

  twilight_sweep_survey   NO PINS. Walks the clock across dusk reading sun_elevation next to the
                          two §26 probes, so the report's ActualValue fields say where the window
                          actually is on this tile. Every expectation is a dummy with a huge
                          tolerance, so the scenario passes regardless — it is a measuring
                          instrument, not a gate.

  twilight_sweep          The A/B. Off then on at the same three hours, with captures, so the ΔE
                          is measured against a real baseline rather than against an empty map.

  twilight_sweep_lapse    The film. §26's entire claim is about MOTION, which no still can show and
                          which a video makes very easy to believe on no evidence — an evening
                          getting darker looks like a sweep whether or not anything swept. The
                          probes in the other two scenarios are what make this falsifiable.

HOURS ARE PLACEHOLDERS UNTIL THE SURVEY RUNS. The three below are seeded from §23b's own measured
dusk on this fixture (its window opens at 20.62 at latitude 45, day 40) plus the arithmetic that
§26's window is three times as wide, because it runs to -6 degrees rather than to a 4 km deck's
2.03-degree shadow entry. They are a starting point for the survey, NOT a measurement, and the
survey's output replaces them here before anything is pinned. CLAUDE.md's rule stands: never
replace a measured pin with a computed one, and these are computed.
"""

import json
import os

OUT_DIR = os.path.join(os.path.dirname(__file__), "..", "..", "Tests", "Scenarios")

# Latitude 45, day 40 — the tile and season §23's and §23b's scenarios already established civil
# dusk on. Reusing them means §26's window can be compared against theirs without a second unknown.
LATITUDE = "45"
DAY_OF_YEAR = "40"

# Read at every survey stop. sun_elevation sits FIRST and is repeated beside the effect probes at
# every pinned hour in the A/B too, per CLAUDE.md: a later clock change then fails loudly instead of
# silently emptying the frames.
SURVEY_PROBES = [
    "sun_elevation",
    "twilight_sweep_position",
    "twilight_sweep_amplitude",
]

# 0.05 h is 3 game minutes. §26's window is wide compared with §23b's ~10-minute one, but the
# quantity being located is where it OPENS and CLOSES, and a step coarser than this puts the two
# endpoints inside a single sample.
#
# THE SURVEY RANGE COST THREE RUNS TO SETTLE, AND THE REASON IS WORTH MORE THAN THE NUMBER.
#
# Seeding it from §23b's own scenario — which states its window opens at 20.62 on this same tile and
# season — appeared to be wrong by 2.3 hours: the first survey came back with the sun between -16.6
# and -25.6 degrees across every sample. A second survey, re-aimed at 18.20-19.30, put sunset at
# 18.80. A third run of that same file, byte for byte, put it at 20.65.
#
# Three runs, three answers, one scenario. What actually moved was PERSISTED MOD SETTINGS, which
# run_test.sh does not reset — the parent CLAUDE.md's own warning, met in the wild. The second survey
# was the first run to write `realistic_preset false`, and the write did not fully take until the
# following boot, so runs 1 and 2 were reading a fixture mid-change. §23b's 20.62 was right the whole
# time and the "2.3 hours late" reading was the artifact.
#
# THE LESSON FOR ANY LATER SURVEY HERE: a sun_elevation reading is only trustworthy on a boot whose
# persisted settings were already what the scenario asks for. Run the survey TWICE and believe the
# second, or a whole afternoon of pins gets built on a transient.
#
# Settled values: sunset (elevation 0) at 20.654, §8's -6 floor at 21.09.
SURVEY_START = 20.50
SURVEY_END = 21.60
SURVEY_STEP = 0.05

# MEASURED on the settled boot (third run). Sunset lands at 20.654 and §8's -6 floor at 21.09:
#
#     hour   elevation   position   amplitude
#     20.65     +0.035     0.0000      0.0000     <- last frame with the sun up
#     20.70     -0.364     0.0607      0.0296
#     20.80     -1.490     0.2483      0.0971
#     20.90     -3.073     0.5122      0.1299     <- envelope peak
#     21.00     -4.639     0.7731      0.0912
#     21.05     -5.415     0.9025      0.0458
#     21.10     -6.186     0.0000      0.0000     <- past the floor, stood down
#
# The whole window is 0.435 game hours — 26 game minutes, about 18 seconds of real time at 1x speed.
# That is the sweep's actual on-screen duration and the number the design rests on. Note the sun does
# NOT fall at a constant rate through it (8 deg/h at the horizon, ~15.7 deg/h by the floor), which is
# why these hours are read off the table rather than interpolated between its ends.
LAPSE_START = 20.62
LAPSE_END = 21.12

# A quarter, half and three-quarters across, read off the table above rather than computed. The
# middle one is the envelope's peak. The ends of the window are deliberately NOT sampled: the
# envelope is zero at both by construction, so a capture there shows nothing while the position
# probe still reads a healthy non-zero — which is exactly the trap the amplitude probe exists for.
AB_HOURS = ["20.80", "20.90", "21.00"]


def probe(name, expected="0", tolerance="999"):
    return {
        "type": "Probe",
        "args": {
            "probeName": name,
            "expectedValue": expected,
            "tolerance": tolerance,
        },
    }


def set_time(hour):
    return {"type": "SetTime", "args": {"hour": f"{hour}"}}


def set_feature(name, enabled):
    return {
        "type": "SetFeature",
        "args": {"featureName": name, "enabled": "true" if enabled else "false"},
    }


def preamble():
    """Tile, season, preset, canvas and weather, in the order the harness wants them.

    Clear rather than a cloudy weather, deliberately: §26 reads no cloud fraction at all, and the
    cheapest way to prove that claim is to film it on a sky with no deck in it. It also keeps the
    three cloud lanes — which default off but share this window — from being a confound if somebody
    later runs this scenario in a suite where one of them was left on.

    The preset is reset explicitly because run_test.sh does NOT reset persisted mod settings, and a
    non-default preset rescales every dimming and shadow value in the frame. Cinematic (the shipped
    default) rather than Realistic, matching §23b's own scenarios so the two are comparable.

    The four SetTerrain patches flatten the map to plain soil. §26 is a whole-map wash, so a mixed
    terrain would put most of the frame's per-pixel variance in the ground texture rather than in
    the effect, and a median ΔE over that reads low for reasons that have nothing to do with the
    subsystem. The harness's per-call cell cap is 128x128 against a 250x250 fixture, hence four.
    """
    return [
        {"type": "SetTile", "args": {"latitude": LATITUDE}},
        {"type": "SetSeason", "args": {"dayOfYear": DAY_OF_YEAR}},
        {"type": "SetFeature", "args": {"featureName": "realistic_preset", "enabled": "false"}},
        {"type": "SetTerrain", "args": {"def": "Soil", "width": "128", "height": "128", "anchor": "64,64"}},
        {"type": "SetTerrain", "args": {"def": "Soil", "width": "128", "height": "128", "anchor": "64,186"}},
        {"type": "SetTerrain", "args": {"def": "Soil", "width": "128", "height": "128", "anchor": "186,64"}},
        {"type": "SetTerrain", "args": {"def": "Soil", "width": "128", "height": "128", "anchor": "186,186"}},
        {"type": "SetWeather", "args": {"weatherDef": "Clear", "instant": "true"}},
    ]


def survey():
    steps = preamble()
    steps.append(set_feature("twilight_sweep", True))

    hour = SURVEY_START
    while hour <= SURVEY_END + 1e-9:
        steps.append(set_time(f"{hour:.2f}"))
        steps.extend(probe(name) for name in SURVEY_PROBES)
        hour += SURVEY_STEP

    return {
        "name": "twilight_sweep_survey",
        "saveFile": "minimal_colony.rws",
        "description": (
            "SURVEY ONLY, no pins: locates §26's sweep window on this tile before the A/B pins "
            "anything in it. Every Probe carries a dummy expectation and a huge tolerance, so the "
            "scenario passes regardless and the numbers are read out of the report's ActualValue "
            "fields. §26's window runs from the horizon to §8's -6° fade floor, which "
            "is roughly three times §23b's, but RimWorld's clock does not put sunset at 18:00 and "
            "the window's ENDS are what the A/B needs — the envelope is zero at both of them by "
            "construction, so an hour picked just inside one reads as a healthy non-zero position "
            "with nothing at all on screen. Latitude 45, day 40, Clear: §26 reads no cloud "
            "fraction, and a cloudless sky keeps the three cloud lanes from confounding it."
        ),
        "steps": steps,
    }


def ab():
    steps = preamble()

    # The first Screenshot of any run carries the HUD — hideUi is only honoured from the second
    # capture on — so this one is deliberately thrown away. Without it the first real frame silently
    # measures UI pixels too, and a median ΔE over pawn labels and menu chrome is not a measurement
    # of anything.
    steps.append(set_time(AB_HOURS[0]))
    steps.append(screenshot("ts_warmup"))

    # OFF FIRST, and the whole A half runs before the flag is ever set. "Off" has to reproduce
    # pre-§26 rendering exactly rather than merely look similar, which is what makes the ΔE a
    # measurement of the feature rather than of the mod being present.
    steps.append(set_feature("twilight_sweep", False))
    for i, hour in enumerate(AB_HOURS):
        steps.append(set_time(hour))
        steps.extend(probe(name) for name in SURVEY_PROBES)
        steps.append(screenshot(f"ts_{i}_off"))

    steps.append(set_feature("twilight_sweep", True))
    for i, hour in enumerate(AB_HOURS):
        steps.append(set_time(hour))
        steps.extend(probe(name) for name in SURVEY_PROBES)
        steps.append(screenshot(f"ts_{i}_on"))

    return {
        "name": "twilight_sweep",
        "saveFile": "minimal_colony.rws",
        "description": (
            "§26's A/B (issue #140): the same three hours of one dusk with the sweep off and on. "
            "Three hours rather than one because the claim is that the boundary MOVES — a single "
            "pair proves a gradient exists, not that it went anywhere — and sun_elevation is pinned "
            "beside both §26 probes at every one so a later clock change fails loudly instead of "
            "quietly emptying the frames. The first capture of any run carries the HUD (hideUi is "
            "honoured from the second on), so ts_0_off is deliberately the throwaway."
        ),
        "steps": steps,
    }


def screenshot(name):
    return {"type": "Screenshot", "args": {"fileName": f"{name}.png"}}


# THE FILM PLAYS AT REAL TIME, WHICH IS A REQUIREMENT RATHER THAN A PREFERENCE, and it is the one
# parameter here that cannot be eyeballed.
#
# §26's claim is that a boundary crossing the colony reads as dusk rather than as an artifact. That
# is a judgement about a SPEED, so a film compressed to a convenient length cannot answer it — a
# sweep sped up 5x looks like a wipe transition and one slowed down looks like a stain, and neither
# is what the player would see. The only honest film is one where a second of video is a second of
# play.
#
# RimWorld runs 2500 ticks per in-game hour at 60 ticks/second, so one game hour is 41.667 seconds of
# real time at 1x speed. A film is real-time when its frame interval in game hours, times that, is
# the frame interval in seconds:
#
#     stepHours * SECONDS_PER_GAME_HOUR == 1 / fps
#
# 0.002 h at 12 fps satisfies it exactly (0.002 * 41.667 = 0.08333 s = 1/12). 12 fps is the floor for
# motion reading as motion rather than as a slideshow; going smoother means proportionally more
# frames, and at 350 frames per film times two films this is already the expensive scenario here.
SECONDS_PER_GAME_HOUR = 2500.0 / 60.0

LAPSE_FPS = 12
LAPSE_STEP_HOURS = 1.0 / (LAPSE_FPS * SECONDS_PER_GAME_HOUR)
LAPSE_STEPS = round((LAPSE_END - LAPSE_START) / LAPSE_STEP_HOURS)


def timelapse(prefix, enabled):
    return [
        set_feature("twilight_sweep", enabled),
        {
            "type": "Timelapse",
            "args": {
                "fromHour": f"{LAPSE_START:.2f}",
                "stepHours": f"{LAPSE_STEP_HOURS:.5f}",
                "steps": f"{LAPSE_STEPS}",
                "fileNamePrefix": prefix,
                "fps": f"{LAPSE_FPS}",
                "settleFrames": "2",
            },
        },
    ]


def lapse():
    steps = preamble()

    # Throwaway first capture, same HUD reason as in the A/B.
    steps.append(set_time(f"{LAPSE_START:.2f}"))
    steps.append(screenshot("ts_lapse_warmup"))

    # BOTH halves filmed, not just the on one. A film of the sweep alone shows an evening getting
    # darker with a band in it, and the honest question — does the band read as dusk or as an
    # artifact — needs the same evening without it to compare against. §26's off state is
    # bit-identical to pre-feature rendering, so the off film doubles as the baseline.
    steps.extend(timelapse("ts_sweep_on", True))
    steps.extend(timelapse("ts_sweep_off", False))

    return {
        "name": "twilight_sweep_lapse",
        "saveFile": "minimal_colony.rws",
        "description": (
            "§26 filmed across one dusk, on and off (issue #140). §26's entire claim is about "
            "MOTION, which no still frame can carry — and which a video makes very easy to believe "
            "on no evidence, since an evening simply getting darker looks like a sweep whether or "
            "not anything swept. The twilight_sweep scenario's pinned probes are what make this "
            "falsifiable; this is what makes it watchable. Both halves are filmed because the "
            "question is comparative: a band that reads as a rendering artifact and one that reads "
            "as dusk look identical in isolation. Paced to REAL TIME: 0.002 game hours per frame at "
            "12 fps is exactly 1x play speed, so a second of video is a second of play — a film "
            "compressed to a convenient length cannot answer a question about a speed."
        ),
        "steps": steps,
    }


def write(scenario):
    path = os.path.join(OUT_DIR, f"{scenario['name']}.json")
    with open(path, "w") as handle:
        json.dump(scenario, handle, indent=2)
        handle.write("\n")

    print(f"wrote {path} ({len(scenario['steps'])} steps)")


if __name__ == "__main__":
    write(survey())
    write(ab())
    write(lapse())
