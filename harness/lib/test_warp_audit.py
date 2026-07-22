"""Unit tests for harness/warp_audit.py (the no-1x-coast PR-gate evidence
tool). The pure parsers are pinned against the COMMITTED real pre-fix flight
log ``testdata/2026-07-22_1210_B5-mun-flyby_mission.stdout.log`` (the
seventeenth B5 flight: the finding-14 PLAN-CORRECTION disqualification loop
ran ~277 wall seconds at 1x -- the audit must show exactly that violation),
plus a synthetic post-fix profile that must audit clean.

warp_audit.py lives at the harness root (root-level CLI), so this module
bootstraps the parent directory onto sys.path (same pattern as
test_status.py). Runs under ``python -m unittest discover -s lib -q``.

ASCII only; stdlib only.
"""

import math
import os
import sys
import unittest

_HARNESS_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _HARNESS_DIR not in sys.path:
    sys.path.insert(0, _HARNESS_DIR)

import warp_audit  # noqa: E402

_TESTDATA = os.path.join(os.path.dirname(os.path.abspath(__file__)), "testdata")
_REAL_1210_LOG = os.path.join(
    _TESTDATA, "2026-07-22_1210_B5-mun-flyby_mission.stdout.log")

# Real line shapes (pinned): pre-Phase-2 telemetry (no ut=) from the 1210
# flight, and a post-Phase-2 telemetry line carrying ut=.
OLD_TELEMETRY = (
    "[Mission][VerboseRateLimited][PLAN-CORRECTION] telemetry "
    "ap=19636235.646 pe=75440.669 ecc=0.935 inc=0.814 alt=6014305.234 "
    "vspd=790.249 body=Kerbin nodes=0 nodeDv=nan nodeUt=nan tts=7361.533 "
    "warpTo=nan lf=742.682 thr=0.000 situation=ORBITING warp=NONEx1.000 "
    "apErr=nan")
NEW_TELEMETRY = (
    "[Mission][VerboseRateLimited][COAST-TO-TARGET] telemetry "
    "ap=19636235.646 pe=75440.669 ecc=0.935 inc=0.814 alt=8000000.000 "
    "vspd=700.000 body=Kerbin nodes=0 nodeDv=nan nodeUt=nan tts=150000.0 "
    "warpTo=157000.0 lf=742.682 thr=0.000 situation=ORBITING "
    "warp=RAILSx10000.000 apErr=nan ut=7000.000")


class BucketRateTests(unittest.TestCase):
    def test_canonical_and_ramp_rates(self):
        self.assertEqual(warp_audit.bucket_rate(1.0), 1.0)
        self.assertEqual(warp_audit.bucket_rate(10000.0), 10000.0)
        # Ramp frames join the nearest bucket by log-ratio.
        self.assertEqual(warp_audit.bucket_rate(880.064), 1000.0)
        self.assertEqual(warp_audit.bucket_rate(4.44), 4.0)
        self.assertEqual(warp_audit.bucket_rate(0.0), 1.0)
        self.assertEqual(warp_audit.bucket_rate(float("nan")), 1.0)


class ParseTests(unittest.TestCase):
    def test_parses_old_and_new_telemetry_shapes(self):
        samples, _events = warp_audit.parse_log_lines(
            [OLD_TELEMETRY, NEW_TELEMETRY])
        self.assertEqual(len(samples), 2)
        old, new = samples
        self.assertEqual(old.phase, "PLAN-CORRECTION")
        self.assertEqual(old.mode, "NONE")
        self.assertEqual(old.rate, 1.0)
        self.assertTrue(math.isnan(old.ut))          # pre-Phase-2: no ut=
        self.assertEqual(new.phase, "COAST-TO-TARGET")
        self.assertEqual(new.mode, "RAILS")
        self.assertEqual(new.rate, 10000.0)
        self.assertEqual(new.ut, 7000.0)

    def test_events_capture_phase_action_gate_and_warn(self):
        lines = [
            "[Mission][Info][COAST-TO-TARGET] phase PLAN-CORRECTION -> "
            "COAST-TO-TARGET ut=7739.041 alt=6280389.831 ap=1.0 vsurf=768.9",
            "[Mission][Info][COAST-TO-TARGET] action warp_to_ut value=14946.501",
            "[Mission][Warn][Plan] course-correction dv 169.2 m/s exceeds "
            "cap 150.0; plan removed",
            "[Mission][Info][CORRECTION-BURN] gate corrBurnStarted "
            "False->True | ut=1.0",
            "not a mission line at all",
        ]
        _samples, events = warp_audit.parse_log_lines(lines)
        self.assertEqual(len(events), 4)
        self.assertIn("warp_to_ut", events[1].text)

    def test_segments_fold_on_phase_and_bucket(self):
        lines = []
        for i in range(3):
            lines.append(NEW_TELEMETRY.replace("ut=7000.000",
                                               "ut=%d.000" % (7000 + i * 5000)))
        lines.append(OLD_TELEMETRY)
        samples, _ = warp_audit.parse_log_lines(lines)
        segs = warp_audit.build_segments(samples)
        self.assertEqual(len(segs), 2)
        self.assertEqual(segs[0].samples, 3)
        self.assertEqual(segs[0].label, "RAILSx10000")
        # Game estimate prefers the ut= span when present.
        self.assertEqual(segs[0].game_est, 10000.0)
        # Pre-Phase-2 lines fall back to wall x rate (1x here).
        self.assertEqual(segs[1].label, "1x")
        self.assertEqual(segs[1].game_est, 1.0)


class Real1210FlightTests(unittest.TestCase):
    """The committed pre-fix flight log must show its KNOWN violation: the
    finding-14 PLAN-CORRECTION disqualification loop (~277 wall-s at 1x
    between the over-cap plan removals and the fall-through to the coast),
    and nothing else -- the burn-phase 1x blocks are exempt by design."""

    def _audit(self, min_wall=30.0):
        with open(_REAL_1210_LOG, "r", encoding="utf-8") as f:
            text = f.read()
        return warp_audit.audit_log_text(text, min_wall, "1210")

    def test_known_violation_detected(self):
        report, violations, cumulative = self._audit()
        self.assertEqual(len(violations), 1, report)
        # The cumulative gate (delta-review D1) must ALSO flag the phase.
        self.assertTrue(any(phase == "PLAN-CORRECTION"
                            for phase, _total, _n in cumulative), report)
        v = violations[0]
        self.assertEqual(v.segment.phase, "PLAN-CORRECTION")
        self.assertGreaterEqual(v.segment.wall_est, 250.0)
        self.assertLessEqual(v.segment.wall_est, 320.0)
        # Context names the disqualification loop's surroundings.
        self.assertIsNotNone(v.before)
        self.assertIsNotNone(v.after)
        self.assertIn("COAST-TO-TARGET", v.after.text)
        self.assertIn("VIOLATION PLAN-CORRECTION", report)

    def test_burn_phase_1x_is_exempt(self):
        # The TLI executor's pre-burn alignment sat ~54 wall-s at 1x in
        # TRANSFER-BURN -- a burn phase, exempt from the coast invariant.
        _report, violations, cumulative = self._audit()
        self.assertTrue(all(v.segment.phase != "TRANSFER-BURN"
                            for v in violations))
        self.assertTrue(all(phase != "TRANSFER-BURN"
                            for phase, _total, _n in cumulative))


class PostFixProfileTests(unittest.TestCase):
    def test_clean_profile_has_no_violations(self):
        """A post-fix coast profile (native warp + rails holds, 1x only in
        short burn/arrival windows) audits clean."""
        lines = []

        def tele(phase, mode, rate, ut, n=1):
            for i in range(n):
                lines.append(
                    "[Mission][VerboseRateLimited][%s] telemetry ap=1.0 "
                    "pe=1.0 ecc=0.1 inc=0.1 alt=8000000.0 vspd=700.0 "
                    "body=Kerbin nodes=0 nodeDv=nan nodeUt=nan tts=nan "
                    "warpTo=nan lf=700.0 thr=0.000 situation=ORBITING "
                    "warp=%sx%.3f apErr=nan ut=%.3f"
                    % (phase, mode, rate, ut + i * rate))
        tele("PLAN-CORRECTION", "RAILS", 10.0, 2000.0, n=5)
        tele("CORRECTION-BURN", "PHYSICS", 2.0, 2100.0, n=20)
        tele("CORRECTION-BURN", "NONE", 1.0, 2140.0, n=20)   # burn: exempt
        tele("COAST-TO-TARGET", "RAILS", 10000.0, 2200.0, n=20)
        tele("COAST-TO-TARGET", "RAILS", 100.0, 202200.0, n=10)
        tele("TARGET-FLYBY", "RAILS", 1000.0, 210000.0, n=15)
        tele("TARGET-FLYBY", "RAILS", 100.0, 225000.0, n=10)
        # A SHORT 1x tail at the SOI hand-back: under the 30 s threshold.
        tele("COAST-TO-TARGET", "NONE", 1.0, 226000.0, n=5)
        report, violations, cumulative = warp_audit.audit_log_text(
            "\n".join(lines), 30.0, "postfix")
        self.assertEqual(violations, [], report)
        self.assertEqual(cumulative, [], report)
        self.assertIn("NONE - coast profile is warped end to end.", report)

    def test_fail_threshold_boundary(self):
        lines = []
        for i in range(30):
            lines.append(
                "[Mission][VerboseRateLimited][COAST-TO-TARGET] telemetry "
                "ap=1.0 pe=1.0 ecc=0.1 inc=0.1 alt=%d vspd=1.0 body=Kerbin "
                "nodes=0 nodeDv=nan nodeUt=nan tts=nan warpTo=nan lf=1.0 "
                "thr=0.000 situation=ORBITING warp=NONEx1.000 apErr=nan "
                "ut=%d.0" % (8000000 + i, 100 + i))
        _report, violations, _cum = warp_audit.audit_log_text(
            "\n".join(lines), 30.0, "boundary")
        self.assertEqual(len(violations), 1)   # exactly 30 samples = 30 s
        _report, violations, _cum = warp_audit.audit_log_text(
            "\n".join(lines[:29]), 30.0, "boundary")
        self.assertEqual(violations, [])

    def test_fragmented_sawtooth_trips_the_cumulative_gate(self):
        """Delta-review D1: a 1x/warp sawtooth (the oscillation class the
        operator named) keeps every contiguous 1x block under the threshold
        while the coast spends most of its wall time at 1x -- the CUMULATIVE
        per-phase total must flag it."""
        lines = []

        def tele(mode, rate, ut, n):
            for i in range(n):
                lines.append(
                    "[Mission][VerboseRateLimited][COAST-TO-TARGET] telemetry "
                    "ap=1.0 pe=1.0 ecc=0.1 inc=0.1 alt=8000000.0 vspd=1.0 "
                    "body=Kerbin nodes=0 nodeDv=nan nodeUt=nan tts=nan "
                    "warpTo=nan lf=1.0 thr=0.000 situation=ORBITING "
                    "warp=%sx%.3f apErr=nan ut=%.1f"
                    % (mode, rate, ut + i * rate))
        # Four 20 s 1x blocks separated by single warp blips: no contiguous
        # block reaches 30 s, the phase total is 80 s.
        base = 1000.0
        for k in range(4):
            tele("NONE", 1.0, base, 20)
            base += 20.0
            tele("RAILS", 100.0, base, 1)
            base += 100.0
        report, violations, cumulative = warp_audit.audit_log_text(
            "\n".join(lines), 30.0, "sawtooth")
        self.assertEqual(violations, [], report)   # each block dodges 30 s
        self.assertEqual(len(cumulative), 1, report)
        phase, total, count = cumulative[0]
        self.assertEqual(phase, "COAST-TO-TARGET")
        self.assertGreaterEqual(total, 79.0)
        self.assertEqual(count, 4)
        self.assertIn("CUMULATIVE-VIOLATION COAST-TO-TARGET", report)

    def test_slow_polls_cannot_undercount_a_1x_block(self):
        """Delta-review D2: 25 samples spanning 60 game-s at 1x (polls slower
        than 1 Hz) must still trip the 30 s gate via the game-time span."""
        lines = []
        for i in range(25):
            lines.append(
                "[Mission][VerboseRateLimited][COAST-TO-TARGET] telemetry "
                "ap=1.0 pe=1.0 ecc=0.1 inc=0.1 alt=8000000.0 vspd=1.0 "
                "body=Kerbin nodes=0 nodeDv=nan nodeUt=nan tts=nan "
                "warpTo=nan lf=1.0 thr=0.000 situation=ORBITING "
                "warp=NONEx1.000 apErr=nan ut=%.1f" % (100.0 + i * 2.5))
        _report, violations, cumulative = warp_audit.audit_log_text(
            "\n".join(lines), 30.0, "slowpoll")
        self.assertEqual(len(violations), 1)   # 60 game-s at 1x = 60 wall-s
        self.assertEqual(len(cumulative), 1)


if __name__ == "__main__":
    unittest.main()
