#!/usr/bin/env python3
"""Median per-pixel CIELAB deltaE (CIE76) between two rendered frames, plus a hue verdict.

WHY THIS EXISTS. Every visual subsystem in this mod is signed off with a number like "median
CIELAB deltaE 6.79" (DESIGN.md 20b, 20c, 21, 19b), and until now every one of those was computed by
hand and thrown away. The harness has no comparison tier -- RimWorldTestHarness issue 7 declares a
'delta' assert kind and explicitly rejects it as unimplemented -- so an A/B screenshot pair is
judged either by eye or by a script that does not survive the session. This is that script, kept.

WHY PER-PIXEL AND NOT CHANNEL MEANS. A channel mean has twice reported a subsystem as working
while it was invisible on screen (DESIGN.md 9's first two attempts, and 20b at its original 1500 K
endpoint). Means cancel: a change that lifts red on lit ground and drops it in shadow reads as
nothing. The median of the per-pixel distance is what tracks "would a player notice", and the
thresholds the mod uses are the standard ones -- under 1 imperceptible, 1-2 on close inspection,
2+ at a glance, 5+ obvious.

WHY A HUE VERDICT TOO, AND NOT ONLY A MAGNITUDE. A large deltaE that stayed on the Planckian locus
means the sky merely got warmer or cooler, which for a subsystem whose whole claim is a HUE (19c's
purple) would be a failure wearing a good number. So this also reports Duv, the signed CIE 1960
distance from the Planckian locus: positive is above the locus (green side), negative is below it
(purple/magenta side). A purple result must cross to negative; a warmer one cannot.

Depends on nothing but ffmpeg and the standard library, which is the whole reason it is written
this way -- neither numpy nor Pillow is installed on this machine.

    python3 frame_delta.py BEFORE.png AFTER.png [--stride N]

--stride subsamples every Nth pixel in each axis (default 2). It is a SUBSAMPLE, not a downscale:
averaging neighbouring pixels first would smooth the very per-pixel differences being measured,
whereas taking every Nth pixel leaves the distribution -- and therefore its median -- intact.
"""

import math
import struct
import subprocess
import sys

# ---------------------------------------------------------------------------------------------
# Decoding. ffmpeg is the one image tool present, so shell out to it for raw rgb24 rather than
# writing a PNG decoder.
# ---------------------------------------------------------------------------------------------


def decode(path):
    probe = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "v:0",
         "-show_entries", "stream=width,height", "-of", "csv=p=0:s=x", path],
        capture_output=True, text=True, check=True)
    width, height = (int(v) for v in probe.stdout.strip().split("x"))

    raw = subprocess.run(
        ["ffmpeg", "-v", "error", "-i", path, "-f", "rawvideo", "-pix_fmt", "rgb24", "-"],
        capture_output=True, check=True).stdout

    expected = width * height * 3
    if len(raw) != expected:
        raise SystemExit(f"{path}: expected {expected} bytes of rgb24, got {len(raw)}")
    return width, height, raw


# ---------------------------------------------------------------------------------------------
# Colour. sRGB -> linear is a 256-entry table because there are only 256 possible inputs; the rest
# is the textbook D65 transform.
# ---------------------------------------------------------------------------------------------

_LINEAR = [
    (v / 255.0 / 12.92) if (v / 255.0) <= 0.04045 else (((v / 255.0 + 0.055) / 1.055) ** 2.4)
    for v in range(256)
]

_EPSILON = 216.0 / 24389.0
_KAPPA_OVER_116 = 841.0 / 108.0


def _f(t):
    return t ** (1.0 / 3.0) if t > _EPSILON else _KAPPA_OVER_116 * t + 4.0 / 29.0


def to_lab(r, g, b):
    lr, lg, lb = _LINEAR[r], _LINEAR[g], _LINEAR[b]
    x = (0.4124564 * lr + 0.3575761 * lg + 0.1804375 * lb) / 0.95047
    y = 0.2126729 * lr + 0.7151522 * lg + 0.0721750 * lb
    z = (0.0193339 * lr + 0.1191920 * lg + 0.9503041 * lb) / 1.08883
    fx, fy, fz = _f(x), _f(y), _f(z)
    return 116.0 * fy - 16.0, 500.0 * (fx - fy), 200.0 * (fy - fz)


# ---------------------------------------------------------------------------------------------
# The Planckian locus, via the standard Kim et al. cubic approximation of x(T) and y(T), then
# CIE 1931 xy -> CIE 1960 uv. Sampled once into a table and searched linearly; the table is small
# and this runs on two mean colours, not on every pixel.
# ---------------------------------------------------------------------------------------------


def _planckian_xy(t):
    if t <= 4000.0:
        x = (-0.2661239e9 / t ** 3 - 0.2343589e6 / t ** 2 + 0.8776956e3 / t + 0.179910)
    else:
        x = (-3.0258469e9 / t ** 3 + 2.1070379e6 / t ** 2 + 0.2226347e3 / t + 0.240390)
    if t <= 2222.0:
        y = -1.1063814 * x ** 3 - 1.34811020 * x ** 2 + 2.18555832 * x - 0.20219683
    elif t <= 4000.0:
        y = -0.9549476 * x ** 3 - 1.37418593 * x ** 2 + 2.09137015 * x - 0.16748867
    else:
        y = 3.0817580 * x ** 3 - 5.87338670 * x ** 2 + 3.75112997 * x - 0.37001483
    return x, y


def _xy_to_uv(x, y):
    d = -2.0 * x + 12.0 * y + 3.0
    return 4.0 * x / d, 6.0 * y / d


_LOCUS = []
_t = 1667.0
while _t <= 25000.0:
    _LOCUS.append((_t,) + _xy_to_uv(*_planckian_xy(_t)))
    _t *= 1.0015


def duv(r, g, b):
    """Signed CIE 1960 distance from the Planckian locus. Negative == purple/magenta side."""
    lr, lg, lb = _LINEAR[r], _LINEAR[g], _LINEAR[b]
    X = 0.4124564 * lr + 0.3575761 * lg + 0.1804375 * lb
    Y = 0.2126729 * lr + 0.7151522 * lg + 0.0721750 * lb
    Z = 0.0193339 * lr + 0.1191920 * lg + 0.9503041 * lb
    s = X + Y + Z
    if s <= 0.0:
        return 0.0, 0.0
    u, v = _xy_to_uv(X / s, Y / s)
    best = None
    for (t, lu, lv) in _LOCUS:
        d = math.hypot(u - lu, v - lv)
        if best is None or d < best[0]:
            best = (d, t, lv)
    d, t, lv = best
    return (d if v >= lv else -d), t


VERDICTS = [
    (1.0, "imperceptible"),
    (2.0, "visible on close inspection"),
    (5.0, "visible at a glance"),
    (float("inf"), "obvious"),
]


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    stride = 2
    for a in sys.argv[1:]:
        if a.startswith("--stride"):
            stride = int(a.split("=", 1)[1]) if "=" in a else 2
    if len(args) != 2:
        raise SystemExit(__doc__)

    before_path, after_path = args
    w0, h0, before = decode(before_path)
    w1, h1, after = decode(after_path)
    if (w0, h0) != (w1, h1):
        raise SystemExit(f"frame sizes differ: {w0}x{h0} vs {w1}x{h1}")

    deltas = []
    sums_before = [0, 0, 0]
    sums_after = [0, 0, 0]
    count = 0

    for y in range(0, h0, stride):
        row = y * w0 * 3
        for x in range(0, w0, stride):
            i = row + x * 3
            br, bg, bb = before[i], before[i + 1], before[i + 2]
            ar, ag, ab = after[i], after[i + 1], after[i + 2]
            if (br, bg, bb) != (ar, ag, ab):
                l0, a0, b0 = to_lab(br, bg, bb)
                l1, a1, b1 = to_lab(ar, ag, ab)
                deltas.append(math.sqrt((l1 - l0) ** 2 + (a1 - a0) ** 2 + (b1 - b0) ** 2))
            else:
                deltas.append(0.0)
            sums_before[0] += br; sums_before[1] += bg; sums_before[2] += bb
            sums_after[0] += ar; sums_after[1] += ag; sums_after[2] += ab
            count += 1

    deltas.sort()
    median = deltas[len(deltas) // 2]
    mean = sum(deltas) / len(deltas)
    p90 = deltas[int(len(deltas) * 0.90)]
    p99 = deltas[int(len(deltas) * 0.99)]
    changed = sum(1 for d in deltas if d > 0.0) / len(deltas)

    verdict = next(name for limit, name in VERDICTS if median < limit)

    mean_before = tuple(round(s / count) for s in sums_before)
    mean_after = tuple(round(s / count) for s in sums_after)
    duv_before, t_before = duv(*mean_before)
    duv_after, t_after = duv(*mean_after)

    print(f"frames      {w0}x{h0}, sampled every {stride} px ({count} pixels)")
    print(f"before      {before_path}")
    print(f"after       {after_path}")
    print()
    print(f"median dE   {median:.2f}   <- {verdict}")
    print(f"mean dE     {mean:.2f}")
    print(f"p90 dE      {p90:.2f}")
    print(f"p99 dE      {p99:.2f}")
    print(f"changed     {changed * 100:.1f}% of sampled pixels")
    print()
    print(f"mean colour before  rgb{mean_before}  Duv {duv_before:+.5f}  (nearest {t_before:.0f} K)")
    print(f"mean colour after   rgb{mean_after}  Duv {duv_after:+.5f}  (nearest {t_after:.0f} K)")
    side = "purple/magenta side" if duv_after < 0 else "green side"
    print(f"hue verdict         the frame sits on the {side} of the Planckian locus")
    print(f"                    Duv moved {duv_after - duv_before:+.5f}")


if __name__ == "__main__":
    main()
