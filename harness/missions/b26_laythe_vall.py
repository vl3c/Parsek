"""Mission b26_laythe_vall: START ALREADY PARKED around LAYTHE, transfer to VALL
through JOOL's frame, capture into a Vall park, hold it, and COMMIT the tree
there.

THE POINT (the Parsek surface, not the rocketry). Every same-parent lane so far -
B23 (Duna->Ike), B24 (Eve->Gilly), B25 (Jool->Laythe) - produces a recording whose
launch body is the target's PARENT, which `IsSameParentTarget` classifies as
SAME-PARENT and which therefore routes to PHASE-LOCK. All three measured the same
road: `constraints=1`, `method=single-orbital`, residual 0.

**A MOON-TO-MOON RECORDING IS THE OTHER CLASS, AND NOTHING HAS EVER FLOWN IT.**
Vall is not a child of Laythe, so a Laythe-rooted recording targeting Vall is
CROSS-PARENT: `MissionConfig loop=true` should reach `ApplyReaim` rather than the
phase-lock solver, and `ReaimWindowPlanner.Plan` should space its windows on the
JOOL-FRAME synodic of the two moons. `docs/dev/research/same-parent-reaim-jool-system.md`
section 5.1 walks `TrySynthesizeTransfer` guard by guard and concludes that the
synthesizer "already works in a Jool-centric frame for P3 today, unmodified", and
calls that "the single most surprising finding in this investigation: Parsek may
already be re-aiming moon-to-moon hops. It is untested, unmeasured, and
unrepresented in any harness lane."

THIS MISSION'S PRODUCT IS THE SUBJECT THAT LETS SOMEONE MEASURE IT. What B26 does
is fly the hop and commit the recording; `V17M-laythe-vall-player-loop` and
`V17T-laythe-vall-ts-arrival` are the lanes that read the routing off it, and
their headers carry both hypotheses and the exact log lines that discriminate.
Nothing in this file or its spec asserts a routing outcome.

REUSE, NOT REINVENTION - AND THIS TIME IT IS A COMPOSITION OF TWO PROVEN BLOCKS
RATHER THAN A COPY OF ONE. This is a THIN ALIAS over `mlib.b5_decide`, exactly as
b17 / b19 / b25 are. What is new is the PAIRING, plus the PARENT-RELAY mode that
flight 1 turned out to require:

    startInOrbit = true            the ORBIT-START entry door (B23's, live-proven
                                   on B23/B24/B25 - but only ever on the MOON
                                   transfer path)
    interplanetaryTransfer = true  the INTERPLANETARY path (B7's, live-proven on
                                   eight lanes - but only ever from KERBIN with
                                   the SUN as the transfer frame)
    parentRelayTransfer = true     THE MODE BUILT AFTER FLIGHT 1: an ESCAPE phase
                                   whose node mlib computes itself, then MechJeb's
                                   MOON Hohmann planned from the PARENT'S frame
    homeBodyName = "Laythe"        the SOI the transfer departs from
    targetBodyName = "Vall"        the moon it captures at
    returnBodyName = "Jool"        the TRANSFER FRAME, one level down from "Sun",
                                   and the SOI whose arrival triggers stage 2
    viaBodyNames = ["Jool"]        the SOI the coast legitimately operates in
    ejectionEccFloor = 1.001       STAGE-1 burn-done evidence: a HYPERBOLIC
                                   Laythe-frame eccentricity
    transferMinApoapsisMeters      STAGE-2 burn-done evidence: the JOOL-frame
      = 36,000,000                 apoapsis reaches Vall's orbit. NOT the inert 0
                                   every other interplanetary lane carries -
                                   there are two burns here and they make
                                   different claims.
    captureEnabled = true          the B11/B12 orbit tail

MLIB GAINED THE RELAY AND NOTHING ELSE, and the flag-off contract is PAD-ALIGN's
and ORBIT-START's verbatim: with `parentRelayTransfer` absent the ESCAPE phase is
unreachable, `relay_stage` never leaves 0, and every other lane's decisions and
actions are byte-identical. The relay moves exactly three edges in `b5_decide`,
enumerated in that function's own docstring. The audit that established the
underlying composition is in the spec header (THE JOOL-FRAME AUDIT) and still
holds. Two of its findings belong here as well, because this file is where a
future re-point would happen:

  * **THE INTERPLANETARY PATH CARRIES NO SUN-FRAME ASSUMPTION.** The heliocentric
    ephemeris family is real Sun-hardcoding but is quarantined behind
    `pad_align_ejection` - three live call sites, all inside `if
    params.pad_align_ejection:`, plus a flag-gated load-time check. On the
    ordinary path the phase angle is computed ENTIRELY by MechJeb;
    `_b5_transfer_plan_action` emits `ACTION_MJ_PLAN_INTERPLANETARY_TRANSFER` with
    no body and no angle, and the runner's handler writes exactly one property
    (`wait_for_phase_angle`). `padAlignEjection` MUST STAY ABSENT here: it is
    unreachable anyway (`startInOrbit` is checked first in PRELAUNCH and returns
    unconditionally), and `b5_params_from_dict` would raise at spec load because
    Laythe is not in `STOCK_HELIO_ELEMENTS` - a loud failure, but not one worth
    provoking.
  * **THE ONE THING THE AUDIT COULD NOT ANSWER** was whether MechJeb 2.15.1's own
    `OperationInterplanetaryTransfer` accepts a Jool-parented pair. If it refuses
    it throws server-side, the runner logs and swallows, `node_count` stays 0, and
    the bounded re-plan cadence ends in a PLAN-TRANSFER flake. Loud and bounded,
    never silent - and it was named here as the single likeliest way this lane's
    first flight fails, a MECHJEB question rather than a Parsek one.

    **IT IS ANSWERED, AND THE ANSWER IS NO.** Flight 1 (2026-08-19, runs
    `2026-08-19_2214` / `_2215_a2`) measured exactly the predicted shape: both
    attempts INVALID(autopilot-flake), deterministic, refused in PLAN-TRANSFER six
    wall-seconds in on `OrbitExtensions.NextTimeOfRadius: given radius of
    3723645.81113302 is never achieved` out of
    `KRPC.MechJeb.Maneuver.Operation.MakeNodes`. It is not the Jool PARENTAGE that
    breaks it but the MOON-PARKED ORIGIN: MechJeb idealises the origin to a circle
    at the park's mean radius and builds a sub-escape ejection whose apoapsis lands
    2.443% short of the SOI, then asks for a crossing that does not exist. See
    `docs/dev/todo-and-known-bugs.md -> MECHJEB-INTERPLANETARY-PLANNER-REJECTS-MOON-ORIGIN`
    for the diagnosis, and the spec's own FLIGHT LEDGER for the full account.
    Everything this docstring asserts about mlib and the composition was CONFIRMED
    by that flight: the ORBIT-START door, the fixture, the preamble and target
    acquisition all behaved, and nothing in this file was implicated.

    **AND THE RELAY REMOVES THE QUESTION RATHER THAN ANSWERING IT.**
    `OperationInterplanetaryTransfer` is no longer called on this lane at all.
    Stage 1 is a node mlib computes and kRPC's own `add_node` places; stage 2 is
    `OperationTransfer`, planned from Jool's frame between two bodies that both
    orbit Jool - the shape B5/B6/B23/B24/B25 have flown a dozen times between
    them. What replaces the old open question is a NEW one, and it is now the
    likeliest way flight 2 fails: whether `OperationTransfer` plans a sane
    targeted intercept from the ECCENTRIC, slightly-inclined Jool orbit an UNAIMED
    escape delivers. The failure mode is identical in shape (a server-side throw,
    a logged warn, `node_count` 0, a bounded PLAN-TRANSFER flake), which is why it
    is an acceptable thing to fly at.

WHAT THE ESCAPE DOES *NOT* DO, stated here because it is the limitation a reader
will look for and it is deliberate: it does not AIM. The node is pure prograde at
the park's next periapsis, so the outgoing asymptote points where the park's own
orientation sends it, not at Vall. Aiming it would need the vessel's and Laythe's
state VECTORS in Jool's frame plus an asymptote solve - a new multi-channel
telemetry surface and an ephemeris solver, which was the explicit stop rule for
v1. The cost is priced in the spec's delta-v section (stage 2 is bounded by
2*v_inf = 694.5 m/s whatever direction comes out, and the margin covers it) and
in its forbidden-token derivation (an unaimed escape's parent apoapsis reaches
71.28 Mm, past Tylo's shell, so those tokens are a real guard now).

A FIXTURE PRECONDITION THIS MISSION CANNOT ENFORCE, inherited verbatim from B23
flight 1 and repeated on every orbit-start lane since: the save this mission is
pointed at must carry NO COMMITTED TREE FOR THIS VESSEL. A seam StartRecording on
a vessel that is a committed tree's own launch does not open a standalone tree -
the committed-restore path re-resumes that tree's recording and StartRecording
answers `already=true`, so the whole hop is appended to the OLD recording and the
product's launch body is whatever that recording's was. The mission runs
identically either way and every assertion still passes, so nothing here or in
`mlib` can detect it; the scenario spec owns it by pointing at the Parsek-stripped
`laythe-park-nerv` fixture. Do NOT re-point this mission at
`laythe-orbit-recorded` (the `--keep-parsek` B25 harvest it was derived from). See
docs/dev/todo-and-known-bugs.md -> SEAM-STARTRECORDING-JOINS-COMMITTED-TREE.

WHY BOTH PATHS, ONE PER STAGE, since both endpoints are moons. The MOON path
(`OperationTransfer`) plans a transfer from a PARKING ORBIT to a MOON OF THE BODY
BEING ORBITED - Kerbin-park to Mun, Jool-park to Laythe. At the START of this
mission the craft is parked at LAYTHE while the target orbits JOOL, so the pair is
launch-body-and-sibling, which is structurally the Kerbin->Duna case one level
down: escape the home SOI, coast in the parent frame, capture at the sibling. The
INTERPLANETARY path is the one that models that shape, and its
ejection-eccentricity evidence is exactly what a hyperbolic Laythe escape needs -
which is why `interplanetaryTransfer` stays on. What the relay changes is only
WHO PLANS: MechJeb's interplanetary planner cannot build the ejection (measured),
so mlib builds it. And once that ejection has run, the craft is in JOOL's park and
Vall is a MOON OF THE BODY BEING ORBITED - so stage 2 is the moon path's own
textbook case, not a workaround.

WHAT THE MACHINE ASSERTS - B25's set, plus ONE row the relay adds:

    reachedOrbit           ORBIT reached -- here, the ENTRY gate passed
    startedInHomeOrbit     the mission began from a BOUND, in-gate park around
                           LAYTHE, so the recording it produces is rooted there.
                           Carried evidence, fails CLOSED on a missing stamp.
    escapedHomeSoi         RELAY ONLY. The craft actually LEFT Laythe under its
                           own computed node and reached JOOL's frame - evidenced
                           by `relay_stage` advancing, which happens on exactly
                           one frame (the coast frame that first read Jool) and
                           cannot be satisfied by a node that was merely planned.
                           Every other row on this list is satisfiable by a flight
                           that never left home; this is the one that is not.
    reachedTargetSoi       the Vall SOI was actually entered
    flybyPeriapsisFloor    the whole in-SOI stay cleared the terrain floor
    capturedInTargetOrbit  the orbit read at PARK entry is BOUND and in-window
    parkedStable           the park was held through the dwell
    treeCommitted          the seam commit answered OK at the Vall park

Leaving the Vall SOI anywhere in the tail is an ASSERT-FAIL, not a success.

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

MISSION_NAME = "b26_laythe_vall"


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
    # MechJeb is required for both halves: the transfer
    # (ManeuverPlanner.OperationInterplanetaryTransfer + NodeExecutor) and the
    # capture (circularize-at-periapsis + NodeExecutor). One seam, one connection.
    #
    # The same three opt-in observation channels every capture lane requests,
    # each failing CLOSED at its unread sentinel:
    #   read_docking       angular_velocity IS the PARK tumble gate; without it
    #                      the gate fails closed forever.
    #   read_node_executor OBSERVE that MechJeb's NodeExecutor engaged, never
    #                      infer it from having commanded it (B11 flight 1). It
    #                      matters here for the EJECTION in particular: a ~1,010
    #                      m/s burn at the NERV's ~6 m/s^2 runs ~170 s, so "did it
    #                      engage?" cannot be answered by watching for a fast
    #                      apsis jump.
    #   read_periapsis     the ONLY legitimate in-SOI warp target is the orbit's
    #                      own periapsis clock (B12 flight 3). Vall's SOI-entry ->
    #                      periapsis coast is ~3,900 game s, and B19 flight 4
    #                      measured a single unclamped frame swallowing 27,596.
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
    # lane's coast is SHORT by interplanetary standards - a Laythe->Vall Hohmann
    # is ~38,980 game s, three orders of magnitude under B7's - which is why the
    # spec's warp block looks like a moon lane's rather than like B19's.
    #
    # max_physics_warp is retained at the shared value: the CORRECTION-BURN
    # attitude flip runs under mild PHYSICS warp (flipPhysicsWarpFactor), and that
    # cap is what bounds it. It is a CEILING, not a request.
    allow_rails_warp=True,
    max_physics_warp=4.0,
    # No settle tail (the B5/B11/B17/B23/B24/B25 SF-4 contract): every assertion
    # is machine-carried evidence and evaluate discards the frames, so
    # post-terminal reads only add transient-failure surface that can flip a
    # finished pass into a FLAKE -- and the terminal frame is the frame AFTER the
    # tree was committed, which is exactly the frame not to keep polling past.
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
