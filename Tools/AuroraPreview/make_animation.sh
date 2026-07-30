#!/usr/bin/env bash
# Turns AuroraPreview's frames/ directory into an animated GIF.
#
# WHY A SEPARATE SCRIPT, AND WHY A GIF. Judging §11a from stills was always the weak link: a curtain
# that folds and one that merely slides look identical in a single frame, and "it slides rigidly" is
# half of what issue #42 complains about. The previewer already dumps frames; this is the twenty lines
# that make them watchable.
#
# GIF rather than mp4 purely because gwenview — the image viewer this box actually has — animates GIFs
# inline and will not play video. Two-pass palettegen/paletteuse rather than a straight encode because
# an aurora is a large area of very slightly varying dark green, which is exactly the content GIF's
# default 216-colour web palette destroys: without a generated palette the gradients band into
# contour lines that look like structure in the field and are not.
#
# COPIED FROM THE aurora-overlay BRANCH, with one addition and no changes to the encode. That branch
# previews one field and writes frames/ -> animation.gif; this one previews two fields plus a stacked
# side-by-side, so it needs three GIFs out of three frame sets. The optional third argument names the
# set: frames_<name>/ -> <name>.gif. Omit it and the behaviour is byte-for-byte the original, which
# matters because the two branches' GIFs are meant to be watched back to back and any difference in
# how they were encoded would be a difference the viewer might read as a difference in the field.
#
# Usage: ./make_animation.sh <preview-outdir> [fps] [frame-set-name]
set -euo pipefail

OUTDIR="${1:?usage: make_animation.sh <preview-outdir> [fps] [frame-set-name]}"
FPS="${2:-12}"
NAME="${3:-}"

if [ -n "$NAME" ]; then
    FRAMES="$OUTDIR/frames_$NAME"
    GIF="$OUTDIR/$NAME.gif"
else
    FRAMES="$OUTDIR/frames"
    GIF="$OUTDIR/animation.gif"
fi

[ -d "$FRAMES" ] || { echo "no $FRAMES — run the previewer first" >&2; exit 1; }

PALETTE="$(mktemp -t aurora-palette-XXXXXX.png)"
trap 'rm -f "$PALETTE"' EXIT

# stats_mode=full weights the palette across the whole clip rather than per frame, so colours do not
# shift as the curtain drifts — a per-frame palette makes a steady green appear to pulse.
ffmpeg -loglevel error -y -framerate "$FPS" -i "$FRAMES/frame_%03d.png" \
    -vf "palettegen=max_colors=256:stats_mode=full" "$PALETTE"

# dither=sierra2_4a: the default bayer dithering lays a regular grid over flat areas, and a regular
# grid is the one artifact this effect must not have — it reads as the texture's own lattice showing
# through.
ffmpeg -loglevel error -y -framerate "$FPS" -i "$FRAMES/frame_%03d.png" -i "$PALETTE" \
    -lavfi "paletteuse=dither=sierra2_4a" "$GIF"

echo "$GIF  ($(ls "$FRAMES" | wc -l) frames at ${FPS}fps)"
