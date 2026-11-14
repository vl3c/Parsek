"""Unit tests for the moon-to-moon pair forges B43-B46 and their loop lanes V43M-V46M
(moon flight-test program Group B, todo NEXT-TESTS-TO-BUILD-2026-11-11 item 2).

The forges reuse existing machines (`b29_jool_kerbin` for the Jool pairs, which is
the one schema carrying the parent-relay block, the orbit-start block AND the
elliptical capture key; `b30_mun_minmus` for Minmus -> Mun). Nothing here is new
mission code, so these cells pin what the lanes add: the cited gravity rows, the
coupling of each spec to its own fixture's bytes (entry gate, escape node, delta-v),
the escape sizing arguments written in the spec headers, and the hand-over contract
of the pending-fixture loop lanes.

Every fixture number is read from the committed save, never typed: a re-cut fixture
that moves the park reds the cell that depends on it before a KSP boot is spent.
"""
from __future__ import annotations

import math
import os
import sys
import tomllib
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)
HARNESS_ROOT = os.path.dirname(_MISSIONS)
for p in (_HERE, _MISSIONS):
    if p not in sys.path:
        sys.path.insert(0, p)

import mlib  # noqa: E402

SCENARIOS = os.path.join(HARNESS_ROOT, "scenarios")
SAVES = os.path.join(HARNESS_ROOT, "fixtures", "saves")
G0 = 9.80665

# Stock KSP 1.12.5 values, each also quoted in a committed lane (cited in mlib's
# STOCK_BODY_GRAVITY comment): body -> (mu, R, SOI).
EXPECTED_GRAVITY = {
    "Vall": (2.074815e11, 300_000.0, 2_406_401.4),
    "Tylo": (2.82489e12, 600_000.0, 10_856_518.4),
    "Bop": (2.4868349e9, 65_000.0, 1_221_060.9),
    "Minmus": (1.7658e9, 60_000.0, 2_247_428.4),
}
MU_PARENT = {"Jool": 2.82528e14, "Kerbin": 3.5316e12}
# body -> (parent, orbital radius m); circular approximations (Bop's mean radius).
ORBIT = {
    "Laythe": ("Jool", 27_184_000.0), "Vall": ("Jool", 43_152_000.0),
    "Tylo": ("Jool", 68_500_000.0), "Bop": ("Jool", 128_500_000.0),
    "Mun": ("Kerbin", 12_000_000.0), "Minmus": ("Kerbin", 47_000_000.0),
}
KERBIN_SOI = 84_159_286.0

# forge spec -> (mission, home, target, parent, fixture leaf, harvest leaf, loop lane,
#                elliptical capture?)
FORGES = {
    "B43-vall-tylo-transfer.toml": ("b29_jool_kerbin", "Vall", "Tylo", "Jool",
                                    "vall-park-nerv", "vall-tylo-recorded",
                                    "V43M-vall-tylo-player-loop.toml", True),
    "B44-laythe-tylo-transfer.toml": ("b29_jool_kerbin", "Laythe", "Tylo", "Jool",
                                      "laythe-park-nerv", "laythe-tylo-recorded",
                                      "V44M-laythe-tylo-player-loop.toml", True),
    "B45-tylo-bop-transfer.toml": ("b29_jool_kerbin", "Tylo", "Bop", "Jool",
                                   "tylo-park-nerv", "tylo-bop-recorded",
                                   "V45M-tylo-bop-player-loop.toml", False),
    "B46-minmus-mun-transfer.toml": ("b30_mun_minmus", "Minmus", "Mun", "Kerbin",
                                     "minmus-park-kerbalx", "minmus-mun-recorded",
                                     "V46M-minmus-mun-player-loop.toml", False),
}
# The three new Parsek-stripped fixtures and the save each was cut from.
STRIPPED = {"vall-park-nerv": "vall-transfer-recorded",
            "tylo-park-nerv": "tylo-orbit-recorded",
            "minmus-park-kerbalx": "minmus-orbit-recorded"}

# Craft: the Duna Rocket (NERV, LF only) and the Kerbal X upper stage (Poodle).
# Dry masses are the pessimistic ends the B26 / B30 tests already use.
CRAFT = {"Jool": (800.0, 7.18, False), "Kerbin": (350.0, 8.5, True)}


def _spec(name):
    with open(os.path.join(SCENARIOS, name), "rb") as fh:
        return tomllib.load(fh)


def _parse_cfg(text):
    root = {"name": "root", "vals": [], "kids": []}
    stack, pending = [root], None
    for raw in text.splitlines():
        s = raw.strip()
        if not s:
            continue
        if s == "{":
            node = {"name": pending, "vals": [], "kids": []}
            stack[-1]["kids"].append(node)
            stack.append(node)
            pending = None
        elif s == "}":
            stack.pop()
        elif "=" in s:
            k, v = s.split("=", 1)
            stack[-1]["vals"].append((k.strip(), v.strip()))
        else:
            pending = s
    return root


def _get(node, key):
    for k, v in node["vals"]:
        if k == key:
            return v
    return None


def _kids(node, name):
    return [c for c in node["kids"] if c["name"] == name]


_SAVE_CACHE = {}


def _game(leaf):
    if leaf not in _SAVE_CACHE:
        with open(os.path.join(SAVES, leaf, "persistent.sfs"), encoding="utf-8",
                  errors="replace") as fh:
            _SAVE_CACHE[leaf] = _kids(_parse_cfg(fh.read()), "GAME")[0]
    return _SAVE_CACHE[leaf]


def _active_park(leaf):
    """(sma, ecc, liquid fuel, oxidizer, ut) of the fixture's active vessel."""
    fs = _kids(_game(leaf), "FLIGHTSTATE")[0]
    vessel = _kids(fs, "VESSEL")[int(_get(fs, "activeVessel"))]
    orbit = _kids(vessel, "ORBIT")[0]
    res = {}
    for part in _kids(vessel, "PART"):
        for r in _kids(part, "RESOURCE"):
            res[_get(r, "name")] = res.get(_get(r, "name"), 0.0) + float(_get(r, "amount"))
    return (float(_get(orbit, "SMA")), float(_get(orbit, "ECC")),
            res.get("LiquidFuel", 0.0), res.get("Oxidizer", 0.0), float(_get(fs, "UT")))


def _park_snapshot(forge, **overrides):
    _m, home, _t, _p, fixture, _h, _l, _e = FORGES[forge]
    sma, ecc, _lf, _ox, ut = _active_park(fixture)
    radius = EXPECTED_GRAVITY.get(home, mlib.STOCK_BODY_GRAVITY.get(home, (0, 0, 0)))[1]
    base = dict(ut=ut, body=home, situation="ORBITING",
                apoapsis=sma * (1.0 + ecc) - radius, periapsis=sma * (1.0 - ecc) - radius,
                eccentricity=ecc, altitude=sma - radius, time_to_periapsis=600.0)
    base.update(overrides)
    return mlib.TelemetrySnapshot(**base)


def _hohmann(home, target):
    """(departure excess, arrival excess, time of flight) between two circular
    sibling orbits about their shared parent."""
    parent, r1 = ORBIT[home]
    _, r2 = ORBIT[target]
    mu = MU_PARENT[parent]
    a = (r1 + r2) / 2.0
    vd = abs(math.sqrt(mu * (2.0 / r1 - 1.0 / a)) - math.sqrt(mu / r1))
    va = abs(math.sqrt(mu / r2) - math.sqrt(mu * (2.0 / r2 - 1.0 / a)))
    return vd, va, math.pi * math.sqrt(a ** 3 / mu)


def _soi_floor(home, periapsis_alt, apoapsis_alt):
    """The smallest SOI-crossing speed a prograde periapsis burn can deliver
    (escape_node_plan's reachability floor, v_floor * r / r_soi)."""
    mu, radius, soi = mlib.STOCK_BODY_GRAVITY[home]
    r = radius + periapsis_alt
    v_floor = math.sqrt(2.0 * mu * soi / (r * (soi + r)))
    return v_floor * r / soi


def _capture_dv(target, v_inf, periapsis_alt, elliptical_apoapsis_alt=None):
    mu, radius, _soi = mlib.STOCK_BODY_GRAVITY[target]
    r = radius + periapsis_alt
    v_pe = math.sqrt(v_inf ** 2 + 2.0 * mu / r)
    if elliptical_apoapsis_alt is None:
        return v_pe - math.sqrt(mu / r)
    ra = radius + elliptical_apoapsis_alt
    return v_pe - math.sqrt(mu * (2.0 / r - 2.0 / (r + ra)))


def _dv_available(forge):
    _m, _h, _t, parent, fixture, _hv, _l, _e = FORGES[forge]
    _sma, _ecc, lf, ox, _ut = _active_park(fixture)
    isp, dry, bipropellant = CRAFT[parent]
    prop = (lf + ox) * 0.005 if bipropellant else lf * 0.005
    return isp * G0 * math.log((dry + prop) / dry)


class GravityRowTests(unittest.TestCase):

    def test_the_new_rows_carry_the_cited_values(self):
        for body, triple in EXPECTED_GRAVITY.items():
            with self.subTest(body=body):
                self.assertEqual(triple, mlib.STOCK_BODY_GRAVITY[body])

    def test_the_rows_agree_with_the_committed_moon_tables(self):
        import test_b30_mun_minmus as b30
        import test_jool_moon_orbits as jmo
        self.assertEqual((b30.MU_MINMUS, b30.R_MINMUS, b30.SOI_MINMUS),
                         mlib.STOCK_BODY_GRAVITY["Minmus"])
        for moon in ("Tylo", "Bop"):
            _a, _e, radius, mu, soi, _h = jmo.MOONS[moon]
            self.assertEqual((mu, radius, soi), mlib.STOCK_BODY_GRAVITY[moon])

    def test_every_soi_sits_outside_its_body(self):
        for body in EXPECTED_GRAVITY:
            mu, radius, soi = mlib.STOCK_BODY_GRAVITY[body]
            self.assertGreater(mu, 0.0)
            self.assertGreater(soi, 3.0 * radius)


class ForgeShapeTests(unittest.TestCase):

    def test_every_forge_loads_and_names_its_pair(self):
        for name, (mission, home, target, parent, fixture, _h, _l, ell) in FORGES.items():
            with self.subTest(spec=name):
                spec = _spec(name)
                self.assertEqual(mission, spec["driver"]["mission"])
                self.assertEqual("fixtures/saves/" + fixture, spec["fixture"]["saveTemplate"])
                p = mlib.b5_params_from_dict(spec["driver"]["missionParams"])
                self.assertTrue(p.start_in_orbit)
                self.assertTrue(p.parent_relay_transfer)
                self.assertEqual((home, target, parent), (p.home_body, p.target_body,
                                                          p.return_body))
                self.assertEqual((parent,), tuple(p.via_bodies))
                self.assertTrue(p.capture_enabled)
                self.assertEqual(ell, p.capture_elliptical_apoapsis > 0.0)
                # transferNodeWaitWarp is opt-in after a check flight (owner ruling
                # 2026-11-06); none of these has flown.
                self.assertFalse(p.transfer_node_wait_warp)

    def test_every_forge_requires_both_soi_boundaries_and_the_target_commit(self):
        for name, (_m, home, target, parent, _f, _h, _l, _e) in FORGES.items():
            with self.subTest(spec=name):
                req = _spec(name)["expectations"]["logContracts"]["required"]
                self.assertIn("SOI change boundary suppressed in tree mode: %s to %s"
                              % (home, parent), req)
                self.assertIn("SOI change boundary suppressed in tree mode: %s to %s"
                              % (parent, target), req)
                self.assertIn("CommitTreeFlight terminal: rec=\\w+ terminalState=Orbiting "
                              "terminalOrbitBody=%s" % target, req)

    def test_every_forge_forbids_leaving_the_planet_system(self):
        for name in FORGES:
            with self.subTest(spec=name):
                forb = _spec(name)["expectations"]["logContracts"]["forbidden"]
                self.assertIn("SOI change boundary suppressed in tree mode: \\w+ to Sun", forb)

    def test_b44_forbids_the_vall_encounter_its_outward_transfer_crosses(self):
        _p, r_laythe = ORBIT["Laythe"]
        _p, r_vall = ORBIT["Vall"]
        _p, r_tylo = ORBIT["Tylo"]
        self.assertTrue(r_laythe < r_vall < r_tylo)
        forb = _spec("B44-laythe-tylo-transfer.toml")["expectations"]["logContracts"]["forbidden"]
        self.assertIn("SOI change boundary suppressed in tree mode: \\w+ to Vall", forb)

    def test_the_stage_two_floor_sits_between_the_home_and_target_orbits(self):
        """Outward lanes: the parent-altitude apoapsis floor is real evidence (above
        the home orbit, below the target's orbit, which a Hohmann apoapsis reaches;
        B26's own 36 Mm sits the same way, 97% of Vall's orbit). Inward B46: it lies
        below the home orbit, the B29 weak-but-structural shape."""
        for name, (_m, home, target, parent, _f, _h, _l, _e) in FORGES.items():
            with self.subTest(spec=name):
                p = mlib.b5_params_from_dict(_spec(name)["driver"]["missionParams"])
                parent_r = {"Jool": 6_000_000.0, "Kerbin": 600_000.0}[parent]
                home_alt = ORBIT[home][1] - parent_r
                target_r = ORBIT[target][1]
                if target == "Bop":
                    target_r *= (1.0 - 0.235)        # Bop's periapsis radius
                target_alt = target_r - parent_r
                if target_r > ORBIT[home][1]:
                    self.assertGreater(p.transfer_min_apoapsis, home_alt, name)
                    self.assertLess(p.transfer_min_apoapsis, 0.95 * target_alt, name)
                else:
                    self.assertLess(p.transfer_min_apoapsis, home_alt, name)


class FixtureCouplingTests(unittest.TestCase):

    def test_every_entry_gate_admits_its_fixtures_measured_park(self):
        for name in FORGES:
            with self.subTest(spec=name):
                p = mlib.b5_params_from_dict(_spec(name)["driver"]["missionParams"])
                verdict = mlib.start_in_orbit_frame_verdict(p, _park_snapshot(name))
                self.assertEqual(mlib.START_IN_ORBIT_IN_GATE, verdict[0], verdict)

    def test_the_escape_node_at_the_fixture_park_is_computable_and_under_its_cap(self):
        for name in FORGES:
            with self.subTest(spec=name):
                p = mlib.b5_params_from_dict(_spec(name)["driver"]["missionParams"])
                plan = mlib.escape_node_plan(p, _park_snapshot(name))
                self.assertEqual("", plan.reason)
                self.assertGreater(plan.dv, 0.0)
                self.assertLess(plan.dv, p.escape_max_dv)

    def test_the_escape_speed_clears_the_soi_floor_at_every_gate_corner(self):
        for name, (_m, home, _t, _p, _f, _h, _l, _e) in FORGES.items():
            with self.subTest(spec=name):
                p = mlib.b5_params_from_dict(_spec(name)["driver"]["missionParams"])
                lo, hi = p.start_in_orbit_min_periapsis, p.start_in_orbit_max_apoapsis
                worst = max(_soi_floor(home, pe, ap) for pe, ap in
                            ((lo, lo), (hi, hi), (lo, hi)))
                self.assertGreater(p.escape_soi_speed, 1.15 * worst)

    def test_the_escape_cap_covers_every_gate_corner(self):
        """A park the gate admits must not produce an escape node the cap refuses."""
        for name, (_m, home, _t, _p, _f, _h, _l, _e) in FORGES.items():
            with self.subTest(spec=name):
                p = mlib.b5_params_from_dict(_spec(name)["driver"]["missionParams"])
                lo, hi = p.start_in_orbit_min_periapsis, p.start_in_orbit_max_apoapsis
                for pe, ap in ((lo, lo), (hi, hi), (lo, hi)):
                    plan = mlib.escape_node_plan(p, _park_snapshot(
                        name, periapsis=pe, apoapsis=ap))
                    self.assertEqual("", plan.reason, (pe, ap))

    def test_b46_escape_cannot_leave_kerbins_soi_even_fully_prograde(self):
        """The header's sizing argument: Minmus's 274 m/s plus the whole SOI speed,
        added prograde, still leaves the Kerbin apoapsis inside Kerbin's SOI."""
        p = mlib.b5_params_from_dict(
            _spec("B46-minmus-mun-transfer.toml")["driver"]["missionParams"])
        mu = MU_PARENT["Kerbin"]
        r = ORBIT["Minmus"][1]
        v = math.sqrt(mu / r) + p.escape_soi_speed
        a = 1.0 / (2.0 / r - v * v / mu)
        self.assertGreater(a, 0.0)
        self.assertLess(2.0 * a - r, KERBIN_SOI)
        # and 10 m/s more would not: the margin is real but thin.
        v2 = v + 10.0
        a2 = 1.0 / (2.0 / r - v2 * v2 / mu)
        self.assertGreater(2.0 * a2 - r, KERBIN_SOI)

    def test_the_elliptical_capture_bands_hold_at_the_floor_periapsis(self):
        """The load check reads the commanded periapsis; the delivered one can sit
        as low as the arrival floor, and the capture ellipse must still pass the
        park eccentricity ceiling there."""
        for name, (_m, _h, target, _p, _f, _hv, _l, ell) in FORGES.items():
            if not ell:
                continue
            with self.subTest(spec=name):
                p = mlib.b5_params_from_dict(_spec(name)["driver"]["missionParams"])
                _mu, radius, soi = mlib.STOCK_BODY_GRAVITY[target]
                ra = radius + p.capture_elliptical_apoapsis
                rp = radius + p.target_periapsis_floor
                self.assertLessEqual((ra - rp) / (ra + rp), p.park_max_eccentricity)
                self.assertLess(radius + p.park_max_apoapsis, soi)

    def test_the_capture_plan_closes_a_hohmann_arrival_into_the_park_band(self):
        for name, (_m, home, target, _p, _f, _hv, _l, ell) in FORGES.items():
            if not ell:
                continue
            with self.subTest(spec=name):
                p = mlib.b5_params_from_dict(_spec(name)["driver"]["missionParams"])
                mu, radius, _soi = mlib.STOCK_BODY_GRAVITY[target]
                _vd, v_inf, _tof = _hohmann(home, target)
                pe = p.course_correct_periapsis
                sma = -mu / (v_inf * v_inf)
                ap = 2.0 * (sma - radius) - pe
                plan = mlib.capture_node_plan(p, mlib.TelemetrySnapshot(
                    ut=1.0e7, body=target, situation="ESCAPING", periapsis=pe,
                    apoapsis=ap, eccentricity=1.0 + (radius + pe) * v_inf ** 2 / mu,
                    altitude=5.0e6, time_to_periapsis=3000.0))
                self.assertEqual("", plan.reason)
                self.assertAlmostEqual(
                    _capture_dv(target, v_inf, pe, p.capture_elliptical_apoapsis),
                    abs(plan.dv), delta=0.5)


class DeltaVLedgerTests(unittest.TestCase):
    """The NOMINAL hop fits the fixture's own propellant. Stage 2 is priced at
    B26's measured 1.20x of the Hohmann departure; corrections at two rounds."""

    def _nominal(self, name):
        _m, home, target, _p, _f, _h, _l, ell = FORGES[name]
        p = mlib.b5_params_from_dict(_spec(name)["driver"]["missionParams"])
        escape = mlib.escape_node_plan(p, _park_snapshot(name)).dv
        vd, va, _tof = _hohmann(home, target)
        stage2 = 1.20 * vd
        capture = _capture_dv(target, va, p.course_correct_periapsis,
                              p.capture_elliptical_apoapsis if ell else None)
        return escape + stage2 + capture + 2.0 * p.max_correction_dv

    def test_every_nominal_hop_fits_with_margin(self):
        for name in FORGES:
            with self.subTest(spec=name):
                self.assertLess(1.3 * self._nominal(name), _dv_available(name))

    def _worst(self, name, elliptical):
        """Escape at the fixture park + stage 2 at 1.8x the Hohmann departure +
        capture at 2x the Hohmann arrival excess + all four correction rounds."""
        _m, home, target, _p, _f, _h, _l, _e = FORGES[name]
        p = mlib.b5_params_from_dict(_spec(name)["driver"]["missionParams"])
        escape = mlib.escape_node_plan(p, _park_snapshot(name)).dv
        vd, va, _tof = _hohmann(home, target)
        capture = _capture_dv(target, 2.0 * va, p.course_correct_periapsis,
                              p.capture_elliptical_apoapsis if elliptical else None)
        return escape + 1.8 * vd + capture + 4.0 * p.max_correction_dv

    def test_a_circular_tylo_capture_breaks_the_worst_case_of_b43_and_b44(self):
        """Why the Jool -> Tylo lanes ride B29's elliptical capture: circular, the
        worst case overdraws the tank; elliptical saves ~500 m/s of it."""
        for name in ("B43-vall-tylo-transfer.toml", "B44-laythe-tylo-transfer.toml"):
            with self.subTest(spec=name):
                self.assertGreater(self._worst(name, False), _dv_available(name))
                self.assertGreater(self._worst(name, False) - self._worst(name, True), 450.0)

    def test_b44_worst_case_fits_and_b43_is_the_tight_one(self):
        """B44's worst case fits its tank; B43's does not (its header says so: an
        out-of-fuel capture there is a mission failure, never PARSEK-FAIL)."""
        self.assertLess(self._worst("B44-laythe-tylo-transfer.toml", True),
                        _dv_available("B44-laythe-tylo-transfer.toml"))
        self.assertGreater(self._worst("B43-vall-tylo-transfer.toml", True),
                           _dv_available("B43-vall-tylo-transfer.toml"))

    def test_the_correction_triggers_fit_inside_the_transfer_leg(self):
        for name, (_m, home, target, _p, _f, _h, _l, _e) in FORGES.items():
            with self.subTest(spec=name):
                p = mlib.b5_params_from_dict(_spec(name)["driver"]["missionParams"])
                _vd, _va, tof = _hohmann(home, target)
                first = max(p.correction_trigger_time_to_soi)
                self.assertLess(first, tof)
                self.assertGreater(p.coast_timeout, tof)


class StrippedFixtureTests(unittest.TestCase):

    def test_each_stripped_fixture_keeps_the_node_and_drops_every_child(self):
        for leaf in STRIPPED:
            with self.subTest(fixture=leaf):
                scen = [s for s in _kids(_game(leaf), "SCENARIO")
                        if _get(s, "name") == "ParsekScenario"]
                self.assertEqual(1, len(scen))
                self.assertEqual([], scen[0]["kids"])
                self.assertFalse(os.path.exists(os.path.join(SAVES, leaf, "Parsek")))
                self.assertEqual("%s (SANDBOX)" % leaf, _get(_game(leaf), "Title"))

    def test_each_stripped_fixture_keeps_its_sources_world(self):
        for leaf, src in STRIPPED.items():
            with self.subTest(fixture=leaf):
                self.assertEqual(_active_park(src), _active_park(leaf))
                for extra in ("persistent.loadmeta",):
                    with open(os.path.join(SAVES, leaf, extra), "rb") as a, \
                            open(os.path.join(SAVES, src, extra), "rb") as b:
                        self.assertEqual(b.read(), a.read())


class PendingLoopLaneTests(unittest.TestCase):

    def test_each_loop_lane_names_its_forges_harvest_and_waits_on_it(self):
        for forge, (_m, home, target, parent, _f, harvest, lane, _e) in FORGES.items():
            with self.subTest(lane=lane):
                spec = _spec(lane)
                self.assertEqual("pending-fixture", spec["tier"])
                self.assertIn("awaiting-fixture", spec["tags"])
                self.assertEqual("fixtures/saves/" + harvest, spec["fixture"]["saveTemplate"])
                self.assertFalse(os.path.exists(os.path.join(SAVES, harvest)),
                                 "the harvest exists: hand the lane over (its header)")
                with open(os.path.join(SCENARIOS, forge), encoding="utf-8") as fh:
                    self.assertIn("--target-name %s " % harvest, fh.read())
                trees = {s["args"]["tree"] for s in spec["driver"]["steps"]
                         if s.get("cmd") in ("MissionConfig", "StartLoopPlayback")}
                self.assertEqual({"0" * 32}, trees)

    def test_each_loop_lane_reads_the_moon_rooted_constraint_set(self):
        for _forge, (_m, home, target, parent, _f, _h, lane, _e) in FORGES.items():
            with self.subTest(lane=lane):
                lc = _spec(lane)["expectations"]["logContracts"]
                req = lc["required"]
                self.assertIn("ExtractConstraints: moon-rooted frame launchMoon=%s "
                              "planet=%s launchMoonPeriod=" % (home, parent), req)
                summary = [r for r in req if r.startswith("ExtractConstraints: tree=")]
                self.assertEqual(1, len(summary))
                self.assertIn("launchBody=%s " % home, summary[0])
                self.assertIn("support=Supported constraints=2", summary[0])
                self.assertIn("Orbital\\(%s\\) same-parent" % target, summary[0])
                self.assertIn("Orbital\\(%s\\) same-parent" % home, summary[0])
                self.assertTrue(any(r.startswith("PhaseLock ") for r in req))
                self.assertIn("UnsupportedCrossParent", lc["forbidden"])

    def test_v45m_gates_on_neither_lock_outcome(self):
        req = _spec("V45M-tylo-bop-player-loop.toml")["expectations"]["logContracts"]
        self.assertTrue(any(r.startswith("PhaseLock (APPLIED|SKIPPED)") for r in req["required"]))
        self.assertFalse(any("PhaseLock SKIPPED" in f for f in req["forbidden"]))


if __name__ == "__main__":
    unittest.main()
