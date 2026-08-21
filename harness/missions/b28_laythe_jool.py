"""Mission b28_laythe_jool: START ALREADY PARKED around LAYTHE, fly ONE prograde
escape burn out of Laythe's SOI, coast UP into JOOL's frame, circularize at the
Jool-frame periapsis and COMMIT the tree there.

**FLOWN 2026-08-20, run `2026-08-20_2330`, PASS ATTEMPT 1.** The eleven-phase
chain PRELAUNCH -> ORBIT -> ESCAPE -> TRANSFER-BURN -> COAST-TO-TARGET ->
TARGET-FLYBY -> PLAN-CAPTURE -> CAPTURE-BURN -> PARK -> ORBIT-COMMIT ->
ORBIT-COMMITTED, with NO PLAN-TRANSFER and NO second TRANSFER-BURN -- the
no-stage-2 claim below read off the flown phase list rather than argued -- all
eight assertions met, `escapedHomeSoi` carried by the park-at-parent disjunct
(`provenBy=parkAtParentTargetSoiReached`, `relayStage 0`), harvested as
`fixtures/saves/jool-return-recorded`. The routing pair reserved below WAS
authored 2026-08-21 and HAS flown its unarmed READING runs, both PASS attempt 1
(`2026-08-21_0746` V19M, `2026-08-21_0750` V19T); neither is armed yet. Every
per-flight number, and the disposition of each pre-registration, lives in the
spec's flight ledger (`scenarios/B28-laythe-jool-return.toml`), which is their
single authority -- this docstring states only what the mission IS, so the two
cannot drift into two versions of the same measurement.

WHAT IT FLIES. The crewed Duna Rocket starts ALREADY PARKED around LAYTHE -- the
committed Parsek-stripped `laythe-park-nerv` fixture, an 87,931.3 x 56,240.3 m park
at eccentricity 0.0277, the orbit B25 delivered and B26 departs -- fires ONE
prograde escape node out of Laythe's SOI, coasts into JOOL's frame, circularizes at
the Jool-frame periapsis and commits the tree there. There is no transfer planner
anywhere in the flight: the escape IS the transfer, because the parent frame it
delivers into is the destination.

THE POINT (the Parsek surface, not the rocketry). Every committed loop subject in
the suite is OUTBOUND: B23 Duna->Ike, B24 Eve->Gilly, B25 Jool->Laythe, B26
Laythe->Vall, and every interplanetary lane before them departs Kerbin. A supply
run is a round trip, so half of the render surface the loop machinery will
actually be asked to draw HAD never been produced at all -- until this lane flew
on 2026-08-20 and `fixtures/saves/jool-return-recorded` became the first
return-direction subject in the suite. THE SUBJECT EXISTING IS NOT THE GAP
CLOSED: producing a return-direction recording is this mission's whole job, and
what the render hosts DO with one is the V19 pair's measurement, under the
confirmation bar the roadmap states for G2 (armed runs plus a control that
inverts a render token). Nothing here books that.

**THIS IS THE FIRST RETURN-DIRECTION SUBJECT.** A Laythe-rooted recording whose
target is JOOL is the INVERTED same-parent direction: the target is the origin's
PARENT rather than the origin's child, which is the mirror of B25's Jool->Laythe
and nothing like B26's Laythe->Vall sibling hop. Whether the classifiers treat
that inversion SYMMETRICALLY had never been measured on a real recording, for the
plain reason that no such recording existed to measure.
`IsSameParentTarget(target, launch)` asks exactly ONE question -- is the target a
direct CHILD of the launch body -- so the relation is DIRECTIONAL BY CONSTRUCTION
and a target that IS the parent is simply not the shape it was written against.
What the REST of the routing stack then does with that reading -- the re-aim
classifier's admission, the phase-lock solver's constraint count, the window
spacing -- is what nobody knew. The V19 reading runs named at the top of this
docstring are where it got measured, and the reading belongs in THEIR headers
rather than in a second copy here: a second copy of a measurement is a second
thing to leave stale. The mirror-direction lesson (PRs #1474/#1475) is exactly
"walk the mirror rather than assume it", which is why this lane exists rather
than an argument that it need not.

**AND THIS FILE ASSERTS NOTHING ABOUT ANY OF THAT.** What B28 does is fly the hop
and commit the recording. V19M and V19T -- the loop pair reserved for this
subject, authored 2026-08-21 off its harvested bytes and flown as unarmed READING
runs (both PASS attempt 1), not yet armed -- are the lanes that read the routing
OFF the produced recording, and they gate on NEITHER outcome:
same-parent-phase-lock and cross-parent-re-aim are both admissible readings of a
return leg, and the point of flying it is to find out which one the product gets.
Nothing in this file or in its
spec encodes a prediction. See `docs/dev/autotest-roadmap.md` -> the gap register
-> **G2 - Return legs**, which reserves the B28 / V19 ids and states the
confirmation criteria (destination-frame render tokens at derived epochs on the
flight map, the TS and the KSC host; armed, with a control that inverts a render
token).

THE SECOND THING THE RETURN DIRECTION IS SUPPOSED TO BUY, and it is the reason the
roadmap ranks G2 above the other gaps: the return direction is stated there as the
only one that can ever activate the KSC render host, because `ParsekKSC.Playback`
is HARD-GATED to Kerbin-frame points (skip reason `non-kerbin`), which makes that
whole host VACUOUS on every outbound lane flown to date.

**THAT PREMISE IS PRE-REGISTERED AS A QUESTION RATHER THAN REPEATED AS A FACT**,
because reading the code turns up a STRICTER gate one level up that the roadmap's
sentence does not model: `ParsekKSC.IsKscStructurallyEligible` rejects a recording
on `rec.Points[0].bodyName != "Kerbin"` -- the recording's FIRST point, not its
arrival body -- and the Update loop `continue`s past an ineligible recording
silently. If that is what runs, a return leg ROOTED at a foreign body is excluded
from the KSC host whole, no matter where it arrives, and the roadmap's ranking
argument would need restating. THIS FILE CLAIMS NEITHER READING. The confirmation
criteria are explicit that a documented limitation must CITE A FLOWN RUN, so the
KSC lane of the B29 pair is where that gets measured; an inspection ranks a queue,
it does not close it.

B28 does not arrive at Kerbin at all -- B29 is the lane that does -- so none of
the above is this lane's to settle. What B28 IS, is the cheapest possible proof
that the inverted direction produces a well-formed subject at all, out of a
fixture that is already committed.

REUSE, NOT REINVENTION. This is a THIN ALIAS over `mlib.b5_decide`, exactly as
b25_laythe_orbit / b26_laythe_vall are. It is B26's PARENT-RELAY mode with stage 2
deleted rather than a new machine: stage 1 (the escape mlib computes itself and
kRPC's own `add_node` places) is the entire transfer, because the parent frame the
escape delivers into IS the destination.

    startInOrbit = true            the ORBIT-START entry door (B23's, live-proven
                                   on B23/B24/B25/B26)
    interplanetaryTransfer = true  required by the relay: the correction-domain
                                   narrowing in `_b5_correction_via_bodies` reads
                                   it, and without it the mode degrades silently
                                   into the moon machine
    parentRelayTransfer = true     B26's mode: an ESCAPE phase whose node mlib
                                   computes from the LIVE park, sized by vis-viva
                                   at the park's next periapsis against the speed
                                   it must carry ACROSS the home SOI boundary
    relayParkAtParent = true       THE NEW FLAG, and the only thing mlib gained.
                                   It says THIS RELAY LANE HAS NO STAGE 2.
    homeBodyName = "Laythe"        the SOI the escape departs from
    targetBodyName = "Jool"        the PARENT, and here also the capture target
    returnBodyName = "Sun"         argued at length below; it is NOT "Jool"
    viaBodyNames = ["Sun"]         B22's pairing, and the via list that keeps the
                                   return body a member of it
    captureEnabled = true          the B11/B12 orbit tail, and a hard requirement
                                   of `relayParkAtParent`: a parked, COMMITTED
                                   recording at the parent IS the lane's product
    transferMinApoapsisMeters = 0  ASSERTED ZERO, AND STATED EXPLICITLY. On B26
                                   this is the stage-2 sibling transfer's ONLY
                                   burn-done evidence and mlib raises at spec load
                                   if it is not positive; here stage 2 is
                                   STRUCTURALLY UNREACHABLE, so a positive floor
                                   would be a number no frame can ever be judged
                                   against. `relayParkAtParent` INVERTS that
                                   load-time gate to "must be exactly 0" rather
                                   than relaxing it, so a spec written against the
                                   two-stage lane cannot be flown as this one. The
                                   gate reads the key's PRESENCE, not
                                   `params.get(key, 0.0)`: an ABSENT key parses to
                                   the constructor's own positive default
                                   (10,000,000 m), so omitting it would carry a
                                   live-looking floor into the machine -- which is
                                   the silent shape the gate exists to close.

WHY STAGE 2 IS STRUCTURALLY UNREACHABLE, since "asserted zero" is only honest if
the unreachability is mechanical. In `b5_decide`'s COAST-TO-TARGET branch the very
first test is `snapshot.body == state.params.target_body`, and it hops into
TARGET-FLYBY unconditionally. The relay's stage-2 hand-off
(`_b5_enter_relay_transfer`, guarded on `snapshot.body == _b5_return_body(params)`)
sits TWO branches further down. With `targetBodyName = "Jool"` the frame that first
reads Jool takes the target hop and never reaches the relay branch at all -- so on
this lane the whole `PLAN-TRANSFER` -> `TRANSFER-BURN`(stage 2) leg is dead code
and `relay_stage` never leaves `RELAY_STAGE_ESCAPE`.

BUT THAT IS A PROPERTY OF TWO ADJACENT BRANCHES AND THEIR ORDER, which any later
edit may reorder or insert between without noticing, so `relayParkAtParent` carries
the suppression as an explicit `not relay_park_at_parent` conjunct ON the stage-2
hand-off. The conjunct is a property of the FLAG and survives the reorder; the
failure it closes is silent rather than loud, because with the branches swapped the
machine would plan and fly a sibling Hohmann nobody asked for out of a park it was
supposed to keep. That is the difference between a contract and a coincidence.

Stage 1's burn-done evidence is untouched and is the one this lane actually flies:
`_relay_escape_burn_done`'s SOI-REACH disjunct, i.e. the Laythe-frame apoapsis
ALTITUDE reaching 3,223,645.8 m (Laythe's 3,723,645.81 m SOI radius less its
500,000 m body radius), or the orbit going hyperbolic. `b5_burn_done_evidence_text`
already names that threshold rather than the apoapsis floor for a stage-1 frame,
so the give-up text on this lane quotes a number the frame was really judged
against.

WHY `returnBodyName` IS "Sun" AND NOT "Jool". It reads backwards -- the craft is
GOING to Jool, so surely Jool is the frame -- and it is the single most
consequential value in the spec, so the argument is spelled out rather than
inherited.

  * **"Jool" WOULD ASSERT-FAIL THE MISSION ON ARRIVAL.** `b5_decide`'s TARGET-FLYBY
    branch computes `return_body = _b5_return_body(params)` and its FIRST test is
    `capture_enabled and snapshot.body == return_body` -> ASSERT-FAIL, "left the
    target SOI without capturing". The capture-arming test on `target_body` is the
    NEXT branch down. With `returnBodyName = "Jool"` and `targetBodyName = "Jool"`
    those two are the same string, the failure branch is checked first, and the
    machine kills the mission on the very frame it arrives -- deterministically, on
    every attempt, with a message describing something that did not happen. Not a
    tuning preference: an unflyable lane.
  * **"Sun" KEEPS THE LEAVE-JOOL-SOI ASSERT-FAIL SEMANTICALLY CORRECT.** That guard
    is the right guard and this lane wants it armed: a craft that coasts through
    Jool's SOI without circularizing has failed, and the recording it would produce
    is not the one the lane exists for. Leaving Jool's SOI DOES read "Sun", so with
    "Sun" the guard fires on exactly the event it names and on nothing else.
  * **IT IS B22-jool-orbit's LIVE-PROVEN PAIRING.** B22 flies Kerbin -> Jool with
    `targetBodyName = "Jool"`, `returnBodyName = "Sun"`, and its own capture tail.
    Same target body, same capture semantics, same exit body; B28 differs only in
    where it departs from. Reusing a pairing a green flight has already measured
    beats inventing one.
  * **AND IT SATISFIES THE PINNED PRECONDITION.** `_b5_correction_via_bodies`
    narrows the no-encounter correction domain to `(return_body,)` on an
    interplanetary lane, and its own docstring is explicit that the safety argument
    ("a strict subset, so it can only ever REMOVE a firing opportunity") holds ONLY
    while `return_body` is a member of `via_bodies` -- a PRECONDITION the function
    does not enforce. Hence `viaBodyNames = ["Sun"]`: with it, the narrowing is
    `("Sun",) <= ("Sun",)` and the argument stands. The precondition is pinned by
    `test_the_return_body_is_a_member_of_the_via_bodies_on_every_lane`
    (`missions/lib/test_shells.py:2869`), and this lane's cell in
    `missions/lib/test_b28_laythe_jool.py` re-proves it over the committed spec.
    HONEST SCOPE: this lane sets `courseCorrectPeriapsisMeters = 0`, which disables
    both correction phases outright, so the narrowed domain never fires here at all.
    The precondition is therefore a FORWARD guard -- for whoever turns corrections
    on later -- rather than a live constraint today. It is satisfied anyway because
    satisfying it costs nothing and violating it would leave a trap with no symptom.

  ONE CONSEQUENCE OF THE CHOICE, stated so it is not mistaken for an oversight:
  `_b5_coast_bodies` becomes `("", "Laythe", "Sun")`, which does NOT contain "Jool".
  That is harmless precisely because of the branch ordering above -- the Jool frame
  is caught by the target hop before the coast-domain test is ever reached -- and it
  is why the domain list is not widened to name the parent. Widening it would add a
  body the machine can only reach through the target branch, and would silently
  legalise a Jool reading in a phase that should never see one.

MLIB GAINED `relayParkAtParent` AND ONE GATE ON THE MODE IT MODIFIES, and the
flag-off contract is PAD-ALIGN's, ORBIT-START's and PARENT-RELAY's verbatim: with
`relayParkAtParent` absent the stage-2 hand-off is reachable exactly as B26
leaves it, the `transferMinApoapsisMeters` load-time gate keeps its B26 "must be
positive" meaning, and every other lane's decisions, actions and assertion rows
are byte-identical (pinned by `RelayParkAtParentInertnessTests`, which asserts
the flag-off `escapedHomeSoi` detail dict against the frozen pre-modifier shape
rather than against the keys the modifier added).

THE ONE GATE, and it is on `parentRelayTransfer` rather than on the modifier
because BOTH arms of the mode have the hole: a relay spec whose `targetBodyName`
or `returnBodyName` IS the `homeBodyName` is REJECTED at spec load. Both arms of
`escapedHomeSoi` are proofs by OBSERVED SOI BODY, and an observation proves
DEPARTURE only while the body observed is not the body departed from. With them
equal a single coast frame reading the home body certifies the row -- through the
target hop on a park-at-parent lane, through the stage advance on a two-stage
one. Neither committed relay spec is affected (B26: Laythe/Vall/Jool; B28:
Laythe/Jool/Sun), which is what makes it a trap for the NEXT author rather than a
bug in either. The flag itself carries two load-time implications of its own,
each raising rather than degrading:
it REQUIRES `parentRelayTransfer` (it modifies that mode and nothing else) and it
REQUIRES `captureEnabled` (without the orbit tail there is no park to hold and no
tree to commit, and the mission would terminate at a phase that is not its
product).

A FIXTURE PRECONDITION THIS MISSION CANNOT ENFORCE, inherited verbatim from B23
flight 1 and repeated on every orbit-start lane since: the save this mission is
pointed at must carry NO COMMITTED TREE FOR THIS VESSEL. A seam StartRecording on a
vessel that is a committed tree's own launch does not open a standalone tree -- the
committed-restore path re-resumes that tree's recording and StartRecording answers
`already=true`, so the whole hop is appended to the OLD recording and the product's
launch body is whatever that recording's was. The mission runs identically either
way and every assertion still passes, so nothing here or in `mlib` can detect it;
the scenario spec owns it by pointing at the Parsek-stripped `laythe-park-nerv`
fixture -- B26's own subject, re-used unchanged, a 87,931.3 x 56,240.3 m Laythe park
at eccentricity 0.0277. Do NOT re-point this mission at `laythe-orbit-recorded`
(the `--keep-parsek` B25 harvest that fixture was derived from). See
docs/dev/todo-and-known-bugs.md -> SEAM-STARTRECORDING-JOINS-COMMITTED-TREE.

WHAT THE ESCAPE DOES *NOT* DO, restated from B26 because the limitation is
inherited whole and a reader will look for it: it does not AIM. The node is pure
prograde at the park's next periapsis, so the outgoing direction is where the
park's own orientation sends it. On B26 that cost real delta-v, because an unaimed
escape delivers an arbitrary Jool-frame ellipse and stage 2 had to pay 2*|v_rel|
to fix it. **HERE IT COSTS NOTHING, and that is the whole reason this lane is
cheap:** any escape that clears Laythe's SOI is already in the destination frame,
so there is no direction to be wrong about. What the escape DOES aim at is the
SOI-BOUNDARY relative speed, never a hyperbolic excess at infinity -- KSP hands a
departing vessel to the parent AT the boundary, where the home well is not fully
climbed, and at Laythe the two differ by a factor of 3.12. B26 flight 2 measured
that the hard way; `escapeSoiSpeedMps` is the corrected contract and mlib rejects
the retired `escapeTargetVInfMps` by name.

WHAT THE MACHINE ASSERTS -- B26's set, with the one row the return direction
re-points:

    reachedOrbit           ORBIT reached -- here, the ENTRY gate passed
    startedInHomeOrbit     the mission began from a BOUND, in-gate park around
                           LAYTHE, so the recording it produces is rooted there.
                           Carried evidence, fails CLOSED on a missing stamp.
    escapedHomeSoi         RELAY ONLY, and THE ROW THE RETURN DIRECTION RE-POINTS.
                           On B26 its evidence is `relay_stage` advancing, which
                           happens on the coast frame that first reads the PARENT.
                           On THIS lane that same frame is the TARGET frame, taken
                           by the target hop two branches earlier, so `relay_stage`
                           stays at RELAY_STAGE_ESCAPE for the whole flight and the
                           stage evidence would read met=False forever -- turning a
                           perfectly flown escape-and-park into MISSION-ASSERT-FAIL.
                           `relayParkAtParent` therefore adds a SECOND, POSITIVE
                           disjunct rather than relaxing the first: the ESCAPE phase
                           RAN (the node was authored and flown) AND TARGET-FLYBY
                           was REACHED. TARGET-FLYBY is entered from exactly one
                           place -- the coast frame that observed
                           `snapshot.body == target_body` -- so on a lane whose
                           target IS the parent that pair is a MEASURED reading of
                           the craft inside Jool's SOI. WHICH HALF OF THE PAIR IS
                           LOAD-BEARING, since the two-part reading over-sells it:
                           on a park-at-parent lane the ESCAPE conjunct cannot be
                           False whenever TARGET-FLYBY was reached, because the
                           post-ORBIT hand-off diverts into ESCAPE while the stage
                           is 0 (forever, here) and PLAN-TRANSFER is unreachable, so
                           EVERY route into the coast runs through ESCAPE -- the
                           flown phase list is the proof. The live evidence is the
                           TARGET-FLYBY observation; ESCAPE is a fail-CLOSED belt
                           against a phase list this machine could not produce. AND
                           THAT OBSERVATION PROVES DEPARTURE ONLY BECAUSE THE TARGET
                           IS NOT THE HOME BODY -- a property of the SPEC, not of
                           any frame -- so `mlib` REJECTS a relay spec whose target
                           or return body is the home body, at spec load, by name.
                           Without that gate the row greens on a craft that never
                           left: the reader who found it drove exactly that. The
                           row's `provenBy` detail names which disjunct fired
                           (`parkAtParentTargetSoiReached` here; it rides the row
                           only on a park-at-parent lane, so B26's row keeps its
                           pre-modifier shape byte for byte). B26 is judged by the
                           stage and nothing else: the disjunct is gated on the flag.
    reachedTargetSoi       the Jool SOI was actually entered
    flybyPeriapsisFloor    the whole in-SOI stay cleared the floor -- which on THIS
                           lane guards Jool's ATMOSPHERE, the deepest in the stock
                           system, rather than terrain, so a breach is not just a
                           red, it is a destroyed vehicle
    capturedInTargetOrbit  the orbit read at PARK entry is BOUND and in-window
    parkedStable           the park was held through the dwell
    treeCommitted          the seam commit answered OK at the Jool park

Leaving the Jool SOI anywhere in the tail is an ASSERT-FAIL, not a success.

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

MISSION_NAME = "b28_laythe_jool"


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
    # MechJeb is required even though mlib authors the escape node itself: the
    # NodeExecutor flies it, and the capture half (circularize-at-periapsis +
    # NodeExecutor) is MechJeb end to end. One seam, one connection.
    #
    # The same three opt-in observation channels every capture lane requests,
    # each failing CLOSED at its unread sentinel:
    #   read_docking       angular_velocity IS the PARK tumble gate; without it
    #                      the gate fails closed forever.
    #   read_node_executor OBSERVE that MechJeb's NodeExecutor engaged, never
    #                      infer it from having commanded it (B11 flight 1). It
    #                      matters for BOTH burns on this lane: the escape is a
    #                      several-hundred-m/s node on the NERV's ~6 m/s^2, and
    #                      the Jool capture is larger still, so neither can be
    #                      confirmed by watching for a fast apsis jump.
    #   read_periapsis     the ONLY legitimate in-SOI warp target is the orbit's
    #                      own periapsis clock (B12 flight 3). It matters MORE
    #                      here than on any moon arrival: Jool's SOI is
    #                      2.456e9 m in radius, so the SOI-entry -> periapsis
    #                      coast is the longest in the suite outside the
    #                      heliocentric lanes, and B19 flight 4 measured a single
    #                      unclamped frame swallowing 27,596 game s on a body
    #                      three orders of magnitude smaller.
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
    # coast legs AND both MechJeb executor autowarps (escape and capture) engage
    # rails. The coast this lane warps through is the OUTWARD climb from Laythe's
    # SOI boundary to a Jool-frame periapsis, which is short by interplanetary
    # standards but long by moon-lane standards -- which is why the spec's warp
    # block is sized between B26's and B22's rather than copied from either.
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
