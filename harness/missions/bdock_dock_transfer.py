"""Mission bdock_dock_transfer: the FIRST two-vessel Parsek autotest (B-DOCK
design sections 3 / 5).

Flies a pre-placed Station (the B2 ascent shape) to a ~110 km park and COMMITS
its tree mid-mission via the command seam (route 1), then launches the SAME
docking-variant Kerbal X again as an Interceptor into a ~90 km phasing orbit,
runs MechJeb's rendezvous autopilot to close, MechJeb's docking autopilot to hard
dock, drives two kRPC ResourceTransfers (LiquidFuel one way, MonoPropellant the
other), undocks, and terminates with two separate committed trees. It exercises
Parsek's dock / transfer / undock recording pipeline -- the cross-tree Dock branch,
the authoritative onVesselsUndocking split, and the RouteConnectionWindow whose
recorded resource deltas the offline oracle (design section 6) checks against the
commanded transfers.

Survival is the contract: any vessel-lost / frozen terminal (except the
INT-LAUNCH reload transient) is an ASSERT-FAIL loss; a rendezvous / docking /
transfer stall is a bounded give-up FLAKE, never a PARSEK-FAIL.

A THIN shell: every decision is the pure ``mlib.bdock_decide`` machine +
``mlib.evaluate_bdock_assertions``; the connect / flight / seam-bridge / result
write are the shared ``mission_runner`` runtime (KrpcMissionControl with MechJeb
and the opt-in docking telemetry). ``import krpc`` is lazy inside
``mission_runner`` (never at module top), so this module imports clean on the base
interpreter (no venv).

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

MISSION_NAME = "bdock_dock_transfer"


def build_state(params: dict):
    return mlib.bdock_initial_state(mlib.bdock_params_from_dict(params))


def decide(state, snapshot):
    return mlib.bdock_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # The terminated machine state carries the docked / transfers / undock
    # evidence the frames cannot; it rides the shared evaluate seam.
    return mlib.evaluate_bdock_assertions(
        frames, mlib.bdock_params_from_dict(params),
        phases_reached=tuple(getattr(state, "phases_reached", ()) or ()),
        state=state)


def make_control() -> mission_runner.MissionControl:
    # KRPC.MechJeb for the two ascents (AscentAutopilot) + the rendezvous /
    # docking autopilots, and the opt-in docking / rendezvous / transfer
    # telemetry (read_docking=True) -- one seam, same connection. The seam bridge
    # for the mid-mission Station commit is wired by run_mission via
    # configure_seam (route 1); the shell does not touch it.
    return mission_runner.KrpcMissionControl(
        use_mechjeb=True, client_name=MISSION_NAME, read_docking=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # RAILS warp is central to B-DOCK: the rendezvous AP warps its phasing legs
    # on rails. Physics warp cap is the same MechJeb-ascent rationale as B2/B5
    # (stock 4x ceiling, no 0.8.1 toggle); the 1x docking approach never warps.
    allow_rails_warp=True,
    max_physics_warp=4.0,
    # No settle tail (like B5): every B-DOCK assertion is machine-carried
    # evidence (evaluate discards the frames), so post-terminal reads only add
    # transient-failure surface that can flip a finished pass into a FLAKE.
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
