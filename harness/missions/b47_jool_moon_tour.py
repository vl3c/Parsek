"""Mission b47_jool_moon_tour: START PARKED AROUND JOOL with the Kerbin->Jool tree
still live, then park at LAYTHE, then VALL, then TYLO, in ONE recording session,
and COMMIT the tree once, at Tylo.

THE POINT (the Parsek surface, not the rocketry). The multi-moon configuration
hold (M-MIS-6, `ArrivalHold` kind=config) engages only for a recording tree that
enters TWO OR MORE moon SOIs of the same planet. Every flown moon lane parks at
one moon and commits there, so the hold has never had a subject. This mission's
product is that subject: the Kerbin->Jool tree 6ec68e87 (B22's harvest,
`jool-orbit-recorded`) with a Jool->Laythe->Vall->Tylo tail appended to it. The
loop lane `V38M-jool-moon-tour-player-loop` reads the hold off it; nothing in this
file or its spec asserts a hold outcome.

REUSE. Every leg is `mlib.b5_decide` verbatim with a full B5 param table, and each
leg's table is a lane that already flew or is authored beside this one:

    leg 1  Jool   -> Laythe   B25 / B38's orbit-start moon path (OperationTransfer
                              from a Jool park), with the park band tightened so
                              it nests inside leg 2's entry gate
    leg 2  Laythe -> Vall     B26's parent relay (mlib's own escape node, then
                              MechJeb's moon Hohmann from Jool's frame), with an
                              ELLIPTICAL Vall capture (B29's key) to save delta-v
    leg 3  Vall   -> Tylo     the same parent relay one moon further out, again
                              with an elliptical capture

The composer (`mlib.mtour_decide`) drops the mid-mission CommitTree on the park
exit frame of legs 1 and 2 and starts the next leg's fresh machine, which enters
through the ORBIT-START door at the park just held. `mtour_params_from_dict`
refuses a spec whose park bands do not nest inside the next leg's entry gate.

THE FIXTURE PRECONDITION is B38's, deliberately inverted from B26's: the save MUST
carry the committed Kerbin->Jool tree for this vessel, so the seam StartRecording
answers `already=true` and the tour is appended to tree 6ec68e87. The spec pins
that line and forbids `already=false`.

WHAT THE MACHINE ASSERTS: each leg's B5 rows, prefixed `leg<N><Target>.`
(reachedOrbit, startedInHomeOrbit, escapedHomeSoi on the relay legs,
reachedTargetSoi, flybyPeriapsisFloor, capturedInTargetOrbit, parkedStable; the
final leg adds treeCommitted), plus `tourCompleted`.

This is a THIN shell: every decision is the pure `mlib` machine; the flight,
connect, logging and result write are the shared `mission_runner` runtime.
`import krpc` is lazy inside `mission_runner`, so this module imports clean on
the base interpreter.

GPLv3 (a derivative of the kRPC client; see mission_runner). ASCII only.
"""

from __future__ import annotations

import os
import sys
from typing import List, Optional

_MISSIONS = os.path.dirname(os.path.abspath(__file__))
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)

import mission_runner  # noqa: E402
import mlib  # noqa: E402

MISSION_NAME = "b47_jool_moon_tour"


def build_state(params: dict):
    """Build the mlib moon-tour state from the missionParams (``legs``)."""
    return mlib.mtour_initial_state(mlib.mtour_params_from_dict(params))


def decide(state, snapshot):
    return mlib.mtour_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # Every row's evidence is machine-carried (each leg's B5 state); the frames
    # ride the shared evaluate seam unused, as on b26.
    return mlib.evaluate_mtour_assertions(
        frames, mlib.mtour_params_from_dict(params), state=state)


def make_control() -> mission_runner.MissionControl:
    # b26's control verbatim: MechJeb for every plan and burn, plus the three
    # opt-in observation channels every capture lane requests (angular velocity
    # for the PARK tumble gate, the NodeExecutor engagement read, the periapsis
    # clock for the in-SOI warp target).
    return mission_runner.KrpcMissionControl(
        use_mechjeb=True, client_name=MISSION_NAME, read_docking=True,
        read_node_executor=True, read_periapsis=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # b26's runtime contract: rails warp for the coasts and both executor
    # autowarps, the shared physics-warp ceiling for the correction flip, and no
    # settle tail after the final commit.
    allow_rails_warp=True,
    max_physics_warp=4.0,
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
