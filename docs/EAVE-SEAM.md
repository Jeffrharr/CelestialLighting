# The eave seam — investigation log

A lighter line between a roofline's cast shadow and the roof's own shade, at the roof's
edge, inside a walled room. Reported from play; annotated by the user on the north
boundary of a roof strip. **Not yet fixed.** This file exists so the dead ends are not
re-walked — an earlier session spent a night re-deriving several of them.

## What it is, measured

`Tests/Scenarios/roof_shadow_walled_vs_open.json`, one frame, 18h, latitude 45, clear,
zoom 18 (30 px/cell). Two 12×12 eaves side by side: an unwalled open-air porch, and a
walled room roofed only across a 2-cell strip. Both classify as eaves. Scanning north to
south through each roofline, `on` = eave casters on, `off` = off:

```
UNWALLED porch   on   … 64 58 | 41  37   35 35 35 …      monotonic
                 off  … 64 58 | 52  46   35 35 35 …

WALLED strip     on   … 63 57 | 41 [44]  34 33 35 …      +8 bounce  <- the seam
                 off  … 63 57 | 52  44   34 33 35 …
```

Three things this pins:

- **`on` and `off` are identical from the bounce onward.** The caster's band simply stops
  there and vanilla's lighting-overlay ramp shows through unmodified. Nothing is drawing
  the wrong colour; something is failing to draw at all.
- **The band is one sample (~0.2 tile) shorter in the walled case.** Same caster height,
  same sun, same boundary ramp in `off` — only the band's reach differs.
- **The unwalled porch is clean.** Confirmed by the user in-game and reproduced here.

Reproduces on `main` at 5979435 with no mod changes, so it is not a regression from any
recent work.

## Ruled out — do not re-test these

| Hypothesis | How it died |
|---|---|
| The eave/enclosed classification (`UsesOutdoorTemperature`) | Both rooms classify as eaves; `eave_cells` counts them identically. The strip room is already over the 25%-open-roof threshold, so a wall gap cannot flip it. |
| Roof width / thin strips | `roof_shadow_strip_heights.json` runs 1, 2, 4 and 8-cell bands. Identical signature at every height. |
| Edge orientation (north vs east edge) | Was believed for a while and is wrong. It came from comparing a porch's *east* edge against a strip's *north* edge; measured at the same edge, the porch is clean and the strip is not. |
| `RoomLookup` differing from vanilla | It mirrors `RegionAndRoomQuery.RoomAt`'s Region → District → Room walk exactly, skipping only the rebuild. |
| §7b sky occlusion | Toggling `indoor_sky_occlusion` does not move these cells; they are eaves, so §7b never touches them. |
| §15b's shade being the wrong tone | The shade is correct where it applies. The gap is *outside* it, on cells it does not cover. |

## Tried as fixes — none shipped

All of these were built, measured live, and reverted. Numbers are the lip above the
settled shade at the roofline (lower is better; 0 is the goal).

| Attempt | Result |
|---|---|
| **Shade the casting edge only** (thick-roof cells that cast). Issue #63's proposal. | Made it worse. §7b already darkens the thick-roof interior, so multiplying only the rim produced a dark 1-cell outline where there had been none: `36 \| 28 \| 38` across the roofline. |
| **Eave-shade lattice: OR at corners**, so the shade reaches the half cell past the roofline that vanilla's corner-OR cover already darkens. | Lip +11 → +3, but the band went to 32 against a settled 35 — the shade and the band both landing on one cell. A double multiply. |
| **Eave-shade lattice: MEAN at corners** instead of OR. | Band came back up to 35, but the roof's own edge row lightened 35 → 39, so the roof was brighter at its rim than in its middle. Lip back to +7. |
| **Narrow vanilla's cover bleed** past a roofline, in `Patch_IndoorSkyOcclusion`. | Worst of the three. Removes cover from the roof's *own* edge corners too, so the roof stops being flat and ramps 47 → 37 across four cells. Also breaks that patch's "only ever raises alpha" rule, which is what keeps it composable with Dub's Skylights and Biomes! Caverns. |

## The rule any fix has to respect

A cell either has direct sun or it does not, so the shadow tint must be applied **once**.
Vanilla gets this free: its sun shadow is a *darken* (`Custom/Sun shadow`, queue
Transparent+175), and `min(min(x,t),t) = min(x,t)`. §15b is an alpha blend
(`ShaderDatabase.Transparent`), which is a multiply, so ours compounds wherever two of
{vanilla roof cover, §15b's shade, a cast band} land on the same cell. Every failed
attempt above is a case of two of them landing together.

No darken-blend shader is reachable from a mod: `ShaderDatabase` has none, and the one
that does (`Custom/Sun shadow`) displaces every vertex by `alpha × _CastVect`, so a flat
per-cell quad drawn with it slides a full shadow-length off the cell. That displacement
is the same structural hole §15b exists to work around.

The user's stated target: **one consistent colour across the eave, the shadow and the
seam.**

## Day 2: isolated to enclosure, and to the mesh

`Tests/Scenarios/eave_seam_walls_vs_depth.json` — a 2-cell strip with walls seams; the SAME strip
without walls is clean; a 12-cell-deep porch is clean. So roof depth is not the variable, walls are.

`Tests/Scenarios/eave_seam_which_wall.json` — five identical strips with none / north / south / ends
/ all four walls. Only **all four** seams (+5); every partial enclosure is clean (+0). The `off`
frames are byte-identical across all five, so the lighting overlay is not involved.

`which_18h_bandonly.png` (casters on, shade OFF) proves it is the shadow MESH and not §15b:

    walls=none   y525 40   y528 36   y531 43     band reaches the roofline
    walls=all    y525 40   y528 45   y531 43     band stops ~3px (0.14 tile) short

Caster grid read directly (`SkirtProbe`, added for this):

    metric              open     walled    delta
    caster_cells        2939     2978      +39     the 43 new walls less 4 already casting
    north_skirt_cells    461      480      +19
    caster_height_sum  292660   296560    +3900    = 39 x 100, so every caster is height 1.0

The +19 closes exactly: top wall 12 emitters, bottom wall 10 (its two ends sit under the side walls),
side walls 0, minus the removed dummy wall (1), minus the strip's two END cells which stop emitting
once they are wall cells beneath the side wall (2). 12+10-1-2 = 19.

**So the strip's ten interior north-row cells emit identical north skirts at identical height 1.0 in
both cases, and still render a shorter band.** Same geometry in, different pixels out. That rules out
caster heights, emitter counts, the eave predicate, the shade and the overlay — everything on the
build side. Whatever remains is in how the mesh is drawn, not how it is built.

Also corrected on day 2: the sun shadow is a **multiply**, not a darken. Measured 49 -> 40 and
45 -> 36 in one frame, both x0.8; a min-against-a-constant tint cannot give two outputs. The day-1
note claiming a darken (and the idempotence argument built on it) is wrong.

## Day 2 attempt — failed

**Extend the shade to the overlay's cover edge (OR corners).** Broke the cases that were working:
every previously clean roofline went +0 -> +6, dipping to 29 before recovering, because the shade
then lands on top of the band where the band already reaches (0.8 x 0.8). The walled case stayed at
+5. Strictly worse; reverted.

## Day 2, second negative result — the strip's end cells are not it

`Tests/Scenarios/eave_seam_inset_strip.json`. The obvious remaining suspect was the roof
running into the side walls, which turns the strip's two end cells into wall cells that stop
emitting north skirts. So: two identical 8x2 strips, one bare, one inside a walled room but
INSET so it touches no wall. Every cell of the inset strip is an eave and emits, exactly as
the bare one does.

    P  bare inset strip     on … 53 | 40  36  35 35    +0   clean
    Q  walled inset strip   on … 53 | 40 [45] 35 35    +5   still seams

So it is not the end cells. The seam survives when the strip's emitters are provably identical
to the clean case's. Enclosure alone does it.

## Remaining hypothesis space

Everything on the build side is eliminated by measurement: caster heights (all 1.0), emitter
counts (accounted to the cell), the eave predicate, §15b's shade, §7b, and the lighting overlay
(byte-identical casters-off frames). The strip's emitters are identical between a clean case and
a seaming one. What is left is how the submesh DRAWS, not how it is built — candidates not yet
tested:

- section boundaries and `subMesh.mesh.bounds` / `RefreshSubMeshBounds`
- `SectionLayer_SunShadows.ShouldDrawDynamic` / `GetSunShadowsViewRect` culling
- overlap and blend order among the extra wall-cast quads an enclosed room adds

## Day 2, the mesh read directly (`SeamDump`)

`SeamDump` logs, per cell, exactly what `EmitCell` decides — heights, the four neighbour heights,
which skirts fire, and the packed alpha. Run `eave_seam_inset_strip.json` with `--no-teardown` and
the `seam_dump` feature on, then grep `SEAMDUMP` out of the run's `Player.log`.

Two traps it caught, both of which produced confident wrong conclusions first:

- **The dump must call the shipped predicate, not restate it.** It originally duplicated
  `casters.At(n) < height`; after the rule under test changed, the dump kept reporting the OLD rule
  and the fix looked like it had not fired when it had.
- **`ls -td /tmp/rwth-run-*` gives a STALE run** unless the run used `--no-teardown`. A teardown run
  deletes its own directory, so the newest surviving one is an earlier build's.

What the dump established, diffing the clean strip against the seaming one cell for cell:

    the two strips' own cells are IDENTICAL in the mesh
    the ONLY difference in the whole window is the wall column beside the walled strip

and the strip's north row emits N=1 at alpha 255 in both. So the roofline's own geometry is not
the difference; the wall's extra quads are.

## Day 2 attempts — all three failed

| Attempt | Result |
|---|---|
| **Extend the shade to the overlay's cover edge (OR corners).** | Broke the working cases: every clean roofline went +0 → +6, dipping to 29, because the shade then lands on the band where the band already reaches. Walled case unchanged at +5. |
| **A roof steps down to a wall of equal height** — so a roof abutting a wall emits the skirt it currently suppresses, restoring the sideways sweep the clean case gets. Verified firing via the dump (`W0` → `W1`, meshes then identical for those cells). | No change to the render at all, at any column. The restored skirt does not reach the sampled columns: a skirt sweeps ~3 cells along `_CastVect`, and the seam is uniform across a 12-cell roofline. |
| **Draw §15b's shade after the sun shadow** (`renderQueue = SunShadowQueue + 25`), on the theory that a cast shadow blending TOWARD the tint was lightening an already-shaded eave. | No change. The seam pixel is on the UNROOFED boundary cell, which §15b never shades, so ordering the shade cannot reach it. |

The second and third are reverted. The first was reverted on day 1 and re-confirmed here.

## What the seam actually is, most precisely

The seam pixel is the **unroofed boundary cell** immediately outside the roofline — the one vanilla's
corner-OR cover has already darkened to 45 against 73 for open floor. In the clean case the cast band
darkens it further to 36. In the walled case it stays at 45. The roof itself is 35 in both.

So: same roofline geometry, same heights, same skirts — and one case's boundary cell receives the
band while the other's does not. The only mesh difference anywhere in the window is the wall column.

## Day 2: the user's in-game finding, reproduced exactly

`Tests/Scenarios/eave_seam_remove_one_wall.json` — five identical walled rooms with a full-width
2-cell roof strip, differing only in which wall is missing:

    all four walls      … 53 | 40 [45] 35    +5   seam
    no north wall       … 53 | 40  36  35    +0   clean
    no south wall       … 53 | 40  36  35    +0   clean
    no west end         … 53 | 40  36  35    +0   clean
    ONE-CELL gap in N   … 53 | 40  36  35    +0   clean

Removing ANY wall cures it and a single cell is enough — exactly what was reported from play. So the
trigger is enclosure itself, not a particular wall.

The casters-off frames are identical across all five, and the band alone (on minus off) is:

    all four walls   -9 at ONE sample
    one-cell gap     -9 at TWO samples

Same darkening per covered sample; the enclosed case simply covers one sample less (0.14 tile against
0.29). §15b and §7b are therefore not involved at all.

## Day 2: the vertex buffers, diffed

`SeamDump.Mesh` logs the finished submesh — every vertex near a roofline with the alpha the shader
will displace it by. Diffing the seaming room against the clean one, position-aligned:

    present in the CLEAN mesh but not the seaming one:
       x=85 z=169/170/171  (alpha 0 and 255)   <- the WEST WALL junction column
       x=86 z=169/170/171
       x=96/97 …                               <- the EAST WALL junction column

    present in the seaming mesh but not the clean one:
       x=96 z=170, x=97 z=170 …

Every difference is at the room's EDGE COLUMNS, where the roof strip meets the side walls. Nothing
differs along the middle of the roofline, even though the seam runs its full length. So the missing
geometry is at the wall/roof junction and its absence is felt all the way along — consistent with a
skirt that sweeps sideways along `_CastVect`.

That is what the "roof steps down to a wall" attempt aimed at. It fired (verified in the dump) and
did not move the render, so the specific skirts it restored were not the ones that matter.

## Where to look next

`on` == `off` past the bounce says the defect is in what the mesh *covers*, not in what
any layer colours. That points at `Patch_ShadowMeshPerimeter.EmitCell` — specifically the
skirt geometry and the "buried inside a caster blob" skip — rather than at
`SectionLayer_EaveShade`, which is where four of the five attempts above went.

## Reproducers

- `roof_shadow_walled_vs_open.json` — the two cases in one frame. Start here.
- `roof_shadow_slab_edges.json` — one unwalled slab, no rooms at all: the clean control.
- `roof_shadow_strip_heights.json` — 1/2/4/8-cell bands, showing width is not the variable.
