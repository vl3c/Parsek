"""Mission b13_mun_landing: LKO ascent + Mun transfer + CAPTURE burn + park +
POWERED DESCENT + a held landed dwell + commit-ON-THE-SURFACE terminal.

THE POINT (Parsek surface, not rocketry): nothing in the suite today produces a
recording that ENDS **LANDED ON ANOTHER BODY**. B11 / B12 end parked in ORBIT
around a foreign body; B1 / B4 land on KERBIN. The surfaces this lane buys, and
nothing else reaches:

  * terminal classification ``Landed`` for a tree that closes on Mun soil;
  * SURFACE-class TrackSections OFF Kerbin -- the environment classifier's
    airless path (``Approach -> SurfaceMobile/SurfaceStationary``), which is
    unreachable on a body with an atmosphere because ``Atmospheric`` always
    classifies first below ``atmosphereDepth``;
  * landing-leg part events (3x landingLeg1-2 on the upper stage, extended by
    MechJeb below 1 km AGL);
  * the landed-vessel ghost / playback surface the committed recording carries.

REUSE, NOT REINVENTION: this is the LIVE-PROVEN B11 machine (``mlib.b5_decide``
with ``captureEnabled``) with ``landingEnabled`` added. PRELAUNCH through PARK is
BYTE-IDENTICAL to the five B11 flights -- same ascent, same ManeuverPlanner
Hohmann transfer, same autowarped TLI, same dv-capped corrections, same
periapsis-bounded flyby warp, same circularize-at-periapsis capture, same held
park dwell. What is NEW is the four-phase tail, all unreachable without the flag:

  DESCENT           MechJeb LandingAutopilot.LandUntargeted flies it down. The
                    phase is warp-PASSIVE (MechJeb's landing states own the warp)
                    and carries FOUR distinctly named give-ups plus its GAME-time
                    budget, so a dead actor can never idle to a generic reaper.
  LANDED-SETTLE     throttle cut, the autopilot released, SAS held, rails at 1x,
                    then a HELD settled dwell (the recorded surface coverage).
  SURFACE-COMMIT    the SAME route-1 mid-mission command-seam CommitTree B11/B12
                    fire, but fired while LANDED.
  SURFACE-COMMITTED terminal.

Leaving the Mun SOI at any point in that tail is an ASSERT-FAIL, and so is a
CRASH: ``landing-vessel-lost`` is its own named terminal precisely so a
lithobraked lander can never read as a timeout or as a success.

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

MISSION_NAME = "b13_mun_landing"


def build_state(params: dict):
    """Build the mlib B5 phase-machine initial state from the missionParams dict."""
    return mlib.b5_initial_state(mlib.b5_params_from_dict(params))


def decide(state, snapshot):
    return mlib.b5_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # The machine state carries EVERY assertion's evidence (phases reached, the
    # min in-SOI altitude, the orbit read at PARK entry, the park-ever-stable
    # latch, the TOUCHDOWN reading, the landed-ever-stable latch and the observed
    # seam commit verdict); the frames carry none of it, so they ride the shared
    # evaluate seam unused.
    return mlib.evaluate_b5_assertions(
        frames, mlib.b5_params_from_dict(params),
        phases_reached=tuple(getattr(state, "phases_reached", ()) or ()),
        min_target_altitude=getattr(state, "min_target_altitude", None),
        state=state)


def make_control() -> mission_runner.MissionControl:
    # KRPC.MechJeb for the ascent (AscentAutopilot), the transfer half
    # (ManeuverPlanner + NodeExecutor), the capture (circularize-at-periapsis +
    # NodeExecutor) AND the descent (LandingAutopilot) -- one seam, one
    # connection.
    #
    # The first four opt-ins are B11's, unchanged and load-bearing for the
    # inherited half: read_docking=True for the angular_velocity the PARK tumble
    # gate fails CLOSED without; read_node_executor=True for the OBSERVED
    # CAPTURE-BURN executor channel (B11 flight-1); read_periapsis=True for the
    # orbit clock the capture-mode flyby warp is bounded by (B12 flight-3).
    #
    # read_landing=True (NEW, this lane): three extra RPCs per poll --
    # LandingAutopilot.Enabled (the OBSERVED channel the DESCENT supervisor
    # gates on), LandingAutopilot.Status (diagnosability) and the surface
    # HORIZONTAL speed (the settled-touchdown conjunct that separates a landed
    # craft from one still sliding downhill). Without the flag the first field
    # keeps its -1 UNREAD sentinel -- which grants no autopilot verdict at all,
    # so the descent would run with only its altitude watchdog and budget -- and
    # horizontal_speed keeps NaN, which fails the settled gate closed forever.
    # This flag is load-bearing, not cosmetic.
    return mission_runner.KrpcMissionControl(
        use_mechjeb=True, client_name=MISSION_NAME, read_docking=True,
        read_node_executor=True, read_periapsis=True, read_landing=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # RAILS warp is central: the machine's own bounded hops, the native
    # warp_to_ut coast legs, both MechJeb executor autowarps (TLI and capture)
    # AND MechJeb's own landing-state warps all engage rails. Physics-warp cap is
    # the same MechJeb-ascent rationale as B2/B4/B5/B11 (stock 4x ceiling, no
    # KRPC.MechJeb 0.8.1 toggle).
    allow_rails_warp=True,
    max_physics_warp=4.0,
    # No settle tail (the B5/B6/B7/B11 SF-4 contract): every assertion is
    # machine-carried evidence and evaluate discards the frames, so post-terminal
    # reads only add transient-failure surface that can flip a finished pass into
    # a FLAKE. The terminal frame is the frame AFTER the tree was committed.
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
