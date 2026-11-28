"""Drift and geometry gates for the far-watch pod fixtures (the FW-3 / FW-4 hosts).

Both are DERIVED from `kerbin-splashdown-recorded` by
`harness/tools/build_far_watch_pod_fixtures.py` (read its docstring for why): the B4
capsule lifted into the air (FW-3) or put on a falling arc above it (FW-4), with the save
clock moved next to the looped mission's anchor. These cells keep the derivation
mechanical and re-derive, from the committed bytes and the recording's own points, every
number the two lanes rely on: the clock sits before the anchor and the lanes enable the
loop before it, the FW-3 capsule flies inside the atmosphere far from the ascent ghost,
and the FW-4 capsule reaches h_safe late enough to be admitted with a return point that
comes inside the lane's warp leg, while the coast ghost stays far from it.

Stdlib only; ASCII only; no em dashes.
"""
import importlib.util
import math
import os
import re
import tomllib
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_far_watch_pod_fixtures.py")
SAVES = os.path.join(HARNESS_ROOT, "fixtures", "saves")
SCENARIOS = os.path.join(HARNESS_ROOT, "scenarios")
FW3 = os.path.join(SCENARIOS, "FW-3-far-watch-refused-in-air.toml")
FW4 = os.path.join(SCENARIOS, "FW-4-far-watch-forced-return.toml")
PARENT_RECORDING = os.path.join(SAVES, "kerbin-splashdown-recorded", "Parsek", "Recordings",
                                "28b6e543d67c4d1c9e4763b451c01df5.prec.txt")
TREE_ID = "c05c834cd2754892b4588e7ce9220c3f"
# W1 section 2A: loopUT = UT - LOOP_OFFSET; the chain members' windows in loopUT.
LOOP_OFFSET = 21549.425183089826
ASCENT_WINDOW = (34.54, 212.20)
COAST_WINDOW = (212.20, 853.72)
# FarWatchShipSafety: refused within 600 s of danger, return = danger - 300 s, 250 km hand-back.
REFUSE_DANGER_WITHIN = 600.0
DANGER_LEAD = 300.0
HAND_BACK_METERS = 250000.0
NEAR_WATCH_METERS = 300000.0
ATMOSPHERE_TOP = 70000.0


def _load_builder():
    spec = importlib.util.spec_from_file_location("build_far_watch_pod_fixtures", BUILDER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _text(leaf, name="persistent.sfs"):
    with open(os.path.join(SAVES, leaf, name), encoding="utf-8") as fh:
        return fh.read()


def _vessel(text, builder):
    start, end = builder._vessel_span(text, builder.VESSEL_NAME)
    return text[start:end]


def _orbit(vessel_text):
    block = re.search(r"\n\t\t\tORBIT\n\t\t\t\{\n((?:\t\t\t\t[^\n]*\n)*)\t\t\t\}\n",
                      vessel_text).group(1)
    return {k: float(v) for k, v in re.findall(r"\t\t\t\t(\w+) = ([^\n]*)\n", block)}


def _spec(path):
    with open(path, "rb") as fh:
        return tomllib.load(fh)


def _steps(spec):
    return spec["driver"]["steps"]


def _index(steps, pred):
    for i, s in enumerate(steps):
        if pred(s):
            return i
    raise AssertionError("step not found")


def _ghost_points():
    with open(PARENT_RECORDING, encoding="utf-8") as fh:
        text = fh.read()
    pts = [tuple(float(x) for x in m.groups()) for m in re.finditer(
        r"\tPOINT\n\t\{\n\t\tut = ([^\n]*)\n\t\tlat = ([^\n]*)\n\t\tlon = ([^\n]*)\n\t\talt = ([^\n]*)\n",
        text)]
    return sorted(pts)


def _ghost_body_fixed(points, loop_ut):
    """lat / lon / alt at loopUT, linear between the recorded points (the on-rails gaps are
    bridged straight, which is close enough for separations of hundreds of km)."""
    for a, b in zip(points, points[1:]):
        if a[0] <= loop_ut <= b[0]:
            f = 0.0 if b[0] == a[0] else (loop_ut - a[0]) / (b[0] - a[0])
            return tuple(a[k] + f * (b[k] - a[k]) for k in (1, 2, 3))
    raise AssertionError("loopUT %r outside the recording" % loop_ut)


def _cartesian(lat_deg, lon_deg, radius):
    la, lo = math.radians(lat_deg), math.radians(lon_deg)
    return (radius * math.cos(la) * math.cos(lo), radius * math.cos(la) * math.sin(lo),
            radius * math.sin(la))


def _pod_inertial(builder, orbit, ut):
    """(position, altitude) of the capsule's conic at `ut`, in the inertial frame where the
    longitude is LAN + LPE + true anomaly."""
    sma, ecc = orbit["SMA"], orbit["ECC"]
    m_anom = orbit["MNA"] + builder.mean_motion(sma) * (ut - orbit["EPH"])
    nu = builder.true_anomaly(m_anom % (2.0 * math.pi), ecc)
    radius = sma * (1.0 - ecc * ecc) / (1.0 + ecc * math.cos(nu))
    u = math.radians(orbit["LPE"]) + nu
    lat = math.degrees(math.asin(math.sin(math.radians(orbit["INC"])) * math.sin(u)))
    lon = orbit["LAN"] + orbit["LPE"] + math.degrees(nu)
    return _cartesian(lat, lon, radius), radius - builder.KERBIN_RADIUS


class FarWatchPodFixtureDriftTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()

    def test_the_committed_fixtures_are_byte_identical_to_a_fresh_derivation(self):
        self.assertEqual([], self.builder.check())

    def test_every_file_but_the_save_and_its_loadmeta_is_the_sources(self):
        for leaf in self.builder.TARGETS:
            files = self.builder.build_files(leaf)
            for rel, data in files.items():
                if rel in (self.builder.SFS, self.builder.LOADMETA):
                    continue
                with open(os.path.join(self.builder.SOURCE_DIR, rel), "rb") as fh:
                    self.assertEqual(fh.read(), data, "%s/%s" % (leaf, rel))

    def test_the_derivation_changes_only_the_clock_and_the_capsule(self):
        source = _text(self.builder.SOURCE).split("\n")
        expected = {
            self.builder.IN_AIR: ["ECC", "EPH", "LAN", "SMA", "UT", "alt", "hgt", "landed",
                                  "lastUT", "sit"],
            self.builder.SUBORBITAL: ["ECC", "EPH", "LPE", "MNA", "SMA", "UT", "alt", "hgt",
                                      "landed", "lastUT", "lat", "lon", "sit"],
        }
        for leaf, keys in expected.items():
            derived = _text(leaf).split("\n")
            self.assertEqual(len(source), len(derived), leaf)
            changed = sorted({b.strip().split(" =")[0] for a, b in zip(source, derived) if a != b})
            self.assertEqual(keys, changed, leaf)
            meta_src = _text(self.builder.SOURCE, "persistent.loadmeta").split("\n")
            meta = _text(leaf, "persistent.loadmeta").split("\n")
            self.assertEqual(["UT"], sorted({b.split(" =")[0] for a, b in zip(meta_src, meta) if a != b}))

    def test_the_frame_reads_stock_kerbins_initial_rotation(self):
        source = _text(self.builder.SOURCE)
        self.assertAlmostEqual(90.0, self.builder.derived_initial_rotation(source), places=5)


class FarWatchPodFixtureClockTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()

    def test_the_clock_sits_between_the_source_clock_and_the_loop_anchor(self):
        for leaf in self.builder.TARGETS:
            text = _text(leaf)
            ut = self.builder.source_save_ut(text)
            self.assertEqual(self.builder.SAVE_UT, ut)
            self.assertLess(self.builder.source_save_ut(_text(self.builder.SOURCE)), ut)
            self.assertLess(ut, self.builder.LOOP_ANCHOR_UT)
            self.assertEqual(ut, _orbit(_vessel(text, self.builder))["EPH"])

    def test_both_lanes_enable_the_loop_before_any_clock_moving_step(self):
        # MissionStore stamps the loop anchor with the enable UT: a step that moves the clock
        # past 21583.965 before MissionConfig would push the anchor a Kerbin day out.
        for path, leaf in ((FW3, self.builder.IN_AIR), (FW4, self.builder.SUBORBITAL)):
            spec = _spec(path)
            self.assertEqual("fixtures/saves/" + leaf, spec["fixture"]["saveTemplate"])
            steps = _steps(spec)
            cfg = _index(steps, lambda s: s["cmd"] == "MissionConfig")
            self.assertEqual({"tree": TREE_ID, "loop": "true"}, steps[cfg]["args"])
            first_clock = _index(steps, lambda s: s["cmd"] in ("WarpToUT", "TimeJump",
                                                                "StartLoopPlayback"))
            self.assertLess(cfg, first_clock, path)
            self.assertNotIn("StartLoopPlayback", [s["cmd"] for s in steps], path)
            self.assertIn("phaseAnchor=43133", spec["expectations"]["logContracts"]["forbidden"])


class FarWatchPodInAirTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.text = _text(cls.builder.IN_AIR)
        cls.vessel = _vessel(cls.text, cls.builder)
        cls.orbit = _orbit(cls.vessel)
        cls.steps = _steps(_spec(FW3))

    def _probe_ut(self):
        i = _index(self.steps, lambda s: s["cmd"] == "FarWatch" and s["args"].get("op") == "start")
        warps = [s for s in self.steps[:i] if s["cmd"] == "WarpToUT"]
        self.assertEqual(1, len(warps))
        self.assertEqual("1", warps[0]["args"]["maxRate"], "no warp in the air")
        return float(warps[0]["args"]["ut"]), self.steps[i]

    def test_the_capsule_hangs_at_rest_over_its_landing_spot_inside_the_atmosphere(self):
        b = self.builder
        self.assertIn("\n\t\t\tsit = FLYING\n", self.vessel)
        ra = self.orbit["SMA"] * (1.0 + self.orbit["ECC"])
        self.assertAlmostEqual(b.IN_AIR_ALTITUDE, ra - b.KERBIN_RADIUS, delta=1.0)
        self.assertLess(b.IN_AIR_ALTITUDE, ATMOSPHERE_TOP)
        self.assertAlmostEqual(math.pi, self.orbit["MNA"], places=6)
        v_apex = math.sqrt(b.KERBIN_GM * (2.0 / ra - 1.0 / self.orbit["SMA"]))
        self.assertAlmostEqual(b.corotation_speed(ra), v_apex, delta=0.01)
        # The apoapsis is over the source capsule's body-fixed spot at the new clock.
        src = _vessel(_text(b.SOURCE), b)
        src_lon = float(re.search(r"\n\t\t\tlon = ([^\n]*)\n", src).group(1))
        inertial = self.orbit["LAN"] + self.orbit["LPE"] + 180.0
        lon = (inertial - b.rotation_angle(b.SAVE_UT)) % 360.0
        self.assertAlmostEqual(src_lon, lon, places=6)

    def test_the_capsule_is_still_well_inside_the_air_and_above_the_ground_at_the_probe(self):
        probe_ut, _ = self._probe_ut()
        drop = 0.5 * 9.81 * (probe_ut - self.builder.SAVE_UT) ** 2
        self.assertGreater(self.builder.IN_AIR_ALTITUDE - drop, 15000.0)

    def test_the_probe_addresses_the_live_ascent_member_far_from_the_capsule(self):
        probe_ut, start = self._probe_ut()
        self.assertEqual({"op": "start", "index": "0", "mode": "follow"}, start["args"])
        self.assertEqual("REJECTED", start["expect"])
        loop_ut = probe_ut - LOOP_OFFSET
        self.assertTrue(ASCENT_WINDOW[0] < loop_ut < ASCENT_WINDOW[1], loop_ut)
        # Both in the body-fixed frame: the ghost replays its own lat / lon each Kerbin day.
        lat, lon, alt = _ghost_body_fixed(_ghost_points(), loop_ut)
        ghost = _cartesian(lat, lon, self.builder.KERBIN_RADIUS + alt)
        cap_lat = float(re.search(r"\n\t\t\tlat = ([^\n]*)\n", self.vessel).group(1))
        cap_lon = float(re.search(r"\n\t\t\tlon = ([^\n]*)\n", self.vessel).group(1))
        cap = _cartesian(cap_lat, cap_lon, self.builder.KERBIN_RADIUS + 15000.0)
        self.assertGreater(math.dist(ghost, cap), 3.0 * NEAR_WATCH_METERS)


class FarWatchPodSuborbitalTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.text = _text(cls.builder.SUBORBITAL)
        cls.vessel = _vessel(cls.text, cls.builder)
        cls.orbit = _orbit(cls.vessel)
        cls.steps = _steps(_spec(FW4))
        start = _index(cls.steps, lambda s: s["cmd"] == "FarWatch" and s["args"].get("op") == "start")
        cls.start_args = cls.steps[start]["args"]
        cls.warps = [float(s["args"]["ut"]) for s in cls.steps if s["cmd"] == "WarpToUT"]
        before = [s for s in cls.steps[:start] if s["cmd"] == "WarpToUT"]
        cls.start_ut = float(before[-1]["args"]["ut"])
        cls.end_ut = cls.warps[-1]

    def _crossing_ut(self):
        """First descending crossing of h_safe after the save, walked on the committed conic."""
        b = self.builder
        ut, step = b.SAVE_UT, 0.25
        _, alt = _pod_inertial(b, self.orbit, ut)
        self.assertGreater(alt, b.KERBIN_SAFE_ALTITUDE, "above h_safe at the save")
        while alt >= b.KERBIN_SAFE_ALTITUDE:
            ut += step
            _, alt = _pod_inertial(b, self.orbit, ut)
            self.assertLess(ut, b.SAVE_UT + 3000.0)
        return ut

    def test_the_arc_has_its_low_point_near_60_km_and_its_high_point_above_the_air(self):
        b = self.builder
        self.assertIn("\n\t\t\tsit = SUB_ORBITAL\n", self.vessel)
        sma, ecc = self.orbit["SMA"], self.orbit["ECC"]
        self.assertAlmostEqual(60000.0, sma * (1.0 - ecc) - b.KERBIN_RADIUS, delta=1.0)
        self.assertAlmostEqual(b.SUBORBITAL_APOAPSIS_ALTITUDE, sma * (1.0 + ecc) - b.KERBIN_RADIUS,
                               delta=1.0)
        alt = float(re.search(r"\n\t\t\talt = ([^\n]*)\n", self.vessel).group(1))
        _, conic_alt = _pod_inertial(b, self.orbit, b.SAVE_UT)
        self.assertAlmostEqual(conic_alt, alt, delta=1.0)

    def test_the_crossing_admits_the_start_with_a_return_inside_the_warp_leg(self):
        crossing = self._crossing_ut()
        self.assertAlmostEqual(self.builder.CROSSING_UT, crossing, delta=0.5)
        self.assertEqual({"op": "start", "index": "1", "mode": "follow"}, self.start_args)
        # Over the 600 s refusal with two minutes for seam latency and warp overshoot.
        self.assertGreater(crossing - self.start_ut, REFUSE_DANGER_WITHIN + 100.0)
        return_ut = crossing - DANGER_LEAD
        self.assertLess(self.start_ut + 120.0, return_ut, "return over the 120 s minimum")
        self.assertLess(return_ut, self.end_ut, "the return point comes inside the leg")
        self.assertLess(self.end_ut, crossing, "the leg ends with the capsule above the air")
        for a, b in zip([self.builder.SAVE_UT] + self.warps, self.warps):
            self.assertLessEqual(b - a, 540.0, "each leg inside the WarpToUT cap")

    def test_the_watched_coast_member_is_live_across_the_watch(self):
        b = self.builder
        return_ut = b.CROSSING_UT - DANGER_LEAD
        for ut in (self.start_ut, return_ut, self.end_ut):
            loop_ut = ut - LOOP_OFFSET
            self.assertTrue(COAST_WINDOW[0] < loop_ut < COAST_WINDOW[1], (ut, loop_ut))

    def test_the_coast_ghost_stays_far_from_the_capsule_across_the_watch(self):
        b = self.builder
        points = _ghost_points()
        nearest = float("inf")
        ut = self.start_ut
        while ut <= self.end_ut:
            lat, lon, alt = _ghost_body_fixed(points, ut - LOOP_OFFSET)
            # Body-fixed to inertial: add Kerbin's rotation angle at this UT.
            ghost = _cartesian(lat, lon + b.rotation_angle(ut), b.KERBIN_RADIUS + alt)
            cap, _ = _pod_inertial(b, self.orbit, ut)
            nearest = min(nearest, math.dist(ghost, cap))
            ut += 5.0
        self.assertGreater(nearest, 4.0 * HAND_BACK_METERS)
        self.assertGreater(nearest, NEAR_WATCH_METERS)


if __name__ == "__main__":
    unittest.main()
