"""Mission b7_duna_flyby: LKO ascent + Duna interplanetary flyby + exit to Sun.

A THIN ALIAS over the body-parameterized B5 machine, EXACTLY like
``b6_minmus_flyby``: the flight is ``mlib.b5_decide`` /
``mlib.evaluate_b5_assertions`` verbatim, and every B7-specific behavior is
selected by spec params the shared machine reads -- there is NO ``b7_decide``.
Only the spec params differ from B5/B6, and B7 turns on five params the
Mun/Minmus specs leave at their B5 defaults:

  - ``interplanetaryTransfer = true``   -> ORBIT / PLAN-TRANSFER ask MechJeb's
    ManeuverPlanner for an ``OperationInterplanetaryTransfer`` node with
    WaitForPhaseAngle instead of the moon Hohmann ``OperationTransfer``. The
    ejection node lands possibly ~200 days ahead (the next Kerbin->Duna
    window); the NodeExecutor autowarp carries the wait.
  - ``viaBodyNames = ["Sun"]``          -> the coast legally crosses
    Kerbin -> Sun -> Duna; the machine no longer reads "Sun" as the ejected
    ASSERT-FAIL terminal, and it rails-warps the heliocentric leg.
  - ``returnBodyName = "Sun"``          -> a Duna flyby exits back into SUN SOI
    (a free-return to Kerbin takes years and is out of scope); RETURN fires on
    body == Sun AFTER the flyby, not body == Kerbin.
  - ``ejectionEccFloor = 1.05``         -> the ejection burn-done evidence is a
    HYPERBOLIC Kerbin-frame eccentricity (> 1), not the apoapsis floor: an
    escape burn drives the Kerbin-frame apoapsis NEGATIVE, so
    transferMinApoapsisMeters cannot be the evidence.
  - ``correctionTriggerTimeToSoiSeconds`` -> heliocentric correction rounds
    trigger on TIME-TO-DUNA-SOI thresholds (the coast is in Sun SOI, so the
    B5 Kerbin-altitude trigger can never fire mid-coast).

Kept as its own mission name (not a reused b5/b6) so the client name, result
files, and logs say which body the flight targeted; the schema is a copy for
the same reason (run.py resolves <mission>.schema.toml by name).

REQUIRES the mlib B7 diff plan (docs/dev/design-autotest-b7-duna.md) to have
landed: this shell passes the B7 params straight through
``mlib.b5_params_from_dict``, which must parse the new keys for the machine to
enable the B7 behavior. Until then the params are simply ignored and the flight
runs as a (physically-impossible) Mun-shaped attempt -- do NOT fly B7 before the
diff plan is applied and the shared machine tests are green.

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

MISSION_NAME = "b7_duna_flyby"


def build_state(params: dict):
    """Build the (shared) mlib B5 phase-machine state from the missionParams."""
    return mlib.b5_initial_state(mlib.b5_params_from_dict(params))


def decide(state, snapshot):
    return mlib.b5_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    return mlib.evaluate_b5_assertions(
        frames, mlib.b5_params_from_dict(params),
        phases_reached=tuple(getattr(state, "phases_reached", ()) or ()),
        min_target_altitude=getattr(state, "min_target_altitude", None))


def make_control() -> mission_runner.MissionControl:
    return mission_runner.KrpcMissionControl(use_mechjeb=True, client_name=MISSION_NAME)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # Same warp policy as B5/B6: the machine's non-blocking rails-factor control
    # + the NodeExecutor autowarp (which carries the ~200-day ejection-window
    # wait) are RAILS; MechJeb-ascent physics warp capped at the stock 4x
    # ceiling. The heliocentric coast reaches factor 7 (100,000x) -- legal far
    # from the Sun; the per-body altitude clamp and the time-to-SOI approach
    # bound keep Duna entry from being warped through.
    allow_rails_warp=True,
    max_physics_warp=4.0,
    # No settle tail (review SF-4): same rationale as B5/B6 -- machine-carried
    # assertions, frames discarded, post-RETURN reads are pure flake surface.
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
