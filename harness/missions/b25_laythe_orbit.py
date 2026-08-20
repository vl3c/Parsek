"""Mission b25_laythe_orbit: START ALREADY PARKED around JOOL, Hohmann-transfer
INWARD to LAYTHE, capture + circularize into a Laythe park, hold it, and COMMIT
the tree there.

THE POINT (the Parsek surface, not the rocketry). B23 produced the suite's first
recording whose LAUNCH BODY is not Kerbin (a Duna park -> Ike) and B24 produced
the first at an ECCENTRIC, tiny-SOI moon (an Eve park -> Gilly). The V14 and V15
pairs then measured the same routing at both: `constraints=1`,
`method=single-orbital`, cadence exactly one moon period, residual 0.

THIS lane adds the two things neither of those could.

  (1) IT IS THE FIRST JOOL MOON. `docs/dev/research/same-parent-reaim-jool-system.md`
      section 3.1 carries a committed table of Laythe / Vall / Tylo / Bop / Pol
      and reasons over it for the whole of sections 3.3, 3.4 and 6 -- and NOBODY
      HAS FLOWN ANY OF THOSE ROWS. Laythe is the first, and it is chosen first
      because it is the LOOSEST-tolerance row (tol = SOI/v_orb = 1,155.0 s, duty
      2.18e-2, 34x looser than Gilly's), which makes it the deliberate complement
      to B24: Gilly asked the tolerance question where a residual could not hide,
      Laythe asks the routing question where a residual cannot matter.

  (2) IT IS THE FIRST INWARD TRANSFER. Every b5 moon-path flight so far parks LOW
      and transfers UP. This park sits at 590,325,784.59 m -- 3.28x Pol's orbit,
      OUTSIDE the whole moon system -- so the ejection is RETROGRADE, the
      intercept is the transfer's PERIAPSIS, and the home-frame APOAPSIS never
      moves. That last fact is not cosmetic: it invalidates the machine's default
      burn-done evidence, which is why this lane sets `ejectionEccFloor` (see
      below) and why the mission spec carries a whole INWARD-TRANSFER AUDIT
      block. THE PERIOD ORDERING INVERTS TOO -- the park's 5,361,505 s period is
      101x Laythe's, so the synodic (53,510 s) is about one MOON period rather
      than about one park orbit, and every budget in the spec is sized on that
      inversion rather than on the inherited "the wait is one park orbit" rule.

REUSE, NOT REINVENTION. This is a THIN ALIAS over `mlib.b5_decide` -- the same
shell shape as b24_gilly_orbit / b23_ike_orbit / b11_mun_orbit. NOTHING in mlib
was touched for this lane. The mission-specific content is entirely in the spec's
`missionParams`:

    startInOrbit = true            the ORBIT-START entry door (B23's, unchanged)
    homeBodyName = "Jool"          the SOI the transfer departs from
    targetBodyName = "Laythe"      the moon it captures at
    interplanetaryTransfer = false the MOON Hohmann path (OperationTransfer),
                                   NOT the interplanetary window planner
    ejectionEccFloor = 0.55        the burn-done evidence, moved off the apoapsis
                                   floor because an INWARD burn does not raise
                                   apoapsis (the audit's item (A))
    captureEnabled = true          the B11/B12 orbit tail

TWO CONSTRAINTS THIS MISSION CANNOT ENFORCE, both owned by the scenario spec and
repeated here because the mission file is where a future re-point would happen.

  A FIXTURE PRECONDITION, inherited verbatim from B23 flight 1 (2026-08-18_2242):
  the save this mission is pointed at must carry NO COMMITTED TREE FOR THIS
  VESSEL. A seam StartRecording on a vessel that is a committed tree's own launch
  does not open a standalone tree -- the committed-restore path re-resumes that
  tree's recording and StartRecording answers `already=true`, so the whole hop is
  appended to the OLD recording and the product's launch body is whatever that
  recording's was. The mission runs identically either way and every assertion
  still passes, so nothing in this file or in `mlib` can detect it; the scenario
  spec owns it by pointing at the Parsek-stripped `jool-park-nerv` fixture. Do
  NOT re-point this mission at `jool-orbit-recorded` (the `--keep-parsek` B22
  harvest it was derived from, which carries FIVE committed recordings). See
  docs/dev/todo-and-known-bugs.md -> SEAM-STARTRECORDING-JOINS-COMMITTED-TREE.

  AND `viaBodyNames` MUST STAY UNSET. It is not a tuning knob on this lane, it is
  a defect: naming the home body makes `ejectionEccFloor` vacuous (the branch
  returns True on `snapshot.body in via_bodies` before it ever reads the
  eccentricity), and naming any other body LEGALISES a moon transit that
  `_b5_coast_bodies` should be failing loudly on a descent that crosses Pol's,
  Bop's, Tylo's and Vall's orbital shells. Pinned by
  `missions/lib/test_b25_laythe_orbit.py`.

WHY THE MOON PATH AND NOT THE INTERPLANETARY ONE. Jool -> Laythe is a transfer
between a parking orbit and a MOON of the same parent, which is structurally the
Kerbin -> Mun case B5/B11 have flown dozens of times: `OperationTransfer` plans
the next transfer window and the NodeExecutor autowarps to it. Nothing here needs
the interplanetary window machinery, and using it would drag in the
ejection-eccentricity *evidence* semantics from a different geometry and the
heliocentric phase-angle solver, neither of which describes a transfer that never
leaves Jool's SOI. (The `ejectionEccFloor` KEY is borrowed; the interplanetary
PATH is not.)

WHAT THE MACHINE ASSERTS, unchanged from B23/B24:

    reachedOrbit           ORBIT reached -- here, the ENTRY gate passed
    startedInHomeOrbit     the mission began from a BOUND, in-gate park around
                           the home body, so the recording it produces is rooted
                           at Jool. Carried evidence, fails CLOSED on a missing
                           stamp.
    reachedTargetSoi       the Laythe SOI was actually entered
    flybyPeriapsisFloor    the whole in-SOI stay cleared the 60 km floor -- which
                           on THIS lane guards an ATMOSPHERE (Laythe's tops out
                           at 50 km) rather than terrain, so a breach is not just
                           a red, it is a polluted product
    capturedInTargetOrbit  the orbit read at PARK entry is BOUND and in-window
    parkedStable           the park was held through the dwell
    treeCommitted          the seam commit answered OK at the Laythe park

Leaving the Laythe SOI anywhere in the tail is an ASSERT-FAIL, not a success.

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

MISSION_NAME = "b25_laythe_orbit"


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
    # The same three opt-in observation channels B11/B17/B23/B24 request, each
    # failing CLOSED at its unread sentinel:
    #   read_docking       angular_velocity IS the PARK tumble gate; without it
    #                      the gate fails closed forever.
    #   read_node_executor OBSERVE that MechJeb's NodeExecutor engaged, never
    #                      infer it from having commanded it (B11 flight 1).
    #                      It matters MORE here than on any prior lane: the NERV
    #                      is 60 kN on a ~11.9 t stack, so the capture burn runs
    #                      ~245 s and "did it engage?" cannot be answered by
    #                      watching for a fast apsis jump.
    #   read_periapsis     the ONLY legitimate in-SOI warp target is the orbit's
    #                      own periapsis clock (B12 flight 3). Laythe's
    #                      SOI-entry -> periapsis coast is ~2,050 game s, so the
    #                      periapsis clock is what keeps the flyby warp from
    #                      stepping over it (B19 flight 4's failure mode).
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
    # engage rails. This lane leans on it harder than any sibling -- the transfer
    # coast alone is 1,014,005 game s, ~11.7 wall DAYS at 1x.
    #
    # max_physics_warp is retained at the B11/B17/B23/B24 value even though this
    # lane flies no MechJeb ascent: the CORRECTION-BURN attitude flip runs under
    # mild PHYSICS warp (flipPhysicsWarpFactor), and that cap is what bounds it.
    # It is a CEILING, not a request -- nothing here raises physics warp above
    # the spec's flip factor.
    allow_rails_warp=True,
    max_physics_warp=4.0,
    # No settle tail (the B5/B11/B17/B23/B24 SF-4 contract): every assertion is
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
