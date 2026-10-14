"""The mission-verdict decision core (M-B1), shared by ``run.py`` and the mission
mutation checker (``mutmission``, MUTATION-CHECK-PHASE-2 PR 3).

``run.py`` used to hold these inline: the mission-result read (JSON parse plus the
``schema`` gate), the driver-stage subkind map, and the driver-validity composition
in ``run_verifiers`` (the autopilot carve-out: pre-mission steps plus the mission
verdict gate validity, post-mission RECORDING steps do not, and an unmet
post-mission OUTCOME step becomes the ``missionOutcome`` row and the
``mission_outcome_unmet`` verifier fact). They moved here unchanged so the mutation
checker replays the SAME code a flight runs. ``run.py`` keeps the file read, the
logging and the R10 unresolved-handle override that follows the composition.

Pure: no file I/O, no clock, no logging. Stdlib + ``hlib`` only; this module must
never import the mission library (``run.py`` does not link it).
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from typing import Dict, List, Optional

import hlib

# The mission-result JSON schema the harness accepts (design Data Model "Mission
# result": schema = 1). An INLINE mirror of mlib.MISSION_RESULT_SCHEMA; a result
# carrying a different schema is treated as unreadable (fail-closed), never
# mis-parsed.
MISSION_RESULT_SCHEMA = 1


def parse_mission_result_text(text: Optional[str]) -> Optional[Dict]:
    """The mission-result JSON as a dict, or None when the text is absent /
    unparseable / not an object / carries the WRONG schema (design Backward
    Compatibility: a schema bump makes the harness refuse an old artifact rather
    than mis-parse it)."""
    if text is None:
        return None
    try:
        obj = json.loads(text)
    except (ValueError, TypeError):
        return None
    if not isinstance(obj, dict):
        return None
    if obj.get("schema") != MISSION_RESULT_SCHEMA:
        return None
    return obj


def mission_verdict_of(obj: Optional[Dict]) -> Optional[str]:
    """The result's ``verdict`` string, or None when the result is unreadable or
    carries no string verdict (design edge 12: hlib.classify_mission_step(None)
    fails closed)."""
    if obj is None:
        return None
    v = obj.get("verdict")
    return v if isinstance(v, str) else None


def mission_wall_seconds_of(obj: Optional[Dict]) -> Optional[float]:
    """The mission's own measured wall span (audit finding G6), or None."""
    if obj is None:
        return None
    wall = obj.get("wallSeconds")
    if isinstance(wall, (int, float)) and not isinstance(wall, bool):
        return float(wall)
    return None


def stage_subkind_for(fu) -> str:
    """Map the first unmet seam-step outcome to a driver-stage subkind (design
    driver-validity taxonomy). None (no unmet step) -> "" (met). An M-C1 verb refusal
    carrying a recognized `msg=` reason maps to the finer driver-* subkind (item 6);
    an unrecognized reason falls back to driver-verdict-mismatch."""
    if fu is None:
        return ""
    if not fu.found:
        return "driver-stage"
    if fu.verdict == "TIMEOUT":
        return "seam-timeout"
    if fu.cmd == "LoadGame" and fu.verdict == "ERROR":
        return "load-failed"
    refusal = hlib.classify_seam_refusal_subkind(getattr(fu, "msg", ""))
    if refusal:
        return refusal
    return "driver-verdict-mismatch"


@dataclass
class DriverValidity:
    """What the driver-validity composition decided. ``detail_mission`` and
    ``detail_mission_outcome`` are the ``mission`` / ``missionOutcome`` verifier rows
    (None on a seam-only driver); the outcome fields feed run.py's log line."""
    driver_valid: bool
    stage_subkind: str
    mission_outcome_unmet: bool = False
    detail_mission: Optional[Dict] = None
    detail_mission_outcome: Optional[Dict] = None
    outcome_unmet: Optional["hlib.StepOutcome"] = None
    is_flight_outcome: bool = False
    outcome_driver_subkind: str = ""
    gating_verbs: List[str] = field(default_factory=list)


def compose_driver_validity(ev: "hlib.ResponseEvaluation", mission: Optional[Dict],
                            boot_crashed: bool, batch_crashed: bool) -> DriverValidity:
    """Driver validity from the response stream + the mission step (M-B1).

    ``mission`` is the drive loop's mission-step row (``id`` / ``met`` /
    ``missionVerdict`` / ``subkind``) or None on a seam-only driver."""
    if mission is None:
        # Seam-only driver: every seam step gates validity (unchanged M-A5).
        driver_valid = ev.all_expected_met and not boot_crashed and not batch_crashed
        return DriverValidity(driver_valid, stage_subkind_for(ev.first_unmet))
    # Autopilot driver (design classification carve-out): validity is gated by
    # the steps UP TO AND INCLUDING the mission handoff -- LoadGame/SetSetting
    # (pre-mission seam steps) plus the mission verdict. Post-mission seam steps
    # (CommitTree/FlushAndQuit) are RECORDED but NON-gating on a MISSION-OK run:
    # a good flight Parsek then failed to record is a PARSEK-FAIL(expectation),
    # NOT a driver-INVALID a retry would paper over. When the mission itself did
    # NOT return MISSION-OK, its subkind drives the driver-INVALID.
    mission_id = mission["id"]
    pre_steps = [s for s in ev.steps if s.step_id < mission_id]
    pre_unmet = next((s for s in pre_steps if not s.met), None)
    pre_met = pre_unmet is None
    driver_valid = (pre_met and mission["met"]
                    and not boot_crashed and not batch_crashed)
    if not pre_met:
        stage_subkind = stage_subkind_for(pre_unmet)
    elif not mission["met"]:
        stage_subkind = mission["subkind"]
    else:
        stage_subkind = ""
    detail_mission = {
        "status": "PASS" if mission["met"] else "FAIL",
        "missionVerdict": mission["missionVerdict"], "subkind": mission["subkind"],
    }
    # The EVA-4 fail-open closure (2026-07-25). The carve-out above keeps every
    # post-mission RECORDING step non-gating on driver validity; what it must NOT
    # do is drop a post-mission OUTCOME step's verdict on the floor. That verdict
    # is the run's only channel onto the world state the mission handed off, and
    # on EVA-4 flight 3 it was the ONLY thing that saw the kerbal die. Recorded as
    # its own verifier row and classified PARSEK-FAIL(mission-outcome) so it reds
    # structurally, with no dependence on the spec author's log-token regexes.
    outcome_unmet = hlib.first_unmet_post_mission_outcome(ev.steps, mission_id)
    gating_verbs = [o.cmd for o in ev.steps
                    if str(o.step_id) > str(mission_id)
                    and hlib.post_mission_step_gates(o.cmd)]
    # Only a MET mission reaches the classifier: an unmet mission is already
    # driver-INVALID with its own subkind, and re-reporting its skipped/failed tail
    # as an outcome miss would mask the mission's own reason.
    is_flight_outcome, outcome_driver_subkind = (
        hlib.classify_post_mission_outcome_miss(outcome_unmet)
        if (outcome_unmet is not None and mission["met"]) else (False, ""))
    mission_outcome_unmet = is_flight_outcome
    # A refusal / tooling / never-answered miss is a DRIVER fault, classified exactly
    # as the same fault would be pre-mission, rather than being blamed on the mod.
    if outcome_driver_subkind:
        driver_valid = False
        stage_subkind = outcome_driver_subkind
    if not mission["met"]:
        outcome_status = "SKIPPED"
    elif outcome_unmet is not None:
        outcome_status = "FAIL"
    elif not gating_verbs:
        # NOT "PASS": this row checked nothing. Every autopilot scenario but EVA-4
        # lands here, and reading a blank check as a pass is how a future edit that
        # DROPS the gating step (disarming the gate entirely) goes unnoticed.
        outcome_status = "SKIPPED"
    else:
        outcome_status = "PASS"
    detail_outcome = {
        "status": outcome_status,
        "reason": "" if gating_verbs else "no-gating-verbs",
        "gatingVerbs": gating_verbs,
        "firstUnmet": (None if outcome_unmet is None else {
            "id": outcome_unmet.step_id, "cmd": outcome_unmet.cmd,
            "expect": outcome_unmet.expect, "verdict": outcome_unmet.verdict,
            "msg": outcome_unmet.msg,
            "flightOutcome": is_flight_outcome,
            "driverSubkind": outcome_driver_subkind}),
    }
    return DriverValidity(driver_valid, stage_subkind, mission_outcome_unmet,
                          detail_mission, detail_outcome, outcome_unmet,
                          is_flight_outcome, outcome_driver_subkind, gating_verbs)
