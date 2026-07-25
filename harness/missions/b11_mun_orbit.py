"""Mission b11_mun_orbit: LKO ascent + Mun transfer + CAPTURE burn + park +
commit-in-target-orbit terminal.

THE POINT (Parsek surface, not rocketry): every existing lunar / interplanetary
case (B5 Mun flyby, B6 Minmus flyby, B7 Duna flyby) flies THROUGH a foreign SOI
and comes back or continues. NONE of them ends its recording parked in another
body's SOI. That end-state is the new surface this mission buys: the COMMIT path
and the terminal classification for a tree whose recording closes while the
vessel is in orbit around a FOREIGN body, plus the background-recording handoff
that follows the commit.

REUSE, NOT REINVENTION: this is the LIVE-PROVEN B5 machine
(``mlib.b5_decide``) with ``captureEnabled`` on. Ascent, circularization,
target selection, the ManeuverPlanner Hohmann transfer, the autowarped TLI
burn, the dv-capped DIY course corrections, the arrival-quality re-correct and
the bounded rails/native warp policy are all byte-identical to the twenty-six
B5 flights. What is NEW is the four-phase tail (all unreachable without the
flag):

  PLAN-CAPTURE     MechJeb circularize-at-PERIAPSIS inside the Mun SOI
  CAPTURE-BURN     the NodeExecutor (autowarp EXPLICIT) flies it; done evidence
                   is a BOUND orbit (a hyperbolic approach reads a NEGATIVE
                   apoapsis, so "0 < ap <= ceiling" cannot be faked by flying past)
  PARK             throttle cut, nodes cleared, SAS + RCS held, rails dropped to
                   1x, then a HELD stable-orbit dwell (the recorded coverage)
  ORBIT-COMMIT     the mid-mission command-seam CommitTree (the B-DOCK route-1
                   reserved-id bridge) -> ORBIT-COMMITTED terminal

Leaving the Mun SOI at any point in that tail is an ASSERT-FAIL, not a success:
the free-return that B5 asserts is exactly the outcome this mission must not
have.

This is a THIN shell: every decision is the pure ``mlib`` phase machine +
``mlib.evaluate_b5_assertions``; the flight, connect, logging and result write
are the shared ``mission_runner`` runtime. ``import krpc`` is lazy inside
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

MISSION_NAME = "b11_mun_orbit"


def build_state(params: dict):
    """Build the mlib B5 phase-machine initial state from the missionParams dict."""
    return mlib.b5_initial_state(mlib.b5_params_from_dict(params))


def decide(state, snapshot):
    return mlib.b5_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # The machine state carries EVERY assertion's evidence (phases reached, the
    # min in-SOI altitude, the orbit read at PARK entry, the park-ever-stable
    # latch and the observed seam commit verdict); the frames carry none of it,
    # so they ride the shared evaluate seam unused.
    return mlib.evaluate_b5_assertions(
        frames, mlib.b5_params_from_dict(params),
        phases_reached=tuple(getattr(state, "phases_reached", ()) or ()),
        min_target_altitude=getattr(state, "min_target_altitude", None),
        state=state)


def make_control() -> mission_runner.MissionControl:
    # KRPC.MechJeb for the ascent (AscentAutopilot), the transfer half
    # (ManeuverPlanner + NodeExecutor) AND the capture (circularize-at-periapsis
    # + NodeExecutor) -- one seam, same connection.
    #
    # read_docking=True is NOT about docking here (the same opt-in the ORBITAL
    # forge uses): angular_velocity is the PARK tumble gate and the SAS / RCS
    # readbacks are its diagnosability channel. Without it angular_velocity
    # stays NaN and the park gate fails CLOSED forever, so this flag is
    # load-bearing, not cosmetic.
    #
    # read_node_executor=True (flight-1 forensics, 2026-07-24): CAPTURE-BURN
    # COMMANDED mj_execute_nodes and had NO channel that OBSERVED whether the
    # NodeExecutor engaged -- the exact commanded-vs-observed gap that produced
    # the B-DOCK docking-AP and the EVA-4 ladder-release defects. One extra RPC
    # per poll reads MechJeb NodeExecutor.Enabled; without it the field keeps its
    # -1 UNREAD sentinel and the capture supervisor grants no executor verdict
    # (fail closed).
    #
    # read_periapsis=True (B12 flight-3 forensics, 2026-07-25): TARGET-FLYBY in
    # capture mode may warp ONLY to periapsis_ut - the capture lead, read from
    # the orbit's own clock. Without the flag the field stays NaN and the
    # capture-mode flyby warp is disabled outright (1x, fail closed) rather
    # than falling back to a rails stair that knows nothing about periapsis.
    return mission_runner.KrpcMissionControl(
        use_mechjeb=True, client_name=MISSION_NAME, read_docking=True,
        read_node_executor=True, read_periapsis=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # RAILS warp is central: the machine's own bounded hops, the native
    # warp_to_ut coast legs AND both MechJeb executor autowarps (TLI and
    # capture) engage rails. Physics-warp cap is the same MechJeb-ascent
    # rationale as B2/B4/B5 (stock 4x ceiling, no KRPC.MechJeb 0.8.1 toggle).
    allow_rails_warp=True,
    max_physics_warp=4.0,
    # No settle tail (the B5/B6/B7 SF-4 contract): every assertion is
    # machine-carried evidence and evaluate discards the frames, so post-terminal
    # reads only add transient-failure surface that can flip a finished pass into
    # a FLAKE. It matters more here than in B5: the terminal frame is the frame
    # AFTER the tree was committed, and nothing good comes of polling past it.
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
