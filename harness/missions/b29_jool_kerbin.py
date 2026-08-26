"""Mission b29_jool_kerbin: START ALREADY PARKED around JOOL, wait out the
Jool-Kerbin window inside that park, eject onto a Sun-frame INBOUND transfer,
correct twice, capture into a KERBIN ellipse and COMMIT the tree there.

**AUTHORED 2026-08-26. NEVER FLOWN.** Every number in this file and in the spec is
DERIVED (from `jool-park-nerv`'s own bytes and the stock body constants), not
measured. There is no flight ledger yet; when the first flight happens its ledger
belongs in `scenarios/B29-jool-kerbin-return.toml`, which is its single authority,
and this docstring keeps stating only what the mission IS -- so the two cannot
drift into two versions of the same measurement.

WHAT IT FLIES. The crewed Duna Rocket starts ALREADY PARKED around JOOL -- the
committed Parsek-stripped `jool-park-nerv` fixture, a 584,321,095.005 x
584,330,474.175 m park at eccentricity 7.9440625486254423e-06, the orbit B22
delivered and B25 departs -- waits out the Jool-Kerbin synodic window inside that
park (MechJeb's `WaitForPhaseAngle`), fires the ejection MechJeb's own
interplanetary planner authors, coasts 24,252,690 s on the Sun, takes two course
corrections, and closes a hyperbolic Kerbin arrival into a 150,000 x 6,000,000 m
ellipse before committing the tree.

THE POINT (the Parsek surface, not the rocketry). **THIS IS THE FIRST RECORDING
THAT ARRIVES AT KERBIN FROM ANOTHER PLANET.** Every interplanetary lane in the
suite departs Kerbin -- B7 Duna, B15/B16 Eve, B17 Duna, B18/B19 Dres, B20 Moho,
B21 Eeloo, B22 Jool -- and the ONE return-direction subject that exists
(`jool-return-recorded`, B28's) is a MOON-to-PARENT hop that never leaves the Jool
system. So "Kerbin as a DESTINATION", a body-frame arrival at the one body every
route network touches, has never been produced at all. Producing it is this
mission's whole job; what the render hosts DO with one is the V20 pair's
measurement, under the confirmation bar the roadmap states for G2.

**AND THIS FILE ASSERTS NOTHING ABOUT THAT.** B29 flies the hop and commits the
recording. `V20M` / `V20T` (and `V20K`, the KSC host lane) are reserved for this
subject and are the lanes that read the routing OFF the produced recording; they
gate on no outcome. Same-parent-phase-lock, cross-parent-re-aim and faithful
replay are all admissible readings of an inbound interplanetary leg, and the point
of flying it is to find out which one the product gets. Nothing here or in the
spec encodes a prediction. See `docs/dev/autotest-roadmap.md` -> the gap register
-> **G2 - Return legs**.

THE KSC PREMISE IS PRE-REGISTERED AS A QUESTION, INHERITED VERBATIM FROM B28, AND
THIS LANE IS WHERE IT GETS MEASURED. The roadmap ranks G2 partly because the
return direction is stated there as the only thing that can activate the KSC render
host. Reading the code turns up a STRICTER gate than that sentence models:
`ParsekKSC.IsKscStructurallyEligible` rejects a recording on
`rec.Points[0].bodyName != "Kerbin"` -- the recording's FIRST point, not its
arrival body -- and B29's first point is at JOOL. If that is what runs, this
recording is excluded from the KSC host WHOLE despite arriving at Kerbin, and the
roadmap's ranking argument needs restating. **THIS FILE CLAIMS NEITHER READING.**
The confirmation criteria are explicit that a documented limitation must CITE A
FLOWN RUN, so `V20K` over these bytes is where it becomes either a closed payoff
or a cited limitation -- an inspection ranks a queue, it does not close it.

WHY THIS LANE DEPARTS JOOL AND NOT DUNA, because the roadmap reserved
`B29-duna-kerbin-return` and this is not that. THE DUNA FIXTURE CANNOT FLY THE
MISSION, measured off its own bytes: `duna-park-probe`'s DD1 probe carries 1.6300 t
dry and 124.920 units of propellant = 0.62460 t, which on the LV-909's 345 s vacuum
Isp is **1,097.5 m/s**. A Duna -> Kerbin return needs 585.0 (ejection from its
718 km park) + 1,060.5 (Kerbin capture and circularization) = **1,645 m/s before a
single correction round** -- a ~550 m/s deficit, 50% over budget, and structural
rather than tunable (a lower-periapsis two-burn Oberth ejection saves 12 m/s). The
roadmap already knew the shortfall and reserved `B31` as the Kerbin -> Duna SETUP
lane that would fix it. B31 costs a whole extra lane and flight; departing JOOL
costs nothing, because `jool-park-nerv` already exists, is already Parsek-stripped,
and already carries a crewed craft with ~3,967 m/s. **THE CLASS MEASUREMENT IS
UNCHANGED**: Jool and Duna are both direct children of the Sun, so Jool -> Kerbin
and Duna -> Kerbin are the identical SIBLING-PLANET inbound relation, and V20M /
V20T read the same routing question off either. B31 stays reserved for the eventual
Duna-origin breadth point. The full fixture sweep behind that choice is in the
spec's header.

REUSE, NOT REINVENTION. This is a THIN ALIAS over `mlib.b5_decide`, exactly as
b22_jool_orbit / b25_laythe_orbit / b28_laythe_jool are. It is **B22's ORDINARY
INTERPLANETARY MACHINE entered through B25/B28's ORBIT-START door** -- not a new
mode, and specifically NOT the parent-relay mode:

    startInOrbit = true            the ORBIT-START entry door (B23's, live-proven
                                   on B23/B24/B25/B26/B28). The fixture is already
                                   parked, so PRELAUNCH hands straight to ORBIT.
    interplanetaryTransfer = true  MechJeb's OperationInterplanetaryTransfer plans
                                   the ejection, with WaitForPhaseAngle ON (value
                                   None) so the window wait happens INSIDE the Jool
                                   park rather than being aligned for on a pad.
    homeBodyName = "Jool"          the SOI the ejection departs from
    targetBodyName = "Kerbin"      THE FIRST TIME THIS STRING IS A TARGET
    returnBodyName = "Sun"         the frame a FAILED capture would exit into, and
                                   the correction domain (see viaBodyNames)
    viaBodyNames = ["Sun"]         DERIVED, not copied -- see the spec and schema
    captureEnabled = true          the B11/B12 orbit tail: a parked, COMMITTED
                                   recording at Kerbin IS the lane's product
    captureEllipticalApoapsisMeters = 6000000
                                   THE NEW FLAG, and the only thing mlib gained.

**NO PARENT-RELAY, AND THE REASON IS STRUCTURAL RATHER THAN A PREFERENCE.** B26 and
B28 arm `parentRelayTransfer` because MechJeb 2.15.1's
OperationInterplanetaryTransfer REFUSES a MOON-parked origin (B26 flight 1 measured
it deterministically in six wall-seconds; docs/dev/todo-and-known-bugs.md ->
MECHJEB-INTERPLANETARY-PLANNER-REJECTS-MOON-ORIGIN). **JOOL IS A PLANET**, a direct
child of the Sun, so that refusal is out of scope and the plain planner applies.
None of the six relay keys is set here and none is even declared in this lane's
schema -- declaring them would invite a future author to arm a mode this lane has
no reason to enter, and mlib's relay gates would then judge rows that do not exist.

**THE COMBINATION IS NEVERTHELESS UNFLOWN, AND THE REFUSAL SHAPE IS PRE-REGISTERED
RATHER THAN ASSUMED AWAY.** No committed lane has ever run a NON-RELAY
`interplanetaryTransfer` from a NON-KERBIN park: every interplanetary lane that
uses the plain planner launches from the Kerbin pad, and every lane that starts in
orbit at a foreign body is either a same-parent moon hop (B25) or a relay (B26,
B28). So "MechJeb plans an interplanetary transfer from a planet park it did not
launch from" is an untested assumption of this lane, not an established fact. IF IT
REFUSES, the shape is the B26 flight-1 shape and the spec pre-registers it: the
planner throws server-side, `node_count` stays 0, PLAN-TRANSFER burns its
PLAN_MAX_ATTEMPTS in roughly 90 game-seconds of the plan cadence, and the machine
takes the NAMED early flake rather than idling out `planTimeoutSeconds`. That would
be a HARNESS finding, report-only, never a Parsek defect -- and the route around it
would be the parent-relay mode this file deliberately does not arm, re-argued at
that point rather than pre-emptively.

WHAT THE ELLIPTICAL CAPTURE IS, since it is the one mlib change. `_b5_capture_
achieved` has always been tolerance-only (an apoapsis ceiling, a periapsis floor,
an eccentricity ceiling) while PLAN-CAPTURE only ever emitted MechJeb's
`operation_circularize` at the arrival periapsis -- so a "loose" park window bought
nothing, because the executor flew the full circularization regardless (B28
delivered ecc 7.32e-07 under the loosest band in the suite). AT KERBIN THAT IS
UNAFFORDABLE: v_inf 2,713.00 m/s gives 4,096.09 m/s at a 150,000 m periapsis, and
circularizing there costs 1,926.12 m/s against the 6,000,000 m ellipse's 1,188.07 --
738.05 m/s of difference on a craft with 3,967 m/s TOTAL that has already spent
1,318.55 on the ejection. So `captureEllipticalApoapsisMeters` makes PLAN-CAPTURE
emit `mlib.capture_node_plan`'s ONE retrograde node at the arrival periapsis instead,
through the SAME kRPC `add_node(ut, prograde=)` seam the parent-relay escape already
uses (NO new runner surface -- only a log LABEL, defaulting to the escape's), flown by
the SAME NodeExecutor, judged by the SAME park triple.

**THE PARK SHAPE IS NOT WHAT THIS LANE MEASURES.** The roadmap's G2 entry says
"return to LKO"; LKO WAS DESCOPED FOR MARGIN and the descope is deliberate. What
V20M / V20T read off the product is a KERBIN-FRAME ARRIVAL with Kerbin-frame points,
and an ellipse is that exactly as a circle is. The circular park would have cost
1,926.12 m/s, leaving 58 m/s of reserve after corrections against the ellipse's
1,460.4 -- i.e. `maxCorrectionDvMps` would have become the mission's only dv guard
at an uncomfortable ~15 m/s per round. Descoping the altitude kept the measurement
and bought a 58% margin.

THE FLAG-OFF CONTRACT IS PAD-ALIGN's, ORBIT-START's and PARENT-RELAY's verbatim:
with `captureEllipticalApoapsisMeters` absent or 0 the PLAN-CAPTURE emission is
`ACTION_MJ_PLAN_CAPTURE` exactly as before, and every other lane's decisions,
actions and assertion rows are byte-identical. Pinned by
`CaptureEllipticalInertnessTests` (missions/lib/test_capture_elliptical.py), which
asserts the flag-off emission against the frozen pre-modifier Action rather than
against the keys the modifier added. FOUR LOAD-TIME IMPLICATIONS ride on the flag,
each RAISING rather than degrading, because each fails only AFTER the capture burn
has been flown: it requires `captureEnabled`, it must not exceed
`parkMaxApoapsisMeters`, it must exceed `courseCorrectPeriapsisMeters`, and the
eccentricity it delivers (0.7959 here) must fit under `parkMaxEccentricity` -- whose
default 0.5 is BELOW what any affordable inbound capture ellipse produces, which
makes "set the apoapsis, leave the park triple alone" precisely the spec that
deadlocks.

A FIXTURE PRECONDITION THIS MISSION CANNOT ENFORCE, inherited verbatim from B23
flight 1 and repeated on every orbit-start lane since: the save this mission is
pointed at must carry NO COMMITTED TREE FOR THIS VESSEL. A seam StartRecording on a
vessel that is a committed tree's own launch does not open a standalone tree -- the
committed-restore path re-resumes that tree's recording and StartRecording answers
`already=true`, so the whole hop is appended to the OLD recording and the product's
launch body is whatever that recording's was. The mission runs identically either
way and every assertion still passes, so nothing here or in `mlib` can detect it;
the scenario spec owns it by pointing at the Parsek-stripped `jool-park-nerv`
fixture (verified: no `Parsek/` directory, zero `RECORDING_TREE` nodes). Do NOT
re-point this mission at `jool-orbit-recorded` (the `--keep-parsek` B22 harvest that
fixture was derived from, carrying five committed recordings). See
docs/dev/todo-and-known-bugs.md -> SEAM-STARTRECORDING-JOINS-COMMITTED-TREE.

WHAT THE MACHINE ASSERTS -- B22's capture set, entered through B25/B28's door:

    reachedOrbit           ORBIT reached -- here, the ENTRY gate passed
    startedInHomeOrbit     the mission began from a BOUND, in-gate park around
                           JOOL, so the recording it produces is rooted there.
                           Carried evidence, fails CLOSED on a missing stamp.
    reachedTargetSoi       KERBIN's SOI was actually entered -- the row that makes
                           this the suite's first inbound interplanetary subject
    flybyPeriapsisFloor    the whole in-SOI stay cleared the floor -- which on THIS
                           lane guards KERBIN'S ATMOSPHERE (70,000 m) rather than
                           terrain, so a breach is not just a red, it is an
                           unplanned aerocapture on a craft whose heat shield was
                           never sized for a 4,096 m/s entry
    capturedInTargetOrbit  the orbit read at PARK entry is BOUND and in-window --
                           and the window is the Mun-exclusion one (see the spec)
    parkedStable           the park was held through the dwell
    treeCommitted          the seam commit answered OK at the Kerbin park

There is no `escapedHomeSoi` row: that row is the parent-relay mode's and this lane
is not a relay. Leaving the Kerbin SOI anywhere in the tail is an ASSERT-FAIL, not
a success.

This is a THIN shell: every decision is the pure `mlib` phase machine +
`mlib.evaluate_b5_assertions`; the flight, connect, logging and result write are
the shared `mission_runner` runtime. `import krpc` is lazy inside `mission_runner`
(never at module top), so this module imports clean on the base interpreter (no
venv).

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

MISSION_NAME = "b29_jool_kerbin"


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
    # MechJeb is required: it plans AND flies the ejection (the interplanetary
    # planner plus the NodeExecutor), it plans and flies both course corrections,
    # and the NodeExecutor flies the capture node mlib itself authors. One seam,
    # one connection.
    #
    # The same three opt-in observation channels every capture lane requests,
    # each failing CLOSED at its unread sentinel:
    #   read_docking       angular_velocity IS the PARK tumble gate; without it
    #                      the gate fails closed forever.
    #   read_node_executor OBSERVE that MechJeb's NodeExecutor engaged, never
    #                      infer it from having commanded it (B11 flight 1). It
    #                      matters for BOTH big burns here: the ejection is
    #                      1,318.55 m/s and the capture 1,188.07 m/s on the NERV's
    #                      ~6.2 m/s^2, i.e. burns of ~214 s and ~193 s, so neither
    #                      can be confirmed by watching for a fast apsis jump.
    #   read_periapsis     the ONLY legitimate in-SOI warp target is the orbit's
    #                      own periapsis clock (B12 flight 3), AND on this lane it
    #                      is load-bearing twice over: the capture node's UT IS
    #                      `ut + time_to_periapsis` (capture_node_plan places it
    #                      there), so an unread periapsis clock refuses the node
    #                      rather than mis-placing it. Kerbin's SOI-entry ->
    #                      periapsis coast is a derived 30,323 s.
    return mission_runner.KrpcMissionControl(
        use_mechjeb=True, client_name=MISSION_NAME, read_docking=True,
        read_node_executor=True, read_periapsis=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # RAILS warp is central and this lane warps FURTHER THAN ANY OTHER: the
    # window wait inside the Jool park (up to one 10,090,902 s synodic), the
    # 24,252,690 s heliocentric coast, and both MechJeb executor autowarps. The
    # worst-case span is 34,343,592 game-seconds, which is ~343 wall-seconds at
    # 100,000x -- affordable, but it is why the spec's coast budget is sized off
    # the synodic rather than copied from B17.
    #
    # max_physics_warp is retained at the shared value: the CORRECTION-BURN
    # attitude flip runs under mild PHYSICS warp (flipPhysicsWarpFactor), and that
    # cap is what bounds it. It is a CEILING, not a request.
    allow_rails_warp=True,
    max_physics_warp=4.0,
    # No settle tail (the B5/B11/B17/B23/B24/B25/B26/B28 SF-4 contract): every
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
