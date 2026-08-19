"""Unit tests for the b26_laythe_vall lane and its V17 loop pair.

WHAT IS DIFFERENT ABOUT THIS FILE, and it is the reason it is not just another
copy of `test_b25_laythe_orbit.py`: B26 is the first lane to COMPOSE the b5
interplanetary transfer path with the orbit-start entry door, and to run the
former with JOOL as the transfer frame rather than the Sun. The audit that
established the composition is clean lives in the spec header; what lives HERE is
the subset of it that is mechanically checkable, so a future mlib change that
breaks the composition reds in `harness/missions/lib` rather than on a
6,000-second flight:

  * `CompositionAuditTests` drives the real `mlib` predicates with the committed
    spec's own params - the burn-done evidence across three frame shapes, the
    coast/warp/correction body domains, the entry gate, and the mutual
    reachability of the two blocks.
  * `EjectionFloorTests` is the single most flight-costing value in the spec, and
    it gets a cell of its own: B7's 1.05 would NEVER be reached here.

Runnable with the stdlib runner only (NO pytest, NO kRPC, NO KSP, NO network)::

    cd harness && python -m unittest discover -s missions/lib -q
"""

import math
import os
import tomllib
import unittest

import mlib

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(
    os.path.abspath(__file__))))

# STOCK CONSTANTS. KSP's bodies are on rails with elements that never change, so
# these are DATA and not tolerances. The Jool-system rows are the committed table
# in `docs/dev/research/same-parent-reaim-jool-system.md` section 3.1.
MU_JOOL = 2.82528e14
A_LAYTHE = 27_184_000.0
A_VALL = 43_152_000.0
A_TYLO = 68_500_000.0
A_BOP = 128_500_000.0
A_POL = 179_890_000.0

MU_LAYTHE = 1.962e12
R_LAYTHE = 500_000.0
SOI_LAYTHE = 3_723_645.8
LAYTHE_ATMOSPHERE_TOP_M = 50_000.0

MU_VALL = 2.074e11
R_VALL = 300_000.0
SOI_VALL = 2_406_401.4
VALL_HIGHEST_TERRAIN_M = 8_000.0

# The fixture's MEASURED Laythe park, read off
# `fixtures/saves/laythe-park-nerv/persistent.sfs` (unchanged by the strip - it
# removed only Parsek's own state, never the world). It is B25's delivered park.
PARK_SMA = 572_085.80370560708
PARK_ECC = 0.027697758715375592
PARK_INC_DEG = 16.372447690149517
PARK_UT = 28_817_025.337051559
PARK_LF = 534.8686018902445

# The pessimistic FLOOR of Vall's SOI-entry -> periapsis coast, from the spec
# header: the regime is GRAVITY-LEANING (v_inf^2 / (2*mu/r_soi) = 0.554), so the
# path is solved on the approach hyperbola and comes out 3,871.8 s (at the 20 km
# park floor) to 4,281.6 s (at the 600 km ceiling). 3,860 sits under the whole
# band - and the band is checked at BOTH ends of the PARK window rather than of
# the narrower arrival window, which is the conservative reading.
VALL_APPROACH_COAST_FLOOR_SECONDS = 3860.0

NOMINAL_POLL_SECONDS = 0.5
PESSIMISTIC_POLL_SECONDS = 4.0

# DERIVED IN B26'S HEADER, re-stated as the two numbers every budget leans on.
DV_AVAILABLE_MPS = 2483.9
WORST_HOP_MPS = 1385.1


def _spec(name):
    with open(os.path.join(HARNESS_ROOT, "scenarios", name), "rb") as fh:
        return tomllib.load(fh)


def snap(**kw):
    return mlib.TelemetrySnapshot(**kw)


def parked_snap(**overrides):
    """A frame reading the committed `laythe-park-nerv` fixture's MEASURED Laythe
    park. Periapsis / apoapsis derived from the save's own SMA and ECC."""
    base = dict(ut=PARK_UT, body="Laythe", situation="ORBITING",
                apoapsis=PARK_SMA * (1.0 + PARK_ECC) - R_LAYTHE,
                periapsis=PARK_SMA * (1.0 - PARK_ECC) - R_LAYTHE,
                eccentricity=PARK_ECC,
                altitude=PARK_SMA - R_LAYTHE)
    base.update(overrides)
    return snap(**base)


def hohmann(a1, a2, mu=MU_JOOL):
    """(v_inf at departure, v_inf at arrival, time of flight) for a Hohmann
    transfer between two circular orbits about `mu`."""
    a_t = (a1 + a2) / 2.0
    v1 = math.sqrt(mu * (2.0 / a1 - 1.0 / a_t))
    v2 = math.sqrt(mu * (2.0 / a2 - 1.0 / a_t))
    return (v1 - math.sqrt(mu / a1),
            math.sqrt(mu / a2) - v2,
            math.pi * math.sqrt(a_t ** 3 / mu))


def vall_soi_to_periapsis_seconds(periapsis_altitude):
    """Time from Vall SOI entry to periapsis on the approach hyperbola, by
    hyperbolic Kepler. Required rather than optional: the regime is neither
    gravity-dominated nor negligible."""
    _, v_inf, _ = hohmann(A_LAYTHE, A_VALL)
    a = -MU_VALL / v_inf ** 2
    r_p = R_VALL + periapsis_altitude
    e = 1.0 + r_p / abs(a)
    h = math.acosh((SOI_VALL / abs(a) + 1.0) / e)
    m = e * math.sinh(h) - h
    return m * math.sqrt(abs(a) ** 3 / MU_VALL)


class JoolFrameArithmeticTests(unittest.TestCase):
    """The derivations B26's header states, re-run."""

    def test_the_synodic_is_one_vall_period_because_of_the_resonance(self):
        # THE PLEASING FACT, and the reason this pair is the right FIRST
        # moon-to-moon subject: Vall's period is 2x Laythe's to within 0.3 s
        # (research doc section 3.2), so the transfer window recurs once per Vall
        # orbit almost exactly rather than on some incommensurate beat.
        p_l = 2.0 * math.pi * math.sqrt(A_LAYTHE ** 3 / MU_JOOL)
        p_v = 2.0 * math.pi * math.sqrt(A_VALL ** 3 / MU_JOOL)
        synodic = 1.0 / abs(1.0 / p_l - 1.0 / p_v)
        self.assertAlmostEqual(synodic, 105_961.428, delta=0.5)
        self.assertAlmostEqual(synodic / p_l, 2.0, delta=0.001)
        self.assertAlmostEqual(abs(p_v - 2.0 * p_l), 0.33, delta=0.2)

    def test_the_transfer_is_a_short_elliptic_jool_frame_hohmann(self):
        # The three properties `TrySynthesizeTransfer`'s `IsSaneTransferConic`
        # gate cares about, plus the leg duration every budget is sized on.
        a_t = (A_LAYTHE + A_VALL) / 2.0
        e_t = (A_VALL - A_LAYTHE) / (A_VALL + A_LAYTHE)
        self.assertGreater(a_t, 0.0)
        self.assertGreaterEqual(e_t, 0.0)
        self.assertLess(e_t, 1.0)
        self.assertAlmostEqual(e_t, 0.22702, delta=0.0005)
        _, _, tof = hohmann(A_LAYTHE, A_VALL)
        self.assertAlmostEqual(tof, 38_979.9, delta=5.0)

    def test_the_vall_approach_regime_is_the_fourth_distinct_answer(self):
        # Ike 0.24 (gravity dominates), Laythe 1.445 (mixed), Gilly 11,600
        # (negligible), Vall 0.554 (gravity-leaning). Neither approximation is
        # honest at 0.554, so the hyperbola is solved.
        _, v_inf, _ = hohmann(A_LAYTHE, A_VALL)
        ratio = v_inf ** 2 / (2.0 * MU_VALL / SOI_VALL)
        self.assertAlmostEqual(v_inf, 309.12, delta=0.5)
        self.assertAlmostEqual(ratio, 0.554, delta=0.01)
        self.assertGreater(ratio, 0.2)
        self.assertLess(ratio, 2.0)

    def test_the_approach_coast_floor_is_under_the_whole_park_window(self):
        # The floor every warp number is sized against, checked at BOTH ends of
        # the admissible park window rather than at one convenient point.
        mp = _spec("B26-laythe-vall-transfer.toml")["driver"]["missionParams"]
        for pe in (mp["parkMinPeriapsisMeters"], mp["parkMaxApoapsisMeters"]):
            self.assertLessEqual(VALL_APPROACH_COAST_FLOOR_SECONDS,
                                 vall_soi_to_periapsis_seconds(float(pe)),
                                 "the sizing floor is ABOVE the actual coast at a "
                                 "%s m periapsis" % pe)

    def test_the_delta_v_is_what_the_fixture_bytes_give(self):
        dry = 6.78 + 0.20 + 0.04 + 0.16
        wet = dry + PARK_LF * 0.005
        dv = 800.0 * 9.80665 * math.log(wet / dry)
        self.assertAlmostEqual(dv, DV_AVAILABLE_MPS, delta=1.0)

    def test_the_worst_hop_leaves_a_real_margin_and_the_plane_term_is_why_it_is_tight(self):
        """The header's verdict re-derived, INCLUDING the term that makes this
        lane's margin 82% where B25's was 152%: B25 delivered a park inclined
        16.372 deg about Laythe, and nothing on an orbit-start lane can trim it
        (`parkTrimEccMax`'s round-out step is reachable only from
        `B5_CIRCULARIZE`)."""
        mp = _spec("B26-laythe-vall-transfer.toml")["driver"]["missionParams"]
        v_inf_dep, v_inf_arr, _ = hohmann(A_LAYTHE, A_VALL)
        v_circ = math.sqrt(MU_LAYTHE / PARK_SMA)
        v_eject = math.sqrt(v_inf_dep ** 2 + 2.0 * MU_LAYTHE / PARK_SMA)
        coplanar = v_eject - v_circ
        self.assertAlmostEqual(coplanar, 790.0, delta=1.0)
        # The pessimistic single-burn bound: rotate the outgoing vector by the
        # park's inclination while changing its magnitude.
        folded = math.sqrt(v_circ ** 2 + v_eject ** 2
                           - 2.0 * v_circ * v_eject
                           * math.cos(math.radians(PARK_INC_DEG)))
        self.assertGreater(folded, coplanar)
        r_p = R_VALL + mp["parkMinPeriapsisMeters"]
        capture = (math.sqrt(v_inf_arr ** 2 + 2.0 * MU_VALL / r_p)
                   - math.sqrt(MU_VALL / r_p))
        worst = folded + capture
        self.assertAlmostEqual(worst, WORST_HOP_MPS, delta=5.0)
        self.assertLess(worst, DV_AVAILABLE_MPS)
        self.assertGreater(DV_AVAILABLE_MPS - worst, 1000.0)


class EjectionFloorTests(unittest.TestCase):
    """THE SINGLE MOST FLIGHT-COSTING VALUE IN THE SPEC, and the audit item that
    would have burned a 6,000-second run if it had been copied by analogy."""

    def test_b7s_floor_would_never_have_been_reached(self):
        """B7/B17/B19/B21/B22 all use `ejectionEccFloor = 1.05`, calibrated on a
        KERBIN escape at ~10x this v_inf. The required Laythe ejection hyperbola
        has ecc 1.0352 - BELOW that floor - so a copied 1.05 would sit above the
        correct answer, the phase would ride to its budget on a perfectly good
        ejection, and the named under-burn give-up would read exactly like a real
        defect."""
        v_inf, _, _ = hohmann(A_LAYTHE, A_VALL)
        required_ecc = 1.0 + PARK_SMA * v_inf ** 2 / MU_LAYTHE
        self.assertAlmostEqual(required_ecc, 1.0352, delta=0.002)
        self.assertLess(required_ecc, 1.05,
                        "the required ejection eccentricity now clears B7's 1.05, "
                        "so this lane's whole reason for 1.001 needs re-reading")

    def test_the_committed_floor_discriminates_the_park_from_the_ejection(self):
        mp = _spec("B26-laythe-vall-transfer.toml")["driver"]["missionParams"]
        floor = mp["ejectionEccFloor"]
        v_inf, _, _ = hohmann(A_LAYTHE, A_VALL)
        required_ecc = 1.0 + PARK_SMA * v_inf ** 2 / MU_LAYTHE
        # Above 1 (a bound park can never reach it) and comfortably below the
        # ejection the transfer actually needs.
        self.assertGreater(floor, 1.0)
        self.assertGreater(floor, PARK_ECC * 30.0)
        self.assertLess(floor, required_ecc)
        self.assertGreater(required_ecc - floor, 0.03)


class CompositionAuditTests(unittest.TestCase):
    """THE AUDIT'S MECHANICALLY CHECKABLE HALF, driven through the real `mlib`
    predicates with the committed spec's own params. If a future mlib change
    breaks the interplanetary-path / orbit-start composition, it reds here."""

    @classmethod
    def setUpClass(cls):
        cls.spec = _spec("B26-laythe-vall-transfer.toml")
        cls.mp = cls.spec["driver"]["missionParams"]
        cls.params = mlib.b5_params_from_dict(cls.mp)

    def test_the_two_blocks_compose_without_a_parse_rejection(self):
        # `b5_params_from_dict` raises on three flag combinations; this pair is
        # not among them, and PAD-ALIGN must stay unreachable.
        self.assertTrue(self.params.start_in_orbit)
        self.assertTrue(self.params.interplanetary_transfer)
        self.assertFalse(self.params.pad_align_ejection)
        self.assertNotIn("padAlignEjection", self.mp)

    def test_the_frame_is_jool_everywhere_it_is_read(self):
        self.assertEqual("Laythe", self.params.home_body)
        self.assertEqual("Vall", self.params.target_body)
        self.assertEqual("Jool", self.params.return_body)
        self.assertEqual(("Jool",), self.params.via_bodies)
        # The coast admits the home body, the transfer frame and a blank reading -
        # and nothing else, so a stray Tylo/Bop/Pol SOI is an ejection.
        self.assertEqual(("", "Laythe", "Jool"),
                         mlib._b5_coast_bodies(self.params))
        self.assertEqual(("Laythe", "Jool"), mlib._b5_warp_bodies(self.params))

    def test_the_correction_domain_narrows_to_an_identity(self):
        """`_b5_correction_via_bodies`' documented precondition is that
        `return_body` be a member of `via_bodies` - without it the narrowing ADDS
        a firing opportunity instead of removing one. Here the two are the same
        body, so the narrowing is an identity and the safety argument holds
        trivially."""
        self.assertIn(self.params.return_body, self.params.via_bodies)
        self.assertEqual(("Jool",),
                         mlib._b5_correction_via_bodies(self.params))
        self.assertTrue(set(mlib._b5_correction_via_bodies(self.params))
                        <= set(self.params.via_bodies))

    def test_the_burn_done_evidence_reads_the_laythe_frame_and_the_early_return(self):
        """The ecc branch across all three frame shapes the flight passes
        through. This is the composition's load-bearing predicate: the FLOOR is
        read in Laythe's frame while still in Laythe's SOI, and the early return
        must fire once the craft is in JOOL's."""
        self.assertGreater(self.params.ejection_ecc_floor, 0.0)
        # (a) at the park - bound, must be False.
        self.assertFalse(mlib._b5_transfer_burn_done(self.params, parked_snap()))
        # (b) post-ejection, still in Laythe's SOI - hyperbolic, must be True.
        v_inf, _, _ = hohmann(A_LAYTHE, A_VALL)
        ecc = 1.0 + PARK_SMA * v_inf ** 2 / MU_LAYTHE
        self.assertTrue(mlib._b5_transfer_burn_done(
            self.params, parked_snap(eccentricity=ecc, situation="ESCAPING",
                                     apoapsis=-1.0)))
        # (c) in Jool's SOI - the early return, whatever the ecc reads there.
        self.assertTrue(mlib._b5_transfer_burn_done(
            self.params, snap(ut=PARK_UT + 1e4, body="Jool",
                              situation="ORBITING", eccentricity=0.227,
                              altitude=2.0e7)))
        # (d) INERT counterpart: the apoapsis floor is kept present at 0 (B15's
        # disposition for a schema-required key the evidence does not use).
        self.assertEqual(0, self.mp["transferMinApoapsisMeters"])

    def test_the_entry_gate_admits_the_committed_fixtures_measured_park(self):
        # THE cell that couples the spec to the fixture. If either moves, this
        # reds before a KSP boot is spent.
        verdict, reason = mlib.start_in_orbit_frame_verdict(
            self.params, parked_snap())
        self.assertEqual(verdict, mlib.START_IN_ORBIT_IN_GATE, reason)

    def test_the_entry_ecc_ceiling_is_wider_than_its_predecessors_and_why(self):
        """B23/B24/B25 all use 0.05, but their start parks had been rounded out by
        a CIRCULARIZE. This one is the delivered product of a finite capture burn
        and reads 0.0277, which 0.05 would admit at only 1.8x."""
        ceiling = self.mp["startInOrbitMaxEccentricity"]
        self.assertGreater(ceiling, 0.05)
        self.assertGreaterEqual(ceiling / PARK_ECC, 3.0)
        # And the reason it still matters: MechJeb's ejection planner sizes at the
        # SEMI-MAJOR AXIS and applies at the burn radius, so an eccentric park
        # mis-prices the escape. Quantified for THIS park, the error must stay a
        # small fraction of v_eject.
        v_inf, _, _ = hohmann(A_LAYTHE, A_VALL)
        v_eject_sq = v_inf ** 2 + 2.0 * MU_LAYTHE / PARK_SMA
        r_lo = PARK_SMA * (1.0 - PARK_ECC)
        err = abs(2.0 * MU_LAYTHE * (1.0 / PARK_SMA - 1.0 / r_lo))
        self.assertLess(err / v_eject_sq, 0.05,
                        "the sma-vs-burn-radius sizing error is over 5% of "
                        "v_eject^2; the park is too eccentric for the "
                        "interplanetary ejection planner")

    def test_the_correction_triggers_are_sized_for_a_jool_system_leg(self):
        """THE AUDIT'S ITEM (1). B7's [20,000,000; 500,000] against a 38,980 s leg
        would put `time_to_soi` below BOTH thresholds on the first Jool-SOI frame,
        spending both rounds back to back at the start of the coast - the
        B15-flight-5 failure SHAPE reached by a different route."""
        _, _, tof = hohmann(A_LAYTHE, A_VALL)
        trig = self.params.correction_trigger_time_to_soi
        self.assertEqual((), self.params.correction_trigger_alts)
        self.assertEqual(2, len(trig))
        self.assertEqual(sorted(trig, reverse=True), list(trig),
                         "the time-to-SOI trigger list must be DESCENDING")
        # Round 0 must fire INSIDE the leg, not before the craft can reach it.
        self.assertLess(trig[0], tof)
        self.assertGreater(trig[0], 0.5 * tof)
        # Round 1 must clear both the in-SOI coast and the hardcoded
        # arrival-quality window, so the scheduled round and the extras cannot
        # collide.
        self.assertGreater(trig[1], VALL_APPROACH_COAST_FLOOR_SECONDS)
        self.assertGreater(trig[1], mlib.ARRIVAL_RECORRECT_MAX_TTS_SECONDS)


class SpecArithmeticTests(unittest.TestCase):
    """Cells that read the COMMITTED B26 spec."""

    @classmethod
    def setUpClass(cls):
        cls.spec = _spec("B26-laythe-vall-transfer.toml")
        cls.mp = cls.spec["driver"]["missionParams"]

    # --- the three-rule warp contract ---------------------------------------

    def test_one_frame_cannot_swallow_the_vall_approach(self):
        rate = mlib.RAILS_WARP_RATES[self.mp["approachMaxWarpFactor"]]
        self.assertLessEqual(rate, VALL_APPROACH_COAST_FLOOR_SECONDS / 5.0)

    def test_the_approach_ceiling_is_also_altitude_legal_to_the_arrival_floor(self):
        """THIS LANE'S SECOND, HARDER REASON for x50 over x100, and it is data
        rather than judgement: Vall's own warp table puts factor 4's floor at
        40,000 m, ABOVE the 30,000 m arrival floor, so a commanded x100 would be
        clamped down on the last stretch anyway."""
        limits = mlib.STOCK_WARP_ALTITUDE_LIMITS["Vall"]
        cap = self.mp["approachMaxWarpFactor"]
        self.assertLessEqual(limits[cap], self.mp["targetPeriapsisFloorMeters"])
        self.assertGreater(limits[4], self.mp["targetPeriapsisFloorMeters"],
                           "factor 4 is now altitude-legal at the arrival floor, "
                           "so the spec's second argument for x50 no longer holds")

    def test_the_soi_lead_exceeds_two_nominal_coast_frames(self):
        coast_rate = mlib.RAILS_WARP_RATES[self.mp["coastWarpFactor"]]
        self.assertGreaterEqual(self.mp["soiLeadSeconds"],
                                2.0 * coast_rate * NOMINAL_POLL_SECONDS)

    def test_the_approach_window_swallows_one_pessimistic_coast_frame(self):
        coast_rate = mlib.RAILS_WARP_RATES[self.mp["coastWarpFactor"]]
        self.assertGreaterEqual(self.mp["approachWindowSeconds"],
                                2.0 * coast_rate * PESSIMISTIC_POLL_SECONDS)
        self.assertGreaterEqual(self.mp["approachWindowSeconds"],
                                2.0 * self.mp["soiLeadSeconds"])

    def test_the_whole_coast_at_the_rails_cap_is_cheap_on_this_leg(self):
        """WHY x1,000 AND NOT x10,000, and it is the inversion of every
        interplanetary sibling's argument: this leg is three orders of magnitude
        shorter than B7's, so even the rails FALLBACK for the entire coast is
        seconds of wall clock."""
        _, _, tof = hohmann(A_LAYTHE, A_VALL)
        rate = mlib.RAILS_WARP_RATES[self.mp["coastWarpFactor"]]
        self.assertLess(tof / rate, 120.0)

    def test_the_in_soi_stair_is_at_or_below_the_approach_ceiling(self):
        cap = self.mp["approachMaxWarpFactor"]
        self.assertLessEqual(self.mp["flybyWarpFactor"], cap)
        self.assertLessEqual(self.mp["flybyMaxWarpFactor"], cap)
        self.assertLessEqual(self.mp["flybyWarpFactor"],
                             self.mp["flybyMaxWarpFactor"])

    # --- budgets -------------------------------------------------------------

    def test_the_transfer_burn_budget_covers_several_synodic_waits(self):
        p_l = 2.0 * math.pi * math.sqrt(A_LAYTHE ** 3 / MU_JOOL)
        p_v = 2.0 * math.pi * math.sqrt(A_VALL ** 3 / MU_JOOL)
        synodic = 1.0 / abs(1.0 / p_l - 1.0 / p_v)
        self.assertGreaterEqual(self.mp["transferBurnTimeoutSeconds"],
                                5.0 * synodic)

    def test_the_coast_budget_covers_several_missed_intercepts(self):
        p_l = 2.0 * math.pi * math.sqrt(A_LAYTHE ** 3 / MU_JOOL)
        p_v = 2.0 * math.pi * math.sqrt(A_VALL ** 3 / MU_JOOL)
        synodic = 1.0 / abs(1.0 / p_l - 1.0 / p_v)
        _, _, tof = hohmann(A_LAYTHE, A_VALL)
        self.assertGreaterEqual(self.mp["coastTimeoutSeconds"],
                                3.0 * (synodic + tof))

    def test_the_flyby_and_capture_budgets_cover_the_in_soi_coast(self):
        coast = vall_soi_to_periapsis_seconds(self.mp["parkMaxApoapsisMeters"])
        self.assertGreaterEqual(self.mp["flybyTimeoutSeconds"], 10.0 * coast)
        self.assertGreaterEqual(self.mp["captureBurnTimeoutSeconds"],
                                10.0 * (coast + 600.0))

    # --- the Vall shell ------------------------------------------------------

    def test_both_floors_clear_valls_terrain_and_are_unequal_in_the_right_order(self):
        """The B19..B24 `parkMin == targetFloor` convention stays broken in the
        direction B25 ESTABLISHED and for its measured reason: a finite capture
        burn drops the delivered periapsis below the arrival periapsis, so the
        park floor must sit BELOW the arrival floor or it reds a healthy park.
        The wedge is predicted much smaller here (a ~49 s burn against a ~3,490 s
        park period, against B25's 163.5 s against ~2,000 s), which is why the gap
        is 10 km rather than B25's 8."""
        park = self.mp["parkMinPeriapsisMeters"]
        arrival = self.mp["targetPeriapsisFloorMeters"]
        self.assertGreater(park, 2.0 * VALL_HIGHEST_TERRAIN_M)
        self.assertGreater(arrival, park)
        self.assertGreaterEqual(arrival - park, 5_000.0)

    def test_the_park_ceiling_admits_the_arrival_spread_and_stays_in_the_shell(self):
        ceiling = self.mp["parkMaxApoapsisMeters"]
        # Must admit a long draw off the high end of the k band B25 measured.
        self.assertGreaterEqual(
            ceiling, 3.0 * 0.55 * self.mp["courseCorrectPeriapsisMeters"])
        self.assertLess(ceiling + R_VALL, SOI_VALL)
        self.assertLessEqual((ceiling + R_VALL) / SOI_VALL, 0.40)

    def test_the_correction_request_is_sized_against_b25s_measured_ratio(self):
        """B25 MEASURED k = 0.283-0.289 at req/SOI 6.71%, refuting its own
        pre-flight assumption of k ~ 1. This request is chosen so that even the
        low end of that band clears the arrival floor with room."""
        req = self.mp["courseCorrectPeriapsisMeters"]
        self.assertGreater(req / SOI_VALL, 0.04)
        floor = self.mp["targetPeriapsisFloorMeters"]
        self.assertGreaterEqual(0.29 * req, 2.0 * floor)
        self.assertLess(0.55 * req * 3.0 + R_VALL, SOI_VALL)

    # --- the dv margin -------------------------------------------------------

    def test_the_four_round_correction_budget_fits_inside_the_margin(self):
        margin = DV_AVAILABLE_MPS - WORST_HOP_MPS
        rounds = (len(self.mp["correctionTriggerTimeToSoiSeconds"])
                  + mlib.MAX_ARRIVAL_EXTRA_ROUNDS)
        self.assertEqual(4, rounds)
        self.assertLessEqual(rounds * self.mp["maxCorrectionDvMps"], margin)

    def test_the_correction_cap_is_below_the_ejection_it_corrects(self):
        v_inf, _, _ = hohmann(A_LAYTHE, A_VALL)
        coplanar = (math.sqrt(v_inf ** 2 + 2.0 * MU_LAYTHE / PARK_SMA)
                    - math.sqrt(MU_LAYTHE / PARK_SMA))
        self.assertLess(self.mp["maxCorrectionDvMps"], coplanar / 3.0)

    # --- fixture + posture ---------------------------------------------------

    def test_the_spec_points_at_the_parsek_stripped_fixture(self):
        self.assertEqual("fixtures/saves/laythe-park-nerv",
                         self.spec["fixture"]["saveTemplate"])

    def test_the_stripped_fixture_really_carries_no_parsek_state(self):
        """Checked against the BYTES rather than the name. NOTE this fixture has
        FOUR child types, not the five its predecessors had - there is no
        `GROUP_HIERARCHY` here - which is why the assertion is written as an
        absence of every known node name rather than as a count."""
        save_dir = os.path.join(HARNESS_ROOT,
                                self.spec["fixture"]["saveTemplate"])
        self.assertTrue(os.path.isdir(save_dir), save_dir)
        self.assertFalse(os.path.isdir(os.path.join(save_dir, "Parsek")))
        with open(os.path.join(save_dir, "persistent.sfs"),
                  encoding="utf-8", errors="replace") as fh:
            sfs = fh.read()
        for node in ("RECORDING_TREE", "GROUP_HIERARCHY", "MILESTONE_STATE",
                     "KERBAL_SLOTS", "CREW_REPLACEMENTS"):
            self.assertNotIn(node, sfs,
                             "%s survived in the stripped fixture" % node)
        self.assertIn("name = ParsekScenario", sfs)
        # The WORLD must be untouched: the crewed subject and its Laythe park.
        self.assertIn("Valentina Kerman", sfs)
        self.assertIn("SMA = 572085", sfs)

    def test_the_recordings_window_is_wider_than_its_predecessors_and_why(self):
        """Every earlier lane's product had ONE body change and a {1,2} window.
        This recording has TWO (an ESCAPE under thrust and a rails ARRIVAL), and
        section-3 rule 3's cohesion is argued for a transfer COAST - so the
        optimizer's answer at the escape boundary is genuinely unknown."""
        rec_dir = os.path.join(HARNESS_ROOT,
                               self.spec["fixture"]["saveTemplate"],
                               "Parsek", "Recordings")
        carried = (len([n for n in os.listdir(rec_dir) if n.endswith(".prec")])
                   if os.path.isdir(rec_dir) else 0)
        self.assertEqual(0, carried)
        self.assertEqual({"min": carried + 1, "max": carried + 3},
                         self.spec["expectations"]["recordings"]["count"])

    def test_every_moon_above_the_transfer_apoapsis_is_a_named_poison_token(self):
        """The B17 pattern, with the list DERIVED: the coast climbs from Laythe's
        27.184 Mm to Vall's 43.152 Mm, so Tylo / Bop / Pol all sit ABOVE the
        transfer apoapsis and are geometrically excluded rather than merely
        unlikely."""
        forbidden = self.spec["expectations"]["logContracts"]["forbidden"]
        above = [name for name, a in (("Tylo", A_TYLO), ("Bop", A_BOP),
                                      ("Pol", A_POL)) if a > A_VALL]
        self.assertEqual(["Tylo", "Bop", "Pol"], above)
        for moon in above:
            self.assertIn(
                "SOI change boundary suppressed in tree mode: \\w+ to %s" % moon,
                forbidden)
            self.assertIn(
                "SOI change boundary suppressed in tree mode: %s to \\w+" % moon,
                forbidden)
        req = self.spec["expectations"]["logContracts"]["required"]
        self.assertIn(
            "SOI change boundary suppressed in tree mode: Laythe to Jool", req)
        self.assertIn(
            "SOI change boundary suppressed in tree mode: Jool to Vall", req)

    def test_the_spec_arms_no_gating_block(self):
        for block in ("rewind", "recordings"):
            sub = (self.spec.get("expectations") or {}).get(block) or {}
            self.assertNotIn("gating", sub)
            for nested in sub.values():
                if isinstance(nested, dict):
                    self.assertNotIn("gating", nested)


class V17SeedTests(unittest.TestCase):
    """The V17 pair ships with the WEAKEST seeds of the family - the routing is
    the measurement, so the anchor comes from a planner nobody has picked yet -
    and what can be checked is the internal consistency of the shape."""

    PLACEHOLDER_TREE = "0" * 32
    UT0 = 28_817_026.0
    SPAN = 96_874.9
    DEST_PHASE = 4_425.0
    PARK_FRACTION = 0.707

    @classmethod
    def setUpClass(cls):
        cls.m = _spec("V17M-laythe-vall-player-loop.toml")
        cls.t = _spec("V17T-laythe-vall-ts-arrival.toml")

    def _jump_uts(self, spec):
        return [float(s["args"]["ut"]) for s in spec["driver"]["steps"]
                if s.get("cmd") == "TimeJump"]

    def _synodic(self):
        p_l = 2.0 * math.pi * math.sqrt(A_LAYTHE ** 3 / MU_JOOL)
        p_v = 2.0 * math.pi * math.sqrt(A_VALL ** 3 / MU_JOOL)
        return 1.0 / abs(1.0 / p_l - 1.0 / p_v)

    def test_both_specs_carry_the_placeholder_tree_id(self):
        for spec in (self.m, self.t):
            trees = [s["args"]["tree"] for s in spec["driver"]["steps"]
                     if s.get("cmd") in ("MissionConfig", "StartLoopPlayback")]
            self.assertTrue(trees)
            for tree in trees:
                self.assertEqual(self.PLACEHOLDER_TREE, tree)

    def test_the_jump_table_is_seeded_from_the_stated_inputs(self):
        anchor = self.UT0 + self._synodic()
        seam_off = self.SPAN - self.DEST_PHASE
        uts = self._jump_uts(self.m)
        for got, want in zip(uts[:3], (seam_off - 180.0, seam_off - 60.0,
                                       seam_off + 140.0)):
            self.assertAlmostEqual(got, round(anchor + want), delta=0.5)
        park_off = seam_off + self.PARK_FRACTION * self.DEST_PHASE
        self.assertAlmostEqual(uts[3], round(anchor + park_off), delta=0.5)

    def test_the_cycle_two_bracket_is_one_SYNODIC_past_cycle_one(self):
        """Under H1 the cadence is the moon-moon synodic, NOT a moon period - the
        thing that most distinguishes this pair's arithmetic from V14/V15/V16's.
        The cell pins the shape so a well-meaning edit cannot quietly re-base it
        on a period."""
        syn = self._synodic()
        uts = self._jump_uts(self.m)
        self.assertEqual(8, len(uts))
        for c1, c2 in zip(uts[:4], uts[4:]):
            self.assertAlmostEqual(c2 - c1, syn, delta=1.5)
        p_l = 2.0 * math.pi * math.sqrt(A_LAYTHE ** 3 / MU_JOOL)
        self.assertGreater(uts[4] - uts[0], 1.5 * p_l)

    def test_v17m_jumps_are_strictly_forward(self):
        uts = self._jump_uts(self.m)
        self.assertEqual(sorted(uts), uts)
        self.assertEqual(len(set(uts)), len(uts))

    def test_v17t_reuses_v17ms_third_cycle_one_bracket_jump(self):
        t_jumps = self._jump_uts(self.t)
        self.assertEqual(1, len(t_jumps))
        self.assertEqual(self._jump_uts(self.m)[2], t_jumps[0])

    def test_both_specs_point_at_the_same_pending_fixture(self):
        self.assertEqual(self.m["fixture"]["saveTemplate"],
                         self.t["fixture"]["saveTemplate"])
        self.assertEqual("fixtures/saves/vall-transfer-recorded",
                         self.m["fixture"]["saveTemplate"])
        # It must NOT exist yet; the PENDING_FIXTURE_LANES cell reds when it does.
        self.assertFalse(os.path.isdir(os.path.join(
            HARNESS_ROOT, "fixtures", "saves", "vall-transfer-recorded")))

    def test_the_reaim_trio_is_neither_required_nor_forbidden_on_either_lane(self):
        """THE POSTURE THAT MAKES THIS PAIR A MEASUREMENT RATHER THAN A REPEAT,
        and the exact inversion of V14M/V15M/V16M, which FORBID all three because
        on a same-parent subject re-aim is structurally unreachable. Here re-aim
        is the thing that might legitimately happen: forbidding these tokens would
        red the interesting outcome, and requiring them would gate on a
        prediction."""
        trio = ("\\[ReaimDiag\\]", "ENGAGED re-aim", "FORCED FAITHFUL")
        for spec in (self.m, self.t):
            lc = spec["expectations"]["logContracts"]
            for tok in trio:
                self.assertNotIn(tok, lc["forbidden"])
                self.assertNotIn(tok, lc["required"])
            # ... and neither may require a PHASE-LOCK-road artifact either, for
            # the mirror-image reason.
            for tok in lc["required"]:
                self.assertNotIn("PhaseLock APPLIED", tok)
                self.assertNotIn("same-parent", tok)

    def test_the_backward_jump_guard_is_required_on_both_lanes(self):
        """On a seed set derived from a HYPOTHESIS rather than a known road, this
        is the most load-bearing forbid in either file: a refused backward jump is
        only a WARN, so without it a mis-seeded bracket greens while dwelling
        nowhere - which both specs say is the LIKELY outcome of run 1."""
        for spec in (self.m, self.t):
            self.assertIn("timejump refused reason=backward-jump",
                          spec["expectations"]["logContracts"]["forbidden"])

    def test_only_v17m_carries_the_census_pacing_block(self):
        m_ticks = sum(1 for s in self.m["driver"]["steps"]
                      if s.get("cmd") == "RecordingState")
        t_ticks = sum(1 for s in self.t["driver"]["steps"]
                      if s.get("cmd") == "RecordingState")
        self.assertGreaterEqual(m_ticks * 0.25, 2.0 * 5.0)
        self.assertEqual(2, t_ticks)

    def test_neither_v17_spec_arms_a_gating_block(self):
        for spec in (self.m, self.t):
            for block in ("rewind", "recordings"):
                sub = (spec.get("expectations") or {}).get(block) or {}
                self.assertNotIn("gating", sub)
                for nested in sub.values():
                    if isinstance(nested, dict):
                        self.assertNotIn("gating", nested)

    def test_the_anomaly_posture_is_the_established_pair_shape(self):
        """V17M is the stepped-bracket CONTROL and V17T ships untolerated so its
        pre-registered `icon-off-orbit` red can be MEASURED rather than swallowed
        - the V15/V16 shape, and the reason those findings could be trusted."""
        self.assertEqual([], self.m["expectations"]["allowedAnomalies"])
        self.assertEqual([], self.t["expectations"]["allowedAnomalies"])


# ---------------------------------------------------------------------------
# Flight 1: the MechJeb blocker, pinned from the run bytes.
# ---------------------------------------------------------------------------

# MEASURED on `2026-08-19_2215_B26-laythe-vall-transfer_a2` (run `_2214` agrees to
# six decimals). These are the three numbers out of MechJeb's own exception, and
# they are pinned here so a future edit to the flight-1 ledger cannot quietly
# restate the arithmetic wrongly. See docs/dev/todo-and-known-bugs.md ->
# MECHJEB-INTERPLANETARY-PLANNER-REJECTS-MOON-ORIGIN.
MJ_REQUESTED_RADIUS_M = 3_723_645.81113302   # Laythe's SOI radius, live
MJ_EJECTION_PER_M = 572_085.800578244        # ~ the park's SMA
MJ_EJECTION_APR_M = 3_632_679.92883477       # 2.443% SHORT of the SOI


class Flight1BlockerTests(unittest.TestCase):
    """B26 flight 1 refused in PLAN-TRANSFER on a MECHJEB limitation, not on
    anything this repo owns. The cells below pin the parts of that ledger that are
    arithmetic rather than narrative, because a ledger nobody can re-derive rots
    into folklore - and this one is load-bearing: it is the reason the lane is
    BLOCKED-PENDING rather than withdrawn, and the reason section 5.1 of the
    research doc is still an open question rather than a refuted one."""

    @classmethod
    def setUpClass(cls):
        cls.spec = _spec("B26-laythe-vall-transfer.toml")

    def test_the_radius_mechjeb_asked_for_is_laythes_soi(self):
        """The single fact that identifies the failure. If this is not the SOI
        radius the whole diagnosis is wrong, so it is checked and not asserted."""
        self.assertAlmostEqual(MJ_REQUESTED_RADIUS_M, SOI_LAYTHE, delta=1.0)

    def test_the_orbit_mechjeb_asked_of_is_not_the_park(self):
        """The park is near-circular (ecc 0.028). The orbit that threw has
        e = 0.7279, so the exception is NOT 'the park never reaches the SOI' -
        it is 'the ejection MechJeb built never reaches the SOI'."""
        pe, ap = MJ_EJECTION_PER_M, MJ_EJECTION_APR_M
        ecc = (ap - pe) / (ap + pe)
        self.assertAlmostEqual(ecc, 0.7279, delta=5e-4)
        self.assertGreater(ecc, 10.0 * PARK_ECC)

    def test_the_ejection_periapsis_is_the_parks_mean_radius(self):
        """WHY the ledger says MechJeb 'idealised the origin to a circle at the
        park's mean radius': the thrown orbit's PeR sits millimetres from the
        fixture park's SMA. Within 0.01 m over a 572 km radius."""
        self.assertAlmostEqual(MJ_EJECTION_PER_M, PARK_SMA, delta=0.01)

    def test_the_ejection_falls_short_of_the_soi_by_the_stated_margin(self):
        """2.443%, i.e. 90,965.88 m. Sub-escape by a small but decisive margin -
        which is why the crossing does not exist and NextTimeOfRadius throws."""
        short = MJ_REQUESTED_RADIUS_M - MJ_EJECTION_APR_M
        self.assertAlmostEqual(short, 90_965.88, delta=1.0)
        self.assertAlmostEqual(100.0 * short / MJ_REQUESTED_RADIUS_M, 2.443,
                               delta=0.01)

    def test_the_park_sits_at_a_large_fraction_of_the_soi_and_kerbins_does_not(self):
        """The GENERAL statement behind the blocker, and the reason eight flown
        interplanetary lanes never hit it: a Laythe park is ~15% of its SOI
        radius, a Kerbin park under 10% of Kerbin's 84,159,286 m. The ratio is
        the variable, not the body."""
        laythe_ratio = PARK_SMA / SOI_LAYTHE
        self.assertGreater(laythe_ratio, 0.10)
        kerbin_soi, kerbin_park = 84_159_286.0, 700_000.0
        self.assertLess(kerbin_park / kerbin_soi, 0.01)
        self.assertGreater(laythe_ratio, 15.0 * (kerbin_park / kerbin_soi))

    def test_the_spec_stays_flyable_but_blocked_and_says_so(self):
        """BLOCKED-PENDING, not withdrawn: the spec is still committed, still
        parses, still carries the pointer to its todo entry, and its status is
        stated in the description rather than only in a comment block."""
        desc = self.spec["description"]
        self.assertIn("BLOCKED-PENDING", desc)
        self.assertIn("MECHJEB-INTERPLANETARY-PLANNER-REJECTS-MOON-ORIGIN", desc)
        # and the lane is not silently disarmed while blocked
        self.assertEqual("operator", self.spec["tier"])

    def test_the_ledger_does_not_claim_a_mechanism_nobody_read(self):
        """The one discipline this ledger can get mechanically checked: it must
        NOT assert why MechJeb sizes the ejection short. MechJeb 2.15.1 was not
        decompiled, and a plausible mechanism written as fact is exactly the kind
        of thing a later reader would build on."""
        with open(os.path.join(HARNESS_ROOT, "scenarios",
                               "B26-laythe-vall-transfer.toml"),
                  encoding="utf-8") as fh:
            body = fh.read()
        self.assertIn("WHAT IS **NOT**", body)
        self.assertIn("was not read", body)


if __name__ == "__main__":
    unittest.main()
