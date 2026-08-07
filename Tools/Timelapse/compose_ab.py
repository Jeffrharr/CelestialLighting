#!/usr/bin/env python3
"""Compose a harness Timelapse feature-off/feature-on frame pair into one labelled A/B video.

WHY THIS EXISTS. A still can only *assert* that a subsystem is a narrow transient; it cannot show
it, and a reviewer is right to suspect a still was picked from a sweep. The harness Timelapse step
already films the sweep and stitches each sequence into its own video, but two separate videos are
not evidence a reader can check at a glance -- they have to be scrubbed in lockstep, which nobody
does. This pairs them frame-for-frame into a single side-by-side, burns in the clock and the sun
elevation so the reader can see *where* in the sweep they are, and marks the frames whose off/on
pair actually differs. That last mark is measured from the rendered PNGs, not asserted.

WHY IT LIVES HERE AND NOT IN THE HARNESS. The harness has no comparison tier at all (see
RimWorldTestHarness issue 7, and Tools/FrameDelta/frame_delta.py next door, which exists for the
same reason). Both are this mod's local answer until it does.

Depends on nothing but ffmpeg and the standard library -- neither numpy nor Pillow is installed on
this machine, which is also why frame_delta.py shells out to ffmpeg for pixels.

    python3 compose_ab.py REPORTS_DIR OFF_PREFIX ON_PREFIX OUT_BASENAME \
        [--from-hour H] [--step-hours H] [--fps N] [--skip-first]

Writes OUT_BASENAME.mp4 (full size, for the archive) and OUT_BASENAME.gif (downscaled -- GitHub
renders a GIF inline from a raw URL but will not give a video from one a player).
"""

import argparse
import filecmp
import os
import shutil
import subprocess
import tempfile

FONT_CANDIDATES = [
    "/usr/share/fonts/TTF/DejaVuSans.ttf",
    "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
    "/usr/share/fonts/noto/NotoSans-Regular.ttf",
]

# Sun elevation against hour, fitted through the five elevations Tests/Scenarios/purple_light.json
# already pins as live probe expectations. Quadratic rather than linear so the mild curvature over
# a third of an hour is carried instead of smeared -- and, more to the point, so the burnt-in
# number agrees with the scenario's own pinned values rather than drifting from them.
ELEVATION_SAMPLES = [
    (20.05, -3.2133),
    (20.10, -4.0512),
    (20.15, -4.8913),
    (20.20, -5.7331),
    (20.25, -6.5767),
]


def find_font():
    for path in FONT_CANDIDATES:
        if os.path.exists(path):
            return path
    raise SystemExit("no usable TTF found; add one to FONT_CANDIDATES")


def fit_quadratic(samples):
    """Least-squares quadratic through (x, y) samples, solved by Gaussian elimination.

    Hand-rolled because numpy is not available here. Three unknowns and five points, so the normal
    equations are tiny and conditioning is a non-issue over this range.
    """
    xs = [x for x, _ in samples]
    ys = [y for _, y in samples]
    power_sums = [sum(x ** k for x in xs) for k in range(5)]
    moments = [sum(y * x ** k for x, y in zip(xs, ys)) for k in range(3)]
    matrix = [[power_sums[i + j] for j in range(3)] + [moments[i]] for i in range(3)]

    for col in range(3):
        pivot = max(range(col, 3), key=lambda r: abs(matrix[r][col]))
        matrix[col], matrix[pivot] = matrix[pivot], matrix[col]
        for row in range(3):
            if row != col:
                factor = matrix[row][col] / matrix[col][col]
                for k in range(col, 4):
                    matrix[row][k] -= factor * matrix[col][k]

    return [matrix[i][3] / matrix[i][i] for i in range(3)]


def frame_paths(reports, prefix):
    """Every frame of one Timelapse sequence, in the order the expander numbered them."""
    index = 0
    out = []
    while True:
        path = os.path.join(reports, f"{prefix}_{index:04d}.png")
        if not os.path.exists(path):
            return out
        out.append(path)
        index += 1


def frames_differ(a, b):
    """Whether two rendered frames differ at all.

    Byte comparison of the encoded PNGs, which is sound here only because the harness writes them
    through one deterministic encoder in one run: identical pixels give identical files. It is the
    cheap half of the same question frame_delta.py answers expensively, and all this caller needs
    is the boolean.
    """
    return not filecmp.cmp(a, b, shallow=False)


def build_filter(font, left_label, right_label, info, status, status_colour):
    """One filtergraph turning the off frame and the on frame into a labelled 1280x412 pair.

    Deliberately free of ':' in every burnt-in string -- drawtext takes ':' as its own argument
    separator and escaping it through two layers of quoting is a reliable way to produce a caption
    that silently truncates.
    """
    def text(body, x, y, size, colour):
        return (f"drawtext=fontfile={font}:text='{body}':x={x}:y={y}"
                f":fontsize={size}:fontcolor={colour}")

    return (
        "[0:v]scale=640:360[l];[1:v]scale=640:360[r];[l][r]hstack=inputs=2[s];"
        "[s]pad=1280:412:0:52:black[p];"
        "[p]drawbox=x=639:y=52:w=2:h=360:color=white@0.30:t=fill[b];[b]"
        + text(left_label, 16, 8, 22, "white")
        + "," + text(right_label, 656, 8, 22, "white")
        + "," + text(info, 16, 34, 15, "0xB0B0B0")
        + "," + text(status, 656, 34, 15, status_colour)
    )


def compose(args):
    font = find_font()
    a, b, c = fit_quadratic(ELEVATION_SAMPLES)

    off = frame_paths(args.reports, args.off_prefix)
    on = frame_paths(args.reports, args.on_prefix)
    if not off or not on:
        raise SystemExit(f"no frames found for '{args.off_prefix}' / '{args.on_prefix}' in {args.reports}")
    if len(off) != len(on):
        raise SystemExit(f"sequences differ in length ({len(off)} vs {len(on)}) -- not a frame-for-frame pair")

    # The first screenshot of a run can still carry UI chrome that hidden-UI mode has not finished
    # tearing down, which makes frame 0 of the two sweeps differ for a reason that has nothing to do
    # with the subsystem under test. Dropping it is honest; leaving it in would put a false
    # "frames differ" mark on a frame outside the window.
    first = 1 if args.skip_first else 0

    work = tempfile.mkdtemp(prefix="compose_ab-")
    try:
        for out_index, i in enumerate(range(first, len(off))):
            hour = args.from_hour + i * args.step_hours
            elevation = a + b * hour + c * hour * hour
            differs = frames_differ(off[i], on[i])
            status = "WINDOW OPEN — frames differ" if differs \
                else "outside window — frames byte-identical"
            colour = "0xE8A0FF" if differs else "0x8A8A8A"

            subprocess.run(
                ["ffmpeg", "-v", "error", "-y", "-i", off[i], "-i", on[i],
                 "-filter_complex", build_filter(
                     font, args.left_label, args.right_label,
                     f"hour {hour:.3f}    sun elevation {elevation:.2f}°",
                     status, colour),
                 os.path.join(work, f"ab_{out_index:04d}.png")],
                check=True)

        pattern = os.path.join(work, "ab_%04d.png")

        subprocess.run(
            ["ffmpeg", "-v", "error", "-y", "-framerate", str(args.fps), "-i", pattern,
             "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "18",
             "-movflags", "+faststart", args.out + ".mp4"],
            check=True)

        # Downscaled and palette-reduced on purpose. A GIF is the only thing GitHub will animate
        # inline from a raw URL, and an inline image that takes a visible beat to load is one a
        # reviewer scrolls past. stats_mode=diff spends the palette on the pixels that actually
        # move across the sweep, which for a dusk sequence is nearly all of the useful colour.
        subprocess.run(
            ["ffmpeg", "-v", "error", "-y", "-framerate", str(args.fps), "-i", pattern,
             "-vf", f"scale={args.gif_width}:-2:flags=lanczos,split[a][b];"
                    f"[a]palettegen=max_colors={args.gif_colors}:stats_mode=diff[p];"
                    f"[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle",
             "-loop", "0", args.out + ".gif"],
            check=True)
    finally:
        shutil.rmtree(work, ignore_errors=True)

    print(f"wrote {args.out}.mp4 and {args.out}.gif "
          f"({len(off) - first} frames at {args.fps} fps)")


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("reports", help="harness reports directory holding the two frame sequences")
    p.add_argument("off_prefix", help="fileNamePrefix of the feature-off Timelapse")
    p.add_argument("on_prefix", help="fileNamePrefix of the feature-on Timelapse")
    p.add_argument("out", help="output basename; .mp4 and .gif are appended")
    p.add_argument("--from-hour", type=float, default=20.0)
    p.add_argument("--step-hours", type=float, default=0.004)
    p.add_argument("--fps", type=int, default=10)
    p.add_argument("--left-label", default="§19c OFF")
    p.add_argument("--right-label", default="§19c ON")
    p.add_argument("--gif-width", type=int, default=800)
    p.add_argument("--gif-colors", type=int, default=96)
    p.add_argument("--skip-first", action="store_true",
                   help="drop frame 0 of each sweep (un-settled UI chrome)")
    compose(p.parse_args())


if __name__ == "__main__":
    main()
