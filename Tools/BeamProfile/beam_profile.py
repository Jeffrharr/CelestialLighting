#!/usr/bin/env python3
"""Measures a §27 light beam in a capture: how much light it carries, and how sharp its edges are.

WHY THIS EXISTS RATHER THAN A ΔE. The repo's standing instrument is a masked median CIELAB ΔE
against a baseline, and it is the right one for "did anything change". It cannot answer the question
§27's compositions actually differ on, which is whether the light through an aperture reads as a
SHAFT or as a SMUDGE — two frames can carry the same amount of light through the same doorway and
look nothing alike. Judged by eye, the difference is obvious and the reason for it is not; judged by
ΔE, it does not appear at all.

So this takes a lateral transect across the beam at a fixed distance past the opening and reports
three numbers:

  peak       the brightest column, i.e. how bright the centre of the shaft is
  integral   the area under the profile, i.e. how much light got through the aperture in total
  rise       the 10-90% width of the beam's flank, in CELLS, i.e. how abruptly it stops

`rise` is the one that separates the compositions, and it has a floor nothing can go under: anything
composed into the lighting overlay's vertex lattice is one value per cell corner plus one per centre,
bilinearly interpolated, so its edges cannot be tighter than about a cell. Measured on the shipped
arms of room_parity.json, 2 cells past the opening:

    arm                  peak   integral   rise (cells)
    vanilla, gap        15.05      89.28           3.27
    flat beam, door      4.51       9.79           0.44
    max, door            7.54      17.70           1.70
    max, gap            11.08      36.96           3.83

Read together those say what neither says alone: the max puts 1.8x MORE light through an open door
than the flat beam does (17.70 against 9.79) and is twice as sharp as vanilla's own gap beam (1.70
against 3.27) — while the flat beam's 0.44 is a drawn triangle fan at sub-cell resolution, carrying
little light very concentrated. The eye reads the third column and calls the max formless; the second
column says it is the brighter beam of the two.

Usage:

    python3 Tools/BeamProfile/beam_profile.py --torch 705,553 --scale 15.91 \\
        --cells-below 5 <capture.png> [<capture.png> ...]

The torch pixel and the scale are properties of the capture's camera, not of this script: find the
torch as the brightest pixel near the emitter and divide the pixel distance between two known lamps
by their distance in cells. --find-torches does both for a capture with exactly two lamps.
"""

import argparse
import sys

try:
    from PIL import Image
except ImportError:  # pragma: no cover - the box either has Pillow or it does not
    sys.exit("beam_profile needs Pillow (python3 -c 'import PIL')")


def luminance(px):
    r, g, b = px[:3]
    return 0.2126 * r + 0.7152 * g + 0.0722 * b


def find_torches(im):
    """Brightest pixel in each half of the frame, for a capture holding exactly two lamps."""
    px = im.load()
    best = [(-1, 0, 0), (-1, 0, 0)]

    for y in range(im.height):
        for x in range(im.width):
            half = 0 if x < im.width // 2 else 1
            value = sum(px[x, y][:3])

            if value > best[half][0]:
                best[half] = (value, x, y)

    return (best[0][1], best[0][2]), (best[1][1], best[1][2])


def profile(im, cx, torch_y, scale, cells_below, half_cells):
    """Mean luminance per pixel column across the beam, with the local floor subtracted.

    The floor subtraction is what makes the numbers comparable between arms: the ground under the
    beam is lit by the sky and by whatever else the mod is doing, and none of that is the beam.
    """
    px = im.load()
    # A band 0.7 cells tall rather than a single row, so one noisy scanline cannot set the peak.
    y0 = int(torch_y + (cells_below - 0.35) * scale)
    y1 = int(torch_y + (cells_below + 0.35) * scale)

    columns = []

    for x in range(int(cx - half_cells * scale), int(cx + half_cells * scale)):
        values = [luminance(px[x, y]) for y in range(y0, y1)]
        columns.append(sum(values) / len(values))

    floor = min(columns)
    return [c - floor for c in columns]


def rise_width_cells(prof, scale):
    """10-90% rise width of the beam's left flank, in cells, or None if there is no beam."""
    peak = max(prof)

    # Below this the profile is the ground, and a "width" measured across noise is a number that
    # looks like a measurement and is not one.
    if peak <= 0.5:
        return None

    low, high = 0.1 * peak, 0.9 * peak
    flank = prof[:prof.index(peak)]

    if not flank:
        return None

    below = [i for i, v in enumerate(flank) if v <= low]
    above = [i for i, v in enumerate(flank) if v >= high]

    if not below or not above:
        return None

    start, end = max(below), min(above)

    # THE FLANK HAS TO ACTUALLY RISE. Where there is no beam — vanilla's side of the open door, which
    # gets nothing at all — the profile is ground noise, its "peak" is a bright speck somewhere in the
    # middle, and the last sample below 10% can sit to the RIGHT of the first sample above 90%. That
    # subtraction comes out negative: a width of -4.34 cells, printed in a results table beside real
    # ones. Caught by exactly that reading on the first run of this tool, hence the guard rather
    # than an abs().
    if end <= start:
        return None

    return (end - start) / scale


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("captures", nargs="+")
    parser.add_argument("--torch", help="x,y of the emitter in pixels")
    parser.add_argument("--scale", type=float, help="pixels per cell")
    parser.add_argument("--cells-below", type=float, default=5.0,
                        help="how far south of the emitter to take the transect, in cells")
    parser.add_argument("--half-width", type=float, default=5.0,
                        help="half the transect's width, in cells")
    parser.add_argument("--find-torches", action="store_true",
                        help="locate two lamps and derive the scale from their separation")
    parser.add_argument("--cells-apart", type=float, default=32.0,
                        help="distance between the two lamps, in cells, for --find-torches")
    args = parser.parse_args()

    print(f"{'capture':34}{'side':>7}{'peak':>9}{'integral':>10}{'rise (cells)':>15}")

    for path in args.captures:
        im = Image.open(path).convert("RGB")

        if args.find_torches:
            (wx, wy), (ex, _) = find_torches(im)
            scale = (ex - wx) / args.cells_apart
            sides = [("west", wx), ("east", ex)]
            torch_y = wy
        else:
            if not args.torch or not args.scale:
                sys.exit("give --torch x,y and --scale, or --find-torches")

            tx, ty = (int(v) for v in args.torch.split(","))
            scale, torch_y, sides = args.scale, ty, [("beam", tx)]

        name = path.rsplit("/", 1)[-1]

        for side, cx in sides:
            prof = profile(im, cx, torch_y, scale, args.cells_below, args.half_width)
            width = rise_width_cells(prof, scale)
            shown = "none" if width is None else f"{width:.2f}"
            print(f"{name[:33]:34}{side:>7}{max(prof):9.2f}{sum(prof) / scale:10.2f}{shown:>15}")


if __name__ == "__main__":
    main()
