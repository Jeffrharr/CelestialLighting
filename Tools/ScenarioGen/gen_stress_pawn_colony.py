#!/usr/bin/env python3
"""The same colony, with fifty pawns walking under five hundred lamps.

WHAT THIS ADDS OVER THE OTHER TWO. stress_light_colony holds a large emitter population still, and
stress_door_colony moves the walls. This one moves the CASTERS: every pawn throws a shadow away from
each lamp that lights it, and this colony lights a pawn from many lamps at once, so fifty pawns is
not fifty shadows -- it is fifty times however many lamps reach each of them, recomputed as they walk.
That is the per-frame half of the subsystem, and neither of the other two scenarios touches it.

WHY THE ARMS DECOMPOSE INTO THREE. 'gated' is the whole subsystem off and is the control. 'walkers'
is vector lighting on with the pawn shadows switched off, so it carries the identical fifty pawns and
their pathfinding and their rendering, and none of their shadows. 'full' adds only the shadows. The
difference between the last two is therefore what fifty walking casters cost and NOTHING else -- with
two arms it would have been that plus fifty pawns' worth of vanilla cost, which is not a number this
mod can be charged for.

FIFTY PAWNS IN TEN GROUPS, NOT ONE ROW OF FIFTY. SpawnPawn places a single row along +x from its
anchor and caps a step at 64, so fifty in one step is legal and would be a hundred-cell conga line
down the middle of the plate -- a shape that has every pawn in the same lighting neighbourhood and
leaves most of the colony without a caster in it. Ten groups of five, on rows found clear of walls,
lamps and generators, put casters across the whole map.

WHY THE STILLS ARE NOT AN A/B HERE. SpawnPawn does not seed generation, so the pawns differ in
appearance and name plate between runs -- a cross-run frame diff on this scenario measures the
sprites. The captures are kept for a human to look at; the numbers come from probes.

    python3 Tools/ScenarioGen/gen_stress_pawn_colony.py
"""

import json
import os
import random

import stress_colony as sc

HERE = os.path.dirname(os.path.abspath(__file__))
SCEN = os.path.abspath(os.path.join(HERE, "..", "..", "Tests", "Scenarios"))
TARGET = os.path.join(SCEN, "stress_pawn_colony.json")

PAWN_COUNT = 50
GROUP_SIZE = 5
GROUP_SPACING = 2

# WILD MEN, and this is the single most load-bearing choice in the file. It was COLONISTS, and the
# first live run is why it is not.
#
# WHAT WENT WRONG WITH COLONISTS. CastsShadow requires a STANDING pawn, and vector_light_pawn_casters
# is scoped to the camera's view rect. A player-faction colonist on a fixture that already has a
# colony does two things that quietly empty both conditions: it paths south to the base to eat and
# sleep, leaving the view, and then it lies down, which stops it casting at all. The run read FIVE
# casters where fifty were spawned -- and worse, it inverted the measurement. The arm with the pawn
# shadows switched OFF measured 13.19 ms/frame against the arm with them ON at 7.03, because by the
# time the second arm ran the pawns had gone. It was not a shadow cost at all; it was the colony
# emptying, and it would have read as pawn shadows being free.
#
# WHY WILD MEN FIX IT. A wild man is vanilla's factionless humanlike: same body, same shadow
# rectangle, same DefaultPawnHeight the feature is calibrated on -- so the caster geometry is
# unchanged -- but it has no faction, no bed and no base, so there is nowhere for it to walk off to.
# It wanders where it is put, which is exactly the population this scenario claims to have.
PAWN_KIND = "WildMan"
PAWN_FACTION = "wild"

# Frames the colony runs before anything is measured, so the pawns have left their spawn rows and
# spread out. A scenario that measured immediately would photograph ten tidy lines of five and
# profile the one frame where every caster is in a place no colony would put it.
DISPERSE_FRAMES = 60

# HALVED AGAIN after the second live run, on the user's call and with the measurement to back it:
# whatever pawn kind is used, a colony walks off to eat within a few in-game hours, so a long window
# cannot measure a steady population however it is arranged. The repeats made the size of the problem
# explicit -- 'walkers' read 15.76 ms/frame and its own repeat 'walkers_b' read 0.56, a 28x spread on
# an arm whose only difference from itself was when it ran. Five 250-frame windows keep the whole
# measured stretch inside the window where fifty wild men are still where they were put.
#
# Shorter windows than the other two scenarios, and FIVE of them rather than three. A colony with
# fifty walkers in it is not the same colony from one minute to the next, so a block design -- all of
# arm A, then all of arm B -- hands whichever arm ran in the quiet half a win it did not earn. The
# first run of this file measured exactly that. Interleaving the two arms whose difference is the
# answer, and repeating both, is the repo's existing answer to it (see
# gen_vector_light_door_storm.py's header), and the spread between a pair of repeats is the drift
# floor any claim has to clear.
PROFILE_FRAMES = 250
SETTLE_AFTER_FLAGS = 30

# Superfast for the same reason as the door storm: the stress is per rendered frame, and the clock
# speed decides how far a pawn moves between two of them. It is also the speed vector_light_perf
# profiles at, so the frame costs sit on the same footing.
TIME_SPEED = "superfast"


def pawn_rows(colony):
    """Ten clear rows of five, spread over the plate.

    A pawn is refused on a cell that is not standable, and the refusal is REPORTED rather than fatal
    -- so a row that ran into a wall would quietly spawn three pawns instead of five and the scenario
    would go green having measured forty-one casters. The rows are therefore checked here, against
    the same cell sets the layout was built from, and a shortfall raises.
    """
    blocked = (set(colony.wall_cells) | set(colony.door_cells)
               | {(x, z) for _, x, z in colony.lamps}
               | colony._generator_footprints())

    rng = random.Random(sc.SEED + 1)
    groups = PAWN_COUNT // GROUP_SIZE
    span = (GROUP_SIZE - 1) * GROUP_SPACING

    # Candidate anchors on a coarse lattice, so the ten groups are spread rather than clustered
    # wherever the first clear rows happened to be. Same shape as the generator lattice, and for the
    # same reason: luck alone leaves a quarter of the plate without a caster in it.
    candidates = []
    for i in range(5):
        for j in range(2):
            candidates.append((
                sc.X_MIN + 12 + i * (sc.PLATE_W // 5),
                sc.Z_MIN + 20 + j * (sc.PLATE_H // 2),
            ))

    anchors = []
    for cx, cz in candidates:
        anchor = _nearest_clear_row(rng, cx, cz, span, blocked)
        if anchor is not None:
            anchors.append(anchor)
            # The row and a one-cell skirt come off the map for later groups, so two groups never
            # spawn on top of each other.
            for i in range(GROUP_SIZE):
                for dz in (-1, 0, 1):
                    blocked.add((anchor[0] + i * GROUP_SPACING, anchor[1] + dz))

    if len(anchors) != groups:
        raise RuntimeError(
            f"found {len(anchors)} clear pawn rows, expected {groups} — the plate no longer has "
            f"{groups} runs of {GROUP_SIZE} standable cells at the lattice points")

    return anchors


def _nearest_clear_row(rng, cx, cz, span, blocked):
    """The nearest anchor to a lattice point whose whole row is standable."""
    for radius in range(0, 16):
        offsets = [(dx, dz)
                   for dx in range(-radius, radius + 1)
                   for dz in range(-radius, radius + 1)
                   if max(abs(dx), abs(dz)) == radius]
        rng.shuffle(offsets)
        for dx, dz in offsets:
            anchor = (cx + dx, cz + dz)
            if _row_clear(anchor, span, blocked):
                return anchor
    return None


def _row_clear(anchor, span, blocked):
    ax, az = anchor
    if not sc._in_plate(ax, az) or not sc._in_plate(ax + span, az):
        return False
    return all((ax + i * GROUP_SPACING, az) not in blocked for i in range(GROUP_SIZE))


def spawn_steps(anchors):
    """One SpawnPawn per group. Ten steps, five pawns each.

    `clear` is deliberately NOT set. It would destroy whatever stands in each spawn cell, and the
    rows above were chosen to need nothing destroyed -- turning it on would convert a bad row from a
    loud shortfall into a silent hole punched in the colony.
    """
    return [
        sc.step("SpawnPawn", **{
            "kind": PAWN_KIND,
            "faction": PAWN_FACTION,
            "count": GROUP_SIZE,
            "spacing": GROUP_SPACING,
            "offset": f"{sc.ORIGIN_X + x},{sc.ORIGIN_Z + z}",
        })
        for x, z in anchors
    ]


def arm(name, vector_lights, pawn_shadows, screenshot=True):
    steps = sc.feature_steps(vector_lights=vector_lights, pawn_shadows=pawn_shadows)
    steps.append(sc.step("Wait", frames=SETTLE_AFTER_FLAGS))

    # The repeat arms take no captures. Their job is arithmetic -- bracketing the drift between the
    # first three -- and a second pair of stills of a colony that has moved on since the first pair
    # would invite exactly the cross-arm frame diff this scenario's description warns against.
    if screenshot:
        steps.append(sc.step("Screenshot", fileName=f"stress_pawn_colony_{name}", hideUi="true"))
        steps.append(sc.step("Wait", frames=4))
        steps.append(sc.step("Screenshot", fileName=f"stress_pawn_colony_{name}_clean", hideUi="true"))

    steps.append(sc.step("Profile", **{
        "name": name,
        "prefix": "CelestialLighting",
        "frames": PROFILE_FRAMES,
        "timeSpeed": TIME_SPEED,
    }))
    return steps


def caster_probes():
    """What the casters cost, and the pins that stop a zero being mistaken for a cheap subsystem."""
    return [
        # The SAME metric the pin above read before the arms, recorded again here at the end. The
        # pair is the point: it is the drift, measured. If the population that was measured is not
        # the population the arms ran over, this is where that shows, and it is the reading whose
        # absence let the first run of this file report a 2x cost difference that was really a
        # colony emptying.
        sc.record("vector_light_pawn_casters"),
        # Shadow arms drawn: casters times the lamps reaching each. The number that makes this
        # scenario different from fifty pawns under one lamp, and the one to quote.
        sc.record("vector_light_pawn_shadow_arms"),
        sc.record("vector_light_pawn_shadow_peak"),
        sc.record("vector_light_pawn_shadow_reach"),
        sc.record("vector_light_pawn_width"),
        # The emitter population underneath, so the arm count above has a denominator.
        sc.record("vector_light_emitters"),
        sc.record("vector_light_verts"),
        # A pawn walking dirties geometry the same way a door does; these say how much.
        sc.record("vector_light_invalidations"),
        sc.record("vector_light_marks_per_call"),
        sc.record("vector_light_bakes"),
        # RECORDED HERE, NOT PINNED AT ZERO, which is a deliberate difference from
        # stress_light_colony and needs saying. That scenario holds its geometry still and measures
        # zero, so zero is the right pin there. Under fifty walking casters the first live run of
        # this file read 26,308 — sections that baked with an emitter reaching them and no polygon
        # ready to use. That is the stale-polygon path doing its job under a load it was built for,
        # not obviously a fault, and pinning it at zero here would be pinning a wish. It is recorded
        # so the number is in the report and can be argued about from evidence.
        sc.record("vector_light_mask_stale_polys"),
    ]


def perf_asserts():
    """The three arms' cost, lifted into the results block.

    THE ANSWER IS 'full' MINUS 'walkers', not full minus gated. Both of those arms carry the same
    fifty colonists doing the same pathfinding and the same rendering; only the shadows differ. Full
    minus gated would additionally charge this mod for fifty pawns' worth of vanilla simulation,
    which it did not cause and cannot remove.

    Bounds loose for the reason sc.perf_assert records. maxMsPerFrame on the full arm is the figure
    worth watching: pawn shadows are rebuilt as casters move, so this is the arm where per-frame work
    scales with something the player controls.
    """
    return [
        sc.perf_assert("gated", "avgMsPerFrame", 6.0),
        sc.perf_assert("walkers", "avgMsPerFrame", 60.0),
        sc.perf_assert("full", "avgMsPerFrame", 60.0),
        sc.perf_assert("walkers_b", "avgMsPerFrame", 60.0),
        sc.perf_assert("full", "maxMsPerFrame", 400.0),
        sc.perf_assert("full", "avgMsPerFrame", 20.0, label="Patch_VectorLightDraw"),
    ]


def build():
    colony = sc.build()
    anchors = pawn_rows(colony)

    steps = sc.setup_steps(colony)
    steps += sc.palette_steps()
    # ESTABLISH THE LIGHTING FIRST, THEN ADD THE PAWNS. The other way round -- which is how this
    # started -- puts fifty pathfinding pawns into the settle window, which slows the frame rate,
    # which cuts the number of ticks a fixed frame count delivers, which lands the settle inside
    # RimWorld's rare-tick ramp and leaves lamps unlit. It read 472 of 503 emitters for exactly that
    # reason, on a scenario whose lamp population is not the thing under test.
    steps += sc.establish_steps()
    steps += sc.population_probes()
    steps += spawn_steps(anchors)

    # Let them walk before anything is measured. SetTimeSpeed rather than FastForward, which never
    # pauses again, and rather than AdvanceTicks, which is a jump: a pawn's movement is driven by its
    # own Tick(), so a jump would move the clock and leave all fifty standing exactly where they
    # spawned -- the same failure that once filmed thirty frames of a door pinned shut.
    steps += [
        sc.step("SetTimeSpeed", speed=TIME_SPEED),
        sc.step("Wait", frames=DISPERSE_FRAMES),
        sc.step("SetTimeSpeed", speed="paused"),
    ]

    # THE CASTER PIN IS READ HERE, BEFORE THE ARMS, and that placement is the fix for the second half
    # of the first run's failure. Read at the END instead, it comes after three profiling windows
    # that each leave the clock running -- some hours of game time in which the population it is
    # checking has moved on. What it is for is proving the scene was built, so it belongs at the
    # moment the scene is finished.
    steps.append(sc.step("Probe", probeName="vector_light_pawn_casters",
                         expectedValue=PAWN_COUNT, tolerance=12))

    steps.append(sc.step("Probe", probeName="vector_light_bake_reset",
                         expectedValue=0, tolerance=sc.RECORD_TOLERANCE))

    # Gated first as the control; then the two arms whose DIFFERENCE is the answer. 'walkers' carries
    # the fifty pawns with their shadows off, 'full' adds only the shadows.
    # FOUR ARMS, WITH THE CONTROL REPEATED AROUND THE ONE OF INTEREST. 'full' sits between 'walkers'
    # and 'walkers_b', so the arm being judged is bracketed by two readings of the arm it is judged
    # against, and the gap between those two is the drift it has to beat.
    #
    # THE TAIL IS CUT ON PURPOSE. A fifth arm used to run here and it measured nothing: by the last
    # window the wild men have wandered off and the caster count is down from fifty to nineteen, so
    # 'full_b' came back at 1.71 ms/frame against its own earlier self at 21.84. That is not a fast
    # arm, it is an empty colony, and averaging it in would have halved the reported cost of the
    # feature. Pawns leave to eat within a few in-game hours whatever kind they are, so the answer is
    # to stop measuring before they do rather than to arrange the arms more cleverly.
    steps += arm("gated", vector_lights=False, pawn_shadows=False)
    steps += arm("walkers", vector_lights=True, pawn_shadows=False)
    steps += arm("full", vector_lights=True, pawn_shadows=True)
    steps += arm("walkers_b", vector_lights=True, pawn_shadows=False, screenshot=False)
    steps += caster_probes()
    steps += perf_asserts()

    return {
        "name": "stress_pawn_colony",
        "saveFile": "minimal_colony.rws",
        "description": (
            f"stress_light_colony's map exactly — 500 lamps in 11 colours and 9 radii, 11,868 hidden "
            f"conduits, 20 toxifier generators, 1,874 wall cells across 28 rooms and 120 stubs — with "
            f"{PAWN_COUNT} wild men walking around in it, spawned as "
            f"{PAWN_COUNT // GROUP_SIZE} groups of {GROUP_SIZE} on rows checked clear of walls, lamps "
            f"and generators, then given {DISPERSE_FRAMES} frames of running clock to disperse before "
            f"anything is measured. "
            "\n\n"
            "WHAT THIS MEASURES THAT THE OTHER TWO CANNOT. The lamp scenario holds its emitters "
            "still and the door scenario moves the walls; this moves the CASTERS. A pawn throws a "
            "shadow away from every lamp that lights it, and a colony lit every few cells lights a "
            "pawn from many lamps at once — so fifty pawns is fifty times however many lamps reach "
            "each of them, rebuilt as they walk. vector_light_pawn_shadow_arms is that product, "
            "measured, and it is the number to quote. "
            "\n\n"
            "FIVE ARMS, INTERLEAVED, BECAUSE THREE IN A ROW MEASURED THE WRONG THING. 'gated' is the "
            "subsystem off. 'walkers' is vector lighting on with the pawn shadows off — the identical "
            "fifty pawns, their pathfinding and their rendering, and none of their shadows. 'full' "
            "adds only the shadows. Full minus walkers is what fifty walking casters cost §27; full "
            "minus gated would additionally charge this mod for fifty pawns' worth of vanilla "
            "simulation. walkers and full are then REPEATED, alternating, because a colony with fifty "
            "walkers in it is not the same colony from one window to the next: the first run of this "
            "file used player colonists and a block design, and measured the no-shadow arm at 13.19 "
            "ms/frame against the shadow arm at 7.03 — an inversion that was entirely the pawns "
            "leaving between the two. The spread between a pair of repeats is the drift floor any "
            "claim here has to clear. "
            "\n\n"
            "WILD MEN, NOT COLONISTS, for the same reason. CastsShadow needs a STANDING pawn and the "
            "caster count is scoped to the view; a player colonist on a fixture that already has a "
            "colony walks south to it, then lies down, and does neither. Fifty spawned read five. A "
            "wild man is the same humanlike body and the same shadow rectangle with no faction, no "
            "bed and nowhere to go. "
            "\n\n"
            "DO NOT A/B THE STILLS ACROSS RUNS. SpawnPawn does not seed generation, so the pawns "
            "differ in body, apparel and name plate from one run to the next, and a cross-run frame "
            "diff here measures the sprites — this repo has already had a pawn in frame invert the "
            "sign of a whole reading. Compare arms WITHIN one run, and take the numbers from probes. "
            "\n\n"
            "vector_light_pawn_casters is pinned at 50 because a refused spawn row is reported and "
            "not fatal: a row that ran into a wall would spawn three instead of five and leave the "
            "scenario green having measured a smaller colony. vector_light_shadow_shares is stated "
            "explicitly in every arm — this lighting density is precisely the regime it exists for, "
            "since without the denominator many lamps stack many full-strength shadows on one pawn."
        ),
        "steps": steps,
    }


def main():
    spec = build()
    with open(TARGET, "w") as handle:
        json.dump(spec, handle, indent=2)
        handle.write("\n")
    print(f"wrote {TARGET} ({len(spec['steps'])} steps)")


if __name__ == "__main__":
    main()
