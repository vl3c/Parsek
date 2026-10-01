"""Unit tests for harness/lib/flighteff.py (the flight-efficiency analyzer) and
its thin shell harness/tools/flight_efficiency.py.

Fixtures under testdata/ are TRIMMED REAL excerpts of the 2026-10-01_1231
B11-mun-orbit flight (unused telemetry fields removed, the local result path
redacted): the CAPTURE-BURN node wait + PARK dwell, the MJ-ASCENT coast to the
circularization node, and the KSP.log seam anchors. The pre-ut= telemetry shape
comes from warp_audit's committed 2026-07-22_1210 log.

Runs under ``python -m unittest discover -s lib -q``. ASCII only; stdlib only.
"""

import ast
import contextlib
import io
import json
import os
import re
import shutil
import sys
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)
_TOOLS = os.path.join(_HARNESS, "tools")
for _p in (_HERE, _HARNESS, _TOOLS):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import flighteff as fe  # noqa: E402
import flight_efficiency as tool  # noqa: E402
import warp_audit  # noqa: E402

_TESTDATA = os.path.join(_HERE, "testdata")
_CAPTURE = os.path.join(_TESTDATA, "flighteff_b11_capture_excerpt_mission.stdout.log")
_ASCENT = os.path.join(_TESTDATA, "flighteff_b11_ascent_excerpt_mission.stdout.log")
_KSP = os.path.join(_TESTDATA, "flighteff_b11_ksp_excerpt.log")
_OLD_1210 = os.path.join(_TESTDATA, "2026-07-22_1210_B5-mun-flyby_mission.stdout.log")
_MLIB_PATH = os.path.join(_HARNESS, "missions", "lib", "mlib.py")


def _read(path):
    with open(path, "r", encoding="utf-8") as f:
        return f.read()


def _tel(phase="COAST", ap=100000.0, pe=90000.0, alt=95000.0, vspd=10.0,
         body="Kerbin", nodes=0, node_ut="nan", next_body="?", situation="ORBITING",
         warp="NONEx1.000", ap_err="nan", ut=1000.0, include_ut=True):
    line = ("[Mission][VerboseRateLimited][%s] telemetry ap=%s pe=%s alt=%s vspd=%s "
            "body=%s nodes=%d nodeUt=%s nextBody=%s situation=%s warp=%s apErr=%s"
            % (phase, ap, pe, alt, vspd, body, nodes, node_ut, next_body, situation,
               warp, ap_err))
    if include_ut:
        line += " ut=%.3f" % ut
    return line


def _phase(src, dst, ut):
    return "[Mission][Info][%s] phase %s -> %s ut=%.3f alt=1 ap=1" % (dst, src, dst, ut)


def _sample(**kw):
    log = fe.parse_mission_log([_tel(**kw)])
    return log.samples[0]


class MirrorTableTests(unittest.TestCase):
    """flighteff mirrors two mlib tables (stdlib-only harness code does not
    import the mission library at runtime); pin them byte-for-byte."""

    def _mlib(self):
        sys.path.insert(0, os.path.join(_HARNESS, "missions", "lib"))
        try:
            import mlib
        finally:
            sys.path.pop(0)
        return mlib

    def test_rails_rates_match_mlib(self):
        self.assertEqual(fe.RAILS_WARP_RATES, self._mlib().RAILS_WARP_RATES)

    def test_altitude_limits_match_mlib(self):
        self.assertEqual(fe.STOCK_WARP_ALTITUDE_LIMITS, self._mlib().STOCK_WARP_ALTITUDE_LIMITS)

    def test_legal_rate_agrees_with_mlib_factor(self):
        mlib = self._mlib()
        for body in fe.STOCK_WARP_ALTITUDE_LIMITS:
            for alt in (0.0, 4000.0, 30000.0, 85000.0, 250000.0, 700000.0, 1e8):
                self.assertEqual(fe.max_legal_rails_rate(body, alt),
                                 mlib.RAILS_WARP_RATES[mlib.max_legal_rails_factor(body, alt)],
                                 (body, alt))


class TableTests(unittest.TestCase):
    def test_atmosphere_heights(self):
        self.assertEqual(fe.atmosphere_height("Kerbin"), 70000.0)
        self.assertEqual(fe.atmosphere_height("Jool"), 200000.0)
        self.assertEqual(fe.atmosphere_height("Mun"), 0.0)
        self.assertIsNone(fe.atmosphere_height("Rask"))

    def test_legal_rate_unknown_body_or_nan_is_none(self):
        self.assertIsNone(fe.max_legal_rails_rate("Rask", 1e6))
        self.assertIsNone(fe.max_legal_rails_rate("Kerbin", float("nan")))
        self.assertEqual(fe.max_legal_rails_rate("Kerbin", 80000.0), 50.0)
        self.assertEqual(fe.max_legal_rails_rate("Kerbin", 600000.0), 100000.0)
        self.assertEqual(fe.max_legal_rails_rate("Mun", 262000.0), 100000.0)

    def test_dwell_phase_tokens(self):
        for p in ("PARK", "HOLD-PARK", "PLAYBACK-WAIT", "LOOP-WATCH", "LANDED-SETTLE",
                  "DWELL-HOLD-1X", "POST-DEPLOY", "EVA-WINDOW"):
            self.assertTrue(fe.is_dwell_phase(p), p)
        for p in ("COAST", "WATCHER-LAUNCH", "CAPTURE-BURN", "ORBIT-COMMIT", "SETTLED"):
            self.assertFalse(fe.is_dwell_phase(p), p)


class TelemetryParseTests(unittest.TestCase):
    def test_new_and_old_shapes(self):
        log = fe.parse_mission_log([_tel(ut=10.0), _tel(include_ut=False)])
        self.assertEqual(len(log.samples), 2)
        self.assertEqual(log.samples[0].ut, 10.0)
        self.assertNotEqual(log.samples[1].ut, log.samples[1].ut)  # NaN
        self.assertTrue(log.has_ut)
        self.assertEqual(log.samples[0].mode, "NONE")
        self.assertEqual(log.samples[0].rate, 1.0)

    def test_malformed_lines_are_counted_not_fatal(self):
        lines = [
            "[Mission]garbage",
            "[Mission][VerboseRateLimited][COAST] telemetry ap=1 pe=2",  # no warp=
            "unrelated harness noise",
            _tel(ut=5.0),
        ]
        log = fe.parse_mission_log(lines)
        self.assertEqual(log.malformed, 2)
        self.assertEqual(log.telemetry_lines, 2)
        self.assertEqual(len(log.samples), 1)

    def test_visits_follow_transitions_including_repeats(self):
        lines = [
            _tel(phase="PRELAUNCH", ut=0.0),
            _phase("PRELAUNCH", "COAST-TO-TARGET", 1.0),
            _tel(phase="COAST-TO-TARGET", ut=1.0),
            _phase("COAST-TO-TARGET", "PLAN-CORRECTION", 2.0),
            _phase("PLAN-CORRECTION", "COAST-TO-TARGET", 3.0),
            _tel(phase="COAST-TO-TARGET", ut=3.0),
        ]
        log = fe.parse_mission_log(lines)
        self.assertEqual([v.phase for v in log.visits],
                         ["PRELAUNCH", "COAST-TO-TARGET", "PLAN-CORRECTION", "COAST-TO-TARGET"])
        self.assertEqual([s.visit for s in log.samples], [0, 1, 3])

    def test_warp_actions_and_mission_name(self):
        log = fe.parse_mission_log(_read(_CAPTURE).splitlines())
        self.assertEqual(log.mission_name, "b11_mun_orbit")
        kinds = [c.kind for c in log.commands]
        self.assertIn("cancel_warp", kinds)
        self.assertEqual(log.visits[1].phase, "PLAN-CAPTURE")
        self.assertEqual(log.visits[1].cancels, 1)

    def test_sample_count_agrees_with_warp_audit(self):
        for path in (_OLD_1210, _CAPTURE, _ASCENT):
            lines = _read(path).splitlines()
            wa, _ = warp_audit.parse_log_lines(lines)
            self.assertEqual(len(fe.parse_mission_log(lines).samples), len(wa), path)

    def test_old_format_log_parses_without_ut(self):
        res = fe.analyze_mission(_read(_OLD_1210), None)
        self.assertFalse(res["hasUt"])
        self.assertGreater(res["telemetryLines"], 100)
        self.assertEqual(res["malformedLines"], 0)


class MatchAndCalibrationTests(unittest.TestCase):
    def _two_visit_log(self):
        lines = [_phase("A", "COAST", 0.0)]
        lines += [_tel(phase="COAST", ut=float(i)) for i in range(5)]
        lines += [_phase("COAST", "PARK", 5.0)]
        lines += [_tel(phase="PARK", ut=5.0 + i) for i in range(4)]
        return fe.parse_mission_log(lines)

    def test_match_visits_in_order_with_repeats_and_skips(self):
        log = fe.parse_mission_log([
            _phase("P", "COAST", 0.0), _phase("COAST", "PLAN", 1.0),
            _phase("PLAN", "COAST", 2.0)])
        rows = [{"phase": "P", "wallSeconds": 1.0},
                {"phase": "COAST", "wallSeconds": 10.0},
                {"phase": "UNREACHED", "wallSeconds": 99.0},
                {"phase": "PLAN", "wallSeconds": 2.0},
                {"phase": "COAST", "wallSeconds": 30.0}]
        self.assertEqual(fe.match_visits(log.visits, rows), 4)
        self.assertEqual([v.json_wall for v in log.visits], [1.0, 10.0, 2.0, 30.0])

    def test_visit_wall_distributed_over_intervals(self):
        log = self._two_visit_log()
        fe.match_visits(log.visits, [{"phase": "A", "wallSeconds": 0.0},
                                     {"phase": "COAST", "wallSeconds": 50.0},
                                     {"phase": "PARK", "wallSeconds": 30.0}])
        ivs = fe.build_intervals(log)
        coast = [iv for iv in ivs if iv.phase == "COAST"]
        park = [iv for iv in ivs if iv.phase == "PARK"]
        self.assertAlmostEqual(sum(iv.wall for iv in coast), 50.0, places=6)
        self.assertAlmostEqual(sum(iv.wall for iv in park), 30.0, places=6)
        self.assertEqual(len(park), 3)  # the last sample opens no interval

    def test_uncalibrated_fallback_is_about_one_second_per_line(self):
        log = self._two_visit_log()
        ivs = fe.build_intervals(log)
        self.assertTrue(all(abs(iv.wall - 1.0) < 1e-9 for iv in ivs))

    def test_one_x_blocking_gap_takes_its_game_span(self):
        lines = [_tel(ut=0.0), _tel(ut=1.0), _tel(ut=11.0), _tel(ut=12.0)]
        ivs = fe.build_intervals(fe.parse_mission_log(lines))
        self.assertEqual([round(iv.wall, 3) for iv in ivs], [1.0, 10.0, 1.0])

    def test_warp_to_gap_takes_the_visit_residual(self):
        lines = [_phase("A", "REENTRY", 0.0), _tel(phase="REENTRY", ut=0.0),
                 "[Mission][Info][REENTRY] action warp_to value=120.000",
                 _tel(phase="REENTRY", ut=1.0), _tel(phase="REENTRY", ut=121.0),
                 _tel(phase="REENTRY", ut=122.0)]
        log = fe.parse_mission_log(lines)
        fe.match_visits(log.visits, [{"phase": "A", "wallSeconds": 0.0},
                                     {"phase": "REENTRY", "wallSeconds": 12.0}])
        ivs = fe.build_intervals(log)
        gap = [iv for iv in ivs if iv.cause == "warp-to-gap"]
        self.assertEqual(len(gap), 1)
        self.assertEqual(gap[0].bucket, "warped")
        self.assertAlmostEqual(gap[0].wall, 10.0, places=6)
        self.assertAlmostEqual(sum(iv.wall for iv in ivs), 12.0, places=6)


class ClassifierTests(unittest.TestCase):
    def _cls(self, a_kw, b_kw):
        a, b = _sample(**a_kw), _sample(**b_kw)
        return fe.classify_interval(a, b)

    def test_one_metre_orbit_threshold(self):
        self.assertEqual(self._cls({}, {"ap": 100000.9})[0], "idle")
        self.assertEqual(self._cls({}, {"ap": 100001.1})[0], "burn")
        self.assertEqual(self._cls({}, {"pe": 90001.5})[0], "burn")

    def test_unknowable_orbit_is_unclassified(self):
        self.assertEqual(self._cls({}, {"body": "Mun"})[0], "unclassified")
        self.assertEqual(self._cls({"ap": "nan"}, {})[0], "unclassified")

    def test_atmosphere_and_ground_gating(self):
        flying = {"situation": "FLYING", "alt": 69000.0}
        self.assertEqual(self._cls(flying, flying)[0], "atmoOrGround")
        above = {"situation": "FLYING", "alt": 71000.0}
        self.assertEqual(self._cls(above, above)[0], "idle")
        landed = {"situation": "LANDED", "alt": 50.0, "body": "Mun"}
        self.assertEqual(self._cls(landed, landed)[0], "atmoOrGround")
        mun = {"situation": "FLYING", "alt": 3000.0, "body": "Mun"}
        self.assertEqual(self._cls(mun, mun)[0], "idle")  # airless always qualifies
        unknown = {"situation": "FLYING", "body": "Rask"}
        self.assertEqual(self._cls(unknown, unknown)[0], "atmoOrGround")
        unknown_orbit = {"situation": "ORBITING", "body": "Rask"}
        self.assertEqual(self._cls(unknown_orbit, unknown_orbit)[0], "idle")
        eve = {"situation": "FLYING", "alt": 85000.0, "body": "Eve"}
        self.assertEqual(self._cls(eve, eve)[0], "atmoOrGround")

    def test_low_warp_legality(self):
        low = {"warp": "RAILSx10.000", "alt": 80000.0}
        self.assertEqual(self._cls(low, low), ("lowWarp", "low-warp"))
        legal = {"warp": "RAILSx50.000", "alt": 80000.0}
        self.assertEqual(self._cls(legal, legal)[0], "warped")
        ramp = {"warp": "RAILSx44.000", "alt": 80000.0}  # buckets to 50
        self.assertEqual(self._cls(ramp, ramp)[0], "warped")
        unknown = {"warp": "RAILSx10.000", "body": "Rask"}
        self.assertEqual(self._cls(unknown, unknown)[0], "warped")
        # the lower endpoint altitude decides legality (Kerbin: 100x from 120 km)
        self.assertEqual(self._cls({"warp": "RAILSx50.000", "alt": 130000.0},
                                   {"warp": "RAILSx50.000", "alt": 130000.0})[0], "lowWarp")
        self.assertEqual(self._cls({"warp": "RAILSx50.000", "alt": 130000.0},
                                   {"warp": "RAILSx50.000", "alt": 110000.0})[0], "warped")

    def test_physics_warp(self):
        coast = {"warp": "PHYSICSx2.000"}
        self.assertEqual(self._cls(coast, coast), ("lowWarp", "low-warp"))
        flip = {"warp": "PHYSICSx2.000", "ap_err": 40.0}
        self.assertEqual(self._cls(flip, flip), ("lowWarp", "attitude-align"))
        self.assertEqual(self._cls(coast, {"warp": "PHYSICSx2.000", "ap": 100500.0})[0], "burn")
        atmo = {"warp": "PHYSICSx2.000", "situation": "FLYING", "alt": 30000.0}
        self.assertEqual(self._cls(atmo, atmo)[0], "warped")

    def test_each_idle_cause(self):
        cases = [
            ({"phase": "PARK"}, "dwell"),
            ({"ap_err": 120.0}, "attitude-align"),
            ({"nodes": 1, "node_ut": "5000", "ut": 1000.0}, "waiting-for-node"),
            ({"next_body": "Mun"}, "soi-approach"),
            ({"vspd": 12.0}, "coast-to-apoapsis"),
            ({"vspd": -12.0, "pe": 40000.0}, "coast-to-entry"),
            ({"vspd": -12.0, "pe": -5000.0, "body": "Mun", "alt": 50000.0}, "coast-to-entry"),
            ({"vspd": -12.0, "pe": 90000.0}, "coast-to-periapsis"),
            ({"vspd": "nan"}, "other-coast"),
        ]
        for kw, want in cases:
            self.assertEqual(fe.idle_cause(_sample(**kw)), want, kw)

    def test_cause_precedence(self):
        # a node in the past is not a node wait
        self.assertEqual(fe.idle_cause(_sample(nodes=1, node_ut="900", ut=1000.0, vspd=5.0)),
                         "coast-to-apoapsis")
        # dwell beats everything; node beats SOI
        self.assertEqual(fe.idle_cause(_sample(phase="PARK", ap_err=90.0)), "dwell")
        self.assertEqual(fe.idle_cause(_sample(nodes=1, node_ut="5000", next_body="Kerbin",
                                               body="Mun", alt=200000.0)), "waiting-for-node")


class RunAndRecoverableTests(unittest.TestCase):
    def _log(self, n, **kw):
        return fe.parse_mission_log([_tel(ut=float(i), **kw) for i in range(n)])

    def test_idle_run_recoverable(self):
        ivs = fe.build_intervals(self._log(101, alt=700000.0))
        runs = fe.build_runs(ivs)
        self.assertEqual(len(runs), 1)
        r = runs[0]
        self.assertEqual(r.kind, "idle")
        self.assertAlmostEqual(r.wall, 100.0, places=6)
        # 100 game s at 100000x is ~0; recoverable = wall - overhead
        self.assertAlmostEqual(r.recoverable, 100.0 - 0.001 - fe.RAMP_SETTLE_OVERHEAD_SECONDS, places=3)
        self.assertEqual(list(r.by_cause), ["coast-to-apoapsis"])

    def test_short_run_floors_at_zero(self):
        runs = fe.build_runs(fe.build_intervals(self._log(6)))
        self.assertEqual(runs[0].recoverable, 0.0)

    def test_attitude_align_uses_physics_target(self):
        ivs = fe.build_intervals(self._log(41, ap_err=90.0))
        r = fe.build_runs(ivs)[0]
        self.assertAlmostEqual(r.recoverable, 40.0 - 40.0 / 4.0 - fe.RAMP_SETTLE_OVERHEAD_SECONDS,
                               places=6)

    def test_unknown_body_gets_no_estimate(self):
        r = fe.build_runs(fe.build_intervals(self._log(50, body="Rask")))[0]
        self.assertEqual(r.recoverable, 0.0)

    def test_long_burn_is_optional_only(self):
        lines = [_tel(ut=float(i), ap=100000.0 + 10.0 * i) for i in range(80)]
        res = fe.analyze_mission("\n".join(lines), None)
        self.assertEqual(res["recoverable"], 0.0)
        self.assertGreater(res["optionalBurnSaving"], 0.0)
        burns = [r for r in res["runs"] if r["kind"] == "burn"]
        self.assertEqual(len(burns), 1)
        self.assertAlmostEqual(burns[0]["optionalPhysicsWarpSaving"], 79.0 - 79.0 / 2.0, places=3)
        self.assertEqual([e["cause"] for e in res["recommendations"]], [fe.BURN_CAUSE])
        self.assertTrue(res["recommendations"][0]["optional"])

    def test_short_burn_not_flagged(self):
        lines = [_tel(ut=float(i), ap=100000.0 + 10.0 * i) for i in range(30)]
        res = fe.analyze_mission("\n".join(lines), None)
        self.assertEqual(res["optionalBurnSaving"], 0.0)

    def test_no_double_counting(self):
        res = fe.analyze_mission(_read(_ASCENT), None)
        self.assertAlmostEqual(sum(res["buckets"].values()), res["sampledWallSeconds"], places=2)
        log = fe.parse_mission_log(_read(_CAPTURE).splitlines())
        ivs = fe.build_intervals(log)
        res = fe.analyze_mission(_read(_CAPTURE), None)
        for p in res["phases"]:
            visit_wall = sum(iv.wall for iv in ivs if iv.visit == p["visit"])
            self.assertAlmostEqual(sum(p[b] for b in fe.BUCKETS), visit_wall, places=2)
        self.assertTrue(all(iv.bucket in fe.BUCKETS for iv in ivs))
        self.assertEqual(len(ivs), len(log.samples) - 1)


class WarpFlagTests(unittest.TestCase):
    def _flags(self, lines):
        log = fe.parse_mission_log(lines)
        return fe.warp_flags(log, fe.build_intervals(log))

    def test_oscillation_counts_reraises(self):
        warps = ["RAILSx100.000", "NONEx1.000", "RAILSx100.000", "NONEx1.000",
                 "RAILSx100.000", "NONEx1.000"]
        lines = [_phase("A", "COAST", 0.0)] + [
            _tel(phase="COAST", warp=w, alt=700000.0, ut=float(i)) for i, w in enumerate(warps)]
        flags = [f for f in self._flags(lines) if f["kind"] == "warp-oscillation"]
        self.assertEqual(flags[0]["count"], 2)

    def test_slow_ladder_and_coast_1x(self):
        lines = [_phase("A", "COAST-TO-TARGET", 0.0), _tel(phase="COAST-TO-TARGET", ut=0.0),
                 "[Mission][Info][COAST-TO-TARGET] action set_rails_warp value=5.000"]
        lines += [_tel(phase="COAST-TO-TARGET", ut=1.0 + i, alt=700000.0) for i in range(40)]
        lines += [_tel(phase="COAST-TO-TARGET", warp="RAILSx1000.000", ut=50.0, alt=700000.0)]
        kinds = {f["kind"]: f for f in self._flags(lines)}
        self.assertTrue(kinds["slow-warp-ladder"]["reached"])
        self.assertGreaterEqual(kinds["slow-warp-ladder"]["seconds"], 40.0)
        self.assertGreaterEqual(kinds["coast-phase-1x"]["seconds"], 30.0)

    def test_slow_warp_to_gap(self):
        lines = [_phase("A", "REENTRY", 0.0), _tel(phase="REENTRY", ut=0.0, alt=80000.0),
                 "[Mission][Info][REENTRY] action warp_to value=1000.000",
                 _tel(phase="REENTRY", ut=1.0, alt=80000.0),
                 _tel(phase="REENTRY", ut=1000.0, alt=75000.0),
                 _tel(phase="REENTRY", ut=1001.0, alt=75000.0)]
        log = fe.parse_mission_log(lines)
        fe.match_visits(log.visits, [{"phase": "A", "wallSeconds": 0.0},
                                     {"phase": "REENTRY", "wallSeconds": 102.0}])
        flags = fe.warp_flags(log, fe.build_intervals(log))
        gap = [f for f in flags if f["kind"] == "slow-warp-to-gap"]
        self.assertEqual(gap[0]["hops"], 1)
        self.assertEqual(gap[0]["legalRate"], 50.0)
        self.assertAlmostEqual(gap[0]["seconds"], 100.0, places=1)


class RealExcerptTests(unittest.TestCase):
    def test_capture_burn_node_wait_and_park_dwell(self):
        log = fe.parse_mission_log(_read(_CAPTURE).splitlines())
        # the excerpt opens on the TARGET-FLYBY -> PLAN-CAPTURE transition, so the
        # source phase becomes an empty first visit (a full log's PRELAUNCH).
        self.assertEqual([v.phase for v in log.visits],
                         ["TARGET-FLYBY", "PLAN-CAPTURE", "CAPTURE-BURN", "PARK"])
        ivs = fe.build_intervals(log)
        cb = [iv for iv in ivs if iv.phase == "CAPTURE-BURN"]
        # the MechJeb ramp to 1000x at ~2,000 km of Mun is below the legal 100000x
        self.assertEqual(cb[0].bucket, "lowWarp")
        x1 = [iv for iv in cb if iv.rate <= 1.05 and iv.bucket == "idle"]
        self.assertGreaterEqual(len(x1), 10)
        self.assertTrue(all(iv.cause == "waiting-for-node" for iv in x1))
        self.assertTrue(any(iv.bucket == "burn" for iv in cb))  # the capture burn tail
        park = [iv for iv in ivs if iv.phase == "PARK"]
        self.assertTrue(park and all((iv.bucket, iv.cause) == ("idle", "dwell") for iv in park))

    def test_capture_recommendation_sites(self):
        res = fe.analyze_mission(_read(_CAPTURE), None)
        self.assertEqual(res["mission"], "b11_mun_orbit")
        sites = {(e["phase"], e["cause"]): e["site"] for e in res["recommendations"]}
        self.assertIn("mlib.b5_decide phase CAPTURE-BURN", sites[("CAPTURE-BURN", "waiting-for-node")])
        self.assertIn("ACTION_MJ_EXECUTE_NODES", sites[("CAPTURE-BURN", "waiting-for-node")])

    def test_ascent_coast_burn_and_ramp(self):
        log = fe.parse_mission_log(_read(_ASCENT).splitlines())
        ivs = fe.build_intervals(log)
        buckets = [(iv.bucket, iv.cause) for iv in ivs]
        self.assertEqual(buckets[0], ("warped", ""))  # physics warp inside the atmosphere
        self.assertIn(("idle", "waiting-for-node"), buckets)  # 1x coast to the circ node
        self.assertIn(("lowWarp", "low-warp"), buckets)  # MechJeb rails 5-10x where 50x is legal
        burn = [iv for iv in ivs if iv.bucket == "burn"]
        self.assertGreaterEqual(len(burn), 15)  # the circularization burn, thr= notwithstanding
        res = fe.analyze_mission(_read(_ASCENT), None)
        site = [e["site"] for e in res["recommendations"]
                if (e["phase"], e["cause"]) == ("MJ-ASCENT", "waiting-for-node")]
        self.assertTrue(site and "ACTION_MJ_ENGAGE_ASCENT" in site[0])


class KspAnchorTests(unittest.TestCase):
    def test_excerpt_anchors(self):
        a = fe.parse_ksp_anchors(_read(_KSP).splitlines())
        self.assertAlmostEqual(a["init"], 15 * 3600 + 31 * 60 + 6.003, places=3)
        self.assertAlmostEqual(a["firstMainMenu"] - a["init"], 23.911, places=3)
        c1 = a["commands"]["0001"]
        self.assertEqual(c1["verb"], "LoadGame")
        self.assertTrue(c1["pending"])
        self.assertEqual(c1["verdict"], "OK")
        self.assertAlmostEqual(c1["final"] - c1["recv"], 5.992, places=3)
        self.assertEqual(a["commandOrder"], ["0001", "0002", "0003", "0004"])
        self.assertEqual(len(a["loadgameComplete"]), 1)
        self.assertAlmostEqual(a["last"] - a["quit"], 0.537, places=3)

    def test_midnight_rollover(self):
        lines = ["[LOG 23:59:50.000] ******* Log Initiated for Kerbal Space Program",
                 "[LOG 23:59:59.500] x",
                 "[LOG 00:00:10.000] [HighLogic]: === Scene Change : From LOADING to MAINMENU ===",
                 "[LOG 00:00:11.000] [Parsek][INFO][TestCommands] recv id=0001 cmd=LoadGame args=2",
                 "[LOG 00:00:12.000] [Parsek][INFO][TestCommands] exec id=0001 verdict=OK"]
        a = fe.parse_ksp_anchors(lines)
        self.assertAlmostEqual(a["firstMainMenu"] - a["init"], 20.0, places=6)
        self.assertAlmostEqual(a["commands"]["0001"]["final"], 86412.0, places=6)

    def test_out_of_order_mod_stamp_is_skipped(self):
        lines = ["[LOG 00:00:55.000] ******* Log Initiated for Kerbal Space Program",
                 "[LOG 20:20:20.484] Log started at 2026-09-10 20:20:20.484",
                 "[LOG 00:01:00.000] y",
                 "[LOG 00:01:18.000] [HighLogic]: === Scene Change : From LOADING to MAINMENU ==="]
        a = fe.parse_ksp_anchors(lines)
        self.assertEqual(a["outOfOrder"], 1)
        self.assertAlmostEqual(a["firstMainMenu"] - a["init"], 23.0, places=6)
        back = ["[LOG 21:58:00.000] ******* Log Initiated for Kerbal Space Program",
                "[LOG 20:20:20.484] Log started at ...",
                "[LOG 21:58:30.000] [HighLogic]: === Scene Change : From LOADING to MAINMENU ==="]
        b = fe.parse_ksp_anchors(back)
        self.assertEqual(b["outOfOrder"], 1)
        self.assertAlmostEqual(b["firstMainMenu"] - b["init"], 30.0, places=6)

    def test_no_init_line_leaves_init_unknown(self):
        a = fe.parse_ksp_anchors(["[LOG 10:00:00.000] something else"])
        self.assertIsNone(a["init"])
        self.assertIsNone(fe.parse_ksp_time("no stamp here"))

    def test_utc_offset_derivation(self):
        self.assertEqual(fe.derive_utc_offset(15 * 3600 + 31 * 60 + 6, 12 * 3600 + 31 * 60 + 3),
                         3 * 3600)
        self.assertEqual(fe.derive_utc_offset(0 * 3600 + 0 * 60 + 55, 21 * 3600 + 0 * 60 + 52),
                         3 * 3600)  # local just past midnight
        self.assertEqual(fe.derive_utc_offset(7 * 3600 + 5, 12 * 3600), -5 * 3600)
        self.assertEqual(fe.derive_utc_offset(12 * 3600 + 30 * 60 + 4, 7 * 3600), 5.5 * 3600)


def _b11_result():
    return {"runId": "2026-10-01_1231_B11-mun-orbit", "scenarioId": "B11-mun-orbit",
            "verdict": "PARSEK-FAIL", "startedUtc": "2026-10-01T12:31:03Z",
            "endedUtc": "2026-10-01T12:53:08Z", "wallSeconds": 1324, "attempt": 1,
            "attemptsWallSeconds": 1324, "missionWallSeconds": 1275,
            "driver": {"steps": [
                {"id": "0001", "cmd": "LoadGame"}, {"id": "0002", "cmd": "SetSetting"},
                {"id": "0003", "phase": "mission"}, {"id": "0004", "cmd": "FlushAndQuit"}]}}


class OverheadTests(unittest.TestCase):
    def test_b11_overhead_rows(self):
        anchors = fe.parse_ksp_anchors(_read(_KSP).splitlines())
        o = fe.build_overhead(_b11_result(), anchors, 1274.615, {"lock": "acquired-free"})
        rows = {r["key"]: r["seconds"] for r in o["rows"]}
        self.assertEqual(o["utcOffsetSeconds"], 10800.0)
        self.assertAlmostEqual(rows["preLaunch"], 3.003, places=3)
        self.assertAlmostEqual(rows["kspBoot"], 23.911, places=3)
        self.assertAlmostEqual(rows["missionEnvelope"], 1275.429, places=3)
        self.assertAlmostEqual(rows["missionSpawnConnect"], 0.814, places=3)
        self.assertAlmostEqual(rows["quit"], 0.537, places=3)
        self.assertAlmostEqual(rows["postQuitTail"], 15.172, places=3)
        self.assertEqual([s["verb"] for s in o["steps"]], ["LoadGame", "SetSetting", "FlushAndQuit"])
        self.assertEqual([s["verb"] for s in o["inMissionSeamCommands"]], ["CommitTree"])
        self.assertIsNotNone(o["unaccounted"])
        self.assertLess(abs(o["unaccounted"]), 2.0)
        self.assertEqual(o["priorAttemptsWallSeconds"], 0)
        self.assertEqual(o["lock"], "acquired-free")

    def test_missing_anchors_report_unknown(self):
        o = fe.build_overhead(_b11_result(), None, None, None)
        rows = {r["key"]: r for r in o["rows"]}
        self.assertIsNone(rows["kspBoot"]["seconds"])
        self.assertIn("no KSP.log", rows["kspBoot"]["note"])
        self.assertIsNone(o["unaccounted"])
        o2 = fe.build_overhead(None, fe.parse_ksp_anchors(_read(_KSP).splitlines()), None, None)
        self.assertIsNone({r["key"]: r for r in o2["rows"]}["postQuitTail"]["seconds"])

    def test_retry_attempts(self):
        res = dict(_b11_result(), attempt=2, attemptsWallSeconds=2000)
        o = fe.build_overhead(res, None, None, None)
        self.assertEqual(o["priorAttemptsWallSeconds"], 676)


class HarnessLogTests(unittest.TestCase):
    def test_multi_scenario_blocks(self):
        lines = [
            "[Harness][Info][Lock] run-lock acquired (acquired-free) path=x pid=1 selection=s",
            "[Harness][Info][Result] result written C:\\r\\2026-10-01_1127_B2-lko-ascent.json",
            "[Harness][Info][Retry] retry scenario=B2-lko-ascent attempt=2 reason=INVALID",
            "[Harness][Info][Result] result written C:\\r\\2026-10-01_1130_B2-lko-ascent_a2.json",
            "[Harness][Info][Cost] scenario cost attempts=2 wallTotal=400s terminal=PASS",
            "[Harness][Info][Result] result written /r/2026-10-01_1135_B5-mun-flyby.json",
            "[Harness][Info][Cost] scenario cost attempts=1 wallTotal=500s terminal=PASS",
        ]
        out = fe.parse_harness_log(lines)
        self.assertEqual(out["2026-10-01_1127_B2-lko-ascent"]["cost"]["attempts"], 2)
        self.assertEqual(out["2026-10-01_1130_B2-lko-ascent_a2"]["retries"][0]["reason"], "INVALID")
        self.assertEqual(out["2026-10-01_1135_B5-mun-flyby"]["cost"]["wallTotal"], 500)
        self.assertEqual(out["2026-10-01_1135_B5-mun-flyby"]["retries"], [])
        self.assertTrue(all(v["lock"] == "acquired-free" for v in out.values()))


class AggregateTests(unittest.TestCase):
    def _run(self, rid, rec):
        return {"runId": rid, "scenario": fe.scenario_from_run_id(rid), "totalWallSeconds": 100.0,
                "missionWallSeconds": 80.0,
                "overhead": {"rows": [{"key": "kspBoot", "seconds": 24.0}],
                             "steps": [{"verb": "LoadGame", "seconds": 6.0}]},
                "mission": {"sampledWallSeconds": 80.0, "recoverable": rec,
                            "optionalBurnSaving": 0.0,
                            "buckets": dict({b: 0.0 for b in fe.BUCKETS}, idle=40.0),
                            "recommendations": [{"phase": "PARK", "cause": "dwell",
                                                 "recoverable": rec, "optional": False,
                                                 "site": "s"}]}}

    def test_ranking_and_shares(self):
        runs = [self._run("2026-10-01_1000_A", 10.0), self._run("2026-10-01_1100_B_a2", 50.0),
                self._run("2026-10-01_1200_B", 30.0)]
        agg = fe.aggregate(runs)
        self.assertEqual([l["scenario"] for l in agg["lanes"]], ["B", "A"])
        self.assertEqual(agg["lanes"][0]["recoverable"], 80.0)
        self.assertEqual(agg["idleShareOfMissionWall"], 0.5)
        self.assertEqual(agg["causes"][0], {"cause": "dwell", "recoverable": 90.0, "runs": 3})
        self.assertEqual(agg["overhead"]["kspBoot"]["mean"], 24.0)
        self.assertEqual(agg["stepsByVerb"][0]["count"], 3)

    def test_cause_runs_count_distinct_runs(self):
        run = self._run("2026-10-01_1000_A", 10.0)
        run["mission"]["recommendations"].append(
            {"phase": "ORBIT-COMMIT", "cause": "dwell", "recoverable": 5.0,
             "optional": False, "site": "s"})
        run["mission"]["recoverable"] = 15.0
        agg = fe.aggregate([run, self._run("2026-10-01_1100_A", 20.0)])
        self.assertEqual(agg["causes"][0], {"cause": "dwell", "recoverable": 35.0, "runs": 2})
        park = [r for r in agg["laneCauses"] if r["phase"] == "PARK"][0]
        self.assertEqual(park["runs"], 2)

    def test_scenario_from_run_id(self):
        self.assertEqual(fe.scenario_from_run_id("2026-10-01_1231_B11-mun-orbit"), "B11-mun-orbit")
        self.assertEqual(fe.scenario_from_run_id("2026-09-29_2209_BDOCK-1-station-interceptor_a2"),
                         "BDOCK-1-station-interceptor")
        self.assertEqual(fe.scenario_from_run_id("2026-09-29_2209_X_run2_a2"), "X")
        self.assertEqual(fe.scenario_from_run_id("odd"), "odd")

    def test_dedupe_prefers_most_artifacts(self):
        cands = [{"runId": "r1", "dir": "a", "result": "x"},
                 {"runId": "r1", "dir": "b", "result": "x", "kspLog": "k"},
                 {"runId": "r2", "dir": "b", "result": "x"},
                 {"runId": "r2", "dir": "a", "result": "x"}]
        kept, dropped = fe.dedupe_candidates(cands)
        self.assertEqual(dropped, 2)
        self.assertEqual([(c["runId"], c["dir"]) for c in kept], [("r1", "b"), ("r2", "a")])


class DocumentTests(unittest.TestCase):
    def test_json_schema_keys_and_nan_safety(self):
        run = fe.analyze_run("2026-10-01_1231_B11-mun-orbit", _b11_result(), None,
                             _read(_CAPTURE), _read(_KSP).splitlines(), None)
        doc = fe.build_document([run], [{"runId": "x", "dir": "d", "reason": "r"}], 0)
        text = fe.to_json(doc)
        back = json.loads(text)
        self.assertEqual(back["schema"], "flight-efficiency")
        self.assertEqual(back["schemaVersion"], fe.SCHEMA_VERSION)
        self.assertEqual(sorted(back), ["aggregate", "constants", "duplicatesDropped", "runs",
                                        "schema", "schemaVersion", "skipped"])
        r = back["runs"][0]
        for key in ("runId", "scenario", "verdict", "totalWallSeconds", "missionWallSeconds",
                    "harnessResidueSeconds", "mission", "overhead", "notes"):
            self.assertIn(key, r)
        for key in ("buckets", "phases", "runs", "flags", "recommendations",
                    "recoverableByCause", "idleByCause"):
            self.assertIn(key, r["mission"])
        self.assertNotIn("NaN", text)
        self.assertIsNone(fe.sanitize(float("nan")))
        self.assertEqual(fe.sanitize((1, float("inf"))), [1, None])

    def test_partial_runs_never_crash(self):
        for args in ((None, None, None, None, None),
                     ({}, {}, "", [], {}),
                     (None, {"warpUtilisation": "bogus"}, _read(_CAPTURE)[:3000], None, None),
                     ({"startedUtc": "garbage"}, None, None, ["[LOG 99:99:99.000] x"], None)):
            run = fe.analyze_run("2026-10-01_0000_X", *args)
            text = fe.render_run(run)
            self.assertIn("FLIGHT EFFICIENCY", text)
            json.loads(fe.to_json(fe.build_document([run], [], 0)))


class CodeSiteTests(unittest.TestCase):
    # Both gates walk the AST so a call left only in a comment or docstring
    # cannot keep them green.
    @staticmethod
    def _calls(src):
        return [n for n in ast.walk(ast.parse(src)) if isinstance(n, ast.Call)]

    def test_every_mission_machine_is_called_by_its_shell(self):
        for mission, machine in fe.MISSION_MACHINES.items():
            path = os.path.join(_HARNESS, "missions", mission + ".py")
            self.assertTrue(os.path.isfile(path), path)
            called = {c.func.attr for c in self._calls(_read(path))
                      if isinstance(c.func, ast.Attribute)
                      and isinstance(c.func.value, ast.Name) and c.func.value.id == "mlib"}
            self.assertIn(machine, called, mission)

    def test_every_hint_param_exists_in_mlib(self):
        keys = {c.args[0].value for c in self._calls(_read(_MLIB_PATH))
                if isinstance(c.func, ast.Attribute) and c.func.attr == "get"
                and isinstance(c.func.value, ast.Name) and c.func.value.id == "params"
                and c.args and isinstance(c.args[0], ast.Constant)
                and isinstance(c.args[0].value, str)}
        for key, (_text, params) in fe.SITE_HINTS.items():
            for p in params:
                self.assertIn(p, keys, (key, p))

    def test_ast_gates_ignore_comments(self):
        src = '# mlib.b5_decide(\n"""params.get(\'parkDwellSeconds\')"""\nx = 1\n'
        self.assertEqual(self._calls(src), [])

    def test_hint_resolution_and_fallbacks(self):
        self.assertIn("parkDwellSeconds", fe.code_site("b11_mun_orbit", "dwell", "PARK"))
        self.assertIn("harness/missions/b11_mun_orbit.py -> mlib.b5_decide phase PARK",
                      fe.code_site("b11_mun_orbit", "dwell", "PARK"))
        self.assertEqual(fe.site_hint("dwell", "SOME-HOLD"), fe.SITE_HINTS[("dwell", "*")])
        self.assertIn("1x mystery in X", fe.code_site(None, "mystery", "X"))
        self.assertIn("harness/missions/unknown_m.py phase", fe.code_site("unknown_m", "dwell", "X"))

    def test_every_idle_cause_has_a_hint(self):
        for cause in fe.IDLE_CAUSES + (fe.LOW_WARP_CAUSE, fe.BURN_CAUSE):
            self.assertIn((cause, "*"), fe.SITE_HINTS, cause)


class ShellTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="flighteff_")
        rid = "2026-10-01_1231_B11-mun-orbit"
        shutil.copy(_CAPTURE, os.path.join(self.tmp, rid + "_mission.stdout.log"))
        shots = os.path.join(self.tmp, rid + "_shots")
        os.makedirs(shots)
        shutil.copy(_KSP, os.path.join(shots, "KSP.log"))
        with open(os.path.join(self.tmp, rid + ".json"), "w") as f:
            json.dump(_b11_result(), f)
        with open(os.path.join(self.tmp, rid + "_status.json"), "w") as f:
            f.write("{}")
        with open(os.path.join(self.tmp, "2026-10-01_1300_ONLY-STATUS_status.json"), "w") as f:
            f.write("{}")
        with open(os.path.join(self.tmp, "2026-10-01_123103_harness.log"), "w") as f:
            f.write("[Harness][Info][Lock] run-lock acquired (acquired-free) path=x pid=1\n"
                    "[Harness][Info][Result] result written C:\\x\\%s.json\n"
                    "[Harness][Info][Cost] scenario cost attempts=1 wallTotal=1324s terminal=PASS\n"
                    % rid)

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_discover_suffixes(self):
        cands = {c["runId"]: c for c in tool.discover(self.tmp)}
        c = cands["2026-10-01_1231_B11-mun-orbit"]
        for k in ("result", "missionLog", "status", "shots", "kspLog"):
            self.assertIn(k, c)
        self.assertNotIn("2026-10-01_123103_harness.log", cands)

    def test_collect_and_json_out(self):
        doc = tool.collect([self.tmp, self.tmp], None, [], [])
        self.assertEqual(len(doc["runs"]), 1)
        self.assertEqual(doc["duplicatesDropped"], 2)
        self.assertEqual([s["reason"] for s in doc["skipped"]],
                         ["no result json and no mission log"])
        run = doc["runs"][0]
        self.assertEqual(run["overhead"]["cost"]["wallTotal"], 1324)
        out = os.path.join(self.tmp, "eff.json")
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            rc = tool.main(["--results-dir", self.tmp, "--json-out", out, "--since", "2026-10-01"])
        self.assertEqual(rc, 0)
        self.assertIn("FLIGHT EFFICIENCY  2026-10-01_1231_B11-mun-orbit", buf.getvalue())
        with open(out) as f:
            self.assertEqual(json.load(f)["schemaVersion"], fe.SCHEMA_VERSION)

    def test_filters(self):
        self.assertEqual(len(tool.collect([self.tmp], "2026-10-02", [], [])["runs"]), 0)
        self.assertEqual(len(tool.collect([self.tmp], None, ["B5-mun-flyby"], [])["runs"]), 0)
        self.assertEqual(len(tool.collect([self.tmp], None, ["B11-mun-orbit"], [])["runs"]), 1)


if __name__ == "__main__":
    unittest.main()
