#!/usr/bin/env bash
# Cropped door-film frames -> a looping GIF inside Steam's 2 MB per-description-image cap.
#
# Two-pass palette, as everywhere else in this repo, but with BOTH knobs set opposite to
# Tools/AuroraCapture/make_gif.sh, and each was measured on this material rather than inherited.
#
# stats_mode=diff, not full. An aurora is most of the frame changing slightly, so one palette
# weighted over the whole clip is right there -- under `diff` a steady green appears to pulse. This
# clip is the reverse: a dark yard that does not move and a beam that does. `diff` weights the
# palette towards the pixels that CHANGE, which here is exactly the effect being advertised, and it
# is also smaller (2.0 MB against 2.2 at 96 colours).
#
# dither=none, not sierra2_4a, and this one is worth stating as a number because the aurora recipe
# says the opposite for a real reason. Dithering exists to stop flat gradients banding, and a night
# panel is the classic case for it. It buys NOTHING here. Measured over the 90-frame crop, encoding
# each setting and comparing the GIF's own frame 45 back against the source frame it came from:
#
#     colours   dither        size    quantisation dE (median / p90)
#     48        none          1.3 MB  0.91 / 1.67
#     48        sierra2_4a    2.3 MB  0.98 / 1.72
#     64        none          1.7 MB  0.83 / 1.49
#     64        sierra2_4a    2.6 MB  0.92 / 1.64
#     96        none          2.0 MB  0.75 / 1.42
#     96        sierra2_4a    2.7 MB  0.86 / 1.55
#
# Dithering is 75% more bytes for a fractionally WORSE frame at every colour count. The reason is
# that RimWorld's ground is not a flat gradient -- concrete and dirt carry per-texel speckle that
# already breaks the bands an undithered palette would otherwise lay down, so the dither pattern is
# added on top of noise that was doing its job for free. Then it costs twice: a dithered flat area
# re-dithers frame to frame, which defeats the inter-frame compression the static yard exists to
# give us. Do not copy sierra2_4a in here from the aurora tooling without re-running the table --
# the aurora draws over a genuinely smooth sky and the answer there is genuinely different.
#
# Spend the saving on colours, not on dithering: 64 undithered lands at 1.7 MB with a quantisation
# error of dE 0.83, which is below the threshold at which any of it is visible at all.
#
# Usage: make_gif.sh <frames-dir> <out.gif> [fps] [max-colors]
set -euo pipefail

FRAMES="${1:?usage: make_gif.sh <frames-dir> <out.gif> [fps] [max-colors]}"
OUT="${2:?out.gif}"
FPS="${3:-25}"
COLORS="${4:-64}"

PALETTE="$(mktemp -t doorfilm-palette-XXXXXX.png)"
trap 'rm -f "$PALETTE"' EXIT

ffmpeg -loglevel error -y -framerate "$FPS" -i "$FRAMES/frame_%04d.png" \
    -vf "palettegen=max_colors=${COLORS}:stats_mode=diff" "$PALETTE"

ffmpeg -loglevel error -y -framerate "$FPS" -i "$FRAMES/frame_%04d.png" -i "$PALETTE" \
    -lavfi "paletteuse=dither=none:diff_mode=rectangle" -loop 0 "$OUT"

BYTES=$(stat -c %s "$OUT")
echo "$OUT  ($(numfmt --to=iec "$BYTES"), $(ls "$FRAMES" | wc -l) frames at ${FPS}fps, ${COLORS} colours)"
# Steam rejects above 2 MB and there is no warning until the upload fails, so say it here.
if [ "$BYTES" -gt 2097152 ]; then
    echo "OVER STEAM'S 2 MB CAP by $(numfmt --to=iec $((BYTES - 2097152))) -- cut colours, then frames." >&2
    exit 1
fi
