"""Unit tests for the b25_laythe_orbit lane and its V16 loop pair.

STILL NO MLIB CELLS, and that absence is again the claim: B25 introduces NO
machine change. `startInOrbit` is body-generic and was live-proven on B23 flight
2; `ejectionEccFloor` is the branch eight interplanetary lanes already use and is
not gated on `interplanetaryTransfer` anywhere. What IS new is that this lane is
the first INWARD transfer the b5 moon path has been asked to fly, so the two
sites that encode "outward" are pinned HERE, against the committed spec's own
values, rather than being trusted to a header paragraph:

  * `InwardTransferAuditTests` runs `_b5_transfer_burn_done` against a
    park-shaped frame and a post-burn-shaped frame and asserts the evidence is
    False then True. That single pair is the whole values-only workaround, and if
    a future edit drops `ejectionEccFloor` or sets `viaBodyNames` it reds here
    instead of on a 6,000-second flight.
  * The correction-round degradation is pinned as a SHAPE (exactly one scheduled
    round) with its reason in the docstring, so nobody "fixes" it by adding a
    second altitude value that cannot fire.

Otherwise this is the b17 / b23 / b24 `SpecArithmeticTests` pattern: the
committed spec states arithmetic relationships between its own values in prose,
and every one of those relationships is CHECKED rather than trusted.

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
# The Laythe row is quoted from the committed table in
# `docs/dev/research/same-parent-reaim-jool-system.md` section 3.1, which in turn
# cites `Source/Parsek.Tests/MultiMoonAlignmentTests.cs`.
MU_JOOL = 2.82528e14           # m^3/s^2
R_JOOL = 6_000_000.0           # m
JOOL_ATMOSPHERE_TOP_M = 200_000.0
# THE ASSET-DERIVED figure, not the wiki's 2,455,985,200 (which is ~14.6 m high).
# `B22-jool-orbit.toml` derived it and `V13A-jool-loop-arrival` PINNED it as a
# literal against a live run, so this is a measured constant rather than a quoted
# one - see `docs/dev/research/same-parent-reaim-jool-system.md` section 5.2.
JOOL_SOI = 2_455_985_185.0     # m from Jool's centre

A_LAYTHE = 27_184_000.0        # m
MU_LAYTHE = 1.962e12           # m^3/s^2
R_LAYTHE = 500_000.0           # m
# CARRIES THE J2 CAVEAT and it is recorded rather than ignored: the research
# doc's own open-items table flags the moon SOI radii as WIKI FIGURES and asks
# for them to be re-read from the live bodies, precisely because Jool's own
# asset-vs-wiki gap turned out to be ~14.6 m. Every bound in B25 that is derived
# from this number carries margin in the tens of percent (the park ceiling is
# 37.6% of the SOI radius, the correction request 6.71%), so a metres-scale
# correction moves nothing - but the number is not asset-verified and the spec
# should not pretend otherwise.
SOI_LAYTHE = 3_723_645.8       # m from Laythe's centre
V_ORB_LAYTHE = 3_223.8         # m/s (the section-3.1 table's circular figure)
LAYTHE_ATMOSPHERE_TOP_M = 50_000.0

# The other Jool moons, whose orbital SHELLS this inward transfer crosses. Only
# their SMAs matter here.
A_VALL = 43_152_000.0
A_TYLO = 68_500_000.0
A_BOP = 128_500_000.0
A_POL = 179_890_000.0
# Pol's APOAPSIS radius, B22's own measured figure (ecc 0.17085).
POL_APOAPSIS_RADIUS = 210_624_206.5

# The fixture's MEASURED Jool park, read off
# `fixtures/saves/jool-park-nerv/persistent.sfs` (unchanged by the strip - it
# removed only Parsek's own state, never the world).
PARK_SMA = 590_325_784.58972526
PARK_ECC = 7.9440625486254423e-06
PARK_INC_DEG = 3.005060548898792
PARK_UT = 27_787_319.41951086

# THE APPROACH COAST FLOOR, from B25's header. The regime is MIXED
# (v_inf^2 / (2*mu/r_soi) = 1.445 - neither Ike's gravity-dominated nor Gilly's
# negligible), so the SOI-entry -> periapsis time is solved on the approach
# hyperbola and comes out a remarkably flat 2,048-2,139 game s across the whole
# admissible periapsis range. 2,000 sits under the band.
LAYTHE_APPROACH_COAST_FLOOR_SECONDS = 2000.0

# Poll-frame units both warp rules are measured in: mlib's own
# _WARP_SAFETY_SECONDS = 1.0 ("two ~0.5 s polls") for the NOMINAL frame, and
# B19 flight 4's MEASURED 3.16 s (27,596 game s at x8,738) rounded up for the
# PESSIMISTIC one.
NOMINAL_POLL_SECONDS = 0.5
PESSIMISTIC_POLL_SECONDS = 4.0

# DERIVED IN B25'S HEADER from the fixture's own part list and the stock cfg
# masses, and re-stated here as the two numbers every budget leans on.
DV_AVAILABLE_MPS = 3967.3
WORST_HOP_MPS = 1573.9


def _spec(name):
    with open(os.path.join(HARNESS_ROOT, "scenarios", name), "rb") as fh:
        return tomllib.load(fh)


def snap(**kw):
    return mlib.TelemetrySnapshot(**kw)


def parked_snap(**overrides):
    """A frame reading the committed `jool-park-nerv` fixture's MEASURED Jool
    park. Periapsis / apoapsis are derived from the save's own SMA and ECC:
    SMA*(1 -/+ ECC) - R_Jool."""
    base = dict(ut=PARK_UT, body="Jool", situation="ORBITING",
                apoapsis=PARK_SMA * (1.0 + PARK_ECC) - R_JOOL,
                periapsis=PARK_SMA * (1.0 - PARK_ECC) - R_JOOL,
                eccentricity=PARK_ECC,
                altitude=PARK_SMA - R_JOOL)
    base.update(overrides)
    return snap(**base)


def transfer_ecc_for_intercept(intercept_radius):
    """The eccentricity of the transfer ellipse whose APOAPSIS is the park radius
    and whose PERIAPSIS is `intercept_radius`. This is the inward mirror of the
    usual formula and it is what `ejectionEccFloor` is sized against."""
    return (PARK_SMA - intercept_radius) / (PARK_SMA + intercept_radius)


def soi_entry_to_periapsis_seconds(periapsis_altitude):
    """Time from Laythe SOI entry to periapsis on the approach hyperbola, by
    hyperbolic Kepler (`M = e sinh H - H`, `t = M sqrt(|a|^3/mu)`).

    B23's method, and it is REQUIRED here rather than optional: the regime is
    MIXED, so neither Ike's "gravity dominates" nor Gilly's straight-line chord
    is a legitimate approximation."""
    a_t = (PARK_SMA + A_LAYTHE) / 2.0
    v_at_intercept = math.sqrt(MU_JOOL * (2.0 / A_LAYTHE - 1.0 / a_t))
    v_inf = v_at_intercept - math.sqrt(MU_JOOL / A_LAYTHE)
    a = -MU_LAYTHE / v_inf ** 2
    r_p = R_LAYTHE + periapsis_altitude
    e = 1.0 + r_p / abs(a)
    h = math.acosh((SOI_LAYTHE / abs(a) + 1.0) / e)
    m = e * math.sinh(h) - h
    return m * math.sqrt(abs(a) ** 3 / MU_LAYTHE)


class LaytheConstantsTests(unittest.TestCase):
    """THE J-TABLE ROW, re-run. `docs/dev/research/same-parent-reaim-jool-system.md`
    section 3.1 reasons over five Jool moons and NOT ONE has ever been flown;
    Laythe is the first, so its row is checked here rather than quoted. The
    tolerance formula is `ToleranceSecondsFor` (MissionPeriodicity.cs, Orbital
    branch) = `SoiRadius / OrbitalVelocity`."""

    def test_laythes_period_is_what_both_v16_specs_quote(self):
        # The number the whole cadence prediction rests on - if it is wrong, both
        # loop specs' seeds are wrong together and nothing else catches it.
        period = 2.0 * math.pi * math.sqrt(A_LAYTHE ** 3 / MU_JOOL)
        self.assertAlmostEqual(period, 52_980.879454, delta=0.001)

    def test_the_committed_table_row_reproduces(self):
        v_circ = math.sqrt(MU_JOOL / A_LAYTHE)
        period = 2.0 * math.pi * math.sqrt(A_LAYTHE ** 3 / MU_JOOL)
        tol = SOI_LAYTHE / v_circ
        self.assertAlmostEqual(v_circ, V_ORB_LAYTHE, delta=0.5)
        self.assertAlmostEqual(tol, 1155.03, delta=0.1)
        self.assertAlmostEqual(tol / period, 2.1801e-2, delta=0.0001e-2)

    def test_laythe_is_the_loosest_tolerance_subject_flown_so_far(self):
        # THE REASON IT IS THE FIRST JOOL MOON: it is the deliberate complement
        # to Gilly, which is the tightest duty cycle in the stock system. Gilly's
        # row (Eve's moon) from the same committed table.
        mu_eve, a_gilly, soi_gilly = 8.1717302e12, 31_500_000.0, 126_123.27
        duty_laythe = ((SOI_LAYTHE / math.sqrt(MU_JOOL / A_LAYTHE))
                       / (2.0 * math.pi * math.sqrt(A_LAYTHE ** 3 / MU_JOOL)))
        duty_gilly = ((soi_gilly / math.sqrt(mu_eve / a_gilly))
                      / (2.0 * math.pi * math.sqrt(a_gilly ** 3 / mu_eve)))
        self.assertGreater(duty_laythe, 30.0 * duty_gilly)

    def test_the_period_ordering_inverts_against_every_prior_moon_lane(self):
        # THE FACT EVERY BUDGET IN THE SPEC IS SIZED ON. On Kerbin->Mun /
        # Duna->Ike / Eve->Gilly the PARK period is the short one and the synodic
        # is roughly one park orbit. Here the park period is 101x the moon's and
        # the synodic is ~1.01 MOON periods, i.e. under 1% of a park orbit.
        park = 2.0 * math.pi * math.sqrt(PARK_SMA ** 3 / MU_JOOL)
        moon = 2.0 * math.pi * math.sqrt(A_LAYTHE ** 3 / MU_JOOL)
        synodic = 1.0 / abs(1.0 / park - 1.0 / moon)
        self.assertGreater(park, 100.0 * moon)
        self.assertAlmostEqual(synodic, 53_509.6, delta=1.0)
        self.assertLess(synodic / park, 0.01)
        self.assertAlmostEqual(synodic / moon, 1.0099, delta=0.001)

    def test_the_transfer_is_inward_and_the_coast_is_a_number_not_a_band(self):
        # Both endpoints are circular, so the Hohmann half-period has no free
        # variable - the opposite of B24, whose eccentric target made every
        # quantity a band.
        self.assertGreater(PARK_SMA, A_POL)
        self.assertGreater(PARK_SMA, 3.0 * A_POL)
        a_t = (PARK_SMA + A_LAYTHE) / 2.0
        tof = math.pi * math.sqrt(a_t ** 3 / MU_JOOL)
        self.assertAlmostEqual(tof, 1_014_004.5, delta=1.0)

    def test_the_approach_regime_is_mixed_so_neither_approximation_is_honest(self):
        # THE THIRD DISTINCT ANSWER IN THREE LANES, and the reason the coast has
        # to be solved on the hyperbola rather than copied from either sibling.
        a_t = (PARK_SMA + A_LAYTHE) / 2.0
        v_inf = (math.sqrt(MU_JOOL * (2.0 / A_LAYTHE - 1.0 / a_t))
                 - math.sqrt(MU_JOOL / A_LAYTHE))
        escape_term = 2.0 * MU_LAYTHE / SOI_LAYTHE
        ratio = v_inf ** 2 / escape_term
        self.assertAlmostEqual(v_inf, 1233.88, delta=0.5)
        self.assertAlmostEqual(ratio, 1.445, delta=0.01)
        # NEITHER approximation applies: Ike's is ratio << 1, Gilly's ratio >> 1.
        self.assertGreater(ratio, 0.5)
        self.assertLess(ratio, 5.0)

    def test_the_approach_coast_floor_is_under_the_whole_periapsis_range(self):
        # The floor every warp number is sized against, checked at BOTH ends of
        # the admissible park window rather than at one convenient point.
        for pe in (60_000.0, 250_000.0, 900_000.0):
            self.assertLessEqual(LAYTHE_APPROACH_COAST_FLOOR_SECONDS,
                                 soi_entry_to_periapsis_seconds(pe),
                                 "the sizing floor is ABOVE the actual coast at a "
                                 "%.0f m periapsis; every warp bound derived from "
                                 "it is optimistic" % pe)
        # And the band really is as flat as the header claims (a 4.4% spread over
        # a 15x periapsis range), which is itself the mixed regime showing up as
        # a shape rather than as a number.
        lo = soi_entry_to_periapsis_seconds(60_000.0)
        hi = soi_entry_to_periapsis_seconds(900_000.0)
        self.assertLess(hi / lo, 1.10)


class InwardTransferAuditTests(unittest.TestCase):
    """THE TWO SITES THAT ENCODE 'OUTWARD', pinned against the committed spec.

    B25 is the first INWARD transfer the b5 moon path has flown. Five of the
    seven areas the machine was audited over are direction-agnostic by
    construction and need no cell (the MechJeb plan call takes no direction
    argument; the coast gates are body- and time-based; the window solver is
    unreachable on the moon path; the flyby / capture read the target's own
    frame; the burn-stagnation watchdog compares BOTH apsides). These two are the
    ones that would have bitten, and each is pinned as BEHAVIOUR rather than as a
    value."""

    @classmethod
    def setUpClass(cls):
        cls.spec = _spec("B25-laythe-orbit.toml")
        cls.mp = cls.spec["driver"]["missionParams"]
        cls.params = mlib.b5_params_from_dict(cls.mp)

    def test_the_burn_done_evidence_is_false_at_the_park_and_true_after_the_burn(self):
        """THE WHOLE VALUES-ONLY WORKAROUND, IN ONE CELL.

        `_b5_transfer_burn_done`'s DEFAULT branch is
        `apoapsis >= transfer_min_apoapsis`, and on an inward transfer the burn
        happens AT what becomes the transfer's apoapsis, so the home-frame
        apoapsis never moves: any floor above the park is unreachable forever and
        any floor below it passes on the frame BEFORE the burn. The
        `ejectionEccFloor` branch reads the home-frame ECCENTRICITY instead,
        which moves from 7.944e-06 to 0.911956, and is not gated on
        `interplanetaryTransfer` anywhere.

        If a future edit drops the floor, or sets `viaBodyNames` (whose first
        disjunct short-circuits this branch to True), this cell reds instead of a
        6,000-second flight discovering that TRANSFER-BURN exits before it
        burns."""
        self.assertGreater(self.params.ejection_ecc_floor, 0.0,
                           "ejectionEccFloor is off, so the burn-done evidence "
                           "falls back to the apoapsis floor, which cannot move "
                           "on an inward transfer")
        self.assertFalse(mlib._b5_transfer_burn_done(self.params, parked_snap()),
                         "the burn-done evidence is already satisfied AT THE PARK")
        post_burn = parked_snap(
            eccentricity=transfer_ecc_for_intercept(A_LAYTHE),
            periapsis=A_LAYTHE - R_JOOL)
        self.assertTrue(mlib._b5_transfer_burn_done(self.params, post_burn),
                        "a completed inward transfer burn does not satisfy the "
                        "burn-done evidence")

    def test_the_apoapsis_floor_really_would_have_been_vacuous(self):
        # The negative half, stated as arithmetic rather than as prose: with the
        # ecc branch OFF, the SAME park frame passes the default evidence at the
        # spec's declared floor. That is what makes the floor inert-at-0 rather
        # than merely unused.
        self.assertEqual(0, self.mp["transferMinApoapsisMeters"])
        no_ecc = mlib.b5_params_from_dict(dict(self.mp, ejectionEccFloor=0.0))
        self.assertTrue(mlib._b5_transfer_burn_done(no_ecc, parked_snap()),
                        "the apoapsis floor is NOT vacuous at the park, so the "
                        "spec's whole argument for moving off it needs re-reading")

    def test_the_ecc_floor_sits_between_the_park_and_a_pol_crossing_periapsis(self):
        # The floor has a GEOMETRIC meaning rather than an arbitrary one: 0.55 is
        # above the eccentricity at which the transfer's periapsis reaches Pol's
        # orbit (0.5329), so clearing it certifies "the periapsis has fallen
        # inside the moon system". And it is comfortably below the completed
        # transfer's 0.911956, so an under-burn a correction would fix does not
        # fail the phase outright.
        floor = self.params.ejection_ecc_floor
        self.assertGreater(floor, PARK_ECC * 1000.0)
        self.assertGreater(floor, transfer_ecc_for_intercept(A_POL))
        self.assertLess(floor, transfer_ecc_for_intercept(A_LAYTHE))
        # And not so low that it stops distinguishing a burn from a correction.
        self.assertGreater(floor, 0.3)

    def test_via_body_names_is_unset_and_the_coast_admits_only_jool(self):
        """`viaBodyNames` IS NOT A TUNING KNOB ON THIS LANE, IT IS A DEFECT.

        Naming the HOME body makes `ejectionEccFloor` vacuous (the branch returns
        True on `snapshot.body in via_bodies` before it reads the eccentricity).
        Naming any OTHER body legalises a moon transit that `_b5_coast_bodies`
        should be failing loudly - and this descent crosses Pol's, Bop's, Tylo's
        and Vall's orbital shells. The schema deliberately does not declare the
        key (an undeclared param is neither rejected nor read), so THIS is the
        guard that actually bites."""
        self.assertNotIn("viaBodyNames", self.mp)
        self.assertEqual((), self.params.via_bodies)
        self.assertEqual(("", "Jool"), mlib._b5_coast_bodies(self.params))

    def test_exactly_one_scheduled_correction_round_and_it_fires_post_burn(self):
        """THE KNOWN, ACCEPTED DEGRADATION, pinned as a SHAPE so nobody 'fixes'
        it with a second altitude value that cannot fire.

        `_b5_correction_round_ready`'s altitude mode is a bare
        `altitude >= trigger[idx]` LEVEL test, not a rising-edge crossing. On
        this lane altitude DESCENDS from 584,330,474 m, so a trigger above the
        park never fires at all and one below it fires on the FIRST coast frame -
        a `[0, X]` list would simply spend both rounds back to back at transfer
        start, which is the shape the mid-coast round exists to prevent. The
        late refinement is delegated to mlib's own direction-agnostic
        arrival-quality extras instead."""
        self.assertEqual((0.0,), self.params.correction_trigger_alts)
        self.assertEqual((), self.params.correction_trigger_time_to_soi)
        state = mlib.b5_initial_state(self.params)
        self.assertTrue(mlib._b5_correction_round_ready(state, parked_snap()))
        # ... and the arithmetic that makes a second altitude value impossible:
        # every candidate is either below the park (fires immediately) or above
        # it (never fires), because there is nothing in between on a descent.
        park_alt = PARK_SMA * (1.0 + PARK_ECC) - R_JOOL
        self.assertGreater(park_alt, A_POL - R_JOOL)

    def test_the_arrival_quality_extras_are_the_declared_fallback(self):
        # The delegation is only honest if the fallback exists and is bounded, so
        # the constant is read rather than quoted. It is a SAFETY NET (it needs a
        # PREDICTED arrival periapsis BELOW the assertion floor), not a general
        # refinement, which is why the spec says so rather than claiming the
        # mid-coast round survived.
        self.assertEqual(2, mlib.MAX_ARRIVAL_EXTRA_ROUNDS)
        self.assertGreater(mlib.ARRIVAL_RECORRECT_MAX_TTS_SECONDS,
                           mlib.ARRIVAL_RECORRECT_MIN_TTS_SECONDS)


class SpecArithmeticTests(unittest.TestCase):
    """Cells that read the COMMITTED B25 spec."""

    @classmethod
    def setUpClass(cls):
        cls.spec = _spec("B25-laythe-orbit.toml")
        cls.mp = cls.spec["driver"]["missionParams"]

    # --- the three-rule warp contract (B21's header, applied at Laythe) ------

    def test_one_frame_cannot_swallow_the_laythe_approach(self):
        # RULE 3, the house form: the rails RATE at the approach ceiling must sit
        # under a FIFTH of the SOI-entry -> periapsis coast.
        rate = mlib.RAILS_WARP_RATES[self.mp["approachMaxWarpFactor"]]
        self.assertLessEqual(rate, LAYTHE_APPROACH_COAST_FLOOR_SECONDS / 5.0,
                             "the approach ceiling can swallow Laythe's "
                             "SOI-entry -> periapsis coast in one poll")

    def test_the_approach_ceiling_kept_its_eight_times_margin(self):
        # The DELIBERATE half of the choice, pinned so a future edit cannot
        # quietly take the x100 option: factor 4 passes rule 3 at 4.0x and was
        # REJECTED because a PESSIMISTIC 4 s frame at x100 is 19.6% of the coast,
        # so five such frames swallow it end to end.
        rate = mlib.RAILS_WARP_RATES[self.mp["approachMaxWarpFactor"]]
        self.assertGreaterEqual(LAYTHE_APPROACH_COAST_FLOOR_SECONDS / 5.0,
                                8.0 * rate,
                                "the approach ceiling no longer carries the 8x "
                                "margin the header argues for")
        pessimistic = rate * PESSIMISTIC_POLL_SECONDS
        self.assertGreaterEqual(LAYTHE_APPROACH_COAST_FLOOR_SECONDS,
                                10.0 * pessimistic,
                                "one pessimistic approach frame is more than a "
                                "tenth of the whole in-SOI coast")

    def test_the_soi_lead_exceeds_two_nominal_coast_frames(self):
        # RULE 1. One NOMINAL 0.5 s poll at coastWarpFactor is the unit.
        coast_rate = mlib.RAILS_WARP_RATES[self.mp["coastWarpFactor"]]
        one_frame = coast_rate * NOMINAL_POLL_SECONDS
        self.assertGreaterEqual(self.mp["soiLeadSeconds"], 2.0 * one_frame,
                                "soiLeadSeconds is under two nominal coast frames")

    def test_the_approach_window_swallows_one_pessimistic_coast_frame(self):
        # RULE 2, in the STRONGER form: a PESSIMISTIC 4 s coast frame entering
        # from just outside the window must still land INSIDE it, where the clamp
        # rules - otherwise the frame skips the whole window and the ceiling
        # never engages (B19 flight 4's failure).
        coast_rate = mlib.RAILS_WARP_RATES[self.mp["coastWarpFactor"]]
        pessimistic_frame = coast_rate * PESSIMISTIC_POLL_SECONDS
        self.assertGreaterEqual(self.mp["approachWindowSeconds"],
                                2.0 * pessimistic_frame,
                                "one pessimistic coast frame can cross the whole "
                                "approach window")
        self.assertGreaterEqual(self.mp["approachWindowSeconds"],
                                2.0 * self.mp["soiLeadSeconds"])

    def test_the_clamped_approach_stretch_stays_affordable(self):
        # THE PRICE OF RULE 3's CHOICE, checked rather than asserted: the clamped
        # window costs approachWindowSeconds / rate WALL seconds on every flight,
        # and it must stay small against the mission budget or the x50 choice was
        # the wrong trade after all.
        rate = mlib.RAILS_WARP_RATES[self.mp["approachMaxWarpFactor"]]
        wall = self.mp["approachWindowSeconds"] / rate
        mission = next(s["budget"] for s in self.spec["driver"]["steps"]
                       if s.get("phase") == "mission")
        self.assertLessEqual(wall, 0.10 * mission)

    def test_the_coast_rate_fallback_fits_inside_the_mission_budget(self):
        # WHY x1,000 AND NOT x10,000. The rails cap is the FALLBACK (the primary
        # path is a native warp_to), and the whole coast at that cap must still
        # fit the mission budget - which is what makes paying a certain 1,600
        # wall s of clamped window to avoid this contingency the wrong trade.
        a_t = (PARK_SMA + A_LAYTHE) / 2.0
        tof = math.pi * math.sqrt(a_t ** 3 / MU_JOOL)
        rate = mlib.RAILS_WARP_RATES[self.mp["coastWarpFactor"]]
        mission = next(s["budget"] for s in self.spec["driver"]["steps"]
                       if s.get("phase") == "mission")
        self.assertLessEqual(tof / rate, mission)

    def test_the_in_soi_stair_is_at_or_below_the_approach_ceiling(self):
        cap = self.mp["approachMaxWarpFactor"]
        self.assertLessEqual(self.mp["flybyWarpFactor"], cap)
        self.assertLessEqual(self.mp["flybyMaxWarpFactor"], cap)
        self.assertLessEqual(self.mp["flybyWarpFactor"],
                             self.mp["flybyMaxWarpFactor"])

    def test_every_commanded_factor_is_altitude_legal_at_the_park_floor(self):
        # Laythe HAS AN ATMOSPHERE, so its stock warp-altitude table is the one
        # place a commanded factor can be silently clamped. The approach ceiling
        # must be legal at the lowest altitude the lane admits.
        limits = mlib.STOCK_WARP_ALTITUDE_LIMITS["Laythe"]
        self.assertLessEqual(limits[self.mp["approachMaxWarpFactor"]],
                             self.mp["parkMinPeriapsisMeters"])

    # --- the transfer / coast budgets ----------------------------------------

    def test_the_coast_budget_covers_several_missed_intercepts(self):
        a_t = (PARK_SMA + A_LAYTHE) / 2.0
        tof = math.pi * math.sqrt(a_t ** 3 / MU_JOOL)
        self.assertGreaterEqual(self.mp["coastTimeoutSeconds"], 3.0 * tof,
                                "coastTimeoutSeconds does not cover a missed "
                                "first intercept plus its re-coast")

    def test_the_transfer_burn_budget_covers_several_synodic_waits(self):
        park = 2.0 * math.pi * math.sqrt(PARK_SMA ** 3 / MU_JOOL)
        moon = 2.0 * math.pi * math.sqrt(A_LAYTHE ** 3 / MU_JOOL)
        synodic = 1.0 / abs(1.0 / park - 1.0 / moon)
        self.assertGreaterEqual(self.mp["transferBurnTimeoutSeconds"],
                                5.0 * synodic)

    def test_the_flyby_and_capture_budgets_cover_the_in_soi_coast(self):
        coast = soi_entry_to_periapsis_seconds(self.mp["parkMaxApoapsisMeters"])
        self.assertGreaterEqual(self.mp["flybyTimeoutSeconds"], 20.0 * coast)
        # The capture executor autowarps that coast and MechJeb's own 1x
        # pre-ignition WARPALIGN hold is ~600 game s of it.
        self.assertGreaterEqual(self.mp["captureBurnTimeoutSeconds"],
                                10.0 * (coast + 600.0))

    # --- the entry gate ------------------------------------------------------

    def test_the_entry_gate_admits_the_committed_fixtures_measured_park(self):
        # THE cell that couples the spec to the fixture. If either moves, this
        # reds before a KSP boot is spent.
        params = mlib.b5_params_from_dict(self.mp)
        verdict, reason = mlib.start_in_orbit_frame_verdict(params, parked_snap())
        self.assertEqual(verdict, mlib.START_IN_ORBIT_IN_GATE, reason)

    def test_the_entry_floor_encodes_the_lanes_actual_precondition(self):
        """THE MOST LOAD-BEARING ENTRY CONJUNCT ON THIS LANE, and it does NOT
        mean what a periapsis floor usually means.

        Everywhere else the floor means "not decaying into the parent's
        atmosphere"; Jool's tops out at 200 km and this park sits 2,900x above
        it, so that reading would make the conjunct decorative. What it actually
        encodes is the PRECONDITION OF THE WHOLE LANE: the transfer is INWARD
        only while the park is OUTSIDE the moon system, so the floor must clear
        Pol's APOAPSIS altitude."""
        floor = self.mp["startInOrbitMinPeriapsisMeters"]
        self.assertGreater(floor, POL_APOAPSIS_RADIUS - R_JOOL)
        self.assertGreater(floor, 1000.0 * JOOL_ATMOSPHERE_TOP_M)
        self.assertLess(floor, PARK_SMA * (1.0 - PARK_ECC) - R_JOOL)

    def test_the_entry_ceiling_stays_well_inside_jools_soi(self):
        ceiling = self.mp["startInOrbitMaxApoapsisMeters"]
        self.assertGreater(ceiling, PARK_SMA * (1.0 + PARK_ECC) - R_JOOL)
        self.assertLess((ceiling + R_JOOL) / JOOL_SOI, 0.40)

    # --- the Laythe shell: the atmosphere is what the floors guard ------------

    def test_the_park_floor_and_the_assertion_floor_agree_and_clear_the_air(self):
        # The B19/B20/B21/B22/B24 convention (parkMinPeriapsisMeters ==
        # targetPeriapsisFloorMeters), plus the check the floor exists for. On
        # THIS lane it guards an ATMOSPHERE rather than terrain (the B17 Duna
        # pattern): a graze would put an Atmospheric TrackSection into the
        # product and perturb the park.
        self.assertEqual(self.mp["parkMinPeriapsisMeters"],
                         self.mp["targetPeriapsisFloorMeters"])
        self.assertGreater(self.mp["targetPeriapsisFloorMeters"],
                           LAYTHE_ATMOSPHERE_TOP_M)
        self.assertGreaterEqual(self.mp["targetPeriapsisFloorMeters"],
                                1.2 * LAYTHE_ATMOSPHERE_TOP_M)

    def test_the_park_ceiling_admits_the_arrival_spread_and_stays_in_the_shell(self):
        # LOAD-BEARING TWICE. It must ADMIT a 3x-long corrected arrival (or it
        # reds a perfectly good flight), and it must keep the park durably INSIDE
        # the shell rather than grazing its edge.
        ceiling = self.mp["parkMaxApoapsisMeters"]
        three_x_long = 3.0 * self.mp["courseCorrectPeriapsisMeters"]
        self.assertGreaterEqual(ceiling, three_x_long)
        self.assertLess(ceiling + R_LAYTHE, SOI_LAYTHE)
        self.assertLessEqual((ceiling + R_LAYTHE) / SOI_LAYTHE, 0.40)

    def test_the_correction_request_survives_the_corpus_three_times_spread(self):
        # The finding-16d corpus fits no tight law and carries a ~3x within-body
        # spread, so the request has to survive BOTH ends: 3x short must clear
        # the atmosphere AND the assertion floor, and 3x long must stay well
        # inside the SOI shell.
        req = self.mp["courseCorrectPeriapsisMeters"]
        self.assertGreaterEqual(req / 3.0, self.mp["targetPeriapsisFloorMeters"],
                                "a 3x under-delivery lands below the assertion floor")
        self.assertGreaterEqual(req / 3.0, 1.5 * LAYTHE_ATMOSPHERE_TOP_M,
                                "a 3x under-delivery grazes Laythe's atmosphere, "
                                "which pollutes the product rather than just "
                                "failing the flight")
        self.assertLess(req * 3.0 + R_LAYTHE, SOI_LAYTHE)

    def test_the_correction_request_sits_in_the_k_near_one_regime(self):
        # The corpus's own ordering: the "arrives higher" bias is a
        # req/SOI < 1% phenomenon and inverts above ~4%. This request is chosen
        # to sit near the Eve row (5.875%, k = 0.997), the one row whose k is
        # essentially unity.
        ratio = self.mp["courseCorrectPeriapsisMeters"] / SOI_LAYTHE
        self.assertGreater(ratio, 0.04)
        self.assertLess(ratio, 0.10)

    # --- the delta-v margin --------------------------------------------------

    def test_the_three_round_correction_budget_fits_inside_the_margin(self):
        # At most THREE rounds can fire here (the ONE this spec schedules plus
        # mlib's two arrival-quality extras), which is itself lower than every
        # prior lane's because of the inward-transfer audit - and part of why a
        # larger per-round cap is affordable.
        margin = DV_AVAILABLE_MPS - WORST_HOP_MPS
        rounds = (len(self.mp["correctionTriggerAltsMeters"])
                  + mlib.MAX_ARRIVAL_EXTRA_ROUNDS)
        self.assertEqual(3, rounds)
        self.assertLessEqual(rounds * self.mp["maxCorrectionDvMps"], margin)
        self.assertLessEqual(rounds * self.mp["maxCorrectionDvMps"],
                             0.30 * margin,
                             "the worst correction spend is over 30% of the "
                             "delta-v margin; re-derive before raising the cap")

    def test_the_correction_cap_is_below_the_transfer_it_corrects(self):
        # The B15 rule: a "correction" priced above the departure burn is the
        # wrong plan, and the cap is what makes the machine discard it. The
        # coplanar departure is 486.5 m/s.
        a_t = (PARK_SMA + A_LAYTHE) / 2.0
        depart = (math.sqrt(MU_JOOL / PARK_SMA)
                  - math.sqrt(MU_JOOL * (2.0 / PARK_SMA - 1.0 / a_t)))
        self.assertAlmostEqual(depart, 486.53, delta=0.5)
        self.assertLess(self.mp["maxCorrectionDvMps"], depart / 2.0)

    def test_the_worst_hop_really_is_under_the_available_dv(self):
        # The header's verdict, re-derived rather than quoted: the PESSIMISTIC
        # departure (a separate pure plane change at the park) plus the most
        # expensive capture in the admissible window.
        a_t = (PARK_SMA + A_LAYTHE) / 2.0
        v_park = math.sqrt(MU_JOOL / PARK_SMA)
        depart = v_park - math.sqrt(MU_JOOL * (2.0 / PARK_SMA - 1.0 / a_t))
        plane = 2.0 * v_park * math.sin(math.radians(PARK_INC_DEG) / 2.0)
        v_inf = (math.sqrt(MU_JOOL * (2.0 / A_LAYTHE - 1.0 / a_t))
                 - math.sqrt(MU_JOOL / A_LAYTHE))
        r_p = R_LAYTHE + self.mp["parkMinPeriapsisMeters"]
        capture = (math.sqrt(v_inf ** 2 + 2.0 * MU_LAYTHE / r_p)
                   - math.sqrt(MU_LAYTHE / r_p))
        worst = depart + plane + capture
        self.assertLessEqual(worst, WORST_HOP_MPS + 5.0)
        self.assertLess(worst, 0.5 * DV_AVAILABLE_MPS,
                        "the worst hop is over half the tank; the 200 m/s "
                        "correction cap was sized against a margin that no "
                        "longer exists")

    # --- fixture + posture ---------------------------------------------------

    def test_the_spec_points_at_the_parsek_stripped_fixture(self):
        """B23 FLIGHT 1'S FINDING, PINNED SO IT CANNOT SILENTLY REVERT.

        That flight ran against a `--keep-parsek` harvest and came back PASS
        attempt 1 with every assertion met -- and the subject was wrong anyway,
        because the committed-restore path re-resumed the save's own committed
        recording and the seam StartRecording answered `already=true`. Nothing in
        the harness's expectation vocabulary can express "the recording is rooted
        at Jool", so a revert of this one string would restore the defect with
        EVERY verifier still green."""
        self.assertEqual("fixtures/saves/jool-park-nerv",
                         self.spec["fixture"]["saveTemplate"])

    def test_the_stripped_fixture_really_carries_no_parsek_state(self):
        """The other half, checked against the BYTES rather than the name. FIVE
        node types, the same set `eve-park-kerbalx` needed: the two crew ones
        exist only because this subject is CREWED too, and they point at the
        recordings that were removed."""
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
        # And the WORLD must be untouched: the crewed subject and its park.
        self.assertIn("Valentina Kerman", sfs)
        self.assertIn("SMA = 590325784", sfs)

    def test_the_recordings_window_matches_the_stripped_fixtures_arithmetic(self):
        rec_dir = os.path.join(HARNESS_ROOT,
                               self.spec["fixture"]["saveTemplate"],
                               "Parsek", "Recordings")
        carried = (len([n for n in os.listdir(rec_dir) if n.endswith(".prec")])
                   if os.path.isdir(rec_dir) else 0)
        window = self.spec["expectations"]["recordings"]["count"]
        self.assertEqual(0, carried)
        # carried + this lane's single-piece product, with one slot of headroom
        # for a load-time optimizer split at the Jool->Laythe boundary.
        self.assertEqual({"min": carried + 1, "max": carried + 2}, window)

    def test_every_crossed_moon_shell_is_a_named_poison_token(self):
        """THE B17 NAMED-POISON PATTERN, and the list is derived rather than
        chosen by intuition: the descent from the park to Laythe passes through
        EVERY intervening orbital shell, so every one of those moons gets a
        forbidden token in BOTH directions. A subset would be a list picked by
        which encounter felt likeliest."""
        forbidden = self.spec["expectations"]["logContracts"]["forbidden"]
        crossed = [name for name, a in (("Vall", A_VALL), ("Tylo", A_TYLO),
                                        ("Bop", A_BOP), ("Pol", A_POL))
                   if A_LAYTHE < a < PARK_SMA]
        self.assertEqual(["Vall", "Tylo", "Bop", "Pol"], crossed)
        for moon in crossed:
            self.assertIn(
                "SOI change boundary suppressed in tree mode: \\w+ to %s" % moon,
                forbidden)
            self.assertIn(
                "SOI change boundary suppressed in tree mode: %s to \\w+" % moon,
                forbidden)
        # The target must NOT be forbidden - it is the required token.
        self.assertIn("SOI change boundary suppressed in tree mode: Jool to Laythe",
                      self.spec["expectations"]["logContracts"]["required"])

    def test_the_spec_arms_no_gating_block(self):
        # A first-flight READING lane must arm nothing: the ARMED_ALLOWLIST in
        # harness/lib/test_hlib.py owns that decision and this spec is not on it.
        for block in ("rewind", "recordings"):
            sub = (self.spec.get("expectations") or {}).get(block) or {}
            self.assertNotIn("gating", sub)
            for nested in sub.values():
                if isinstance(nested, dict):
                    self.assertNotIn("gating", nested)


class V16CalibrationSeedTests(unittest.TestCase):
    """The V16 pair ships with CALIBRATION SEEDS rather than measurements, so what
    CAN be checked is their internal consistency - the relationships the headers
    assert between the seeds. A seed that is merely wrong is expected and is
    fixed at calibration; a seed set that is INCONSISTENT WITH ITS OWN DERIVATION
    would survive the calibration pass unnoticed, because the pass substitutes
    numbers rather than re-deriving the shape."""

    # The PLACEHOLDER tree id both specs ship with. It must be a placeholder
    # rather than a plausible-looking id, so a forgotten substitution fails
    # loudly in KSP rather than arming some other tree.
    PLACEHOLDER_TREE = "0" * 32

    # The seeds header section 3 derives everything from.
    UT0 = 27_787_319.0
    SPAN = 1_044_305.0
    DEST_PHASE = 2_400.0
    PARK_FRACTION = 0.707

    @classmethod
    def setUpClass(cls):
        cls.m = _spec("V16M-laythe-player-loop.toml")
        cls.t = _spec("V16T-laythe-ts-arrival.toml")

    def _jump_uts(self, spec):
        return [float(s["args"]["ut"]) for s in spec["driver"]["steps"]
                if s.get("cmd") == "TimeJump"]

    def _period(self):
        return 2.0 * math.pi * math.sqrt(A_LAYTHE ** 3 / MU_JOOL)

    def test_both_specs_carry_the_placeholder_tree_id(self):
        # Pre-calibration state, pinned in BOTH directions: it reds if a real id
        # is substituted without the rest of the calibration pass landing, and it
        # documents which string the pass is looking for.
        for spec in (self.m, self.t):
            trees = [s["args"]["tree"] for s in spec["driver"]["steps"]
                     if s.get("cmd") in ("MissionConfig", "StartLoopPlayback")]
            self.assertTrue(trees)
            for tree in trees:
                self.assertEqual(self.PLACEHOLDER_TREE, tree)

    def test_the_cadence_multiple_is_greater_than_one_across_the_whole_span_band(self):
        """THE LANE'S SHARPEST PRE-REGISTERED CLAIM, and the ONLY part of the
        cadence prediction that is robust.

        `QuantizeCadenceToMultipleOfP` takes the smallest `k*P` at or above the
        raw cadence, and the raw cadence is floored at the recording's SPAN.
        B25's span is dominated by a transfer coast that is 19.14 Laythe periods
        on its own, so k >= 20 whatever the transfer window wait turns out to be
        - which is what makes this the suite's first cadence that is not one moon
        period. The EXACT value is not robust (20 vs 21 across the band) and is
        seeded, not predicted."""
        p = self._period()
        a_t = (PARK_SMA + A_LAYTHE) / 2.0
        tof = math.pi * math.sqrt(a_t ** 3 / MU_JOOL)
        self.assertGreater(tof / p, 19.0)
        for span in (1_017_505.0, self.SPAN, 1_071_015.0):
            self.assertGreaterEqual(math.ceil(span / p - 1e-9), 20)

    def test_the_jump_table_is_derived_from_the_seeded_inputs(self):
        """THE DERIVATION, re-run. Every jump UT is the anchor plus a fixed
        offset, and the anchor is `NextWindow(UT0, P, referenceUT)`. When the
        fixture is harvested this reds with the arithmetic in hand."""
        p = self._period()
        reference = self.UT0 + self.SPAN + 3.0
        k = math.ceil((reference - self.UT0) / p - 1e-9)
        self.assertEqual(20, k)
        anchor = self.UT0 + k * p
        seam_off = self.SPAN - self.DEST_PHASE
        park_off = seam_off + self.PARK_FRACTION * self.DEST_PHASE
        uts = self._jump_uts(self.m)
        for got, want in zip(uts[:3], (seam_off - 180.0, seam_off - 60.0,
                                       seam_off + 140.0)):
            self.assertAlmostEqual(got, round(anchor + want), delta=0.5)
        self.assertAlmostEqual(uts[3], round(anchor + park_off), delta=0.5)

    def test_v16m_jumps_are_strictly_forward(self):
        # The single most load-bearing property of a seeded bracket: a backward
        # jump answers REJECTED and is only a WARN, so a non-monotonic seed set
        # would green while dwelling nowhere. (Both specs FORBID the token too;
        # this cell catches the error before a KSP boot is spent.)
        uts = self._jump_uts(self.m)
        self.assertEqual(8, len(uts))
        self.assertEqual(sorted(uts), uts)
        self.assertEqual(len(set(uts)), len(uts))

    def test_the_v16m_cycle_two_bracket_is_cycle_one_plus_the_CADENCE(self):
        """THE k > 1 CLAIM AS ARITHMETIC, and the cell that would catch the
        commonest possible mistake on this lane: seeding the cycle-2 jumps one
        PERIOD later (every prior loop lane's shape) instead of one CADENCE."""
        p = self._period()
        cadence = 20.0 * p
        uts = self._jump_uts(self.m)
        for cycle1, cycle2 in zip(uts[:4], uts[4:]):
            self.assertAlmostEqual(cycle2 - cycle1, cadence, delta=1.5,
                                   msg="the cycle-2 seeds are not one CADENCE "
                                       "past the cycle-1 seeds")
            self.assertGreater(cycle2 - cycle1, 10.0 * p,
                               "the cycle-2 seeds look like a k = 1 cadence")

    def test_the_v16m_bracket_offsets_are_v14ms_minus180_minus60_plus140(self):
        uts = self._jump_uts(self.m)
        seam = uts[0] + 180.0
        self.assertEqual([-180.0, -60.0, 140.0], [u - seam for u in uts[:3]])
        self.assertEqual([-180.0, -60.0, 140.0], [u - (uts[4] + 180.0)
                                                  for u in uts[4:7]])

    def test_v16t_reuses_v16ms_third_cycle_one_bracket_jump(self):
        # The pair's whole point is that the two lanes observe the SAME instant
        # from the two scenes; if the seeds drift apart that stops being true and
        # nothing else notices.
        t_jumps = self._jump_uts(self.t)
        self.assertEqual(1, len(t_jumps))
        self.assertEqual(self._jump_uts(self.m)[2], t_jumps[0])

    def test_both_specs_point_at_the_same_pending_fixture(self):
        self.assertEqual(self.m["fixture"]["saveTemplate"],
                         self.t["fixture"]["saveTemplate"])
        self.assertEqual("fixtures/saves/laythe-orbit-recorded",
                         self.m["fixture"]["saveTemplate"])
        # ... and it must NOT exist yet: this pair is committed ahead of it, and
        # the `PENDING_FIXTURE_LANES` exemption in harness/lib/test_hlib.py is
        # what reds when it appears. If this assertion fails, the calibration
        # pass is owed (and that cell will say so too).
        self.assertFalse(os.path.isdir(os.path.join(
            HARNESS_ROOT, "fixtures", "saves", "laythe-orbit-recorded")))

    def test_the_census_pacing_block_is_sized_against_the_poll_floor(self):
        """THE CENSUS-PACING UPGRADE, pinned as arithmetic rather than as a
        count. Research section 9.3's RECOURSE 1 needs > 5 wall-seconds between
        the two arrival brackets, and the cheapest honest unit is `run.py`'s
        POLL_INTERVAL_SECONDS floor, not V5's measured 0.54 s/tick - sizing
        against the measured rate would clear the window only if the run behaves
        as well as V5's did."""
        steps = self.m["driver"]["steps"]
        ticks = sum(1 for s in steps if s.get("cmd") == "RecordingState")
        poll_floor = 0.25          # run.py POLL_INTERVAL_SECONDS
        window = 5.0               # ParsekLog.VerboseRateLimited(..., 5.0)
        self.assertGreaterEqual(ticks * poll_floor, 2.0 * window,
                                "the dwell block does not clear the rate-limiter "
                                "window at the poll floor, only at the measured "
                                "per-tick cost")

    def test_the_dwell_block_sits_between_the_two_cycles(self):
        """PLACEMENT IS THE WHOLE POINT: the ticks must fall between the cycle-1
        census emission (the third cycle-1 bracket jump) and the cycle-2 one, or
        they buy no separation at all."""
        cmds = [s.get("cmd") for s in self.m["driver"]["steps"]]
        first_tick = cmds.index("RecordingState")
        last_tick = len(cmds) - 1 - cmds[::-1].index("RecordingState")
        jumps = [i for i, c in enumerate(cmds) if c == "TimeJump"]
        # 4 cycle-1 jumps, then the block, then the 4 cycle-2 jumps.
        self.assertGreater(first_tick, jumps[3])
        self.assertLess(last_tick, jumps[4])
        # ... and the cycle-2 re-arm must come AFTER the block (the verb that
        # resolves NextLaunchAfter(now) is what starts cycle 2).
        relaunches = [i for i, c in enumerate(cmds) if c == "StartLoopPlayback"]
        self.assertEqual(2, len(relaunches))
        self.assertLess(last_tick, relaunches[1])

    def test_only_v16m_carries_the_dwell_block(self):
        """The asymmetry is arithmetic, not style. V16T is a SINGLE-JUMP shape
        with ONE arrival, so it has exactly one census emission to make and no
        second one to rescue; its two `RecordingState` steps are the V6T/V14T/V15T
        TS dwell pair, which is a different instrument with a different job."""
        t_ticks = sum(1 for s in self.t["driver"]["steps"]
                      if s.get("cmd") == "RecordingState")
        self.assertEqual(2, t_ticks)

    def test_the_watch_verdict_is_pinned_rejected_on_park_independent_geometry(self):
        """V15M's watch call depended on the park B24 actually delivered (2a =
        79,345 m, inside the measured bracket). THIS lane's does not: every park
        B25's own window admits is far outside it, which is a stronger claim and
        is worth pinning as such."""
        b25 = _spec("B25-laythe-orbit.toml")["driver"]["missionParams"]
        zone_boundary = 120_000.0          # GhostVisualRangeMeters
        for pe in (b25["parkMinPeriapsisMeters"], b25["parkMaxApoapsisMeters"]):
            self.assertGreater(2.0 * (R_LAYTHE + pe), 9.0 * zone_boundary)
        watch = [s for s in self.m["driver"]["steps"]
                 if s.get("cmd") == "EnterWatchMode"]
        self.assertEqual(2, len(watch))
        for step in watch:
            self.assertEqual("REJECTED", step["expect"])

    def test_the_anomaly_posture_is_the_v15_pair_shape(self):
        """V16M stays the stepped-bracket CONTROL and V16T ships untolerated so
        its pre-registered `icon-off-orbit` red can be MEASURED rather than
        swallowed - the V15 pair's shape, and the reason its finding could be
        trusted. Both halves are pinned, because either one drifting alone
        destroys the claim."""
        self.assertEqual([], self.m["expectations"]["allowedAnomalies"])
        self.assertEqual([], self.t["expectations"]["allowedAnomalies"])

    def test_neither_v16_spec_arms_a_gating_block(self):
        for spec in (self.m, self.t):
            for block in ("rewind", "recordings"):
                sub = (spec.get("expectations") or {}).get(block) or {}
                self.assertNotIn("gating", sub)
                for nested in sub.values():
                    if isinstance(nested, dict):
                        self.assertNotIn("gating", nested)

    def test_the_method_conjunction_is_deliberately_not_required_yet(self):
        """V14M's header records what pre-pinning a PREDICTED method costs (its
        tidal-collapse prediction was refuted). This pair's cadence multiple is
        genuinely new, so the value-free prefix is required and the conjunction
        is left to the arming pass. Pinned so a well-meaning edit does not arm it
        ahead of the measurement."""
        conjunction = ("PhaseLock APPLIED: mission=.*method=single-orbital"
                       ".*zeroDrift=no")
        for spec in (self.m, self.t):
            req = spec["expectations"]["logContracts"]["required"]
            self.assertIn("PhaseLock APPLIED: mission=", req)
            self.assertNotIn(conjunction, req)
            # The classification half IS structural (Laythe is a child of Jool),
            # so it is safe to require on a first run and both lanes do.
            self.assertIn("Orbital\\(Laythe\\) same-parent", req)


if __name__ == "__main__":
    unittest.main()
