"""Mission b5_mun_flyby: LKO ascent + Mun transfer + flyby + free-return.

Flies the same fixture save as B2/B4 (b2-lko-craft, the stock Kerbal X) through
the same MechJeb AscentAutopilot path to orbit, then targets the Mun, plans a
Hohmann transfer with the MechJeb ManeuverPlanner, executes the TLI burn with
the autowarping NodeExecutor, optionally refines the flyby periapsis with a
course-correction plan, coasts across the SOI boundary in bounded rails-warp
hops, flies through the Mun SOI (tracking the min altitude as flyby evidence),
and terminates when the SOI body is Kerbin again (the free-return). Survival is
the contract: any vessel-lost / frozen terminal in ANY phase is an ASSERT-FAIL
loss. Asserts reached-ORBIT + reached-target-SOI phase evidence, a flyby
periapsis floor, and the return-to-home terminal -- all driver-validity
assertions, never a golden trajectory.

Coverage intent: the transfer coast + flyby exercise Parsek's cross-SOI
recording (ExoBallistic body change kept cohesive), orbital checkpoints across
warp, and the warp-reseed seams that the known-bug taxonomy names.

This is a THIN shell: every decision is the pure ``mlib.b5_decide`` phase
machine and ``mlib.evaluate_b5_assertions``; the flight, connect, logging, and
result write are the shared ``mission_runner`` runtime. ``import krpc`` is lazy
inside ``mission_runner`` (never at module top), so this module imports clean on
the base interpreter (no venv).

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

MISSION_NAME = "b5_mun_flyby"


def build_state(params: dict):
    """Build the mlib B5 phase-machine initial state from the missionParams dict."""
    return mlib.b5_initial_state(mlib.b5_params_from_dict(params))


def decide(state, snapshot):
    return mlib.b5_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # The machine state carries the phase evidence (reachedOrbit /
    # reachedTargetSoi / returnedToHome) and the min-altitude flyby evidence the
    # frames cannot: both ride the shared evaluate seam.
    return mlib.evaluate_b5_assertions(
        frames, mlib.b5_params_from_dict(params),
        phases_reached=tuple(getattr(state, "phases_reached", ()) or ()),
        min_target_altitude=getattr(state, "min_target_altitude", None))


def make_control() -> mission_runner.MissionControl:
    # KRPC.MechJeb for the ascent (AscentAutopilot) AND the transfer half
    # (ManeuverPlanner + NodeExecutor) -- one seam, same connection.
    return mission_runner.KrpcMissionControl(use_mechjeb=True, client_name=MISSION_NAME)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # RAILS warp is central to B5: the machine's own bounded warp_to hops AND
    # MechJeb's NodeExecutor autowarp both engage rails. Physics warp cap is the
    # same MechJeb-ascent rationale as B2/B4 (stock 4x ceiling, no 0.8.1 toggle).
    allow_rails_warp=True,
    max_physics_warp=4.0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
