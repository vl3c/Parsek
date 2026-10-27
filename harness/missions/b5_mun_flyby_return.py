"""Mission b5_mun_flyby_return: B5's Mun flyby, then a Kerbin return that lands.

The scripted campaign's CS-5 ("Mun flyby and return"). B5 ends at its RETURN
terminal, the frame the craft is back in Kerbin's SOI; nothing lands. This
mission runs B5's pure machine VERBATIM to that terminal, then trims the Kerbin
periapsis into a reentry window with one horizontal burn, hands the craft to B4's
pure machine VERBATIM at its REENTRY (the service-stage drop owed, the bounded exo
hops, the chute, the splashdown terminal), and takes one native warp down the
hours-long fall in between. Every decision is ``mlib.mfr_decide`` (the block note
above ``mlib.MFR_FLYBY`` carries the plan and the reasons); every assertion is
``mlib.evaluate_mfr_assertions``.

WHY A NEW MISSION AND NOT A B5 PARAM: hlib.validate_spec allows one mission step
per autopilot spec, and the return half is B4's machine, not B5's. Composing the
two leaves ``b5_decide`` and ``b4_decide`` untouched, so every lane that flies
either is byte-identical by construction (the V1 / RCR wrapper precedent).

THE CONTROL: B5's (MechJeb for the ascent and the transfer half) plus two reads
the tail needs. read_chute=True is B4's LOAD-BEARING read (craftCanopyObserved
gates on the OBSERVED ParachuteState). read_periapsis=True feeds the fall warp's
periapsis clock; B5's flyby walk reads that channel only in its capture and relay
modes, which mfr_params_from_dict refuses, so the flyby decisions are unchanged.
tolerate_unreadable_nodes stays OFF: the career host (cs-career-ts1) has the
Tracking Station at level 1, so the vessel carries a patched-conic solver and kRPC
reads nodes; a refusal there is a real fault, never a blind frame to tolerate.

This is a THIN shell; ``import krpc`` never happens at module top, so this module
imports clean on the base interpreter (no venv).

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

MISSION_NAME = "b5_mun_flyby_return"


def build_state(params: dict):
    """The composed machine's initial state, inside B5's own PRELAUNCH."""
    return mlib.mfr_initial_state(mlib.mfr_params_from_dict(params))


def decide(state, snapshot):
    return mlib.mfr_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # B5's four rows over the delegated B5 state, then the return window and B4's
    # landed / canopy rows over the delegated B4 state.
    return mlib.evaluate_mfr_assertions(frames, mlib.mfr_params_from_dict(params), state)


def make_control() -> mission_runner.MissionControl:
    return mission_runner.KrpcMissionControl(use_mechjeb=True, client_name=MISSION_NAME,
                                             read_chute=True, read_periapsis=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # B5's warp policy (its rails hops and the NodeExecutor autowarp; MechJeb's
    # ascent physics warp up to the stock 4x ceiling), which also covers B4's hops
    # and the fall warp.
    allow_rails_warp=True,
    max_physics_warp=4.0,
    # B4's settle tail (the default): landedSituation reads the final frame, so
    # the splashed craft is read settled.
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
