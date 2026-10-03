"""Unit tests for samplingq.py, the recording-sampling quality measures behind the
saveParse row's ``[expectations.recordings.sampling]`` block.

Runnable with the stdlib runner only::

    python -m unittest discover -s harness/lib

The corpus is SYNTHETIC sidecar text authored against the readable-mirror writer
(``RecordingStore`` / the trajectory codec's ``.prec.txt`` shape: TRACK_SECTION with
env / ref / optional src / startUT / endUT and POINT children carrying ut, lat, lon,
alt, body, velX..velZ) and synthetic recorder close lines authored against
``SectionWarpRuns.Format``. Each negative control seeds exactly one defect into an
otherwise clean recording: a gap, a duplicate UT, a backstep, an over-wide spacing
for the declared density, a too-dense spacing for the declared density, a missed
direction trigger, a missed speed trigger, and a teleport.

Two source-sync guards read OUTSIDE harness/: ``DensityPresetSourceSyncTests`` reads
``Source/Parsek/ParsekSettings.cs`` (the four preset getters) and
``WarpRunTokenSourceSyncTests`` reads ``Source/Parsek/SectionWarpRuns.cs`` and
``Source/Parsek/FlightRecorder.cs`` (the run kinds and the ``warpRuns=`` token the
parser anchors on). Both strip comments before matching.
"""

import math
import os
import re
import unittest

import samplingq
import saveparse

LIB_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(os.path.dirname(LIB_DIR))

KERBIN_R = 600000.0


def _point(ut, lon_deg, alt=100000.0, vel=(2000.0, 0.0, 0.0), body="Kerbin"):
    return ("\tPOINT\n\t{\n\t\tut = %r\n\t\tlat = 0\n\t\tlon = %r\n\t\talt = %r\n"
            "\t\tbody = %s\n\t\tvelX = %r\n\t\tvelY = %r\n\t\tvelZ = %r\n\t}\n"
            % (ut, lon_deg, alt, body, vel[0], vel[1], vel[2]))


def _section(points, env=2, ref=0, src=None, start=None, end=None):
    head = "TRACK_SECTION\n{\n\tenv = %d\n\tref = %d\n" % (env, ref)
    if src is not None:
        head += "\tsrc = %d\n" % src
    uts = [p[0] for p in points]
    head += "\tstartUT = %r\n\tendUT = %r\n" % (start if start is not None else uts[0],
                                                end if end is not None else uts[-1])
    return head + "".join(_point(*p) for p in points) + "}\n"


def _straight(uts, speed=2000.0, alt=100000.0):
    """Points moving east along the equator at a constant speed: positions agree
    with the velocity, so the continuity measure reads them as continuous."""
    out = []
    for ut in uts:
        lon = math.degrees(speed * (ut - uts[0]) / (KERBIN_R + alt))
        out.append((ut, lon, alt, (speed, 0.0, 0.0)))
    return out


def _prec(*sections, rid="rec0"):
    return "version = 1\nrecordingId = %s\n" % rid + "".join(sections)


def _close_line(runs):
    return ("[LOG 12:00:00.000] [Parsek][INFO][Recorder] TrackSection closed: env=ExoBallistic "
            "ref=Absolute frames=10 checkpoints=0 duration=1.00s avgGap=0.100s maxGap=0.100s "
            "largeGaps=0 warpRuns=%s" % runs)


def _measure(prec_text, density="medium", log=""):
    data = samplingq.SamplingInput(prec_texts={"rec0": prec_text}, log_text=log)
    return samplingq.observed_sampling_facets({"density": density}, data)


def _spaced(start, n, gap):
    return [start + i * gap for i in range(n)]


class ParseTests(unittest.TestCase):
    def test_prec_sections_and_scope(self):
        text = _prec(_section(_straight(_spaced(10.0, 4, 3.0))),
                     _section(_straight(_spaced(30.0, 4, 1.0)), src=1),
                     _section(_straight(_spaced(40.0, 4, 1.0)), ref=1))
        parsed = samplingq.parse_prec_text(text)
        self.assertEqual("", parsed.error)
        self.assertEqual("rec0", parsed.recording_id)
        self.assertEqual(3, len(parsed.sections))
        self.assertEqual([True, False, False], [s.in_scope for s in parsed.sections])
        self.assertEqual(4, len(parsed.sections[0].points))
        self.assertAlmostEqual(2000.0, parsed.sections[0].points[0].vel[0])

    def test_warp_runs_parse_the_recorder_token_only(self):
        log = "\n".join([
            _close_line("1x:52:83.140-142.460,phys:23:142.500-212.180:max=2.000"),
            # A background close line and a quoted echo never count.
            "[Parsek][INFO][BgRecorder] TrackSection closed: env=X warpRuns=phys:9:1.000-2.000:max=4.000",
            "note: TrackSection closed: warpRuns=phys:9:1.000-2.000:max=4.000",
            _close_line("-"),
        ])
        runs, lines = samplingq.parse_warp_runs(log)
        self.assertEqual(2, lines)
        self.assertEqual(2, len(runs))
        self.assertEqual("phys", runs[1].kind)
        self.assertEqual(23, runs[1].count)
        self.assertAlmostEqual(2.0, runs[1].max_rate)
        self.assertEqual(((), 0), samplingq.parse_warp_runs(None))

    def test_classify_pair(self):
        runs = (samplingq.WarpRun("1x", 3, 10.0, 20.0, 1.0),
                samplingq.WarpRun("phys", 3, 20.04, 30.0, 4.0))
        self.assertEqual(("1x", 1.0), samplingq.classify_pair(11.0, 12.0, runs))
        self.assertEqual(("phys", 4.0), samplingq.classify_pair(21.0, 22.0, runs))
        self.assertEqual(("mixed", 4.0), samplingq.classify_pair(20.0, 20.04, runs))
        self.assertEqual("unknown", samplingq.classify_pair(40.0, 41.0, runs)[0])


class MeasureTests(unittest.TestCase):
    def test_clean_backstop_coast_reads_clean_at_1x_and_under_physics_warp(self):
        # Medium max 3.0 s: 1x gaps of 3.00, then 4x physics gaps of 3.04 (the
        # first 0.08 s frame past the backstop).
        uts = _spaced(100.0, 6, 3.0) + _spaced(118.04, 6, 3.04)
        log = _close_line("1x:6:100.000-115.000,phys:6:118.040-133.240:max=4.000")
        f = _measure(_prec(_section(_straight(uts))), "medium", log)
        self.assertEqual(0, sum(f["totals"][k] for k in samplingq.SAMPLING_TOTAL_WINDOW_KEYS))
        self.assertEqual(5, f["1x"]["gaps"])
        self.assertEqual(5, f["phys"]["gaps"])
        self.assertAlmostEqual(3.04, f["phys"]["maxGap"])
        self.assertEqual(1, f["mixed"]["gaps"])
        self.assertEqual(1, f["warpCloseLines"])
        self.assertAlmostEqual(4.0, f["physRunMaxRate"])

    def test_one_frame_allowance_follows_the_rate(self):
        # 3.04 s is within max + one frame at 4x (0.08) but NOT at 1x (0.02).
        uts = [100.0, 103.04]
        f1 = _measure(_prec(_section(_straight(uts))), "medium",
                      _close_line("1x:2:100.000-103.040"))
        f4 = _measure(_prec(_section(_straight(uts))), "medium",
                      _close_line("phys:2:100.000-103.040:max=4.000"))
        self.assertEqual(1, f1["totals"]["overMax"])
        self.assertEqual(0, f4["totals"]["overMax"])

    def test_seeded_gap_is_over_max(self):
        uts = _spaced(100.0, 5, 3.0) + [112.0 + 7.0]
        f = _measure(_prec(_section(_straight(uts))))
        self.assertEqual(1, f["totals"]["overMax"])
        self.assertTrue(any(x.startswith("overMax") for x in f["findings"]))

    def test_seeded_duplicate_inside_a_section_counts_but_across_sections_does_not(self):
        pts = _straight(_spaced(100.0, 5, 3.0))
        dup = _prec(_section(pts[:3] + [pts[2]] + pts[3:]))
        f = _measure(dup)
        self.assertEqual(1, f["totals"]["duplicates"])
        # Two sections sharing their boundary seed point: legitimate.
        shared = _prec(_section(pts[:3]), _section(pts[2:]))
        self.assertEqual(0, _measure(shared)["totals"]["duplicates"])

    def test_warp_duplicates_count_only_warp_touching_pairs(self):
        pts = _straight(_spaced(100.0, 6, 3.04))
        text = _prec(_section(pts[:3] + [pts[2]] + pts[3:]))
        f1 = _measure(text, log=_close_line("1x:7:100.000-115.200"))
        self.assertEqual(1, f1["totals"]["duplicates"])
        self.assertEqual(0, samplingq.measured_value(f1, "warpDuplicates"))
        f4 = _measure(text, log=_close_line("phys:7:100.000-115.200:max=4.000"))
        self.assertEqual(1, samplingq.measured_value(f4, "warpDuplicates"))

    def test_seeded_backstep(self):
        pts = _straight(_spaced(100.0, 5, 3.0))
        f = _measure(_prec(_section([pts[0], pts[2], pts[1], pts[3]])))
        self.assertEqual(1, f["totals"]["backsteps"])

    def test_wrong_density_declared_denser_reads_over_max(self):
        # A Medium backstop coast (3 s gaps) declared High (max 1 s).
        f = _measure(_prec(_section(_straight(_spaced(100.0, 8, 3.0)))), "high")
        self.assertEqual(7, f["totals"]["overMax"])

    def test_wrong_density_declared_sparser_reads_sub_min(self):
        # A High-density run (0.06 s gaps) declared Low (min 0.5 s).
        f = _measure(_prec(_section(_straight(_spaced(100.0, 30, 0.06)))), "low")
        self.assertEqual(29, f["totals"]["subMin"])

    def test_missed_direction_trigger(self):
        # A 10 degree turn across a 2.9 s gap at Medium (threshold 2 deg): the
        # trigger should have fired long before.
        a = (100.0, 0.0, 100000.0, (2000.0, 0.0, 0.0))
        ang = math.radians(10.0)
        b = (102.9, math.degrees(2000.0 * 2.9 / (KERBIN_R + 100000.0)), 100000.0,
             (2000.0 * math.cos(ang), 0.0, 2000.0 * math.sin(ang)))
        f = _measure(_prec(_section([a, b])))
        self.assertEqual(1, f["totals"]["dirMisses"])

    def test_floor_limited_trigger_is_not_a_miss(self):
        # A big turn sampled at min + one frame is the floor working, not a miss.
        a = (100.0, 0.0, 100000.0, (2000.0, 0.0, 0.0))
        ang = math.radians(10.0)
        b = (100.22, math.degrees(2000.0 * 0.22 / (KERBIN_R + 100000.0)), 100000.0,
             (2000.0 * math.cos(ang), 0.0, 2000.0 * math.sin(ang)))
        f = _measure(_prec(_section([a, b])))
        self.assertEqual(0, f["totals"]["dirMisses"])
        self.assertEqual(1, f["totals"]["triggered"])

    def test_missed_speed_trigger(self):
        a = (100.0, 0.0, 100000.0, (2000.0, 0.0, 0.0))
        b = (102.9, math.degrees(2100.0 * 2.9 / (KERBIN_R + 100000.0)), 100000.0,
             (2400.0, 0.0, 0.0))
        f = _measure(_prec(_section([a, b])))
        self.assertEqual(1, f["totals"]["speedMisses"])

    def test_teleport_is_a_jump(self):
        pts = _straight(_spaced(100.0, 4, 1.0))
        ut, lon, alt, vel = pts[2]
        pts[2] = (ut, lon + 1.0, alt, vel)  # ~12 km off in one second at 2 km/s
        f = _measure(_prec(_section(pts)))
        self.assertGreaterEqual(f["totals"]["jumps"], 1)

    def test_unknown_body_skips_jump_measure(self):
        pts = [(100.0, 0.0, 1000.0, (10.0, 0, 0), "Nowhere"),
               (101.0, 0.0, 1000.0, (10.0, 0, 0), "Nowhere")]
        text = ("version = 1\nTRACK_SECTION\n{\n\tenv = 2\n\tref = 0\n\tstartUT = 100\n"
                "\tendUT = 101\n" + "".join(_point(*p) for p in pts) + "}\n")
        f = _measure(text)
        self.assertEqual(1, f["1x"]["jumpUnmeasured"] + f["unknown"]["jumpUnmeasured"])

    def test_bad_density_measures_nothing(self):
        self.assertEqual({}, samplingq.observed_sampling_facets({"density": "ultra"}, None))
        self.assertEqual({}, samplingq.observed_sampling_facets(None, None))


class SpecSurfaceTests(unittest.TestCase):
    def test_validator(self):
        v = samplingq.validate_sampling_expectations
        self.assertEqual([], v(None))
        self.assertEqual([], v({"density": "high", "overMax": 0}))
        self.assertTrue(any("density" in e for e in v({"overMax": 0})))
        self.assertTrue(any("unknown key" in e for e in v({"density": "low", "bogus": 1})))
        self.assertTrue(any("no assertion key" in e for e in v({"density": "low", "gating": True})))
        self.assertTrue(any("can never red" in e
                            for e in v({"density": "low", "gating": True, "physGaps": {"min": 0}})))
        self.assertTrue(v({"density": "low", "overMax": -1}))

    def test_evaluate_defined_mismatches(self):
        block = {"density": "medium", "physGaps": {"min": 1}}
        f = _measure(_prec(_section(_straight(_spaced(100.0, 3, 3.0)))), "medium", log="")
        mm = samplingq.evaluate_sampling(block, f)
        self.assertTrue(any("warpRuns" in m for m in mm))
        self.assertTrue(any("physGaps 0 < min 1" in m for m in mm))
        data = samplingq.SamplingInput(prec_texts={}, missing_mirrors=2)
        f2 = samplingq.observed_sampling_facets(block, data)
        mm2 = samplingq.evaluate_sampling(block, f2)
        self.assertTrue(any("no .prec.txt" in m for m in mm2))
        self.assertTrue(any("no active absolute" in m for m in mm2))

    def test_routes_through_the_save_parse_row(self):
        snapshot = saveparse.parse_parsek_scenario(
            "GAME\n{\n\tSCENARIO\n\t{\n\t\tname = ParsekScenario\n\t}\n}\n")
        clean = _prec(_section(_straight(_spaced(100.0, 6, 3.04))))
        log = _close_line("phys:6:100.000-115.200:max=4.000")
        armed = {"recordings": {"sampling": {
            "gating": True, "density": "medium", "overMax": 0, "duplicates": 0,
            "physGaps": {"min": 5}}}}
        data = samplingq.SamplingInput(prec_texts={"r": clean}, log_text=log)
        r = saveparse.evaluate_save_structure(armed, snapshot, data)
        self.assertEqual(("recordings.sampling",), r.blocks)
        self.assertEqual(("recordings.sampling",), r.armed_blocks)
        self.assertEqual(saveparse.STATUS_PASS, r.status, r.mismatches)
        self.assertEqual(5, r.observed["recordings"]["sampling"]["phys"]["gaps"])
        # The same block over a seeded over-wide gap FAILs.
        bad = _prec(_section(_straight([100.0, 103.04, 110.0])))
        r2 = saveparse.evaluate_save_structure(
            armed, snapshot, samplingq.SamplingInput(prec_texts={"r": bad},
                                                     log_text=_close_line("phys:3:100.000-110.000:max=4.000")))
        self.assertEqual(saveparse.STATUS_FAIL, r2.status)
        self.assertTrue(any("overMax 1 != 0" in m for m in r2.armed_mismatches))
        # Unarmed: report-only.
        unarmed = {"recordings": {"sampling": {"density": "medium", "overMax": 0}}}
        r3 = saveparse.evaluate_save_structure(
            unarmed, snapshot, samplingq.SamplingInput(prec_texts={"r": bad}))
        self.assertEqual(saveparse.STATUS_REPORT, r3.status)
        self.assertTrue(r3.mismatches)


class RunReaderTests(unittest.TestCase):
    """run.read_sampling_input: the thin I/O half (mirrors read, a sidecar with no
    mirror counted, a missing Recordings dir read as nothing)."""

    def test_reads_mirrors_and_counts_missing_ones(self):
        import sys
        import tempfile
        harness_root = os.path.dirname(LIB_DIR)
        if harness_root not in sys.path:
            sys.path.insert(0, harness_root)
        import run
        with tempfile.TemporaryDirectory() as tmp:
            rec = os.path.join(tmp, "Parsek", "Recordings")
            os.makedirs(rec)
            for name, body in (("a.prec", "bin"), ("a.prec.txt", "recordingId = a"),
                               ("b.prec", "bin"), ("a_vessel.craft", "x")):
                with open(os.path.join(rec, name), "w", encoding="utf-8") as fh:
                    fh.write(body)
            data = run.read_sampling_input(tmp, "LOG")
            self.assertEqual({"a": "recordingId = a"}, data.prec_texts)
            self.assertEqual(1, data.missing_mirrors)
            self.assertEqual("LOG", data.log_text)
            empty = run.read_sampling_input(os.path.join(tmp, "nope"), None)
            self.assertEqual({}, empty.prec_texts)
            self.assertEqual(0, empty.missing_mirrors)


def _strip_cs_comments(text):
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return re.sub(r"//[^\n]*", "", text)


def _read(*rel):
    with open(os.path.join(REPO_ROOT, *rel), "r", encoding="utf-8") as fh:
        return _strip_cs_comments(fh.read().replace("\r\n", "\n"))


class DensityPresetSourceSyncTests(unittest.TestCase):
    """DENSITY_PRESETS must equal the four ParsekSettings preset getters."""

    GETTERS = ("GetMinSampleInterval", "GetMaxSampleInterval",
               "GetVelocityDirThreshold", "GetSpeedChangeThreshold")

    def _getter(self, src, name):
        m = re.search(
            r"internal static float %s\(SamplingDensity level\) =>\s*"
            r"level == SamplingDensity\.Low \? ([0-9.]+)f\s*"
            r": level == SamplingDensity\.High \? ([0-9.]+)f\s*"
            r": ([0-9.]+)f;" % name, src)
        self.assertIsNotNone(m, "could not read %s from ParsekSettings.cs" % name)
        return {"low": float(m.group(1)), "high": float(m.group(2)), "medium": float(m.group(3))}

    def test_presets_match_parsek_settings(self):
        src = _read("Source", "Parsek", "ParsekSettings.cs")
        values = [self._getter(src, g) for g in self.GETTERS]
        for density, preset in samplingq.DENSITY_PRESETS.items():
            self.assertEqual(tuple(v[density] for v in values), preset, density)


class WarpRunTokenSourceSyncTests(unittest.TestCase):
    def test_run_kinds_and_token_match_the_recorder(self):
        runs_src = _read("Source", "Parsek", "SectionWarpRuns.cs")
        for const, kind in (("KindNormal", "1x"), ("KindPhysics", "phys"), ("KindRails", "rails")):
            self.assertRegex(runs_src, r'internal const string %s = "%s";' % (const, re.escape(kind)))
        self.assertIn('.Append(":max=")', runs_src)
        rec_src = _read("Source", "Parsek", "FlightRecorder.cs")
        self.assertRegex(rec_src, r'\$"warpRuns=\{SectionWarpRuns\.Format\(')
        self.assertIn('ParsekLog.Info("Recorder",\n                $"TrackSection closed: ', rec_src)
        self.assertAlmostEqual(0.02, samplingq.PHYSICS_FRAME_SECONDS)


if __name__ == "__main__":
    unittest.main()
