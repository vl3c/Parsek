"""Tests for endingsq.py, the `[expectations.recordings.endings]` block of the saveParse
row: the mirror parse, the two-body altitude, the per-chain facets, the spec surface,
the evaluator and the route through `saveparse.evaluate_save_structure` and the hlib
validator."""

import math
import os
import sys
import unittest

LIB_DIR = os.path.dirname(os.path.abspath(__file__))
if LIB_DIR not in sys.path:
    sys.path.insert(0, LIB_DIR)

import endingsq  # noqa: E402
import hlib  # noqa: E402
import samplingq  # noqa: E402
import saveparse  # noqa: E402

KERBIN_GM, KERBIN_R = endingsq.BODY_GM_AND_RADIUS["Kerbin"]


def _point(ut, alt=1000.0):
    return ("POINT\n{\nut = %r\nlat = 0\nlon = 0\nalt = %r\nbody = Kerbin\n"
            "velX = 0\nvelY = 0\nvelZ = 0\n}\n" % (ut, alt))


def _seg(start, end, sma, ecc, mna, epoch, predicted, body="Kerbin"):
    return ("ORBIT_SEGMENT\n{\nstartUT = %r\nendUT = %r\ninc = 0\necc = %r\nsma = %r\n"
            "lan = 0\nargPe = 0\nmna = %r\nepoch = %r\nbody = %s\nisPredicted = %s\n}\n"
            % (start, end, ecc, sma, mna, epoch, body, "True" if predicted else "False"))


def _section(ref, body, start, end):
    return ("TRACK_SECTION\n{\nenv = 0\nref = %s\nstartUT = %r\nendUT = %r\n%s}\n"
            % (ref, start, end, body))


def _mirror(rid, body):
    return "version = 1\nrecordingId = %s\n%s" % (rid, body)


def _rec(rid, name, terminal=None, chain="", explicit_end=None, predicted=False,
         quiet=False, orbit_body=""):
    lines = ["RECORDING", "{", "recordingId = %s" % rid, "vesselName = %s" % name]
    if terminal is not None:
        lines.append("terminalState = %d" % saveparse.TERMINAL_STATE_NAMES.index(terminal))
    if predicted:
        lines.append("terminalPredicted = True")
    if quiet:
        lines.append("quietEnd = True")
    if orbit_body:
        lines.append("tOrbBody = %s" % orbit_body)
    if chain:
        lines.append("chainId = %s" % chain)
    if explicit_end is not None:
        lines.append("explicitEndUT = %r" % explicit_end)
    lines.append("}")
    return "\n".join(lines) + "\n"


def _save(*recs):
    return ("GAME\n{\nSCENARIO\n{\nname = ParsekScenario\nRECORDING_TREE\n{\nid = t1\n"
            "rootRecordingId = r1\n%s}\n}\n}\n" % "".join(recs))


def _impact_segment(start, impact_alt=0.0):
    """A suborbital Kerbin arc (apoapsis 80 km, periapsis radius 200 km) whose
    endUT is the instant its radius falls to KERBIN_R + impact_alt."""
    ra, rp = KERBIN_R + 80000.0, 200000.0
    a, e = (ra + rp) / 2.0, (ra - rp) / (ra + rp)
    n = math.sqrt(KERBIN_GM / a ** 3)
    r_target = KERBIN_R + impact_alt
    # Outbound from apoapsis (M = pi) toward periapsis: E where r = a (1 - e cos E).
    ea = 2.0 * math.pi - math.acos((1.0 - r_target / a) / e)
    m_end = ea - e * math.sin(ea)
    epoch = start
    t_end = start + (m_end - math.pi) / n
    return a, e, math.pi, epoch, t_end


class MirrorAndAltitudeTests(unittest.TestCase):

    def test_real_samples_exclude_checkpoint_frames(self):
        text = _mirror("r1", _section("0", _point(10.0) + _point(20.0), 10.0, 20.0)
                       + _section("2", _point(90.0) + _seg(20.0, 95.0, 700000.0, 0.01,
                                                           0.0, 20.0, True), 20.0, 95.0))
        mf = endingsq.parse_mirror(text)
        self.assertEqual(20.0, mf.last_real_sample_ut)
        self.assertEqual(95.0, mf.max_ut)
        self.assertEqual(1, len(mf.segments))
        self.assertTrue(mf.segments[0].predicted)

    def test_a_predicted_section_is_not_a_real_sample(self):
        # A chute descent Parsek predicted to touchdown (src = 3): its points end the
        # recording but are not watched, so the tail past the last real sample is measured.
        predicted = _section("0", _point(20.0) + _point(90.0, alt=70.0), 20.0, 90.0)
        predicted = predicted.replace("ref = 0\n", "ref = 0\nsrc = 3\n")
        text = _mirror("r1", _section("0", _point(10.0) + _point(20.0), 10.0, 20.0) + predicted)
        mf = endingsq.parse_mirror(text)
        self.assertEqual(20.0, mf.last_real_sample_ut)
        self.assertEqual(90.0, mf.max_ut)

    def test_the_predicted_source_is_the_csharp_enum_value(self):
        path = os.path.join(LIB_DIR, "..", "..", "Source", "Parsek", "Trajectory", "TrackSection.cs")
        with open(path, encoding="utf-8") as fh:
            text = fh.read()
        self.assertIn("Predicted = " + endingsq.PREDICTED_SECTION_SRC, text)
        # The ending flags the endings block reads are the keys the C# codec writes.
        codec = os.path.join(LIB_DIR, "..", "..", "Source", "Parsek", "Recording",
                             "RecordingTreeRecordCodec.cs")
        with open(codec, encoding="utf-8") as fh:
            ctext = fh.read()
        for key in ("terminalPredicted", "quietEnd", "tOrbBody"):
            self.assertIn('recNode.AddValue("%s"' % key, ctext)

    def test_flat_write_points_count_and_duplicate_segments_collapse(self):
        seg = _seg(20.0, 95.0, 700000.0, 0.01, 0.0, 20.0, True)
        text = _mirror("r1", _point(5.0) + _point(15.0) + seg
                       + _section("2", seg, 20.0, 95.0))
        mf = endingsq.parse_mirror(text)
        self.assertEqual(15.0, mf.last_real_sample_ut)
        self.assertEqual(1, len(mf.segments))

    def test_radius_at_epoch_periapsis_and_apoapsis(self):
        a, e = 700000.0, 0.1
        s = endingsq.OrbitSeg(0.0, 0.0, a, e, 0.0, 0.0, "Kerbin", True)
        self.assertAlmostEqual(a * (1 - e), endingsq.radius_at_ut(s, 0.0), places=3)
        s2 = endingsq.OrbitSeg(0.0, 0.0, a, e, math.pi, 0.0, "Kerbin", True)
        self.assertAlmostEqual(a * (1 + e), endingsq.radius_at_ut(s2, 0.0), places=3)
        hyp = endingsq.OrbitSeg(0.0, 0.0, -2500000.0, 1.2, 0.0, 0.0, "Mun", True)
        self.assertAlmostEqual(500000.0, endingsq.radius_at_ut(hyp, 0.0), places=3)

    def test_altitude_of_an_impact_arc_is_the_ground_and_unknown_body_is_none(self):
        a, e, mna, epoch, t_end = _impact_segment(100.0)
        s = endingsq.OrbitSeg(100.0, t_end, a, e, mna, epoch, "Kerbin", True)
        self.assertAlmostEqual(0.0, endingsq.altitude_at_end(s), delta=1.0)
        # The same arc cut at the atmosphere top reads 70 km, which is the bug shape.
        a2, e2, m2, ep2, t70 = _impact_segment(100.0, impact_alt=70000.0)
        s70 = endingsq.OrbitSeg(100.0, t70, a2, e2, m2, ep2, "Kerbin", True)
        self.assertAlmostEqual(70000.0, endingsq.altitude_at_end(s70), delta=1.0)
        self.assertIsNone(endingsq.altitude_at_end(
            endingsq.OrbitSeg(0.0, 1.0, a, e, 0.0, 0.0, "Planet X", True)))


class FacetTests(unittest.TestCase):

    def _setup(self, impact_alt=0.0, predicted=True):
        a, e, mna, epoch, t_end = _impact_segment(200.0, impact_alt)
        head = _mirror("h1", _section("0", _point(100.0) + _point(150.0), 100.0, 150.0))
        tip = _mirror("t1", _section("2", _seg(200.0, t_end, a, e, mna, epoch, predicted),
                                     200.0, t_end))
        save = _save(_rec("h1", "Kerbal X Probe", chain="c1"),
                     _rec("t1", "Kerbal X Probe", terminal="Destroyed", chain="c1",
                          explicit_end=t_end),
                     _rec("d1", "Kerbal X Debris", terminal="Destroyed"))
        snap = saveparse.parse_parsek_scenario(save)
        data = samplingq.SamplingInput(prec_texts={"h1": head, "t1": tip})
        return snap, data, t_end

    def test_chain_facets_measure_tail_over_the_head(self):
        snap, data, t_end = self._setup()
        block = {"vessels": {"Kerbal X Probe": {"ended": 1}}}
        f = endingsq.observed_endings_facets(block, snap, data)
        v = f["vessels"]["Kerbal X Probe"]
        self.assertEqual(2, v["recordings"])
        self.assertEqual(1, v["ended"])
        e = v["endings"][0]
        self.assertEqual("Destroyed", e["terminal"])
        self.assertEqual(2, e["chainMembers"])
        self.assertEqual(1, e["predictedSegments"])
        self.assertEqual(150.0, e["lastWatchedUT"])
        self.assertAlmostEqual(t_end - 150.0, e["tailSeconds"], delta=0.1)
        self.assertAlmostEqual(0.0, e["endAltitudeMeters"], delta=1.0)
        self.assertEqual(0, e["missingMirrors"])

    def test_evaluate_passes_the_ground_ending_and_reds_the_mid_air_one(self):
        block = {"gating": True, "vessels": {"Kerbal X Probe": {
            "ended": {"min": 1, "max": 1}, "terminalStates": ["Destroyed"],
            "predictedSegments": {"min": 1}, "tailSeconds": {"min": 60},
            "endAltitudeMeters": {"min": -500, "max": 5000}}}}
        snap, data, _ = self._setup()
        self.assertEqual([], endingsq.evaluate_endings(
            block, endingsq.observed_endings_facets(block, snap, data)))
        snap2, data2, _ = self._setup(impact_alt=70000.0)
        mm = endingsq.evaluate_endings(
            block, endingsq.observed_endings_facets(block, snap2, data2))
        self.assertTrue(any("endAltitudeMeters 70000 > max 5000" in m for m in mm), mm)
        snap3, data3, _ = self._setup(predicted=False)
        mm3 = endingsq.evaluate_endings(
            block, endingsq.observed_endings_facets(block, snap3, data3))
        self.assertTrue(any("predictedSegments 0 < min 1" in m for m in mm3), mm3)
        self.assertTrue(any("endAltitudeMeters: not measured" in m for m in mm3), mm3)

    def test_a_non_predicted_segment_counts_as_watched(self):
        """An on-rails background vessel is watched through its orbit segments: the
        tail runs from the END of the last non-predicted segment, not from the last
        physics sample before it went on rails."""
        a, e, mna, epoch, t_end = _impact_segment(300.0)
        mirror = _mirror("t1", _section("0", _point(100.0) + _point(150.0), 100.0, 150.0)
                         + _section("2", _seg(150.0, 300.0, 700000.0, 0.01, 0.0, 150.0, False)
                                    + _seg(300.0, t_end, a, e, mna, epoch, True),
                                    150.0, t_end))
        snap = saveparse.parse_parsek_scenario(
            _save(_rec("t1", "Core", terminal="Destroyed", explicit_end=t_end)))
        block = {"vessels": {"Core": {"tailSeconds": {"min": 1}}}}
        f = endingsq.observed_endings_facets(
            block, snap, samplingq.SamplingInput(prec_texts={"t1": mirror}))
        got = f["vessels"]["Core"]["endings"][0]
        self.assertEqual(300.0, got["lastWatchedUT"])
        self.assertAlmostEqual(t_end - 300.0, got["tailSeconds"], delta=0.1)
        self.assertEqual(1, got["predictedSegments"])

    def test_wrong_terminal_and_missing_mirror_are_mismatches(self):
        snap, data, _ = self._setup()
        block = {"vessels": {"Kerbal X Debris": {"terminalStates": ["Landed"],
                                                 "tailSeconds": {"min": 1}}}}
        mm = endingsq.evaluate_endings(
            block, endingsq.observed_endings_facets(block, snap, data))
        self.assertTrue(any("terminal Destroyed not in ['Landed']" in m for m in mm), mm)
        self.assertTrue(any("carry no .prec.txt mirror" in m for m in mm), mm)

    def test_absent_vessel_counts_zero_ended(self):
        snap, data, _ = self._setup()
        block = {"vessels": {"Nobody": {"ended": {"min": 1}}}}
        mm = endingsq.evaluate_endings(
            block, endingsq.observed_endings_facets(block, snap, data))
        self.assertTrue(any("Nobody.ended 0 < min 1" in m for m in mm), mm)

    def test_ending_flags_and_terminal_orbit_body(self):
        snap = saveparse.parse_parsek_scenario(_save(
            _rec("q1", "Booster", terminal="Destroyed", predicted=True, quiet=True),
            _rec("s1", "Escape", terminal="Orbiting", orbit_body="Sun"),
            _rec("k1", "Kerbin Stage", terminal="Orbiting", orbit_body="Kerbin")))
        data = samplingq.SamplingInput(prec_texts={})
        block = {"vessels": {
            "Booster": {"quietEnd": True, "terminalPredicted": True},
            "Escape": {"terminalOrbitBodies": ["Sun"], "quietEnd": False},
            "Kerbin Stage": {"terminalOrbitBodies": ["Sun"]}}}
        f = endingsq.observed_endings_facets(block, snap, data)
        b = f["vessels"]["Booster"]["endings"][0]
        self.assertTrue(b["quietEnd"])
        self.assertTrue(b["terminalPredicted"])
        self.assertIsNone(b["terminalOrbitBody"])
        self.assertEqual("Sun", f["vessels"]["Escape"]["endings"][0]["terminalOrbitBody"])
        mm = endingsq.evaluate_endings(block, f)
        self.assertEqual(1, len(mm), mm)
        self.assertIn("Kerbin Stage[k1].terminalOrbitBody Kerbin not in ['Sun']", mm[0])
        # The flags red when they differ, and a missing orbit reads (none).
        snap2 = saveparse.parse_parsek_scenario(_save(
            _rec("q2", "Booster", terminal="Destroyed"),
            _rec("s2", "Escape", terminal="SubOrbital")))
        mm2 = endingsq.evaluate_endings(
            block, endingsq.observed_endings_facets(block, snap2, data))
        self.assertTrue(any("Booster[q2].quietEnd False != True" in m for m in mm2), mm2)
        self.assertTrue(any("Booster[q2].terminalPredicted False != True" in m for m in mm2), mm2)
        self.assertTrue(any("Escape[s2].terminalOrbitBody (none) not in ['Sun']" in m
                            for m in mm2), mm2)

    def test_unreadable_save_is_not_measured(self):
        self.assertEqual({}, endingsq.observed_endings_facets({"vessels": {"x": {}}}, None, None))
        self.assertTrue(endingsq.evaluate_endings({"vessels": {}}, {}))


class SpecSurfaceTests(unittest.TestCase):

    def test_validator(self):
        v = endingsq.validate_endings_expectations
        self.assertEqual([], v(None))
        self.assertEqual([], v({"vessels": {"A": {"ended": 1, "terminalStates": ["Destroyed"],
                                                  "endAltitudeMeters": {"min": -500}}}}))
        self.assertTrue(any("unknown key" in e for e in v({"vesels": {}})))
        self.assertTrue(any("unknown key" in e for e in v({"vessels": {"A": {"tail": 1}}})))
        self.assertTrue(any("unknown name" in e
                            for e in v({"vessels": {"A": {"terminalStates": ["Crashed"]}}})))
        self.assertTrue(any("non-empty array" in e
                            for e in v({"vessels": {"A": {"terminalStates": "Destroyed"}}})))
        self.assertTrue(any("non-negative" in e
                            for e in v({"vessels": {"A": {"tailSeconds": {"min": -1}}}})))
        self.assertTrue(any("gates nothing" in e for e in v({"gating": True})))
        self.assertTrue(any("can never red" in e for e in v(
            {"gating": True, "vessels": {"A": {"tailSeconds": {"min": 0}}}})))
        self.assertTrue(any("must be a bool" in e for e in v({"gating": "yes"})))
        self.assertEqual([], v({"gating": True, "vessels": {"A": {
            "quietEnd": True, "terminalPredicted": False, "terminalOrbitBodies": ["Sun"]}}}))
        self.assertTrue(any("quietEnd: 'yes' must be a bool" in e
                            for e in v({"vessels": {"A": {"quietEnd": "yes"}}})))
        self.assertTrue(any("non-empty array of body names" in e
                            for e in v({"vessels": {"A": {"terminalOrbitBodies": []}}})))
        self.assertTrue(any("non-empty array of body names" in e
                            for e in v({"vessels": {"A": {"terminalOrbitBodies": "Sun"}}})))

    def test_hlib_validates_and_warns(self):
        exp = {"recordings": {"endings": {"vessels": {"A": {"tail": 1}}}}}
        # validate_spec is the public entry; the endings errors ride on it.
        res = hlib.validate_spec({"expectations": exp}, {})
        self.assertTrue(any(e.startswith("expectations.recordings.endings")
                            for e in res.errors), res.errors)
        warns = saveparse.save_structure_expectation_warnings(
            {"recordings": {"endings": {"vessels": {"A": {}}}}})
        self.assertTrue(any("recordings.endings" in w for w in warns), warns)


class SaveParseRouteTests(unittest.TestCase):

    def test_block_routes_through_evaluate_save_structure(self):
        a, e, mna, epoch, t_end = _impact_segment(200.0)
        tip = _mirror("t1", _section("0", _point(150.0) + _point(160.0), 150.0, 160.0)
                      + _section("2", _seg(200.0, t_end, a, e, mna, epoch, True),
                                 200.0, t_end))
        snap = saveparse.parse_parsek_scenario(
            _save(_rec("t1", "Stage", terminal="Destroyed", explicit_end=t_end)))
        armed = {"recordings": {"endings": {"gating": True, "vessels": {"Stage": {
            "terminalStates": ["Destroyed"], "tailSeconds": {"min": 60}}}}}}
        data = samplingq.SamplingInput(prec_texts={"t1": tip})
        r = saveparse.evaluate_save_structure(armed, snap, data)
        self.assertEqual(("recordings.endings",), r.blocks)
        self.assertEqual(("recordings.endings",), r.armed_blocks)
        self.assertEqual(saveparse.STATUS_PASS, r.status, r.mismatches)
        self.assertIn("endings", r.observed["recordings"])
        # Unarmed and failing: REPORT with the mismatch recorded.
        unarmed = {"recordings": {"endings": {"vessels": {"Stage": {
            "terminalStates": ["Landed"]}}}}}
        r2 = saveparse.evaluate_save_structure(unarmed, snap, data)
        self.assertEqual(saveparse.STATUS_REPORT, r2.status)
        self.assertTrue(r2.mismatches)
        # A save with no ParsekScenario node is a defined mismatch.
        r3 = saveparse.evaluate_save_structure(
            armed, saveparse.parse_parsek_scenario("GAME\n{\n}\n"), data)
        self.assertEqual(saveparse.STATUS_FAIL, r3.status)

    def test_recording_row_reads_explicit_end_and_chain(self):
        snap = saveparse.parse_parsek_scenario(
            _save(_rec("t1", "S", terminal="Landed", chain="abc", explicit_end=12.5)))
        row = snap.trees[0].recordings[0]
        self.assertEqual(12.5, row.explicit_end_ut)
        self.assertEqual("abc", row.chain_id)
        snap2 = saveparse.parse_parsek_scenario(_save(_rec("t2", "S")))
        self.assertIsNone(snap2.trees[0].recordings[0].explicit_end_ut)
        self.assertEqual("", snap2.trees[0].recordings[0].chain_id)
        r2 = snap2.trees[0].recordings[0]
        self.assertFalse(r2.terminal_predicted)
        self.assertFalse(r2.quiet_end)
        self.assertEqual("", r2.terminal_orbit_body)
        snap3 = saveparse.parse_parsek_scenario(_save(_rec(
            "t3", "S", terminal="Orbiting", predicted=True, quiet=True, orbit_body="Sun")))
        r3 = snap3.trees[0].recordings[0]
        self.assertTrue(r3.terminal_predicted)
        self.assertTrue(r3.quiet_end)
        self.assertEqual("Sun", r3.terminal_orbit_body)


if __name__ == "__main__":
    unittest.main()
