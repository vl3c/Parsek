"""Mission b2_lko_ascent: LKO ascent via KRPC.MechJeb (design M-B1 "Mission B2").

Flies the fixture save's pre-placed adequate-TWR stack to an 80 km circular orbit
with MechJeb's ``AscentAutopilot``: set the target apoapsis, enable autostage,
engage the autopilot, wait (bounded) until the apoapsis reaches target, then let
MechJeb execute the circularization node. Asserts the orbit params WITHIN
TOLERANCE (apoapsis / periapsis error, near-circular eccentricity, inclination
error) -- all DRIVER-VALIDITY telemetry assertions, never a golden orbit.

This is a THIN shell: every decision is the pure ``mlib.b2_decide`` phase machine
and ``mlib.evaluate_b2_assertions``; the flight, connect, logging, and result
write are the shared ``mission_runner`` runtime, which drives MechJeb through the
kRPC seam. ``import krpc`` is lazy inside ``mission_runner`` (never at module
top), so this module imports clean on the base interpreter (no venv).

GPLv3 (a derivative of the kRPC client; see mission_runner). ASCII only.
"""

from __future__ import annotations

import os
import sys
from typing import List, Optional

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import mission_runner  # noqa: E402
import mlib  # noqa: E402

MISSION_NAME = "b2_lko_ascent"


def build_state(params: dict):
    """Build the mlib B2 phase-machine initial state from the missionParams dict."""
    return mlib.b2_initial_state(mlib.b2_params_from_dict(params))


def decide(state, snapshot):
    return mlib.b2_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # ``state`` (the terminated machine state) is part of the shared evaluate
    # seam; B2's orbit assertions need only the frames.
    return mlib.evaluate_b2_assertions(frames, mlib.b2_params_from_dict(params))


def make_control() -> mission_runner.MissionControl:
    # KRPC.MechJeb AscentAutopilot for B2.
    return mission_runner.KrpcMissionControl(use_mechjeb=True, client_name=MISSION_NAME)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # B2 permits RAILS warp on its exo-atmospheric coast (design edge 7 /
    # guardrails: "RAILS warp only for exoatmospheric coasts"). B1 keeps the
    # default False (1x throughout).
    allow_rails_warp=True,
    # MechJeb's AscentAutopilot engages its OWN physics warp during ascent,
    # escalating to the STOCK physics-warp ceiling of 4x, and KRPC.MechJeb
    # 0.8.1 exposes no toggle for it (observed live 2026-07-20: ramping
    # 1.1-1.5x early, 3.96-4.12x later; a 2x bound flaked the real flight).
    # Bound at the stock ceiling: physics warp is legitimate gameplay Parsek
    # must record correctly, and anything above the mode's own maximum (plus
    # the ramp allowance) is an inconsistent state that still flakes.
    max_physics_warp=4.0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
