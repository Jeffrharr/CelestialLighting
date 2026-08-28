# DoorCapture — filming the open-door beam

How to produce the Workshop clip of §27e's doorway beam from a live harness run. Written down
because three of the four decisions in it cost a boot each to discover, and none of them is
guessable from the code.

Not to be confused with `Tools/VectorLightPreview/`, which renders the polygon offline from the pure
core. This is the effect in the game, over real ground, under the real sky, with a real door
animating.

```bash
# 1. shoot. --install matters even though this changes no shader: --mod-overlay swaps ASSEMBLIES
#    ONLY, so without it the branch runs against the main checkout's bundle.
../RimWorldTestHarness/Runner/run_test.sh \
    --mod         /home/deck/Developer/RimWorldMods/CelestialLighting \
    --mod-overlay <worktree> \
    --mod         <worktree>/TestMod \
    --install     <worktree>/1.6/AssetBundles:<main checkout>/1.6/AssetBundles \
    --no-profiler \
    <worktree>/Tests/Scenarios/vector_light_door_film.json

# 2. crop, then encode
Tools/DoorCapture/crop_frames.sh \
    ../RimWorldTestHarness/Runner/reports /tmp/doorframes 1210:680:240:180 960
Tools/DoorCapture/make_gif.sh /tmp/doorframes Tests/Screenshots/vector_light_door_beam.gif 25 256

# 3. the off/on stills, same crop, straight out of ffmpeg
for arm in off on; do
    ffmpeg -y -i ../RimWorldTestHarness/Runner/reports/doorfilm_ref_${arm}_z11.png \
        -vf "crop=1210:680:240:180,scale=960:-2:flags=lanczos" \
        Tests/Screenshots/vector_light_door_beam_${arm}.png
done
```

**Reset the persisted settings first.** `run_test.sh` does not claim
`Config/Mod_CelestialLighting_CelestialLightingSettingsMod.xml`, so the run measures whatever preset
was last selected in-game. This box was on Realistic; the clip has to be shot on the shipped
Cinematic default or it is an advertisement for a configuration. Back the file up, drop in a
shipped-default copy, shoot, restore.

## The four things that are not obvious

**1. The clock has to RUN, which rules out the obvious tool.** `TickLapse` is the harness's
instrument for anything animating on `TicksGame`, and it is wrong here: `AdvanceTicks` is a *jump*,
and a door's own `Tick()` never runs under a jump. A `TickLapse` of this scene returns ninety frames
of a door that never moves, with `door_aperture` pinned at 0 throughout, and passes. So the film is
hand-rolled `Wait`/`Screenshot` pairs under `SetTimeSpeed normal`. One step costs one frame, so each
captured frame costs **two ticks**, which is what sizes the phases in the generator.

**2. The hour is measured, and it is the whole loop.** Running the clock for the ~260 ticks the film
needs is not free: the ambient moves under you. Shot at hour 0, this clip drifted **dE 2.17** across
its ninety frames, with the far sky — nowhere near the door — brightening by as much as the lit yard.
On a loop all of that lands in the wrap, where the last frame cuts back to a first frame two L*
darker and reads as a flash. `vector_light_door_film_survey.json` holds the scene unpaused for
exactly that span at six candidate hours:

| hour | 18 | 20 | 22 | 00 | 02 | 04 |
|---|---|---|---|---|---|---|
| whole-frame dE over 260 ticks | 1.55 | 3.46 | **0.00** | 0.93 | 0.86 | 3.24 |

Hour 22 is a genuine plateau — far sky L* 8.40 at both ends — and is as dark as midnight, so it
costs nothing in contrast. Re-shot there, frame 1 against frame 90 is dE **0.00** over 99.99% of the
frame; the 0.01% that differs is the torch flame, which is not something a loop can or should hold
still.

Note what none of this was visible to: the scenario's `door_aperture` probes read 0 at both ends,
correctly, in both cuts. The defect was in the sky, and only a pixel measurement across the seam
finds it.

**3. Where the torch stands is worth a survey of its own.** The gate scenarios put it three cells
inside the door, a placement chosen so a probe cell lands somewhere useful. Filmed, that is masked
median dE 1.58 over 5.6% of the frame — real, and "visible on close inspection", which is the wrong
band for a listing image with about a second to make its point. The survey puts three doorways in
one frame with torches three, two and one cell inside, and the yard just outside each reads:

| torch distance | 3 cells | 2 cells | 1 cell | control yard |
|---|---|---|---|---|
| yard L*, near | 12.79 | 14.78 | **16.74** | 8.17 |
| yard L*, 3 cells out | 10.26 | 12.84 | **14.96** | — |

One cell is roughly double the lift of three, and the shipped clip uses it: masked median dE **2.10**,
p90 **7.77**, peak **13.22**, over **10.3%** of the frame, with the yard going L* 8.39 → 16.74. It
moves two things at once, which is why it wanted photographing rather than deriving — the emitter is
brighter at the aperture *and* the aperture subtends a far wider angle from it, so the fan goes from
a narrow shaft to most of a half-disc. Brighter but blobbier; no number decides that.

A second torch beside the first would not help. §27 composes emitters with a per-fragment **max**,
not a sum, so two lamps either side of a doorway widen the fan without raising it.

**4. Objects in the beam do not earn their place.** The survey also scatters granite chunks in the
lit yard, on the theory that a lit shape reads better at thumbnail size than a gradient does. It does
not: RimWorld's chunks are dark grey and stay dark grey, they are already legible against the night
ambient before the door opens, and they add per-frame detail to the one region the encoder is
relying on being still. Left out.

## Sizes, and why this clip is so cheap

960x540, 90 frames, 25fps, 256 colours: **432 KB**, comfortably inside Steam's 2 MB per-description-
image cap.

That number is a consequence of fix 2, not of the encoder. The *same clip shot at hour 0* came out at
1.7 MB and could not afford more than 64 colours. Ambient drift changes every pixel in every frame,
which defeats inter-frame compression outright; on a flat sky the static stretches are byte-identical
and cost almost nothing, and what is left to encode is the beam, the door leaves and the torch flame.
**So a loop that wraps cleanly and a file that fits are the same problem.** If a re-shoot comes back
unexpectedly large, check the seam before reaching for the colour count.

See `make_gif.sh` for the measured table behind `dither=none` — dithering costs 75% more bytes here
for a fractionally *worse* frame, which is the opposite of what `Tools/AuroraCapture/make_gif.sh`
concluded on smooth sky, and the two are both right about their own material.

## Committed output

- `Tests/Screenshots/vector_light_door_beam.gif` — the clip.
- `Tests/Screenshots/vector_light_door_beam_{off,on}.png` — the same framing, door fully open, with
  the three open-door flags off and on. The off arm is what vanilla delivers: nothing at all, because
  RimWorld's glow grid never learns a door opened. Committed because every dE quoted above is
  measured against that pair, and a number nobody can re-measure is worth about as much as a
  screenshot nobody looked at.
