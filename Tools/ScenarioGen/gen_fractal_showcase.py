#!/usr/bin/env python3
"""Generates the `fractal_*` showcase scenarios in ../../Tests/Scenarios.

Why a generator rather than hand-written JSON: the showcase scene is two fractals totalling
755 wall placements, and the harness's `PlaceThings` `layout: cells` arg wants them as one
literal "dx,dz; dx,dz; ..." string. Typing that by hand is not reviewable, and neither is
diffing it — but `sierpinski_carpet()` and `sierpinski_pyramid()` below are both four lines
and obviously correct. The generated JSON is committed (the harness reads JSON, not Python);
re-run this script after changing a shape or a shot list.

The four scenarios share one scene and differ only in sky state, because game conditions
leak: a scenario that starts an Eclipse contaminates whatever runs after it in the same boot
(see the harness README on suite isolation). So the clear-sky showcase, the eclipse, the
aurora and the solar flare are separate files meant to be run standalone.

Scope note: these exist for MARKETING captures — Workshop screenshots and the release video.
They deliberately carry no `Probe` gates, unlike every other scenario in Tests/Scenarios.
A probe pins a number; these pin a look, and the only reviewer is a human eye.
"""

import json
import os

# ----- the fractals ---------------------------------------------------------------------

# Level-3 Sierpinski carpet: 27x27, 8^3 = 512 filled cells — which is exactly the harness's
# MaxPlacements cap for one PlaceThings step, so this is the largest carpet that fits in a
# single step. A cell is a hole iff any base-3 digit pair is (1,1); recursion therefore comes
# out of digit inspection with no recursion.
def sierpinski_carpet(order=3):
    n = 3 ** order
    cells = []
    for x in range(n):
        for z in range(n):
            a, b, solid = x, z, True
            while a > 0 or b > 0:
                if a % 3 == 1 and b % 3 == 1:
                    solid = False
                a //= 3
                b //= 3
            if solid:
                # Centre on the anchor: 27 wide means offsets -13..13.
                cells.append((x - n // 2, z - n // 2))
    return cells


# Order-5 Sierpinski pyramid via Pascal's triangle mod 2 (rule 90): 32 rows, 3^5 = 243 cells,
# 63 wide. Chosen over the filled right-triangle form for one lighting reason: every cell in a
# row sits two apart from its neighbours, so no two walls ever touch. RimWorld auto-links
# adjacent walls into one smooth mass, which would merge the shape into a blob AND merge its
# shadow; isolated pillars each throw their own shadow, so at low sun the ground fills with a
# second copy of the fractal made of shadows. That is the shot.
def sierpinski_pyramid(rows=32):
    cells = []
    for r in range(rows):
        c = 1
        for k in range(r + 1):
            if c % 2 == 1:
                # Apex north, base south; vertically centred on the anchor.
                cells.append((2 * k - r, rows // 2 - 1 - r))
            c = c * (r - k) // (k + 1)
    return cells


def cells_arg(cells):
    return "; ".join(f"{dx},{dz}" for dx, dz in cells)


# Order-3 Vicsek fractal: 27x27, 5^3 = 125 cells, a recursive plus. Two of these flank the
# main pair purely as framing — the wide shot is 124 cells across and the carpet-plus-pyramid
# column only fills 63 of them, so without something in the flanks a third of the frame is
# bare sand. A third shape also keeps the wide shot from reading as "one fractal, twice".
def vicsek(order=3):
    n = 3 ** order
    plus = {(1, 1), (0, 1), (2, 1), (1, 0), (1, 2)}
    cells = []
    for x in range(n):
        for z in range(n):
            a, b, solid = x, z, True
            # Exactly `order` digit positions, INCLUDING leading zeros. Draining the digits with
            # `while a > 0 or b > 0` instead silently skips the leading (0,0) pairs, and since
            # (0,0) is not in the plus set that admits cells the fractal excludes — it produced
            # 156 walls instead of 5^3. The carpet's loop can drain, because its test is "is any
            # pair (1,1)" and a skipped (0,0) never satisfies it.
            for _ in range(order):
                if (a % 3, b % 3) not in plus:
                    solid = False
                a //= 3
                b //= 3
            if solid:
                cells.append((x - n // 2, z - n // 2))
    return cells


CARPET = sierpinski_carpet()
PYRAMID = sierpinski_pyramid()
VICSEK = vicsek()

# ----- scene geometry -------------------------------------------------------------------

# THE SITE IS FIXTURE-SPECIFIC. Everything below is anchor-relative, but the anchor itself is
# absolute, and it was solved for rather than chosen: minimal_colony.rws has five SteamGeysers
# (91,202 / 114,71 / 139,147 / 192,160 / 226,198) and ten pawns, and the first run of this
# scenario put the site at map centre and paid for both — a geyser is flagged not-destroyable,
# so `clear` could not remove it and it punched two pillars out of the pyramid, while two
# colonists stood in frame wearing their name labels (which screenshot mode does NOT hide).
# 155,198 is the site whose wide frame contains no geyser and no pawn while leaving the pad
# inside the map. Geysers are 2x2, not 1x1 — the second run solved against their origin cell
# alone and still caught two of them in the pad, which failed the run on a not-destroyable
# blocker even though nothing was visible in frame. Re-solve if the fixture is regenerated.
SITE = "155,198"

# Content column: carpet 27 tall, pyramid 32 tall, 8 cells of air between them — 66 total,
# which is what sets the wide zoom below.
CARPET_AT = "0,-19"       # carpet centre, south half of the frame
PYRAMID_AT = "0,17"       # pyramid centre, north half
# Flanks sit BESIDE the carpet, not beside the pyramid: the pyramid's base is 63 cells wide,
# so anything level with it would nearly touch, while the carpet band is 27 wide and has room
# either side. x=+-45 also keeps them out of the carpet close-up (which sees x +-30) and off
# the wide frame's edge (x +-62).
VICSEK_L_AT = "-45,-19"
VICSEK_R_AT = "45,-19"

# Sand pad, sized to overrun the wide frame on every side so no colony rubble, rock or map
# edge shows past it, while still threading between the (91,202) and (192,160) geysers —
# hence 124 wide (not wider) and pushed north. 124x83 = 10292 cells, inside the 16384 cap.
PAD_W, PAD_H = "124", "83"
PAD_AT = "0,5"    # pushed north so its south edge clears the (192,160) geyser

# Camera. RimWorld's rootSize is HALF the vertical view in cells, and screenshots come out
# 1920x1080, so visible width is rootSize * 3.56. Wide 34 -> 68x121 cells (content is 64 tall,
# 116 wide with the flanks); carpet 16 -> 32x57 (content 27); pyramid 21 -> 42x75 (content
# 63x32). Wide was 44 on the first run and that was too loose — it showed the pad's own edge.
ZOOM_WIDE, ZOOM_CARPET, ZOOM_PYRAMID = "34", "16", "21"

LATITUDE = "45"    # high enough that late-afternoon shadows run long, low enough to keep a real day
DAY = "37"         # the fixture's near-full moon — moon_shadow_visibility pins moon_illumination
                   # at 0.99 on this day, and a new moon would waste every night frame here


def step(kind, **args):
    return {"type": kind, "args": {k: str(v) for k, v in args.items()}}


def settle(frames=20):
    # Clock jumps don't settle glow grid and shadow direction in the same frame, and these
    # shots exist to be looked at — 20 frames is cheap next to a wrong-looking capture.
    return step("Wait", frames=frames)


def build_scene():
    """The steps that raise the site. Identical in all three scenarios."""
    return [
        step("SetTile", latitude=LATITUDE),
        step("SetSeason", dayOfYear=DAY),
        step("SetWeather", weatherDef="Clear", instant="true"),

        # Bulldoze first, then strip roof: clearing a mountain destroys the rock but leaves the
        # overhead roof behind, and a roofed pad would put the whole showcase indoors — the one
        # state that silently kills every sky-lighting effect we are trying to film.
        step("SetTerrain", **{"def": "Sand"}, anchor=SITE, offset=PAD_AT, width=PAD_W,
             height=PAD_H, clear="true"),
        step("SetRoof", **{"def": "None"}, anchor=SITE, offset=PAD_AT, width=PAD_W, height=PAD_H),

        # Dark slate plaza under the carpet: pale marble walls and warm torch pools both read
        # against it, where sand-on-sand would flatten the night shots.
        step("SetTerrain", **{"def": "TileSlate"}, anchor=SITE, width="31", height="31",
             offset=CARPET_AT),

        step("PlaceThings", **{"def": "Wall"}, stuff="BlocksMarble", anchor=SITE, offset=CARPET_AT,
             layout="cells", clear="true", cells=cells_arg(CARPET)),
        step("PlaceThings", **{"def": "Wall"}, stuff="BlocksSlate", anchor=SITE, offset=PYRAMID_AT,
             layout="cells", clear="true", cells=cells_arg(PYRAMID)),
        # Flanks in a third stuff, so the wide shot separates the three shapes by tone as well
        # as by form.
        step("PlaceThings", **{"def": "Wall"}, stuff="BlocksSandstone", anchor=SITE,
             offset=VICSEK_L_AT, layout="cells", clear="true", cells=cells_arg(VICSEK)),
        step("PlaceThings", **{"def": "Wall"}, stuff="BlocksSandstone", anchor=SITE,
             offset=VICSEK_R_AT, layout="cells", clear="true", cells=cells_arg(VICSEK)),

        # Light sources are a quincunx, not one per courtyard: torches carry a 10-cell glow
        # radius against a 27-cell carpet, so lighting all nine courtyards would leave the
        # structure evenly lit and there would be nothing for pitch-black nights to be dark
        # against. Five lights leave the four edge courtyards genuinely unlit.
        step("PlaceThings", **{"def": "Campfire"}, anchor=SITE, offset=CARPET_AT,
             layout="cells", cells="0,0"),
        step("PlaceThings", **{"def": "TorchLamp"}, anchor=SITE, offset=CARPET_AT,
             layout="cells", cells="-9,-9; 9,-9; -9,9; 9,9"),
        # Three around the pyramid: an apex beacon and two at the base corners, placed one cell
        # clear of the walls so they light the pillars instead of replacing them. The apex torch
        # sits at +16, not +17: the wide frame's top edge is +35 from the site and the apex wall
        # is already at +33.
        step("PlaceThings", **{"def": "TorchLamp"}, anchor=SITE, offset=PYRAMID_AT,
             layout="cells", cells="0,16; -33,-16; 33,-16"),
    ]


def ab(feature, shot, name):
    """Off/on pair for one feature at the current time and camera.

    The feature name is IN the filename, not just the shot name. The first run filmed two
    different A/Bs — pitch-black nights and moon shadows — at the same hour and camera, both
    named `night_wide`, and the second pair silently overwrote the first.

    Always leaves the flag ON: flags are plain statics with no per-scenario reset, so a
    scenario that ends with one off re-lights every later capture in the same boot.
    """
    return [
        step("SetFeature", featureName=feature, enabled="false"),
        settle(),
        step("Screenshot", fileName=f"{name}_{shot}_{feature}_off.png"),
        step("SetFeature", featureName=feature, enabled="true"),
        settle(),
        step("Screenshot", fileName=f"{name}_{shot}_{feature}_on.png"),
    ]


def look(offset, zoom):
    return step("LookAt", anchor=SITE, offset=offset, zoom=zoom)


def warmup(name):
    """A throwaway first capture, discarded — it exists to arm screenshot mode.

    The Screenshot step sets `screenshotMode.Active` and captures in the same frame, so the
    UI is still up in whatever it grabs first: the noon frame of the third run came back with
    the alert stack, the top toolbar and the learning helper drawn over it. Every later shot
    is clean because the mode stays active. The harness's own scenarios all open with a
    `*_warmup.png` for this reason.
    """
    return [settle(), step("Screenshot", fileName=f"{name}_warmup.png")]


# ----- scenario 1: the clear-sky showcase -------------------------------------------------

def showcase():
    s = build_scene()
    n = "fx"

    # Establishing stills, wide, through the day. Noon is the control the rest are read
    # against — it is the only hour where the mod is meant to be nearly invisible.
    s += [look("0,0", ZOOM_WIDE), step("SetTime", hour="12")] + warmup(n)
    s += [step("Screenshot", fileName=f"{n}_noon_wide.png")]

    # Dawn: the mod's colour-temperature ramp is at its widest here.
    s += [step("SetTime", hour="6.5")] + ab("sky_color_temperature", "dawn", n)

    # Golden hour on the carpet — long soft-edged shadows through the fractal's holes.
    s += [look(CARPET_AT, ZOOM_CARPET), step("SetTime", hour="17.75")]
    s += ab("penumbra", "goldenhour_carpet", n)

    # Same hour on the pyramid: 243 free-standing pillars each casting a shadow, which is the
    # single densest shadow field the mod can be photographed against. No A/B here — nothing
    # in this scene is roofed, so the roof-driven effects have nothing to say about it, and
    # the penumbra pair above already carries the daytime claim.
    s += [look(PYRAMID_AT, ZOOM_PYRAMID), settle(), step("Screenshot", fileName=f"{n}_goldenhour_pyramid.png")]

    # Dusk: the vanilla sky snaps; ours holds a twilight band after the sun is down.
    s += [look("0,0", ZOOM_WIDE), step("SetTime", hour="20")]
    s += ab("civil_twilight", "dusk", n)

    # Night, full moon. Three separate claims, three A/Bs, all on the same frame:
    # how dark unlit ground gets, whether the moon casts its own shadows, and whether
    # colour drains out of the scene the way night vision actually drains it.
    s += [step("SetTime", hour="1")]
    s += ab("pitch_black_nights", "night_wide", n)
    s += ab("moon_shadows", "night_wide", n)
    s += [look(CARPET_AT, ZOOM_CARPET)]
    s += ab("low_light_desaturation", "night_carpet", n)

    # The video. Two sweeps: a full day for the whole arc, then a slow dusk-to-midnight run
    # over the torch-lit carpet, where the interesting half of the mod lives.
    # A full day, starting at NOON, as one seamless loop. It used to run 04:00->23:30, and as a
    # looping gif that was unwatchable: the loop's seam sat at the moment shadows are longest and
    # most directional, so the wrap snapped from evening shadows leaning one way to morning
    # shadows leaning the other and read as the sun teleporting mid-video. Starting at noon puts
    # the seam where shadows are shortest and the wrap all but disappears. `steps` rather than
    # `toHour` because 96 frames IS the deliverable — a gif's size is frames.
    s += [look("0,0", ZOOM_WIDE),
          step("Timelapse", fromHour="12", stepHours="0.25", steps="96",
               fileNamePrefix="fx_day_wide", settleFrames="4", fps="12")]
    # Runs THROUGH midnight to 02:00. The first cut of this stopped at 23:45 and the reviewer's
    # note was that it ends just as the interesting part starts — a full moon rises around sunset
    # and is not high until after midnight, so the moonlight the mod exists to render was always
    # off the end of the video. The harness rejected wrapping ranges when this was written; it
    # doesn't any more (Shared/TimelapseExpander.cs), so this is one continuous sweep rather than
    # two videos with a cut at the exact moment worth watching.
    s += [look(CARPET_AT, ZOOM_CARPET),
          step("Timelapse", fromHour="12", stepHours="0.25", steps="96",
               fileNamePrefix="fx_day_carpet", settleFrames="4", fps="12")]

    return {"name": "fractal_showcase", "saveFile": "minimal_colony.rws", "steps": s}


# ----- scenario 2: eclipse ----------------------------------------------------------------

def eclipse():
    s = build_scene()
    n = "fxe"
    # Midday, so the eclipse has the brightest possible sky to take away, and the torches
    # that were decorative at noon become the only light on the plaza.
    s += [step("SetTime", hour="12"), look("0,0", ZOOM_WIDE)] + warmup(n)
    s += [step("Screenshot", fileName=f"{n}_before.png"),
          # agedHours 2 skips the fade-in, so the first frame after this is already totality.
          step("StartCondition", conditionDef="Eclipse", durationHours="24", agedHours="2")]
    s += ab("eclipse_darkening", "totality_wide", n)
    s += [look(CARPET_AT, ZOOM_CARPET)]
    s += ab("eclipse_darkening", "totality_carpet", n)
    return {"name": "fractal_eclipse", "saveFile": "minimal_colony.rws", "steps": s}


# ----- scenario 3: aurora -----------------------------------------------------------------

def aurora():
    s = build_scene()
    n = "fxa"
    s += [step("SetTime", hour="1"), look("0,0", ZOOM_WIDE)] + warmup(n)
    s += [step("StartCondition", conditionDef="SolarFlare", durationHours="24", agedHours="2")]
    # Shipped default first (pitch-black nights on): the honest capture of what a player sees.
    s += ab("aurora", "night_wide", n)
    # Then again with the darkness floor lifted, where the tint has more sky to sit on. Left
    # OFF is not an option — see ab() — so pitch-black nights is restored before the last pair.
    s += [step("SetFeature", featureName="pitch_black_nights", enabled="false")]
    s += ab("aurora", "night_lifted", n)
    s += [step("SetFeature", featureName="pitch_black_nights", enabled="true")]
    return {"name": "fractal_aurora", "saveFile": "minimal_colony.rws", "steps": s}


# ----- scenario 4: the flash pack ---------------------------------------------------------

# Pure marketing frames for the Workshop page — no A/B, no control, no claim being evidenced.
# Every shot here is chosen because it looks good, and each one is the ON state only. The
# showcase scenario above is the honest one; this is the trailer.
def flash():
    s = build_scene()
    n = "fxf"
    s += [look("0,0", ZOOM_WIDE)] + warmup(n)

    # 1. Rake light. The sun is nearly down, so every one of the pyramid's 243 pillars throws
    #    a shadow several times its own length and the ground becomes the fractal.
    s += [step("SetTime", hour="18.6"), settle(),
          step("Screenshot", fileName=f"{n}_rakelight_wide.png"),
          look(PYRAMID_AT, ZOOM_PYRAMID), settle(),
          step("Screenshot", fileName=f"{n}_rakelight_pyramid.png"),
          look(CARPET_AT, ZOOM_CARPET), settle(),
          step("Screenshot", fileName=f"{n}_rakelight_carpet.png")]

    # 2. Dawn fog: the softest, least contrasty frame in the set, and the only one where the
    #    torches read as haloes rather than points.
    s += [look("0,0", ZOOM_WIDE), step("SetWeather", weatherDef="Fog", instant="true"),
          step("SetTime", hour="6.8"), settle(),
          step("Screenshot", fileName=f"{n}_dawnfog_wide.png")]

    # 3. Storm at dusk — the mod's weather dimming at its most aggressive.
    s += [step("SetWeather", weatherDef="RainyThunderstorm", instant="true"),
          step("SetTime", hour="20"), settle(),
          step("Screenshot", fileName=f"{n}_storm_wide.png"),
          look(CARPET_AT, ZOOM_CARPET), settle(),
          step("Screenshot", fileName=f"{n}_storm_carpet.png")]

    # 4. Deep night, torches only. This used to flip `pitch_black_true` to force the night floor
    #    to 0, on the understanding that the shipped floor sat above it. That is no longer true:
    #    as of the current build NightRadianceMath.DefaultMinNightBrightness is 0, so the toggle
    #    changed nothing and the flip only made the frame look like it needed a non-default
    #    setting to reproduce. Removed rather than kept as a no-op — a marketing frame that a
    #    player cannot get on a fresh install is worse than no frame, and this one they can.
    s += [step("SetWeather", weatherDef="Clear", instant="true"),
          step("SetTime", hour="1"),
          look(CARPET_AT, ZOOM_CARPET), settle(),
          step("Screenshot", fileName=f"{n}_deepnight_carpet.png"),
          look("0,0", ZOOM_WIDE), settle(),
          step("Screenshot", fileName=f"{n}_deepnight_wide.png")]

    # 5. The hero video: sunset to full dark over the pyramid at double the showcase's frame
    #    rate, so the shadows sweep smoothly instead of stepping.
    s += [look(PYRAMID_AT, ZOOM_PYRAMID),
          step("Timelapse", fromHour="12", stepHours="0.25", steps="96",
               fileNamePrefix="fxf_day_pyramid", settleFrames="4", fps="12")]

    # 6. Snow. Two earlier runs tried to get here through gameplay — winter plus 7500 ticks of
    #    SnowHard, then the same again on an IceSheet tile — and both came back with bare ground,
    #    because depth accrues from TEMPERATURE over ticks and this tile is warm. The harness now
    #    has a SetSnow step that writes the grid directly, which is the whole difference between
    #    "snowy scene" being a two-boot gamble and being one line.
    #
    #    0.55 rather than 1: full depth buries the 1-cell holes that make the carpet legible as a
    #    fractal, and a partial dusting keeps the geometry while still turning the ground white.
    #    Late afternoon, so the low sun rakes across a high-albedo surface.
    s += [step("SetWeather", weatherDef="SnowGentle", instant="true"),
          step("SetSnow", depth="0.55", width=PAD_W, height=PAD_H, anchor=SITE, offset=PAD_AT),
          step("SetTime", hour="16.5"),
          look("0,0", ZOOM_WIDE), settle(),
          step("Screenshot", fileName=f"{n}_snow_wide.png"),
          look(CARPET_AT, ZOOM_CARPET), settle(),
          step("Screenshot", fileName=f"{n}_snow_carpet.png"),
          look(PYRAMID_AT, ZOOM_PYRAMID), settle(),
          step("Screenshot", fileName=f"{n}_snow_pyramid.png")]

    return {"name": "fractal_flash", "saveFile": "minimal_colony.rws", "steps": s}


def main():
    out_dir = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                            "..", "..", "Tests", "Scenarios"))
    for scenario in (showcase(), eclipse(), aurora(), flash()):
        path = os.path.join(out_dir, scenario["name"] + ".json")
        with open(path, "w") as f:
            json.dump(scenario, f, indent=2)
            f.write("\n")
        print(f"{path}: {len(scenario['steps'])} steps")
    print(f"carpet {len(CARPET)} walls, pyramid {len(PYRAMID)} walls")


if __name__ == "__main__":
    main()
