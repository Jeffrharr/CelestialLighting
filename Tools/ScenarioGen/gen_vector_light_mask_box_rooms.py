"""The mask's edit box on a sparse scene: twenty torches in twenty rooms, not five hundred lamps.

WHY A SECOND SCENE FOR ONE FLAG. stress_light_mask_box.json measures the edit box on the 500-lamp
colony, where 21,875 edited cells over 84 sections means nearly every section is edited edge to
edge and the box can only remove a few visits. That file establishes the box is inert on the
outputs and cheap where it cannot win. This one is the scene the flag exists for: a rebaked section
carrying one wedge, or none, where the fixed 613-point walk is most of what the section pays. It
is vector_light_perf's fixture -- twenty rooms with doorways, one torch each -- with the perf
file's Profile windows replaced by the alternating rounds the mask scenarios use.

READ IT AS PAIRS, as the colony files are read. shadow_cells_edited, saturated_samples and
saturation_skipped are the passes' input and read the same on every arm. corner_visits and
centre_visits are the walks' lengths and should fall on the on arms; corners_ms and centres_ms are
the clocks; every other clock is a control. Durations are recorded, not pinned.

    python3 Tools/ScenarioGen/gen_vector_light_mask_box_rooms.py
"""

import json
import os

import gen_stress_light_colony as colony
import stress_colony as sc

HERE = os.path.dirname(os.path.abspath(__file__))
SCEN = os.path.abspath(os.path.join(HERE, "..", "..", "Tests", "Scenarios"))
SOURCE = os.path.join(SCEN, "vector_light_perf.json")
TARGET = os.path.join(SCEN, "vector_light_mask_box_rooms.json")


def fixture_steps():
    """vector_light_perf's scene: everything up to and including its emitter-count pin."""
    with open(SOURCE) as handle:
        perf = json.load(handle)

    steps = []
    for step in perf["steps"]:
        steps.append(step)
        if step["type"] == "Probe" and step["args"]["probeName"] == "vector_light_count":
            return steps

    raise SystemExit("vector_light_perf.json no longer pins vector_light_count; nothing to build on")


def build():
    steps = fixture_steps()
    steps += sc.feature_steps(vector_lights=True, changed_dirty=False)
    steps.append(sc.step("SetFeature", featureName=colony.GATE_TOGGLE, enabled="true"))
    steps.append(sc.step("SetFeature", featureName=colony.VERTEX_TOGGLE, enabled="true"))
    steps.append(sc.step("SetTimeSpeed", speed="paused"))
    steps.append(sc.step("Wait", frames=colony.SETTLE_FRAMES))
    steps.append(sc.step("Probe", probeName="vector_light_mask_available", expectedValue=1, tolerance=0))

    for _ in range(colony.GATE_WARMUP):
        steps.append(sc.step("SetFeature", featureName=colony.INERT_TOGGLE, enabled=colony.INERT_VALUE))

    for _ in range(colony.GATE_ROUNDS):
        for enabled in ("false", "true"):
            steps.append(sc.step("SetFeature", featureName=colony.BOX_TOGGLE, enabled=enabled))
            steps.append(sc.step("AdvanceTicks", ticks=2))
            steps.append(sc.step("Wait", frames=3))
            for probe in colony.VERTEX_READS:
                steps.append(sc.record(probe))

    steps.append(sc.step("Probe", probeName="vector_light_mask_stale_polys", expectedValue=0, tolerance=0))

    return {
        "name": "vector_light_mask_box_rooms",
        "saveFile": "minimal_colony.rws",
        "description": (
            "vector_light_perf's fixture -- twenty torches walled into twenty rooms with doorways "
            "-- with the mask's vertex passes clipped to the emitters' edit box "
            "(vector_light_mask_edit_box) measured OFF and ON, alternately, four rounds in one "
            "boot, the advancing bodies on in both arms. "
            "\n\n"
            "WHY IT EXISTS. stress_light_mask_box.json measures the same flag on the 500-lamp "
            "colony, where nearly every section is edited edge to edge and the box cannot win. "
            "This is the scene the flag is for: a rebaked section carrying one wedge or none, "
            "where the fixed 613-point walk is most of what the section pays. "
            "\n\n"
            "READ IT AS PAIRS. shadow_cells_edited, saturated_samples and saturation_skipped read "
            "the same on every arm; corner_visits and centre_visits should fall on the on arms; "
            "corners_ms and centres_ms are the clocks and the stage clocks are controls. "
            "\n\n"
            "Every duration is RECORDED, not pinned. What is pinned is the scene: 23 emitters, "
            "the mask available, stale polys at zero."
        ),
        "steps": steps,
    }


if __name__ == "__main__":
    spec = build()
    with open(TARGET, "w") as handle:
        json.dump(spec, handle, indent=2)
        handle.write("\n")
    print(f"wrote {TARGET} ({len(spec['steps'])} steps)")
