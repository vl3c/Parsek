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
import sys
import tomllib
import unittest

_MISSIONS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)

import mission_runner          # noqa: E402
import mlib                    # noqa: E402

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
RJ_ = 6_000_000.0          # Jool's equatorial radius (kRPC apsides are ALTITUDES)

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

# DERIVED IN B26'S HEADER, re-stated as the numbers every budget leans on.
# WORST_HOP MOVED WHEN THE PARENT-RELAY LANDED, and in both directions at once:
# the old 1,385.1 priced a MechJeb ejection with the plane term folded in
# pessimistically (1,010.4) plus the capture. The relay's escape is CHEAPER
# (774.70, because it is sized by vis-viva at the periapsis it actually burns at)
# but its stage-2 transfer is UNAIMED, so the plane term moves out of the ejection
# and into a parent-frame bound of 2*v_inf.
DV_AVAILABLE_MPS = 2483.9
# RE-DERIVED AFTER FLIGHT 2. The first cut sized the escape for a hyperbolic
# excess AT INFINITY and flew 774.70 m/s, which delivered 1,083.69 m/s across
# Laythe's SOI (3.12x) - see Flight2DefectATests. The corrected SOI-referenced
# contract is CHEAPER on the escape and DEARER on stage 2, because stage 2's
# bound is 2*|v_rel| and |v_rel| is now the honest 450 rather than a believed 347.
ESCAPE_NODE_MPS = 586.69          # the relay's stage-1 node, at the fixture's park
ESCAPE_SOI_SPEED_MPS = 450.0      # the committed escapeSoiSpeedMps
NOMINAL_HOP_MPS = 1530.8          # escape + typical stage 2 + a ~100 km capture
WORST_HOP_MPS = 1861.4            # escape + worst-direction stage 2 + a 20 km capture
# MEASURED on run 2026-08-20_1701_B26-laythe-vall-transfer_a2 (run _1646 agrees).
F2_NODE_DV_MPS = 774.705          # what the pre-fix contract planned
F2_NODE_UT = 28_818_204.529
F2_LF_BEFORE = 534.869
F2_LF_AFTER = 351.228
F2_ECC_AFTER = 1.035              # Laythe-frame, post-escape
F2_STAGE2_AP_M = 126_333_908.680  # Jool-frame apoapsis ALTITUDE at stage-2 entry
F2_STAGE2_ALT_M = 20_968_630.330
F2_TTS_VALL = 145_030.028         # the two clocks that flapped
F2_TTS_LAYTHE = 123_077.921
F2_THRASH_UT = 29_041_600.577
F2_THRASH_TARGET = 29_062_180.256


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

    def test_the_worst_relay_hop_leaves_a_real_margin_and_the_unaimed_escape_is_why_it_is_tight(self):
        """The header's verdict re-derived FOR THE RELAY, term by term, including
        the one that replaced the old plane-change bound: the escape is not aimed,
        so stage 2 pays up to 2*v_inf to turn whatever asymptote came out into the
        one the transfer wants."""
        mp = _spec("B26-laythe-vall-transfer.toml")["driver"]["missionParams"]
        v_inf_dep, v_inf_arr, _ = hohmann(A_LAYTHE, A_VALL)
        # STAGE 1, at the PERIAPSIS the relay places its node at, sized against
        # the SOI BOUNDARY rather than infinity (flight 2's defect-A fix).
        r_pe = PARK_SMA * (1.0 - PARK_ECC)
        v_pe = math.sqrt(MU_LAYTHE * (2.0 / r_pe - 1.0 / PARK_SMA))
        escape = (math.sqrt(ESCAPE_SOI_SPEED_MPS ** 2
                            + 2.0 * MU_LAYTHE * (1.0 / r_pe - 1.0 / SOI_LAYTHE))
                  - v_pe)
        self.assertAlmostEqual(escape, ESCAPE_NODE_MPS, delta=0.05)
        # STAGE 2: the parent-frame vector difference, bounded by 2*|v_rel|
        # however the departure direction comes out. The PLANE TERM IS INSIDE
        # this bound, not added to it.
        out_of_plane = ESCAPE_SOI_SPEED_MPS * math.sin(math.radians(PARK_INC_DEG))
        self.assertAlmostEqual(out_of_plane, 126.8, delta=0.5)
        self.assertLess(out_of_plane, 2.0 * ESCAPE_SOI_SPEED_MPS)
        stage2_worst = 2.0 * ESCAPE_SOI_SPEED_MPS
        stage2_typical = (4.0 / 3.0) * ESCAPE_SOI_SPEED_MPS
        self.assertAlmostEqual(stage2_worst, 900.0, delta=0.5)
        self.assertAlmostEqual(stage2_typical, 600.0, delta=0.5)
        # THE IDEAL IS NOT DELIVERABLE, and that asymmetry is the point: the
        # transfer wants 347.245 and the SOI geometry cannot hand over less than
        # 370.08, so 450 is a compromise rather than a target.
        self.assertGreater(ESCAPE_SOI_SPEED_MPS, v_inf_dep)
        # CAPTURE at the most expensive periapsis the park window admits.
        r_p = R_VALL + mp["parkMinPeriapsisMeters"]
        capture = (math.sqrt(v_inf_arr ** 2 + 2.0 * MU_VALL / r_p)
                   - math.sqrt(MU_VALL / r_p))
        worst = escape + stage2_worst + capture
        self.assertAlmostEqual(worst, WORST_HOP_MPS, delta=1.0)
        self.assertLess(worst, DV_AVAILABLE_MPS)
        self.assertGreater(DV_AVAILABLE_MPS - worst, 600.0)
        # FOUR correction rounds at the committed cap must still fit.
        rounds = (len(mp["correctionTriggerTimeToSoiSeconds"])
                  + mlib.MAX_ARRIVAL_EXTRA_ROUNDS)
        self.assertLessEqual(rounds * mp["maxCorrectionDvMps"],
                             DV_AVAILABLE_MPS - worst)

    def test_the_margin_agrees_with_the_propellant_mass_it_is_made_of(self):
        """THE CROSS-CHECK the header claims to 0.4 m/s, and it is worth a cell
        because the two derivations are independent: one subtracts dv figures,
        the other burns the actual tank. A drift between them means one of the
        hop terms is wrong."""
        dry, wet = 7.180, 9.854
        ve = 800.0 * 9.80665
        remaining = wet / math.exp(WORST_HOP_MPS / ve)
        self.assertGreater(remaining, dry, "the worst hop must not run it dry")
        by_mass = ve * math.log(remaining / dry)
        by_subtraction = DV_AVAILABLE_MPS - WORST_HOP_MPS
        self.assertAlmostEqual(by_mass, by_subtraction, delta=1.0)
        self.assertAlmostEqual(by_subtraction, 622.5, delta=1.0)

    def test_the_unaimed_escape_puts_tylos_shell_back_in_reach(self):
        """THE ONE THING THE RELAY MADE WORSE, and it is measured rather than
        asserted because the spec's forbidden-token block now rests on it. The
        pre-relay claim was that Tylo / Bop / Pol are GEOMETRICALLY EXCLUDED
        because a Hohmann coast reaches only Vall's orbit. That rested on an
        AIMED ejection. Swept over every v_inf direction and every exit point on
        Laythe's SOI shell, the unaimed escape's parent-frame apoapsis reaches
        past Tylo -- so those tokens stopped being a restatement of geometry."""
        v_inf, _, _ = hohmann(A_LAYTHE, A_VALL)
        v_moon = math.sqrt(MU_JOOL / A_LAYTHE)
        worst_ap, worst_pe = 0.0, float("inf")
        for deg in range(0, 181):
            th = math.radians(deg)
            for r in (A_LAYTHE - SOI_LAYTHE, A_LAYTHE, A_LAYTHE + SOI_LAYTHE):
                vt = v_moon + v_inf * math.cos(th)
                vr = v_inf * math.sin(th)
                energy = (vt * vt + vr * vr) / 2.0 - MU_JOOL / r
                self.assertLess(energy, 0.0,
                                "an escape that leaves JOOL is a different lane")
                a = -MU_JOOL / (2.0 * energy)
                h = r * abs(vt)
                e = math.sqrt(max(0.0, 1.0 + 2.0 * energy * h * h / MU_JOOL ** 2))
                worst_ap = max(worst_ap, a * (1.0 + e))
                worst_pe = min(worst_pe, a * (1.0 - e))
        self.assertAlmostEqual(worst_ap, 71_283_568.0, delta=5_000.0)
        self.assertGreater(worst_ap, A_TYLO,
                           "Tylo is back OUTSIDE the escape's reach; the spec's "
                           "forbidden-token derivation needs re-reading")
        self.assertLess(worst_ap, A_BOP)
        self.assertLess(worst_ap, A_POL)
        # And the other direction: it cannot drop us into Jool either. Jool's
        # atmosphere tops out at 200 km over a 6,000 km radius, and the deepest
        # periapsis the escape family can produce is 12.28 Mm - 1.98x that, which
        # is comfortable but NOT a large multiple, so it is pinned rather than
        # waved at.
        self.assertAlmostEqual(worst_pe, 12_278_379.0, delta=5_000.0)
        self.assertGreater(worst_pe / 6_200_000.0, 1.9)


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

    def test_the_floor_is_retired_on_this_lane_and_the_reason_outlives_it(self):
        """**THE CELL ABOVE IS NOW HISTORY, AND SO IS THIS KEY.** Flight 2
        showed the whole ejection-eccentricity framing is wrong for a
        patched-conic escape: reaching the SOI needs an apoapsis past it, not an
        escape, so the correct burn leaves a BOUND orbit and no floor above 1 is
        meetable. The key stays at 0 and the machine does not read it.

        Kept as a cell rather than deleted because the trap it guards is still
        live: the next reader's instinct will be to 'restore' 1.001."""
        mp = _spec("B26-laythe-vall-transfer.toml")["driver"]["missionParams"]
        self.assertEqual(0, mp["ejectionEccFloor"])
        # The escape the spec actually commissions is BOUND, so any floor > 1
        # would be unmeetable and would ride the phase to its budget.
        p = mlib.b5_params_from_dict(mp)
        plan = mlib.escape_node_plan(p, relay_snap())
        r_pe = PARK_SMA * (1.0 - PARK_ECC)
        v_pe = math.sqrt(MU_LAYTHE * (2.0 / r_pe - 1.0 / PARK_SMA))
        energy = (v_pe + plan.dv) ** 2 / 2.0 - MU_LAYTHE / r_pe
        self.assertLess(energy, 0.0)


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

    def test_the_stage_1_evidence_is_the_soi_reach_not_the_ecc_floor(self):
        """**REWRITTEN BY FLIGHT 2.** The relay's stage-1 claim is "this orbit
        will LEAVE", and on a patched-conic escape that means an APOAPSIS at or
        past the SOI - not a hyperbolic eccentricity, which the correctly-sized
        escape never reaches (ecc 0.7586). Three frame shapes, plus the negative
        case the old gate would have accepted and this one must not."""
        self.assertEqual(0.0, self.params.ejection_ecc_floor)
        # (a) at the park - apoapsis nowhere near the SOI, must be False.
        self.assertFalse(mlib._b5_transfer_burn_done(self.params, parked_snap()))
        # (b) post-escape, still in Laythe's SOI, BOUND but reaching: True.
        reaching = parked_snap(eccentricity=0.7586,
                               apoapsis=4_053_149.0 - R_LAYTHE,
                               periapsis=PARK_SMA * (1.0 - PARK_ECC) - R_LAYTHE)
        self.assertTrue(mlib._b5_transfer_burn_done(self.params, reaching))
        # (b2) a BOUND orbit that does NOT reach the SOI must still be False -
        # this is the under-burn the gate exists to catch.
        short = parked_snap(eccentricity=0.60, apoapsis=3_000_000.0 - R_LAYTHE)
        self.assertFalse(mlib._b5_transfer_burn_done(self.params, short))
        # (b3) a genuinely hyperbolic reading (negative apoapsis) is accepted.
        self.assertTrue(mlib._b5_transfer_burn_done(
            self.params, parked_snap(eccentricity=1.035, apoapsis=-1.0)))
        # (c) in Jool's SOI - the left-home early return, whatever the ecc says.
        self.assertTrue(mlib._b5_transfer_burn_done(
            self.params, snap(ut=PARK_UT + 1e4, body="Jool",
                              situation="ORBITING", eccentricity=0.227,
                              altitude=2.0e7)))
        # (d) an unreadable apoapsis satisfies nothing.
        self.assertFalse(mlib._b5_transfer_burn_done(
            self.params, parked_snap(apoapsis=float("nan"))))
        # (d) The apoapsis floor is NO LONGER the inert 0 every other
        # interplanetary lane carries: under the relay it is STAGE 2's evidence,
        # read in JOOL's frame. It must sit under Vall's own orbital altitude
        # (an orbit that reaches Vall necessarily reads at least that) and well
        # above what an un-escaped orbit at Laythe's radius reads.
        floor = self.mp["transferMinApoapsisMeters"]
        jool_radius = 6_000_000.0
        self.assertGreater(floor, 0)
        self.assertLess(floor, A_VALL - jool_radius)
        self.assertGreater(floor, 1.5 * (A_LAYTHE - jool_radius))
        # (e) AND AT STAGE 1 THE JOOL-FRAME FLOOR MUST NOT BE CONSULTED. The
        # escape's own reaching-orbit reads a Laythe apoapsis of ~3.55 Mm, three
        # orders under the stage-2 floor, so a stage-1 call that read it could
        # never be satisfied.
        self.assertTrue(mlib._b5_transfer_burn_done(
            self.params, reaching, mlib.RELAY_STAGE_ESCAPE))
        # (f) At STAGE 2 the ecc branch's left-the-home-SOI early return must NOT
        # be what decides: in Jool's SOI with a still-low apoapsis the answer is
        # FALSE (the transfer has not been flown), and TRUE only past the floor.
        in_jool_low = snap(ut=PARK_UT + 1e4, body="Jool", situation="ORBITING",
                           eccentricity=0.05, altitude=2.1e7, apoapsis=2.2e7)
        self.assertTrue(mlib._b5_transfer_burn_done(self.params, in_jool_low),
                        "stage 0 must keep its early return - that is the "
                        "escape's own exit")
        self.assertFalse(mlib._b5_transfer_burn_done(
            self.params, in_jool_low, mlib.RELAY_STAGE_TRANSFER))
        in_jool_high = snap(ut=PARK_UT + 2e4, body="Jool", situation="ORBITING",
                            eccentricity=0.227, altitude=2.1e7,
                            apoapsis=float(floor) + 1.0)
        self.assertTrue(mlib._b5_transfer_burn_done(
            self.params, in_jool_high, mlib.RELAY_STAGE_TRANSFER))

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

    def test_the_correction_cap_is_below_the_stage_2_transfer_it_refines(self):
        """THE B15 RULE, re-pointed: under the relay a correction refines the
        STAGE-2 transfer (a MechJeb targeted Hohmann in Jool's frame), not the
        escape. It must sit well below even the CHEAPEST stage 2, or a
        legitimate refinement would be indistinguishable from a re-plan."""
        v_inf, _, _ = hohmann(A_LAYTHE, A_VALL)
        cap = self.mp["maxCorrectionDvMps"]
        self.assertLess(cap, v_inf / 3.0)          # 3.5x below the BEST case
        self.assertLess(cap, 2.0 * v_inf / 5.0)    # 6.9x below the WORST case
        # ... and it must still be well clear of B24's 50, which is the value the
        # B20 lesson says is too small to let an encounter-fixing round through.
        self.assertGreater(cap, 50)

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
        optimizer's answer at the escape boundary is genuinely unknown. THE RELAY
        ADDS A THIRD CANDIDATE: the stage-2 transfer burn is a propulsive run in
        the MIDDLE of the Jool coast, which rule 6 would split and rule 7's graze
        collapse should then suppress. Hence {1,4}, one wider than the
        pre-relay {1,3}."""
        rec_dir = os.path.join(HARNESS_ROOT,
                               self.spec["fixture"]["saveTemplate"],
                               "Parsek", "Recordings")
        carried = (len([n for n in os.listdir(rec_dir) if n.endswith(".prec")])
                   if os.path.isdir(rec_dir) else 0)
        self.assertEqual(0, carried)
        self.assertEqual({"min": carried + 1, "max": carried + 4},
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
    """**RE-PINNED 2026-08-20 OFF THE REAL FIXTURE.** The V17 pair shipped with
    the weakest seeds of the family because its subject's ROUTING is the
    measurement and the two candidate roads anchor from different planners. B26
    flight 3 produced `vall-transfer-recorded`, and the calibration turned the
    pre-flight worry around: at THIS pair the 1:2:4 resonance puts P_Vall and the
    Laythe-Vall synodic **0.6615 s** apart, so H1's and H2's jump tables are
    IDENTICAL on cycle 1 and 1 s apart on cycle 2. The cells below drive the
    committed H1 table AND the H2 re-pin table from the fixture's own bytes."""

    # --- THE MEASURED H3 CLOCK (V17M reading run 2026-08-20_1841) -----------
    # Every one of these is READ OUT OF KSP.log, not derived. The H1/H2 anchor
    # formulas both belonged to planners that DECLINED, and both overshot the
    # real cycle-1 arrival by 22-128 ks.
    ANCHOR_UT = 28_896_850.262240175        # SetLoopEnabled ... anchorUT=
    UNIT_SPAN_START = 28_817_026.597051531  # the unit's span, NOT explicitStartUT
    UNIT_SPAN_END = 28_896_847.102240246
    RELAUNCH_1 = 28_900_841.28749961        # startloopplayback relaunchUt=
    MAX_OVERLAP_INSTANCES = 20              # ParsekConfig.cs MaxOverlapMissionInstances

    TREE = "9aa3c87c95a147388e6220bd36796fd9"
    RECORDING = "625d63e022c449d6a44b5269c8b54a21"
    # ALL FOUR READ FROM `fixtures/saves/vall-transfer-recorded`.
    UT0 = 28_817_026.617051531          # explicitStartUT (NOT segment 0's startUT)
    END_UT = 28_896_846.042240269       # explicitEndUT
    SAVE_UT = 28_896_848.582240213      # the FLIGHTSTATE clock
    SEAM_ESCAPE = 28_823_386.230090793  # Laythe -> Jool
    SEAM_ARRIVAL = 28_892_466.219888370  # Jool  -> Vall
    LAST_SEG_END = 28_896_012.542259    # last ORBIT_SEGMENT endUT
    SEG0_START = 28_817_029.317051470   # the WRONG anchor, pinned as such
    PARK_FRACTION = 0.707

    @property
    def SPAN(self):
        """The RECORDING's span (explicit stamps). Kept for the fixture cells."""
        return self.END_UT - self.UT0

    @property
    def UNIT_SPAN(self):
        """The LOOP UNIT's span - what the playback clock actually uses, and what
        every jump below is measured from. It is NOT the recording's explicit
        span: the unit takes the member's first/last POINT, 0.020 s and 1.060 s
        away from explicitStartUT/explicitEndUT."""
        return self.UNIT_SPAN_END - self.UNIT_SPAN_START

    @property
    def OVERLAP_CADENCE(self):
        """`ComputeEffectiveLaunchCadence`'s `duration / maxCycles` floor - the
        spacing of the SELF-OVERLAPPING relaunch train, and the number the run's
        first relaunch actually used."""
        return self.UNIT_SPAN / self.MAX_OVERLAP_INSTANCES

    @property
    def SEAM_OFF(self):
        return self.SEAM_ARRIVAL - self.UNIT_SPAN_START

    @property
    def DEST_PHASE(self):
        return self.UNIT_SPAN_END - self.SEAM_ARRIVAL

    @property
    def PARK_OFF(self):
        inbound = self.LAST_SEG_END - self.SEAM_ARRIVAL
        tail = self.UNIT_SPAN_END - self.LAST_SEG_END
        return self.SEAM_OFF + inbound + self.PARK_FRACTION * tail

    def _relaunch(self, instance):
        return self.ANCHOR_UT + instance * self.OVERLAP_CADENCE

    def _p_vall(self):
        return 2.0 * math.pi * math.sqrt(A_VALL ** 3 / MU_JOOL)

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

    def test_both_specs_carry_the_harvested_tree_id(self):
        """The placeholder is GONE. A zero tree id surviving the calibration
        would make every seam step a silent no-op."""
        for spec in (self.m, self.t):
            trees = [s["args"]["tree"] for s in spec["driver"]["steps"]
                     if s.get("cmd") in ("MissionConfig", "StartLoopPlayback")]
            self.assertTrue(trees)
            for tree in trees:
                self.assertEqual(self.TREE, tree)
                self.assertNotEqual("0" * 32, tree)

    def test_the_pinned_bytes_are_the_fixture_s_own(self):
        """THE CELL THAT COUPLES THE SEEDS TO THE PAYLOAD. Every UT above is read
        from the harvested save; if a re-harvest moves any of them, the jump
        table is stale and this reds before a run is spent."""
        save = os.path.join(HARNESS_ROOT, "fixtures", "saves",
                            "vall-transfer-recorded", "persistent.sfs")
        self.assertTrue(os.path.isfile(save), save)
        with open(save, encoding="utf-8", errors="replace") as fh:
            sfs = fh.read()
        import re as _re

        def _val(key):
            m = _re.search(r"\b%s = ([0-9.]+)" % key, sfs)
            self.assertIsNotNone(m, key)
            return float(m.group(1))

        self.assertAlmostEqual(self.UT0, _val("explicitStartUT"), delta=1e-6)
        self.assertAlmostEqual(self.END_UT, _val("explicitEndUT"), delta=1e-6)
        self.assertIn("id = %s" % self.TREE, sfs)
        self.assertIn("recordingId = %s" % self.RECORDING, sfs)
        prec = os.path.join(HARNESS_ROOT, "fixtures", "saves",
                            "vall-transfer-recorded", "Parsek", "Recordings",
                            self.RECORDING + ".prec.txt")
        with open(prec, encoding="utf-8", errors="replace") as fh:
            body = fh.read()
        # Parse the segment chain rather than string-matching UTs: Parsek writes
        # floats round-trip ("R"), so a padded %.9f rendering does not appear.
        segs = []
        seg_re = _re.compile(r"ORBIT_SEGMENT\s*{(.*?)\n\t\}", _re.S)
        for m in seg_re.finditer(body):
            b = m.group(1)
            def _g(key):
                return _re.search(r"\b%s = ([-0-9.]+)" % key, b).group(1)
            segs.append((float(_g("startUT")), float(_g("endUT")),
                         _re.search(r"\bbody = (\w+)", b).group(1)))
        self.assertEqual(13, len(segs),
                         "the segment count moved; the seams need re-reading")
        self.assertEqual(["Laythe"] * 4 + ["Jool"] * 6 + ["Vall"] * 3,
                         [s[2] for s in segs])
        self.assertAlmostEqual(self.SEG0_START, segs[0][0], delta=1e-6)
        self.assertAlmostEqual(self.SEAM_ESCAPE, segs[4][0], delta=1e-6)
        self.assertAlmostEqual(self.SEAM_ARRIVAL, segs[10][0], delta=1e-6)
        self.assertAlmostEqual(self.LAST_SEG_END, segs[12][1], delta=1e-3)
        # BOTH seams are adjacent endUT == startUT body-change pairs.
        self.assertAlmostEqual(segs[3][1], segs[4][0], delta=1e-6)
        self.assertAlmostEqual(segs[9][1], segs[10][0], delta=1e-6)

    def test_the_v6m_convention_is_used_and_it_matters(self):
        """UT0 is the recording's EXPLICIT start, not segment 0's startUT. The
        gap is small and load-bearing: anchoring on the wrong one shifts every
        bracket by it."""
        gap = self.SEG0_START - self.UT0
        self.assertAlmostEqual(gap, 2.700, delta=0.01)
        self.assertGreater(gap, 0.0)

    def test_the_overlap_cadence_reproduces_the_logged_relaunch_exactly(self):
        """**THE H3 IDENTIFICATION, and it is exact to the last digit.** The
        relaunch train does NOT step by the span cadence; it steps by
        `ComputeEffectiveLaunchCadence`'s `span / MaxOverlapMissionInstances`
        floor. If that constant or that formula ever moves, every jump below is
        wrong and this cell is where it reds."""
        self.assertAlmostEqual(self.UNIT_SPAN, 79_820.505188715, delta=1e-6)
        self.assertAlmostEqual(self.OVERLAP_CADENCE, 3991.0252594357357,
                               delta=1e-9)
        self.assertAlmostEqual(self._relaunch(1), self.RELAUNCH_1, delta=1e-6)
        # SELF-OVERLAPPING: the launch spacing is far shorter than one play.
        self.assertLess(self.OVERLAP_CADENCE, self.UNIT_SPAN)

    def test_instance_21_is_exactly_one_span_after_instance_1(self):
        """WHY CYCLE 2 IS INSTANCE 21 RATHER THAN INSTANCE 2. Because the overlap
        cadence is EXACTLY span/20, the 21st relaunch is exactly one span after
        the 1st - so 'one full cycle later' and 'a real relaunch epoch' are the
        same instant. Consecutive instances are only 3,991 s apart, which says
        nothing about recurrence."""
        delta = self._relaunch(21) - self._relaunch(1)
        self.assertAlmostEqual(delta, self.UNIT_SPAN, delta=1e-6)
        self.assertAlmostEqual(self.MAX_OVERLAP_INSTANCES * self.OVERLAP_CADENCE,
                               self.UNIT_SPAN, delta=1e-6)

    def test_the_jump_table_is_derived_from_the_MEASURED_H3_clock(self):
        uts = self._jump_uts(self.m)
        self.assertEqual(8, len(uts))
        for cycle, instance in enumerate((1, 21)):
            relaunch = self._relaunch(instance)
            arrival = relaunch + self.SEAM_OFF
            want = [round(arrival - 180.0), round(arrival - 60.0),
                    round(arrival + 140.0), round(relaunch + self.PARK_OFF)]
            self.assertEqual(want, uts[cycle * 4:cycle * 4 + 4], instance)

    def test_the_previous_H1_table_overshot_the_real_arrival(self):
        """THE EVIDENCE THAT THE NEW TABLE IS WORTH TRUSTING, kept as a cell
        because it is the whole reason the reading run red: the H1 anchor was
        derived from a planner that declined, and it put every cycle-1 bracket
        ~22 ks past the arrival - which the log confirms directly by reading
        `body=Jool` at the old epoch instead of Vall."""
        arrival = self._relaunch(1) + self.SEAM_OFF
        self.assertAlmostEqual(arrival, 28_976_280.910336, delta=0.01)
        h1_anchor = self.UT0 + self._synodic()
        h1_bracket = round(h1_anchor + (self.SEAM_ARRIVAL - self.UT0) - 180.0)
        self.assertGreater(h1_bracket - arrival, 20_000.0)
        self.assertLess(h1_bracket - arrival, 25_000.0)

    def test_both_pre_flight_hypotheses_agreed_with_each_other_and_still_missed(self):
        """**THE SHARPEST THING THE READING RUN TAUGHT, kept as a cell so it is
        not re-learned.** The calibration pass found that H1 (re-aim, synodic) and
        H2 (the phase-lock window snap) produced jump tables 0-1 s apart, and
        concluded the seeds were robust to the routing outcome. THEY WERE ROBUST
        TO EACH OTHER AND WRONG TOGETHER: the real road was H3, both planners
        DECLINED, and the loop kept the plain enable-time anchor - 97 ks earlier
        than either candidate.

        The lesson is not "the arithmetic was bad"; both tables were correct for
        the planners they modelled. It is that AGREEMENT BETWEEN CANDIDATE MODELS
        IS NOT EVIDENCE when a third road exists that neither models."""
        p_vall = self._p_vall()
        self.assertAlmostEqual(p_vall - self._synodic(), 0.6615, delta=0.01)
        h1 = self.UT0 + self._synodic()
        ref = max(self.SAVE_UT, self.END_UT)
        h2 = self.UT0 + math.ceil((ref - self.UT0) / p_vall - 1e-9) * p_vall
        self.assertAlmostEqual(h2 - h1, 0.6615, delta=0.01)
        # ... and the MEASURED anchor is nowhere near either of them.
        self.assertGreater(h1 - self.ANCHOR_UT, 20_000.0)
        self.assertGreater(h2 - self.ANCHOR_UT, 20_000.0)
        # The measured anchor is the loop-enable UT, which sits just past the
        # recording's own end - no planner arithmetic in it at all.
        self.assertGreater(self.ANCHOR_UT, self.UNIT_SPAN_END)
        self.assertLess(self.ANCHOR_UT - self.UNIT_SPAN_END, 10.0)

    def test_the_park_epoch_sits_in_the_segment_less_parked_tail(self):
        """**THE PRE-FLIGHT PREDICTION THAT CAME BACK WRONG, and the fixture is
        why the fraction moved.** The spec predicted B26's short capture burn
        would NOT leave a segment-less tail. It did: the three Vall segments are
        all the approach hyperbola and the captured park carries none of its own,
        so V14M's 70.7% applied to the whole destination phase would land on the
        INBOUND leg. Re-based on the tail, V16M's rule."""
        inbound = self.LAST_SEG_END - self.SEAM_ARRIVAL
        tail = self.END_UT - self.LAST_SEG_END
        self.assertAlmostEqual(inbound, 3546.322, delta=0.01)
        self.assertAlmostEqual(tail, 833.500, delta=0.01)
        self.assertGreater(inbound, tail, "the inbound leg dominates the phase, "
                                          "which is why the fraction moved")
        # the epoch must land INSIDE the tail, not on the hyperbola
        self.assertGreater(self.PARK_OFF, self.SEAM_OFF + inbound)
        self.assertLess(self.PARK_OFF, self.SPAN)
        naive = self.SEAM_OFF + self.PARK_FRACTION * self.DEST_PHASE
        self.assertLess(naive, self.SEAM_OFF + inbound,
                        "the naive whole-phase fraction must land on the "
                        "inbound leg - that is the trap this cell records")

    def test_the_relay_removed_the_transfer_window_wait(self):
        """WHY THE SPAN CAME IN AT THE LOW END OF ITS DECLARED BAND. The
        pre-flight seed was 96,874.9 s with a 43,874.9 -> 149,836.3 band built
        around a transfer-window wait of 0 -> one synodic. The parent-relay mode
        places its OWN escape node at the next park periapsis, so that wait never
        opened - the escape seam is 6,359.6 s in, not the seeded 53,470."""
        self.assertGreater(self.SPAN, 43_874.9)
        self.assertLess(self.SPAN, 96_874.9)
        escape_off = self.SEAM_ESCAPE - self.UT0
        self.assertAlmostEqual(escape_off, 6359.613, delta=0.01)
        self.assertLess(escape_off, 0.2 * 53_470.0)

    def test_the_cycle_two_bracket_is_one_UNIT_SPAN_past_cycle_one(self):
        """Under H1 the cadence is the moon-moon synodic, NOT a moon period - the
        thing that most distinguishes this pair's arithmetic from V14/V15/V16's.
        The cell pins the shape so a well-meaning edit cannot quietly re-base it
        on a period."""
        uts = self._jump_uts(self.m)
        self.assertEqual(8, len(uts))
        for c1, c2 in zip(uts[:4], uts[4:]):
            self.assertAlmostEqual(c2 - c1, self.UNIT_SPAN, delta=1.5)
        # ... and NOT the synodic the pre-flight seeds used: the planner that
        # would have produced a synodic cadence DECLINED (H3).
        self.assertNotAlmostEqual(uts[4] - uts[0], self._synodic(), delta=1000.0)

    def test_the_printed_cycle_two_seam_matches_the_cadence_it_claims(self):
        # Retained verbatim from the seed era: the header still prints both
        # cycle seams and the carry-down slip it guards is still possible.
        """A 0.572 s CARRY-DOWN SLIP lived in this header: the printed cycle-2
        arrival seam had been advanced by a rounded 105,962.000 rather than by the
        synodic 105,961.428 the same block declares two lines above. The rounded
        jump UTs were unaffected, which is exactly why nobody sees it - the file
        stays self-consistent everywhere a machine reads it and drifts only where
        a HUMAN reads it. This cell reads the printed figure back out of the
        header comment and re-derives it, so the next carry slip reds here."""
        import re
        with open(os.path.join(HARNESS_ROOT, "scenarios",
                               "V17M-laythe-vall-player-loop.toml"),
                  encoding="utf-8") as fh:
            body = fh.read()

        def printed(label):
            m = re.search(r"#\s+" + label + r"[^=]*=\s*([\d,]+\.\d+)", body)
            self.assertIsNotNone(m, "header line %r not found" % label)
            return float(m.group(1).replace(",", ""))

        c1_seam = printed("cycle-1 arrival seam")
        c2_seam = printed("cycle-2 arrival seam")
        self.assertAlmostEqual(c2_seam - c1_seam, self.UNIT_SPAN, delta=0.05)
        # and the printed figure must agree with the jumps derived from it
        uts = self._jump_uts(self.m)
        self.assertAlmostEqual(round(c2_seam - 180.0), uts[4], delta=0.5)

    def test_the_ts_ghost_forbid_is_retired_on_BOTH_lanes(self):
        """THE INVERSION of this cell's own pre-flight assertion, which required
        `created 0 ghost vessel(s)` as a V17T forbid. V17T run 1
        (2026-08-20_1917) measured init-zero as the CORRECT product outcome on
        this subject: with the loop armed the init walk skips the base recording
        (`noOrbit=1` - the Vall tail is segment-less, no orbit source resolves)
        and every real ghost comes from the DYNAMIC overlap path. Forbidding the
        init line would forbid a deliberate product behavior; the anti-vacuity
        duty moved to the two required dynamic-path tokens (the next cell)."""
        tok = "created 0 ghost vessel\\(s\\)"
        for spec in (self.m, self.t):
            self.assertNotIn(tok, spec["expectations"]["logContracts"]["forbidden"])
        # both lanes now carry exactly the road-independent forbid set
        self.assertEqual(3, len(self.m["expectations"]["logContracts"]["forbidden"]))
        self.assertEqual(3, len(self.t["expectations"]["logContracts"]["forbidden"]))

    def test_v17t_anti_vacuity_rides_the_dynamic_overlap_path(self):
        """Run 1's re-pin: the init-walk pin (`initialized: created [1-9]`) is
        unsatisfiable on a loop-armed segment-less-tail subject, so the TS
        anti-vacuity is the dynamic path materializing instances in-scene, plus
        the Vall-frame GhostCreated pin that needs cycle 1 (spawned LAST,
        newest-first at MaxSpawnsPerFrame=2 - the reason the TS phase carries the
        forty-tick dwell)."""
        req = self.t["expectations"]["logContracts"]["required"]
        self.assertIn("GhostMap\\] Created ghost vessel .*orbitSource=overlap-instance-segment .*scene=TRACKSTATION", req)
        self.assertIn("phase=GhostCreated surface=ProtoIcon pid=\\d+ .*body=Vall scene=TRACKSTATION", req)
        self.assertFalse(any("initialized: created [1-9]" in t for t in req))

    def test_v17m_jumps_are_strictly_forward(self):
        uts = self._jump_uts(self.m)
        self.assertEqual(sorted(uts), uts)
        self.assertEqual(len(set(uts)), len(uts))

    def test_v17t_reuses_v17ms_third_cycle_one_bracket_jump(self):
        t_jumps = self._jump_uts(self.t)
        self.assertEqual(1, len(t_jumps))
        self.assertEqual(self._jump_uts(self.m)[2], t_jumps[0])

    def test_both_specs_point_at_the_same_fixture_and_it_now_EXISTS(self):
        """The inversion of this cell's own seed-era assertion: it used to
        require the fixture to be ABSENT. B26 flight 3 produced it."""
        self.assertEqual(self.m["fixture"]["saveTemplate"],
                         self.t["fixture"]["saveTemplate"])
        self.assertEqual("fixtures/saves/vall-transfer-recorded",
                         self.m["fixture"]["saveTemplate"])
        self.assertTrue(os.path.isdir(os.path.join(
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

    def test_both_lanes_carry_dwell_blocks_for_two_different_reasons(self):
        """Grown from `test_only_v17m_carries_the_census_pacing_block` across the
        reading rounds. V17M's dwells are CENSUS PACING (two 40-tick blocks after
        the +140 brackets plus the two park blocks - see its run-2/run-3 ledger);
        V17T's single 40-tick block is SPAWN-THROTTLE HEADROOM (run 1: two ticks
        = 3 lifecycle ticks = 6 of 20 overlap instances, and cycle 1 - the
        Vall-leg instance both its anti-vacuity pins need - spawns LAST)."""
        m_ticks = sum(1 for s in self.m["driver"]["steps"]
                      if s.get("cmd") == "RecordingState")
        t_ticks = sum(1 for s in self.t["driver"]["steps"]
                      if s.get("cmd") == "RecordingState")
        self.assertEqual(120, m_ticks)  # 3 x 40: +140 c1, park c1 (+watch), +140 c2... see spec
        self.assertEqual(40, t_ticks)

    def test_both_v17_lanes_arm_the_same_two_gating_blocks(self):
        """Grown from `test_neither_v17_spec_arms_a_gating_block` in two steps.
        V17M armed `rewind` + `recordings.structure` 2026-08-20 off its OWN
        green reading run `2026-08-20_1915`; V17T armed the SAME two blocks the
        same day off ITS own green reading run `2026-08-20_1933` - both the V16
        discipline, both in test_hlib.ARMED_ALLOWLIST, and both reading runs
        measured byte-identical facets (0/0/0/0; 1/1/1 {Orbiting: 1}; points
        746/746/746), which is the pair's determinism statement."""
        for spec in (self.m, self.t):
            self.assertTrue(spec["expectations"]["rewind"].get("gating"))
            self.assertTrue(
                spec["expectations"]["recordings"]["structure"].get("gating"))
            self.assertEqual(
                {"min": 1, "max": 1},
                spec["expectations"]["recordings"]["structure"]["recordings"])

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

    def test_the_spec_records_the_blocker_and_the_mode_that_answers_it(self):
        """THE LEDGER MUST OUTLIVE THE BLOCK. The lane is no longer
        BLOCKED-PENDING - the capability exists - but the measurement is the
        REASON the mode exists, so the description has to carry both: what was
        measured, and what was built in response. A description that quietly
        dropped the blocker would leave the next reader with a mode whose
        justification lives nowhere."""
        desc = self.spec["description"]
        self.assertIn("LIVE-PROVEN 2026-08-20 ON FLIGHT 3", desc)
        # ALL THREE FLIGHTS must stay named. The green one is the least likely
        # to be dropped; the two that failed are the ones a tidying pass would
        # cut, and they are the reason the mode exists and the reason its two
        # values are what they are.
        for beat in ("FLIGHT 1 (runs", "FLIGHT 2 (runs", "FLIGHT 3 (run"):
            self.assertIn(beat, desc, beat)
        self.assertIn("MECHJEB-INTERPLANETARY-PLANNER-REJECTS-MOON-ORIGIN", desc)
        self.assertIn("NextTimeOfRadius", desc,
                      "the measured exception is the blocker's evidence and must "
                      "survive the re-point")
        self.assertIn("parentRelayTransfer", desc)
        self.assertNotIn("BLOCKED-PENDING", desc,
                         "the lane is not blocked any more; saying so would "
                         "make the status doc and this file disagree")
        # and the lane keeps its calibration-discipline tier
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


# ---------------------------------------------------------------------------
# The PARENT-RELAY mode. Everything below tests `mlib` (and one runner action)
# rather than this spec, and it is HERE rather than in test_mlib.py because the
# mode exists for exactly one lane and its numbers are this fixture's numbers.
# ---------------------------------------------------------------------------


def relay_params(**overrides):
    """A minimal armed relay param dict. Deliberately NOT the committed spec's:
    these cells must be able to break the invariants one at a time."""
    base = dict(homeBodyName="Laythe", targetBodyName="Vall",
                returnBodyName="Jool", viaBodyNames=["Jool"],
                interplanetaryTransfer=True, parentRelayTransfer=True,
                escapeSoiSpeedMps=ESCAPE_SOI_SPEED_MPS,
                escapeMaxDeltaVMps=700.0,
                escapeNodeMinLeadSeconds=300.0, escapeTimeoutSeconds=2400.0,
                ejectionEccFloor=0.0, transferMinApoapsisMeters=36_000_000)
    base.update(overrides)
    return base


def relay_snap(**overrides):
    """A frame reading the committed fixture's park, with the periapsis clock the
    escape planner needs (the capture lanes' `read_periapsis` channel)."""
    base = dict(time_to_periapsis=900.0)
    base.update(overrides)
    return parked_snap(**base)


class ParentRelayLoadTimeTests(unittest.TestCase):
    """THE FOUR IMPLICATIONS THE MODE RESTS ON, each asserted rather than
    documented. Every one is a SILENT-DEGRADATION risk: with the flag armed and
    the implication broken the machine does not crash, it flies something else."""

    def test_the_committed_spec_arms_the_mode_and_parses(self):
        mp = _spec("B26-laythe-vall-transfer.toml")["driver"]["missionParams"]
        p = mlib.b5_params_from_dict(mp)
        self.assertTrue(p.parent_relay_transfer)
        self.assertTrue(p.interplanetary_transfer)
        self.assertFalse(p.pad_align_ejection)
        self.assertTrue(p.start_in_orbit)
        self.assertEqual("Jool", p.return_body)
        self.assertGreater(p.escape_soi_speed, 0.0)
        self.assertGreater(p.escape_max_dv, 0.0)

    def test_the_relay_requires_the_interplanetary_flag(self):
        # Without it the stage-1 evidence falls back to an apoapsis floor an
        # ESCAPE drives NEGATIVE, and the correction domain stops narrowing to
        # the parent SOI.
        with self.assertRaises(ValueError):
            mlib.b5_params_from_dict(relay_params(interplanetaryTransfer=False))

    def test_the_relay_requires_a_parent_body(self):
        # returnBodyName IS the SOI whose arrival triggers stage 2.
        with self.assertRaises(ValueError):
            mlib.b5_params_from_dict(relay_params(returnBodyName=""))

    def test_the_relay_and_pad_align_are_mutually_exclusive(self):
        with self.assertRaises(ValueError):
            mlib.b5_params_from_dict(relay_params(padAlignEjection=True,
                                                  homeBodyName="Kerbin"))

    def test_the_relay_requires_committed_gravity_constants_for_the_home_body(self):
        # The raise must land at SPEC LOAD, not mid-flight on the one frame that
        # matters. Duna is a stock body with no row in the table.
        with self.assertRaises(ValueError) as ctx:
            mlib.b5_params_from_dict(relay_params(homeBodyName="Duna"))
        self.assertIn("STOCK_BODY_GRAVITY", str(ctx.exception))

    def test_the_relay_requires_a_positive_soi_speed(self):
        with self.assertRaises(ValueError):
            mlib.b5_params_from_dict(relay_params(escapeSoiSpeedMps=0.0))

    def test_the_retired_v_inf_key_is_rejected_rather_than_ignored(self):
        """**THE RENAME IS THE FIX, SO THE OLD NAME MUST NOT PARSE.** Silently
        ignoring `escapeTargetVInfMps` would fly the corrected burn from a spec
        that asked for the refuted one - or, worse, leave the new key absent and
        refuse everything - from a file that reads as if it were fine. Flight 2
        is the whole reason: the two names differ by a factor of 3.12 in what
        they deliver."""
        with self.assertRaises(ValueError) as ctx:
            mlib.b5_params_from_dict(relay_params(escapeTargetVInfMps=347.245))
        self.assertIn("RETIRED", str(ctx.exception))
        self.assertIn("escapeSoiSpeedMps", str(ctx.exception))
        # ... and the committed spec must not carry it
        mp = _spec("B26-laythe-vall-transfer.toml")["driver"]["missionParams"]
        self.assertNotIn("escapeTargetVInfMps", mp)
        self.assertIn("escapeSoiSpeedMps", mp)

    def test_the_gravity_table_carries_only_cited_bodies(self):
        """THE DISCIPLINE, pinned: this table is STOCK_HELIO_ELEMENTS' sibling
        and follows its rule - only bodies whose values appear in a committed,
        reviewed spec. A row added from memory is a wrongly sized burn that the
        dv cap bounds but does not correct."""
        self.assertEqual(["Laythe"], sorted(mlib.STOCK_BODY_GRAVITY))
        mu, radius, soi = mlib.STOCK_BODY_GRAVITY["Laythe"]
        self.assertEqual(MU_LAYTHE, mu)
        self.assertEqual(R_LAYTHE, radius)
        # THE THIRD NUMBER IS FLIGHT 2's, and it is load-bearing rather than
        # decorative: it is what makes the escape target the HAND-OFF speed
        # instead of an asymptotic one that KSP never reaches.
        self.assertAlmostEqual(SOI_LAYTHE, soi, delta=1.0)


class EscapeNodePlanTests(unittest.TestCase):
    """The pure escape-node planner. It is the only new ARITHMETIC in the mode,
    so every term and every refusal gets a cell."""

    def setUp(self):
        self.p = mlib.b5_params_from_dict(relay_params())

    def test_the_node_is_the_vis_viva_answer_at_the_periapsis(self):
        plan = mlib.escape_node_plan(self.p, relay_snap())
        self.assertEqual("", plan.reason)
        r_pe = PARK_SMA * (1.0 - PARK_ECC)
        v_pe = math.sqrt(MU_LAYTHE * (2.0 / r_pe - 1.0 / PARK_SMA))
        want = math.sqrt(ESCAPE_SOI_SPEED_MPS ** 2
                         + 2.0 * MU_LAYTHE * (1.0 / r_pe - 1.0 / SOI_LAYTHE)) - v_pe
        self.assertAlmostEqual(plan.dv, want, delta=1e-6)
        self.assertAlmostEqual(plan.dv, ESCAPE_NODE_MPS, delta=0.05)

    def test_the_node_targets_the_soi_boundary_not_infinity(self):
        """**FLIGHT 2's DEFECT A, AS A UNIT CELL.** The `- 1/r_soi` term is the
        entire fix, so it is asserted as a DIFFERENCE against the formula that
        was flown, not just as a number: sizing for an asymptotic excess
        over-burns, and by a margin that matters."""
        plan = mlib.escape_node_plan(self.p, relay_snap())
        r_pe = PARK_SMA * (1.0 - PARK_ECC)
        v_pe = math.sqrt(MU_LAYTHE * (2.0 / r_pe - 1.0 / PARK_SMA))
        flown = math.sqrt(347.245 ** 2 + 2.0 * MU_LAYTHE / r_pe) - v_pe
        self.assertAlmostEqual(flown, F2_NODE_DV_MPS, delta=0.05,
                               msg="the pre-fix formula must reproduce the "
                                   "node flight 2 actually planned")
        self.assertLess(plan.dv, flown)
        self.assertAlmostEqual(flown - plan.dv, 188.0, delta=1.0)

    def test_the_planned_node_delivers_the_requested_speed_at_the_boundary(self):
        """THE CONTRACT ITSELF, closed loop: propagate the planned orbit out to
        the SOI radius and read the speed there. It must BE the requested one -
        which is what flight 2's contract failed to do by a factor of 3.12."""
        plan = mlib.escape_node_plan(self.p, relay_snap())
        r_pe = PARK_SMA * (1.0 - PARK_ECC)
        v_pe = math.sqrt(MU_LAYTHE * (2.0 / r_pe - 1.0 / PARK_SMA))
        v_post = v_pe + plan.dv
        energy = v_post ** 2 / 2.0 - MU_LAYTHE / r_pe
        v_at_soi = math.sqrt(2.0 * (energy + MU_LAYTHE / SOI_LAYTHE))
        self.assertAlmostEqual(v_at_soi, ESCAPE_SOI_SPEED_MPS, delta=0.05)
        # ... and the flown contract's delivered speed, for the contrast.
        v_flown = math.sqrt(347.245 ** 2 + 2.0 * MU_LAYTHE / r_pe)
        e_flown = v_flown ** 2 / 2.0 - MU_LAYTHE / r_pe
        soi_flown = math.sqrt(2.0 * (e_flown + MU_LAYTHE / SOI_LAYTHE))
        self.assertAlmostEqual(soi_flown, 1083.69, delta=0.1)
        self.assertAlmostEqual(soi_flown / 347.245, 3.12, delta=0.01)

    def test_a_request_below_the_geometric_floor_is_refused_by_name(self):
        """THE NEW FAILURE MODE THE CORRECTED FORMULA CREATES, and it must be
        NAMED rather than clamped: a prograde burn at this periapsis cannot
        reach the SOI at all below ~370.08 m/s of relative speed. Clamping up
        would fly a burn the spec did not ask for."""
        low = mlib.b5_params_from_dict(relay_params(escapeSoiSpeedMps=300.0))
        plan = mlib.escape_node_plan(low, relay_snap())
        self.assertNotEqual("", plan.reason)
        self.assertIn("below what this park's geometry can deliver", plan.reason)
        self.assertIn("370.1", plan.reason)
        self.assertIsNone(plan.dv)
        # and the committed value is above it, with real room
        self.assertGreater(ESCAPE_SOI_SPEED_MPS, 370.08)
        self.assertGreater(ESCAPE_SOI_SPEED_MPS / 370.08, 1.15)

    def test_the_correct_escape_is_BOUND_which_is_why_the_ecc_floor_retired(self):
        """The coupling flight 2 forced. Reaching a SOI needs an APOAPSIS past
        it, not an escape - so the right burn leaves a BOUND orbit and any
        `ejectionEccFloor` above 1 would be unmeetable. Demanding hyperbolic
        would cost the parabolic burn instead."""
        plan = mlib.escape_node_plan(self.p, relay_snap())
        r_pe = PARK_SMA * (1.0 - PARK_ECC)
        v_pe = math.sqrt(MU_LAYTHE * (2.0 / r_pe - 1.0 / PARK_SMA))
        v_post = v_pe + plan.dv
        energy = v_post ** 2 / 2.0 - MU_LAYTHE / r_pe
        self.assertLess(energy, 0.0, "the correct escape must be BOUND")
        a = -MU_LAYTHE / (2.0 * energy)
        h = r_pe * v_post
        ecc = math.sqrt(1.0 + 2.0 * energy * h * h / MU_LAYTHE ** 2)
        self.assertAlmostEqual(ecc, 0.7586, delta=0.001)
        self.assertGreater(a * (1.0 + ecc), SOI_LAYTHE,
                           "a bound escape must still REACH the SOI")
        # the parabolic alternative a hyperbolic gate would force
        parabolic = math.sqrt(2.0 * MU_LAYTHE / r_pe) - v_pe
        self.assertAlmostEqual(parabolic, 752.10, delta=0.5)
        self.assertGreater(parabolic - plan.dv, 160.0)
        # ... and the committed spec must therefore NOT arm the ecc floor
        mp = _spec("B26-laythe-vall-transfer.toml")["driver"]["missionParams"]
        self.assertEqual(0, mp["ejectionEccFloor"])

    def test_the_node_lands_at_the_next_periapsis(self):
        plan = mlib.escape_node_plan(self.p, relay_snap(time_to_periapsis=900.0))
        self.assertAlmostEqual(plan.node_ut, PARK_UT + 900.0, delta=1e-6)

    def test_a_periapsis_inside_the_lead_rolls_forward_one_park_period(self):
        """The executor autowarps to the node and holds a WARPALIGN; a node 10 s
        ahead is a node it cannot fly. The roll uses the SAME a and mu the burn
        is sized with, so no new constant enters."""
        plan = mlib.escape_node_plan(self.p, relay_snap(time_to_periapsis=10.0))
        period = 2.0 * math.pi * math.sqrt(PARK_SMA ** 3 / MU_LAYTHE)
        self.assertAlmostEqual(period, 1941.0, delta=1.0)
        self.assertAlmostEqual(plan.node_ut, PARK_UT + 10.0 + period, delta=1e-6)
        self.assertGreater(plan.node_ut - PARK_UT,
                           self.p.escape_node_min_lead)

    def test_the_dv_is_unchanged_by_the_roll(self):
        # The roll moves WHEN, never HOW MUCH: the following periapsis has the
        # same radius.
        near = mlib.escape_node_plan(self.p, relay_snap(time_to_periapsis=10.0))
        far = mlib.escape_node_plan(self.p, relay_snap(time_to_periapsis=900.0))
        self.assertAlmostEqual(near.dv, far.dv, delta=1e-9)

    def test_the_periapsis_placement_beats_the_circular_formula(self):
        """WHY the placement is not incidental, held at the SAME contract on
        both sides so the comparison is about the BURN POINT and not about the
        SOI term (which the cell above owns)."""
        plan = mlib.escape_node_plan(self.p, relay_snap())
        soi_term = 2.0 * MU_LAYTHE * (1.0 / PARK_SMA - 1.0 / SOI_LAYTHE)
        circular = (math.sqrt(ESCAPE_SOI_SPEED_MPS ** 2 + soi_term)
                    - math.sqrt(MU_LAYTHE / PARK_SMA))
        self.assertGreater(circular - plan.dv, 5.0)
        self.assertLess(circular - plan.dv, 40.0)

    def test_the_resulting_hyperbola_clears_the_committed_ejection_floor(self):
        """THE COUPLING between the node and the burn-done evidence: the node
        this planner computes must produce an eccentricity the spec's
        `ejectionEccFloor` accepts, or the phase rides its budget on a perfectly
        good escape (B7's 1.05 mistake, one level down)."""
        mp = _spec("B26-laythe-vall-transfer.toml")["driver"]["missionParams"]
        r_pe = PARK_SMA * (1.0 - PARK_ECC)
        ecc = 1.0 + r_pe * 347.245 ** 2 / MU_LAYTHE
        self.assertAlmostEqual(ecc, 1.03418, delta=1e-4)
        self.assertGreater(ecc, mp["ejectionEccFloor"])
        self.assertGreater(ecc - mp["ejectionEccFloor"], 0.03)

    def test_every_unreadable_field_refuses_with_a_name(self):
        for field in ("ut", "periapsis", "apoapsis", "time_to_periapsis"):
            plan = mlib.escape_node_plan(
                self.p, relay_snap(**{field: float("nan")}))
            self.assertNotEqual("", plan.reason, field)
            self.assertIsNone(plan.dv, field)
            self.assertIsNone(plan.node_ut, field)
            self.assertIn("unreadable orbit", plan.reason, field)

    def test_an_unbound_orbit_refuses(self):
        # A hyperbolic reading is NEGATIVE in the body frame; that is the
        # still-bound discriminator the whole machine uses.
        plan = mlib.escape_node_plan(self.p, relay_snap(apoapsis=-1.0))
        self.assertIn("not a bound orbit", plan.reason)

    def test_a_frame_in_the_wrong_soi_refuses(self):
        plan = mlib.escape_node_plan(self.p, relay_snap(body="Jool"))
        self.assertIn("not the home body", plan.reason)
        blank = mlib.escape_node_plan(self.p, relay_snap(body=""))
        self.assertIn("not the home body", blank.reason)

    def test_an_over_cap_node_refuses_and_names_the_cap(self):
        tight = mlib.b5_params_from_dict(relay_params(escapeMaxDeltaVMps=100.0))
        plan = mlib.escape_node_plan(tight, relay_snap())
        self.assertIn("escapeMaxDeltaVMps", plan.reason)
        self.assertIsNone(plan.dv)

    def test_every_park_the_entry_gate_admits_is_actually_deliverable(self):
        """**THE THIRD CORRECTION FLIGHT 2's DEFECT-A FIX FORCED**, and it is the
        one that would otherwise have bitten silently. The SOI-speed floor RISES
        with the park, so a gate ceiling that is too generous admits parks whose
        floor is ABOVE the requested `escapeSoiSpeedMps` - and `escape_node_plan`
        would then REFUSE a fixture the entry gate had just declared legal. At
        the old 500,000 m ceiling that really happens (the floor reaches
        472.33 m/s against a 450 request); at 300,000 it does not.

        This cell sweeps the WHOLE admissible window, not three corners, because
        the offending region is interior to it."""
        mp = _spec("B26-laythe-vall-transfer.toml")["driver"]["missionParams"]
        p = mlib.b5_params_from_dict(mp)
        floor = int(mp["startInOrbitMinPeriapsisMeters"])
        ceiling = int(mp["startInOrbitMaxApoapsisMeters"])
        worst = 0.0
        for pe in range(floor, ceiling + 1, 4_000):
            for ap in (pe, (pe + ceiling) // 2, ceiling):
                plan = mlib.escape_node_plan(
                    p, relay_snap(periapsis=pe, apoapsis=ap,
                                  eccentricity=0.0, altitude=pe))
                self.assertEqual("", plan.reason,
                                 "the entry gate admits a park the escape "
                                 "planner refuses: pe=%s ap=%s" % (pe, ap))
                worst = max(worst, plan.dv)
        self.assertAlmostEqual(worst, 616.18, delta=0.1)
        self.assertLess(worst, mp["escapeMaxDeltaVMps"])
        self.assertGreater(mp["escapeMaxDeltaVMps"] / worst, 1.10)

    def test_the_old_gate_ceiling_would_have_refused_a_legal_park(self):
        """The premise of the cell above, asserted so the tightening cannot be
        quietly reverted: at 500,000 m the gate really does admit a park whose
        geometric floor exceeds the committed request."""
        mp = _spec("B26-laythe-vall-transfer.toml")["driver"]["missionParams"]
        p = mlib.b5_params_from_dict(mp)
        self.assertEqual(300_000, mp["startInOrbitMaxApoapsisMeters"])
        offender = mlib.escape_node_plan(
            p, relay_snap(periapsis=470_000.0, apoapsis=470_000.0,
                          eccentricity=0.0, altitude=470_000.0))
        self.assertNotEqual("", offender.reason)
        self.assertIn("below what this park's geometry can deliver",
                      offender.reason)

    def test_an_unarmed_lane_never_produces_a_node(self):
        off = mlib.b5_params_from_dict({"homeBodyName": "Laythe",
                                        "targetBodyName": "Vall"})
        plan = mlib.escape_node_plan(off, relay_snap())
        self.assertIn("not armed", plan.reason)
        self.assertIsNone(plan.dv)


class ParentRelayMachineFlowTests(unittest.TestCase):
    """The three edges the relay moves in `b5_decide`, driven end to end."""

    @classmethod
    def setUpClass(cls):
        cls.mp = _spec("B26-laythe-vall-transfer.toml")["driver"]["missionParams"]

    def _flown(self):
        """Drive a nominal relay hop from the fixture's park to the second coast,
        returning (final state, per-frame [(phase, relay_stage, action kinds)])."""
        p = mlib.b5_params_from_dict(self.mp)
        st = mlib.b5_initial_state(p)
        log = []
        ut = [PARK_UT]

        def step(**kw):
            nonlocal st
            st, acts = mlib.b5_decide(st, relay_snap(ut=ut[0], **kw))
            log.append((st.phase, st.relay_stage, [a.kind for a in acts]))
            return acts

        for _ in range(3):                       # PRELAUNCH entry-gate debounce
            ut[0] += 1.0
            step()
        ut[0] += 1.0
        step()                                    # ORBIT -> ESCAPE
        ut[0] += 1.0
        escape_actions = step()                   # ESCAPE: the node is authored
        ut[0] += 1.0
        step(node_count=1, node_ut=ut[0] + 800.0)  # -> TRANSFER-BURN
        ut[0] += 200.0
        step(node_count=0, eccentricity=1.03418, apoapsis=-1.0)   # -> COAST
        ut[0] += 100.0
        # tts ABOVE soiLeadSeconds, so the coast arms its NATIVE warp toward the
        # Laythe SOI exit - which is the state the stage-2 hand-off has to
        # cancel, and therefore the state worth scripting.
        step(node_count=0, eccentricity=1.03418, apoapsis=-1.0,
             time_to_soi=5000.0)
        ut[0] += 5000.0
        jool = dict(body="Jool", altitude=2.1e7, apoapsis=2.2e7, periapsis=2.0e7,
                    eccentricity=0.05, time_to_soi=float("nan"),
                    time_to_periapsis=float("nan"))
        step(**jool)                              # -> PLAN-TRANSFER, stage 2
        ut[0] += 1.0
        step(node_count=1, node_ut=ut[0] + 500.0, **jool)          # -> TRANSFER-BURN
        ut[0] += 600.0
        arrived = dict(jool)
        arrived.update(apoapsis=float(self.mp["transferMinApoapsisMeters"]) + 1e5,
                       periapsis=2.1e7, time_to_soi=38_000.0)
        step(node_count=0, **arrived)             # -> COAST, stage 2
        return st, log, escape_actions

    def test_the_hop_walks_orbit_escape_burn_coast_plan_burn_coast(self):
        st, log, _ = self._flown()
        self.assertEqual(
            (mlib.B5_PRELAUNCH, mlib.B5_ORBIT, mlib.B5_ESCAPE,
             mlib.B5_TRANSFER_BURN, mlib.B5_COAST_TO_TARGET,
             mlib.B5_PLAN_TRANSFER, mlib.B5_TRANSFER_BURN,
             mlib.B5_COAST_TO_TARGET),
            st.phases_reached)
        self.assertIsNone(st.verdict)
        self.assertEqual(mlib.RELAY_STAGE_TRANSFER, st.relay_stage)

    def test_the_escape_frame_authors_the_node_the_planner_computed(self):
        st, _, actions = self._flown()
        adds = [a for a in actions if a.kind == mlib.ACTION_ADD_MANEUVER_NODE]
        self.assertEqual(1, len(adds), [a.kind for a in actions])
        self.assertAlmostEqual(adds[0].value, ESCAPE_NODE_MPS, delta=0.05)
        self.assertIsNotNone(adds[0].node_ut)
        # NO MECHJEB PLAN ANYWHERE ON THE ESCAPE. This is the whole point: the
        # operation that refused the moon-parked origin is not called.
        self.assertNotIn(mlib.ACTION_MJ_PLAN_INTERPLANETARY_TRANSFER,
                         [a.kind for a in actions])
        # ... and the machine carries what it authored, as the row's evidence.
        self.assertAlmostEqual(st.escape_node_dv, ESCAPE_NODE_MPS, delta=0.05)
        self.assertIsNotNone(st.escape_node_ut)

    def test_stage_two_plans_the_moon_hohmann_and_retargets_vall(self):
        _, log, _ = self._flown()
        plan_frames = [row for row in log if row[0] == mlib.B5_PLAN_TRANSFER]
        self.assertEqual(1, len(plan_frames))
        kinds = plan_frames[0][2]
        self.assertIn(mlib.ACTION_SET_TARGET_BODY, kinds)
        self.assertIn(mlib.ACTION_MJ_PLAN_TRANSFER, kinds)
        self.assertNotIn(mlib.ACTION_MJ_PLAN_INTERPLANETARY_TRANSFER, kinds)
        # And warp is brought under plan control on the hand-off frame: the coast
        # crosses the boundary still running its own native warp.
        self.assertIn(mlib.ACTION_CANCEL_WARP, kinds)

    def test_the_interplanetary_operation_is_never_called_on_this_lane(self):
        """THE HEADLINE CLAIM OF THE WHOLE MODE, and it is checked STRUCTURALLY
        rather than by reachability: `_b5_transfer_plan_action` branches on the
        relay FLAG, not on `relay_stage`, so an armed lane cannot emit the
        operation MechJeb refused from ANY stage - including a stage-0
        PLAN-TRANSFER that no edge reaches today."""
        p = mlib.b5_params_from_dict(self.mp)
        self.assertEqual(mlib.ACTION_MJ_PLAN_TRANSFER,
                         mlib._b5_transfer_plan_action(p).kind)
        # ... and no frame of a whole flown hop emits it either.
        _, log, escape_actions = self._flown()
        emitted = [kind for _, _, kinds in log for kind in kinds]
        emitted += [a.kind for a in escape_actions]
        self.assertNotIn(mlib.ACTION_MJ_PLAN_INTERPLANETARY_TRANSFER, emitted)
        self.assertIn(mlib.ACTION_ADD_MANEUVER_NODE, emitted)
        self.assertIn(mlib.ACTION_MJ_PLAN_TRANSFER, emitted)

    def test_the_stage_two_handoff_pre_empts_the_no_encounter_trigger(self):
        """THE ORDERING GUARANTEE, and it is the one thing in the relay that
        would fail SILENTLY. A post-escape Jool frame satisfies every condition
        of the no-encounter early trigger - time mode, rounds pending, body in
        the correction domain, no node, no encounter - so if the stage-2 check
        did not come first the machine would spend a correction round trying to
        course-correct a transfer nobody has planned."""
        p = mlib.b5_params_from_dict(self.mp)
        st = mlib.b5_initial_state(p)
        st = mlib._b5_enter(st, mlib.B5_COAST_TO_TARGET, PARK_UT, None)
        frame = snap(ut=PARK_UT + 1.0, body="Jool", situation="ORBITING",
                     altitude=2.1e7, apoapsis=2.2e7, periapsis=2.0e7,
                     eccentricity=0.05, node_count=0,
                     time_to_soi=float("nan"))
        # The trigger's own preconditions really are all met on this frame.
        self.assertTrue(p.correction_trigger_time_to_soi)
        self.assertTrue(mlib._b5_rounds_pending(st))
        self.assertIn(frame.body, mlib._b5_correction_via_bodies(p))
        # Repeat past the debounce depth: without the ordering, a round fires.
        for _ in range(mlib.NO_ENCOUNTER_DEBOUNCE_FRAMES + 2):
            st, _ = mlib.b5_decide(st, frame)
        self.assertEqual(mlib.B5_PLAN_TRANSFER, st.phase)
        self.assertEqual(0, st.correction_rounds_done)
        self.assertEqual(mlib.RELAY_STAGE_TRANSFER, st.relay_stage)

    def test_the_stage_advances_exactly_once(self):
        st, _, _ = self._flown()
        # Re-entering the coast in Jool's SOI must NOT re-plan the transfer.
        before = st.phase
        for _ in range(5):
            st, actions = mlib.b5_decide(
                st, snap(ut=PARK_UT + 1e5, body="Jool", situation="ORBITING",
                         altitude=2.1e7, apoapsis=3.8e7, periapsis=2.1e7,
                         eccentricity=0.2, node_count=0, time_to_soi=30_000.0))
            self.assertEqual(mlib.RELAY_STAGE_TRANSFER, st.relay_stage)
        self.assertEqual(before, mlib.B5_COAST_TO_TARGET)

    def test_an_uncomputable_escape_gives_up_by_name_rather_than_by_timeout(self):
        """The one give-up this mode adds. The generic "phase ESCAPE timed out"
        would send a reader at the budget when the actual event is a refused
        reading - the B15 mis-named-give-up lesson, applied before it can bite."""
        p = mlib.b5_params_from_dict(self.mp)
        st = mlib._b5_enter(mlib.b5_initial_state(p), mlib.B5_ESCAPE,
                            PARK_UT, None)
        blind = relay_snap(ut=PARK_UT + 10.0, time_to_periapsis=float("nan"))
        st, actions = mlib.b5_decide(st, blind)
        self.assertIsNone(st.verdict, "a single blind frame must not give up")
        self.assertIn("unreadable orbit", st.escape_refusal)
        self.assertEqual([], [a for a in actions
                              if a.kind == mlib.ACTION_ADD_MANEUVER_NODE],
                         "a refused plan must emit nothing")
        late = relay_snap(ut=PARK_UT + p.escape_timeout + 10.0,
                          time_to_periapsis=float("nan"))
        st, _ = mlib.b5_decide(st, late)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("escape-node never computable", st.flake_reason)
        self.assertIn("unreadable orbit", st.flake_reason)

    def test_a_refused_frame_consumes_no_plan_attempt(self):
        """An attempt is a thing the PLANNER refused, not a frame the machine
        could not read. Counting blind frames as attempts would burn the
        3-attempt bound on telemetry noise."""
        p = mlib.b5_params_from_dict(self.mp)
        st = mlib._b5_enter(mlib.b5_initial_state(p), mlib.B5_ESCAPE,
                            PARK_UT, None)
        st = mlib.replace(st, last_plan_ut=0.0, plan_attempts=0)
        for i in range(6):
            st, _ = mlib.b5_decide(
                st, relay_snap(ut=PARK_UT + 20.0 * (i + 1),
                               time_to_periapsis=float("nan")))
        self.assertEqual(0, st.plan_attempts)
        self.assertIsNone(st.verdict)

    def test_the_escape_row_is_the_only_one_that_proves_the_craft_left_home(self):
        p = mlib.b5_params_from_dict(self.mp)
        # A machine that reached ORBIT and stopped: the row must be UNMET.
        parked = mlib.b5_initial_state(p)
        rows = {r.name: r for r in mlib.evaluate_b5_assertions(
            (), p, phases_reached=(mlib.B5_PRELAUNCH, mlib.B5_ORBIT),
            state=parked)}
        self.assertIn("escapedHomeSoi", rows)
        self.assertFalse(rows["escapedHomeSoi"].met)
        self.assertEqual("Laythe", rows["escapedHomeSoi"].detail["homeBody"])
        self.assertEqual("Jool", rows["escapedHomeSoi"].detail["parentBody"])
        # A flown hop: MET, carrying the node it authored.
        flown, _, _ = self._flown()
        rows = {r.name: r for r in mlib.evaluate_b5_assertions(
            (), p, phases_reached=flown.phases_reached, state=flown)}
        self.assertTrue(rows["escapedHomeSoi"].met)
        self.assertAlmostEqual(rows["escapedHomeSoi"].detail["escapeNodeDvMps"],
                               ESCAPE_NODE_MPS, delta=0.05)

    def test_a_planned_but_unflown_escape_does_not_satisfy_the_row(self):
        """The row's evidence is `relay_stage`, which advances on the coast frame
        that OBSERVED the parent SOI - not on the frame that planned a node.
        Otherwise a lane that authored a node and never burned it would report
        the escape as met."""
        p = mlib.b5_params_from_dict(self.mp)
        st = mlib._b5_enter(mlib.b5_initial_state(p), mlib.B5_ESCAPE,
                            PARK_UT, None)
        st, _ = mlib.b5_decide(st, relay_snap(ut=PARK_UT + 1.0))
        self.assertIsNotNone(st.escape_node_dv)
        rows = {r.name: r for r in mlib.evaluate_b5_assertions(
            (), p, phases_reached=st.phases_reached, state=st)}
        self.assertFalse(rows["escapedHomeSoi"].met)

    def test_the_escape_phase_is_budgeted_and_in_the_phase_list(self):
        p = mlib.b5_params_from_dict(self.mp)
        self.assertIn(mlib.B5_ESCAPE, mlib.B5_PHASES)
        self.assertEqual(p.escape_timeout,
                         mlib._b5_phase_budget(p, mlib.B5_ESCAPE))


class ParentRelayInertnessTests(unittest.TestCase):
    """THE FLAG-OFF CONTRACT, and it is the same one PAD-ALIGN and ORBIT-START
    carry: a lane that says nothing about the relay must decide and act exactly
    as it did before the mode existed."""

    B5_MOON = {"targetApoapsisMeters": 80000, "targetBodyName": "Mun",
               "homeBodyName": "Kerbin"}
    B7_INTERPLANETARY = {"targetApoapsisMeters": 700000,
                         "targetBodyName": "Duna", "homeBodyName": "Kerbin",
                         "interplanetaryTransfer": True,
                         "viaBodyNames": ["Sun"], "returnBodyName": "Sun",
                         "ejectionEccFloor": 1.05,
                         "transferMinApoapsisMeters": 0}

    def test_absent_keys_default_to_the_inert_shape(self):
        off = mlib.b5_params_from_dict({})
        self.assertFalse(off.parent_relay_transfer)
        self.assertEqual(0.0, off.escape_soi_speed)
        self.assertEqual(0.0, off.escape_max_dv)
        self.assertEqual(300.0, off.escape_node_min_lead)
        self.assertEqual(600.0, off.escape_timeout)
        self.assertEqual(mlib.RELAY_STAGE_ESCAPE,
                         mlib.b5_initial_state(off).relay_stage)

    def test_the_orbit_waypoint_still_hands_straight_to_plan_transfer(self):
        # THE SHARPEST INERTNESS STATEMENT AVAILABLE for this mode: the ORBIT
        # frame is the exact edge the relay diverts, so a flag-off lane taking it
        # unchanged is the whole claim.
        for name, params in (("B5", self.B5_MOON),
                             ("B7", self.B7_INTERPLANETARY)):
            p = mlib.b5_params_from_dict(params)
            st = mlib._b5_enter(mlib.b5_initial_state(p), mlib.B5_ORBIT,
                                1000.0, None)
            st, actions = mlib.b5_decide(st, snap(ut=1001.0, body="Kerbin",
                                                  situation="ORBITING",
                                                  apoapsis=80000.0,
                                                  periapsis=80000.0))
            self.assertEqual(mlib.B5_PLAN_TRANSFER, st.phase, name)
            self.assertEqual(mlib.RELAY_STAGE_ESCAPE, st.relay_stage, name)
            self.assertEqual([mlib.ACTION_SET_TARGET_BODY,
                              mlib.ACTION_MJ_PLAN_TRANSFER
                              if params is self.B5_MOON
                              else mlib.ACTION_MJ_PLAN_INTERPLANETARY_TRANSFER],
                             [a.kind for a in actions], name)

    def test_the_plan_action_is_unchanged_for_every_flag_off_lane(self):
        self.assertEqual(mlib.ACTION_MJ_PLAN_TRANSFER,
                         mlib._b5_transfer_plan_action(
                             mlib.b5_params_from_dict(self.B5_MOON)).kind)
        self.assertEqual(mlib.ACTION_MJ_PLAN_INTERPLANETARY_TRANSFER,
                         mlib._b5_transfer_plan_action(
                             mlib.b5_params_from_dict(
                                 self.B7_INTERPLANETARY)).kind)
        # ... and the PAD-ALIGN ASAP selector is untouched too (value 0.0 is
        # what picks WaitForPhaseAngle OFF).
        pad = dict(self.B7_INTERPLANETARY)
        pad.update(padAlignEjection=True)
        self.assertEqual(0.0, mlib._b5_transfer_plan_action(
            mlib.b5_params_from_dict(pad)).value)

    def test_the_burn_done_evidence_is_unchanged_for_every_flag_off_lane(self):
        b7 = mlib.b5_params_from_dict(self.B7_INTERPLANETARY)
        in_sun = snap(body="Sun", eccentricity=0.2, apoapsis=1.0e10)
        for stage in (mlib.RELAY_STAGE_ESCAPE, mlib.RELAY_STAGE_TRANSFER):
            self.assertTrue(mlib._b5_transfer_burn_done(b7, in_sun, stage),
                            "B7's left-the-home-SOI early return moved")
        b5 = mlib.b5_params_from_dict(self.B5_MOON)
        self.assertTrue(mlib._b5_transfer_burn_done(
            b5, snap(apoapsis=1.1e7), mlib.RELAY_STAGE_TRANSFER))
        self.assertFalse(mlib._b5_transfer_burn_done(
            b5, snap(apoapsis=1.0e6), mlib.RELAY_STAGE_TRANSFER))

    def test_no_assertion_row_is_added_for_a_flag_off_lane(self):
        for params in (self.B5_MOON, self.B7_INTERPLANETARY):
            p = mlib.b5_params_from_dict(params)
            rows = mlib.evaluate_b5_assertions(
                (), p, phases_reached=(mlib.B5_ORBIT,),
                state=mlib.b5_initial_state(p))
            self.assertNotIn("escapedHomeSoi", [r.name for r in rows])

    def test_the_escape_phase_is_unreachable_with_the_flag_off(self):
        # Even from a frame that would be a perfectly good escape origin.
        p = mlib.b5_params_from_dict({"homeBodyName": "Laythe",
                                      "targetBodyName": "Vall"})
        st = mlib._b5_enter(mlib.b5_initial_state(p), mlib.B5_ORBIT,
                            PARK_UT, None)
        st, _ = mlib.b5_decide(st, relay_snap(ut=PARK_UT + 1.0))
        self.assertEqual(mlib.B5_PLAN_TRANSFER, st.phase)
        self.assertNotIn(mlib.B5_ESCAPE, st.phases_reached)

    def test_the_relay_state_fields_ride_the_diff_but_not_the_line(self):
        """WHERE THE NEW FIELDS LIVE, and it is not a style question. Listing
        them in MACHINE_STATE_FIELDS would widen the ~5 s machine line of every
        mission for tokens only one lane can set (the CL-1 precedent);
        `diff_machine_state` contributes nothing when a field is absent on both
        sides, so DIFF is free everywhere else."""
        declared = dict(mlib.MACHINE_STATE_FIELDS)
        diffed = dict(mlib.MACHINE_DIFF_FIELDS)
        for attr in ("relay_stage", "escape_node_ut", "escape_node_dv"):
            self.assertIn(attr, diffed, attr)
            self.assertNotIn(attr, declared, attr)

    def test_the_free_text_refusal_never_reaches_a_kv_parsed_line(self):
        """THE INJECTION HAZARD, closed by ABSENCE rather than by a squeeze.
        `escape_refusal` is a whole sentence containing spaces AND '=' (it quotes
        the telemetry that failed); `diff_machine_state` renders through the
        UNsqueezed `_obs_fmt` and the fly loop wraps each change in a kv-shaped
        gate line, so listing it would inject bogus keys - the exact
        confident-wrong-reading class `MachineStateFreeTextTokenTests` exists
        for. The squeeze is unavailable because `_MACHINE_TOKEN_FIELDS` is pinned
        to be a SUBSET of MACHINE_STATE_FIELDS."""
        p = mlib.b5_params_from_dict(relay_params())
        st = mlib._b5_enter(mlib.b5_initial_state(p), mlib.B5_ESCAPE,
                            PARK_UT, None)
        st, _ = mlib.b5_decide(st, relay_snap(ut=PARK_UT + 1.0,
                                              time_to_periapsis=float("nan")))
        # The premise: it really is free text with both hazardous characters.
        self.assertIn(" ", st.escape_refusal)
        self.assertIn("=", st.escape_refusal)
        # ... and it is on NEITHER kv-rendered surface.
        self.assertNotIn("escape_refusal", dict(mlib.MACHINE_STATE_FIELDS))
        self.assertNotIn("escape_refusal", dict(mlib.MACHINE_DIFF_FIELDS))
        self.assertNotIn("escape_refusal", mlib._MACHINE_TOKEN_FIELDS)
        # It still reaches an operator: the give-up quotes it, and the give-up
        # IS squeezed on the line.
        late = relay_snap(ut=PARK_UT + p.escape_timeout + 10.0,
                          time_to_periapsis=float("nan"))
        st, _ = mlib.b5_decide(st, late)
        self.assertIn("unreadable orbit", st.flake_reason)
        self.assertIn("flake_reason", mlib._MACHINE_TOKEN_FIELDS)

    def test_the_new_action_field_leaves_every_pre_existing_action_equal(self):
        # Action is frozen + compared by value in the emitted-action tests; a new
        # field with a non-None default would have moved every one of them.
        self.assertEqual(mlib.Action(mlib.ACTION_MJ_EXECUTE_NODES),
                         mlib.Action(mlib.ACTION_MJ_EXECUTE_NODES))
        self.assertIsNone(mlib.Action(mlib.ACTION_MJ_EXECUTE_NODES).node_ut)


class _FakeNode(object):
    def __init__(self, ut, prograde):
        self.ut = ut
        self.prograde = prograde


class _FakeControl(object):
    """The kRPC `Vessel.Control` surface `ACTION_ADD_MANEUVER_NODE` touches."""

    def __init__(self, raises=False):
        self.nodes = []
        self.calls = []
        self._raises = raises

    def add_node(self, ut, prograde=0.0, normal=0.0, radial=0.0):
        self.calls.append((ut, prograde, normal, radial))
        if self._raises:
            raise RuntimeError("MechJeb-free node add refused by the server")
        node = _FakeNode(ut, prograde)
        self.nodes.append(node)
        return node


class _FakeVessel(object):
    def __init__(self, control):
        self.control = control


class _FakeSpaceCenter(object):
    def __init__(self, vessel):
        self.active_vessel = vessel


class _FakeConn(object):
    def __init__(self, space_center):
        self.space_center = space_center


class EscapeNodeRunnerSeamTests(unittest.TestCase):
    """The ONE new runner action, driven through the injectable seam with no
    kRPC, no MechJeb and no KSP. It is the thin half of the mode; the decision
    half is `escape_node_plan` above."""

    def _control(self, raises=False):
        fake = _FakeControl(raises=raises)
        c = mission_runner.KrpcMissionControl()
        c._conn = _FakeConn(_FakeSpaceCenter(_FakeVessel(fake)))
        return c, fake

    def test_the_node_is_added_at_the_ut_and_dv_the_machine_computed(self):
        c, fake = self._control()
        c.perform(mlib.Action(mlib.ACTION_ADD_MANEUVER_NODE, value=774.70,
                              node_ut=28_817_925.337))
        self.assertEqual(1, len(fake.calls))
        ut, prograde, normal, radial = fake.calls[0]
        self.assertAlmostEqual(28_817_925.337, ut, delta=1e-6)
        self.assertAlmostEqual(774.70, prograde, delta=1e-9)

    def test_the_burn_is_prograde_only(self):
        """PROGRADE ONLY is a contract, not an omission: this mode does not aim
        the asymptote, and a normal/radial component here would imply an aiming
        capability that does not exist and would silently diverge from the number
        the machine's own evidence row reports."""
        c, fake = self._control()
        c.perform(mlib.Action(mlib.ACTION_ADD_MANEUVER_NODE, value=774.70,
                              node_ut=1.0))
        _, _, normal, radial = fake.calls[0]
        self.assertEqual(0.0, normal)
        self.assertEqual(0.0, radial)

    def test_a_server_side_refusal_is_swallowed_and_leaves_no_node(self):
        """The PLAN_* throw/log/swallow contract verbatim: a failure must leave
        `node_count` at 0 so the ESCAPE phase's bounded re-plan cadence and its
        NAMED budget give-up own the outcome - never an unhandled mission error."""
        c, fake = self._control(raises=True)
        c.perform(mlib.Action(mlib.ACTION_ADD_MANEUVER_NODE, value=774.70,
                              node_ut=1.0))
        self.assertEqual([], fake.nodes)

    def test_a_second_add_is_refused_while_a_node_is_already_pending(self):
        """THE WINDOW THIS ACTION HAS AND THE PLAN_* ACTIONS DO NOT. The machine
        re-issues on its bounded cadence only while it BELIEVES node_count is 0 -
        a reading up to one poll old. `make_nodes` is safe under that lag because
        MechJeb replaces its own plan; `add_node` is PURELY ADDITIVE, so a stale
        read would stack a SECOND full escape burn. The runner checks LIVE truth
        at the moment of the call, which removes the window rather than surviving
        it."""
        c, fake = self._control()
        act = mlib.Action(mlib.ACTION_ADD_MANEUVER_NODE, value=774.70,
                          node_ut=28_817_925.337)
        c.perform(act)
        c.perform(act)
        c.perform(act)
        self.assertEqual(1, len(fake.nodes))
        self.assertEqual(1, len(fake.calls),
                         "the second add must not even reach add_node")

    def test_a_malformed_action_is_swallowed_too(self):
        # A None node_ut would be a machine bug, but the runner must still not
        # take the mission down with it.
        c, fake = self._control()
        c.perform(mlib.Action(mlib.ACTION_ADD_MANEUVER_NODE, value=774.70))
        self.assertEqual([], fake.nodes)


# ---------------------------------------------------------------------------
# Flight 2: the relay's first live data. Two defects, both fixed; the cells
# below RE-RUN THE DIAGNOSIS from the run's own bytes, because a ledger nobody
# can re-derive rots into folklore and these two numbers are the ones a future
# reader will be tempted to "simplify".
# ---------------------------------------------------------------------------


class Flight2DefectATests(unittest.TestCase):
    """DEFECT A: the escape CONTRACT was wrong, not the escape.

    Read the order of these cells, because it is the order the diagnosis went
    in and it is what rules out the three cheaper explanations first: the node
    was computed correctly, added exactly once, placed at the periapsis, and
    executed to within 1%. Only then does the contract itself come under
    suspicion."""

    R_PE = PARK_SMA * (1.0 - PARK_ECC)

    def _v_pe(self):
        return math.sqrt(MU_LAYTHE * (2.0 / self.R_PE - 1.0 / PARK_SMA))

    def test_the_node_the_flight_planned_is_the_old_formula_exactly(self):
        """RULES OUT AN ARITHMETIC SLIP. The flown 774.705 reproduces from the
        pre-fix formula and the fixture's own park to two decimals, so nothing
        was mis-derived - the formula was simply the wrong one."""
        flown = (math.sqrt(347.245 ** 2 + 2.0 * MU_LAYTHE / self.R_PE)
                 - self._v_pe())
        self.assertAlmostEqual(flown, F2_NODE_DV_MPS, delta=0.05)

    def test_the_node_was_placed_at_the_periapsis(self):
        """RULES OUT A WRONG ANCHOR. The node UT minus the entry UT must equal
        the periapsis clock the machine read on the frame it planned."""
        entry_ut, tt_pe = 28_817_028.977, 1_175.552
        self.assertAlmostEqual(F2_NODE_UT, entry_ut + tt_pe, delta=0.05)

    def test_the_executor_burned_the_node_and_not_more(self):
        """RULES OUT A LONG BURN. The propellant mass says what was actually
        spent, which is the B25-diagnosis method: 767.4 m/s measured against
        774.705 planned is under 1% - the executor did its job."""
        spent_t = (F2_LF_BEFORE - F2_LF_AFTER) * 0.005
        wet, ve = 9.854, 800.0 * 9.80665
        measured = ve * math.log(wet / (wet - spent_t))
        self.assertAlmostEqual(measured, 767.4, delta=1.0)
        self.assertLess(abs(measured - F2_NODE_DV_MPS) / F2_NODE_DV_MPS, 0.01)

    def test_the_resulting_laythe_orbit_is_the_one_the_node_asked_for(self):
        """AND RULES OUT THE LAST CHEAP EXPLANATION. The post-burn Laythe-frame
        eccentricity matches the planned hyperbola to three decimals, so the
        escape delivered EXACTLY what it was told to."""
        predicted = 1.0 + self.R_PE * 347.245 ** 2 / MU_LAYTHE
        self.assertAlmostEqual(predicted, F2_ECC_AFTER, delta=0.001)

    def test_the_contract_delivered_three_times_the_intended_excess(self):
        """**THE ROOT CAUSE.** KSP hands a departing vessel to the parent AT THE
        SOI BOUNDARY, where the home well is not fully climbed. The asymptotic
        excess is never reached, so what crossed was
        sqrt(v_inf^2 + 2*mu/r_soi) - 3.12x what the spec asked for."""
        delivered = math.sqrt(347.245 ** 2 + 2.0 * MU_LAYTHE / SOI_LAYTHE)
        self.assertAlmostEqual(delivered, 1083.69, delta=0.1)
        self.assertAlmostEqual(delivered / 347.245, 3.12, delta=0.01)

    def test_the_measured_jool_orbit_follows_from_that_delivered_speed(self):
        """THE CONSEQUENCE, CHECKED AGAINST THE FLIGHT'S OWN READING. The
        stage-2 entry orbit (a = 73.6 Mm) must sit inside the band the DELIVERED
        1,083.69 m/s admits and OUTSIDE the band the intended 347.245 would --
        which is what makes the SOI term the explanation rather than a story."""
        a_measured = (2.0 * RJ_ + F2_STAGE2_AP_M + self._stage2_pe()) / 2.0
        # 79.0 Mm, NOT the 73.6 a back-of-envelope gives: that one reads the
        # stage-2 ENTRY ALTITUDE (20.97 Mm) as the periapsis, where the run's own
        # telemetry line reports pe 19.70 Mm. The correction does not change the
        # diagnosis; it is recorded because the ledger quotes the number.
        self.assertAlmostEqual(a_measured / 1e6, 79.0, delta=0.5)
        lo, hi = self._band(1083.69)
        self.assertLessEqual(lo, a_measured)
        self.assertGreaterEqual(hi, a_measured)
        lo2, hi2 = self._band(347.245)
        self.assertLess(hi2, a_measured,
                        "the intended excess cannot produce the measured orbit, "
                        "which is the whole diagnosis")
        # ... and the CORRECTED contract's band excludes it too, so flight 3
        # reading anything like this again would be a NEW defect.
        lo3, hi3 = self._band(450.0)
        self.assertLess(hi3, a_measured)

    def _stage2_pe(self):
        # pe ALTITUDE at the stage-2 entry frame, from the run's telemetry line.
        return 19_697_088.474

    def _band(self, v_rel):
        v_moon = math.sqrt(MU_JOOL / A_LAYTHE)
        lo, hi = float("inf"), 0.0
        for deg in range(0, 181):
            th = math.radians(deg)
            for r in (A_LAYTHE - SOI_LAYTHE, A_LAYTHE, A_LAYTHE + SOI_LAYTHE):
                vt = v_moon + v_rel * math.cos(th)
                vr = v_rel * math.sin(th)
                energy = (vt * vt + vr * vr) / 2.0 - MU_JOOL / r
                if energy >= 0.0:
                    continue
                a = -MU_JOOL / (2.0 * energy)
                lo, hi = min(lo, a), max(hi, a)
        return lo, hi

    def test_the_flown_contract_admitted_two_lane_ending_outcomes(self):
        """WHY THE FIX IS NOT MERELY A dv SAVING. Swept over every departure
        direction, the contract flight 2 flew admits orbits that leave JOOL
        ENTIRELY and orbits whose periapsis is BELOW Jool's atmosphere top. The
        corrected contract admits neither."""
        for v_rel, expect_escape, min_pe_mm in ((1083.69, True, 5.51),
                                                (450.0, False, 11.01)):
            escaping, worst_pe = 0, float("inf")
            v_moon = math.sqrt(MU_JOOL / A_LAYTHE)
            for deg in range(0, 181):
                th = math.radians(deg)
                for r in (A_LAYTHE - SOI_LAYTHE, A_LAYTHE,
                          A_LAYTHE + SOI_LAYTHE):
                    vt = v_moon + v_rel * math.cos(th)
                    vr = v_rel * math.sin(th)
                    energy = (vt * vt + vr * vr) / 2.0 - MU_JOOL / r
                    if energy >= 0.0:
                        escaping += 1
                        continue
                    a = -MU_JOOL / (2.0 * energy)
                    h = r * abs(vt)
                    e = math.sqrt(max(0.0, 1.0 + 2.0 * energy * h * h
                                      / MU_JOOL ** 2))
                    worst_pe = min(worst_pe, a * (1.0 - e))
            self.assertEqual(expect_escape, escaping > 0, v_rel)
            self.assertAlmostEqual(worst_pe / 1e6, min_pe_mm, delta=0.05)
        # Jool's atmosphere tops out at 200 km over a 6,000 km radius.
        self.assertLess(5.51e6, RJ_ + 200_000.0)
        self.assertGreater(11.01e6, RJ_ + 200_000.0)

    def test_the_fix_is_cheaper_as_well_as_correct(self):
        mp = _spec("B26-laythe-vall-transfer.toml")["driver"]["missionParams"]
        p = mlib.b5_params_from_dict(mp)
        plan = mlib.escape_node_plan(p, relay_snap())
        self.assertAlmostEqual(plan.dv, ESCAPE_NODE_MPS, delta=0.05)
        self.assertAlmostEqual(F2_NODE_DV_MPS - plan.dv, 188.0, delta=1.0)
        self.assertLess(plan.dv, mp["escapeMaxDeltaVMps"])


class Flight2DefectBTests(unittest.TestCase):
    """DEFECT B: coast-warp-thrash, and it is an AMBIGUOUS CLOCK rather than a
    warp bug. The cap fired correctly at 500; what needed fixing is the decision
    that spent 501 issues."""

    TRIGGER0 = 35_000.0

    def test_the_thrash_target_reproduces_from_the_trigger_formula(self):
        """THE IDENTIFICATION. The flake line's target is exactly
        `ut + (tts - trigger0)`, which is what proves the coast was in the
        TIME-MODE correction-trigger branch and not one of the other three."""
        tts = 55_579.679                                    # the flake line's own
        self.assertAlmostEqual(F2_THRASH_UT + (tts - self.TRIGGER0),
                               F2_THRASH_TARGET, delta=0.01)

    def test_the_two_clocks_that_flapped_are_two_different_encounters(self):
        """THE CAUSE. The post-escape Jool ellipse re-crosses LAYTHE'S OWN
        orbit, so the patched-conic solver alternated between a Vall encounter
        and a Laythe one and `time_to_soi` stepped ~22 ks between them."""
        step = F2_TTS_VALL - F2_TTS_LAYTHE
        self.assertAlmostEqual(step, 21_952.1, delta=1.0)
        # The pe of the flown Jool orbit really is below Laythe's radius, which
        # is what makes a Laythe re-encounter geometrically possible at all.
        self.assertLess(19_697_088.474 + RJ_, A_LAYTHE)

    def test_that_step_blows_through_the_retarget_threshold_both_ways(self):
        """WHY IT THRASHED RATHER THAN SETTLED. The shared native-warp policy
        retargets on an EARLIER target past a 120 s floor and on a LATER one
        past 2% of the span - a 22 ks step clears both by orders of magnitude,
        so every flip cancelled a running warp and re-armed it."""
        step = F2_TTS_VALL - F2_TTS_LAYTHE
        self.assertGreater(step, mlib.WARP_RETARGET_THRESHOLD_SECONDS)
        span = F2_THRASH_TARGET - F2_THRASH_UT
        self.assertGreater(step, max(mlib.WARP_RETARGET_THRESHOLD_SECONDS,
                                     0.02 * span))
        self.assertEqual(500, mlib.MAX_PHASE_WARP_ISSUES,
                         "the cap fired at 501 against 500; if this constant "
                         "moves the ledger's numbers need re-reading")

    def test_the_identity_check_suppresses_a_foreign_clock(self):
        """THE FIX, at the predicate. A `next_body` that POSITIVELY names a
        non-target body is not a refinement of the armed target."""
        self.assertTrue(mlib.soi_clock_describes_target("Vall", "Vall"))
        self.assertFalse(mlib.soi_clock_describes_target("Laythe", "Vall"))

    def test_it_fails_OPEN_on_an_unread_next_body(self):
        """WHY IT IS SAFE EVERYWHERE RATHER THAN BEHIND THE RELAY FLAG. Only a
        POSITIVE mismatch suppresses; a blank name passes, so a read fault can
        never close a proven lane's trigger."""
        self.assertTrue(mlib.soi_clock_describes_target("", "Vall"))
        self.assertTrue(mlib.soi_clock_describes_target("Vall", ""))
        self.assertTrue(mlib.soi_clock_describes_target("", ""))

    def test_the_hold_keeps_an_armed_warp_instead_of_cancelling_it(self):
        """AND THE HOLD, which is the half that actually stops the thrash:
        suppressing the branch alone would fall through and re-arm from the same
        foreign clock."""
        self.assertTrue(mlib.coast_foreign_soi_clock_hold(
            "Laythe", "Vall", F2_THRASH_TARGET, F2_THRASH_UT))
        # nothing armed -> nothing to hold
        self.assertFalse(mlib.coast_foreign_soi_clock_hold(
            "Laythe", "Vall", None, F2_THRASH_UT))
        # target already reached -> the arrival path owns the frame
        self.assertFalse(mlib.coast_foreign_soi_clock_hold(
            "Laythe", "Vall", F2_THRASH_UT - 1.0, F2_THRASH_UT))
        # a TARGET clock never holds - the ordinary policy decides
        self.assertFalse(mlib.coast_foreign_soi_clock_hold(
            "Vall", "Vall", F2_THRASH_TARGET, F2_THRASH_UT))
        # an unreadable ut fails closed
        self.assertFalse(mlib.coast_foreign_soi_clock_hold(
            "Laythe", "Vall", F2_THRASH_TARGET, float("nan")))

    def test_the_flapping_coast_now_emits_nothing_instead_of_re_arming(self):
        """END TO END, on the flown geometry: a coast with an armed warp and a
        Laythe reading must emit NO action at all. Before the fix this frame
        produced a cancel plus a re-dispatch."""
        mp = _spec("B26-laythe-vall-transfer.toml")["driver"]["missionParams"]
        p = mlib.b5_params_from_dict(mp)
        st = mlib._b5_enter(mlib.b5_initial_state(p), mlib.B5_COAST_TO_TARGET,
                            F2_THRASH_UT - 1000.0, None)
        st = mlib.replace(st, relay_stage=mlib.RELAY_STAGE_TRANSFER,
                          warp_to_cmd=F2_THRASH_TARGET)
        foreign = snap(ut=F2_THRASH_UT, body="Jool", situation="ORBITING",
                       altitude=F2_STAGE2_ALT_M, apoapsis=F2_STAGE2_AP_M,
                       periapsis=19_697_088.474, eccentricity=0.675,
                       node_count=0, next_body="Laythe",
                       time_to_soi=F2_TTS_LAYTHE, warp_mode=mlib.WARP_RAILS,
                       warp_rate=7120.209, warping_to=F2_THRASH_TARGET)
        issues_before = st.phase_warp_issues
        for _ in range(20):
            st, actions = mlib.b5_decide(st, foreign)
            self.assertEqual([], actions,
                             "a foreign-clock frame must emit nothing")
        self.assertEqual(issues_before, st.phase_warp_issues)
        self.assertEqual(F2_THRASH_TARGET, st.warp_to_cmd)
        self.assertEqual(0, st.correction_rounds_done,
                         "a Laythe reading must not fire a Vall round either")
        self.assertIsNone(st.verdict)

    def test_a_target_clock_frame_still_drives_the_warp_normally(self):
        """THE OTHER SIDE, so the fix cannot be 'hold forever': the SAME coast
        with a VALL reading must still arm and drive its warp."""
        mp = _spec("B26-laythe-vall-transfer.toml")["driver"]["missionParams"]
        p = mlib.b5_params_from_dict(mp)
        st = mlib._b5_enter(mlib.b5_initial_state(p), mlib.B5_COAST_TO_TARGET,
                            F2_THRASH_UT - 1000.0, None)
        st = mlib.replace(st, relay_stage=mlib.RELAY_STAGE_TRANSFER)
        good = snap(ut=F2_THRASH_UT, body="Jool", situation="ORBITING",
                    altitude=F2_STAGE2_ALT_M, apoapsis=F2_STAGE2_AP_M,
                    periapsis=19_697_088.474, eccentricity=0.675,
                    node_count=0, next_body="Vall", time_to_soi=F2_TTS_VALL)
        st, actions = mlib.b5_decide(st, good)
        self.assertIn(mlib.ACTION_WARP_TO_UT, [a.kind for a in actions])
        self.assertAlmostEqual(st.warp_to_cmd,
                               F2_THRASH_UT + (F2_TTS_VALL - 35_000.0),
                               delta=0.01)


if __name__ == "__main__":
    unittest.main()
