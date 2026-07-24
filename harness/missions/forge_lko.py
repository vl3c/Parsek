"""Mission forge_lko: the ORBITAL FIXTURE-FORGE runner (the EVA-2 sibling of
``forge_station``).

Boots an EXISTING valid save (so the command-seam LoadGame passes on its active
vessel), launches the craft onto a clear pad via kRPC ``launch_vessel`` WITH
NAMED CREW, flies the LIVE-PROVEN B-DOCK Interceptor-leg shape to a circular
Kerbin park (MechJeb ascent -> circularization node -> the two-step separation
contract), holds the parked orbital stage stable, and exits MISSION-OK. The
scenario's post-mission seam steps (SaveGame + FlushAndQuit) persist the orbital
state; ``harness/tools/harvest_bdock_station.py --target-name eva2-lko-crewed
--expect-situation ORBITING`` then prunes Parsek state and normalizes the produced
save into the committed crewed-LKO fixture EVA-2-orbital-board consumes.

WHY NOT ``forge_station``: that forge ends on the pad by design (its machine has
no ascent). WHY NOT a new ascent: there is none here -- the ascent, the
circularization, the two-step separation and the attitude hold are the SAME
``mlib`` action builders and the SAME pure separation-evidence counter B-DOCK has
flown many times; ``forge_lko`` only adds the LAUNCH-with-crew entry (from
``forge_station``) and the PARK dwell that makes the SAVED state a clean start
state instead of a moment mid-flight.

VEHICLE CONFIGURATION CONTRACT: the fixture is the ORBITAL STAGE ONLY (spent
lifter separated, orbital engine lit and verified, throttle cut, nodes cleared,
SAS + RCS holding attitude) on a stable, on-rails-safe circular orbit. See the
MISSION PROFILE step list in ``scenarios/FORGE-eva2-lko.toml``.

This is NOT a coverage flight (it commits no recording): it exists only to STAMP
a fixture headlessly. It is GENERIC over the craft (``craftName``), the named
crew (``crewNames`` -- explicit kerbal names, never a count, since kRPC 0.5.4 has
no roster-enumeration API) and the park altitude, so the SAME forge produces any
future crewed-orbit fixture from a different ``missionParams``.

A THIN shell: every decision is the pure ``mlib.forge_lko_decide`` machine +
``mlib.evaluate_forge_lko_assertions``; the connect / launch / flight / result
write are the shared ``mission_runner`` runtime. ``import krpc`` is lazy inside
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

MISSION_NAME = "forge_lko"


def build_state(params: dict):
    return mlib.forge_lko_initial_state(mlib.forge_lko_params_from_dict(params))


def decide(state, snapshot):
    return mlib.forge_lko_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # The terminated machine state carries the separation evidence (split
    # confirmed + orbital engine ignited) the frames cannot; the orbit-quality
    # half reads the frames (the B2 evaluator verbatim).
    return mlib.evaluate_forge_lko_assertions(
        frames, mlib.forge_lko_params_from_dict(params),
        phases_reached=tuple(getattr(state, "phases_reached", ()) or ()),
        state=state)


def make_control() -> mission_runner.MissionControl:
    # KRPC.MechJeb for the ascent (AscentAutopilot) + the circularization node
    # executor. read_docking=True is NOT about docking here: vessel_count (the
    # separation split evidence), angular_velocity (the park tumble gate) and the
    # SAS/RCS readbacks ride that same opt-in telemetry block. read_crew=True
    # adds the one crew-count RPC the crewed-fixture contract is certified on.
    return mission_runner.KrpcMissionControl(
        use_mechjeb=True, client_name=MISSION_NAME, read_docking=True,
        read_crew=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # RAILS warp is permitted: the circularization node is handed to MechJeb's
    # node executor with autowarp EXPLICITLY on (B-DOCK flight-12 lesson -- the
    # executor's autowarp is shared global state, so an unset one warps or
    # coasts at 1x by luck). Physics-warp cap is the same MechJeb-ascent
    # rationale as B2/B5/B-DOCK (stock 4x ceiling, no KRPC.MechJeb 0.8.1 toggle).
    allow_rails_warp=True,
    max_physics_warp=4.0,
    # Settle-tail frames KEPT (unlike B5/B-DOCK): the orbit-quality assertions are
    # the B2 debounced-window evaluators over the FRAMES, and the parkedStable row
    # reads the FINAL situation -- exactly the settled post-terminal samples this
    # tail provides. The machine only terminates after a held, stable park, so the
    # tail reads a parked ship, not a transient.
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
