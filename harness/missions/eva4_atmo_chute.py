"""Mission eva4_atmo_chute: fly the b1-pad-craft hop into a mid-air EVA envelope.

Scenario EVA-4-atmo-chute. Flies the SAME pre-placed SRB-plus-chute vessel B1 flies,
with raw kRPC (NO MechJeb): throttle + activate stage (ignite the Flea), burn to fuel
exhaustion, coast over apoapsis, arm the CRAFT's chute on the way down -- then STOP,
still airborne and crewed, the first frame the craft is inside a verified-safe EVA
envelope, and hand off to the command seam.

WHY THE MISSION STOPS MID-AIR. kRPC exposes no EVA API, and a scenario spec may declare
exactly ONE mission-kind step (hlib: "kind 'autopilot' requires exactly one mission-kind
step"). So the EVA itself, the kerbal's chute, and the kerbal's descent are all SEAM
work (EvaExit -> EvaChuteDeploy). This mission's only product is the HANDOFF STATE: a
live, crewed craft decelerating under its own canopy inside a bounded altitude band, so
the seam's irreversible EvaExit fires into a known-safe envelope instead of a guess.

The window is self-regulating rather than a golden altitude: it requires the craft's own
chute to READ Deployed AND |vertical speed| under a bound, so it opens where the canopy
actually bites. Missing it (sinking past the floor) is a bounded, NAMED ASSERT-FAIL,
never a silent burn-down of the descent budget.

FLIGHT-1 (2026-07-24) reshaped two things; the full evidence is in the EVA4_* comment
block in mlib.py. (1) The craft's chute is ARMED WHILE SLOW at the apoapsis crossing,
not at an altitude on the way down: at ~300 m/s stock refuses to open it at all
(automateSafeDeploy = 0 + DeploySafe unsafe), so a low arm is inert, not late. (2) The
window gates on the chute's OBSERVED kRPC state, never on the machine's own "we
commanded it" latch - which was true for the whole failed flight.

This is a THIN shell: every decision is the pure ``mlib.eva4_decide`` phase machine and
``mlib.evaluate_eva4_assertions``; the flight, connect, logging, and result write are
the shared ``mission_runner`` runtime. ``import krpc`` never happens at module top -- it
is lazy inside ``mission_runner.KrpcMissionControl.open`` -- so this module imports clean
on the base interpreter, which is what lets unittest discovery import it without krpc.

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

MISSION_NAME = "eva4_atmo_chute"


def build_state(params: dict):
    """Build the mlib EVA-4 phase-machine initial state from the missionParams dict."""
    return mlib.eva4_initial_state(mlib.eva4_params_from_dict(params))


def decide(state, snapshot):
    return mlib.eva4_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # Every assertion reads machine-carried handoff evidence (the terminal skips the
    # settle tail because the craft is still descending and the seam is waiting), so the
    # terminated state is passed through.
    return mlib.evaluate_eva4_assertions(frames, mlib.eva4_params_from_dict(params), state)


def make_control() -> mission_runner.MissionControl:
    # Raw kRPC (no MechJeb), exactly like B1: the Flea hop needs no ascent autopilot.
    # read_chute=True is the FLIGHT-1 FIX: this mission's terminal depends on the craft's
    # canopy being OBSERVED open (kRPC ParachuteState), not on having commanded it. On
    # flight 1 the command succeeded, the canopy never opened, and the machine had no way
    # to tell. Every other mission leaves the read off and keeps its snapshot unchanged.
    return mission_runner.KrpcMissionControl(use_mechjeb=False, client_name=MISSION_NAME,
                                             read_chute=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # settle_frames=0: the terminal hands a STILL-DESCENDING craft to the seam and the
    # next seam command (EvaExit) is time-critical -- every settle sample spends altitude
    # the kerbal needs for its own canopy. The machine also sets skip_settle_tail on the
    # EVA-WINDOW terminal; this makes the intent explicit at the spec level too.
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
