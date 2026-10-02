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

    def test_physics_warp_is_its_own_bucket(self):
        coast = {"warp": "PHYSICSx4.000"}
        self.assertEqual(self._cls(coast, coast), ("physicsWarp", ""))
        flip = {"warp": "PHYSICSx2.000", "ap_err": 40.0}
        self.assertEqual(self._cls(flip, flip), ("physicsWarp", ""))
        self.assertEqual(self._cls(coast, {"warp": "PHYSICSx4.000", "ap": 100500.0}),
                         ("physicsWarp", ""))
        atmo = {"warp": "PHYSICSx2.000", "situation": "FLYING", "alt": 30000.0}
        self.assertEqual(self._cls(atmo, atmo), ("physicsWarp", ""))
        unknown = {"warp": "PHYSICSx3.000", "ap": "nan"}
        self.assertEqual(self._cls(unknown, unknown), ("physicsWarp", ""))
        # at or below the 1x tolerance a PHYSICS reading is 1x
        settle = {"warp": "PHYSICSx1.040"}
        self.assertEqual(self._cls(settle, settle), ("idle", "coast-to-apoapsis"))

    def test_physics_warp_is_never_recoverable(self):
        lines = [_phase("A", "PARK", 0.0)]
        lines += [_tel(phase="PARK", warp="PHYSICSx4.000", ut=4.0 * i, alt=700000.0)
                  for i in range(60)]
        lines.insert(0, "[Mission][Info][PRELAUNCH] mission start name=b11_mun_orbit")
        res = fe.analyze_mission("\n".join(lines), None)
        self.assertGreater(res["buckets"]["physicsWarp"], 50.0)
        self.assertEqual(res["buckets"]["lowWarp"], 0.0)
        self.assertEqual(res["recoverable"], 0.0)
        self.assertEqual(res["byDesign"], 0.0)
        self.assertEqual(res["recommendations"], [])
        self.assertAlmostEqual(sum(res["buckets"].values()), res["sampledWallSeconds"], places=3)

    def test_bucket_set(self):
        self.assertEqual(fe.BUCKETS, ("idle", "lowWarp", "physicsWarp", "burn", "atmoOrGround",
                                      "warped", "unclassified"))

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
        self.assertEqual(buckets[0], ("physicsWarp", ""))  # physics warp inside the atmosphere
        self.assertIn(("idle", "waiting-for-node"), buckets)  # 1x coast to the circ node
        self.assertIn(("lowWarp", "low-warp"), buckets)  # MechJeb rails 5-10x where 50x is legal
        burn = [iv for iv in ivs if iv.bucket == "burn"]
        self.assertGreaterEqual(len(burn), 15)  # the circularization burn, thr= notwithstanding
        res = fe.analyze_mission(_read(_ASCENT), None)
        site = [e["site"] for e in res["recommendations"]
                if (e["phase"], e["cause"]) == ("MJ-ASCENT", "waiting-for-node")]
        self.assertTrue(site and "ACTION_MJ_ENGAGE_ASCENT" in site[0])
        rows = [e for e in res["recommendations"] if e["phase"] == "MJ-ASCENT"
                and e["cause"] in ("waiting-for-node", "coast-to-apoapsis")]
        self.assertTrue(rows and all(e["byDesign"] and e["recoverable"] == 0.0 for e in rows))


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
        self.assertEqual(agg["causes"][0], {"cause": "dwell", "recoverable": 90.0, "runs": 3,
                                            "byDesign": 0.0, "byDesignRuns": 0})
        self.assertEqual(agg["overhead"]["kspBoot"]["mean"], 24.0)
        self.assertEqual(agg["stepsByVerb"][0]["count"], 3)

    def test_cause_runs_count_distinct_runs(self):
        run = self._run("2026-10-01_1000_A", 10.0)
        run["mission"]["recommendations"].append(
            {"phase": "ORBIT-COMMIT", "cause": "dwell", "recoverable": 5.0,
             "optional": False, "site": "s"})
        run["mission"]["recoverable"] = 15.0
        agg = fe.aggregate([run, self._run("2026-10-01_1100_A", 20.0)])
        self.assertEqual(agg["causes"][0], {"cause": "dwell", "recoverable": 35.0, "runs": 2,
                                            "byDesign": 0.0, "byDesignRuns": 0})
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
                             _old_capture_log(), _read(_KSP).splitlines(), None)
        doc = fe.build_document([run], [{"runId": "x", "dir": "d", "reason": "r"}], 0)
        text = fe.to_json(doc)
        back = json.loads(text)
        self.assertEqual(back["schema"], "flight-efficiency")
        self.assertEqual(back["schemaVersion"], fe.SCHEMA_VERSION)
        self.assertEqual(back["schemaVersion"], 2)
        self.assertEqual(sorted(back), ["aggregate", "byDesignPolicy", "constants",
                                        "duplicatesDropped", "runs", "schema", "schemaVersion",
                                        "skipped"])
        self.assertEqual(len(back["byDesignPolicy"]), len(fe.BY_DESIGN_POLICY) + 1)
        r = back["runs"][0]
        for key in ("runId", "scenario", "verdict", "totalWallSeconds", "missionWallSeconds",
                    "harnessResidueSeconds", "mission", "overhead", "notes"):
            self.assertIn(key, r)
        for key in ("buckets", "phases", "runs", "flags", "recommendations",
                    "recoverableByCause", "idleByCause", "byDesign", "byDesignWall",
                    "byDesignByCause"):
            self.assertIn(key, r["mission"])
        self.assertIn("physicsWarp", r["mission"]["buckets"])
        for p in r["mission"]["phases"]:
            for key in ("physicsWarp", "byDesign", "byDesignWall", "byDesignByCause",
                        "captureLeadSeconds", "captureLeadHalfBurnKnown"):
                self.assertIn(key, p)
        for e in r["mission"]["recommendations"]:
            for key in ("byDesign", "byDesignReason", "byDesignSeconds", "outcomeSensitive"):
                self.assertIn(key, e)
        self.assertTrue(any(e["byDesign"] for e in r["mission"]["recommendations"]))
        for key in ("physicsWarpSeconds", "byDesignSeconds", "byDesignWallSeconds"):
            self.assertIn(key, back["aggregate"])
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


def _mission_log(mission, phase, n, step=1.0, prefix=(), **kw):
    lines = ["[Mission][Info][PRELAUNCH] mission start name=%s" % mission] if mission else []
    lines += list(prefix)
    lines.append(_phase("PRELAUNCH", phase, 0.0))
    lines += [_tel(phase=phase, ut=step * i, **kw) for i in range(n)]
    return "\n".join(lines)


class PolicyTableTests(unittest.TestCase):
    """The by-design / policy-target / outcome tables (contract items 5, 8, 9)."""

    _ORBIT = dict(alt=700000.0, ap=800000.0, pe=650000.0)

    def _mlib_phase_literals(self):
        tree = ast.parse(_read(_MLIB_PATH))
        return {n.value.value for n in ast.walk(tree)
                if isinstance(n, ast.Assign) and isinstance(n.value, ast.Constant)
                and isinstance(n.value.value, str)}

    def test_table_keys(self):
        self.assertEqual(sorted(fe.BY_DESIGN_POLICY), sorted([
            ("attitude-align", "DEORBIT"), ("dwell", "HOLD-DEPART"), ("dwell", "HOLD-ARRIVE"),
            ("dwell", "HOLD-PARK"), ("waiting-for-node", "TRANSFER-BURN"),
            ("waiting-for-node", "MJ-ASCENT"), ("coast-to-apoapsis", "MJ-ASCENT")]))
        self.assertEqual(sorted(fe.OUTCOME_SENSITIVE), [("waiting-for-node", "CIRCULARIZE")])
        self.assertEqual(fe.CAPTURE_LEAD_KEY, ("waiting-for-node", "CAPTURE-BURN"))
        self.assertTrue(all(fe.BY_DESIGN_POLICY.values()))

    def test_every_policy_key_names_a_real_cause_and_mlib_phase(self):
        phases = self._mlib_phase_literals()
        keys = (list(fe.BY_DESIGN_POLICY) + list(fe.OUTCOME_SENSITIVE)
                + list(fe.POLICY_PHYSICS_TARGETS) + [fe.CAPTURE_LEAD_KEY])
        for cause, phase in keys:
            self.assertIn(cause, fe.IDLE_CAUSES, (cause, phase))
            self.assertIn(phase, phases, (cause, phase))
        for machine in fe.POLICY_PHYSICS_TARGETS.values():
            self.assertIn(machine, fe.MISSION_MACHINES.values())

    def test_each_by_design_entry(self):
        cases = {
            "attitude-align": dict(ap_err=90.0),
            "dwell": {},
            "waiting-for-node": dict(nodes=1, node_ut="99999"),
            "coast-to-apoapsis": dict(vspd=10.0),
        }
        for (cause, phase), reason in fe.BY_DESIGN_POLICY.items():
            kw = dict(self._ORBIT, **cases[cause])
            res = fe.analyze_mission(_mission_log("b4_reentry", phase, 61, **kw), None)
            self.assertEqual(res["idleByCause"], {cause: 60.0}, (cause, phase))
            self.assertEqual(res["recoverable"], 0.0, (cause, phase))
            self.assertGreater(res["byDesign"], 0.0, (cause, phase))
            self.assertAlmostEqual(res["byDesignWall"], 60.0, places=6)
            rows = res["recommendations"]
            self.assertEqual(len(rows), 1, (cause, phase))
            self.assertTrue(rows[0]["byDesign"])
            self.assertEqual(rows[0]["byDesignReason"], reason)
            self.assertEqual(rows[0]["recoverable"], 0.0)
            self.assertEqual(rows[0]["byDesignSeconds"], res["byDesign"])
            self.assertFalse(rows[0]["outcomeSensitive"])
            phase_row = [p for p in res["phases"] if p["phase"] == phase][0]
            self.assertEqual(phase_row["byDesign"], res["byDesign"])
            self.assertEqual(phase_row["recoverable"], 0.0)

    def test_same_cause_elsewhere_stays_recoverable(self):
        kw = dict(self._ORBIT, ap_err=90.0)
        res = fe.analyze_mission(_mission_log("b4_reentry", "CORRECTION-BURN", 61, **kw), None)
        self.assertGreater(res["recoverable"], 0.0)
        self.assertEqual(res["byDesign"], 0.0)
        self.assertFalse(res["recommendations"][0]["byDesign"])

    def test_outcome_sensitive_flag(self):
        kw = dict(self._ORBIT, nodes=1, node_ut="99999")
        res = fe.analyze_mission(_mission_log("b22_jool_orbit", "CIRCULARIZE", 61, **kw), None)
        row = res["recommendations"][0]
        self.assertGreater(row["recoverable"], 0.0)
        self.assertEqual(res["byDesign"], 0.0)
        self.assertTrue(row["outcomeSensitive"])
        self.assertFalse(row["byDesign"])
        self.assertIn("OUTCOME-SENSITIVE: ", row["site"])
        self.assertIn(fe.OUTCOME_SENSITIVE[("waiting-for-node", "CIRCULARIZE")], row["site"])
        other = fe.analyze_mission(_mission_log("b22_jool_orbit", "ORBIT", 61, **kw), None)
        self.assertFalse(other["recommendations"][0]["outcomeSensitive"])
        self.assertNotIn("OUTCOME-SENSITIVE", other["recommendations"][0]["site"])


class PolicyTargetTests(unittest.TestCase):
    """PARK / kx COAST recoverable uses the 4x physics target (contract item 5)."""

    def _rec(self, mission, phase, **kw):
        kw = dict(dict(alt=700000.0, ap=800000.0, pe=650000.0), **kw)
        return fe.analyze_mission(_mission_log(mission, phase, 101, **kw), None)

    def test_park_dwell_targets_4x_physics_for_b5(self):
        res = self._rec("b11_mun_orbit", "PARK")
        self.assertAlmostEqual(res["recoverable"],
                               100.0 - 100.0 / 4.0 - fe.RAMP_SETTLE_OVERHEAD_SECONDS, places=3)
        self.assertTrue(res["runs"][0]["policyTarget"])

    def test_park_of_another_machine_or_unknown_mission_targets_rails(self):
        for mission in ("forge_lko", None):
            res = self._rec(mission, "PARK")
            self.assertAlmostEqual(res["recoverable"],
                                   100.0 - 100.0 / 100000.0 - fe.RAMP_SETTLE_OVERHEAD_SECONDS,
                                   places=3, msg=mission)
            self.assertFalse(res["runs"][0]["policyTarget"])

    def test_kx_coast_targets_4x_physics(self):
        up = self._rec("kx_rewind_watch", "COAST", vspd=50.0)
        self.assertAlmostEqual(up["recoverable"], 100.0 - 25.0 - fe.RAMP_SETTLE_OVERHEAD_SECONDS,
                               places=3)
        down = self._rec("kx_rewind_watch", "COAST", vspd=-50.0, pe=40000.0, alt=80000.0)
        self.assertEqual(list(down["recoverableByCause"]), ["coast-to-entry"])
        self.assertAlmostEqual(down["recoverable"], 100.0 - 25.0 - fe.RAMP_SETTLE_OVERHEAD_SECONDS,
                               places=3)
        other = self._rec("gs1_auto_chute_booster", "COAST", vspd=50.0)
        self.assertGreater(other["recoverable"], 89.0)

    def test_policy_target_rate(self):
        self.assertEqual(fe.policy_target_rate("dwell", "PARK", "b5_decide"), 4.0)
        self.assertIsNone(fe.policy_target_rate("dwell", "PARK", None))
        self.assertIsNone(fe.policy_target_rate("dwell", "PARK", "forge_lko_decide"))
        self.assertIsNone(fe.policy_target_rate("coast-to-periapsis", "COAST", "kxrw_decide"))


def _old_capture_log():
    """A pre-policy CAPTURE-BURN: 600 s of 1x node wait, no node-wait line."""
    return "\n".join(
        ["[Mission][Info][PRELAUNCH] mission start name=b11_mun_orbit",
         _phase("PLAN-CAPTURE", "CAPTURE-BURN", 400.0)]
        + [_tel(phase="CAPTURE-BURN", ut=400.0 + i, nodes=1, node_ut="1000",
                alt=700000.0, ap=800000.0, pe=650000.0) for i in range(601)])


class CaptureLeadTests(unittest.TestCase):
    """The CAPTURE-BURN node wait inside the capture lead is by design."""

    NODE_UT = 1000.0

    def _intervals(self, prefix=(), start=800.0, n=101):
        lines = ["[Mission][Info][PRELAUNCH] mission start name=b11_mun_orbit"]
        lines.append(_phase("PLAN-CAPTURE", "CAPTURE-BURN", start))
        lines += list(prefix)
        lines += [_tel(phase="CAPTURE-BURN", ut=start + i, nodes=1, node_ut=str(self.NODE_UT),
                       alt=700000.0, ap=800000.0, pe=650000.0) for i in range(n)]
        log = fe.parse_mission_log(lines)
        ivs = fe.build_intervals(log)
        return log, ivs, fe.apply_policy(log, ivs)

    def test_lead_seconds(self):
        base = fe.NODE_WAIT_ORIENT_LEAD_SECONDS + fe.NODE_WAIT_ARRIVAL_TOLERANCE_SECONDS
        self.assertEqual(fe.capture_lead_seconds(8.2), (base + 8.2, True))
        self.assertEqual(fe.capture_lead_seconds(float("nan")), (base, False))
        self.assertEqual(fe.capture_lead_seconds(None), (base, False))

    def test_half_burn_from_the_node_wait_lines(self):
        hold = ("[Mission][Info][CAPTURE-BURN] action warp_to_ut value=871.800 "
                "text=node-wait: nodeUt=1000.0 halfBurn=8.2 lead=120")
        log, _ivs, leads = self._intervals(prefix=[hold])
        self.assertAlmostEqual(log.visits[-1].half_burn, 8.2)
        self.assertAlmostEqual(leads[log.visits[-1].index][0], 133.2)
        self.assertTrue(leads[log.visits[-1].index][1])
        declined = ("[Mission][Info][CAPTURE-BURN] action mj_execute_nodes value=none "
                    "text=node-wait declined: warp window 30 s < 60 s (node-wait: "
                    "nodeUt=1000.0 halfBurn=12.5 lead=120)")
        log2, _ivs2, _l = self._intervals(prefix=[declined])
        self.assertAlmostEqual(log2.visits[-1].half_burn, 12.5)
        other = "[Mission][Info][CAPTURE-BURN] gate x halfBurn=99"  # not an action line
        log3, _ivs3, leads3 = self._intervals(prefix=[other])
        self.assertNotEqual(log3.visits[-1].half_burn, log3.visits[-1].half_burn)
        self.assertFalse(leads3[log3.visits[-1].index][1])

    def test_split_at_the_boundary(self):
        # halfBurn 5 -> lead 120 + 5 + 5 = 130 s: ut 870 sits exactly on it.
        hold = ("[Mission][Info][CAPTURE-BURN] action warp_to_ut value=875.000 "
                "text=node-wait: nodeUt=1000.0 halfBurn=5 lead=120")
        _log, ivs, leads = self._intervals(prefix=[hold], start=860.0, n=20)
        self.assertEqual(list(leads.values()), [(130.0, True)])
        by_ut = {iv.ut: iv for iv in ivs}
        self.assertEqual(by_ut[869.0].by_design, "")
        self.assertEqual(by_ut[870.0].by_design, fe.CAPTURE_LEAD_REASON)
        self.assertEqual(by_ut[871.0].by_design, fe.CAPTURE_LEAD_REASON)
        runs = fe.build_runs(ivs, "b5_decide")
        self.assertEqual([(r.by_design, r.last - r.first + 1) for r in runs],
                         [(False, 10), (True, 9)])

    def test_old_run_constants_only(self):
        res = fe.analyze_mission(_old_capture_log(), None)
        row = [p for p in res["phases"] if p["phase"] == "CAPTURE-BURN"][0]
        self.assertEqual(row["captureLeadSeconds"], 125.0)
        self.assertFalse(row["captureLeadHalfBurnKnown"])
        # 600 s of node wait: 475 s before the lead (recoverable), 125 s inside it
        self.assertAlmostEqual(res["byDesignWall"], 125.0, places=6)
        self.assertAlmostEqual(res["recoverable"], 475.0 - fe.RAMP_SETTLE_OVERHEAD_SECONDS,
                               places=2)
        self.assertAlmostEqual(res["byDesign"], 125.0 - fe.RAMP_SETTLE_OVERHEAD_SECONDS,
                               places=2)
        rows = {r["byDesign"]: r for r in res["recommendations"]}
        self.assertEqual(rows[True]["byDesignReason"], fe.CAPTURE_LEAD_REASON)
        self.assertFalse(rows[False]["byDesign"])
        self.assertEqual(res["recommendations"][0]["byDesign"], False)  # counted rows first

    def test_capture_excerpt_lead(self):
        # the trimmed excerpt keeps only a few lines inside the lead
        res = fe.analyze_mission(_read(_CAPTURE), None)
        row = [p for p in res["phases"] if p["phase"] == "CAPTURE-BURN"][0]
        self.assertGreater(row["byDesignWall"], 0.0)
        self.assertGreater(row["recoverable"], 500.0)

    def test_render_has_by_design_sections(self):
        run = fe.analyze_run("2026-10-01_1231_B11-mun-orbit", _b11_result(), None,
                             _old_capture_log(), None, None)
        text = fe.render_run(run)
        self.assertIn("BY DESIGN (not counted", text)
        self.assertIn("capture lead: visit", text)
        self.assertIn("PHYSWARP", text)
        agg = fe.render_aggregate(fe.build_document([run], [], 0))
        self.assertIn("BY DESIGN (not counted", agg)


class PolicyMirrorTests(unittest.TestCase):
    """The mirrored mission warp policy constants agree with mlib."""

    def _mlib(self):
        sys.path.insert(0, os.path.join(_HARNESS, "missions", "lib"))
        try:
            import mlib
        finally:
            sys.path.pop(0)
        return mlib

    def test_constants_match_mlib(self):
        mlib = self._mlib()
        self.assertEqual(fe.NODE_WAIT_ORIENT_LEAD_SECONDS, mlib.NODE_WAIT_ORIENT_LEAD_SECONDS)
        self.assertEqual(fe.NODE_WAIT_ARRIVAL_TOLERANCE_SECONDS,
                         mlib.NODE_WAIT_ARRIVAL_TOLERANCE_SECONDS)
        self.assertEqual(fe.PHYSICS_DWELL_WARP_INDEX, mlib.PHYSICS_DWELL_WARP_INDEX)
        self.assertEqual(fe.POLICY_PHYSICS_RATE, 4.0)
        self.assertEqual(mlib._B5_NODE_WAIT_PHASES, (fe.CAPTURE_LEAD_KEY[1],))

    def test_machine_lead_is_the_analyzer_lead(self):
        mlib = self._mlib()
        half = mlib.half_burn_seconds(277.0, 250000.0, 15000.0)
        target, _why = mlib.node_wait_warp_plan(0.0, 5000.0, 277.0, 250000.0, 15000.0,
                                                float("nan"), 700000.0, 650000.0, 0.0)
        lead, known = fe.capture_lead_seconds(half)
        self.assertTrue(known)
        self.assertAlmostEqual(5000.0 - target + fe.NODE_WAIT_ARRIVAL_TOLERANCE_SECONDS, lead)

    def test_kx_coast_ceiling_is_the_policy_rate(self):
        import types
        mlib = self._mlib()
        self.assertEqual(mlib.kxrw_max_physics_warp(types.SimpleNamespace(phase="COAST")),
                         fe.POLICY_PHYSICS_RATE)
        self.assertEqual(fe.PHYSICS_WARP_RATES[mlib.PHYSICS_DWELL_WARP_INDEX],
                         fe.POLICY_PHYSICS_RATE)


class AggregateByDesignTests(unittest.TestCase):
    def _run(self, rid, recs):
        return {"runId": rid, "scenario": fe.scenario_from_run_id(rid), "totalWallSeconds": 100.0,
                "missionWallSeconds": 80.0, "overhead": {"rows": [], "steps": []},
                "mission": {"sampledWallSeconds": 80.0,
                            "recoverable": sum(r["recoverable"] for r in recs),
                            "byDesign": sum(r["byDesignSeconds"] for r in recs),
                            "byDesignWall": 2.0 * sum(r["byDesignSeconds"] for r in recs),
                            "optionalBurnSaving": 0.0,
                            "buckets": dict({b: 0.0 for b in fe.BUCKETS}, idle=40.0,
                                            physicsWarp=7.0),
                            "recommendations": recs}}

    @staticmethod
    def _rec(phase, cause, rec=0.0, design=0.0):
        return {"phase": phase, "cause": cause, "recoverable": rec, "byDesignSeconds": design,
                "optional": False, "byDesign": design > 0, "outcomeSensitive": False,
                "byDesignReason": "why" if design > 0 else "", "site": "s"}

    def test_by_design_totals_and_distinct_runs(self):
        a = self._run("2026-10-01_1000_B4", [
            self._rec("DEORBIT", "attitude-align", design=100.0),
            self._rec("CORRECTION-BURN", "attitude-align", design=20.0),
            self._rec("CORRECTION-BURN", "attitude-align", rec=5.0)])
        b = self._run("2026-10-01_1100_B4", [
            self._rec("DEORBIT", "attitude-align", design=50.0)])
        agg = fe.aggregate([a, b])
        self.assertEqual(agg["byDesignSeconds"], 170.0)
        self.assertEqual(agg["byDesignWallSeconds"], 340.0)
        self.assertEqual(agg["recoverableSeconds"], 5.0)
        self.assertEqual(agg["physicsWarpSeconds"], 14.0)
        self.assertEqual(agg["causes"], [{"cause": "attitude-align", "recoverable": 5.0, "runs": 1,
                                          "byDesign": 170.0, "byDesignRuns": 2}])
        self.assertEqual(agg["lanes"][0]["byDesign"], 170.0)
        self.assertEqual(agg["lanes"][0]["physicsWarp"], 14.0)
        lc = {(e["phase"], e["byDesign"]): e for e in agg["laneCauses"]}
        self.assertEqual(lc[("DEORBIT", True)]["byDesignSeconds"], 150.0)
        self.assertEqual(lc[("DEORBIT", True)]["runs"], 2)
        self.assertEqual(lc[("CORRECTION-BURN", True)]["runs"], 1)
        self.assertEqual(lc[("CORRECTION-BURN", False)]["recoverable"], 5.0)
        self.assertFalse(agg["laneCauses"][0]["byDesign"])  # counted rows rank first
        text = fe.render_aggregate({"aggregate": agg, "skipped": [], "duplicatesDropped": 0})
        top = text.split("TOP LANE / PHASE / CAUSE")[1].split("BY CAUSE")[0]
        self.assertNotIn("DEORBIT", top)
        self.assertIn("DEORBIT/attitude-align -> why",
                      text.split("BY DESIGN (not counted")[1])


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
