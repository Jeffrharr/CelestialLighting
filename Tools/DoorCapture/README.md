# DoorCapture — filming the open-door beam

How to produce the Workshop clip of §27e's doorway beam from a live harness run. Written down
because most of what makes it work is not guessable from the code, and several of the steps cost a
boot each to discover.

Not to be confused with `Tools/VectorLightPreview/`, which renders the polygon offline from the pure
core. This is the effect in the game, over real ground, under the real sky, with a real door
animating.

```bash
# 0. FIRST: reset the mod's persisted settings, and turn autosave off. Neither is claimed by the
#    harness's rollback ledger, and each silently ruins a run. See below.

# 1. shoot. --install matters even though this changes no shader: --mod-overlay swaps ASSEMBLIES
#    ONLY, so without it the branch runs against the main checkout's bundle.
../RimWorldTestHarness/Runner/run_test.sh \
    --mod         /home/deck/Developer/RimWorldMods/CelestialLighting \
    --mod-overlay <worktree> \
    --mod         <worktree>/TestMod \
    --install     <worktree>/1.6/AssetBundles:<main checkout>/1.6/AssetBundles \
    --no-profiler \
    <worktree>/Tests/Scenarios/vector_light_door_film.json

# 2. crop, then encode. 10 fps is a measured choice, not a default — see "How fast to play it".
Tools/DoorCapture/crop_frames.sh \
    ../RimWorldTestHarness/Runner/reports /tmp/doorframes 1210:680:240:180 960
Tools/DoorCapture/make_gif.sh /tmp/doorframes Tests/Screenshots/vector_light_door_beam.gif 10 256

# 3. the off/on stills, same crop, straight out of ffmpeg
for arm in off on; do
    ffmpeg -y -i ../RimWorldTestHarness/Runner/reports/doorfilm_ref_${arm}_z11.png \
        -vf "crop=1210:680:240:180,scale=960:-2:flags=lanczos" \
        Tests/Screenshots/vector_light_door_beam_${arm}.png
done
```

## The big one: a door cannot be filmed in real time on this harness

**A `Screenshot` step costs about fifty game ticks.** The game keeps ticking at 60/s while a
1920x1080 PNG is encoded and written, so consecutive captures are a quarter of a second of game time
apart. A wooden door's slide is `45 / DoorOpenSpeed` ticks — 45 ticks, three quarters of a second —
so **the entire animation lands inside two captures.** Measured on the granite door, filmed the
obvious way: the doorway reads L\* 8.15 shut, 14.55 on the very next capture, 15.22 on the one after.

No playback rate fixes that. Slowing a two-frame slide down just holds two stills for longer; the
intermediate positions were never photographed. The first two cuts of this clip both failed here,
and the second failed *after* switching to a slower door, which is what made the real cause obvious.

**A bare `Wait` costs about ONE tick**, because nothing is written to disk. (Measured: `Wait
frames=60` left the 100-tick granite door at `door_aperture` 0.625 — 62.5 ticks in 60 frames.) So the
clock can be positioned to roughly single-tick precision as long as no screenshot is taken while it
runs.

That is what the scenario does. Each frame of the clip is **its own pass**: shut the door, run the
clock forward exactly N ticks on cheap `Wait` frames, `SetTimeSpeed paused`, *then* shoot. The
screenshot's fifty ticks are now spent on a frozen scene, where they cost nothing. The sweep steps N
by four ticks across the 100-tick slide, so the clip is 26 separate openings of the same door, each
stopped a little later than the last.

Nothing is interpolated, reversed or repeated: every frame is a real render of a real door at a real
intermediate position. What it costs is run time — the sweep re-settles the door 58 times, so the
scenario is 559 steps and takes a few minutes.

Two consequences worth knowing:

- **The sky has to be re-pinned per frame.** Each pass burns a couple of hundred ticks of settle, so
  across the sweep the clock advances *hours*. `SetTime` jumps the calendar without ticking anything,
  so the door's own `ticksSinceOpen` survives it; it is issued while paused, immediately before every
  shot, and every frame is then photographed under an identical sky.
- **Everything else in the scene is sampled at a random phase too.** The torch flame is on its own
  cycle and lands wherever each pass leaves it, so it flickers rather than animating smoothly. On a
  torch that reads as a torch. On anything whose motion the clip was *about*, it would not.

**The door is granite, not wood.** Stone's `DoorOpenSpeed` stuff factor is 0.45, so the slide is
`45 / 0.45` = 100 ticks against wood's 45 — twice as many phases to sample at the same spacing. It is
also the slowest a plain vanilla door gets: the stat floors at 0.2, no Core stuff goes below stone,
and `unpoweredDoorOpenSpeedFactor` defaults to 1 on a door with no power comp.

## Two things outside the harness's rollback ledger

**Reset the persisted mod settings.** `run_test.sh` does not claim
`Config/Mod_CelestialLighting_CelestialLightingSettingsMod.xml`, so the run measures whatever preset
was last selected in-game. This box was on Realistic; the clip has to be shot on the shipped
Cinematic default or it is an advertisement for a configuration. Back the file up, drop in a
shipped-default copy, shoot, restore.

**Turn autosave off in `Prefs.xml`** (`autosaveIntervalDays`, 1 → 15). `Autosaver.ticksSinceSave` is
scribed into the fixture save, so the autosave fires at the *same point every run* rather than
randomly — it is not a flake you can re-roll. It draws an "Autosaving..." box across the middle of the
frame which screenshot mode does not hide, and here it landed on frame 11, mid-slide, where a frame
cannot be dropped without a visible jump. `Prefs.xml` *is* in the ledger, so the harness restores
whatever it finds; edit it before the run and put it back afterwards.

## How fast to play it

**This is a free parameter.** Because every frame is a separate frozen pass rather than a moment of
one continuous take, the frame rate carries no information — re-encode at any speed without
re-shooting. Only the *spacing* (`PHASE_STEP` in the generator) needs a new run.

The sweep samples 3.85 ticks per frame, i.e. 64 ms of game time, so the speed relative to real play
is `64 ms / frame duration`. GIF delays are integer centiseconds, which is what makes some rates
exact and others not:

| fps | delay | clip | door opens over | vs real time |
|---|---|---|---|---|
| 4 | 25 cs | 14.5 s | 6.5 s | 0.26x |
| 6.25 | 16 cs | 9.3 s | 4.2 s | 0.40x |
| **10** | **10 cs** | **5.8 s** | **2.6 s** | **0.64x** |
| 12.5 | 8 cs | 4.6 s | 2.1 s | 0.80x |

**The clip ships at 10 fps.** Quarter speed was tried first and reads as sluggish — the swing is over
in under two seconds of real play and a six-second version of it looks like a stuck animation rather
than a slow one. 10 fps keeps the slide comfortably readable while landing the loop at a length a
listing image can get away with. Past about 12.5 fps the swing starts to go by before it registers.

Avoid rates whose delay is not a whole number of centiseconds (8 fps wants 12.5 cs and gets 13,
i.e. 7.7 fps); the table above is the set worth using. File size is identical at every rate — the
frames are the same bytes, only the delays differ.

## The hour is measured

Even a mostly-paused clip runs the clock, and ambient drift is what breaks a loop's wrap: an early
cut, filmed in real time at hour 0, drifted **dE 2.17** across its frames, with the far sky — nowhere
near the door — brightening by as much as the lit yard. All of it lands in the wrap, where the last
frame cuts back to a first frame two L\* darker. `vector_light_door_film_survey.json` holds the scene
unpaused for the film's own span at six candidate hours:

| hour | 18 | 20 | 22 | 00 | 02 | 04 |
|---|---|---|---|---|---|---|
| whole-frame dE | 1.55 | 3.46 | **0.00** | 0.93 | 0.86 | 3.24 |

Hour 22 is a genuine plateau — far sky L\* 8.40 at both ends — and is as dark as midnight, so it costs
nothing in contrast. The shipped clip's first frame against its last is dE **0.00** over 99.99% of the
frame; the 0.01% that differs is the torch flame, which is not something a loop can or should hold
still.

Note what none of this was visible to: the scenario's `door_aperture` probes read 0 at both ends in
every cut, correctly. The defect was in the sky, and only a pixel diff across the seam finds it.

## Where the torch stands

The gate scenarios put it three cells inside the door, a placement chosen so a probe cell lands
somewhere useful. Filmed, that is masked median dE 1.58 over 5.6% of the frame — "visible on close
inspection", the wrong band for a listing image with about a second to make its point. The survey puts
three doorways in one frame with torches three, two and one cell inside:

| torch distance | 3 cells | 2 cells | 1 cell | control yard |
|---|---|---|---|---|
| yard L\*, near | 12.79 | 14.78 | **16.74** | 8.17 |
| yard L\*, 3 cells out | 10.26 | 12.84 | **14.96** | — |

One cell is roughly double the lift of three, and the shipped clip uses it: masked median dE **2.10**,
p90 **7.77**, peak **12.25**, over **10.3%** of the frame, with the yard going L\* 8.39 → 16.74. It
moves two things at once, which is why it wanted photographing rather than deriving — the emitter is
brighter at the aperture *and* the aperture subtends a far wider angle from it, so the fan goes from a
narrow shaft to most of a half-disc. Brighter but blobbier; no number decides that.

A second torch beside the first would not help. §27 composes emitters with a per-fragment **max**, not
a sum, so two lamps either side of a doorway widen the fan without raising it.

**Objects in the beam do not earn their place.** The survey also scatters granite chunks in the lit
yard, on the theory that a lit shape reads better at thumbnail size than a gradient does. It does not:
RimWorld's chunks are dark grey and stay dark grey, they are already legible against the night ambient
before the door opens, and they add per-frame detail to the one region the encoder relies on being
still. Left out.

## Sizes

960x540, 58 frames, 10 fps, 256 colours: **780 KB**, inside Steam's 2 MB per-description-image cap.

The static stretches are byte-identical because the sky is pinned, which is where the headroom comes
from — an early cut that drifted came out at 1.7 MB for *more* frames at a quarter of the colours,
because drift changes every pixel in every frame and defeats inter-frame compression outright. **So if
a re-shoot comes back unexpectedly large, check the seam before reaching for the colour count.**

See `make_gif.sh` for the measured table behind `dither=none` — dithering costs 75% more bytes here
for a fractionally *worse* frame, which is the opposite of what `Tools/AuroraCapture/make_gif.sh`
concluded on smooth sky, and the two are both right about their own material.

## Committed output

- `Tests/Screenshots/vector_light_door_beam.gif` — the clip.
- `Tests/Screenshots/vector_light_door_beam_{off,on}.png` — the same framing, door fully open, with
  the three open-door flags off and on. The off arm is what vanilla delivers: nothing at all, because
  RimWorld's glow grid never learns a door opened. Committed because every dE quoted above is measured
  against that pair, and a number nobody can re-measure is worth about as much as a screenshot nobody
  looked at.
