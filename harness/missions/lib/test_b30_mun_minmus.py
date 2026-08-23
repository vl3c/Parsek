"""Unit tests for the b30_mun_minmus lane (roadmap gap G4).

WHAT IS DIFFERENT ABOUT THIS FILE, and it is the reason it is not a copy of
`test_b26_laythe_vall.py` with the body names swapped: B30 REPLICATES B26's
parent-relay mode at a second parent, and three things about that parent make
the arithmetic genuinely different rather than merely re-scaled.

  * **THE PARENT ENVELOPE IS 12.9x TIGHTER AND IT INVERTS THE ESCAPE'S BINDING
    CONSTRAINT.** Kerbin's SOI is 7.01x Mun's orbital radius where Jool's is
    90.35x Laythe's, and Minmus sits at 55.8% of Kerbin's SOI where Vall sits at
    1.8% of Jool's. So B26 had to OVER-size its escape to clear a geometric
    reachability floor, while B30 has to UNDER-size its escape to keep the
    unaimed band inside the PARENT'S OWN SOI. `KerbinEscapeSweepTests` is the
    cell that pins that, and it is the one with no B26 analogue at all.
  * **MINMUS IS INCLINED ~6 deg** where Laythe and Vall are coplanar, so the
    plane-change term is a new cost the dv ledger has to carry.
  * **MUN AND MINMUS ARE NOT RESONANT**, where B26's pair sits in Jool's 1:2:4
    chain. `test_the_pair_is_NOT_resonant_and_that_is_the_point` is the
    deliberate inversion of B26's `test_the_synodic_is_one_vall_period_...`.

Everything mechanically checkable about the spec lives here, so a future mlib
change or a spec edit reds in `harness/missions/lib` rather than on a
7,900-second flight.

Runnable with the stdlib runner only (NO pytest, NO kRPC, NO KSP, NO network)::

    cd harness && python -m unittest discover -s missions/lib -q
"""

import math
import os
import sys
import tomllib
import unittest

_MISSIONS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)

import mlib                    # noqa: E402

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(
    os.path.abspath(__file__))))
REPO_ROOT = os.path.dirname(HARNESS_ROOT)

# STOCK CONSTANTS. KSP's bodies are on rails with elements that never change, so
# these are DATA and not tolerances. Each has an in-repo citation, recorded in
# the same discipline `mlib.STOCK_BODY_GRAVITY` applies to its own rows.
MU_KERBIN = 3.5316e12                 # SpawnSafetyNetTests.cs:1083
R_KERBIN = 600_000.0                  # SpawnSafetyNetTests.cs:1083
SOI_KERBIN = 84_159_286.0             # research doc s5.2 SOI/SMA table
KERBIN_ATMOSPHERE_TOP_M = 70_000.0

MU_MUN = 6.5138398e10                 # SpawnSafetyNetTests.cs:1083, reaim_sim.py:22
R_MUN = 200_000.0                     # SpawnSafetyNetTests.cs:1083
SOI_MUN = 2_429_559.1                 # V6M-mun-player-loop.toml:45
A_MUN = 12_000_000.0                  # research doc s5.2 table
MUN_HIGHEST_TERRAIN_M = 7_061.0

MU_MINMUS = 1.7658e9                  # B12-minmus-orbit.toml:149 ("mu 1.766e9")
R_MINMUS = 60_000.0                   # B12-minmus-orbit.toml:149 ("R 60 km")
SOI_MINMUS = 2_247_428.4              # V7M-minmus-player-loop.toml:29
A_MINMUS = 47_000_000.0
MINMUS_INCLINATION_DEG = 6.0
MINMUS_HIGHEST_TERRAIN_M = 5_700.0    # B12-minmus-orbit.toml:255

# The Jool-system figures this lane is measured AGAINST, so the replication's
# claims are checkable rather than asserted. Committed table, research doc s3.1.
SOI_JOOL = 2_455_985_185.0
A_LAYTHE = 27_184_000.0
A_VALL = 43_152_000.0

# THE FIXTURE'S MEASURED PARK, read off
# `harness/fixtures/saves/mun-park-kerbalx/persistent.sfs`.
PARK_SMA = 339568.96811189735
PARK_ECC = 0.00012634901315221596
PARK_INC_DEG = 0.76518295858058083
PARK_UT = 21745.960307940506
PARK_LF = 592.90425646695473
PARK_OX = 724.66075042198247

# dv ledger. Propellant mass is EXACT off the bytes (LF and Ox are both
# 0.005 t/unit and the 9:11 mix empties together); the DRY mass is a part-list
# estimate, so the available dv is a BAND and every cell below uses its
# PESSIMISTIC end.
PROPELLANT_TONNES = (PARK_LF + PARK_OX) * 0.005
POODLE_ISP_S = 350.0
G0 = 9.80665
DRY_TONNES_PESSIMISTIC = 8.5
DV_AVAILABLE_PESSIMISTIC = POODLE_ISP_S * G0 * math.log(
    (DRY_TONNES_PESSIMISTIC + PROPELLANT_TONNES) / DRY_TONNES_PESSIMISTIC)

ESCAPE_SOI_SPEED_MPS = 110.0
ESCAPE_NODE_MPS = 146.93
STAGE2_WORST_MPS = 186.6
CAPTURE_MPS = 81.5


def _spec(name="B30-mun-minmus-transfer.toml"):
    with open(os.path.join(HARNESS_ROOT, "scenarios", name), "rb") as fh:
        return tomllib.load(fh)


def snap(**kw):
    return mlib.TelemetrySnapshot(**kw)


def parked_snap(**overrides):
    """A frame reading the committed `mun-park-kerbalx` fixture's MEASURED Mun
    park. Periapsis / apoapsis derived from the save's own SMA and ECC."""
    base = dict(ut=PARK_UT, body="Mun", situation="ORBITING",
                apoapsis=PARK_SMA * (1.0 + PARK_ECC) - R_MUN,
                periapsis=PARK_SMA * (1.0 - PARK_ECC) - R_MUN,
                eccentricity=PARK_ECC,
                altitude=PARK_SMA - R_MUN,
                time_to_periapsis=1200.0)
    base.update(overrides)
    return snap(**base)


def hohmann(a1, a2, mu=MU_KERBIN):
    """(v_inf at departure, v_inf at arrival, time of flight) for a Hohmann
    transfer between two circular orbits about `mu`."""
    a_t = (a1 + a2) / 2.0
    v1 = math.sqrt(mu * (2.0 / a1 - 1.0 / a_t))
    v2 = math.sqrt(mu * (2.0 / a2 - 1.0 / a_t))
    return (v1 - math.sqrt(mu / a1),
            math.sqrt(mu / a2) - v2,
            math.pi * math.sqrt(a_t ** 3 / mu))


def minmus_soi_to_periapsis_seconds(periapsis_altitude):
    """Time from Minmus SOI entry to periapsis on the approach hyperbola, by
    hyperbolic Kepler. It matters MORE here than at Vall: Minmus's mu is tiny
    and its SOI is large, so this leg is ~19 ks rather than ~4 ks and it is what
    sizes correction round 1, the flyby budget and the capture budget."""
    _, v_inf, _ = hohmann(A_MUN, A_MINMUS)
    a = -MU_MINMUS / v_inf ** 2
    r_p = R_MINMUS + periapsis_altitude
    e = 1.0 + r_p / abs(a)
    h = math.acosh((SOI_MINMUS / abs(a) + 1.0) / e)
    return (e * math.sinh(h) - h) * math.sqrt(abs(a) ** 3 / MU_MINMUS)


def relay_params(**overrides):
    """The committed spec's own missionParams, so every predicate below is
    driven by the bytes a flight would use rather than by a copy."""
    mp = dict(_spec()["driver"]["missionParams"])
    mp.update(overrides)
    return mlib.b5_params_from_dict(mp)


def _unit_dirs(azimuths=12, tilts=5):
    """A grid over the unit sphere, used for both the exit point on Mun's SOI
    shell and the direction of the unaimed v_rel. Coarser than the 24x7 grid the
    spec header quotes, because this cell runs in a unit suite - the QUALITATIVE
    facts it asserts (which hazards exist at all, and which way the fractions
    move with speed) are grid-independent."""
    out = []
    for i in range(azimuths):
        th = 2.0 * math.pi * i / azimuths
        for k in range(-(tilts // 2), tilts // 2 + 1):
            ph = math.radians(k * 18.0)
            out.append((math.cos(th) * math.cos(ph),
                        math.sin(th) * math.cos(ph),
                        math.sin(ph)))
    return out


def unaimed_escape_band(v_soi):
    """The KERBIN-frame orbits an UNAIMED escape at `v_soi` can deliver, swept
    over every exit point on Mun's SOI shell and every v_rel direction.

    Returns (escaped_kerbin, sub_atmosphere, total, min_periapsis_m).
    `escaped_kerbin` counts deliveries that are hyperbolic about Kerbin OR whose
    apoapsis exceeds Kerbin's SOI - both of which put the craft in the Sun's SOI,
    which `_b5_coast_bodies` makes an immediate ASSERT-FAIL."""
    v_mun = math.sqrt(MU_KERBIN / A_MUN)
    dirs = _unit_dirs()
    escaped = 0
    sub_atmo = 0
    total = 0
    min_pe = float("inf")
    for ex in dirs:
        r = (A_MUN + SOI_MUN * ex[0], SOI_MUN * ex[1], SOI_MUN * ex[2])
        rn = math.sqrt(sum(c * c for c in r))
        for vd in dirs:
            v = (v_soi * vd[0], v_mun + v_soi * vd[1], v_soi * vd[2])
            vn2 = sum(c * c for c in v)
            eps = vn2 / 2.0 - MU_KERBIN / rn
            total += 1
            if eps >= 0.0:
                escaped += 1
                continue
            a = -MU_KERBIN / (2.0 * eps)
            h = (r[1] * v[2] - r[2] * v[1],
                 r[2] * v[0] - r[0] * v[2],
                 r[0] * v[1] - r[1] * v[0])
            h2 = sum(c * c for c in h)
            e = math.sqrt(max(0.0, 1.0 + 2.0 * eps * h2 / (MU_KERBIN ** 2)))
            if a * (1.0 + e) > SOI_KERBIN:
                escaped += 1
            pe = a * (1.0 - e)
            if pe < R_KERBIN + KERBIN_ATMOSPHERE_TOP_M:
                sub_atmo += 1
            min_pe = min(min_pe, pe)
    return escaped, sub_atmo, total, min_pe


class KerbinFrameArithmeticTests(unittest.TestCase):
    """The derivations B30's header states, re-run."""

    def test_the_pair_is_NOT_resonant_and_that_is_the_point(self):
        """THE DELIBERATE INVERSION OF B26's RESONANCE CELL, and it is a warning
        to the V21 lanes rather than a property of this one.

        B26's pair sits in Jool's 1:2:4 chain: `P_Vall - 2*P_Laythe = +0.3 s`,
        which put P_Vall and the Laythe-Vall synodic 0.6615 s apart and made
        V17M's two candidate jump tables IDENTICAL on cycle 1. Nothing like that
        holds here, so V21M/V21T's seed calibration is genuinely harder and
        agreement between candidate models cannot be mistaken for evidence."""
        p_mun = 2.0 * math.pi * math.sqrt(A_MUN ** 3 / MU_KERBIN)
        p_minmus = 2.0 * math.pi * math.sqrt(A_MINMUS ** 3 / MU_KERBIN)
        synodic = 1.0 / abs(1.0 / p_mun - 1.0 / p_minmus)
        self.assertAlmostEqual(138984.377, p_mun, delta=0.5)
        self.assertAlmostEqual(1077310.521, p_minmus, delta=1.0)
        self.assertAlmostEqual(159570.670, synodic, delta=0.5)
        # NOT a small-integer ratio, in either of the two ways B26's was.
        ratio = p_minmus / p_mun
        self.assertGreater(abs(ratio - round(ratio)) * p_mun, 60.0,
                           "P_Minmus is within a minute of an integer multiple "
                           "of P_Mun - re-check the resonance claim")
        # And the synodic is nowhere near the target's period, which is the
        # coincidence V17M got to lean on.
        self.assertGreater(abs(synodic - p_minmus), 0.5 * p_minmus)

    def test_the_transfer_is_a_long_eccentric_kerbin_frame_hohmann(self):
        """The three properties `IsSaneTransferConic` requires, plus the leg
        that sizes half the budget block. NOTE it is 2.6x more eccentric and
        6.9x longer than B26's - which is why NONE of B26's timing numbers were
        copied."""
        a_t = (A_MUN + A_MINMUS) / 2.0
        e_t = (A_MINMUS - A_MUN) / (A_MINMUS + A_MUN)
        self.assertGreater(a_t, 0.0)
        self.assertTrue(0.0 <= e_t < 1.0)
        self.assertAlmostEqual(0.59322, e_t, places=5)
        dep, arr, tof = hohmann(A_MUN, A_MINMUS)
        self.assertAlmostEqual(142.257, dep, delta=0.05)
        self.assertAlmostEqual(99.287, arr, delta=0.05)
        self.assertAlmostEqual(267853.4, tof, delta=5.0)
        self.assertGreater(tof / 38979.9, 6.5, "the leg is ~6.9x B26's; a "
                                               "copied budget would be wrong")

    def test_the_plane_change_is_a_cost_b26_never_paid(self):
        """(b) IN THE HEADER, priced. Minmus is inclined ~6 deg where Laythe and
        Vall are both coplanar with Jool's equator, so this term is new. It is
        cheapest at the transfer apoapsis, which is where a combined manoeuvre
        takes it."""
        a_t = (A_MUN + A_MINMUS) / 2.0
        v_ap = math.sqrt(MU_KERBIN * (2.0 / A_MINMUS - 1.0 / a_t))
        v_pe = math.sqrt(MU_KERBIN * (2.0 / A_MUN - 1.0 / a_t))
        half = math.radians(MINMUS_INCLINATION_DEG / 2.0)
        at_apoapsis = 2.0 * v_ap * math.sin(half)
        at_mun_radius = 2.0 * v_pe * math.sin(half)
        self.assertAlmostEqual(18.30, at_apoapsis, delta=0.05)
        self.assertAlmostEqual(71.67, at_mun_radius, delta=0.05)
        # The cheap end is what the derived stage-2 band carries, and even the
        # EXPENSIVE end is small against the margin - which is why bending the
        # one-dimension-at-a-time rule is affordable here.
        self.assertLess(at_mun_radius, 0.1 * DV_AVAILABLE_PESSIMISTIC)

    def test_the_delta_v_is_what_the_fixture_bytes_give(self):
        """The propellant mass is EXACT off the save; the dry mass is estimated,
        so the ledger is stated as a band and every margin cell uses the
        PESSIMISTIC end."""
        self.assertAlmostEqual(1317.565, PARK_LF + PARK_OX, delta=0.01)
        self.assertAlmostEqual(6.5878, PROPELLANT_TONNES, delta=0.001)
        self.assertGreater(DV_AVAILABLE_PESSIMISTIC, 1900.0)
        self.assertLess(DV_AVAILABLE_PESSIMISTIC, 2000.0)

    def test_the_worst_hop_leaves_a_real_margin(self):
        """THE dv LEDGER, against the PESSIMISTIC dv. B26 had to squeeze here
        (622.5 m/s of margin forced its correction cap down to 100); this lane
        does not, which is why its cap can be 75 rather than 50."""
        mp = _spec()["driver"]["missionParams"]
        rounds = len(mp["correctionTriggerTimeToSoiSeconds"]) + \
            mlib.MAX_ARRIVAL_EXTRA_ROUNDS
        self.assertEqual(4, rounds)
        # The WORST capture is at the LOWEST admissible periapsis, not at the
        # nominal 30 km park, so the ledger uses the park floor.
        r_p = R_MINMUS + mp["parkMinPeriapsisMeters"]
        _, v_inf, _ = hohmann(A_MUN, A_MINMUS)
        capture_worst = (math.sqrt(v_inf ** 2 + 2.0 * MU_MINMUS / r_p)
                         - math.sqrt(MU_MINMUS / r_p))
        self.assertAlmostEqual(86.75, capture_worst, delta=0.05)
        worst = (ESCAPE_NODE_MPS + STAGE2_WORST_MPS
                 + rounds * mp["maxCorrectionDvMps"] + capture_worst)
        self.assertAlmostEqual(720.3, worst, delta=1.0)
        self.assertLess(worst, DV_AVAILABLE_PESSIMISTIC)
        margin = DV_AVAILABLE_PESSIMISTIC - worst
        self.assertGreater(margin, 1200.0)
        self.assertGreater(DV_AVAILABLE_PESSIMISTIC / worst, 2.5)

    def test_the_minmus_approach_coast_is_the_long_one(self):
        """It is 4.6x Vall's, and it is what sizes correction round 1, the flyby
        budget and the capture budget. Checked at BOTH ends of the park window
        so the sizing is not a property of one hoped-for periapsis."""
        mp = _spec()["driver"]["missionParams"]
        lo = minmus_soi_to_periapsis_seconds(mp["parkMinPeriapsisMeters"])
        hi = minmus_soi_to_periapsis_seconds(mp["parkMaxApoapsisMeters"])
        for coast in (lo, hi):
            self.assertGreater(coast, 15000.0)
            self.assertLess(coast, 25000.0)
        self.assertGreater(lo / 4149.0, 3.5,
                           "the coast should be several times Vall's; if it is "
                           "not, the budgets below were sized wrong")


class KerbinEscapeSweepTests(unittest.TestCase):
    """**THE CELL WITH NO B26 ANALOGUE.** B26's equivalent sweep asked whether
    the unaimed escape could reach a SIBLING MOON'S SHELL or dip below the
    parent's atmosphere. At Kerbin the hazard is different and worse: the
    PARENT'S OWN SOI is only 7.01x Mun's orbital radius, so a high-energy draw
    leaves the system entirely. That is what sets `escapeSoiSpeedMps`."""

    def test_the_committed_speed_is_BELOW_the_ideal_which_inverts_b26(self):
        """The one-line statement of (c). B26 asked for 450 against a 347.245
        ideal because the geometry could not deliver less; B30 asks for 110
        against a 142.257 ideal because the parent's SOI is close. Same mode,
        opposite binding constraint - and a future edit that 'corrects' this
        toward the ideal reds here."""
        mp = _spec()["driver"]["missionParams"]
        ideal, _, _ = hohmann(A_MUN, A_MINMUS)
        self.assertEqual(ESCAPE_SOI_SPEED_MPS, mp["escapeSoiSpeedMps"])
        self.assertLess(mp["escapeSoiSpeedMps"], ideal)
        self.assertGreater(mp["escapeSoiSpeedMps"], 0.7 * ideal)

    def test_the_parent_envelope_is_the_reason(self):
        """The two ratios, stated as a comparison rather than as bare numbers,
        because the claim is RELATIVE to B26."""
        self.assertAlmostEqual(7.01, SOI_KERBIN / A_MUN, delta=0.02)
        self.assertAlmostEqual(90.35, SOI_JOOL / A_LAYTHE, delta=0.1)
        self.assertGreater((SOI_JOOL / A_LAYTHE) / (SOI_KERBIN / A_MUN), 12.0)
        # And the destination's own position in that envelope.
        self.assertAlmostEqual(55.8, 100.0 * A_MINMUS / SOI_KERBIN, delta=0.2)
        self.assertLess(100.0 * A_VALL / SOI_JOOL, 2.0)

    def test_the_committed_speed_holds_the_parent_escape_tail_small(self):
        """The pass/fail on the chosen value: at 110 the fraction of the unaimed
        band that leaves Kerbin is a fraction of a percent, and at the IDEAL it
        is several percent. Fractions rather than counts, because the assertion
        must not depend on the grid."""
        esc_c, _, tot_c, _ = unaimed_escape_band(ESCAPE_SOI_SPEED_MPS)
        ideal, _, _ = hohmann(A_MUN, A_MINMUS)
        esc_i, _, tot_i, _ = unaimed_escape_band(ideal)
        self.assertLess(esc_c / tot_c, 0.01,
                        "the committed speed lets too much of the unaimed band "
                        "leave Kerbin's SOI")
        self.assertGreater(esc_i / tot_i, 0.02,
                           "the ideal Hohmann excess should be materially worse "
                           "here - if it is not, the whole under-sizing "
                           "argument is wrong")
        self.assertGreater(esc_i / tot_i, 3.0 * (esc_c / tot_c))

    def test_the_sub_atmosphere_hazard_b26_had_does_not_exist_here(self):
        """B26's FLOWN (broken) contract admitted periapsides below Jool's
        atmosphere top. At Kerbin, at every speed this lane could plausibly
        carry, the worst periapsis clears Kerbin's atmosphere by a wide margin -
        so the forbidden-token list does NOT need an atmosphere guard, and the
        one it does carry is the parent-escape pair."""
        for v_soi in (ESCAPE_SOI_SPEED_MPS, 142.257, 200.0):
            _, sub, _, min_pe = unaimed_escape_band(v_soi)
            self.assertEqual(0, sub, "v_soi=%s put a delivery below Kerbin's "
                                     "atmosphere" % v_soi)
            self.assertGreater(min_pe, R_KERBIN + KERBIN_ATMOSPHERE_TOP_M)


class EscapeNodeSizingTests(unittest.TestCase):
    """The pure escape-node planner, driven with THIS lane's params. The refusal
    branches are generic and pinned by B26's file; what is pinned here is the
    arithmetic at Mun and the COUPLING between the entry gate and the speed."""

    def setUp(self):
        self.p = relay_params()

    def test_the_node_is_the_vis_viva_answer_at_the_periapsis(self):
        plan = mlib.escape_node_plan(self.p, parked_snap())
        self.assertEqual("", plan.reason)
        r_pe = PARK_SMA * (1.0 - PARK_ECC)
        v_now = math.sqrt(MU_MUN * (2.0 / r_pe - 1.0 / PARK_SMA))
        v_needed = math.sqrt(ESCAPE_SOI_SPEED_MPS ** 2
                             + 2.0 * MU_MUN * (1.0 / r_pe - 1.0 / SOI_MUN))
        self.assertAlmostEqual(v_needed - v_now, plan.dv, delta=0.01)
        self.assertAlmostEqual(ESCAPE_NODE_MPS, plan.dv, delta=0.02)

    def test_the_node_targets_the_soi_boundary_not_infinity(self):
        """B26 FLIGHT 2's DEFECT A, AS A UNIT CELL AT A SECOND BODY. The pre-fix
        formula sized for an asymptotic excess; at Mun that would deliver 2.33x
        the requested speed across the boundary (against 3.12x at Laythe)."""
        r_pe = PARK_SMA * (1.0 - PARK_ECC)
        delivered_by_old_contract = math.sqrt(
            ESCAPE_SOI_SPEED_MPS ** 2 + 2.0 * MU_MUN / SOI_MUN)
        self.assertAlmostEqual(256.36, delivered_by_old_contract, delta=0.05)
        self.assertGreater(delivered_by_old_contract / ESCAPE_SOI_SPEED_MPS, 2.3)
        # The corrected formula subtracts the SOI term, and the difference is
        # real rather than cosmetic.
        v_now = math.sqrt(MU_MUN * (2.0 / r_pe - 1.0 / PARK_SMA))
        old = math.sqrt(ESCAPE_SOI_SPEED_MPS ** 2 + 2.0 * MU_MUN / r_pe) - v_now
        plan = mlib.escape_node_plan(self.p, parked_snap())
        self.assertGreater(old - plan.dv, 20.0)

    def test_the_correct_escape_is_BOUND_which_is_why_the_ecc_floor_retired(self):
        """The same arithmetic that retired `ejectionEccFloor` at Laythe, re-run
        at Mun. Reaching a SOI needs an APOAPSIS past it, not an escape."""
        plan = mlib.escape_node_plan(self.p, parked_snap())
        r_pe = PARK_SMA * (1.0 - PARK_ECC)
        v_now = math.sqrt(MU_MUN * (2.0 / r_pe - 1.0 / PARK_SMA))
        v_after = v_now + plan.dv
        eps = v_after ** 2 / 2.0 - MU_MUN / r_pe
        self.assertLess(eps, 0.0, "the commissioned escape must be BOUND")
        self.assertAlmostEqual(-20760.8, eps, delta=5.0)
        a = -MU_MUN / (2.0 * eps)
        ecc = 1.0 - r_pe / a
        self.assertAlmostEqual(0.7836, ecc, places=3)
        apoapsis_radius = a * (1.0 + ecc)
        self.assertGreater(apoapsis_radius, SOI_MUN)
        self.assertAlmostEqual(15.2, 100.0 * apoapsis_radius / SOI_MUN - 100.0,
                               delta=0.5)
        # And the spec keeps the floor retired at 0, not "restored for safety".
        self.assertEqual(0, _spec()["driver"]["missionParams"]["ejectionEccFloor"])

    def test_a_request_below_the_geometric_floor_is_refused_by_name(self):
        """`escape_node_plan` REFUSES rather than clamping, so the committed
        speed must clear the floor with headroom. At Mun the floor is 81.08 m/s
        against B26's 370.08 - which is exactly why this lane had room to go
        DOWN and B26 did not."""
        r_pe = PARK_SMA * (1.0 - PARK_ECC)
        floor = math.sqrt(2.0 * MU_MUN * r_pe / (SOI_MUN * (SOI_MUN + r_pe)))
        self.assertAlmostEqual(81.08, floor, delta=0.05)
        self.assertGreater(ESCAPE_SOI_SPEED_MPS / floor, 1.30)
        refused = mlib.escape_node_plan(
            relay_params(escapeSoiSpeedMps=60.0), parked_snap())
        self.assertNotEqual("", refused.reason)
        self.assertIn("below what this park's geometry can deliver",
                      refused.reason)

    def test_every_park_the_entry_gate_admits_is_actually_deliverable(self):
        """**THE COUPLING CELL, AND IT IS WHY THE APOAPSIS CEILING IS 250,000.**
        The smallest deliverable SOI speed RISES with the park, so a gate ceiling
        that is too generous admits parks whose floor sits ABOVE the requested
        `escapeSoiSpeedMps` - and `escape_node_plan` then REFUSES a park the gate
        declared legal, killing the lane in ESCAPE with a correct-but-baffling
        give-up. B26 flight 2 had to discover that; this lane pins it before a
        flight. Sweeps the WHOLE admissible window, not three corners, because
        the offending region is interior to it."""
        mp = _spec()["driver"]["missionParams"]
        pe_floor = mp["startInOrbitMinPeriapsisMeters"]
        ap_ceiling = mp["startInOrbitMaxApoapsisMeters"]
        ecc_ceiling = mp["startInOrbitMaxEccentricity"]
        worst_dv = 0.0
        checked = 0
        for pa in range(int(pe_floor), int(ap_ceiling) + 1, 2000):
            for aa in range(pa, int(ap_ceiling) + 1, 2000):
                r_p = R_MUN + pa
                sma = R_MUN + (pa + aa) / 2.0
                if (aa - pa) / (2.0 * R_MUN + pa + aa) > ecc_ceiling:
                    continue
                plan = mlib.escape_node_plan(
                    self.p, parked_snap(periapsis=pa, apoapsis=aa,
                                        eccentricity=(aa - pa) /
                                        (2.0 * R_MUN + pa + aa),
                                        altitude=sma - R_MUN))
                self.assertEqual("", plan.reason,
                                 "gate-legal park pe=%d ap=%d was REFUSED: %s"
                                 % (pa, aa, plan.reason))
                worst_dv = max(worst_dv, plan.dv)
                checked += 1
        self.assertGreater(checked, 100)
        self.assertAlmostEqual(160.72, worst_dv, delta=0.5)
        self.assertGreater(mp["escapeMaxDeltaVMps"] / worst_dv, 1.10,
                           "the dv cap must clear the gate's worst corner")

    def test_a_wider_gate_ceiling_would_refuse_a_legal_park(self):
        """The other half of the coupling, stated as the counterfactual: a
        400,000 m ceiling would admit a round park whose own geometric floor is
        ABOVE the committed 110 m/s."""
        mp = _spec()["driver"]["missionParams"]
        self.assertEqual(250000, mp["startInOrbitMaxApoapsisMeters"])
        # THE CURVE IS A RAMP, NOT A CLIFF: the break-even is a round park on a
        # ~508,000 m ceiling, so the committed 250,000 carries a deliberate 2x
        # margin rather than sitting on the last safe value. 600,000 is the
        # first round figure past the break-even.
        for ceiling, expect_deliverable in ((250000, True), (500000, True),
                                            (600000, False)):
            r_p = R_MUN + ceiling
            floor = math.sqrt(2.0 * MU_MUN * r_p / (SOI_MUN * (SOI_MUN + r_p)))
            self.assertEqual(expect_deliverable,
                             ESCAPE_SOI_SPEED_MPS > floor,
                             "ceiling %d: floor %.2f" % (ceiling, floor))


class CompositionAuditTests(unittest.TestCase):
    """The audit's mechanically checkable half, driven through the real `mlib`
    predicates with the committed spec's own params."""

    def setUp(self):
        self.spec = _spec()
        self.mp = self.spec["driver"]["missionParams"]
        self.p = relay_params()

    def test_the_two_blocks_compose_without_a_parse_rejection(self):
        self.assertTrue(self.p.start_in_orbit)
        self.assertTrue(self.p.interplanetary_transfer)
        self.assertTrue(self.p.parent_relay_transfer)
        self.assertFalse(self.p.pad_align_ejection)
        self.assertNotIn("padAlignEjection", self.mp)

    def test_the_frame_is_kerbin_everywhere_it_is_read(self):
        self.assertEqual("Mun", self.p.home_body)
        self.assertEqual("Minmus", self.p.target_body)
        self.assertEqual("Kerbin", self.p.return_body)
        self.assertEqual(("Kerbin",), tuple(self.p.via_bodies))
        self.assertEqual(("", "Mun", "Kerbin"), mlib._b5_coast_bodies(self.p))
        self.assertEqual(("Mun", "Kerbin"), mlib._b5_warp_bodies(self.p))

    def test_the_correction_domain_narrows_to_an_identity(self):
        self.assertIn(self.p.return_body, self.p.via_bodies)
        self.assertEqual(("Kerbin",), mlib._b5_correction_via_bodies(self.p))

    def test_the_entry_gate_admits_the_committed_fixtures_measured_park(self):
        """THE cell that couples the spec to the fixture. If either moves, this
        reds before a KSP boot is spent."""
        verdict = mlib.start_in_orbit_frame_verdict(self.p, parked_snap())
        # The verdict is (code, reason); a healthy frame carries no reason.
        self.assertEqual(mlib.START_IN_ORBIT_IN_GATE, verdict[0], verdict)
        self.assertEqual("", verdict[1])

    def test_the_entry_gate_is_the_family_ecc_ceiling_not_b26s_widened_one(self):
        """B26 widened to 0.10 because ITS park was a finite-capture product at
        ecc 0.0277. This park is a circularization product at 0.000126, so the
        family's 0.05 admits it 396x over and the widening is not inherited."""
        self.assertEqual(0.05, self.mp["startInOrbitMaxEccentricity"])
        self.assertGreater(self.mp["startInOrbitMaxEccentricity"] / PARK_ECC,
                           100.0)

    def test_the_correction_triggers_are_sized_for_a_kerbin_system_leg(self):
        """All four inequalities are pure functions of THIS pair's leg and
        approach coast, so a copied B26 list reds here."""
        trig = self.mp["correctionTriggerTimeToSoiSeconds"]
        self.assertEqual([], self.mp["correctionTriggerAltsMeters"])
        self.assertEqual(2, len(trig))
        self.assertEqual(sorted(trig, reverse=True), trig)
        _, _, tof = hohmann(A_MUN, A_MINMUS)
        self.assertLess(trig[0], tof)
        self.assertGreater(trig[0], 0.5 * tof)
        approach = minmus_soi_to_periapsis_seconds(
            self.mp["parkMinPeriapsisMeters"])
        self.assertGreater(trig[1], approach,
                           "round 1 must fire BEFORE the SOI-entry -> periapsis "
                           "coast, or it collides with the arrival extras")
        self.assertGreater(trig[1], mlib.ARRIVAL_RECORRECT_MAX_TTS_SECONDS)

    def test_the_stage_two_floor_is_bracketed_by_the_two_radii_it_separates(self):
        """`transferMinApoapsisMeters` must sit BELOW the altitude an orbit
        reaching Minmus reads and ABOVE the one an un-escaped orbit at Mun's own
        radius reads, or it separates nothing."""
        floor = self.mp["transferMinApoapsisMeters"]
        self.assertGreater(floor, 0.0)
        self.assertLess(floor, A_MINMUS - R_KERBIN)
        self.assertGreater(floor, 1.5 * (A_MUN - R_KERBIN))


class SpecArithmeticTests(unittest.TestCase):
    """Cells that read the COMMITTED B30 spec."""

    def setUp(self):
        self.spec = _spec()
        self.mp = self.spec["driver"]["missionParams"]

    def test_the_approach_clamp_is_governed_by_KERBINS_table_not_minmuss(self):
        """**WHICH BODY IS UNDER THE CRAFT WHEN THE CLAMP APPLIES.** An earlier
        draft of this cell asserted the approach ceiling against MINMUS's table.
        That is the wrong table: `approach_warp_clamp` is applied in
        COAST-TO-TARGET while the craft is still in KERBIN's SOI (the latch arms
        on `next_body == target_body` and arrival RELEASES it), so Kerbin's
        limits govern and every factor is legal at coast altitude. The
        conclusion the spec draws - that x50 is a poll-control choice rather
        than a legality one - survives; its reason did not."""
        kerbin = mlib.STOCK_WARP_ALTITUDE_LIMITS["Kerbin"]
        cap = self.mp["approachMaxWarpFactor"]
        # The coast runs between Mun's and Minmus's orbital radii, i.e. tens of
        # Mm of Kerbin altitude - far above the top of Kerbin's own table, so
        # every rails factor is legal and the clamp binds nothing by legality.
        coast_altitude = A_MUN - R_KERBIN
        self.assertGreater(coast_altitude, kerbin[-1])
        self.assertEqual(len(mlib.RAILS_WARP_RATES) - 1,
                         mlib.max_legal_rails_factor("Kerbin", coast_altitude))
        self.assertGreater(mlib.RAILS_WARP_RATES[cap], 1.0)

    def test_the_flyby_ceiling_is_INERT_on_a_capture_enabled_lane(self):
        """`flyby_max_warp_factor` has exactly ONE reader - the rails-stair
        fallback in the final `else` of the TARGET-FLYBY warp chain - and that
        chain tests `capture_enabled` FIRST. This lane enables capture, so the
        stair is unreachable and the whole in-SOI leg is flown by a native warp
        toward the periapsis clock. The value is kept at B26's 3 rather than
        raised, because a raise here would be an unearned claim about a path the
        phase never takes."""
        self.assertTrue(self.mp["captureEnabled"])
        self.assertEqual(3, self.mp["flybyMaxWarpFactor"])
        # `capture_flyby_warp_target` IS the in-SOI policy: it returns a target
        # bounded by the periapsis clock, and refuses once that clock is past.
        target = mlib.capture_flyby_warp_target(5000.0, 1000.0)
        self.assertIsNotNone(target)
        self.assertLess(target, 1000.0 + 5000.0)
        self.assertIsNone(mlib.capture_flyby_warp_target(float("nan"), 1000.0))

    def test_the_flyby_floor_is_live_even_though_the_ceiling_is_not(self):
        """The asymmetry is worth pinning so a future reader does not delete the
        floor alongside the dead ceiling: `flybyWarpFactor` is consumed as the
        rails-stair FLOOR in COAST-TO-TARGET, which this lane does reach."""
        self.assertEqual(2, self.mp["flybyWarpFactor"])
        self.assertLessEqual(self.mp["flybyWarpFactor"],
                             self.mp["flybyMaxWarpFactor"])

    def test_the_in_soi_stair_is_ordered(self):
        self.assertLessEqual(self.mp["flybyWarpFactor"],
                             self.mp["flybyMaxWarpFactor"])
        self.assertLessEqual(self.mp["approachMaxWarpFactor"],
                             self.mp["flybyMaxWarpFactor"])

    def test_the_budgets_clear_the_legs_they_bound(self):
        p_mun = 2.0 * math.pi * math.sqrt(A_MUN ** 3 / MU_KERBIN)
        p_minmus = 2.0 * math.pi * math.sqrt(A_MINMUS ** 3 / MU_KERBIN)
        synodic = 1.0 / abs(1.0 / p_mun - 1.0 / p_minmus)
        _, _, tof = hohmann(A_MUN, A_MINMUS)
        approach = minmus_soi_to_periapsis_seconds(
            self.mp["parkMinPeriapsisMeters"])
        self.assertGreaterEqual(self.mp["transferBurnTimeoutSeconds"],
                                5.0 * synodic)
        self.assertGreaterEqual(self.mp["coastTimeoutSeconds"],
                                3.0 * (synodic + tof))
        self.assertGreaterEqual(self.mp["flybyTimeoutSeconds"], 10.0 * approach)
        self.assertGreaterEqual(self.mp["captureBurnTimeoutSeconds"],
                                10.0 * (approach + 600.0))
        # The escape budget is about one park period, as B26's was.
        park_period = 2.0 * math.pi * math.sqrt(PARK_SMA ** 3 / MU_MUN)
        self.assertGreater(self.mp["escapeTimeoutSeconds"], park_period)
        self.assertLess(self.mp["escapeTimeoutSeconds"], 2.0 * park_period)

    def test_both_floors_clear_minmus_terrain_and_are_unequal_in_the_right_order(self):
        """B25's lesson: the PARK floor must sit BELOW the ARRIVAL floor,
        because a finite capture burn drops the delivered periapsis."""
        park = self.mp["parkMinPeriapsisMeters"]
        arrival = self.mp["targetPeriapsisFloorMeters"]
        self.assertGreater(park, MINMUS_HIGHEST_TERRAIN_M)
        self.assertGreater(arrival, 2.0 * MINMUS_HIGHEST_TERRAIN_M)
        self.assertGreater(arrival, park)
        self.assertGreaterEqual(arrival - park, 5000.0)

    def test_the_park_ceiling_admits_the_arrival_spread_and_stays_in_the_shell(self):
        ceiling = self.mp["parkMaxApoapsisMeters"]
        self.assertLessEqual((ceiling + R_MINMUS) / SOI_MINMUS, 0.40)
        # A 3x-long draw off the high end of the k band must still fit.
        req = self.mp["courseCorrectPeriapsisMeters"]
        self.assertLess(0.675 * req * 3.0, ceiling)

    def test_the_correction_request_sits_in_the_measured_k_band(self):
        req = self.mp["courseCorrectPeriapsisMeters"]
        self.assertGreater(req / SOI_MINMUS, 0.04,
                           "below ~4% req/SOI the finding-16d bias inverts")
        self.assertAlmostEqual(11.12, 100.0 * req / SOI_MINMUS, delta=0.1)
        # Even a 3x-short draw off the low end clears the arrival floor.
        self.assertGreater(0.55 * req / 3.0,
                           self.mp["targetPeriapsisFloorMeters"])

    def test_the_correction_cap_is_below_the_stage_two_transfer_it_refines(self):
        cap = self.mp["maxCorrectionDvMps"]
        self.assertEqual(75, cap)
        self.assertLess(cap, STAGE2_WORST_MPS / 2.0)
        self.assertGreater(cap, 50, "B20's lesson: an under-sized cap discards "
                                    "the encounter-creating correction")

    def test_the_spec_points_at_the_parsek_stripped_fixture(self):
        self.assertEqual("fixtures/saves/mun-park-kerbalx",
                         self.spec["fixture"]["saveTemplate"])
        self.assertEqual("none", self.spec["fixture"]["injectedRecordings"])
        self.assertEqual([], self.spec["fixture"]["craft"])

    def test_the_stripped_fixture_really_carries_no_parsek_state(self):
        """BYTE-LEVEL, because the strip is what stops the hop being appended to
        `mun-orbit-recorded`'s committed tree (B23 flight 1's finding). The node
        itself must SURVIVE - a flyable template needs it - while every child
        type must be gone."""
        path = os.path.join(HARNESS_ROOT, "fixtures", "saves",
                            "mun-park-kerbalx", "persistent.sfs")
        with open(path, encoding="utf-8") as fh:
            text = fh.read()
        self.assertIn("name = ParsekScenario", text)
        for child in ("RECORDING_TREE", "GROUP_HIERARCHY", "MILESTONE_STATE",
                      "KERBAL_SLOTS", "CREW_REPLACEMENTS"):
            self.assertNotIn(child, text, "%s survived the strip" % child)
        # The park the spec is sized against is still the park on disk.
        self.assertIn("SMA = %r" % PARK_SMA, text)
        self.assertIn("Jebediah Kerman", text)
        self.assertFalse(os.path.isdir(os.path.join(
            HARNESS_ROOT, "fixtures", "saves", "mun-park-kerbalx", "Parsek")))

    def test_the_forbidden_list_guards_the_parent_escape_not_a_sibling_shell(self):
        """**THE DERIVATION THAT HAS NO B26 ANALOGUE.** Minmus is Kerbin's
        OUTERMOST moon, so there is no sibling shell above the transfer to cross
        and B26's six poison tokens have nothing to point at. What replaces them
        is sharper: the parent-escape pair, which guards a REAL and measured
        tail rather than an unlikely one."""
        forbidden = self.spec["expectations"]["logContracts"]["forbidden"]
        self.assertIn("SOI change boundary suppressed in tree mode: \\w+ to Sun",
                      forbidden)
        self.assertIn("SOI change boundary suppressed in tree mode: Sun to \\w+",
                      forbidden)
        # No moon of Kerbin orbits above Minmus, so no sibling-shell token is
        # derivable - and none is present.
        for body in ("Tylo", "Bop", "Pol", "Vall", "Laythe"):
            for token in forbidden:
                self.assertNotIn(body, token,
                                 "a Jool-system token was copied into a "
                                 "Kerbin-system lane: %r" % token)
        # A `Kerbin to Mun` re-entry is NOT forbidden: the transfer's periapsis
        # is at Mun's own radius by construction, so a re-cross is a reading.
        self.assertFalse(any("to Mun" in t for t in forbidden))

    def test_the_required_tokens_witness_both_seams_and_the_terminal_body(self):
        required = self.spec["expectations"]["logContracts"]["required"]
        self.assertIn("SOI change boundary suppressed in tree mode: Mun to Kerbin",
                      required)
        self.assertIn("SOI change boundary suppressed in tree mode: Kerbin to Minmus",
                      required)
        self.assertTrue(any("terminalOrbitBody=Minmus" in t for t in required))

    def test_the_spec_arms_no_gating_block(self):
        exp = self.spec["expectations"]
        self.assertEqual([], exp["allowedAnomalies"])
        self.assertNotIn("structure", exp.get("recordings", {}))
        self.assertNotIn("gating", exp.get("rewind", {}))

    def test_nothing_reaim_is_required_or_forbidden(self):
        """The routing is V21M/V21T's measurement, not this lane's claim."""
        lc = self.spec["expectations"]["logContracts"]
        for token in ("[ReaimDiag]", "ENGAGED re-aim", "FORCED FAITHFUL",
                      "PhaseLock APPLIED"):
            for arr in (lc["required"], lc["forbidden"]):
                self.assertFalse(any(token in t for t in arr),
                                 "%r must be neither required nor forbidden"
                                 % token)
        self.assertNotIn("D11", self.spec["dimensionsCovered"])


class GravityTableTests(unittest.TestCase):
    """The one `mlib` change this lane needed, and the discipline around it."""

    def test_the_mun_row_is_present_and_cited(self):
        self.assertIn("Mun", mlib.STOCK_BODY_GRAVITY)
        mu, radius, soi = mlib.STOCK_BODY_GRAVITY["Mun"]
        self.assertEqual(MU_MUN, mu)
        self.assertEqual(R_MUN, radius)
        self.assertAlmostEqual(SOI_MUN, soi, delta=1.0)

    def test_the_relay_would_refuse_a_home_body_with_no_row(self):
        """The load-time gate that makes an uncited body a loud parse failure
        rather than a mis-sized burn."""
        mp = dict(_spec()["driver"]["missionParams"])
        # A body with no cited row, and NOT the target (equal home/target is a
        # different, earlier load-time refusal).
        mp["homeBodyName"] = "Ike"
        with self.assertRaises(ValueError) as ctx:
            mlib.b5_params_from_dict(mp)
        self.assertIn("STOCK_BODY_GRAVITY", str(ctx.exception))


if __name__ == "__main__":
    unittest.main()
