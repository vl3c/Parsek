"""Mission record_climb_rewind: two powered Jumping Flea hops with a
Rewind-to-Launch between them, via RAW kRPC (NO MechJeb).

WHY IT EXISTS. Lane WR-1 asks whether a stock world-record threshold
(RecordsAltitude / RecordsSpeed / RecordsDistance bands) paid on a COMMITTED
flight is paid AGAIN when a second flight re-crosses it after a Rewind-to-Launch.
That needs two hops and a rewind in one run, and hlib.validate_spec allows exactly
one mission-kind step per autopilot spec, so the second hop and the rewind live
inside this one mission.

THE PHASE PLAN (``mlib.rcr_decide``; every state name is an ``mlib.RCR_*``):

    HOP1        science_bench_recover, delegated VERBATIM (``mlib.sbr_decide``) to
                its RECOVERED terminal; the recovery's scene exit auto-commits the
                hop-1 tree under the scenario's autoMerge
    COMMITTED   seam ListHandles kind=committed until count >= 1
    REWIND      seam InvokeRewindToLaunch tree=latest (from the Space Center)
    SPACECENTER the OBSERVED backward clock (UT below hop 1's recovery UT)
    EDITOR      seam GoToEditor facility=<editorFacility> craft=<craftName>
    LAUNCH      seam LaunchFromEditor
    READY       the relaunched craft reads PRE_LAUNCH
    RECORD      seam StartRecording (holdStartRecording, default on)
    HOLD        1x on the pad until UT >= hop 1's recovery UT +
                holdPastFirstRecoverySeconds, under the live recorder
    HOP2        a FRESH science_bench_recover state, delegated verbatim
    DONE

The block note above ``mlib.RCR_HOP1`` carries the reasons: why the relaunch goes
through the seam, why the hold runs under a live recorder, why READY reads no vessel
name, and how vessel loss and a stopped clock are handled.

WHAT THIS MISSION DOES NOT VERIFY, declared in ``mlib.MISSION_HANDOFF_CONTRACTS``:
it has no view of the ledger. Whether the record band was raised, whether stock paid
the second hop nothing, and whether the ledger credited each threshold once are the
spec's log contracts and the LedgerGroundTruth batch.

This is a THIN shell: every decision is the pure ``mlib.rcr_decide`` /
``mlib.evaluate_rcr_assertions``; the flight, connect, logging, seam transport and
result write are the shared ``mission_runner`` runtime. ``import krpc`` never
happens at module top, so this module imports clean on the base interpreter.

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

MISSION_NAME = "record_climb_rewind"


def build_state(params: dict):
    """Build the mlib RCR phase-machine initial state from the missionParams dict."""
    return mlib.rcr_initial_state(mlib.rcr_params_from_dict(params))


def decide(state, snapshot):
    return mlib.rcr_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # Every row is machine-carried evidence stamped on the frame that produced it;
    # the run straddles two scene reloads, so no row reads the frames.
    return mlib.evaluate_rcr_assertions(frames, mlib.rcr_params_from_dict(params), state)


def make_control() -> mission_runner.MissionControl:
    # science_bench_recover's control, flag for flag: both hops ARE that mission.
    #
    # read_chute=True and read_science=True are load-bearing for SBR's reasons (the
    # delegated canopy latch gates on the OBSERVED ParachuteState; every career gate
    # fails closed without the science / funds channels).
    #
    # tolerate_unreadable_nodes=True for SBR's measured reason: this lane flies a
    # CAREER save (cs-career-new), where kRPC refuses the maneuver-node read on an
    # un-upgraded Tracking Station and three raises read as vessel_lost at
    # PRELAUNCH. WHY IT IS SAFE HERE: the flag's hazard is a terminal a PAD frame can
    # satisfy once blind frames become believable (CL-1). Each hop's only success
    # terminal is SBR's RECOVERED, reached through B1's PRELAUNCH -> ASCENT -> COAST
    # -> DESCENT -> LANDED progression and the observed funds credit, which a pad
    # craft cannot walk, and both hops' SBR rows (the apoapsis-window flight row
    # included) must hold before the machine moves on. The phases between the hops
    # gate on seam verdicts and on the clock, which is read off space_center and not
    # off the vessel; the one pad gate (READY) only moves the machine toward the
    # second hop, and the HOLD's own guards (vessel lost, situation off the pad, a
    # stopped clock) can only end the run, never certify it.
    #
    # No other opt-in: the new phases read ut, situation, vessel_lost and the three
    # generalized seam fields, all in the BASE snapshot. read_vessel_name stays off
    # (the block note above mlib.RCR_HOP1 says why READY needs no name).
    return mission_runner.KrpcMissionControl(use_mechjeb=False, client_name=MISSION_NAME,
                                             read_chute=True, read_science=True,
                                             tolerate_unreadable_nodes=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # NO WARP anywhere, which is what science_bench_recover permits: both hops are
    # 1x by construction, and the hold must run at 1x (a fresh launch holds the
    # TIMEWARP lock under physicsHold, which WF-1 measured).
    # settle_frames=0 (the KXRW / R1 precedent): every assertion is machine-carried
    # evidence, the run straddles scene reloads, and DONE follows a recovery that
    # removed the craft.
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
