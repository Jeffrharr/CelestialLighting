#!/usr/bin/env python3
"""Compare Circinus run documents across builds — one arm per run, differenced offline.

WHY THIS EXISTS. §27 phase 3 does all of its work inside a section regenerate, and measuring several
arms inside one RimWorld process does not work: the flag flips stop dirtying the map after the first
whole-map rebake, so every arm after the first records nothing while the instrumentation stays live
and healthy. The fix is a process per arm, which makes "exactly one rebake" true by construction —
and once each arm is its own run, the comparison has to happen out here rather than in the harness
report.

It also gives §27 the thing a Profile step never could: HISTORY. Circinus keeps every run it records
with a hardware calibration attached, and the harness ledger does not roll those files back, so a run
from a month ago is still there to diff a branch against.

WHAT TO READ AND IN WHAT ORDER.

  1. `calls` first, always. A row with zero calls is a window in which nothing happened, and its
     timings are of nothing happening. A Dubs window once reported §27 as three times cheaper than
     the feature-off baseline on exactly that basis — the row was absent rather than fast.
  2. `total/calls` next, not the mean per frame. Two runs do not produce the same number of
     regenerates, so a per-frame average silently mixes "cheaper per call" with "called less".
  3. `worstFrameMs` last and separately. A bake is a hitch, not a steady cost; an average over frames
     that mostly do no baking at all says almost nothing about how it feels.

Compares like with like or refuses: runs whose hardware calibration or mod list differ are reported
as such rather than differenced, because Circinus's own figures are shares of frame time on the
machine that measured them.

    python3 circinus_diff.py [--prefix celestiallighting-] [--owner joof.celestiallighting]
"""

import io
import json
import os
import sys

ROOT = os.path.expanduser(
    "~/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Circinus")


def load_runs(prefix):
    """Every recorded run whose label starts with `prefix`, newest last."""
    runs_dir = os.path.join(ROOT, "Runs")

    if not os.path.isdir(runs_dir):
        sys.exit("no Circinus data at %s — is the mod installed and has it recorded a run?" % ROOT)

    out = []

    for name in sorted(os.listdir(runs_dir)):
        if not name.endswith(".json"):
            continue

        try:
            doc = json.load(io.open(os.path.join(runs_dir, name), encoding="utf-8"))
        except ValueError:
            continue

        if str(doc.get("label", "")).startswith(prefix):
            out.append(doc)

    out.sort(key=lambda d: d.get("startedUtc", ""))
    return out


def rows_for(doc, owner):
    """Patch rows attributed to `owner`, keyed by the method they patch.

    The owner id carries a `_steam` suffix for the Workshop copy and none for a dev checkout, so
    matching is by prefix — otherwise a dev run and a subscriber run never compare, which is the one
    comparison most worth having.
    """
    found = {}

    for row in doc.get("patches") or []:
        patch = row.get("patch") or {}

        if not str(patch.get("ownerPackageId", "")).startswith(owner):
            continue

        target = patch.get("target") or {}
        key = "%s.%s" % (target.get("type", "?"), target.get("method", "?"))
        found[key] = row

    return found


def summarise(doc):
    env = doc.get("env") or {}
    hardware = env.get("hardware") or {}
    samples = doc.get("samples") or []
    worst = max((s.get("fmx") or 0.0) for s in samples) if samples else 0.0

    return {
        "label": doc.get("label", "?"),
        "started": (doc.get("startedUtc") or "?")[:19],
        "mods": env.get("modCount"),
        "modHash": env.get("modListHash"),
        "calib": hardware.get("calibrationScore"),
        "cycles": env.get("profilerCycles"),
        "active": env.get("profilingActive"),
        "worstFrameMs": worst,
        "samples": len(samples),
    }


def main():
    prefix = "celestiallighting-"
    owner = "joof.celestiallighting"
    args = sys.argv[1:]

    for i, a in enumerate(args):
        if a == "--prefix" and i + 1 < len(args):
            prefix = args[i + 1]
        if a == "--owner" and i + 1 < len(args):
            owner = args[i + 1]

    runs = load_runs(prefix)

    if not runs:
        sys.exit("no recorded runs labelled '%s*'. Run a bake scenario first." % prefix)

    print("%-34s %-19s %5s %10s %6s %8s %12s" % (
        "label", "started", "mods", "modHash", "calib", "cycles", "worstFrameMs"))

    metas = []

    for doc in runs:
        m = summarise(doc)
        metas.append(m)
        print("%-34s %-19s %5s %10s %6s %8s %12.2f" % (
            m["label"], m["started"], m["mods"], m["modHash"], m["calib"], m["cycles"],
            m["worstFrameMs"]))

    # Comparability is checked and reported rather than assumed. Circinus's figures are shares of
    # frame time on the machine that took them, so a differing calibration or mod list means the
    # rows are not the same measurement wearing two labels.
    calibs = {m["calib"] for m in metas}
    hashes = {m["modHash"] for m in metas}

    if len(calibs) > 1:
        print("\nWARNING: hardware calibration differs across these runs (%s) — do not difference"
              " them." % ", ".join(str(c) for c in sorted(calibs, key=str)))

    if len(hashes) > 1:
        print("\nNOTE: the mod list differs across these runs (%d distinct). Patch costs can move"
              " because another mod patched the same method." % len(hashes))

    idle = [m["label"] for m in metas if not m["active"]]

    if idle:
        print("\nWARNING: profiling was not active for: %s. Their timings are of nothing." %
              ", ".join(idle))

    print("\nPatch rows owned by %s*  (calls first — a zero makes the rest meaningless)" % owner)

    targets = []

    for doc in runs:
        for key in rows_for(doc, owner):
            if key not in targets:
                targets.append(key)

    if not targets:
        print("  none. Either the arm had §27 off, or Circinus never armed our patches in this run.")
        return

    for key in sorted(targets):
        print("\n  %s" % key)
        print("    %-30s %9s %11s %11s %11s" % ("run", "calls", "total ms", "us/call", "max ms"))

        for doc in runs:
            row = rows_for(doc, owner).get(key)

            if row is None:
                print("    %-30s %9s %11s %11s %11s" % (doc.get("label", "?"), "-", "-", "-", "-"))
                continue

            calls = row.get("calls") or 0
            total = row.get("totalMs") or 0.0
            per = (total * 1000.0 / calls) if calls else 0.0
            print("    %-30s %9d %11.2f %11.1f %11.3f" % (
                doc.get("label", "?"), calls, total, per, row.get("maxMs") or 0.0))


if __name__ == "__main__":
    main()
