"""Mission b21_eeloo_orbit: HIGH-park ascent + PRE-TRANSFER JETTISON + Eeloo
interplanetary transfer + CAPTURE burn + park + commit-in-target-orbit terminal.

The B16 -> B17 -> B19 precedent, applied once more: a THIN ALIAS over the
body-parameterized B5 machine, carrying B7's five INTERPLANETARY params, B11/B12's
ORBIT TAIL and B19's PRE-TRANSFER JETTISON, retargeted at Eeloo. There is no
b21_decide and, unlike B19, no new mlib code at all -- `b5_decide` is
target-generic (docs/dev/design-testing-unified.md S190), the jettison phase is
already shipped, and Eeloo is a parameter.

WHAT IS GENUINELY NEW HERE IS A REGIME, NOT A MECHANISM. Eeloo's eccentricity is
0.26, the highest of any stock planet, so the arrival radius spans
66.69e9 .. 113.55e9 m and the heliocentric transfer time is a 2x BAND --
23.34e6 to 46.51e6 game s -- rather than a number. MechJeb picks the window
(OperationInterplanetaryTransfer with WaitForPhaseAngle), so which end of that
band this flight gets is unknowable before it flies, and every game-second budget
in the spec is sized on the 46.51e6 APHELION worst case, not on the 34.27e6
mid-band figure: sizing on the middle reaps a legitimate aphelion-end arrival at
~74% of its coast. The same "check which direction the number moves" discipline
is why transferBurnTimeoutSeconds does NOT grow -- it carries the
ejection-window wait, and the Kerbin->Eeloo synodic period is SHORTER than
Kerbin->Dres's (9,776,696 vs 11,392,903 game s), so this lane's worst case is
smaller than B19's.

THE JETTISON PHASE, inherited unchanged, exists because of a MEASURED property of
this lane's CRAFT -- the same Duna Rocket B18/B19 flew -- and not because of the
target body:

  B18 measured the Duna Rocket in its 80 km park carrying 20.775 t of SPENT
  CHEMICAL HARDWARE -- the core Mainsail, its tanks, the fairing and an unlit
  Skipper -- with a CHEMICAL engine still live. MechJeb autostage never sheds
  any of it, because autostage fires only on EMPTY stages and the core Mainsail
  still held ~40% of its LFO at insertion. The transfer stage's LV-N sits four
  stages further down the list.

  Two things break if that stack is carried into PLAN-TRANSFER. (1) TWR: the
  LV-N is 60 kN, so against the full 74.3 t stack it pushes at 0.08 g and a
  ~2,000 m/s Eeloo ejection is not a burn, it is a spiral. After the jettison the
  same engine pushes 18.2 t at 0.34 g and the burn is 9-10 minutes (594 s at the
  aphelion-end worst case). (2) The node would be planned and sized against a
  vehicle about to lose two thirds of its mass.

  `_b5_flameout_stage` cannot do this job: it fires only on OBSERVED zero thrust
  under a COMMANDED burn, and this jettison happens at throttle 0 with nothing
  commanded. So the phase pops stages ONE AT A TIME, thrust-safe, re-observing
  between pops, and then CERTIFIES the outcome on two independent channels --
  a vessel split (the spent stack became its own vessel) and a live-thrust
  SIGNATURE (60 kN reads as the Nerv; the 650 kN Skipper and 1,500 kN Mainsail
  do not fit under the ceiling). The evidence half is B-DOCK's shared pure
  `separation_evidence`; see `mlib._b5_jettison_step`.

  `jettisonMaxActivations` IS A SAFETY CAP, NOT A SPEC-DECLARED COUNT, and the
  distinction is load-bearing rather than pedantic: `_b5_jettison_step` evaluates
  its early stop (`split_confirmed and thrust_ok`) BEFORE the pop branch, so a
  satisfied signature can never be followed by one more pop, and the cap only
  bounds a craft that never reaches the signature at all. B19 flight 1 MEASURED
  why an exact count cannot be right -- how many stages separate the park from
  the transfer engine depends on how far autostage already walked the list during
  the ascent, and the 700 km ascent ran the core dry and reached its park with
  the SKIPPER live, two stages to go, where a blind four pops would have shed the
  transfer stage's own drop tanks and fired the pod decoupler.

WHY THE PARK IS HIGH (700 km), the B16 reason verbatim and NOT a copied habit:
the Kerbin->Eeloo ejection window wait is warped through, and KSP's stock rails
table caps warp by ALTITUDE. At a 100 km park the ceiling is 50x, which cannot
cover a multi-million-second window wait inside any sane wall budget; above
600 km the full 100,000x ladder is legal. The park altitude is a WARP decision.

DELTA-V IS CLOSER TO BEING THE CONSTRAINT HERE THAN ON ANY LANE BEFORE IT, which
is the one claim of B19's docstring that must not be carried over unexamined --
and this lane is ONE-WAY (Eeloo orbit and a mid-mission commit; there is no
return leg to pay for). The stage is the MEASURED one, 18.220 t wet / 7.020 t
burnout at Isp 800 s, and a part-by-part rebuild of that mass reproduces B18's
measurement: 800 * 9.80665 * ln(18.220/7.020) = 7,482.5 m/s against the measured
7,483. Every mission figure below is FIRST-PRINCIPLES patched-conic from a 700 km
park, never an empirical ratio applied to an ideal -- an earlier draft scaled
two-body ideals by Dres's measured-vs-derived ejection and capture ratios and
produced a 1,720 m/s ejection with a 2,806 m/s capture, two numbers belonging to
MUTUALLY EXCLUSIVE geometries (1,720 implies a transfer apoapsis inside Eeloo's
perihelion), which it then added. The spec header carries the retraction with its
arithmetic. The ladder that replaces it, ejection + capture-to-a-300-km-park:
3,254.7 m/s on a matched aphelion arrival, 3,694.5 on a matched SMA one, 3,852.8
on a matched perihelion one, and 5,124.5 on the SIZING case -- a MISMATCHED
perihelion arrival with the transfer apoapsis at Eeloo's SMA, where the craft
arrives still climbing and meets Eeloo's 4,706.4 m/s perihelion speed at
v_inf 3,348.0. The corollary is that maxCorrectionDvMps is a real ceiling on this
lane rather than a formality, and that it comes DOWN to 550 from B19's 1,200.

THE CAP IS PER ROUND, AND THIS LANE CAN FIRE FOUR CAPPED ROUNDS -- not two. Two
are SCHEDULED by the spec's correctionTriggerTimeToSoiSeconds
(`_b5_rounds_pending`, mlib.py:8324-8328), and the machine grants up to TWO MORE
arrival-quality extras that the spec neither declares nor can decline: at
mlib.py:10343-10376, once the scheduled rounds are exhausted, a sub-floor
PREDICTED arrival periapsis debounced over ARRIVAL_BAD_DEBOUNCE_FRAMES = 3 frames
re-enters the SAME PLAN-CORRECTION carrying the SAME `limit=max_correction_dv`
(mlib.py:8621), bounded by MAX_ARRIVAL_EXTRA_ROUNDS = 2 (mlib.py:1560) on a
SEPARATE counter (`extra_rounds_done`, mlib.py:7708). So the worst correction
spend is 4 x cap. On the sizing case the four-round tails are
2,115.2 + 4 x cap + 3,009.3 against the measured no-shed 7,482.5 m/s and the
8,434.0 m/s a drop-tank shed would give: 7,324.5 at cap 550 (1.02x / 1.15x),
8,324.5 at 800 (0.90x / 1.01x -- and 4 x 800 = 2 x 1,600, so that IS the tail the
spec rejected outright as "1,600 counted twice"), 9,924.5 at 1,200 (0.75x /
0.85x). 550 is the largest round value that survives four rounds on the sizing
geometry -- (7,482.5 - 5,124.5) / 4 = 589.5 bare, 571.8 with a 3% finite-burn
loss applied to the CORRECTION BUDGET (on the stricter whole-spend reading the
bound is 535.0 and 550 is ~1.03x over it, accepted because that tail is a BOUND
rather than an expectation; the spec header carries the arithmetic) -- and it
clears every MATCHED geometry, the whole realistic band, at
1.23x-1.37x while keeping 2.2x headroom over B19's MEASURED ~250 m/s per round.
The four-round tail is pessimistic (the extras fire only inside
600 s < time_to_soi < 3,600 s, where the code's own measurement gives ~12.8 km of
arrival shift per m/s, so a floor-fix there is tens of m/s), but it is still the
sizing basis, because overspending does not fail softly: `_b5_flameout_stage`
pops a stage on flameout, and the two stages below the LV-N are its own radial
drop tanks and the pod decoupler -- the second of which separates the pod and
destroys the committed recording that IS this lane's product.

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

MISSION_NAME = "b21_eeloo_orbit"


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
    # The SAME seam as B11/B12/B19 -- the capture tail's three OBSERVED channels
    # are opt-in and every one of them fails CLOSED at its unread sentinel:
    #   read_docking       -> angular_velocity for the PARK tumble gate
    #   read_node_executor -> NodeExecutor.Enabled, the B11 flight-1 lesson
    #                         (CAPTURE-BURN must OBSERVE that MechJeb engaged,
    #                         never infer it from having commanded it)
    #   read_periapsis     -> Orbit.TimeToPeriapsis, the B12 flight-3 lesson
    #                         (the only legitimate in-SOI warp target is
    #                         periapsis_ut - 900, read off the orbit's own clock)
    #                         and the one that matters MOST at Eeloo: its SOI is
    #                         119,082,942 m, so the SOI-edge -> periapsis coast is
    #                         ~52,500-93,000 game s, and without this channel the
    #                         capture never arms.
    return mission_runner.KrpcMissionControl(
        use_mechjeb=True, client_name=MISSION_NAME, read_docking=True,
        read_node_executor=True, read_periapsis=True)


SPEC = mission_runner.MissionSpec(
    name=MISSION_NAME,
    build_state=build_state,
    decide=decide,
    evaluate=evaluate,
    make_control=make_control,
    # Same warp policy as B7 (heliocentric coast at factor 7) and B11/B12/B19
    # (both NodeExecutor autowarps are RAILS); MechJeb-ascent physics warp capped
    # at the stock 4x ceiling. mlib.STOCK_WARP_ALTITUDE_LIMITS already carries
    # Eeloo, so the arrival + park legs are clamped from committed data.
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
