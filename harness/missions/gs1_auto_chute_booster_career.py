"""Mission gs1_auto_chute_booster_career: GS-1's two-stage flight on a CAREER save.

The SAME flight as ``gs1_auto_chute_booster`` - the same pure ``mlib.gs1_decide``
phase machine, the same ``gs1_auto_chute_booster.evaluate`` assertions and the
same param schema (pinned key for key by ``test_gs1_auto_chute_booster_career``)
- with exactly one difference in the control: ``tolerate_unreadable_nodes=True``.
It exists so a career lane can fly the two-stage profile without changing the
sandbox lanes that fly ``gs1_auto_chute_booster`` (GS-1, QL-4 / QL-4b / QL-4c,
RF-1, RF-4, CA-1), whose control keeps the flag's DEFAULT-OFF semantics.

WHY A SHELL AND NOT A PARAM: the same reason as ``b1_pad_hop_career`` -
``MissionSpec.make_control`` takes no params, and the opt-in is a per-MISSION
decision with a written safety argument, enforced by the allowlist cell
``test_only_the_career_fliers_opt_in_across_every_mission_shell``.

This is a THIN shell: every decision is the pure ``mlib.gs1_decide`` /
``mlib.evaluate_gs1_assertions`` (through ``gs1_auto_chute_booster.evaluate``);
the flight, connect, logging and result write are the shared ``mission_runner``
runtime. ``import krpc`` never happens at module top, so this module imports
clean on the base interpreter (no venv).

GPLv3 (a derivative of the kRPC client; see mission_runner). ASCII only.
"""

from __future__ import annotations

import os
import sys
from typing import List, Optional

# Self-sufficient path bootstrap: as a subprocess this file's dir (missions/) is
# sys.path[0]; put it on the path so ``import mission_runner`` /
# ``gs1_auto_chute_booster`` resolve, and mission_runner puts missions/lib on the
# path for mlib.
_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import mission_runner  # noqa: E402
import mlib  # noqa: E402
import gs1_auto_chute_booster  # noqa: E402

MISSION_NAME = "gs1_auto_chute_booster_career"


def build_state(params: dict):
    """GS-1's initial state, from GS-1's params (the schema is GS-1's, key for key)."""
    return mlib.gs1_initial_state(mlib.gs1_params_from_dict(params))


def decide(state, snapshot):
    return mlib.gs1_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # GS-1's evaluator verbatim: every row reads the same machine-carried evidence.
    return gs1_auto_chute_booster.evaluate(frames, params, state)


def make_control() -> mission_runner.MissionControl:
    # Raw kRPC (no MechJeb), read_chute=True and read_vessel_name=True for exactly
    # the reasons GS-1's own make_control states (the OBSERVED canopy row; the
    # focusImpactAtExit handoff channel).
    #
    # tolerate_unreadable_nodes=True is THE reason this shell exists. It is the
    # finding QL-2 MEASURED on `b1_pad_hop` (`2026-10-07_2231` / `_2232_a2`: kRPC
    # raises `Maneuver node editing is not available` on a career whose Tracking
    # Station is at level 0, three consecutive raises escalate to a `vessel_lost`
    # snapshot, and the mission dies MISSION-ASSERT-FAIL at PRELAUNCH in 1.2 s),
    # applied BEFORE `QL-2b-quickload-booster-career-reward-not-paid` flies GS-1's
    # profile on `career-gs1-two-stage-pad` (every facility at level 0) into it:
    # GS-1's machine condemns a vessel_lost frame in every phase exactly as B1's does.
    #
    # WHY IT IS SAFE HERE (the flag's docstring records that turning it on GLOBALLY
    # broke CL-1, whose `crew-survived-impact` terminal a PAD frame satisfied once
    # blind frames became believable). The tolerance turns a career pad frame into
    # the frame a SANDBOX flight of this mission already delivers (there the node
    # read succeeds), and GS-1's machine never reads node_count / node_dv / node_ut,
    # so it decides on a career pad exactly as it has on every green sandbox flight.
    # Terminal by terminal, none is reachable from the pad:
    #   - LANDED (via SIBLING-DOWN, or directly with no sibling name) is gated on
    #     `airborne_seen`, an OBSERVED FLYING / SUB_ORBITAL / ORBITING / ESCAPING
    #     situation - the gate written because KSP MEASURED `situation = LANDED` at
    #     230 m climbing at 113 m/s on flight 1, CL-1's defect closed in advance -
    #     and on reaching DESCENT through ASCENT -> STAGE -> COAST;
    #   - every SIBLING-DOWN exit (the nominal landed / gone pair, and QL-2b's
    #     inverted `siblingAirborneAtExit` exit) needs the booster OBSERVED present,
    #     and the booster does not exist until the decoupler fires; the inverted exit
    #     further needs two consecutive real airborne readings of it;
    #   - IMPACTED (focusImpactAtExit only) needs DESCENT after the OBSERVED
    #     separation (available thrust falling from a real positive pre-stage
    #     reading after the stage click);
    #   - and the assertions gate on `separationObserved` and on the PEAK apoapsis
    #     inside `apoapsisWindowMeters` (QL-2b declares 600..1600 m), neither of
    #     which a craft still on the pad can produce.
    return mission_runner.KrpcMissionControl(use_mechjeb=False, client_name=MISSION_NAME,
                                             read_chute=True, read_vessel_name=True,
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
