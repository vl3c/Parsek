"""Mission bdock_second_dock: B-DOCK-2, the D18 second-dock harvest.

Boots the ``bdock-recorded`` fixture (the Station Kerbal X in orbit, the ACTIVE
vessel, its docking history already committed in two trees), launches a THIRD
Kerbal X, and flies B-DOCK's own Interceptor half against the Station: ascent,
circularize, two-step separation, rendezvous, match velocity, hard dock. The
committed docking tree already claims the Station's pid via MERGE, so the new
dock adds a SECOND claim on the same pid from a second tree - the two-link chain
(catalog S4.7 (iv)).

Then the background tail (``mlib.sdock_decide``): a stock map Switch-To click on
a pre-existing vessel in the bubble (the fixture's undocked Interceptor half)
starts a standalone switch segment for it inside the active tree, its engines are
activated while it is focused, a plain kRPC switch returns to the docked
combination, and its engines are deactivated while it is a BACKGROUND recording.
That recording is neither the tree's root lineage nor a split product of it,
which is what an honest D18 ``background-event-claims`` witness needs.

A THIN shell: every decision is the pure ``mlib.sdock_decide`` wrapper (which
delegates the whole Interceptor half to ``mlib.bdock_decide``) and
``mlib.evaluate_sdock_assertions``; the connect / flight / seam bridge / result
write are the shared ``mission_runner`` runtime. ``import krpc`` is lazy inside
``mission_runner``, so this module imports clean on the base interpreter.

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

MISSION_NAME = "bdock_second_dock"


def build_state(params: dict):
    return mlib.sdock_initial_state(mlib.sdock_params_from_dict(params))


def decide(state, snapshot):
    return mlib.sdock_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    return mlib.evaluate_sdock_assertions(
        frames, mlib.sdock_params_from_dict(params),
        phases_reached=tuple(getattr(state, "phases_reached", ()) or ()),
        state=state)


def make_control() -> mission_runner.MissionControl:
    # Same seam as bdock_dock_transfer: MechJeb for the ascent / rendezvous /
    # docking autopilots and the opt-in docking telemetry.
    return mission_runner.KrpcMissionControl(
        use_mechjeb=True, client_name=MISSION_NAME, read_docking=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # bdock_dock_transfer's warp contract: rails warp for the rendezvous phasing
    # legs, the stock 4x physics ceiling for the MechJeb ascent.
    allow_rails_warp=True,
    max_physics_warp=4.0,
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
