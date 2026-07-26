"""Mission b16_eve_orbit: HIGH-park ascent + Eve interplanetary transfer +
CAPTURE burn + park + commit-in-target-orbit terminal.

A THIN ALIAS over the body-parameterized B5 machine with BOTH established param
groups on at once, and NO new machine code:

  - B7's five INTERPLANETARY params (``interplanetaryTransfer``,
    ``viaBodyNames``, ``returnBodyName``, ``ejectionEccFloor``,
    ``correctionTriggerTimeToSoiSeconds``) -- LIVE-PROVEN on three B7 Duna
    flights, 2026-07-25.
  - B11/B12's ORBIT TAIL (``captureEnabled`` + the park window / dwell /
    commit params) -- LIVE-PROVEN on five B11 and six B12 flights, 2026-07-25.

THE COMBINATION has never flown, but it needs no third mechanism: the two groups
are read by DISJOINT parts of ``mlib.b5_decide``. The interplanetary params shape
PLAN-TRANSFER / TRANSFER-BURN / COAST-TO-TARGET (everything BEFORE the target
SOI); the capture params shape TARGET-FLYBY onward (everything AFTER it). The one
place they touch is ``_b5_return_body``: in capture mode TARGET-FLYBY treats
reading the RETURN body as the "flew past instead of circularizing" ASSERT-FAIL,
and with ``returnBodyName = "Sun"`` that failure fires on the correct body with
the correct message. Left unset it would default to Kerbin and the same escape
would still ASSERT-FAIL, just through the generic ejected-off-course branch with
a less apt reason -- so setting it is a message-quality choice, not a mechanism.

WHY EVE IS THE THIRD CAPTURE CASE, honestly. B11/B12 already commit a tree while
parked in a foreign SOI; Eve does that again at a body with ~125x the Mun's
gravitational parameter, reached across a heliocentric transfer instead of a
lunar one. The genuinely new Parsek surface is thin (see the B16 spec's WHY THIS
MISSION EXISTS block, which states plainly what this does and does NOT buy). What
it does add that nothing else has: the capture tail exercised after a Kerbin ->
Sun -> Eve traverse, so the committed tree's terminal orbit body is reached
through TWO SOI transitions rather than one.

DELTA-V, the thing that decides whether this mission can exist at all: the Eve
capture burn is DERIVED at ~640-690 m/s (circularize at an 8,000 km park from a
Hohmann-class arrival), against a remaining margin DERIVED at ~1,825-1,880 m/s
from the MEASURED end-of-flight fuel state of the three green B7 Duna runs. The
arithmetic and every input's provenance are in the B16 spec.

Kept as its own mission name so the client name, the result files and the logs
say which body the flight parked at; the schema is a copy for the same reason
(run.py resolves <mission>.schema.toml by name).

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

MISSION_NAME = "b16_eve_orbit"


def build_state(params: dict):
    """Build the (shared) mlib B5 phase-machine state from the missionParams."""
    return mlib.b5_initial_state(mlib.b5_params_from_dict(params))


def decide(state, snapshot):
    return mlib.b5_decide(state, snapshot)


def evaluate(frames, params: dict, state=None) -> List[mlib.AssertionOutcome]:
    return mlib.evaluate_b5_assertions(
        frames, mlib.b5_params_from_dict(params),
        phases_reached=tuple(getattr(state, "phases_reached", ()) or ()),
        min_target_altitude=getattr(state, "min_target_altitude", None),
        state=state)


def make_control() -> mission_runner.MissionControl:
    # The SAME seam as B11/B12 -- the capture tail's three OBSERVED channels are
    # opt-in and every one of them fails CLOSED at its unread sentinel:
    #   read_docking       -> angular_velocity for the PARK tumble gate
    #   read_node_executor -> NodeExecutor.Enabled, the B11 flight-1 lesson
    #                         (CAPTURE-BURN must OBSERVE that MechJeb engaged,
    #                         never infer it from having commanded it)
    #   read_periapsis     -> Orbit.TimeToPeriapsis, the B12 flight-3 lesson
    #                         (the only legitimate in-SOI warp target is
    #                         periapsis_ut - 900, read off the orbit's own clock)
    return mission_runner.KrpcMissionControl(
        use_mechjeb=True, client_name=MISSION_NAME, read_docking=True,
        read_node_executor=True, read_periapsis=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # Same warp policy as B7 (heliocentric coast at factor 7) and B11/B12 (both
    # NodeExecutor autowarps are RAILS); MechJeb-ascent physics warp capped at
    # the stock 4x ceiling. mlib.STOCK_WARP_ALTITUDE_LIMITS already carries Eve,
    # so the arrival + park legs are clamped from committed data.
    allow_rails_warp=True,
    max_physics_warp=4.0,
    # No settle tail (the SF-4 contract): machine-carried assertions, frames
    # discarded, and the terminal frame is the one AFTER the commit.
    settle_frames=0,
)


def main(argv: Optional[List[str]] = None) -> int:
    return mission_runner.main_from_spec(SPEC, argv)


if __name__ == "__main__":
    sys.exit(main())
