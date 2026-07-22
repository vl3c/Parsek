"""Mission b6_minmus_flyby: LKO ascent + Minmus transfer + flyby + free-return.

A THIN ALIAS over the body-parameterized B5 machine: the flight is
``mlib.b5_decide`` / ``mlib.evaluate_b5_assertions`` verbatim with
targetBodyName=Minmus in the spec params -- same MechJeb ascent, same
intercept-only ManeuverPlanner Hohmann transfer (OperationTransfer's summary
names moons explicitly), same dv-capped best-effort course correction, same
bounded rails-warp coast/flyby hops and RETURN terminal. Only the spec params
differ: the ~46,400 km Minmus orbit means a ~9-day transfer coast (bigger coast
budget, bigger hops) and the flyby floor sits above Minmus's ~5.7 km peaks.

Kept as its own mission name (not a reused b5_mun_flyby) so the client name,
result files, and logs say which body the flight targeted; the schema is a
copy for the same reason (run.py resolves <mission>.schema.toml by name).

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

MISSION_NAME = "b6_minmus_flyby"


def build_state(params: dict):
    """Build the (shared) mlib B5 phase-machine state from the missionParams."""
    return mlib.b5_initial_state(mlib.b5_params_from_dict(params))


def decide(state, snapshot):
    return mlib.b5_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    return mlib.evaluate_b5_assertions(
        frames, mlib.b5_params_from_dict(params),
        phases_reached=tuple(getattr(state, "phases_reached", ()) or ()),
        min_target_altitude=getattr(state, "min_target_altitude", None))


def make_control() -> mission_runner.MissionControl:
    return mission_runner.KrpcMissionControl(use_mechjeb=True, client_name=MISSION_NAME)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # Same warp policy as B5: machine warp_to hops + NodeExecutor autowarp are
    # RAILS; MechJeb-ascent physics warp capped at the stock 4x ceiling.
    allow_rails_warp=True,
    max_physics_warp=4.0,
    # No settle tail (review SF-4): same rationale as B5 -- machine-carried
    # assertions, frames discarded, post-RETURN reads are pure flake surface.
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
