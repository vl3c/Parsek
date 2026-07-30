"""Mission v1_map_dwell: the V1 visual-validation map-dwell lane
(design-testing-unified section 6, V1) -- aim the SHIPPED render parity oracle
(MapRenderProbe + RenderParityOracle) at REAL flown-mission geometry ACROSS
TIME, instead of the one-frame synthetic fixtures S1.6/S1.7 watch.

The flight leg is NOT re-implemented. This mission COMPOSES the live-proven
B11 profile (``mlib.b5_decide`` with captureEnabled -- ascent, ManeuverPlanner
Hohmann transfer, autowarped TLI, the Kerbin->Mun SOI crossing, capture burn,
held park, mid-mission CommitTree; four full B11 flights on record) by carrying
a nested ``B5State`` and delegating every FLIGHT frame to it verbatim. On its
clean ORBIT-COMMITTED terminal the R1-proven rewind tail runs, then the NEW
dwell tail:

    FLIGHT (delegated b5_decide, captureEnabled)
      -> STOP            (seam StopRecording; the R1 flight-1 lesson)
      -> RECORDER-IDLE   (seam RecordingState, until recording=false OBSERVED)
      -> REWIND          (seam InvokeRewind rp=<id> slot=<n>)
      -> VERIFY          (the OBSERVED backward-clock gate)
      -> DWELL-CAMERA    (kRPC map camera staged; OBSERVED mode readback)
      -> DWELL-WARP-IN   (native warp to just past the flown launch)
      -> DWELL-HOLD-1X   (densest per-frame probe sampling window)
      -> DWELL-WARP-RAMP (rails stair up and back down, SOI-guarded)
      -> DWELL-SOI-WARP  (native warp to the recorded crossing minus lead)
      -> DWELL-SOI-CROSS (held moderate rails across the recorded boundary UT)
      -> DWELL-DONE      (terminal)

WHY THE REWIND LEG IS LOAD-BEARING (do not "simplify" it away): a committed
tree in normal forward play draws NO map ghosts -- PlaybackScopeTracker
(BUG-B, historical-not-replayed) keeps every committed recording dormant, and
BDOCK-1's archived log proves it (674 post-commit probe frames, every one
ghosts=0). The rewind lands the playhead BEFORE the flown launch, the flown
tree re-enters replay scope, and warping forward REPLAYS the real recorded
geometry as ghosts in front of the per-frame MapRenderProbe. The machine
carries that precondition as an OBSERVED assertion row
(``rewoundBeforeFlightStart``).

WHAT THIS MISSION DOES NOT DO (by construction):
  - It never flies the rewound craft. The craft sits PRE_LAUNCH on the pad
    (landed => every rails factor is legal) while the GHOSTS do the flying.
    The re-fly provisional therefore stays EMPTY, so the scenario's
    post-mission step MUST be ``AnswerMergeDialog choice=discard`` -- merging
    an empty provisional is the known R1-EMPTY-PROVISIONAL defect and reds the
    forbidden-ERROR contract.
  - It measures nothing about rendering itself. The probe + parity oracle
    (C#-side, armed by the scenario's SetSetting mapRenderTracing=true +
    verboseLogging=true steps) do the measuring; the scenario's logContract
    pins on the probe's own summary lines are the gate. The mission only
    stages the camera, the clock and the warp -- and asserts the OBSERVED UT
    milestones that make an empty dwell impossible to green.

This is a THIN shell: every decision is the pure ``mlib.v1_map_dwell_decide``
machine and ``mlib.evaluate_v1_map_dwell_assertions``; the flight, connect,
logging, seam transport and result write are the shared ``mission_runner``
runtime. ``import krpc`` is lazy inside ``mission_runner`` (never at module
top), so this module imports clean on the base interpreter (no venv).

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

MISSION_NAME = "v1_map_dwell"


def build_state(params: dict):
    """Build the mlib V1 machine initial state (which carries a fresh nested
    B11 machine built from the SAME missionParams dict)."""
    return mlib.v1_map_dwell_initial_state(
        mlib.v1_map_dwell_params_from_dict(params))


def decide(state, snapshot):
    return mlib.v1_map_dwell_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # Every row is machine-CARRIED evidence stamped on the frame that produced
    # it (the B5/B6/R1 pattern): the delegated B11 rows over the nested flight
    # state, the rewind rows, then the dwell rows.
    return mlib.evaluate_v1_map_dwell_assertions(
        frames, mlib.v1_map_dwell_params_from_dict(params), state=state)


def make_control() -> mission_runner.MissionControl:
    # The B11 control VERBATIM (the delegated flight leg is the same flight,
    # so the same opt-in channels are load-bearing: read_docking for the PARK
    # tumble gate, read_node_executor for the capture supervisor,
    # read_periapsis for the capture-mode flyby warp) PLUS read_camera=True --
    # the V1 dwell gates on the OBSERVED camera_mode readback, never on the
    # camera commands having been issued.
    return mission_runner.KrpcMissionControl(
        use_mechjeb=True, client_name=MISSION_NAME, read_docking=True,
        read_node_executor=True, read_periapsis=True, read_camera=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # Inherited from B11 verbatim for the flight leg; the dwell tail then
    # DRIVES rails deliberately (the stair + the crossing hold), which the
    # same permission covers.
    allow_rails_warp=True,
    max_physics_warp=4.0,
    # No settle tail (the B5/B6/R1 SF-4 contract): every assertion is
    # machine-carried evidence and evaluate discards the frames.
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
