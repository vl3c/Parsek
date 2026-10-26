"""Mission b4_reentry_career: B4's orbit-and-return, flown on a CAREER save.

The SAME pure ``mlib.b4_decide`` phase machine, the same
``mlib.evaluate_b4_assertions`` (through ``b4_reentry.evaluate``) and B4's param
schema plus the career opt-ins (pinned key for key by
``test_b4_reentry_career``), with one difference in the control:
``tolerate_unreadable_nodes=True``. It exists so a career lane (CS-4) can fly to
orbit and back on an un-upgraded Tracking Station without changing the sandbox
lanes that fly ``b4_reentry`` (B4 and the rest), whose control keeps the flag's
DEFAULT-OFF semantics.

WHAT THE CAREER SAVE TAKES AWAY, and the opt-ins that answer it (all spec keys of
the B4 machine, off by default, declared only in THIS shell's schema):
  - kRPC refuses the maneuver-node read below Tracking Station level 0.2, so the
    control tolerates it (below);
  - MechJeb cannot circularize without patched conics ("MechJeb is unable to
    circularize without an upgraded Tracking Station"), so MechJeb flies the
    ascent only (``skipCircularization``) and the machine circularizes natively
    (``circularizeMode = "native"``, RF-13's pitch program);
  - the ascent computer's availability in a career save is a reading run's
    question, so the launch is staged only after the autopilot READS enabled
    (``mjEngageConfirmFrames``), and an ``autostageLimit`` keeps MechJeb's
    autostage off the pod's own separation stage.

WHY A SHELL AND NOT A PARAM: ``MissionSpec.make_control`` takes no params, and
the flag's opt-in is a per-MISSION decision with a written safety argument,
enforced by the allowlist cell
``test_only_the_career_fliers_opt_in_across_every_mission_shell``.

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
import b4_reentry  # noqa: E402

MISSION_NAME = "b4_reentry_career"


def build_state(params: dict):
    """B4's initial state from B4's params (the career opt-ins ride the same dict)."""
    return mlib.b4_initial_state(mlib.b4_params_from_dict(params))


def decide(state, snapshot):
    return mlib.b4_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    # B4's evaluator verbatim: reachedOrbit (the ORBIT phase, which the native
    # circularization enters exactly as the MechJeb one does), the peak-apoapsis
    # floor, the landed / splashed terminal and the OBSERVED canopy.
    return b4_reentry.evaluate(frames, params, state)


def make_control() -> mission_runner.MissionControl:
    # MechJeb for the ascent, kRPC's native AutoPilot for the circularization and
    # the deorbit, read_chute=True for the same LOAD-BEARING reason B4 carries it
    # (craftCanopyObserved gates on the OBSERVED ParachuteState).
    #
    # tolerate_unreadable_nodes=True is MEASURED on three career lanes before this
    # one (QL-2 `2026-10-07_2231`, L3 `2026-08-19_1817`, CL-3): kRPC raises
    # `Maneuver node editing is not available` on an un-upgraded Tracking Station,
    # three consecutive raises escalate to a vessel_lost snapshot, and B4 condemns
    # that as a loss at PRELAUNCH.
    #
    # WHY IT IS SAFE HERE (the flag's docstring records that turning it on
    # GLOBALLY broke CL-1, whose terminal a PAD frame satisfied once blind frames
    # became believable). B4 has no terminal a pad frame can reach:
    #   - its only success terminal is a landed / splashed situation INSIDE
    #     SPLASHDOWN, entered only from REENTRY, entered only from DEORBIT, entered
    #     only from ORBIT, which needs the MechJeb ascent-complete latch (engaged
    #     earlier, now disabled) AND an apoapsis within apoErrorMeters of the
    #     80 km target - a pad craft reads about 0 m;
    #   - the reachedOrbit assertion needs the ORBIT phase and the peak-apoapsis
    #     floor, so even a misread terminal could not resolve MISSION-OK;
    #   - the native circularization reads no node at all, and the default
    #     MechJeb path's node read (ACTION_MJ_EXECUTE_CIRCULARIZATION) is not
    #     taken in native mode.
    # The node read is the only field the tolerance degrades, and B4 decides
    # nothing on node_count / node_dv / node_ut.
    return mission_runner.KrpcMissionControl(use_mechjeb=True, client_name=MISSION_NAME,
                                             read_chute=True,
                                             tolerate_unreadable_nodes=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # B4's warp policy verbatim: its own rails hops, and MechJeb's AscentAutopilot
    # physics warp up to the stock 4x ceiling.
    allow_rails_warp=True,
    max_physics_warp=4.0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
