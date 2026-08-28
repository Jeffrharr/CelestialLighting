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

# 2. crop each arm, decimating to every 3rd frame, then encode at the matching rate. Both numbers
#    together are what makes it play at game speed — see "How fast to play it".
for arm in "doorfilm_ vector_light_door_beam" "doorvibrant_ vector_light_door_beam_vibrant"; do
    set -- $arm
    Tools/DoorCapture/crop_frames.sh \
        ../RimWorldTestHarness/Runner/reports /tmp/$1 1210:680:240:180 960 1 520 $1 3
    Tools/DoorCapture/make_gif.sh /tmp/$1 Tests/Screenshots/$2.gif 33.333 256
done

# 3. the off/on stills, same crop, straight out of ffmpeg
for arm in off on; do
    ffmpeg -y -i ../RimWorldTestHarness/Runner/reports/doorfilm_ref_${arm}_z11.png \
        -vf "crop=1210:680:240:180,scale=960:-2:flags=lanczos" \
        Tests/Screenshots/vector_light_door_beam_${arm}.png
done
```

## Two arms, one boot

The scenario films the same take twice, differing in exactly one flag:

| | `vector_light_indoor_multiply` | clip |
|---|---|---|
| shipped default | off | `vector_light_door_beam.gif` |
| "Extra vibrant indoor lighting" | on | `vector_light_door_beam_vibrant.gif` |

It is the mod's own taste option — off by default because the additive beam and the surface lift are
two *deliveries* of the same quantity and running both lifts the lit region twice. Measured on the
same frame of both arms, it is real and bounded:

| | room floor | beam, near | beam, far | unlit yard |
|---|---|---|---|---|
| default | 15.12 | 16.57 | 14.54 | 8.32 |
| vibrant | 15.54 | 17.01 | 15.22 | **8.32** |

Masked median dE **1.20**, p90 2.07, over 10.9% of the frame. The unlit yard not moving at all is the
part worth checking: the layer gates per *emitter* on the roof grid, so it reaches everything this
lamp's fan touches and nothing it does not. Note the largest lift is the **far beam** (+0.68), not the
indoor floor — a roofed lamp carries the layer out through its own doorway, which the flag's own
documentation calls out and which this scene is the strongest case of.

**THE ROOM HAS TO BE ROOFED FOR ANY OF THIS.** `PlaceThings` never roofs, so the fixture's earlier
cuts were an *outdoor* room, and the gate is asked at the emitter's cell — an unroofed fixture would
have produced two identical clips and a confident claim that the setting does nothing. The `SetRoof`
rect is exactly the room's footprint, walls included: one cell wider in any direction is an eave over
the very yard the beam is filmed falling on. Roofing also darkens the interior (indoor sky occlusion),
which is why the room reads L\* 15.1 here against 26.8 in the unroofed cuts.

`vector_light_surface_lift` is pinned **off** in both arms, because the multiply stands down while it
is on — an arm that inherited it true would report the surface lift's numbers under the vibrant flag's
name and the two clips would differ by nothing.

## The big one: shoot in slow motion, play back at speed

**A `Screenshot` step's frame is long.** Encoding a 1920x1080 PNG takes the better part of a second,
and `Verse.TickManager.TickManagerUpdate` accumulates `Time.deltaTime` and ticks the game through it.
So at normal speed a captured frame swallows twenty to fifty game ticks. A wooden door's slide is
`45 / DoorOpenSpeed` ticks — 45 — which means **the whole animation falls between two consecutive
captures.** Measured, filmed the obvious way: the doorway reads L\* 8.15 shut, 14.55 on the very next
capture, 15.22 on the one after.

No playback rate fixes that. Slowing a two-frame slide down just holds two stills for longer; the
intermediate positions were never rendered.

**`SetTimeScale` fixes it at the source.** Unity computes `Time.deltaTime` as
`min(unscaledDeltaTime, maximumDeltaTime) * timeScale`, and TickManager reads the scaled value — so
dropping `Time.timeScale` drops ticks-per-frame in exact proportion without touching the frame rate,
the render path, or anything the mod does. At **0.05** a captured frame advances about **0.565
ticks**, and the ordinary `Screenshot` loop becomes a slow-motion camera over a *continuous take*.
The step is `Source/Probes/SetTimeScaleStep.cs`, dev-only, compiled into the probe bridge.

RimWorld's own `TimeSpeed` cannot do this: its slowest non-paused setting is Normal, which *is* 60
ticks a second. `TimeSpeed` picks how many ticks run per unit of game time; `timeScale` picks how
fast that time passes, and only the second can be less than 1.

**What this replaced, and why it matters.** An earlier cut *sampled* the animation instead — stop the
door at a known phase with cheap `Wait` frames, freeze, shoot, repeat, one pass per frame. That
produces perfectly even frames **of the door** and is wrong about everything else, because a scene is
not only the thing being filmed. Every other animation gets sampled at whatever phase its own pass
happened to end on, so the torch flame strobed between unrelated frames instead of flickering. It
looked broken in a way no measurement of the door would ever show. A continuous take cannot have that
problem, because there is only one take.

**The door is granite, not wood.** Stone's `DoorOpenSpeed` stuff factor is 0.45, so the slide is
`45 / 0.45` = 100 ticks against wood's 45 — twice as many frames to film at the same scale. It is
also the slowest a plain vanilla door gets: the stat floors at 0.2, no Core stuff goes below stone,
and `unpoweredDoorOpenSpeedFactor` defaults to 1 on a door with no power comp.

**`Time.timeScale` is process-global Unity state.** No save reload restores it and the harness's
`WorldStateReset` has never heard of it, so the scenario sets it back to 1 itself as its last act
before the reference stills. A scenario that leaves it at 0.05 hands the next one a game running
twenty times slow.

## How fast to play it

**Playback speed is set in two places** — how many source frames are dropped, and the encoder's rate
— and they have to agree. The take is slow motion, so the source is far finer than any GIF can play:
at 0.565 ticks per frame, real time would be 106 fps.

Keeping every Nth frame makes each output frame worth `0.565 x N` ticks, i.e. `9.42 x N` ms of game
time, and the delay should match. GIF delays are integer centiseconds:

| every | ticks/frame | game ms | fps | delay | frames | clip | vs game speed |
|---|---|---|---|---|---|---|---|
| 2 | 1.13 | 18.8 | 50 | 2 cs | 260 | 5.2 s | 0.94x |
| **3** | **1.70** | **28.3** | **33.3** | **3 cs** | **174** | **5.2 s** | **0.94x** |
| 4 | 2.26 | 37.7 | 25 | 4 cs | 130 | 5.2 s | 0.94x |
| 6 | 3.39 | 56.5 | 16.7 | 6 cs | 87 | 5.2 s | 0.94x |

**The clip ships at every-3rd / 33.3 fps.** The 6% shortfall is the centisecond rounding and is not
perceptible; the clip runs 5.2 s against 4.9 s of game time either way. Every row is the same speed
and the same duration — N only trades smoothness against frame count, and 3 puts **59 frames across
the door's slide**, which is smooth without spending frames nobody sees.

To play it genuinely slowly, raise the delay *without* changing N (`make_gif.sh <dir> <out> 8 256`
gives 0.23x). That was tried as the shipped asset and reads as a stuck animation rather than a slow
one — the swing is under two seconds in a running colony, and stretching it advertises something
nobody will ever watch.

File size is nearly flat across the table: fewer, more-different frames compress about as well as
more, more-similar ones.

## Three things outside the harness's rollback ledger

**Pin the preset in the scenario, don't just reset the file.** `realistic_preset` is registered
`defaultEnabled: false`, so `FeatureRegistry.ResetAll` *should* leave Cinematic standing — and it
does not reliably. Two runs of this scenario differing only in phase lengths came back one Cinematic
(yard L\* 8.15) and one Realistic (2.81), which is the difference between a night floor that keeps
the yard readable and a yard that is genuinely black. The beam looks *far* more dramatic against
black, which is exactly why it must not be left to chance. The scenario now states
`SetFeature realistic_preset enabled=false` as its first step, as `snow_glare.json` does.

**Reset the persisted mod settings too.** `run_test.sh` does not claim
`Config/Mod_CelestialLighting_CelestialLightingSettingsMod.xml`, so whatever preset was last selected
in-game is where the run starts. Back the file up, drop in a shipped-default copy, shoot, restore.

**Turn autosave off in `Prefs.xml`** (`autosaveIntervalDays`, 1 → 15). `Autosaver.ticksSinceSave` is
scribed into the fixture save, so the autosave fires at the *same point every run* rather than
randomly — it is not a flake you can re-roll. It draws an "Autosaving..." box across the middle of the
frame which screenshot mode does not hide, and it landed mid-slide, where a frame cannot be dropped
without a visible jump. `Prefs.xml` *is* in the ledger, so the harness restores whatever it finds;
edit it before the run and put it back afterwards.

## The hour is measured

Even a slow-motion clip runs the clock, and ambient drift is what breaks a loop's wrap: an early cut,
captured in real time at hour 0, drifted **dE 2.17** across its frames, with the far sky — nowhere
near the door — brightening by as much as the lit yard. All of it lands in the wrap, where the last
frame cuts back to a first frame two L\* darker. `vector_light_door_film_survey.json` holds the scene
unpaused for the film's own span at six candidate hours:

| hour | 18 | 20 | 22 | 00 | 02 | 04 |
|---|---|---|---|---|---|---|
| whole-frame dE | 1.55 | 3.46 | **0.00** | 0.93 | 0.86 | 3.24 |

Hour 22 is a genuine plateau — far sky L\* 8.40 at both ends — and is as dark as midnight, so it costs
nothing in contrast. The shipped clip's first frame against its last is dE **0.00** over 99.95% of the
frame; the 0.05% that differs is the torch flame, which is not something a loop can or should hold
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
p90 **7.75**, peak **12.60**, over **10.3%** of the frame. It
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

960x540, 174 frames, 33.3 fps, 256 colours: **669 KB** default and **697 KB** vibrant, both inside
Steam's 2 MB per-description-image cap.

The static stretches are byte-identical because the sky is pinned, which is where the headroom comes
from — an early cut that drifted came out at 1.7 MB for *fewer* frames at a quarter of the colours,
because drift changes every pixel in every frame and defeats inter-frame compression outright. **So if
a re-shoot comes back unexpectedly large, check the seam before reaching for the colour count.**

See `make_gif.sh` for the measured table behind `dither=none` — dithering costs 75% more bytes here
for a fractionally *worse* frame, which is the opposite of what `Tools/AuroraCapture/make_gif.sh`
concluded on smooth sky, and the two are both right about their own material.

## Committed output

- `Tests/Screenshots/vector_light_door_beam.gif` — the clip, shipped defaults.
- `Tests/Screenshots/vector_light_door_beam_vibrant.gif` — the same take with "Extra vibrant indoor
  lighting" on.
- `Tests/Screenshots/vector_light_door_beam_{off,on}.png` — the same framing, door fully open, with
  the three open-door flags off and on. The off arm is what vanilla delivers: nothing at all, because
  RimWorld's glow grid never learns a door opened. Committed because every dE quoted above is measured
  against that pair, and a number nobody can re-measure is worth about as much as a screenshot nobody
  looked at.
