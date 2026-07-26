"""Mission r1_rewind_loop: fly to a committed orbital state, then drive the
Rewind-to-Separation cycle IN FLIGHT through the generalized command seam.

The flight leg is NOT re-implemented. This mission COMPOSES the live-proven B2
LKO-ascent machine (``mlib.b2_decide``, PASS 2026-07-20) by carrying a nested
``B2State`` and delegating every ASCENT frame to it verbatim; only when that
machine reaches its own clean ORBIT terminal does the rewind cycle start:

    ASCENT (delegated b2_decide)
      -> COMMIT  (seam CommitTree)
      -> REWIND  (seam InvokeRewind rp=<id> slot=<n>)
      -> VERIFY  (the OBSERVED gate)
      -> REWOUND (terminal)

WHAT THE MISSION ASSERTS, AND WHY IT IS NOT THE SEAM'S OWN OK. The rewind is
judged by an OBSERVATION nothing else in a flight can produce: the game clock
RUNNING BACKWARD. A Rewind-to-Separation loads an earlier RewindPoint quicksave,
so kRPC's ``space_center.ut`` reads LOWER after the cycle than the value stamped
before it, corroborated by the vessel's own situation / altitude changing. The
``InvokeRewind`` verb answering ``verdict=OK`` is a COMMANDED reading -- it says
the actor believes it acted -- and it only opens the door to VERIFY; it is
carried as one extra (strictly-additional) assertion row, never as the evidence.

WHAT THIS MISSION DOES NOT DO (PENDING-OPERATOR / by construction):
  - ``AnswerMergeDialog`` is NOT a mission action. Its completion contract
    (``TestCommandMergeAnswer.DecideAnswerCompletion``) requires the scene to
    LEAVE FLIGHT, which ends the mission's own kRPC telemetry. It belongs in the
    scenario's POST-mission seam steps, exactly like EVA-4's EvaExit tail.
  - The corpus / supersede-row / tombstone asserts are the verifier chain's, not
    the mission's.
  - ``rewindPointId`` must name a RewindPoint that EXISTS in the loaded save.
    ``ParsekTestCommandAddon.InvokeRewindImpl`` matches ``RewindPointId`` exactly
    and REJECTS ``unknown-rp`` otherwise, and live RewindPoint ids are fresh
    GUIDs (``RewindPointAuthor.cs``: ``"rp_" + Guid.NewGuid().ToString("N")``).
    See the scenario header for the open blocker.

This is a THIN shell: every decision is the pure ``mlib.r1_decide`` machine and
``mlib.evaluate_r1_assertions``; the flight, connect, logging, seam transport and
result write are the shared ``mission_runner`` runtime. ``import krpc`` is lazy
inside ``mission_runner`` (never at module top), so this module imports clean on
the base interpreter (no venv).

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

MISSION_NAME = "r1_rewind_loop"


def build_state(params: dict):
    """Build the mlib R1 machine initial state (which carries a fresh nested B2
    machine built from the SAME missionParams dict)."""
    return mlib.r1_initial_state(mlib.r1_params_from_dict(params))


def decide(state, snapshot):
    return mlib.r1_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # Every R1 row is machine-CARRIED evidence stamped on the frame that produced
    # it (the B5/B6 pattern), so the frames are unused: a post-terminal settle
    # tail cannot retro-satisfy the observed rewind gate.
    return mlib.evaluate_r1_assertions(frames, mlib.r1_params_from_dict(params), state)


def make_control() -> mission_runner.MissionControl:
    # Same control as B2: the KRPC.MechJeb AscentAutopilot flies the delegated
    # ascent leg, and the seam bridge (configure_seam) rides the same object.
    return mission_runner.KrpcMissionControl(use_mechjeb=True, client_name=MISSION_NAME)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # Inherited from B2 verbatim: the delegated ascent leg is the same flight, so
    # the same warp envelope applies (RAILS permitted on the exo-atmospheric
    # coast; physics warp bounded at MechJeb's own stock 4x ceiling).
    allow_rails_warp=True,
    max_physics_warp=4.0,
    # No settle tail. Every assertion is machine-carried evidence, and the frames
    # AFTER the rewind terminal are read across a just-reloaded scene where a
    # transient read failure would flip a finished pass into a spurious FLAKE
    # (the B5/B6 rationale, sharpened by the reload straddle).
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
