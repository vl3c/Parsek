"""Mission gs2_orbital_probe_deploy: the in-orbit deploy, via RAW kRPC.

Flies nothing. The fixture (`gs2-orbital-stack`, stamped by FORGE-gs2-orbital-stack)
already holds the ATTACHED two-controllable stack parked on a stable ~100 km
circular Kerbin orbit, so this mission is four phases long: settle the loaded
state, fire ONE stage - which blows the istg=2 decoupler IN ORBIT and drops the
spent Rockomax core off the crewed pod stack - then dwell long enough for the
recorder to author real post-split surfaces on BOTH vessels, then stop.

Focus stays on the pod stack (KSP keeps the root part's vessel active), which is
the whole point of the lane: the RewindPoint the split authors stamps
FocusSlotIndex at the FOCUS vessel's slot, and Parsek's R5 classifier then treats
the two slots differently. NONE of that is asserted here - the mission's contract
is only that the gameplay moment happened and that the focus vessel was still
orbiting safely when it did. Everything about RewindPoints, MergeStates and the
reaper is asserted by the SPEC (GS-2 / GS-3 logContracts + the M-C2 saveParse
block), which is the mission-vs-Parsek orthogonality the harness README describes.

NOT a handoff mission: it terminates on exactly the outcome it certifies, so it
is deliberately absent from ``mlib.MISSION_HANDOFF_CONTRACTS`` (every mission but
EVA-4 is).

This is a THIN shell: every decision is the pure ``mlib.gs2_decide`` phase machine
and ``mlib.evaluate_gs2_assertions``; the flight, connect, logging, and result
write are the shared ``mission_runner`` runtime. ``import krpc`` never happens at
module top -- it is lazy inside ``mission_runner.KrpcMissionControl.open`` -- so
this module imports clean on the base interpreter (no venv), which is what lets
the unittest discovery + the fake-telemetry tests import it without krpc.

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

MISSION_NAME = "gs2_orbital_probe_deploy"


def build_state(params: dict):
    """Build the mlib GS-2 phase-machine initial state from the missionParams dict."""
    return mlib.gs2_initial_state(mlib.gs2_params_from_dict(params))


def decide(state, snapshot):
    return mlib.gs2_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # Four of the five rows read MACHINE-CARRIED evidence rather than re-deriving
    # from the frame tail: the split latch is sticky (a vessel_count read that
    # faults after the split must not erase something that really happened), the
    # activation count is a commanded fact no telemetry scan could recover, and
    # the baseline/peak counts are single-frame facts. Only `periapsisSafe` scans
    # the tail, and it scans for the LAST FINITE read so an unreadable final frame
    # cannot fabricate a pass.
    return mlib.evaluate_gs2_assertions(frames, mlib.gs2_params_from_dict(params),
                                        phases_reached=getattr(state, "phases_reached", ()),
                                        state=state)


def make_control() -> mission_runner.MissionControl:
    # Raw kRPC (no MechJeb): nothing here steers, burns, or executes a node. The
    # fixture is already parked, and the only command the mission issues is one
    # activate_stage. No opt-in telemetry channel is requested - the mission reads
    # only situation / periapsis / vessel_count, all of which are in the base
    # snapshot.
    return mission_runner.KrpcMissionControl(use_mechjeb=False, client_name=MISSION_NAME)


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
