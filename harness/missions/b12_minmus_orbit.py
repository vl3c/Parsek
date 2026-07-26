"""Mission b12_minmus_orbit: LKO ascent + Minmus transfer + CAPTURE burn + park
+ commit-in-target-orbit terminal.

A THIN ALIAS over the body-parameterized B5 machine with ``captureEnabled`` on
-- exactly the relationship b6_minmus_flyby has to b5_mun_flyby. The flight is
``mlib.b5_decide`` / ``mlib.evaluate_b5_assertions`` verbatim with
targetBodyName=Minmus in the spec params: same MechJeb ascent, same
intercept-only ManeuverPlanner Hohmann transfer, same dv-capped best-effort
course corrections, same bounded warp policy, same capture / park /
commit-in-foreign-SOI tail. ONLY the spec params differ.

WHY Minmus IS THE CHEAP SECOND CASE: the ~46,400 km Minmus orbit means a ~9-day
transfer coast (bigger coast budget), and Minmus's tiny gravity well makes the
capture burn CHEAPER than the Mun's (~90-160 m/s vs ~200-300 m/s from a low
periapsis) -- but its SOI is also small and its warp altitude-limit table is the
tightest of the three bodies, which the machine's per-body clamp already handles.
Nothing about that needs a second machine.

Kept as its own mission name (not a reused b11_mun_orbit) so the client name,
the result files and the logs say which body the flight parked at; the schema is
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

MISSION_NAME = "b12_minmus_orbit"


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
    # Same seam as B11: MechJeb for ascent / transfer / capture, and
    # read_docking=True for the angular_velocity the PARK tumble gate needs
    # (it fails CLOSED on the NaN an un-opted-in read would leave).
    #
    # read_node_executor=True is the B11 flight-1 lesson (see b11_mun_orbit):
    # CAPTURE-BURN must OBSERVE that MechJeb's NodeExecutor engaged, not infer
    # it from having commanded it. Without the flag the channel stays at its -1
    # UNREAD sentinel and the capture supervisor grants no executor verdict.
    return mission_runner.KrpcMissionControl(
        use_mechjeb=True, client_name=MISSION_NAME, read_docking=True,
        read_node_executor=True, read_periapsis=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # Same warp policy as B5/B6/B11: machine hops + native warp legs + both
    # NodeExecutor autowarps are RAILS; MechJeb-ascent physics warp capped at
    # the stock 4x ceiling.
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
