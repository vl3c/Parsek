"""B30: MUN -> MINMUS, the SECOND moon-to-moon transfer, and a REPLICATION.

THE POINT, and it is not rocketry. `B26-laythe-vall-transfer` flew the suite's
first moon-to-moon hop and `V17M`/`V17T` read its routing: Parsek NEITHER
re-aims NOR phase-locks a moon-to-moon recording, it replays it faithfully on
the raw cadence (H3). That answer was measured ONCE, at ONE parent, on ONE
pair of moons. The research doc states the mechanism as a property of the
FLIGHT PROFILE rather than of the pair -- "THE MECHANISM IS THE FLIGHT
PROFILE'S, NOT THE PAIR'S" (`same-parent-reaim-jool-system.md` section 11.3) --
but that is a PREDICTION, and roadmap gap G4 exists to convert it into a
measurement at a second parent.

So this lane's product is a Mun-rooted recording targeting MINMUS: two SIBLING
moons of KERBIN, which `IsSameParentTarget` classifies CROSS-PARENT exactly as
it classified Laythe/Vall. `V21M`/`V21T` read it. THIS SPEC ASSERTS NOTHING
ABOUT THE ROUTING -- B30 produces the subject, the V lanes read it, and a
DIFFERENT outcome here would be the finding of the lane.

REUSE, NOT REINVENTION. This is a THIN ALIAS over `mlib.b5_decide`, exactly as
b17 / b19 / b23 / b24 / b25 / b26 are. It adds NO new code: every mechanism it
flies was built for and live-proven by B26, and the ONLY thing this lane needed
from `mlib` was one cited row in `STOCK_BODY_GRAVITY` (Mun's mu / radius / SOI
triple). The whole lane is therefore parameterisation, which is precisely what
makes it a test of the CLASS rather than of a mechanism:

    startInOrbit = true            the ORBIT-START entry door; the fixture is
                                   already parked at Mun and PRELAUNCH hands
                                   straight to the ORBIT waypoint
    interplanetaryTransfer = true  rides, not replaced, by the relay: it narrows
                                   the correction domain and carries the stage-1
                                   evidence path
    parentRelayTransfer = true     B26's mode: an ESCAPE phase whose node mlib
                                   computes itself, then MechJeb's MOON Hohmann
                                   planned from the PARENT'S frame
    homeBodyName = "Mun"           the SOI the transfer departs from
    targetBodyName = "Minmus"      the sibling moon it captures at
    returnBodyName = "Kerbin"      THE TRANSFER FRAME. B26's was Jool; this is
                                   the same relation one system in
    viaBodyNames = ["Kerbin"]      the SOI the coast legitimately operates in
    ejectionEccFloor = 0           RETIRED on a relay lane, and for the SAME
                                   arithmetic reason as at Laythe: a correctly
                                   sized patched-conic escape from Mun is BOUND
                                   (ecc 0.7836 at the committed 110 m/s),
                                   because reaching a SOI needs an APOAPSIS past
                                   it, not an escape
    transferMinApoapsisMeters      STAGE-2 burn-done evidence: the KERBIN-frame
      = 45,000,000                 apoapsis reaches Minmus's orbit
    captureEnabled = true          the B11/B12 orbit tail

WHAT IS GENUINELY NEW HERE, stated up front so the readings are attributed
rather than pooled with B26's:

  (a) A DIFFERENT PARENT. That is the replication's whole purpose and needs no
      further defence.

  (b) MINMUS IS INCLINED ~6 deg, where Laythe and Vall are both coplanar with
      Jool's equator. The stage-2 plan and the capture therefore carry a
      plane-change cost B26 never paid: 18.30 m/s if taken at the transfer
      apoapsis, 71.67 m/s if taken at Mun's own orbital radius. MechJeb's
      `OperationTransfer` plans a combined manoeuvre, so the term is priced
      INSIDE the stage-2 node rather than added beside it. **THE
      ONE-DIMENSION-AT-A-TIME RULE IS UNAVOIDABLY BENT BY THIS LANE** -- it
      changes the parent AND adds an inclined target in one step -- and the
      honest consequence is that a stage-2 node materially above the derived
      band cannot be attributed to the parent change alone. Said out loud
      rather than discovered later.

  (c) THE PARENT ENVELOPE IS 13x TIGHTER, AND IT INVERTS THE ESCAPE'S BINDING
      CONSTRAINT. Kerbin's SOI is only **7.01x** Mun's orbital radius
      (84,159,286 / 12,000,000) where Jool's is **90.35x** Laythe's, and Minmus
      sits at **55.8%** of Kerbin's SOI where Vall sits at 1.8% of Jool's. So
      the unaimed escape's high-energy tail does not merely overshoot the
      target the way B26's did -- IT LEAVES THE PARENT SYSTEM, which is a
      lane-ending outcome B26 never had to size against. B26 had to OVER-size
      its escape (450 m/s against a 347.245 ideal) to clear a reachability
      floor; this lane deliberately UNDER-sizes its escape (110 m/s against a
      142.257 ideal) to hold the Kerbin-escape tail at 0.1% of the unaimed
      band. Same mode, opposite binding constraint. The sweep is in the spec.

WHAT THE ESCAPE DOES *NOT* DO. It is not aimed. A prograde burn at whatever
periapsis comes next sends the outgoing asymptote in a direction set by the
park's own orientation, so the delivered Kerbin-frame orbit lands anywhere in a
band. That is B26's stated v1 limitation, inherited verbatim: aiming it would
need the vessel's and Mun's state VECTORS in Kerbin's frame plus an asymptote
solve. STAGE 2 RE-PLANS FROM WHEREVER WE ARE, which is what makes the band
affordable -- and here the band is CHEAPER than B26's, because the whole
transfer is smaller (a derived stage-2 median of 129 m/s against B26's measured
415.46).

WHAT THE ESCAPE *DOES* AIM AT is the relative speed ACROSS MUN'S SOI BOUNDARY,
which is where KSP's patched conic hands the vessel to Kerbin. That is B26
flight 2's defect-A fix and it is a property of the GAME, not of Laythe:
`sqrt(v_soi^2 + 2*mu*(1/r_pe - 1/r_soi))`. At Mun the two references differ by a
factor of **2.33**: a 110 m/s ASYMPTOTIC request would deliver 256.36 m/s at the
boundary (`sqrt(110^2 + 2*mu_Mun/r_soi)`), against 3.12x at Laythe -- smaller,
because Mun's well is shallower relative to its SOI, but the same defect and the
same fix. The sharpest way to put it at this pair: the CORRECTLY sized escape
here has a specific energy of **-20,760.8 m^2/s^2**, i.e. it is BOUND and has NO
asymptotic excess at all, so the quantity the pre-fix formula aimed at does not
exist for this orbit.

A FIXTURE PRECONDITION THIS MISSION CANNOT ENFORCE. The save this mission is
pointed at must carry NO COMMITTED TREE FOR THIS VESSEL. A seam StartRecording
on a vessel that is a committed tree's own launch does not open a standalone
tree -- the committed-restore path re-resumes that tree's recording and
StartRecording answers `already=true`, so the hop is APPENDED to the wrong tree
and the run goes mechanically green while producing the wrong product (measured,
B23 flight 1). See docs/dev/todo-and-known-bugs.md ->
SEAM-STARTRECORDING-JOINS-COMMITTED-TREE. That is why this lane points at the
PARSEK-STRIPPED `mun-park-kerbalx` and not at `mun-orbit-recorded`, which is
V6M/V6T's subject and carries a committed tree for exactly this craft.

WHY BOTH PATHS, ONE PER STAGE. There are TWO burns and they make DIFFERENT
claims, so they need different evidence:
    STAGE 1 claims "the orbit will LEAVE Mun"          -> `_relay_escape_burn_done`
      (already outside the home SOI, OR genuinely hyperbolic, OR the home-frame
      apoapsis RADIUS reaches the SOI). It needs no spec value; the machine
      reads the SOI radius from `STOCK_BODY_GRAVITY`.
    STAGE 2 claims "the transfer REACHES MINMUS'S ORBIT" -> transferMinApoapsisMeters,
      read in KERBIN's frame. Without it stage 2 has no evidence at all: at
      stage 2 the craft is inside a via body's SOI, so the ecc branch's
      left-the-home-SOI early return fires unconditionally and the exit would
      collapse to "a node was consumed".

WHAT THE MACHINE ASSERTS, unchanged from B26's eight rows: `reachedOrbit`,
`startedInHomeOrbit`, `escapedHomeSoi`, `reachedTargetSoi`, `flybyPeriapsisFloor`,
`capturedInTargetOrbit`, `parkedStable`, `treeCommitted`.

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

_MISSIONS = os.path.dirname(os.path.abspath(__file__))
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)

import mission_runner  # noqa: E402
import mlib  # noqa: E402

MISSION_NAME = "b30_mun_minmus"


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
    # MechJeb is required for both halves: the stage-2 sibling transfer
    # (ManeuverPlanner.OperationTransfer + NodeExecutor) and the capture
    # (circularize-at-periapsis + NodeExecutor). One seam, one connection. The
    # ESCAPE node is NOT MechJeb's -- mlib computes it and kRPC's own
    # Control.add_node places it, which is the whole reason the flight-1
    # moon-origin refusal cannot recur.
    #
    # The same three opt-in observation channels every capture lane requests,
    # each failing CLOSED at its unread sentinel:
    #   read_docking       angular_velocity IS the PARK tumble gate; without it
    #                      the gate fails closed forever.
    #   read_node_executor OBSERVE that MechJeb's NodeExecutor engaged, never
    #                      infer it from having commanded it (B11 flight 1).
    #                      **IT MATTERS MORE HERE THAN ON B26**: all three of
    #                      this lane's burns are SHORT -- ~147 m/s escape,
    #                      ~129 m/s stage 2, ~82 m/s capture, each under 10 s at
    #                      the Poodle's 250 kN on a ~14 t stack -- so "did it
    #                      engage?" is exactly the question a human watching the
    #                      apsides cannot answer in time. B26's burns were
    #                      96 / 51-103 / 54 s.
    #   read_periapsis     the ONLY legitimate in-SOI warp target is the orbit's
    #                      own periapsis clock (B12 flight 3). Minmus's
    #                      SOI-entry -> periapsis coast is ~19,077 game s -- 4.6x
    #                      Vall's -- and B19 flight 4 measured a single unclamped
    #                      frame swallowing 27,596.
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
    # coast legs AND both MechJeb executor autowarps engage rails. Note this
    # lane's coast is LONG by moon-system standards and is the one place the
    # warp block could NOT be copied from B26: a Mun->Minmus Hohmann is
    # ~267,853 game s against B26's ~38,980, i.e. 6.9x, because Minmus orbits
    # at 47 Mm where Vall orbits at 43 Mm but Jool's mu is 80x Kerbin's.
    #
    # max_physics_warp is retained at the shared value: the CORRECTION-BURN
    # attitude flip runs under mild PHYSICS warp (flipPhysicsWarpFactor), and that
    # cap is what bounds it. It is a CEILING, not a request.
    allow_rails_warp=True,
    max_physics_warp=4.0,
    # No settle tail (the B5/B11/B17/B23/B24/B25/B26 SF-4 contract): every
    # assertion is machine-carried evidence and evaluate discards the frames, so
    # post-terminal reads only add transient-failure surface that can flip a
    # finished pass into a FLAKE -- and the terminal frame is the frame AFTER the
    # tree was committed, which is exactly the frame not to keep polling past.
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
