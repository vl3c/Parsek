"""Mission b1_pad_hop_career: B1's pad hop, flown on a CAREER save.

The SAME flight as ``b1_pad_hop`` - the same pure ``mlib.b1_decide`` phase machine,
the same ``b1_pad_hop.evaluate`` assertions and the same param schema (pinned key
for key by ``test_b1_pad_hop_career``) - with exactly one difference in the
control: ``tolerate_unreadable_nodes=True``. It exists so a career lane can fly
the hop without changing the sandbox lanes that fly ``b1_pad_hop`` (B1 and the
rest), whose control keeps the flag's DEFAULT-OFF semantics.

WHY A SHELL AND NOT A PARAM. ``MissionSpec.make_control`` takes no params (it is
built before the spec's ``missionParams`` reach the machine), and the flag's own
docstring (``KrpcMissionControl._read_nodes``) makes the opt-in a per-MISSION
decision with a written safety argument, enforced by the allowlist cell
``test_only_the_career_fliers_opt_in_across_every_mission_shell``. A shell is
that decision in its reviewable form; a spec-level knob would let any spec flip
it without one.

This is a THIN shell: every decision is the pure ``mlib.b1_decide`` /
``mlib.evaluate_b1_assertions`` (through ``b1_pad_hop.evaluate``); the flight,
connect, logging and result write are the shared ``mission_runner`` runtime.
``import krpc`` never happens at module top, so this module imports clean on the
base interpreter (no venv).

GPLv3 (a derivative of the kRPC client; see mission_runner). ASCII only.
"""

from __future__ import annotations

import os
import sys
from typing import List, Optional

# Self-sufficient path bootstrap: as a subprocess this file's dir (missions/) is
# sys.path[0]; put it on the path so ``import mission_runner`` / ``b1_pad_hop``
# resolve, and mission_runner puts missions/lib on the path for mlib.
_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import mission_runner  # noqa: E402
import mlib  # noqa: E402
import b1_pad_hop  # noqa: E402

MISSION_NAME = "b1_pad_hop_career"


def build_state(params: dict):
    """B1's initial state, from B1's params (the schema is B1's, key for key)."""
    return mlib.b1_initial_state(mlib.b1_params_from_dict(params))


def decide(state, snapshot):
    return mlib.b1_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # B1's evaluator verbatim: the DOWN terminal, the OBSERVED canopy latch and the
    # apoapsis window all read exactly as they do in the sandbox lanes.
    return b1_pad_hop.evaluate(frames, params, state)


def make_control() -> mission_runner.MissionControl:
    # Raw kRPC (no MechJeb), read_chute=True for the same LOAD-BEARING reason B1
    # carries it (the craftCanopyObserved row and the DOWN terminal gate on the
    # OBSERVED ParachuteState).
    #
    # tolerate_unreadable_nodes=True is THE reason this shell exists, and it is
    # MEASURED. `QL-2-quickload-career-recovery-not-paid`'s first reading run
    # (`2026-10-07_2231` and its retry `_2232_a2`, identical) flew `b1_pad_hop` on
    # the CAREER fixture `career-pad-craft` (every facility at level 0) and died in
    # 1.2 s at PRELAUNCH: kRPC's maneuver-node read raises `Maneuver node editing is
    # not available` on an un-upgraded Tracking Station, three consecutive raises
    # escalate to a `vessel_lost` snapshot, and B1's machine correctly condemns that
    # as `vessel-lost (unreadable after repeated telemetry failures)` ->
    # MISSION-ASSERT-FAIL. The same finding CL-3 and science_bench_recover recorded.
    #
    # WHY IT IS SAFE HERE (the flag's docstring records that turning it on GLOBALLY
    # broke CL-1, whose `crew-survived-impact` terminal a PAD frame satisfied once
    # blind frames became believable). B1 has no terminal a pad frame can reach:
    #   - its success terminals are LANDED and DOWN, both reachable only from the
    #     DESCENT phase, and DESCENT is reached only through B1's structured phase
    #     progression - PRELAUNCH -> ASCENT (throttle + stage) -> COAST (active-stage
    #     solid fuel exhausted) -> DESCENT (vertical speed negative) - which a craft
    #     sitting on the pad cannot walk;
    #   - DOWN additionally needs the canopy OBSERVED Deployed on consecutive DESCENT
    #     frames (the chute is stowed until the apoapsis-crossing arm);
    #   - the `apoapsisWindow` assertion gates on the PEAK apoapsis landing inside
    #     `apoapsisWindowMeters` (QL-2 declares 6000..30000 m), which a pad craft's
    #     ~0 m never does, so even a misread terminal could not resolve MISSION-OK.
    # The node read is the only field the tolerance degrades, and B1 never reads
    # node_count / node_dv / node_ut at all, so a degraded frame changes no decision.
    return mission_runner.KrpcMissionControl(use_mechjeb=False, client_name=MISSION_NAME,
                                             read_chute=True,
                                             tolerate_unreadable_nodes=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
