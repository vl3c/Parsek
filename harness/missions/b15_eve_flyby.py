"""Mission b15_eve_flyby: HIGH-park ascent + Eve interplanetary flyby + exit to Sun.

A THIN ALIAS over the body-parameterized B5 machine, EXACTLY like
``b7_duna_flyby`` -- the flight is ``mlib.b5_decide`` /
``mlib.evaluate_b5_assertions`` verbatim and every interplanetary behavior is
selected by spec params the shared machine already reads. There is NO
``b15_decide``, and this change adds NO new machine code: B15 turns on the SAME
five params B7 turns on, with Eve in place of Duna.

  - ``interplanetaryTransfer = true``   -> ORBIT / PLAN-TRANSFER ask MechJeb for
    an ``OperationInterplanetaryTransfer`` node with WaitForPhaseAngle.
  - ``viaBodyNames = ["Sun"]``          -> Kerbin -> Sun -> Eve is a legal coast,
    and the heliocentric leg is a legal rails-warp body.
  - ``returnBodyName = "Sun"``          -> an Eve flyby exits back into SUN SOI.
  - ``ejectionEccFloor = 1.05``         -> hyperbolic Kerbin-frame eccentricity is
    the ejection burn-done evidence (an escape burn drives the Kerbin-frame
    apoapsis NEGATIVE, so the apoapsis floor cannot be it).
  - ``correctionTriggerTimeToSoiSeconds`` -> correction rounds trigger on
    time-to-EVE-SOI (the coast is in Sun SOI, so B5's Kerbin-altitude trigger can
    never fire mid-coast).

THE ONE THING THAT IS GENUINELY NEW HERE, and it is a PARAMETER not a mechanism:
every interplanetary case so far went OUTWARD (Kerbin -> Duna). Eve is INWARD, so
the ejection burn is RETROGRADE relative to Kerbin's heliocentric motion. Nothing
in the shared machine has a direction: ``_b5_transfer_burn_done``'s ejection gate
reads |ecc| >= floor in the HOME frame (an inward escape is just as hyperbolic as
an outward one), the coast's via-body gates are name comparisons, and the
time-to-SOI correction trigger is a scalar clock. The direction assumption, if
one exists anywhere, lives in MechJeb's own ``OperationInterplanetaryTransfer``
(WaitForPhaseAngle), which the runner drives through a two-line property set --
see the B15 spec's INWARD TRANSFER block for the risk statement. That is the
lane's first-flight unknown, and it is a MechJeb question, not an mlib one.

Kept as its own mission name (not a reused b7_duna_flyby) so the client name,
result files and logs say which body the flight targeted; the schema is a copy
for the same reason (run.py resolves <mission>.schema.toml by name).

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

MISSION_NAME = "b15_eve_flyby"


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
    # Same warp policy as B5/B6/B7: the machine's non-blocking rails-factor
    # control + the NodeExecutor autowarp (which carries the ejection-window
    # wait) are RAILS; MechJeb-ascent physics warp capped at the stock 4x
    # ceiling. The heliocentric coast reaches factor 7 (100,000x): Eve's orbit
    # sits ~9.57e9 m above the Sun's surface, five orders of magnitude past the
    # 65,400 km Sun factor-7 limit, so an INWARD transfer is just as legal as
    # B7's outward one. mlib.STOCK_WARP_ALTITUDE_LIMITS already carries Eve
    # (identical to Kerbin's table: factor 7 needs >= 600 km), so the per-body
    # clamp shapes the arrival legs with no new data.
    allow_rails_warp=True,
    max_physics_warp=4.0,
    # No settle tail (review SF-4): same rationale as B5/B6/B7 -- machine-carried
    # assertions, frames discarded, post-RETURN reads are pure flake surface.
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
