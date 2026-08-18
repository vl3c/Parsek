"""Mission b23_ike_orbit: START ALREADY PARKED around Duna, Hohmann-transfer to
IKE, capture + circularize into an Ike park, hold it, and COMMIT the tree there.

THE POINT (the Parsek surface, not the rocketry). Every recording the suite has
produced so far is rooted at KERBIN: the craft launches from the pad and the
tree's launch body is the world it left. The loop lanes read that launch body off
the recording's own first frames, so a SAME-PARENT moon transfer (Duna -> Ike)
can only be exercised as a loop subject if a recording EXISTS whose launch body
is Duna. No committed fixture carries one, and no lane could produce one, because
the shared B5 machine had exactly one door: PRELAUNCH -> MJ-ASCENT.

This mission opens the second door. The `startInOrbit` flag makes PRELAUNCH
verify that the active vessel is already parked around `homeBodyName` inside a
declared entry gate and then enter the ORBIT waypoint DIRECTLY -- no ascent, no
circularization, no ascent kickoff actions. Everything after that waypoint is the
LIVE-PROVEN B11/B12 path, byte for byte: ManeuverPlanner Hohmann transfer to the
moon, autowarped transfer burn, dv-capped DIY course corrections, cross-SOI
coast, the periapsis-clock-bounded approach, the CAPTURE burn, the held park, and
the mid-mission command-seam CommitTree that closes the tree WHILE PARKED IN THE
FOREIGN SOI.

REUSE, NOT REINVENTION. This is a THIN ALIAS over `mlib.b5_decide` -- the same
shell shape as b11_mun_orbit / b17_duna_direct. The mission-specific content is
entirely in the spec's `missionParams`:

    startInOrbit = true            the new entry door (this lane's only new flag)
    homeBodyName = "Duna"          the SOI the transfer departs from
    targetBodyName = "Ike"         the moon it captures at
    interplanetaryTransfer = false the MOON Hohmann path (OperationTransfer),
                                   NOT the interplanetary window planner
    captureEnabled = true          the B11/B12 orbit tail

WHY THE MOON PATH AND NOT THE INTERPLANETARY ONE. Duna -> Ike is a transfer
between a parking orbit and a MOON of the same parent, which is structurally the
Kerbin -> Mun case B5/B11 have flown dozens of times: `OperationTransfer` plans
the next transfer window, and for this geometry that window is never far. The
park's period is ~12,110 game s and Ike's is ~65,520, so the synodic period is
~14,850 game s -- about 1.2 park orbits, the same "the wait IS roughly one park
orbit" regime B11's transfer budget was sized against. The NodeExecutor autowarps
that wait on rails; nothing here needs the interplanetary window machinery, and
using it would drag in the ejection-eccentricity evidence, which is meaningless
for a transfer that never leaves Duna's SOI.

WHAT THE MACHINE ASSERTS, unchanged from B11 plus the one new row:

    reachedOrbit           ORBIT reached -- here, the ENTRY gate passed
    startedInHomeOrbit     NEW (startInOrbit lanes only): the mission began from
                           a BOUND, in-gate park around the home body, so the
                           recording it produces is rooted at Duna. Carried
                           evidence, fails CLOSED on a missing stamp.
    reachedTargetSoi       the Ike SOI was actually entered
    flybyPeriapsisFloor    the whole in-SOI stay cleared the terrain floor
    capturedInTargetOrbit  the orbit read at PARK entry is BOUND and in-window
    parkedStable           the park was held through the dwell
    treeCommitted          the seam commit answered OK at the Ike park

Leaving the Ike SOI anywhere in the tail is an ASSERT-FAIL, not a success.

This is a THIN shell: every decision is the pure `mlib` phase machine +
`mlib.evaluate_b5_assertions`; the flight, connect, logging and result write are
the shared `mission_runner` runtime. `import krpc` is lazy inside
`mission_runner` (never at module top), so this module imports clean on the base
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

MISSION_NAME = "b23_ike_orbit"


def build_state(params: dict):
    """Build the (shared) mlib B5 phase-machine state from the missionParams."""
    return mlib.b5_initial_state(mlib.b5_params_from_dict(params))


def decide(state, snapshot):
    return mlib.b5_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # The machine state carries EVERY assertion's evidence (phases reached, the
    # orbit-start entry stamps, the min in-SOI altitude, the orbit read at PARK
    # entry, the park-ever-stable latch and the observed seam commit verdict);
    # the frames carry none of it, so they ride the shared evaluate seam unused.
    return mlib.evaluate_b5_assertions(
        frames, mlib.b5_params_from_dict(params),
        phases_reached=tuple(getattr(state, "phases_reached", ()) or ()),
        min_target_altitude=getattr(state, "min_target_altitude", None),
        state=state)


def make_control() -> mission_runner.MissionControl:
    # MechJeb is still required even though there is NO ascent: the transfer
    # half (ManeuverPlanner + NodeExecutor) and the capture (circularize-at-
    # periapsis + NodeExecutor) are both MechJeb, one seam, same connection.
    #
    # The same three opt-in observation channels B11/B17 request, each failing
    # CLOSED at its unread sentinel:
    #   read_docking       angular_velocity IS the PARK tumble gate; without it
    #                      the gate fails closed forever.
    #   read_node_executor OBSERVE that MechJeb's NodeExecutor engaged, never
    #                      infer it from having commanded it (B11 flight 1).
    #   read_periapsis     the ONLY legitimate in-SOI warp target is the orbit's
    #                      own periapsis clock (B12 flight 3). It matters MORE
    #                      here than at the Mun: Ike's SOI is ~1,049 km and the
    #                      arrival crosses it slowly, so the entry -> periapsis
    #                      coast is the leg that must be warped rather than
    #                      1x-crawled.
    return mission_runner.KrpcMissionControl(
        use_mechjeb=True, client_name=MISSION_NAME, read_docking=True,
        read_node_executor=True, read_periapsis=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # RAILS warp is central: the machine's bounded hops, the native warp_to_ut
    # coast legs AND both MechJeb executor autowarps (transfer and capture)
    # engage rails.
    #
    # max_physics_warp is retained at the B11/B17 value even though this lane
    # flies no MechJeb ascent: the CORRECTION-BURN attitude flip runs under mild
    # PHYSICS warp (flipPhysicsWarpFactor), and that cap is what bounds it. It
    # is a CEILING, not a request -- nothing here raises physics warp above the
    # spec's flip factor.
    allow_rails_warp=True,
    max_physics_warp=4.0,
    # No settle tail (the B5/B11/B17 SF-4 contract): every assertion is
    # machine-carried evidence and evaluate discards the frames, so post-terminal
    # reads only add transient-failure surface that can flip a finished pass into
    # a FLAKE -- and the terminal frame is the frame AFTER the tree was
    # committed, which is exactly the frame not to keep polling past.
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
