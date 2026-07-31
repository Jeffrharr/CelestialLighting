# The eave seam — investigation log

**Status: NOT FIXED.** Diagnostics only on branch `eave-seam`; no shipped behaviour has changed.
Everything below is measured, not argued. Read "Handoff" first.

---

## Handoff: what to do next

**Do not** diff the sun-shadow mesh again, and do not try another variant of "extend §15b's shade
over the boundary". Both are exhausted — see "Dead ends" below, which lists five failed fixes and the
measurement that killed each.

The one contradiction that has to be resolved first:

> Two rooms differing by ONE wall cell render bands of 3 px and 6 px. Their sun-shadow meshes are
> byte-identical. Editing the skirt triangles changes those pixels not at all.

So **the geometry we build is not what covers the seam pixels.** Until that is explained, no fix can
be aimed. The cheapest way to settle it, and the recommended next step:

1. **Tint each layer's material a distinct colour** (sun shadow, `SectionLayer_EdgeShadows`,
   `SectionLayer_EaveShade`, the lighting overlay) and shoot one frame. Whichever colour lands on the
   seam rows names the layer that owns them. One boot, no theory required.
2. If it is the sun-shadow material after all, the submesh being rendered is not the one our Prefix
   builds — check for a second `LayerSubMesh` on the same material, and check whether the section
   drawing those pixels is the one we think.
3. Only once the owning layer is known should a fix be attempted.

A fix must satisfy: **it may not double-darken the case that is already correct.** Every failure
below is that same error. Any per-cell layer needs to know whether the cast band already reaches a
cell, and that is direction-dependent, so it cannot be baked the way §15b is. That constraint is
probably the real design problem.

---

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

## Dead ends — five failed fixes

| Attempt | Killed by |
|---|---|
| Shade the casting edge only (issue #63's proposal) | Produced a dark 1-cell outline: `36 \| 28 \| 38`. §7b already darkens the thick-roof interior. |
| Eave-shade lattice, OR at corners | Clean rooflines went +0 → +6, dipping to 29. Double-multiply. |
| Eave-shade lattice, MEAN at corners | Roof's own edge row lightened 35 → 39; lip back to +7. |
| Draw §15b's shade after the sun shadow (`renderQueue`) | No change — the seam pixel is on the UNROOFED boundary cell, which §15b never touches. |
| A roof steps down to a wall of equal height | Verified firing in the dump; render unchanged at every column. Tried twice, on two different scenarios. |
| Author each skirt's near edge one cell back | Byte-identical frame. DLL verified fresh. **This is the result that retires the mesh entirely.** |

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

## Day 2, decisive: identical mesh, different render

`Tests/Scenarios/eave_seam_same_phase.json`. The earlier per-cell diff compared rooms at x=85 and
x=149 — one exactly on a 17-cell section boundary, the other straddling one — so section phase was
confounded with the wall config. This places two rooms exactly 34 cells (two sections) apart, giving
identical section and sub-cell alignment, differing only by one wall cell:

    ENCLOSED      band covers 1 sample   (-10)
    ONE-CELL GAP  band covers 2 samples  (-11, -9)

    per-cell mesh diff: ONE differing cell, (147,174) — the cell directly under the gap,
                        which emits nothing (h=0.00)

Every EMITTING cell is identical. Same heights, same neighbour heights, same skirt flags, same
alphas. Combined with the casters-off frames being identical, that means:

**The shadow mesh is not the difference.** Identical geometry renders a shorter band when the room
is enclosed. The defect is at draw time, not build time.

This retires the whole build-side search, including the vertex-position diff in the previous commit
(its apparent differences were an artifact of counting verts by position, where many cells author a
vertex at the same coordinate, and of the section-phase confound).

Draw-time candidates, none yet tested:
- `SectionLayer_SunShadows.ShouldDrawDynamic` / `GetSunShadowsViewRect` culling per section
- `subMesh.mesh.bounds` and `RefreshSubMeshBounds` — a skirt extends beyond its own section's rect
- whether the section covering the roofline is drawn at all, versus drawing a stale mesh

## Day 2: the defect at 1px, and the paradox

Same-phase rooms, one column each, 1px steps (y increases southward, roof begins at y=530):

                    y=  522 523 524 525 526 527 528 529 530
    ENCLOSED  off       54  53  51  50  48  47  45  44  35
              on        54 [42  41  40] 48  47  45  44  35    band stops; ramp resumes at 48
    GAP       off       54  53  52  51  48  47  46  45  35
              on        54 [42  41  40  39  38  37  35] 35    band runs into the roof

The resuming 48 IS the seam. Band thickness measured across every column of each roofline:

    ENCLOSED   3 px    (0.14 tile)
    GAP        7 px    (0.34 tile)

uniform along the whole roofline in both, starting at the same row.

**And the vertex buffers are byte-identical.** `SeamDump.Mesh` over both rooms:

    z=169 alpha=0 x72 / alpha=255 x72        identical
    z=170 alpha=0 x112 / alpha=255 x24       identical
    z=171 alpha=0 x72 / alpha=255 x72        identical
    z=172 alpha=0 x32 / alpha=255 x32        identical

Same positions, same displacement alphas, same counts, same frame, same `_CastVect` (a per-map
shader global). The band's screen extent is `alpha x _CastVect` projected, so with every one of those
equal the two bands cannot differ — and they differ by 2.3x.

That paradox is where this stands. Something outside the sun-shadow mesh is shortening the band in
the enclosed case, and it is not §15b, §7b, the lighting overlay, culling, mesh bounds or staleness —
each measured out. The remaining candidates are all "another layer drawing into the same pixels":

- `SectionLayer_EdgeShadows` (vanilla, `MatBases.EdgeShadow`, also at AltitudeLayer.Shadows) — an
  enclosed room has more wall edges, and its quads land in the same rows
- any layer whose output is identical with casters OFF but composes differently with them ON

Note the casters-off frames are NOT quite identical at 1px (off differs by 1 in places: 51/50/48/47
against 52/51/48/47), which the earlier 3px sampling reported as identical. Small, but it means
"everything else is identical" was overstated.

## RETRACTED: "one missing cell emission" was an analysis bug

The section below was wrong and is kept only so it is not re-derived. The vertex analysis keyed on
(section, vertex index) and took the last value seen. When a later bake produces a SHORTER mesh, the
stale high-index entries from an earlier, longer bake survive in that map and invent differences that
are not in the final mesh.

Re-run with a bake id logged per call and only the highest bake per section compared:

    FINAL-bake-only, position-aligned vertex diff:
    differing positions: 0
    total verts compared: 180 vs 180

The meshes are IDENTICAL. This agrees with the per-cell decision diff, which had said identical all
along; the vertex diff was the one that was wrong. Any future instrumentation of this mesh must tag
each bake and compare only the last one per section.

## SUPERSEDED (kept for the record): "one missing cell emission"

Position-aligned vertex diff of the same-phase pair (enclosed vs one-cell-gap, offset 34) — the
comparison that finally controls for section phase:

    x=108 z=170 a=255   enclosed 0   gap 1
    x=108 z=171 a=255   enclosed 2   gap 3
    x=109 z=170 a=  0   enclosed 2   gap 3
    x=109 z=170 a=255   enclosed 0   gap 1
    x=109 z=171 a=  0   enclosed 2   gap 3
    x=109 z=171 a=255   enclosed 2   gap 3

Decoded: the GAP case emits, from cell (109,170), a FOOTPRINT QUAD (4 verts at alpha 0) plus a WEST
SKIRT (2 verts at alpha 255) that the ENCLOSED case does not emit at all. One extra cell emission,
at the strip's west end. Everything else in both meshes is identical.

A cell emits nothing only when EmitCell's "buried inside a caster blob" check fires — every neighbour
at least as tall. So in the enclosed room that cell is buried and in the gapped one it is not, which
means one of its four neighbours differs in height. The diff also shows an extra emission at x=108,
the wall column itself.

Why a wall cell five cells south and five cells west of the removed cell changes at all is not yet
explained, and that is the open question. It is a ROOM-scoped effect reaching cells nowhere near the
wall that changed — which is exactly the known limitation DESIGN.md §15b already records for the
eave shade ("enclosing a room flips cells nowhere near the wall that closed it").

Retested on this scenario and still does nothing: the "roof steps down to a wall of equal height"
rule. It fires (verified in the dump) and the band stays 3 px against 7 px, so the skirts it restores
are not these.

## Day 2 close: what is now impossible to explain, stated plainly

Established beyond doubt, all by measurement:

- ONE room alone, at fixed coordinates, nothing else on the map: enclosed band 3 px, one-cell-gap
  band 6 px, same starting row (`eave_seam_solo_enclosed` / `eave_seam_solo_gap`).
- The run is DETERMINISTIC — the same scenario twice gives byte-identical frames.
- The two rooms' sun-shadow meshes are IDENTICAL: final-bake, position-aligned, over a +-25 cell
  window and z 166..175. 256 verts each, zero differing positions.
- The seam survives with §15b's shade AND §7b's occlusion both switched off
  (`eave_seam_layer_isolation`, arm `neither`), so neither of our layers makes it.
- The shortfall is a constant 4 px at 09/12/15/18h, always at the end adjacent to the roof.
- Editing the skirt triangles has NO effect on those pixels. Authoring each skirt's near edge one
  cell back inside the caster — which should move the alpha ramp under the roof — produced a
  byte-identical frame (verified the DLL was fresh: built 14:21:52, run 14:21:53).

That last point is the important one and it contradicts the obvious model. The vertices our builder
emits are demonstrably not what covers the seam pixels. Either something else draws there, or the
sun-shadow submesh we build is not the one being rendered at that location.

Do not spend more time diffing the mesh we build. The next instrumentation has to identify what
actually covers those pixels — e.g. by disabling layers one at a time at the DrawLayer level rather
than through feature flags, or by tinting each layer's material a distinct colour and reading which
one lands on the seam.

## Reproducers, in the order to use them

| Scenario | Use |
|---|---|
| `eave_seam_solo_enclosed` / `eave_seam_solo_gap` | **Start here.** One room, fixed coordinates, nothing else on the map, differing by one wall cell. Zoom 27 = exactly 20 px/cell so a 34-cell offset is exactly 680 px. Enclosed band 3 px, gapped 6 px. |
| `eave_seam_remove_one_wall` | Five rooms: all four walls / no N / no S / no W end / one-cell N gap. Only "all four" seams. |
| `eave_seam_layer_isolation` | Four arms toggling §15b and §7b with the roof casting. Seam survives all four. |
| `eave_seam_hours` | 09/12/15/18h. Constant 4 px shortfall at every angle. |
| `eave_seam_same_phase` | Two rooms 34 cells apart — identical section phase. Use when comparing two configs in one frame. |

`SeamDump` (feature flag `seam_dump`) logs per-cell EmitCell decisions and the finished vertex
buffer. It is diagnostic only and must be deleted before anything ships.

## Traps that have already cost time

- **`ls -td /tmp/rwth-run-*` returns a STALE directory** unless the run used `--no-teardown`. A
  teardown run deletes its own dir, so you get an earlier build's log and conclude the fix did not fire.
- **A diagnostic that restates a predicate instead of calling it** will report the OLD rule after you
  change the rule. `SeamDump` originally duplicated `casters.At(n) < height`.
- **Keying vertices by `(section, index)` and taking the last value is wrong.** A later, shorter bake
  leaves stale high-index entries behind and invents differences. Use the `SEAMBAKE` id and compare
  only the highest bake per section. This produced one entirely fictitious "finding" (retracted in
  65fe4ce).
- **Sample at 1 px, not on a 3 px grid.** The 3 px grid made a failed fix look like a partial success.
- **Rooms must be an exact pixel multiple apart** or sub-pixel offsets contaminate the comparison.
  At zoom 27 a cell is exactly 20 px.

## Where to look next

`on` == `off` past the bounce says the defect is in what the mesh *covers*, not in what
any layer colours. That points at `Patch_ShadowMeshPerimeter.EmitCell` — specifically the
skirt geometry and the "buried inside a caster blob" skip — rather than at
`SectionLayer_EaveShade`, which is where four of the five attempts above went.

## Reproducers

- `roof_shadow_walled_vs_open.json` — the two cases in one frame. Start here.
- `roof_shadow_slab_edges.json` — one unwalled slab, no rooms at all: the clean control.
- `roof_shadow_strip_heights.json` — 1/2/4/8-cell bands, showing width is not the variable.
