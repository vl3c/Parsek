"""Mission b14_minmus_landing: LKO ascent + Minmus transfer + CAPTURE burn +
park + POWERED DESCENT + a held landed dwell + commit-ON-THE-SURFACE terminal.

A THIN ALIAS over the body-parameterized B5 machine with ``captureEnabled`` AND
``landingEnabled`` on -- exactly the relationship b12_minmus_orbit has to
b11_mun_orbit, and b6_minmus_flyby to b5_mun_flyby. The flight is
``mlib.b5_decide`` / ``mlib.evaluate_b5_assertions`` verbatim with
targetBodyName=Minmus in the spec params: same MechJeb ascent, same
intercept-only Hohmann transfer, same dv-capped best-effort corrections, same
bounded warp policy, same capture / park tail, same untargeted MechJeb descent,
same landed dwell, same commit-on-the-surface terminal. ONLY the spec params
differ.

WHY MINMUS IS THE CHEAP SECOND CASE, AND WHY IT IS NOT REDUNDANT:
  * CHEAP: B12 flies the whole shared transfer + capture in 581 wall seconds
    against B11's 1,270, so the Minmus axis is the fast regression check for the
    shared capture machine.
  * NOT REDUNDANT for a LANDING: Minmus's surface gravity is ~0.05 g against the
    Mun's ~0.17 g, so the powered descent is a genuinely different regime for
    MechJeb's descent-speed policy (a long, slow, low-thrust settle instead of a
    short suicide burn), and the flats make an untargeted landing far more likely
    to end on level ground. The landed-stability gate is the same in both.

Kept as its own mission name (not a reused b13_mun_landing) so the client name,
the result files and the logs say which body the flight landed on; the schema is
a copy for the same reason (run.py resolves <mission>.schema.toml by name).

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

MISSION_NAME = "b14_minmus_landing"


def build_state(params: dict):
    """Build the (shared) mlib B5 phase-machine state from the missionParams."""
    return mlib.b5_initial_state(mlib.b5_params_from_dict(params))


def decide(state, snapshot):
    return mlib.b5_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    return mlib.evaluate_b5_assertions(
        frames, mlib.b5_params_from_dict(params),
        phases_reached=tuple(getattr(state, "phases_reached", ()) or ()),
        min_target_altitude=getattr(state, "min_target_altitude", None),
        state=state)


def make_control() -> mission_runner.MissionControl:
    # Same seam as B13: MechJeb for ascent / transfer / capture / DESCENT, with
    # all four opt-in channels. read_landing=True is load-bearing exactly as it
    # is on B13 -- without it LandingAutopilot.Enabled keeps its -1 UNREAD
    # sentinel (no autopilot verdict at all) and horizontal_speed keeps NaN
    # (the settled-touchdown gate fails closed forever, so landed-never-stable
    # would fire on a perfect landing).
    return mission_runner.KrpcMissionControl(
        use_mechjeb=True, client_name=MISSION_NAME, read_docking=True,
        read_node_executor=True, read_periapsis=True, read_landing=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # Same warp policy as B5/B6/B11/B12/B13: machine hops + native warp legs +
    # both NodeExecutor autowarps + MechJeb's own landing-state warps are RAILS;
    # MechJeb-ascent physics warp capped at the stock 4x ceiling.
    allow_rails_warp=True,
    max_physics_warp=4.0,
    # No settle tail (the SF-4 contract): machine-carried assertions, frames
    # discarded, and the terminal frame is the one AFTER the commit.
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
