"""Mission cl3_refly_crew_tombstone: rewind onto a CREWED, terminally-Destroyed
recording, RE-FLY it, and hand the post-mission merge a provisional worth
merging -- so that `SupersedeCommit.CommitTombstones` runs over the
`KerbalAssignment` + `KerbalEndState.Dead` ledger row Parsek DERIVES from that
recording at load.

NOTHING IS FLOWN UP FIRST, BECAUSE NOTHING NEEDS TO BE. The tree this mission
rewinds is already committed on disk when the game starts (the injected
`rewind-crew-loss` corpus: `cl-stack-root`, with the crewless probe `cl-probe-b`
in slot 0 and the CREWED pod `cl-pod-a` in slot 1). So this is the R1 rewind loop
with its ASCENT and COMMIT halves cut off the front:

    STOP           (seam StopRecording; the machine's own first act)
      -> RECORDER-IDLE (seam RecordingState, until it READS recording=false)
      -> REWIND        (seam InvokeRewind rp=<id> slot=<n>)
      -> VERIFY        (the OBSERVED backward-clock gate)
      -> REWOUND       (waypoint: throttle up + stage the rewound craft)
      -> RELAUNCH      (OBSERVED: measured altitude gain)
      -> LOOP-POINTS   (seam RecordingState, until it reads points > 0)
      -> LOOP-CLOSED   (terminal)

Every phase keeps R1's semantics exactly; `v1_map_dwell` is the committed
precedent for lifting this tail (it takes STOP / RECORDER-IDLE / REWIND / VERIFY
verbatim). CL-3 differs from V1 in the one place that matters: V1 deliberately
never re-flies and must therefore answer the merge dialog `discard`, while CL-3
re-flies precisely so the merge can be `merge`.

WHY THE RE-FLY LEG IS NOT OPTIONAL. `SupersedeCommit.ValidateSupersedeTarget`
REFUSES to write supersede rows over a provisional with no Points
(`AppendRelations outcome=refused-unflown-provisional ... reason=empty Points`),
and a merge that writes zero supersede rows tombstones nothing. R1 flight 2
(2026-07-26) rewound perfectly, tore down without re-flying, and red the run on
exactly that. RELAUNCH therefore gates on MEASURED altitude gain and LOOP-POINTS
on an observed non-zero point count.

WHAT THE MISSION ASSERTS, AND WHY IT IS NOT THE SEAM'S OWN OK. The rewind is
judged by an OBSERVATION nothing else in a flight can produce: the game clock
RUNNING BACKWARD, corroborated by the vessel's own situation / altitude changing.
`InvokeRewind` answering `verdict=OK` is a COMMANDED reading -- it says the actor
believes it acted -- and it only opens the door to VERIFY; it rides one extra,
strictly-additional assertion row, never as the evidence.

WHAT THIS MISSION DOES NOT DO (by construction):
  - `AnswerMergeDialog` is NOT a mission action. Its completion contract
    (`TestCommandMergeAnswer.DecideAnswerCompletion`) requires the scene to LEAVE
    FLIGHT, which ends the mission's own kRPC telemetry. It belongs in the
    scenario's POST-mission seam steps, exactly like R1's and EVA-4's tails.
  - It proves NOTHING about the tombstone itself. The supersede-row and
    `Tombstoned N career actions (... Kerbal=K ...)` evidence is the scenario's
    logContract + M-C2 save-parse job, not the mission's.
  - `postRewindFlightRecordedSomewhere` is named for exactly what it can see.
    `RecordingState.points` is the LIVE recorder's buffered count for whatever
    recording is live, NOT the re-fly provisional's, and R1 flight 3 (2026-07-26)
    read `points=24 tree=820de77e` while the merge refused
    `provisional=rec_5b0697a6... reason=empty Points`. No seam verb exposes the
    marker's provisional id, so the row carries the reply's `recordedTree` as
    evidence for a human instead of pretending to gate on it.
  - `rewindPointId` must name a RewindPoint that EXISTS in the loaded save.
    `ParsekTestCommandAddon.InvokeRewindImpl` matches `RewindPointId` exactly and
    REJECTS `unknown-rp` otherwise; the injected corpus authors `rp_cl_root`.

This is a THIN shell: every decision is the pure `mlib.cl3_decide` machine and
`mlib.evaluate_cl3_assertions`; the flight, connect, logging, seam transport and
result write are the shared `mission_runner` runtime. `import krpc` is lazy inside
`mission_runner` (never at module top), so this module imports clean on the base
interpreter (no venv).

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

MISSION_NAME = "cl3_refly_crew_tombstone"


def build_state(params: dict):
    """Build the mlib CL-3 machine initial state (at STOP; the first decide frame
    arms the StopRecording command itself, since no phase precedes it)."""
    return mlib.cl3_initial_state(mlib.cl3_params_from_dict(params))


def decide(state, snapshot):
    return mlib.cl3_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # Every row is machine-CARRIED evidence stamped on the frame that produced it
    # (the B5/B6/R1 pattern), so the frames are unused: a post-terminal settle
    # tail cannot retro-satisfy the observed rewind gate.
    return mlib.evaluate_cl3_assertions(
        frames, mlib.cl3_params_from_dict(params), state)


def make_control() -> mission_runner.MissionControl:
    # Raw kRPC, no MechJeb (the CL-1 control): this lane never flies an ascent
    # profile -- the re-fly is a throttle and one stage activation on whatever
    # craft the RewindPoint quicksave puts back. No opt-in channels: a channel the
    # machine does not gate on is an RPC per poll bought for nothing.
    return mission_runner.KrpcMissionControl(use_mechjeb=False,
                                             client_name=MISSION_NAME)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # 1x THROUGHOUT (the B1 / CL-1 contract), which is why both warp knobs keep
    # their strict defaults instead of R1's B2-inherited permissions: this lane
    # has no coast to warp across, and the leg it does fly is the one whose
    # RECORDING has to be non-empty and undistorted for the merge to mean
    # anything. A warped re-fly is a corrupted provisional.
    allow_rails_warp=False,
    max_physics_warp=0.0,
    # No settle tail. Every assertion is machine-carried evidence, and the frames
    # AFTER the terminal are read across a just-reloaded scene where a transient
    # read failure would flip a finished pass into a spurious FLAKE (the B5/B6
    # rationale, sharpened by the reload straddle -- R1's reasoning verbatim).
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
