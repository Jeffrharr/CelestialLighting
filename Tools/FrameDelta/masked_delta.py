#!/usr/bin/env python3
"""Masked median CIELAB deltaE over the pixels an effect actually TOUCHED, plus per-region L*.

WHY THIS EXISTS ALONGSIDE frame_delta.py. The plain median in frame_delta answers "would a player
notice this frame is different", which is the right question for a subsystem that repaints the
whole sky and the wrong one for a subsystem that lights a doorway. A beam through a door covers a
few percent of the frame; every unchanged pixel votes zero, so the median and even the p90 read 0.00
for an effect that is plainly visible in the crop. Issue 151 measured its own composition at 0.91%
of frame for exactly this reason and had to report a masked number to say anything at all.

So this reports BOTH, and the pair is the honest statement:

  * `changed`  -- what share of the frame moved at all. This is the effect's footprint, and a small
                  one is not a small effect, it is a bounded one.
  * `masked`   -- the median deltaE computed over ONLY the pixels that moved. This is how strong the
                  effect is where it exists.
  * `whole`    -- the plain median over every pixel, kept so the two can never be confused and so a
                  masked number cannot be quoted as if it were a whole-frame one.

REGIONS ARE READ IN L*, NOT IN CHANNELS. A region's mean lightness is what "the room got brighter"
means; a channel mean has twice reported a working subsystem as dead in this repo. Regions are given
as x,y,w,h in pixels and are printed for every frame passed, so an arm table is one invocation.

    python3 masked_delta.py --before A.png --after B.png [--stride N] [--threshold T]
    python3 masked_delta.py --regions room=760,430,120,120 --frames A.png B.png C.png

Depends on nothing but ffmpeg and the standard library -- neither numpy nor Pillow is on this box.
"""

import argparse
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from frame_delta import decode, to_lab


def delta_e(before, after, i):
    l0, a0, b0 = to_lab(before[i], before[i + 1], before[i + 2])
    l1, a1, b1 = to_lab(after[i], after[i + 1], after[i + 2])
    return math.sqrt((l1 - l0) ** 2 + (a1 - a0) ** 2 + (b1 - b0) ** 2)


def compare(before_path, after_path, stride, threshold, region=None):
    w0, h0, before = decode(before_path)
    w1, h1, after = decode(after_path)

    if (w0, h0) != (w1, h1):
        raise SystemExit(f"frame sizes differ: {w0}x{h0} vs {w1}x{h1}")

    x0, y0, x1, y1 = region if region else (0, 0, w0, h0)

    deltas = []
    touched = []

    for y in range(y0, min(y1, h0), stride):
        row = y * w0 * 3
        for x in range(x0, min(x1, w0), stride):
            i = row + x * 3

            # The identical-bytes shortcut is not just a speed-up: it is what makes `changed` mean
            # "the renderer wrote something different here" rather than "the float maths wobbled".
            if before[i] == after[i] and before[i + 1] == after[i + 1] \
                    and before[i + 2] == after[i + 2]:
                deltas.append(0.0)
                continue

            d = delta_e(before, after, i)
            deltas.append(d)

            if d >= threshold:
                touched.append(d)

    deltas.sort()
    touched.sort()

    return {
        "pixels": len(deltas),
        "whole": deltas[len(deltas) // 2] if deltas else 0.0,
        "p90": deltas[int(len(deltas) * 0.90)] if deltas else 0.0,
        "masked": touched[len(touched) // 2] if touched else 0.0,
        "masked_p90": touched[int(len(touched) * 0.90)] if touched else 0.0,
        "changed": len(touched) / len(deltas) if deltas else 0.0,
        "peak": deltas[-1] if deltas else 0.0,
    }


def region_lightness(path, regions, stride):
    w, h, raw = decode(path)
    out = {}

    for name, (x0, y0, x1, y1) in regions.items():
        total = 0.0
        count = 0

        for y in range(y0, min(y1, h), stride):
            row = y * w * 3
            for x in range(x0, min(x1, w), stride):
                i = row + x * 3
                total += to_lab(raw[i], raw[i + 1], raw[i + 2])[0]
                count += 1

        out[name] = total / count if count else 0.0

    return out


def parse_region(text):
    name, spec = text.split("=", 1)
    x, y, w, h = (int(v) for v in spec.split(","))
    return name, (x, y, x + w, y + h)


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--before")
    ap.add_argument("--after")
    ap.add_argument("--frames", nargs="*", default=[])
    ap.add_argument("--regions", nargs="*", default=[])
    ap.add_argument("--stride", type=int, default=2)
    ap.add_argument("--threshold", type=float, default=0.5,
                    help="deltaE at or above which a pixel counts as touched")
    args = ap.parse_args()

    regions = dict(parse_region(r) for r in args.regions)

    if args.before and args.after:
        whole = compare(args.before, args.after, args.stride, args.threshold)
        print(f"{os.path.basename(args.before)} -> {os.path.basename(args.after)}")
        print(f"  whole-frame median dE  {whole['whole']:.2f}   p90 {whole['p90']:.2f}")
        print(f"  masked median dE       {whole['masked']:.2f}   p90 {whole['masked_p90']:.2f}")
        print(f"  frame touched          {whole['changed'] * 100:.2f}%")
        print(f"  peak dE                {whole['peak']:.2f}")

        for name, bounds in regions.items():
            r = compare(args.before, args.after, args.stride, args.threshold, bounds)
            print(f"  region {name:<22} masked dE {r['masked']:5.2f}  "
                  f"touched {r['changed'] * 100:5.1f}%")

    for frame in args.frames:
        levels = region_lightness(frame, regions, args.stride)
        cells = "  ".join(f"{n} {v:6.2f}" for n, v in levels.items())
        print(f"{os.path.basename(frame):<34} {cells}")


if __name__ == "__main__":
    main()
