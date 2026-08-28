#!/usr/bin/env python3
"""The colony the three stress scenarios share, and the placement rules that keep it spawnable.

WHY ONE MODULE FOR THREE SCENARIOS. The brief was one map with three different loads on it -- five
hundred coloured lamps, then the same map with thirty doors swinging, then the same map with fifty
pawns walking. If each generator drew its own colony the three would diverge the first time one of
them was tuned, and the numbers would stop being comparable to each other, which is the only reason
to build them on one map in the first place. So the geometry lives here and the three generators
differ only in what they do to it.

WHY THE RANDOMNESS IS BAKED, NOT ROLLED AT RUN TIME. The walls are meant to be irregular -- a
lattice of identical rooms is a scene the shadow bake can be accidentally good at, and every existing
vector-light fixture in this repo is a hand-transcribed regular one. But a scenario that rolled its
own layout would place its walls somewhere else on every run, and an A/B across two runs would then
measure the layout rather than the change. So the dice are thrown HERE, with a fixed seed, and the
JSON that results is a fully explicit cell list. Re-running this script reproduces it byte for byte;
running the scenario twice cannot move a single wall.

WHAT DECIDES THE SIZE OF THE PLATE. VectorLightOverlay culls against the view, so a lamp off screen
is a lamp that costs almost nothing -- profile a tight zoom and you measure the culling and report a
fraction of the cost as though it were the whole. Every lamp therefore has to be on screen at once.
RimWorld's camera clamps rootSize at 60, which at 16:9 shows approximately 213x120 cells, so the
plate is 140x88: comfortably inside that at maximum zoom-out, with margin for the aspect ratio of
whatever resolution the run captures at.
"""

import random

# ----- The plate ------------------------------------------------------------------------------

# Offset of the plate's centre from map centre. North-east of the fixture's own colony, so `clear`
# does not bulldoze the save's buildings and pawns out from under a run: the plate spans x 93..224,
# z 163..238 on the 250x250 fixture and the colony sits around the middle of the map.
#
# THE EXACT POSITION IS CHOSEN TO MISS THE FIXTURE'S STEAM GEYSERS, and that is not a nicety.
# SetTerrain's `clear` destroys what stands in its footprint, a SteamGeyser is flagged
# not-destroyable, and a clear that cannot remove a blocker is reported as an ERROR -- which fails
# the run outright, however good every other number in it is. The first cut of this plate was
# 140x88 at (0,50) and it swallowed three of the five geysers, so the whole scenario came back red
# on twelve cells of scenery. The fixture's geysers are at (91,202), (114,71), (139,147), (192,160)
# and (226,198), each two cells square; this rectangle clears all five with cells to spare on both
# axes. Move it and you have to re-check them.
ORIGIN_X, ORIGIN_Z = 34, 76
ORIGIN = f"{ORIGIN_X},{ORIGIN_Z}"

PLATE_W = 132
PLATE_H = 76

# Local coordinates are relative to ORIGIN, which is what every step's `offset` is measured from.
X_MIN, X_MAX = -PLATE_W // 2, PLATE_W // 2 - 1      # -66 .. 65, i.e. map x 93..224
Z_MIN, Z_MAX = -PLATE_H // 2, PLATE_H // 2 - 1      # -38 .. 37, i.e. map z 163..238

# One cell of the plate is left unbuilt all the way round. Nothing needs it structurally; it means a
# room wall never sits ON the boundary between painted concrete and whatever the fixture's biome put
# down, where a screenshot would show the plate's own edge as a hard line through the geometry.
BUILD_MARGIN = 1

# ----- Rooms ----------------------------------------------------------------------------------

# The plate divides into a 7x4 lattice of blocks and each block gets ONE room somewhere inside it.
# Blocks rather than free scattering because free scattering needs rejection sampling to stop rooms
# touching, and the failure mode when it goes wrong is two rooms sharing a wall -- which silently
# merges them into one room with two doors and changes what the roof and the indoor gates see.
# Six by four, and both have to DIVIDE the plate exactly -- a block size that truncated would leave
# a strip of plate outside every block and therefore roomless, which reads on screen as the colony
# having a suburb nobody built.
BLOCK_COLS, BLOCK_ROWS = 6, 4
BLOCK_W = PLATE_W // BLOCK_COLS   # 22
BLOCK_H = PLATE_H // BLOCK_ROWS   # 19

# Gap from the block's own edge to the room's outer wall, so two rooms in adjacent blocks are always
# at least 2*BLOCK_PAD cells apart and never share or abut a wall.
BLOCK_PAD = 2

ROOM_W_RANGE = (9, BLOCK_W - 2 * BLOCK_PAD)   # 9..18
ROOM_H_RANGE = (9, BLOCK_H - 2 * BLOCK_PAD)   # 9..15

# Share of rooms that get a roof. The rest are open-air courtyards with walls and a door, which is
# not decoration: "some that lead to interiors" is half the point of the door scenario, and a door
# into an unroofed enclosure and a door into a roofed room are different cases for every sky-derived
# gate this mod has. Two thirds roofed keeps both populations big enough to compare.
ROOFED_SHARE = 2 / 3

# Rooms that get a SECOND door, in a different wall. Two jobs: it puts two apertures on one room, so
# a light inside is dirtied by two independent swings rather than one, and it is what lifts the door
# population clear of the thirty stress_door_colony drives. Twenty-four rooms at one door each would
# leave only four spare, and a layout tweak that lost five rooms would then quietly starve that
# scenario -- so the share is set high enough that the margin is comfortable rather than exact.
SECOND_DOOR_SHARE = 0.6

# ----- Free-standing walls --------------------------------------------------------------------

# Wall stubs scattered in the space between rooms: short runs of wall that enclose nothing.
#
# WHY THEY ARE HERE. A room wall only ever occludes a lamp from one side, and a scene made only of
# rooms gives the shadow bake a very tidy problem -- every blocker is axis-aligned, closed, and part
# of a rectangle. Stubs are the untidy half: isolated occluders in open ground with light reaching
# them from every direction, which is what the ray cull and the silhouette memo actually have to
# survive in a real colony.
STUB_COUNT = 100
STUB_LEN_RANGE = (2, 7)

# ----- Power ----------------------------------------------------------------------------------

# Twenty toxifier generators, as briefed. 2x2 each, 1400 W each, 28,000 W in total against a lamp
# draw of 12,000 W -- a margin wide enough that no lamp goes dark because a generator happened to be
# stunned or shut down.
#
# WHY THE GENERATOR MATTERS BEYOND THE WATTAGE. CompPowerPlant zeroes a toxifier's output when its
# CompToxifier cannot find a pollutable cell in radius 26.9, and an unpowered lamp does not glow at
# all: CompPowerTrader is an IThingGlower, so the roster would simply be short a few hundred
# emitters. Concrete's canBePolluted defaults to true, so a freshly painted plate is entirely
# pollutable and the generators run -- but that is a fact about a def field, not something the
# scenario can see, which is why vector_light_count is pinned at the full lamp count rather than
# left to be read off a screenshot.
GENERATOR_COUNT = 20
GENERATOR_DEF = "ToxifierGenerator"

# Clearance reserved around each generator's anchor. The def is 2x2 and GenAdj.OccupiedRect resolves
# which two-by-two from the anchor and rotation, so rather than reimplement that here the generator
# keeps a 3x3 to itself and nothing else is allowed into it.
GENERATOR_CLEARANCE = 1

# HiddenConduit rather than PowerConduit, and not only because the brief says hidden. A plain conduit
# draws a visible cable on every cell it occupies, and this carpets the entire plate -- every outdoor
# pixel of every screenshot would be conduit texture, which is precisely the surface a lighting A/B
# is trying to read. HiddenConduit renders nothing at all: its graphic is (0,0,0,0) on the
# Transparent shader.
CONDUIT_DEF = "HiddenConduit"

# ----- Lamps ----------------------------------------------------------------------------------

# Five hundred lamps, as briefed, split across three defs.
#
# WHY THIS MIX AND NOT FIVE HUNDRED STANDING LAMPS. StandingLamp is the powered, colour-pickable one
# and it is deliberately the large majority -- it is what makes the conduit and the generators load
# bearing, since an unpowered StandingLamp contributes no emitter at all. TorchLamp and AncientLamp
# carry a glower and nothing else, so they light regardless of the power net; keeping some means a
# collapse of the net shows up as a partial loss in vector_light_count rather than an empty map,
# which is a more informative failure. They also come in at radius 10 and 5.5 against StandingLamp's
# 12, so the scene has three base radii before the palette's overrides touch it.
#
# SunLamp is deliberately absent despite being the obvious fourth. It carries a CompSchedule that
# runs 06:00 to 19:12, and these scenarios are shot at hour 0 so every one of them would be dark --
# a silent subtraction from the emitter count that would look like a power fault.
LAMP_DEFS = [
    ("StandingLamp", 400),   # 30 W each, 12,000 W total
    ("TorchLamp", 70),       # unpowered
    ("AncientLamp", 30),     # unpowered
]

LAMP_COUNT = sum(count for _, count in LAMP_DEFS)

# Glowers the FIXTURE brings, which every map-wide count has to carry on top of ours.
#
# minimal_colony.rws is a real colony save with its own lit buildings, they sit south of the plate
# and so survive `clear`, and neither the glow grid nor VectorLightField has any notion of "inside
# the plate" -- so vector_light_count, vector_light_emitters and glow_colour_overrides all see them.
#
# MEASURED, NOT ASSUMED: read off the first live run of stress_light_colony, which reported 503
# against a pinned 500. Written down as its own constant rather than folded into a magic 503 so that
# a fixture regenerated with a different number of lamps fails with an arithmetic that says why. It
# is also why the pins are exact rather than tolerant: a handful of extra emitters is a fact about
# the save, and the right response to it moving is to re-measure, not to widen a tolerance until it
# stops mattering.
FIXTURE_GLOWERS = 3

# Every emitter on the map once the colony is built.
TOTAL_GLOWERS = LAMP_COUNT + FIXTURE_GLOWERS

# Minimum Chebyshev separation between two lamps: 2 means no two are adjacent, but two cells apart
# is allowed. Not a realism constraint -- it stops the sampler dropping a clump on one square of the
# plate and leaving a quarter of it unlit, which would make the scene's average overlap meaningless.
#
# WHY NOT 3. Each lamp claims a (2s-1)-square of the plate, so the number that fit scales as 1/s^2:
# at 2 the roughly 9,200 free cells hold on the order of a thousand lamps and five hundred is
# comfortable, while at 3 the ceiling falls to about 370 and the sampler cannot place the population
# at all. The failure would be a RuntimeError here rather than a bad scenario, but the margin is
# worth stating -- raising this is not free.
LAMP_MIN_SPACING = 2

# ----- The palette ----------------------------------------------------------------------------

# Colours in vanilla's 0-255 ColorInt units, the same ones the glower defs are written in.
#
# WHY THESE ARE SATURATED AND NOT A SPREAD OF WHITES. The point of a coloured population is that the
# composition has to reconcile emitters that disagree, and two lamps a few points apart on the warm
# axis do not disagree about anything. These span the hue circle at full chroma plus the two vanilla
# lamp whites, so overlapping discs genuinely have to blend rather than reinforce.
#
# The list length is deliberately coprime with nothing in particular, but it is NOT a divisor of any
# lamp count above: the step cycles the palette over lamps sorted by cell, so a length that divided
# evenly into a row would paint stripes down the plate instead of scattering colour across it.
PALETTE = [
    (255, 60, 60),      # red
    (255, 150, 40),     # orange
    (245, 235, 90),     # yellow
    (110, 245, 110),    # green
    (60, 220, 210),     # cyan
    (70, 140, 255),     # blue
    (170, 90, 255),     # violet
    (255, 90, 200),     # magenta
    (214, 148, 94),     # vanilla StandingLamp warm white
    (184, 136, 83),     # vanilla TorchLamp warm white
    (120, 255, 180),    # mint
]

# Radii cycled over the same lamps on their own cycle length, so colour and size vary independently.
#
# WHY VARY RADIUS AT ALL WHEN THE BRIEF SAID COLOUR. A recolour is cheap by design -- Upsert dirties
# a polygon on a move or a resize and explicitly not on a colour change, because the shape is
# identical and the colour rides on the material property block. So a palette alone stresses upload
# and composition and provokes no rebakes whatever. Radius is the other half, and it is also what
# spreads the scene across VectorLightOverlay's per-integer-radius gradient and material caches.
# This repo has already shipped a per-emitter texture overflow that a single-radius fixture could not
# have caught, so a stress fixture that varied only colour would be repeating that mistake.
#
# Eleven values against eleven colours would pair the two one to one and give eleven combinations
# instead of the product; nine against eleven gives ninety-nine.
# Every value is an exact multiple of 0.25 on purpose. VectorLightOverlay keys its gradient and
# material caches on RoundToInt(radius * 4), so a radius that landed on a midpoint of that scale
# would depend on the rounding mode agreeing between this generator and the game to decide which
# cache entry it hit — and the probe below pins the count of those entries. Exact quarters make the
# key unambiguous: these are 16, 22, 29, 35, 42, 48, 53, 60 and 69.
RADII = [4.0, 5.5, 7.25, 8.75, 10.5, 12.0, 13.25, 15.0, 17.25]

# ----- Determinism ------------------------------------------------------------------------------

# The one seed the whole layout hangs off. Changing it redraws every wall, every stub, every lamp
# position and every door, so a scenario regenerated under a new seed is not comparable to numbers
# measured under the old one. If it ever has to move, it is a new scenario name, not an edit.
SEED = 20260827


class Colony:
    """The baked layout: cell sets and lists, all in plate-local coordinates."""

    def __init__(self):
        self.wall_cells = []        # ordered, excludes door cells
        self.door_cells = []        # ordered; each is a gap left in a room's wall
        self.roofed_rooms = []      # (x0, z0, w, h) outer rects, walls included
        self.rooms = []             # every room, roofed or not, same rect form
        self.interior_doors = []    # doors whose room is roofed
        self.courtyard_doors = []   # doors whose room is not
        self.generators = []        # anchor cells
        self.lamps = []             # (def_name, x, z)
        self.conduit_cells = []     # every buildable cell of the plate

    @property
    def occupied(self):
        """Every cell something structural stands on, for the samplers to avoid."""
        return set(self.wall_cells) | set(self.door_cells) | self._generator_footprints()

    def _generator_footprints(self):
        cells = set()
        for gx, gz in self.generators:
            for dx in range(-GENERATOR_CLEARANCE, GENERATOR_CLEARANCE + 2):
                for dz in range(-GENERATOR_CLEARANCE, GENERATOR_CLEARANCE + 2):
                    cells.add((gx + dx, gz + dz))
        return cells


def _in_plate(x, z):
    return (X_MIN + BUILD_MARGIN <= x <= X_MAX - BUILD_MARGIN
            and Z_MIN + BUILD_MARGIN <= z <= Z_MAX - BUILD_MARGIN)


def _room_rects(rng):
    """One room per block, sized and positioned inside it."""
    rooms = []
    for col in range(BLOCK_COLS):
        for row in range(BLOCK_ROWS):
            bx = X_MIN + col * BLOCK_W
            bz = Z_MIN + row * BLOCK_H

            w = rng.randint(*ROOM_W_RANGE)
            h = rng.randint(*ROOM_H_RANGE)

            x0 = rng.randint(bx + BLOCK_PAD, bx + BLOCK_W - BLOCK_PAD - w)
            z0 = rng.randint(bz + BLOCK_PAD, bz + BLOCK_H - BLOCK_PAD - h)

            rooms.append((x0, z0, w, h))
    return rooms


def _perimeter(rect):
    """The wall ring of a room rect, corners included, in a stable order."""
    x0, z0, w, h = rect
    cells = []
    for x in range(x0, x0 + w):
        cells.append((x, z0))
        cells.append((x, z0 + h - 1))
    for z in range(z0 + 1, z0 + h - 1):
        cells.append((x0, z))
        cells.append((x0 + w - 1, z))
    return cells


def _door_candidates(rect):
    """Wall cells a door may occupy: mid-run only, never a corner.

    A door in a corner leaves the two walls meeting at it disconnected, which turns the room into an
    open shape and stops it being a room at all -- one wall cell is enough to flip a roofed interior
    into an outdoor room, and this repo has that pinned in room_parity.json.
    """
    x0, z0, w, h = rect
    candidates = []
    for x in range(x0 + 2, x0 + w - 2):
        candidates.append((x, z0))
        candidates.append((x, z0 + h - 1))
    for z in range(z0 + 2, z0 + h - 2):
        candidates.append((x0, z))
        candidates.append((x0 + w - 1, z))
    return candidates


def _side_of(rect, cell):
    """Which wall a door sits in, so a second door can be forced into a different one."""
    x0, z0, w, h = rect
    x, z = cell
    if z == z0:
        return "south"
    if z == z0 + h - 1:
        return "north"
    return "west" if x == x0 else "east"


def _place_stubs(rng, blocked):
    """Short free-standing wall runs in the gaps, avoiding anything already standing."""
    stubs = []
    placed = 0
    taken = set(blocked)

    # `placed` counts STUBS; `stubs` accumulates their CELLS. Counting the latter in the loop
    # condition is a mistake that costs nothing visible: it stops at 120 cells rather than 120 stubs,
    # so the scene comes out with about a quarter of the free-standing occluders it says it has and
    # every number measured over it is quietly measured over a tidier map.
    attempts = 0
    while placed < STUB_COUNT and attempts < STUB_COUNT * 200:
        attempts += 1

        length = rng.randint(*STUB_LEN_RANGE)
        horizontal = rng.random() < 0.5
        x = rng.randint(X_MIN + BUILD_MARGIN, X_MAX - BUILD_MARGIN)
        z = rng.randint(Z_MIN + BUILD_MARGIN, Z_MAX - BUILD_MARGIN)

        cells = [(x + i, z) if horizontal else (x, z + i) for i in range(length)]

        if _stub_fits(cells, taken):
            stubs.extend(cells)
            placed += 1
            # The stub itself AND a one-cell skirt go into `taken`, so two stubs never end up
            # adjacent and silently form one longer wall -- the point of a stub is that it is short.
            for cx, cz in cells:
                for dx in (-1, 0, 1):
                    for dz in (-1, 0, 1):
                        taken.add((cx + dx, cz + dz))

    if placed != STUB_COUNT:
        raise RuntimeError(
            f"placed {placed} wall stubs, expected {STUB_COUNT} — the gaps between rooms no longer "
            "hold them, so either the rooms grew or STUB_LEN_RANGE did")

    return stubs


def _stub_fits(cells, taken):
    return all(_in_plate(x, z) and (x, z) not in taken for x, z in cells)


def _place_generators(rng, blocked):
    """Twenty generator anchors on open ground, spread over the plate rather than clustered."""
    anchors = []
    taken = set(blocked)

    # A coarse lattice of candidate positions, shuffled and then filtered, rather than pure rejection
    # sampling: twenty 3x3 footprints scattered by luck alone tend to leave a quarter of the plate
    # with no generator near it, and the whole point of twenty is that the supply is everywhere.
    step_x = PLATE_W // 5
    step_z = PLATE_H // 4
    candidates = []
    for i in range(5):
        for j in range(4):
            candidates.append((
                X_MIN + step_x // 2 + i * step_x,
                Z_MIN + step_z // 2 + j * step_z,
            ))

    for cx, cz in candidates:
        anchor = _nearest_free(rng, cx, cz, taken)
        if anchor is not None:
            anchors.append(anchor)
            for dx in range(-GENERATOR_CLEARANCE, GENERATOR_CLEARANCE + 2):
                for dz in range(-GENERATOR_CLEARANCE, GENERATOR_CLEARANCE + 2):
                    taken.add((anchor[0] + dx, anchor[1] + dz))

    if len(anchors) != GENERATOR_COUNT:
        raise RuntimeError(
            f"placed {len(anchors)} generators, expected {GENERATOR_COUNT} — "
            "the lattice no longer fits the plate")

    return anchors


def _nearest_free(rng, cx, cz, taken):
    """The nearest 3x3 clearing to a lattice point, searched outward so the spread is preserved."""
    for radius in range(0, 12):
        offsets = [(dx, dz)
                   for dx in range(-radius, radius + 1)
                   for dz in range(-radius, radius + 1)
                   if max(abs(dx), abs(dz)) == radius]
        rng.shuffle(offsets)
        for dx, dz in offsets:
            anchor = (cx + dx, cz + dz)
            if _clearing_free(anchor, taken):
                return anchor
    return None


def _clearing_free(anchor, taken):
    ax, az = anchor
    for dx in range(-GENERATOR_CLEARANCE, GENERATOR_CLEARANCE + 2):
        for dz in range(-GENERATOR_CLEARANCE, GENERATOR_CLEARANCE + 2):
            if not _in_plate(ax + dx, az + dz) or (ax + dx, az + dz) in taken:
                return False
    return True


def _place_lamps(rng, blocked):
    """Lamp cells: free, spaced, and drawn from the whole plate so rooms and open ground both get some.

    Shuffle-and-sweep rather than rejection sampling. Rejection sampling here would ask "is this cell
    at least s from all five hundred chosen lamps" on every throw, which is quadratic and thrashes
    badly at the end when almost every throw is a reject -- and the thrashing is worst exactly when
    the population is near the plate's capacity, i.e. when a run most needs to terminate. Sweeping a
    shuffled candidate list touches each cell once, is O(n), and is just as random: the shuffle is
    the randomness, and taking the first cell that still fits is the same greedy packing.
    """
    candidates = [
        (x, z)
        for x in range(X_MIN + BUILD_MARGIN, X_MAX - BUILD_MARGIN + 1)
        for z in range(Z_MIN + BUILD_MARGIN, Z_MAX - BUILD_MARGIN + 1)
        if (x, z) not in blocked
    ]
    rng.shuffle(candidates)

    # Cells inside the exclusion skirt of an already-placed lamp, so the spacing test is one set
    # lookup rather than a scan over everything placed so far.
    shadowed = set()
    chosen = []

    # Sweeps the whole candidate list rather than breaking out once the population is full. Twelve
    # thousand set lookups cost nothing, and the loop reads as one condition instead of a stop and a
    # test that have to be held in mind together.
    for cell in candidates:
        if len(chosen) < LAMP_COUNT and cell not in shadowed:
            chosen.append(cell)
            x, z = cell
            for dx in range(-(LAMP_MIN_SPACING - 1), LAMP_MIN_SPACING):
                for dz in range(-(LAMP_MIN_SPACING - 1), LAMP_MIN_SPACING):
                    shadowed.add((x + dx, z + dz))

    if len(chosen) != LAMP_COUNT:
        raise RuntimeError(
            f"placed {len(chosen)} lamps, expected {LAMP_COUNT} — "
            "the plate is too crowded for LAMP_MIN_SPACING")

    # Assigned to defs AFTER positions are chosen, and interleaved rather than blocked, so the three
    # defs are mixed through the plate instead of the unpowered ones all landing in one corner --
    # which would make a power fault look like a regional lighting bug.
    order = []
    for def_name, count in LAMP_DEFS:
        order.extend([def_name] * count)
    rng.shuffle(order)

    return [(order[i], x, z) for i, (x, z) in enumerate(chosen)]


def build():
    """Bake the colony. Deterministic: same seed in, same cells out."""
    rng = random.Random(SEED)
    colony = Colony()

    colony.rooms = _room_rects(rng)

    roofed_count = int(round(len(colony.rooms) * ROOFED_SHARE))
    roofed_flags = [True] * roofed_count + [False] * (len(colony.rooms) - roofed_count)
    rng.shuffle(roofed_flags)

    wall_cells = []
    for rect, roofed in zip(colony.rooms, roofed_flags):
        doors = _pick_doors(rng, rect)
        colony.door_cells.extend(doors)
        (colony.interior_doors if roofed else colony.courtyard_doors).extend(doors)

        if roofed:
            colony.roofed_rooms.append(rect)

        wall_cells.extend(cell for cell in _perimeter(rect) if cell not in doors)

    colony.wall_cells = wall_cells

    blocked = set(colony.wall_cells) | set(colony.door_cells)
    # Stubs must also stay out of every room's INTERIOR: a stub inside a room is a legal wall, but it
    # subdivides the room, and a room split in two by a stub roofs as two rooms with one door between
    # them. Rooms are meant to be simple here; the untidy geometry belongs outside them.
    blocked |= _room_interiors(colony.rooms)

    stubs = _place_stubs(rng, blocked)
    colony.wall_cells = colony.wall_cells + stubs

    blocked = set(colony.wall_cells) | set(colony.door_cells)
    colony.generators = _place_generators(rng, blocked)

    # Generators go on open ground only, so their footprints are added to what lamps must avoid --
    # but room interiors come OFF the blocked set here, because lamps are wanted inside rooms.
    colony.lamps = _place_lamps(rng, set(colony.wall_cells) | set(colony.door_cells)
                                | colony._generator_footprints())

    colony.conduit_cells = [
        (x, z)
        for z in range(Z_MIN + BUILD_MARGIN, Z_MAX - BUILD_MARGIN + 1)
        for x in range(X_MIN + BUILD_MARGIN, X_MAX - BUILD_MARGIN + 1)
    ]

    return colony


def _room_interiors(rooms):
    cells = set()
    for x0, z0, w, h in rooms:
        for x in range(x0 + 1, x0 + w - 1):
            for z in range(z0 + 1, z0 + h - 1):
                cells.add((x, z))
    return cells


def _pick_doors(rng, rect):
    """One door, plus sometimes a second in a different wall."""
    candidates = _door_candidates(rect)
    first = rng.choice(candidates)
    doors = [first]

    if rng.random() < SECOND_DOOR_SHARE:
        elsewhere = [c for c in candidates if _side_of(rect, c) != _side_of(rect, first)]
        if elsewhere:
            doors.append(rng.choice(elsewhere))

    return doors


# ----- Step emission ---------------------------------------------------------------------------

# PlaceThings refuses more than 512 placements in one step (SceneLayout.MaxPlacements), so every cell
# list here is chunked. Chunking rather than raising the cap: the cap is the harness's, a shared repo
# another agent may be in, and a scenario that needs 12,000 conduits is not the argument for changing
# what a scene-building step is allowed to do in one frame.
MAX_PLACEMENTS = 512


def step(step_type, **args):
    return {"type": step_type, "args": {k: str(v) for k, v in args.items()}}


# The repo's idiom for a probe that is RECORDED rather than gated: expectedValue 0 with a tolerance
# nothing can exceed. Used by 229 probe steps across the existing scenarios, and it exists because
# the Probe step has no unpinned form -- the driver reads expectedValue and tolerance unconditionally,
# so a step that omitted them would fail on a missing key rather than simply not asserting.
#
# WHY NOT INVENT A PIN INSTEAD. These are workload counts nobody has measured on a population this
# size. A pin derived by prediction rather than measurement is the thing this repo has a rule
# against: when the arithmetic later moves, the honest fix is to re-run and re-measure, and a
# predicted pin makes that indistinguishable from a regression. First run establishes them; they can
# be tightened afterwards, from the report.
RECORD_TOLERANCE = 1000000000


def record(probe_name):
    """A probe read into the report without gating on it."""
    return step("Probe", probeName=probe_name, expectedValue=0, tolerance=RECORD_TOLERANCE)


def place_cells(def_name, cells, stuff=None, clear=False):
    """PlaceThings steps for a cell list, chunked under the placement cap."""
    steps = []
    for start in range(0, len(cells), MAX_PLACEMENTS):
        chunk = cells[start:start + MAX_PLACEMENTS]
        args = {
            "def": def_name,
            "offset": ORIGIN,
            "layout": "cells",
            "cells": "; ".join(f"{x},{z}" for x, z in chunk),
        }
        if stuff is not None:
            args["stuff"] = stuff
        if clear:
            args["clear"] = "true"
        steps.append(step("PlaceThings", **args))
    return steps


def setup_steps(colony, hour=0, zoom=60):
    """Everything from an empty fixture to a lit, powered, fully built colony.

    ORDER IS LOAD BEARING, and three parts of it are not obvious:

    1. TERRAIN FIRST, WITH clear. It destroys the rock, plants and chunks standing in the footprint
       and strips the roof, so every later placement lands on bare buildable concrete. Without it
       CanSpawnAt refuses on whatever the fixture's biome left there, and PlaceThings reports the
       refusals into a report nobody reads before the frames.

    2. CONDUIT BEFORE WALLS, never after. GenSpawn.CanSpawnAt rejects any cell that is not walkable,
       and a wall makes its cell unwalkable -- so conduit laid after the walls simply would not
       appear under them, and the net would be cut into pieces at every wall it crossed. The reverse
       order is safe: SpawningWipes only lets a NEW thing wipe a conduit if the new thing transmits
       power itself, which a wall does not.

    3. DOORS ON EMPTY CELLS, not onto walls. Same walkability rule -- a door cannot be spawned onto a
       cell a wall already occupies. The room perimeters are emitted with the door cells left out,
       and the doors then fill the gaps.

    The generators are the one deliberate exception to (2): a toxifier DOES transmit power, so it
    wipes the conduit under its own footprint. That is harmless, because it joins the net it just
    broke.
    """
    steps = [
        step("SetTile", latitude=45),
        step("SetSeason", dayOfYear=40),
        step("SetWeather", weatherDef="Clear", instant="true"),
        step("SetTerrain", **{
            "def": "Concrete", "width": PLATE_W, "height": PLATE_H,
            "offset": ORIGIN, "clear": "true",
        }),
    ]

    steps += place_cells(CONDUIT_DEF, colony.conduit_cells)
    steps += place_cells("Wall", colony.wall_cells, stuff="BlocksGranite")
    steps += place_cells("Door", colony.door_cells, stuff="WoodLog")
    steps += place_cells(GENERATOR_DEF, colony.generators)

    for def_name, _ in LAMP_DEFS:
        cells = [(x, z) for name, x, z in colony.lamps if name == def_name]
        steps += place_cells(def_name, cells)

    # Roof goes on last of the structural steps. SetRoof paints a rectangle and does not care what is
    # under it, so it can follow the walls; doing it before them would have the same effect, but this
    # way the whole shell of a room is built by consecutive steps and a report is readable.
    for x0, z0, w, h in colony.roofed_rooms:
        steps.append(step("SetRoof", **{
            "def": "RoofConstructed",
            "width": w,
            "height": h,
            # SetRoof's rect is centred on its offset, while a room rect is given by its corner, so
            # the offset is the room's CENTRE. An even width lands the centre half a cell off, which
            # the harness resolves by integer division the same way it does for PlaceThings' grid --
            # hence the same floor here, so the painted rect and the wall ring agree.
            "offset": f"{ORIGIN_X + x0 + w // 2},{ORIGIN_Z + z0 + h // 2}",
        }))

    steps += [
        step("SetTime", hour=hour),
        step("LookAt", offset=ORIGIN, zoom=zoom),
    ]

    return steps


def palette_steps():
    """Repaint every lamp from the palette, then prove the repaint reached §27's roster."""
    return [
        step("SetGlowColors",
             colors="; ".join(f"{r},{g},{b}" for r, g, b in PALETTE),
             radii="; ".join(str(r) for r in RADII)),
    ]


def feature_steps(vector_lights=True, pawn_shadows=None, changed_dirty=None,
                  glow_blocker=None):
    """The flags every arm states explicitly, because a default that moves rewrites what it measured.

    STATED RATHER THAN INHERITED, always. A flag whose default is flipped later silently rewrites
    what every arm that relied on the default was measuring, and the arm still passes -- this repo has
    a committed frame that read 17.08 where it should have read 20.23 and went green. So every flag
    these scenarios depend on is written down in every arm, including the ones already at their
    default.

    The cloud flags are off for the reason CLAUDE.md gives: the cloud sheet drifts on the tick
    counter, so two runs of one build shade different outdoor cells. These scenarios run the clock
    deliberately, which makes that worse here than in a paused fixture, not better.

    `pawn_shadows` defaults to following `vector_lights` -- an arm with the subsystem off must not be
    left drawing half of it. Passing it explicitly is how stress_pawn_colony isolates what fifty
    pawns' shadows cost from what the rest of the subsystem costs.

    `changed_dirty` defaults to following `vector_lights` for the same reason, and is passed
    explicitly by stress_door_colony to isolate what dirtying only the sections a bake CHANGED saves
    from what the rest of the subsystem costs. It is the one flag here whose two settings both render
    the identical frame -- it decides how often a section is asked to rebake, not what the rebake
    produces -- so an arm that moves it is scored on the profiler's call counts rather than on pixels.
    THE THREE DOOR FLAGS WERE MISSING FROM THIS LIST ENTIRELY, and a door scenario is the worst place
    for the omission this function's own header warns about. All three default true, so every arm ever
    run here inherited them -- including `gated`, which is supposed to be the subsystem absent. It was
    not: `vector_light_door_glow_blocker` rides on its OWN flag rather than on vector_lights, by
    deliberate design (it is gameplay light, and gameplay light is not allowed to ride on a render
    switch), and it makes every door swing write vanilla's light-blocker bit -- which dirties the cell,
    re-floods every light that can see it, and regenerates the lighting overlay there. So the gated arm
    was measuring vanilla PLUS our glow-grid writes and calling the difference a baseline.

    `glow_blocker` defaults to following `vector_lights`, which changes what `gated` measures and is
    meant to: with it off, that arm is finally the untouched game. The other two door flags follow
    vector_lights for the ordinary reason -- an arm with the subsystem off must not be left drawing
    half of it.
    """
    if pawn_shadows is None:
        pawn_shadows = vector_lights

    if changed_dirty is None:
        changed_dirty = vector_lights

    if glow_blocker is None:
        glow_blocker = vector_lights

    flags = [
        ("cloud_cover", False),
        ("cloud_sheet", False),
        ("cloud_presence", False),
        ("cloud_deck_varieties", False),
        ("cloud_volume", False),
        ("vector_lights", vector_lights),
        ("vector_light_penumbra", vector_lights),
        ("vector_light_suppress", vector_lights),
        ("vector_light_blend", vector_lights),
        # The mask is stated before the pawn shadows on purpose: the shadows REQUIRE it, because the
        # coverage grid is the only thing in the mod that can say a pawn behind a wall is not lit by
        # a lamp on the far side of it. An arm that turned the shadows on with the mask off would be
        # asking for a feature that cannot run.
        ("vector_light_mask", vector_lights),
        ("vector_light_pawn_shadows", pawn_shadows),
        # Phase 4b's denominator. On by default, and stated because this colony is exactly the regime
        # it exists for: with lamps every few cells a pawn is lit by many at once, and without the
        # share the shadows stack instead of dividing.
        ("vector_light_shadow_shares", pawn_shadows),
        # Stated in every arm even where it is at its default, per this function's own rule, and with
        # a sharper reason than most: it is the flag the door scenario's third arm exists to move, so
        # an arm that inherited it would be measuring whichever setting its neighbour left behind.
        ("vector_light_changed_dirty", changed_dirty),
        # Its partner. The changed-dirty comparison is only sound while a section bakes against an
        # emitter's PREVIOUS shape rather than dropping it, so the two are a pair to keep in step --
        # see CelestialLightingFeatures.VectorLightChangedDirty. Stated here so an arm cannot end up
        # measuring the stand-down path while claiming to measure the feature.
        ("vector_light_stale_polygon", vector_lights),
        # The three door flags, stated at last. The first two decide whether our POLYGON sees an open
        # door as a hole; the third decides whether vanilla's glow grid is told about it as well.
        # Only the third writes gameplay light, and only the third provokes a section regenerate --
        # which is why it is the one this scenario now varies independently.
        ("vector_light_open_doors", vector_lights),
        ("vector_light_door_aperture", vector_lights),
        ("vector_light_door_glow_blocker", glow_blocker),
    ]
    return [step("SetFeature", featureName=name, enabled="true" if on else "false")
            for name, on in flags]


def settle_steps(frames=150, speed="superfast"):
    """Run the colony long enough for the power net to solve and EVERY lamp to register.

    NOT A COSMETIC PAUSE, and the length of it is measured rather than guessed. CompPowerTrader is an
    IThingGlower, so an unpowered lamp does not glow, and a lamp that does not glow is not in §27's
    roster at all -- the difference between a settled and an unsettled colony here is the difference
    between five hundred emitters and some number nobody predicted.

    WHY 150 FRAMES OF SUPERFAST AND NOT 180 OF NORMAL, which is what this used to be. Four hundred
    lamps do not light in the same tick: they come up spread across RimWorld's rare-tick cycle, so
    the roster FILLS over roughly 250 ticks rather than snapping to full. At normal speed 180 frames
    is about 180 ticks, which lands inside that ramp -- the first live run of stress_light_colony
    probed here and read 220 of 503, a number that looks exactly like 283 lamps on a dead power net
    and is nothing of the kind. The same run's workload probes, taken at the end, read the full 503.
    Superfast advances roughly six ticks a frame, so 150 frames is about 900 -- comfortably past the
    ramp, and cheaper in wall-clock than the normal-speed window it replaces.

    Paused at the end so the steps that follow read a still colony at a known tick.
    """
    return [
        step("SetTimeSpeed", speed=speed),
        step("Wait", frames=frames),
        step("SetTimeSpeed", speed="paused"),
    ]


def perf_assert(table, metric, max_value, label="*"):
    """Assert one number out of a harvested profile table, and thereby RECORD it in the report.

    WHY THESE EXIST AT ALL WHEN THE TABLE IS ALREADY IN THE REPORT. A ProfileAssert lands in the same
    ProbeChecks list an ordinary probe does, so the number appears in run_test.sh's own results block
    with its bound next to it -- which is where anyone actually reads a run. A table buried in the
    JSON is evidence nobody looks at, and this repo has the same rule about screenshots.

    WHY THE BOUNDS ARE GENEROUS RATHER THAN TIGHT. This box has measured frame_max_ms spanning 37 to
    85 across three consecutive runs of ONE build, and vector_light_bake_wall_ms moving 40% between
    two runs of an unchanged binary. A bound set close to a measured value on hardware like that is a
    gate that fails for weather, and a gate that fails for weather gets switched off. These are set
    to catch a regression of several times over, which is the size of change a stress scenario is
    for; the value itself is the deliverable, and the bound is only there to stop it rotting.
    """
    return step("ProfileAssert", table=table, label=label, metric=metric, max=max_value)


def establish_steps():
    """Bring the colony up in its measured state: subsystem on, clock run, then paused.

    SEPARATE FROM THE ARMS, and it has to be. The population pins below read VectorLightField's
    roster, which only the subsystem maintains -- probing them between arms, or before the first one,
    reads a roster nobody was keeping. Establishing once up front also means the arms are pure flag
    flips against a colony that is already settled, rather than each one paying for the power net to
    solve inside its own measured window.
    """
    return feature_steps(vector_lights=True) + settle_steps()


def population_probes():
    """The pins that say the scene is the scene, read before anything is measured on it.

    Every one of these is here because its failure is INVISIBLE in a frame. A dead power net, a
    palette that never reached the roster, and a fixture that collapsed to one radius all produce a
    map full of lit lamps that photographs perfectly well, and all three would make every number
    after them a measurement of something other than what the scenario is named for.
    """
    return [
        # The power check, stated as an emitter count because that is what a dead net actually costs.
        #
        # READ ONLY AFTER VECTOR LIGHTING IS ON. VectorLightField's roster is maintained by the
        # subsystem, so with the flag down it holds whatever it happened to hold -- the first cut of
        # this scenario probed here before any arm had enabled anything and read 223, which looks
        # exactly like 277 dead lamps and is nothing of the kind. establish_steps() is what puts the
        # flags up before this runs.
        step("Probe", probeName="vector_light_count",
             expectedValue=TOTAL_GLOWERS, tolerance=0),
        # The step touched every lamp...
        step("Probe", probeName="glow_colour_overrides",
             expectedValue=TOTAL_GLOWERS, tolerance=0),
        # ...and the roster heard about it. Pinned to the palette length: fewer means the recolour
        # stopped at the comp, which no frame would show.
        step("Probe", probeName="glow_emitter_colours",
             expectedValue=len(PALETTE), tolerance=0),
        # Distinct material-cache keys among the emitters, in the quarter cells the cache is keyed
        # in. Every RADII value is an exact quarter, so the count is simply the list length — and
        # pinning it means a future edit that collapses two radii onto one cache key fails here
        # instead of quietly halving what the fixture covers.
        step("Probe", probeName="glow_emitter_radii",
             expectedValue=len(set(round(r * 4) for r in RADII)), tolerance=0),
    ]
