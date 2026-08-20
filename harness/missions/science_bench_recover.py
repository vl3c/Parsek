"""Mission science_bench_recover: THE CAREER-EARNING ATOM, via RAW kRPC (NO MechJeb).

A crewed pad-hop craft flies B1's proven profile, lands under its canopy, runs its
science experiments, transmits the data, and is recovered. That is the whole
mission, and every one of those four things is OBSERVED before it is claimed.

WHY IT EXISTS. Every career surface this harness could forge before today was a
SPEND: `KscAction`'s four sub-actions research a node, upgrade a facility, hire a
kerbal and accept a contract, and not one of them CREDITS anything. So the two
row families a real career ledger is mostly made of - `ScienceEarning` from a
transmitted or recovered experiment, and the funds credit from a recovered vessel
- were reachable only from a hand-played save. `docs/dev/plans/career-ledger-
coverage.md` recorded that as a hard ceiling ("a forged career can NEVER cover
science ordering"). This mission is what lifts it.

THE FLIGHT LEG IS DELEGATED, NOT REWRITTEN. `mlib.sbr_decide` holds a real
`B1State` and hands every pre-landing frame to `mlib.b1_decide` verbatim, so the
pad hop this mission flies is byte-for-byte the one B1 has already proven on this
craft - including the chute-arming window that cost three flights to get right.
What SBR adds is strictly the tail that begins at B1's LANDED terminal:
COLLECT -> TRANSMIT -> RECOVER -> RECOVERED. The `missionParams` for the flight
half are the SAME KEYS `b1_pad_hop` declares, for the same reason.

WHAT MAKES THE THREE NEW VERBS HONEST. All three are the exact
commanded-vs-observed shape that produced the B11 executor, the B-DOCK docking-AP
and the EVA-4 ladder defects. `Experiment.Run()` succeeds on a module whose
conditions are not met and stores nothing; `Experiment.Transmit()` succeeds on a
craft with no antenna, no ElectricCharge or no connection and credits nothing;
`Vessel.Recover()` on a craft that then fails to credit funds is a craft that
broke up. So the machine gates on `science_data_count`, on the CAREER SCIENCE
POOL rising, and on (the craft OBSERVED gone AND the CAREER FUNDS POOL risen) -
never on a latch saying we asked. See the SBR_* block in mlib for the five named
wrong-outcome terminals and the three named channel flakes.

WHAT THIS MISSION DOES NOT VERIFY, declared in `mlib.MISSION_HANDOFF_CONTRACTS`:
it has no view of Parsek at all. A green MISSION-OK here means KSP credited the
science and the funds. Whether a `ScienceEarning` row or a recovery credit landed
in the LEDGER - the entire reason the flight is worth flying - is owned by
CommitTree, the analyzer and the M-B2 ledger oracle, and the handoff block says
so in the verdict line so a forge run can never be misread as "the forge worked".

This is a THIN shell: every decision is the pure `mlib.sbr_decide` /
`mlib.evaluate_sbr_assertions`; the flight, connect, logging and result write are
the shared `mission_runner` runtime. ``import krpc`` never happens at module top,
so this module imports clean on the base interpreter (no venv), which is what lets
unittest discovery and the fake-telemetry tests import it without krpc.

GPLv3 (a derivative of the kRPC client; see mission_runner). ASCII only.
"""

from __future__ import annotations

import os
import sys
from typing import List, Optional

# Self-sufficient path bootstrap: as a subprocess this file's dir (missions/) is
# sys.path[0]; put it on the path so ``import mission_runner`` resolves, and
# mission_runner puts missions/lib on the path for mlib.
_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import mission_runner  # noqa: E402
import mlib  # noqa: E402

MISSION_NAME = "science_bench_recover"


def build_state(params: dict):
    """Build the mlib SBR phase-machine initial state from the missionParams dict."""
    return mlib.sbr_initial_state(mlib.sbr_params_from_dict(params))


def decide(state, snapshot):
    return mlib.sbr_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # All four rows read OBSERVED state carried out of the machine, never a
    # commanded latch, and never the post-terminal frames (after RECOVERED the
    # craft does not exist).
    return mlib.evaluate_sbr_assertions(frames, mlib.sbr_params_from_dict(params), state)


def make_control() -> mission_runner.MissionControl:
    # Raw kRPC, no MechJeb: the flight leg is B1's pad hop (a throttle, one stage
    # activation and a chute), and nothing in the career tail needs an autopilot.
    #
    # read_chute=True is INHERITED FROM B1 AND LOAD-BEARING for the same reason it
    # is there: the delegated machine's canopy latch and its DOWN terminal gate on
    # the OBSERVED ParachuteState, so dropping the flag would silently disarm the
    # proven half of this mission.
    #
    # read_science=True is what arms this lane at all: without it all six career
    # channels stay at their UNREAD sentinels, every gate in the tail fails closed,
    # and the mission flakes `science-channel-dark` six frames after it lands.
    #
    # tolerate_unreadable_nodes=True is the SECOND opt-in this lane needs, and it is
    # MEASURED rather than argued. `L3-career-science-recover`'s first reading run
    # (`2026-08-19_1817` / `_1818_a2`, both attempts identical) died in 1.2 s at
    # PRELAUNCH: this mission flies a CAREER save, kRPC's maneuver-node read raises
    # `Maneuver node editing is not available` on an un-upgraded Tracking Station,
    # and three consecutive raises escalate to a `vessel_lost` snapshot, which the
    # delegated B1 leg correctly condemns as `flight-leg vessel-lost (unreadable
    # after repeated telemetry failures)`. CL-3's own `make_control` already states
    # the identical finding word for word ("it polls on the pad in a career save,
    # where kRPC refuses the maneuver-node read, and without it the mission dies
    # vessel-lost in its first phase"); this lane is the second career flier and
    # the second to need it.
    #
    # WHY IT IS SAFE HERE, stated because the flag's own docstring records that
    # turning it on GLOBALLY broke CL-1: the hazard is a terminal that a PAD frame
    # can satisfy once blind frames become believable, and CL-1's
    # `crew-survived-impact` was exactly that (landed + crew alive, with no
    # has-flown precondition, firing 1.9 s in). This mission has no such terminal.
    # Its flight leg is B1's structured phase progression - PRELAUNCH -> ASCENT
    # (throttle + stage) -> COAST (solid fuel exhausted) -> DESCENT (vertical speed
    # negative) -> LANDED - which a craft sitting on the pad cannot walk, and its
    # `flightCompletedObserved` assertion additionally gates on the peak apoapsis
    # landing inside `apoapsisWindowMeters`, which a pad craft's 0 m never does.
    # The career tail then gates on OBSERVED movements of pools the raise never
    # touched (the career channels read fine on those same frames - the failed run
    # measured `funds=500000.000 science=100.000` on the very snapshot it died on).
    return mission_runner.KrpcMissionControl(use_mechjeb=False, client_name=MISSION_NAME,
                                             read_chute=True, read_science=True,
                                             tolerate_unreadable_nodes=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # settle_frames=0: the RECOVERED terminal means the craft has been REMOVED
    # from the game, so a settle tail would gather only vessel_lost frames. All
    # four assertions are machine-carried evidence, so `evaluate` needs no settled
    # frames at all - the same reasoning CL-1 and B1's DOWN terminal use.
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
