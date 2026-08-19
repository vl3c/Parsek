"""Unit tests for the b24_gilly_orbit lane and its V15 loop pair.

NO MLIB CELLS HERE, deliberately, and that absence is itself the claim: B24
introduces NO machine change. `startInOrbit` is body-generic and was live-proven
on B23 flight 2, so `test_b23_ike_orbit.py` already owns the door's behaviour and
its inertness-when-off. Re-testing it against Gilly's numbers would test the same
code twice and pin nothing new.

WHAT IS HERE is the b17 / b23 `SpecArithmeticTests` pattern: the committed specs
state arithmetic relationships between their own values in prose, and every one
of those relationships is CHECKED rather than trusted, so a parameter edit that
breaks a derivation reds here instead of on a flight. Gilly earns more of these
cells than any prior lane for one reason - its SOI is 126,123 m, so several of
the spec's numbers are within one order of magnitude of each other and a plausible
copy-from-B23 edit puts the park outside the SOI or the approach clamp above the
coast it has to survive.

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

# STOCK CONSTANTS, the STOCK_WARP_ALTITUDE_LIMITS precedent: KSP's bodies are on
# rails with elements that never change, so these are DATA and not tolerances.
MU_EVE = 8.1717302e12          # m^3/s^2
R_EVE = 700_000.0              # m
MU_GILLY = 8_289_449.8         # m^3/s^2
R_GILLY = 13_000.0             # m
SOI_GILLY = 126_123.27         # m from Gilly's centre
A_GILLY = 31_500_000.0         # m
E_GILLY = 0.55
GILLY_HIGHEST_TERRAIN_M = 6_400.0

# The fixture's MEASURED Eve park, read off
# `fixtures/saves/eve-park-kerbalx/persistent.sfs` (unchanged by the strip - it
# removed only Parsek's own state, never the world).
PARK_SMA = 5_685_808.535976883
PARK_ECC = 6.3638813848804419e-05
PARK_INC_DEG = 5.3373958993381008
PARK_UT = 15_764_031.825015297

# The pessimistic FLOOR of Gilly's SOI-entry -> periapsis coast, from B24's
# header: gravity is negligible (2*mu/r_soi = 131.45 m^2/s^2 against v_inf^2 up
# to ~169,000), so the path is the straight-line half-chord
# sqrt(r_soi^2 - b^2) ~ 124.0 km flown at v_inf, and the FASTEST arrival in the
# intercept band (v_inf ~411 m/s) gives ~302 s. 300 sits under the whole band.
GILLY_APPROACH_COAST_FLOOR_SECONDS = 300.0

# Poll-frame units both warp rules are measured in: mlib's own
# _WARP_SAFETY_SECONDS = 1.0 ("two ~0.5 s polls") for the NOMINAL frame, and
# B19 flight 4's MEASURED 3.16 s (27,596 game s at x8,738) rounded up for the
# PESSIMISTIC one.
NOMINAL_POLL_SECONDS = 0.5
PESSIMISTIC_POLL_SECONDS = 4.0


def _spec(name):
    with open(os.path.join(HARNESS_ROOT, "scenarios", name), "rb") as fh:
        return tomllib.load(fh)


def snap(**kw):
    return mlib.TelemetrySnapshot(**kw)


def parked_snap(**overrides):
    """A frame reading the committed `eve-park-kerbalx` fixture's MEASURED Eve
    park. Periapsis / apoapsis are derived from the save's own SMA and ECC:
    SMA*(1 -/+ ECC) - R_Eve."""
    base = dict(ut=PARK_UT, body="Eve", situation="ORBITING",
                apoapsis=PARK_SMA * (1.0 + PARK_ECC) - R_EVE,
                periapsis=PARK_SMA * (1.0 - PARK_ECC) - R_EVE,
                eccentricity=PARK_ECC,
                altitude=PARK_SMA - R_EVE)
    base.update(overrides)
    return snap(**base)


def transfer_coast_seconds(intercept_radius):
    """Half-period of the Hohmann transfer ellipse from the park radius to an
    intercept at `intercept_radius`: pi*sqrt(a_t^3/mu)."""
    a_t = (PARK_SMA + intercept_radius) / 2.0
    return math.pi * math.sqrt(a_t ** 3 / MU_EVE)


class GillyOrbitArithmeticTests(unittest.TestCase):
    """The derivations B24's own header states, re-run."""

    def test_gillys_period_is_what_both_v15_specs_quote(self):
        # P = 2*pi*sqrt(a^3/mu). Quoted to the milli-second in V15M section 2
        # and V15T's header, and it is the number the whole phase-lock cadence
        # prediction rests on - if it is wrong, both loop specs' seeds are wrong
        # together and nothing else catches it.
        period = 2.0 * math.pi * math.sqrt(A_GILLY ** 3 / MU_EVE)
        self.assertAlmostEqual(period, 388_587.378, delta=0.5)

    def test_the_park_and_synodic_periods_are_what_the_header_quotes(self):
        park = 2.0 * math.pi * math.sqrt(PARK_SMA ** 3 / MU_EVE)
        gilly = 2.0 * math.pi * math.sqrt(A_GILLY ** 3 / MU_EVE)
        synodic = 1.0 / abs(1.0 / park - 1.0 / gilly)
        self.assertAlmostEqual(park, 29_799.7, delta=5.0)
        self.assertAlmostEqual(synodic, 32_274.7, delta=10.0)
        # "about 1.08 park orbits" - the regime B11/B23's transfer budgets were
        # sized against, and the claim the transfer-burn budget leans on.
        self.assertAlmostEqual(synodic / park, 1.083, delta=0.01)

    def test_gillys_gravity_really_is_negligible_on_the_approach(self):
        # THE HEADLINE WARP FACT, and the exact INVERSION of Ike's. B23 had to
        # solve the approach hyperbola because 2*mu/r_soi = 35,381 DOMINATED
        # v_inf^2 = 8,482; here the same ratio runs the other way by two orders
        # of magnitude, which is what makes the straight-line coast estimate
        # legitimate rather than lazy.
        escape_term = 2.0 * MU_GILLY / SOI_GILLY
        self.assertAlmostEqual(escape_term, 131.45, delta=0.1)
        slowest_arrival_vinf_sq = 87.6 ** 2      # the apoapsis-intercept end
        self.assertLess(escape_term * 10.0, slowest_arrival_vinf_sq,
                        "Gilly's gravity is not negligible even at the slowest "
                        "arrival in the band; the straight-line coast estimate "
                        "in the header would be invalid")

    def test_the_approach_coast_floor_is_under_the_whole_intercept_band(self):
        # The floor every warp number is sized against. Path = the half-chord
        # from SOI entry to periapsis at a 10 km periapsis (impact parameter
        # ~23 km against a 126.1 km SOI), flown at v_inf.
        r_p = R_GILLY + 10_000.0
        chord = math.sqrt(SOI_GILLY ** 2 - r_p ** 2)
        fastest_vinf = 411.2                      # the peak of the band, r_i ~ 17-20 Mm
        self.assertLessEqual(GILLY_APPROACH_COAST_FLOOR_SECONDS,
                             chord / fastest_vinf,
                             "the sizing floor is ABOVE the fastest arrival's "
                             "actual coast; every warp bound derived from it is "
                             "optimistic")


class SpecArithmeticTests(unittest.TestCase):
    """Cells that read the COMMITTED B24 spec."""

    @classmethod
    def setUpClass(cls):
        cls.spec = _spec("B24-gilly-orbit.toml")
        cls.mp = cls.spec["driver"]["missionParams"]

    # --- the three-rule warp contract (B21's header, applied at Gilly) -------

    def test_one_frame_cannot_swallow_the_gilly_approach(self):
        # RULE 3, the house form (B23's cell verbatim with Gilly's floor): the
        # rails RATE at the approach ceiling must sit under a FIFTH of the
        # SOI-entry -> periapsis coast.
        rate = mlib.RAILS_WARP_RATES[self.mp["approachMaxWarpFactor"]]
        self.assertLessEqual(rate, GILLY_APPROACH_COAST_FLOOR_SECONDS / 5.0,
                             "the approach ceiling can swallow Gilly's "
                             "SOI-entry -> periapsis coast in one poll")

    def test_the_approach_ceiling_kept_the_six_times_margin_b23_took(self):
        # The DELIBERATE half of the choice, pinned so a future edit cannot
        # quietly take the 1.2x option: factor 3 (x50) passes the rule above at
        # 50 <= 60 and was REJECTED for margin, because at the smallest SOI in
        # the game an overshoot costs a whole flight.
        rate = mlib.RAILS_WARP_RATES[self.mp["approachMaxWarpFactor"]]
        self.assertGreaterEqual(GILLY_APPROACH_COAST_FLOOR_SECONDS / 5.0,
                                6.0 * rate,
                                "the approach ceiling no longer carries the 6x "
                                "margin the header argues for")

    def test_the_soi_lead_exceeds_two_nominal_coast_frames(self):
        # RULE 1. One NOMINAL 0.5 s poll at coastWarpFactor is the unit.
        coast_rate = mlib.RAILS_WARP_RATES[self.mp["coastWarpFactor"]]
        one_frame = coast_rate * NOMINAL_POLL_SECONDS
        self.assertGreaterEqual(self.mp["soiLeadSeconds"], 2.0 * one_frame,
                                "soiLeadSeconds is under two nominal coast "
                                "frames")

    def test_the_approach_window_swallows_one_pessimistic_coast_frame(self):
        # RULE 2, in the STRONGER form this lane needs. The bare 2x-the-lead
        # form is also checked, but the binding constraint is that a PESSIMISTIC
        # 4 s coast frame entering from just outside the window must still land
        # INSIDE it, where the clamp rules - otherwise the frame skips the whole
        # window and the ceiling never engages (B19 flight 4's failure).
        coast_rate = mlib.RAILS_WARP_RATES[self.mp["coastWarpFactor"]]
        pessimistic_frame = coast_rate * PESSIMISTIC_POLL_SECONDS
        self.assertGreaterEqual(self.mp["approachWindowSeconds"],
                                2.0 * pessimistic_frame,
                                "one pessimistic coast frame can cross the "
                                "whole approach window")
        self.assertGreaterEqual(self.mp["approachWindowSeconds"],
                                2.0 * self.mp["soiLeadSeconds"])

    def test_the_in_soi_stair_is_below_the_approach_ceiling(self):
        # The flyby pair comes DOWN from the 3/4 every other capture lane uses;
        # neither half may exceed the approach ceiling, or the stair would warp
        # FASTER inside the SOI than on final approach to it.
        cap = self.mp["approachMaxWarpFactor"]
        self.assertLessEqual(self.mp["flybyWarpFactor"], cap)
        self.assertLessEqual(self.mp["flybyMaxWarpFactor"], cap)
        self.assertLessEqual(self.mp["flybyWarpFactor"],
                             self.mp["flybyMaxWarpFactor"])

    # --- the transfer coast band --------------------------------------------

    def test_the_coast_budget_covers_the_worst_intercept_geometry(self):
        # The eccentric-target consequence: the coast is a BAND, and B21's lesson
        # is to budget an eccentric geometry at its EXPENSIVE extreme.
        shortest = transfer_coast_seconds(A_GILLY * (1.0 - E_GILLY))
        nominal = transfer_coast_seconds(A_GILLY)
        longest = transfer_coast_seconds(A_GILLY * (1.0 + E_GILLY))
        self.assertAlmostEqual(shortest, 34_391.0, delta=100.0)
        self.assertAlmostEqual(nominal, 88_108.0, delta=100.0)
        self.assertAlmostEqual(longest, 156_377.0, delta=200.0)
        self.assertGreaterEqual(self.mp["coastTimeoutSeconds"], 5.0 * longest,
                                "coastTimeoutSeconds is sized on the median "
                                "rather than on the expensive end of the band")

    def test_the_transfer_burn_budget_covers_a_few_synodic_waits(self):
        park = 2.0 * math.pi * math.sqrt(PARK_SMA ** 3 / MU_EVE)
        gilly = 2.0 * math.pi * math.sqrt(A_GILLY ** 3 / MU_EVE)
        synodic = 1.0 / abs(1.0 / park - 1.0 / gilly)
        self.assertGreaterEqual(self.mp["transferBurnTimeoutSeconds"],
                                3.0 * synodic)

    # --- the entry gate ------------------------------------------------------

    def test_the_entry_gate_admits_the_committed_fixtures_measured_park(self):
        # THE cell that couples the spec to the fixture. If either moves, this
        # reds before a KSP boot is spent.
        params = mlib.b5_params_from_dict(self.mp)
        verdict, reason = mlib.start_in_orbit_frame_verdict(params, parked_snap())
        self.assertEqual(verdict, mlib.START_IN_ORBIT_IN_GATE, reason)

    def test_the_entry_floor_clears_eves_atmosphere_by_an_order(self):
        # Eve's atmosphere tops out at 90 km, and this floor is an order of
        # magnitude above B23's Duna one for exactly that reason.
        self.assertGreaterEqual(self.mp["startInOrbitMinPeriapsisMeters"],
                                10.0 * 90_000.0)

    def test_the_entry_ceiling_stays_clear_of_gillys_own_shell(self):
        # A start park allowed up to Gilly's PERIAPSIS altitude would let the
        # lane begin inside an unplanned encounter. Gilly's periapsis radius is
        # a*(1-e); the altitude is that minus Eve's radius.
        gilly_periapsis_altitude = A_GILLY * (1.0 - E_GILLY) - R_EVE
        self.assertLess(self.mp["startInOrbitMaxApoapsisMeters"],
                        gilly_periapsis_altitude)

    # --- the Gilly shell: every in-SOI bound, checked against 126,123 m -------

    def test_the_park_ceiling_stays_well_inside_gillys_soi(self):
        # Gilly SOI 126,123.27 m from the centre, radius 13,000 m => 113,123 m
        # of ALTITUDE. The header claims the ceiling sits at ~58% of the SOI
        # RADIUS, which is the "durably inside the shell" claim rather than the
        # bare "not an escape" one.
        park_apo_radius = self.mp["parkMaxApoapsisMeters"] + R_GILLY
        self.assertLess(self.mp["parkMaxApoapsisMeters"],
                        SOI_GILLY - R_GILLY)
        self.assertLessEqual(park_apo_radius / SOI_GILLY, 0.60)

    def test_the_park_floor_and_the_assertion_floor_agree_and_clear_terrain(self):
        # The B19/B20/B21/B22 convention (parkMinPeriapsisMeters ==
        # targetPeriapsisFloorMeters), plus the terrain check the floor exists
        # for. 1.25x is the tightest ratio in the suite and is argued in the
        # spec; the cell pins that it is at least above the terrain.
        self.assertEqual(self.mp["parkMinPeriapsisMeters"],
                         self.mp["targetPeriapsisFloorMeters"])
        self.assertGreater(self.mp["targetPeriapsisFloorMeters"],
                           GILLY_HIGHEST_TERRAIN_M)

    def test_the_correction_request_survives_the_corpus_three_times_spread(self):
        # The finding-16d corpus fits no tight law and carries a ~3x within-body
        # spread, so the request has to survive BOTH ends: 3x short must clear
        # the assertion floor, and 3x long must stay inside the SOI shell (a
        # periapsis above the shell is not an encounter at all).
        req = self.mp["courseCorrectPeriapsisMeters"]
        self.assertGreaterEqual(req / 3.0,
                                self.mp["targetPeriapsisFloorMeters"],
                                "a 3x under-delivery lands below the terrain floor")
        self.assertLess(req * 3.0 + R_GILLY, SOI_GILLY,
                        "a 3x over-delivery puts the periapsis outside Gilly's "
                        "SOI, i.e. no encounter")

    def test_the_correction_request_sits_in_the_k_near_one_regime(self):
        # The corpus's own ordering: the "arrives higher" bias is a
        # req/SOI < 1% phenomenon and inverts above ~4%. This request is chosen
        # to sit up near the Jool row (24.43%, k = 0.974), which is the only
        # regime in which a 126 km SOI can be aimed at at all.
        ratio = self.mp["courseCorrectPeriapsisMeters"] / SOI_GILLY
        self.assertGreater(ratio, 0.04)

    # --- the delta-v margin --------------------------------------------------

    # DERIVED IN B24'S HEADER from the fixture's own part list and the stock cfg
    # masses, and re-stated here as the two numbers every budget leans on.
    DV_AVAILABLE_MPS = 1023.2
    WORST_GEOMETRY_HOP_MPS = 753.0

    def test_the_four_round_correction_budget_fits_inside_the_margin(self):
        # THE binding constraint on this lane, and the reason the cap is 50
        # rather than B23's 80. The cap is PER ROUND and up to FOUR rounds can
        # fire (the two this spec schedules plus mlib's own
        # MAX_ARRIVAL_EXTRA_ROUNDS arrival-quality extras).
        margin = self.DV_AVAILABLE_MPS - self.WORST_GEOMETRY_HOP_MPS
        self.assertLessEqual(4.0 * self.mp["maxCorrectionDvMps"], margin,
                             "four capped correction rounds no longer fit "
                             "inside the worst-geometry delta-v margin")

    def test_the_correction_cap_is_below_the_transfer_it_corrects(self):
        # The B15 rule: a "correction" priced above the departure burn is the
        # wrong plan, and the cap is what makes the machine discard it. The
        # cheapest departure in the intercept band is ~307 m/s.
        self.assertLess(self.mp["maxCorrectionDvMps"], 307.0)

    def test_the_second_correction_trigger_always_fires(self):
        # THE ECCENTRIC-TARGET TRAP, pinned. A trigger placed at a fraction of
        # "the climb" can SILENTLY never fire when the intercept altitude is a
        # free variable; it must sit above the start park's apoapsis and below
        # the LOWEST possible intercept altitude.
        trigger = self.mp["correctionTriggerAltsMeters"][1]
        park_apoapsis = PARK_SMA * (1.0 + PARK_ECC) - R_EVE
        lowest_intercept_altitude = A_GILLY * (1.0 - E_GILLY) - R_EVE
        self.assertGreater(trigger, park_apoapsis)
        self.assertLess(trigger, lowest_intercept_altitude)

    def test_the_transfer_apoapsis_floor_cannot_red_a_low_side_intercept(self):
        park_apoapsis = PARK_SMA * (1.0 + PARK_ECC) - R_EVE
        lowest_intercept_altitude = A_GILLY * (1.0 - E_GILLY) - R_EVE
        self.assertGreater(self.mp["transferMinApoapsisMeters"], park_apoapsis)
        self.assertLess(self.mp["transferMinApoapsisMeters"],
                        lowest_intercept_altitude)

    # --- fixture + posture ---------------------------------------------------

    def test_the_spec_points_at_the_parsek_stripped_fixture(self):
        """B23 FLIGHT 1'S FINDING, PINNED SO IT CANNOT SILENTLY REVERT.

        That flight ran against a `--keep-parsek` harvest and came back PASS
        attempt 1 with every assertion met -- and the subject was wrong anyway,
        because the committed-restore path re-resumed the save's own committed
        recording and the seam StartRecording answered `already=true`. Nothing in
        the harness's expectation vocabulary can express "the recording is rooted
        at Eve", so a revert of this one string would restore the defect with
        EVERY verifier still green."""
        self.assertEqual("fixtures/saves/eve-park-kerbalx",
                         self.spec["fixture"]["saveTemplate"])

    def test_the_stripped_fixture_really_carries_no_parsek_state(self):
        """The other half, checked against the BYTES rather than the name. FIVE
        node types, not B23's three: the two extra ones are crew-reservation
        bookkeeping that exists only because this subject is CREWED, and they
        point at the recording that was removed."""
        save_dir = os.path.join(HARNESS_ROOT,
                                self.spec["fixture"]["saveTemplate"])
        self.assertTrue(os.path.isdir(save_dir), save_dir)
        self.assertFalse(os.path.isdir(os.path.join(save_dir, "Parsek")),
                         "the fixture carries a Parsek/ sidecar directory")
        with open(os.path.join(save_dir, "persistent.sfs"),
                  encoding="utf-8", errors="replace") as fh:
            sfs = fh.read()
        for node in ("RECORDING_TREE", "GROUP_HIERARCHY", "MILESTONE_STATE",
                     "KERBAL_SLOTS", "CREW_REPLACEMENTS"):
            self.assertNotIn(node, sfs,
                             "%s survived in the stripped fixture" % node)
        # The node ITSELF must survive - a flyable template without it records
        # nothing on the FLIGHT route.
        self.assertIn("name = ParsekScenario", sfs)

    def test_the_recordings_window_matches_the_stripped_fixtures_arithmetic(self):
        rec_dir = os.path.join(HARNESS_ROOT,
                               self.spec["fixture"]["saveTemplate"],
                               "Parsek", "Recordings")
        carried = (len([n for n in os.listdir(rec_dir) if n.endswith(".prec")])
                   if os.path.isdir(rec_dir) else 0)
        window = self.spec["expectations"]["recordings"]["count"]
        self.assertEqual(0, carried)
        # carried + this lane's single-piece product, with one slot of headroom
        # for a load-time optimizer split at the Eve->Gilly boundary.
        self.assertEqual({"min": carried + 1, "max": carried + 2}, window)

    def test_the_spec_arms_no_gating_block(self):
        # A first-flight READING lane must arm nothing: the ARMED_ALLOWLIST in
        # harness/lib/test_hlib.py owns that decision and this spec is not on it.
        for block in ("rewind", "recordings"):
            sub = (self.spec.get("expectations") or {}).get(block) or {}
            self.assertNotIn("gating", sub)
            for nested in sub.values():
                if isinstance(nested, dict):
                    self.assertNotIn("gating", nested)


class V15CalibrationSeedTests(unittest.TestCase):
    """The V15 pair ships with CALIBRATION SEEDS rather than measurements, so
    what CAN be checked is their internal consistency - the relationships the
    headers assert between the seeds. A seed that is merely wrong is expected and
    is fixed at calibration; a seed set that is INCONSISTENT WITH ITS OWN
    DERIVATION would survive the calibration pass unnoticed, because the pass
    substitutes numbers rather than re-deriving the shape."""

    PLACEHOLDER_TREE = "0" * 32

    @classmethod
    def setUpClass(cls):
        cls.m = _spec("V15M-gilly-player-loop.toml")
        cls.t = _spec("V15T-gilly-ts-arrival.toml")

    def _jump_uts(self, spec):
        return [float(s["args"]["ut"]) for s in spec["driver"]["steps"]
                if s.get("cmd") == "TimeJump"]

    def test_both_specs_still_carry_the_placeholder_tree_id(self):
        # The banner's claim, checked. When this cell reds, the calibration pass
        # has happened and the cell should be replaced by the real id - which is
        # the point: it is the marker that the pair is not yet flyable.
        for spec in (self.m, self.t):
            trees = [s["args"]["tree"] for s in spec["driver"]["steps"]
                     if s.get("cmd") in ("MissionConfig", "StartLoopPlayback")]
            self.assertTrue(trees)
            for tree in trees:
                self.assertEqual(self.PLACEHOLDER_TREE, tree)

    def test_v15m_jumps_are_strictly_forward(self):
        # The single most load-bearing property of a seeded bracket: a backward
        # jump answers REJECTED and is only a WARN, so a non-monotonic seed set
        # would green while dwelling nowhere. (Both specs FORBID the token too;
        # this cell catches the error before a KSP boot is spent.)
        uts = self._jump_uts(self.m)
        self.assertEqual(8, len(uts))
        self.assertEqual(sorted(uts), uts)
        self.assertEqual(len(set(uts)), len(uts))

    def test_the_v15m_cycle_two_bracket_is_cycle_one_plus_one_gilly_period(self):
        # The cadence claim, as arithmetic: B24's span is far under P, so
        # QuantizeCadenceToMultipleOfP takes k = 1 and cycle N = A1 + (N-1)*P.
        period = 2.0 * math.pi * math.sqrt(A_GILLY ** 3 / MU_EVE)
        uts = self._jump_uts(self.m)
        for cycle1, cycle2 in zip(uts[:4], uts[4:]):
            self.assertAlmostEqual(cycle2 - cycle1, period, delta=1.5,
                                   msg="the cycle-2 seeds are not one Gilly "
                                       "period past the cycle-1 seeds")

    def test_the_v15m_bracket_offsets_are_v14ms_minus180_minus60_plus140(self):
        uts = self._jump_uts(self.m)
        seam = uts[0] + 180.0
        self.assertEqual([-180.0, -60.0, 140.0], [u - seam for u in uts[:3]])
        self.assertEqual([-180.0, -60.0, 140.0], [u - (uts[4] + 180.0)
                                                  for u in uts[4:7]])

    def test_v15t_reuses_v15ms_third_cycle_one_bracket_jump(self):
        # The pair's whole point is that the two lanes observe the SAME instant
        # from the two scenes; if the seeds drift apart that stops being true and
        # nothing else notices.
        t_jumps = self._jump_uts(self.t)
        self.assertEqual(1, len(t_jumps))
        self.assertEqual(self._jump_uts(self.m)[2], t_jumps[0])

    def test_both_specs_point_at_the_same_pending_fixture(self):
        self.assertEqual(self.m["fixture"]["saveTemplate"],
                         self.t["fixture"]["saveTemplate"])
        self.assertEqual("fixtures/saves/gilly-orbit-recorded",
                         self.m["fixture"]["saveTemplate"])

    def test_neither_v15_spec_arms_a_gating_block(self):
        for spec in (self.m, self.t):
            for block in ("rewind", "recordings"):
                sub = (spec.get("expectations") or {}).get(block) or {}
                self.assertNotIn("gating", sub)
                for nested in sub.values():
                    if isinstance(nested, dict):
                        self.assertNotIn("gating", nested)

    def test_v15t_leaves_the_anomaly_sweep_fully_armed(self):
        # The header's deliberate choice: the reading run MEASURES whether
        # V14T's `icon-off-orbit` raise survives a change of parent and moon, so
        # pre-tolerating the token would destroy the measurement.
        self.assertEqual([], self.t["expectations"]["allowedAnomalies"])
        self.assertEqual([], self.m["expectations"]["allowedAnomalies"])


class GillyToleranceTests(unittest.TestCase):
    """The tolerance claim both V15 headers are built on, re-run. It is the one
    quantity that makes this pair different from V14, so it is checked rather
    than quoted."""

    def test_gillys_tolerance_is_the_tightest_of_the_flown_moons(self):
        v_circ = math.sqrt(MU_EVE / A_GILLY)
        tol = SOI_GILLY / v_circ
        self.assertAlmostEqual(v_circ, 509.3, delta=1.0)
        self.assertAlmostEqual(tol, 247.6, delta=1.0)
        # Ike, the nearest flown comparison (SOI and v_orb from the Jool research
        # doc's own committed table plus B23's constants).
        mu_duna, a_ike, soi_ike = 3.0136321e11, 3_200_000.0, 1_049_598.9
        tol_ike = soi_ike / math.sqrt(mu_duna / a_ike)
        self.assertGreater(tol_ike, 10.0 * tol)

    def test_the_eccentricity_swings_the_tolerance_by_three_and_a_half_times(self):
        # THE half a circular moon cannot express, and the reason this lane's own
        # sensitivity is only knowable after B24 flies.
        v_peri = math.sqrt(MU_EVE * (2.0 / (A_GILLY * (1 - E_GILLY)) - 1.0 / A_GILLY))
        v_apo = math.sqrt(MU_EVE * (2.0 / (A_GILLY * (1 + E_GILLY)) - 1.0 / A_GILLY))
        self.assertAlmostEqual(SOI_GILLY / v_peri, 133.0, delta=2.0)
        self.assertAlmostEqual(SOI_GILLY / v_apo, 460.0, delta=3.0)
        self.assertAlmostEqual(v_peri / v_apo, 3.44, delta=0.05)

    def test_gillys_duty_cycle_is_tighter_than_pols(self):
        # The Jool research doc's own metric (tol / period), and the claim that
        # Gilly is the stock system's tightest.
        period = 2.0 * math.pi * math.sqrt(A_GILLY ** 3 / MU_EVE)
        duty = (SOI_GILLY / math.sqrt(MU_EVE / A_GILLY)) / period
        self.assertAlmostEqual(duty, 6.37e-4, delta=0.05e-4)
        self.assertLess(duty, 9.22e-4)          # Pol, the doc's tightest row


if __name__ == "__main__":
    unittest.main()
