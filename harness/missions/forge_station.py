"""Mission forge_station: the FIXTURE-FORGE runner (B-DOCK design section 2, plus
the 2026-07-22 operator-principle override that replaces the operator fixture
flight with a headless forge run).

Boots an EXISTING valid save (so the command-seam LoadGame passes on its active
vessel), launches the docking-variant craft onto the pad via kRPC
``launch_vessel``, waits for the spawned vessel to settle PRELAUNCH, and exits
MISSION-OK. The scenario's post-mission seam steps (SaveGame + FlushAndQuit)
persist the pad state; the ``harness/tools/harvest_bdock_station.py`` tool then
prunes Parsek state and normalizes the produced save into the committed
pre-placed-Station fixture (``harness/fixtures/saves/bdock-station-pad``).

This is NOT a flight mission (no ascent / orbit): it exists only to STAMP a pad
fixture headlessly. It is GENERIC over the craft (``craftName``) and an optional
crew count, so the SAME forge later produces the EVA-3 pad fixture (same Kerbal X
craft, a 3-crew pod) with a different ``missionParams``.

A THIN shell: every decision is the pure ``mlib.forge_decide`` machine +
``mlib.evaluate_forge_assertions``; the connect / launch / settle / result write
are the shared ``mission_runner`` runtime. ``import krpc`` is lazy inside
``mission_runner`` (never at module top), so this module imports clean on the base
interpreter (no venv).

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

MISSION_NAME = "forge_station"


def build_state(params: dict):
    return mlib.forge_initial_state(mlib.forge_params_from_dict(params))


def decide(state, snapshot):
    return mlib.forge_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # The machine state carries the phase evidence (launched / settled); the
    # final-situation check reads the frames.
    return mlib.evaluate_forge_assertions(
        frames, mlib.forge_params_from_dict(params),
        phases_reached=tuple(getattr(state, "phases_reached", ()) or ()))


def make_control() -> mission_runner.MissionControl:
    # No MechJeb, no docking telemetry: the forge only calls launch_vessel and
    # reads the settle situation. use_mechjeb=False keeps the connection minimal.
    return mission_runner.KrpcMissionControl(use_mechjeb=False, client_name=MISSION_NAME)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # The forge never warps: it launches and settles on the pad at 1x. Any warp
    # state is an anomaly the guard flakes.
    allow_rails_warp=False,
    max_physics_warp=0.0,
    # No settle-tail reads after SETTLED: the settle-debounce inside the machine
    # already required K consecutive PRELAUNCH frames, and post-terminal reads
    # only add transient-failure surface.
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
