#!/usr/bin/env python3
"""Build the P7 motion timing golden (design-part-ghost-parity.md Appendix H.1).

`Source/Parsek.Tests/Fixtures/PartParity/motion/timing-golden.json` holds synthetic
per-frame arrays (the shape a motion sidecar carries: the real frames' offsets and pose
progress, the ghost's scout series and same-time progress, the transforms and a servo's
clocks) and, for each, the timing block `partparity.motion_timing` computes from them.
Two cells read it: `PartParityMotionRulesTests` (C#) recomputes every block with
`PartParityMotionRules.Timing` and asserts it within 1e-9, and asserts each synthetic
curve moves only its own measure; `test_partparity` asserts the committed file is
exactly what this builder writes, so a change to the math on either side reds.

The curves are piecewise linear with every knot on a sample, so each crossing is exact:
the real side is sampled every 0.25 s (T / 8 at T = 2 s), the scout every 0.05 s, both
from 0 to 6 s, and the reference motion starts at 0.5 s and runs 2.5 s.

  python harness/tools/build_motion_timing_golden.py           # print
  python harness/tools/build_motion_timing_golden.py --write   # rewrite the golden
"""

from __future__ import annotations

import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, os.path.join(REPO, "harness", "lib"))

import partparity as pp  # noqa: E402

GOLDEN = os.path.join(REPO, "Source", "Parsek.Tests", "Fixtures", "PartParity", "motion", "timing-golden.json")
SCHEMA = "parsek-part-parity-motion-golden/1"
REAL_STEP_S = 0.25
SCOUT_STEP_S = 0.05
WINDOW_S = 6.0
START_S = 0.5
DURATION_S = 2.5


def _grid(step: float) -> list:
    n = int(round(WINDOW_S / step))
    return [round(i * step, 10) for i in range(n + 1)]


def _piecewise(knots):
    """A progress curve through (u, p) knots over u in [0, 1], clamped outside."""
    def f(u: float) -> float:
        if u <= knots[0][0]:
            return knots[0][1]
        for (u0, p0), (u1, p1) in zip(knots, knots[1:]):
            if u <= u1:
                return p0 + (u - u0) * (p1 - p0) / (u1 - u0)
        return knots[-1][1]
    return f


LINEAR = _piecewise([(0.0, 0.0), (1.0, 1.0)])
# Eased: the same t02 / t10 / t50 / t90 / t98 as LINEAR, bowed in between.
EASED = _piecewise([(0.0, 0.0), (0.1, 0.1), (0.3, 0.15), (0.5, 0.5), (0.7, 0.85), (0.9, 0.9), (1.0, 1.0)])
FIRST_HALF = _piecewise([(0.0, 0.0), (0.5, 1.0), (1.0, 1.0)])
SECOND_HALF = _piecewise([(0.0, 0.0), (0.5, 0.0), (1.0, 1.0)])


def _snap(u: float) -> float:
    return 0.0 if u < 0.5 else 1.0


def _stairs(u: float) -> float:
    """A staircase: 0.2 steps at every fifth of the motion (servo samples)."""
    if u <= 0.0:
        return 0.0
    if u >= 1.0:
        return 1.0
    return min(1.0, int(u * 5.0 + 1e-9) / 5.0)


def _flat(u: float) -> float:
    return 0.0


def _at(curve, times, start=START_S, duration=DURATION_S):
    return [round(curve((t - start) / duration), 12) for t in times]


def _case(name, measures, real=LINEAR, ghost=LINEAR, ghost_start=START_S, ghost_duration=DURATION_S,
          transforms=None, servo_sweep=None):
    rs, ss = _grid(REAL_STEP_S), _grid(SCOUT_STEP_S)
    series = {
        "realS": rs,
        "realProgress": _at(real, rs),
        "scoutS": ss,
        "scoutProgress": _at(ghost, ss, ghost_start, ghost_duration),
        "ghostSameProgress": _at(ghost, rs, ghost_start, ghost_duration),
        "realClock": None,
        "ghostClock": None,
        "transforms": [],
    }
    if servo_sweep is not None:
        series["realClock"] = [round(servo_sweep * p, 12) for p in series["realProgress"]]
        series["ghostClock"] = [round(servo_sweep * p, 12) for p in series["ghostSameProgress"]]
    for path, real_curve, ghost_curve, travel_real, travel_ghost in transforms or ():
        series["transforms"].append({
            "path": path,
            "channel": "rotation",
            "travelReal": travel_real,
            "travelGhost": travel_ghost,
            "real": _at(real_curve, rs),
            "ghost": _at(ghost_curve, ss, ghost_start, ghost_duration),
        })
    return {"name": name, "moves": list(measures), "series": series, "timing": pp.motion_timing(series)}


def build() -> dict:
    one = [("model/a", LINEAR, LINEAR, 90.0, 90.0)]
    cases = [
        _case("same", [], transforms=one),
        _case("late", ["lagS", "onsetLagS", "endLagS"], ghost_start=START_S + 1.0, transforms=one),
        _case("slow", ["durationRatio", "lagS", "onsetLagS", "endLagS", "curveShapeDev", "staggerDev"],
              ghost_duration=2.0 * DURATION_S, transforms=one),
        _case("eased", ["curveShapeDev", "staggerDev"], real=EASED,
              transforms=[("model/a", EASED, LINEAR, 90.0, 90.0)]),
        _case("staged", ["staggerDev"],
              transforms=[("model/a", FIRST_HALF, LINEAR, 90.0, 90.0),
                          ("model/b", SECOND_HALF, LINEAR, 90.0, 90.0)]),
        _case("snapped", ["flag:motion-snap", "lagS", "onsetLagS", "endLagS", "durationRatio",
                          "curveShapeDev", "staggerDev"], ghost=_snap,
              transforms=[("model/a", LINEAR, _snap, 90.0, 90.0)]),
        _case("stepped", ["flag:motion-stepped", "lagS", "onsetLagS", "endLagS"], ghost=_stairs,
              transforms=[("model/a", LINEAR, _stairs, 90.0, 90.0)]),
        _case("not-modelled", ["flag:motion-not-modelled", "lagS", "onsetLagS", "endLagS", "durationRatio",
                               "curveShapeDev", "staggerDev"], ghost=_flat,
              transforms=[("model/a", LINEAR, _flat, 90.0, 90.0)]),
        _case("travel-differs", ["travelRatio", "flag:travel-differs"],
              transforms=[("model/a", LINEAR, LINEAR, 90.0, 270.0)]),
        _case("servo-late", ["lagS", "onsetLagS", "endLagS", "angleMaxDevDeg"],
              ghost_start=START_S + 0.5, transforms=one, servo_sweep=45.0),
    ]
    return {
        "schema": SCHEMA,
        "comment": "Written by harness/tools/build_motion_timing_golden.py; never hand-edited.",
        "cases": cases,
    }


def render(doc: dict) -> str:
    return json.dumps(doc, indent=1, sort_keys=False, allow_nan=False) + "\n"


def main(argv) -> int:
    text = render(build())
    if "--write" in argv:
        os.makedirs(os.path.dirname(GOLDEN), exist_ok=True)
        with open(GOLDEN, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(text)
        print("wrote %s" % GOLDEN)
    else:
        sys.stdout.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
