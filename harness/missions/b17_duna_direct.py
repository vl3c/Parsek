"""Mission b17_duna_direct: PAD-ALIGNED direct Kerbin->Duna transfer + CAPTURE
into an Ike-clear park + commit-in-target-orbit terminal.

THE RECORDING IS THE PRODUCT. This mission exists to produce the committed
recording the "Looped re-aim interplanetary transfer" todo entry names as
option-3's validation precondition (a): a CLEAN DIRECT Kerbin->Duna looped
recording with NO parking-orbit loiter and NO moon (Ike) encounter. Both
cleanliness constraints are engineered structurally, not hoped for:

  - NO LOITER: the B5 machine's PAD-ALIGN phase (new, flag-gated, this lane)
    computes the ejection window from the committed stock ephemeris and
    epoch-jumps to it ON THE PAD via one forward-only seam TimeJump BEFORE
    launch -- so recording starts (autoRecordOnLaunch) with the phase angle
    already right. The ejection then plans with WaitForPhaseAngle OFF (ASAP):
    decompiled MechJeb 2.15.1 places the ASAP burnUT within ONE park orbit,
    so the recorded departure is circularize -> single hyperbolic ejection,
    never a parked wait. B7 by contrast parks at 700 km and lets the
    NodeExecutor autowarp a ~200-day window wait IN LKO -- correct for a
    flyby lane, disqualifying for this fixture.
  - NO IKE: the park window is chosen INSIDE Ike's orbital shell. Ike sits
    at ~3,296 km with a ~1,049 km SOI, so the danger band starts at
    ~1,830 km altitude; the spec's parkMaxApoapsisMeters sits far below it,
    making the committed orbit permanently Ike-safe. The single inbound
    shell transit remains a per-attempt encounter risk (the measured B7
    flake), which the spec prices via its retry policy and the forbidden
    Ike logContract token -- an Ike encounter is a NAMED mission failure,
    never a tolerable variance, because it poisons the fixture.

A THIN ALIAS over the body-parameterized B5 machine, like b16_eve_orbit:
B16's two live-proven param groups (B7's five interplanetary params +
B11/B12's capture tail) PLUS the PAD-ALIGN group this lane adds to the shared
machine (padAlignEjection / padAlignMarginSeconds / padAlignTimeoutSeconds /
padAlignWindowGuardSeconds). There is NO b17_decide; every B17-specific
behavior is a spec param the shared machine reads, and with the flag off the
machine is byte-identical to the flown B16 shape.

DELTA-V, the existence argument: the DD1 Duna Direct Probe (authored by
construction, harness/tools/build_dd1_craft.py) carries ~4,190 m/s vacuum on
its transfer stage against ~2,060 required (ejection ~1,080 from a ~100 km
park + corrections <= 450 per the B15 sizing + capture-to-300-km-circular
~460 + trim). The craft doc's A6 carries the full arithmetic with cfg-cited
masses; the B15 lesson (a 200 m/s correction cap is a MOON calibration)
is applied as maxCorrectionDvMps = 450 in the spec.

Kept as its own mission name so the client name, the result files and the
logs say which lane flew; the schema is a copy for the same reason (run.py
resolves <mission>.schema.toml by name).

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

MISSION_NAME = "b17_duna_direct"


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
    # The SAME opt-in observation channels as B16 (each fails CLOSED at its
    # unread sentinel): read_docking for the PARK tumble gate,
    # read_node_executor for the B11 flight-1 lesson (OBSERVE that MechJeb
    # engaged, never infer), read_periapsis for the B12 flight-3 lesson (the
    # only legitimate in-SOI warp target is the orbit's own periapsis clock).
    return mission_runner.KrpcMissionControl(
        use_mechjeb=True, client_name=MISSION_NAME, read_docking=True,
        read_node_executor=True, read_periapsis=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # Same warp policy as B7/B16: heliocentric coast at rails factor 7, both
    # NodeExecutor autowarps RAILS, MechJeb-ascent physics warp at the stock
    # 4x ceiling. The PAD-ALIGN epoch jump is NOT a warp at all (TimeJump
    # stops warp and shifts the epoch instantly), so it needs nothing here.
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
