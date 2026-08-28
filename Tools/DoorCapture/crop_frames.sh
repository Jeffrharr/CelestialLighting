#!/usr/bin/env bash
# Cut the showcase crop out of a run's raw 1920x1080 door-film frames, and renumber from 0001.
#
# Separate from the encoder on purpose. The crop is the decision that costs bytes and the one that
# gets revised -- a game frame is mostly floor, and the doorway plus the yard it lights is about a
# fifth of it -- while the encode below it is a fixed recipe. Keeping them apart means re-cutting the
# crop does not mean re-deriving a palette, and it means the crop can be eyeballed as stills before
# ninety frames are spent on it.
#
# The crop is given in RAW PIXELS of the 1920x1080 capture rather than in cells, because pixels are
# what you can measure off a still. At the film's zoom 11 one cell is ~49 px and the door sits at
# frame centre.
#
# ffmpeg rather than Pillow, for the same reason Tools/FrameDelta says so: the analysis tooling in
# this repo deliberately depends on nothing but ffmpeg and the standard library, and a capture
# pipeline that needs a Python imaging library to run is one more thing to have installed on the
# machine that re-shoots this in a year.
#
# Renumbering from 0001 is not cosmetic: --first can skip a lead-in, and ffmpeg's image2 demuxer
# will not glob a sequence that starts anywhere else without being told, so the encoder would
# silently read a shorter clip.
#
# KEEPING EVERY Nth FRAME is the last argument, and it is how playback speed is set. The take is shot
# in slow motion (see the scenario's TIME_SCALE), so the source frames are far closer together in
# game time than any GIF can play: measured at 0.565 ticks per frame, real time would be 106 fps.
# Decimating by N multiplies the game time each output frame represents, and the encoder's rate is
# then chosen to match. At N=3 a frame is 1.70 ticks = 28.3 ms of game time, so 33 fps (a 3 cs delay)
# plays at 0.94x -- near enough game speed that nothing reads as slowed, with 59 frames across the
# door's slide.
#
# Decimate here rather than in the encoder so the frame count the byte budget is made of is visible
# at the point it is decided.
#
# Usage: crop_frames.sh <reports-dir> <out-dir> <W:H:X:Y> [out-width] [first] [last] [prefix] [every]
set -euo pipefail

REPORTS="${1:?usage: crop_frames.sh <reports-dir> <out-dir> <W:H:X:Y> [out-width] [first] [last] [prefix] [every]}"
OUT="${2:?out-dir}"
CROP="${3:?crop as W:H:X:Y in source pixels}"
WIDTH="${4:-960}"
FIRST="${5:-1}"
LAST="${6:-90}"
PREFIX="${7:-doorfilm_}"
EVERY="${8:-1}"

for n in $(seq "$FIRST" "$LAST"); do
    f=$(printf '%s%04d.png' "$PREFIX" "$n")
    [ -f "$REPORTS/$f" ] || { echo "missing frame $REPORTS/$f -- the run did not finish, or the prefix is wrong" >&2; exit 1; }
done

mkdir -p "$OUT"
rm -f "$OUT"/frame_*.png

# -2 for the height keeps the crop's aspect and rounds to an even number, which a GIF does not care
# about but the mp4 sibling of this pipeline does; paying it here stops the two outputs differing by
# a pixel. lanczos because the default bicubic softens the beam's edge, which is the one gradient in
# the frame that has to survive.
ffmpeg -loglevel error -y -start_number "$FIRST" -i "$REPORTS/${PREFIX}%04d.png" \
    -frames:v $((LAST - FIRST + 1)) \
    -vf "select='not(mod(n\,${EVERY}))',crop=${CROP},scale=${WIDTH}:-2:flags=lanczos" \
    -vsync 0 -start_number 1 "$OUT/frame_%04d.png"

COUNT=$(ls "$OUT"/frame_*.png | wc -l)
DIM=$(ffprobe -v error -select_streams v:0 -show_entries stream=width,height \
      -of csv=p=0:s=x "$OUT/frame_0001.png")
echo "$COUNT frames -> $OUT at $DIM (crop $CROP, every ${EVERY} of $((LAST - FIRST + 1)))"
