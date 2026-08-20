"""Mission b24_gilly_orbit: START ALREADY PARKED around Eve, Hohmann-transfer to
GILLY, capture + circularize into a Gilly park, hold it, and COMMIT the tree
there.

THE POINT (the Parsek surface, not the rocketry). B23 produced the suite's first
recording whose LAUNCH BODY is not Kerbin -- a Duna-park-rooted hop to Ike -- and
the V14 pair then measured what phase-lock does with it: `constraints=1`,
`method=single-orbital`, cadence exactly one moon period, residual 0. That
measurement was taken on a moon whose orbit is very nearly CIRCULAR (Ike e =
0.03, i = 0.2 deg) and whose SOI is 1,049,599 m.

THIS lane is the same shape at the OTHER end of the moon population. Gilly's
orbit is ECCENTRIC (e = 0.55) and INCLINED (12 deg), and its SOI is 126,123 m --
the smallest of any stock moon, and the smallest FRACTION of its own orbit
(SOI/SMA = 0.40%, against Ike's 32.80% and Mun's 20.25%). The recording this
mission produces is what lets the V15 loop pair ask whether a faithful,
phase-locked replay still lands inside the destination SOI when the tolerance is
an order of magnitude tighter than any subject flown so far. That is the
miniature of the Bop/Pol question in
`docs/dev/research/same-parent-reaim-jool-system.md`.

REUSE, NOT REINVENTION. This is a THIN ALIAS over `mlib.b5_decide` -- the same
shell shape as b23_ike_orbit / b11_mun_orbit / b17_duna_direct. NOTHING in mlib
was touched for this lane: `startInOrbit` is body-generic and was live-proven on
B23 flight 2 (2026-08-18_2308). The mission-specific content is entirely in the
spec's `missionParams`:

    startInOrbit = true            the ORBIT-START entry door (B23's, unchanged)
    homeBodyName = "Eve"           the SOI the transfer departs from
    targetBodyName = "Gilly"       the moon it captures at
    interplanetaryTransfer = false the MOON Hohmann path (OperationTransfer),
                                   NOT the interplanetary window planner
    captureEnabled = true          the B11/B12 orbit tail

A FIXTURE PRECONDITION THIS MISSION CANNOT ENFORCE, inherited verbatim from B23
flight 1 (2026-08-18_2242) and repeated here rather than only in the spec because
the mission is where a future re-point would happen: the save this mission is
pointed at must carry NO COMMITTED TREE FOR THIS VESSEL. A seam StartRecording on
a vessel that is a committed tree's own launch does not open a standalone tree --
the committed-restore path re-resumes that tree's recording and StartRecording
answers `already=true`, so the whole hop is appended to the OLD recording and the
product's launch body is whatever that recording's was. The mission runs
identically either way and every assertion still passes, so nothing in this file
or in `mlib` can detect it; the scenario spec owns it by pointing at the
Parsek-stripped `eve-park-kerbalx` fixture. Do NOT re-point this mission at
`eve-orbit-recorded` (the `--keep-parsek` B16 harvest it was derived from). See
docs/dev/todo-and-known-bugs.md -> SEAM-STARTRECORDING-JOINS-COMMITTED-TREE.

WHY THE MOON PATH AND NOT THE INTERPLANETARY ONE. Eve -> Gilly is a transfer
between a parking orbit and a MOON of the same parent, which is structurally the
Kerbin -> Mun case B5/B11 have flown dozens of times: `OperationTransfer` plans
the next transfer window and the NodeExecutor autowarps to it. The park's period
is ~29,800 game s and Gilly's is ~388,587, so the synodic period is ~32,275 game
s -- about 1.08 park orbits, the same "the wait IS roughly one park orbit" regime
B11's transfer budget was sized against. Nothing here needs the interplanetary
window machinery, and using it would drag in the ejection-eccentricity evidence,
which is meaningless for a transfer that never leaves Eve's SOI.

THE ONE THING GILLY MAKES HARDER, stated here because it is a MISSION property
and not a spec preference: the target is ECCENTRIC, so the intercept RADIUS is a
free variable between Gilly's periapsis (14,175 km) and apoapsis (48,825 km).
Every quantity the spec sizes against -- transfer coast, arrival v_inf, capture
dv, the SOI-entry -> periapsis coast -- is therefore a BAND rather than a number,
and the spec derives each one across the whole band instead of at the semi-major
axis. Read the B24-gilly-orbit.toml header before changing any budget.

WHAT THE MACHINE ASSERTS, unchanged from B23:

    reachedOrbit           ORBIT reached -- here, the ENTRY gate passed
    startedInHomeOrbit     the mission began from a BOUND, in-gate park around
                           the home body, so the recording it produces is rooted
                           at Eve. Carried evidence, fails CLOSED on a missing
                           stamp.
    reachedTargetSoi       the Gilly SOI was actually entered
    flybyPeriapsisFloor    the whole in-SOI stay cleared the terrain floor
    capturedInTargetOrbit  the orbit read at PARK entry is BOUND and in-window
    parkedStable           the park was held through the dwell
    treeCommitted          the seam commit answered OK at the Gilly park

Leaving the Gilly SOI anywhere in the tail is an ASSERT-FAIL, not a success.

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

MISSION_NAME = "b24_gilly_orbit"


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
    # The same three opt-in observation channels B11/B17/B23 request, each
    # failing CLOSED at its unread sentinel:
    #   read_docking       angular_velocity IS the PARK tumble gate; without it
    #                      the gate fails closed forever.
    #   read_node_executor OBSERVE that MechJeb's NodeExecutor engaged, never
    #                      infer it from having commanded it (B11 flight 1).
    #   read_periapsis     the ONLY legitimate in-SOI warp target is the orbit's
    #                      own periapsis clock (B12 flight 3). It matters LESS
    #                      here in absolute terms than at Ike -- Gilly's whole
    #                      SOI-entry -> periapsis coast is only ~300-1,400 game s
    #                      -- and MORE in relative terms, because that coast is
    #                      short enough that a single unclamped warp frame can
    #                      swallow it whole (B19 flight 4's failure mode). The
    #                      periapsis clock is what bounds the flyby warp at all.
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
    # max_physics_warp is retained at the B11/B17/B23 value even though this lane
    # flies no MechJeb ascent: the CORRECTION-BURN attitude flip runs under mild
    # PHYSICS warp (flipPhysicsWarpFactor), and that cap is what bounds it. It
    # is a CEILING, not a request -- nothing here raises physics warp above the
    # spec's flip factor.
    allow_rails_warp=True,
    max_physics_warp=4.0,
    # No settle tail (the B5/B11/B17/B23 SF-4 contract): every assertion is
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
