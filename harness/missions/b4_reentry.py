"""Mission b4_reentry: LKO ascent + deorbit + reentry + splashdown INTACT.

Flies the same fixture save as B2 (b2-lko-craft, the stock Kerbal X) through the
same MechJeb AscentAutopilot path to orbit, then deorbits with kRPC's NATIVE
AutoPilot (point retrograde, burn periapsis down), stages off the service stage
(recorded debris), coasts to the atmosphere in bounded rails-warp hops, deploys
the chutes, and SPLASHES DOWN INTACT. B4's contract REQUIRES survival: any
vessel-lost / frozen terminal in ANY phase is an ASSERT-FAIL loss (no B1-style
DOWN success terminal). Asserts reached-ORBIT phase evidence, a peak-apoapsis
floor, the final landed/splashed situation, and the OBSERVED canopy (the craft's
ParachuteState read Deployed, never merely "we sent deploy") -- all
terminal-focused DRIVER-VALIDITY assertions, never a golden trajectory and never
orbital precision post-deorbit.

This is a THIN shell: every decision is the pure ``mlib.b4_decide`` phase machine
and ``mlib.evaluate_b4_assertions``; the flight, connect, logging, and result
write are the shared ``mission_runner`` runtime. ``import krpc`` is lazy inside
``mission_runner`` (never at module top), so this module imports clean on the
base interpreter (no venv).

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

MISSION_NAME = "b4_reentry"


def build_state(params: dict):
    """Build the mlib B4 phase-machine initial state from the missionParams dict."""
    return mlib.b4_initial_state(mlib.b4_params_from_dict(params))


def decide(state, snapshot):
    return mlib.b4_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # The machine state carries the phase evidence (reachedOrbit) and the OBSERVED
    # canopy latch the frames cannot: both ride the shared evaluate seam. The
    # commanded latch rides along as detail only (armCommanded), never as the gate.
    return mlib.evaluate_b4_assertions(
        frames, mlib.b4_params_from_dict(params),
        phases_reached=tuple(getattr(state, "phases_reached", ()) or ()),
        craft_canopy_observed=bool(getattr(state, "craft_chute_full_seen", False)),
        arm_commanded=bool(getattr(state, "chute_deployed", False)),
        last_chute_state=str(getattr(state, "last_chute_state", "") or ""))


def make_control() -> mission_runner.MissionControl:
    # KRPC.MechJeb AscentAutopilot for the ascent half (same path as B2); the
    # deorbit half drives kRPC's native AutoPilot through the same seam.
    # read_chute=True is LOAD-BEARING, not diagnostic: the craftCanopyObserved
    # assertion gates on the OBSERVED ParachuteState, and without the read every
    # frame carries the "" unread sentinel and the assertion can never be met.
    return mission_runner.KrpcMissionControl(use_mechjeb=True, client_name=MISSION_NAME,
                                             read_chute=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # Same warp policy rationale as B2: RAILS warp is a PERMITTED state (the
    # exo coast, PLUS B4's own machine-issued warp_to hops engage RAILS warp),
    # and MechJeb's AscentAutopilot engages its own physics warp up to the
    # stock 4x ceiling with no KRPC.MechJeb 0.8.1 toggle (observed live
    # 2026-07-20 on B2).
    allow_rails_warp=True,
    max_physics_warp=4.0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
