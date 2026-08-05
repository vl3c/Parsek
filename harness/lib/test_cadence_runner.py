"""Pure tests for the cadence runner (`harness/tools/cadence_runner.py`).

The runner is the one-command tier entry point (invoked on demand; schedulable
but not scheduled). Everything it DECIDES is a module-level pure function, and
this file covers those: the outcome classifier (every outcome, its precedence
rules, and the KILLED call-out), the summary.txt verdict tally against real line
shapes, the history-line formatter, the runner-log rotation selector, and the
advisory lock preflight composed over ``provlib.acquire_lock`` with fake lock
payloads.

No subprocess, no KSP, no network. The pure cells touch no filesystem at all;
the two shell-seam groups are bounded -- ``build_run_argv`` only reads an env
var, and the TeeLog / error-path cells write into a temp dir with an injected
console writer.

Runnable with the stdlib runner only (NO pytest, NO KSP, NO network)::

    cd harness && python -m unittest discover -s lib -q
"""

import contextlib
import io
import os
import shutil
import sys
import tempfile
import unittest
from datetime import datetime, timezone

_HERE = os.path.dirname(os.path.abspath(__file__))
_TOOLS = os.path.join(os.path.dirname(_HERE), "tools")
_PROVISION = os.path.join(os.path.dirname(_HERE), "provision")
for _p in (_HERE, _TOOLS, _PROVISION):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import cadence_runner as cr  # noqa: E402
import provlib  # noqa: E402


def _summary(verdict, scenario="B1-pad-hop", attempt=1, wall=1234, note=None):
    """A real summary.txt line, byte-shaped like run.py's writer:
    ``"%s %s %s attempt=%d wall=%ds%s"``."""
    return "2026-08-05T01:02:03Z %s %s attempt=%d wall=%ds%s" % (
        verdict, scenario, attempt, wall, (" note=%s" % note) if note else "")


ADMIT_DRIFT_LINE = ("[Harness][Info][Admit] admit instance=stock-minimal "
                    "manifest=present result=DRIFT")
ADMIT_FIELD_LINE = ("[Harness][Warn][Admit] admit drift field=parsekDllSha256 "
                    "expected=abc actual=def")
LOCK_REFUSAL_LINE = ("[Harness][Error][Lock] refusing selection 'tier:nightly': machine "
                     "lock held by a live sibling run or provision. If you are certain "
                     "no run is active, delete C:/x/automation/.ksp-machine.lock")
LOCK_MIDRUN_LINE = ("[Harness][Error][Lock] lost the machine lock mid-selection; a "
                    "sibling has reclaimed it. Stopping after 3 scenario(s) rather "
                    "than running unlocked.")


class TallyVerdictsTests(unittest.TestCase):
    def test_counts_every_known_verdict_from_real_line_shapes(self):
        lines = [
            _summary("PASS"),
            _summary("PASS", scenario="M1-mun-flyby", wall=4210),
            _summary("PARSEK-FAIL", scenario="S4.1-rewind-merge"),
            _summary("KILLED", scenario="V1-map-dwell", wall=7200),
            _summary("EXPECTED-FAIL", scenario="B12-foo"),
            _summary("XPASS", scenario="B12-foo"),
            _summary("INVALID", scenario="L1-passive-sandbox", wall=3),
        ]
        self.assertEqual(cr.tally_verdicts(lines), {
            "PASS": 2, "PARSEK-FAIL": 1, "KILLED": 1,
            "EXPECTED-FAIL": 1, "XPASS": 1, "INVALID": 1})

    def test_a_note_suffix_does_not_disturb_the_verdict_column(self):
        lines = [_summary("INVALID", attempt=2, note="flakedThenPassed"),
                 _summary("PASS", note="machine lock lost mid-selection after 3 scenario(s)")]
        self.assertEqual(cr.tally_verdicts(lines), {"INVALID": 1, "PASS": 1})

    def test_blank_lines_are_ignored_and_garbage_counts_as_unparsed(self):
        lines = ["", "   ", "this is not a summary line at all", _summary("PASS")]
        counts = cr.tally_verdicts(lines)
        self.assertEqual(counts["PASS"], 1)
        self.assertEqual(counts[cr.UNPARSED_VERDICT], 1)

    def test_a_one_token_line_is_unparsed_not_a_crash(self):
        self.assertEqual(cr.tally_verdicts(["PASS"]), {cr.UNPARSED_VERDICT: 1})

    def test_empty_input_is_an_empty_tally(self):
        self.assertEqual(cr.tally_verdicts([]), {})


class FormatTallyTests(unittest.TestCase):
    def test_known_verdicts_render_in_the_canonical_order(self):
        counts = {"KILLED": 1, "PASS": 20, "PARSEK-FAIL": 2}
        self.assertEqual(cr.format_tally(counts), "PASS=20 PARSEK-FAIL=2 KILLED=1")

    def test_unknown_keys_sort_after_the_known_ones(self):
        counts = {"PASS": 1, "ZZZ": 1, cr.UNPARSED_VERDICT: 2}
        self.assertEqual(cr.format_tally(counts), "PASS=1 UNPARSED=2 ZZZ=1")

    def test_empty_tally_says_no_results(self):
        self.assertEqual(cr.format_tally({}), "no-results")

    def test_zero_counts_are_not_rendered(self):
        self.assertEqual(cr.format_tally({"PASS": 3, "KILLED": 0}), "PASS=3")


class ClassifyOutcomeTests(unittest.TestCase):
    def test_green_on_exit_zero_with_results(self):
        out = cr.classify_outcome(0, [_summary("PASS"), _summary("EXPECTED-FAIL")], [])
        self.assertEqual(out.outcome, cr.OUTCOME_GREEN)
        self.assertEqual(out.exit_code, 0)
        self.assertIn("PASS=1", out.detail)
        self.assertIn("EXPECTED-FAIL=1", out.detail)

    def test_exit_two_is_no_selection(self):
        out = cr.classify_outcome(2, [], [])
        self.assertEqual(out.outcome, cr.OUTCOME_NO_SELECTION)
        self.assertEqual(out.exit_code, 1)
        self.assertIn("no scenario selection", out.detail)

    def test_exit_zero_with_no_result_lines_is_no_selection_not_green(self):
        """The whole point of the check: 'exited 0' and 'flew something' are
        different claims. This is also the --dry-run smoke shape."""
        out = cr.classify_outcome(0, [], ["=== end plan ==="])
        self.assertEqual(out.outcome, cr.OUTCOME_NO_SELECTION)
        self.assertEqual(out.exit_code, 1)
        self.assertIn("no result line", out.detail)

    def test_red_on_exit_one_with_a_finding(self):
        out = cr.classify_outcome(1, [_summary("PASS"), _summary("PARSEK-FAIL")], [])
        self.assertEqual(out.outcome, cr.OUTCOME_RED)
        self.assertEqual(out.exit_code, 1)
        self.assertEqual(out.detail, "PASS=1 PARSEK-FAIL=1")

    def test_red_calls_out_a_killed_explicitly(self):
        lines = [_summary("PASS")] * 20 + [_summary("KILLED", scenario="V1-map-dwell")]
        out = cr.classify_outcome(1, lines, [])
        self.assertEqual(out.outcome, cr.OUTCOME_RED)
        self.assertIn("PASS=20", out.detail)
        self.assertIn("KILLED=1", out.detail)
        self.assertIn("budget kill", out.detail)

    def test_xpass_is_red_amber_not_green(self):
        out = cr.classify_outcome(1, [_summary("XPASS", scenario="B12-foo")], [])
        self.assertEqual(out.outcome, cr.OUTCOME_RED)
        self.assertEqual(out.exit_code, 1)

    def test_needs_provision_on_admit_drift_evidence(self):
        out = cr.classify_outcome(1, [_summary("INVALID")], [ADMIT_DRIFT_LINE, ADMIT_FIELD_LINE])
        self.assertEqual(out.outcome, cr.OUTCOME_NEEDS_PROVISION)
        self.assertEqual(out.exit_code, 2)
        self.assertIn("provision is a HUMAN call", out.detail)
        self.assertIn("provision/provision.py --profile stock-minimal", out.detail)
        self.assertIn("INVALID=1", out.detail)   # the tally still prints

    def test_a_mission_venv_drift_is_not_needs_provision(self):
        """run.py also emits `mission venv-admit stamp=... result=DRIFT` for a
        VENV drift (run.py, Mission step). provision.py does not fix a venv --
        missions/bootstrap_venv.py does -- so that line must never classify
        NEEDS-PROVISION with its provision hint. It reds honestly instead."""
        venv_line = ("[Harness][Info][Mission] mission venv-admit "
                     "stamp=C:/x/harness/missions/.venv/.stamp result=DRIFT")
        self.assertFalse(cr.is_admission_drift_line(venv_line))
        self.assertFalse(cr.is_evidence_line(venv_line))
        out = cr.classify_outcome(1, [_summary("INVALID")], [venv_line])
        self.assertEqual(out.outcome, cr.OUTCOME_RED)
        self.assertNotIn("provision is a HUMAN call", out.detail)

    def test_the_instance_admit_drift_line_matches_by_co_occurrence(self):
        """The instance admit summary is recognised by `admit instance=` +
        `result=DRIFT` together; either alone is not drift evidence."""
        self.assertTrue(cr.is_admission_drift_line(ADMIT_DRIFT_LINE))
        ok_line = ("[Harness][Info][Admit] admit instance=stock-minimal "
                   "manifest=present result=OK")
        self.assertFalse(cr.is_admission_drift_line(ok_line))

    def test_needs_provision_on_a_manifest_missing_subkind_in_a_summary_note(self):
        out = cr.classify_outcome(1, [_summary("INVALID", note="manifest-missing")], [])
        self.assertEqual(out.outcome, cr.OUTCOME_NEEDS_PROVISION)

    def test_needs_provision_on_a_provision_incomplete_subkind(self):
        out = cr.classify_outcome(1, [_summary("INVALID", note="provision-incomplete")], [])
        self.assertEqual(out.outcome, cr.OUTCOME_NEEDS_PROVISION)

    def test_needs_provision_outranks_generic_red(self):
        """Precedence 4: an unfit instance is a different human action than a
        failing test, and its INVALID rows are not product findings."""
        out = cr.classify_outcome(1, [_summary("INVALID")] * 22, [ADMIT_DRIFT_LINE])
        self.assertEqual(out.outcome, cr.OUTCOME_NEEDS_PROVISION)
        self.assertNotEqual(out.outcome, cr.OUTCOME_RED)

    def test_locked_on_the_whole_invocation_refusal(self):
        out = cr.classify_outcome(1, [_summary("INVALID")] * 46, [LOCK_REFUSAL_LINE])
        self.assertEqual(out.outcome, cr.OUTCOME_LOCKED)
        self.assertEqual(out.exit_code, 3)
        self.assertIn("lost the preflight race", out.detail)

    def test_locked_outranks_needs_provision_when_both_markers_somehow_appear(self):
        """Precedence 3: a refused invocation never reached ADMIT, so drift
        evidence on that path cannot be genuine."""
        out = cr.classify_outcome(1, [_summary("INVALID")],
                                  [LOCK_REFUSAL_LINE, ADMIT_DRIFT_LINE])
        self.assertEqual(out.outcome, cr.OUTCOME_LOCKED)

    def test_a_midselection_lock_loss_after_real_flights_is_red_not_locked(self):
        """The all-INVALID guard. LOCKED's exit code means 'nothing attempted';
        applying it to a batch that flew three scenarios would bury whatever
        those three found."""
        lines = [_summary("PASS"), _summary("PARSEK-FAIL"), _summary("PASS")] + \
                [_summary("INVALID")] * 40
        out = cr.classify_outcome(1, lines, [LOCK_MIDRUN_LINE])
        self.assertEqual(out.outcome, cr.OUTCOME_RED)
        self.assertIn("PARSEK-FAIL=1", out.detail)

    def test_a_midselection_loss_before_anything_flew_is_locked(self):
        out = cr.classify_outcome(1, [_summary("INVALID")] * 46, [LOCK_MIDRUN_LINE])
        self.assertEqual(out.outcome, cr.OUTCOME_LOCKED)

    def test_an_unexpected_nonzero_exit_still_classifies_red(self):
        out = cr.classify_outcome(9, [_summary("PASS")], [])
        self.assertEqual(out.outcome, cr.OUTCOME_RED)

    def test_every_outcome_maps_to_a_documented_exit_code(self):
        for outcome in (cr.OUTCOME_GREEN, cr.OUTCOME_RED, cr.OUTCOME_NO_SELECTION,
                        cr.OUTCOME_NEEDS_PROVISION, cr.OUTCOME_LOCKED,
                        cr.OUTCOME_SKIPPED, cr.OUTCOME_ERROR):
            self.assertIn(outcome, cr.OUTCOME_EXIT_CODES)
            self.assertIn(cr.OUTCOME_EXIT_CODES[outcome], (0, 1, 2, 3, 4))

    def test_an_unknown_outcome_label_falls_back_to_the_error_code(self):
        self.assertEqual(cr.CadenceOutcome("WAT", "").exit_code, cr.EXIT_ERROR)


class EvidenceLineTests(unittest.TestCase):
    def test_admit_and_lock_lines_are_evidence(self):
        for line in (ADMIT_DRIFT_LINE, ADMIT_FIELD_LINE, LOCK_REFUSAL_LINE, LOCK_MIDRUN_LINE):
            self.assertTrue(cr.is_evidence_line(line), line)

    def test_ordinary_run_chatter_is_not_retained(self):
        for line in ("[Harness][Info][Admit] admit instance=stock-minimal "
                     "manifest=present result=OK",
                     "[Harness][Info][Select] select expr='tier:daily' -> 22 scenarios",
                     "[Harness][Info][Result] result written results/x.json",
                     ""):
            self.assertFalse(cr.is_evidence_line(line), line)


class HistoryLineTests(unittest.TestCase):
    def test_shape(self):
        line = cr.format_history_line("2026-08-05T04:31:07Z", "nightly", cr.OUTCOME_GREEN,
                                      0, "PASS=46", "nightly_2026-08-04_230000.log")
        self.assertEqual(line,
                         "2026-08-05T04:31:07Z tier=nightly outcome=GREEN exit=0 "
                         "PASS=46 log=nightly_2026-08-04_230000.log")

    def test_detail_newlines_are_collapsed_so_one_invocation_is_one_line(self):
        line = cr.format_history_line("2026-08-05T04:31:07Z", "daily", cr.OUTCOME_ERROR,
                                      4, "boom\n  Traceback\n   File x", "daily_x.log")
        self.assertNotIn("\n", line)
        self.assertIn("boom Traceback File x", line)

    def test_empty_detail_renders_a_placeholder_not_a_double_space(self):
        line = cr.format_history_line("T", "daily", cr.OUTCOME_SKIPPED, 3, "", "d.log")
        self.assertIn("exit=3 - log=d.log", line)

    def test_a_history_line_is_greppable_by_outcome_and_tier(self):
        line = cr.format_history_line("T", "operator", cr.OUTCOME_NEEDS_PROVISION, 2,
                                      "INVALID=6", "operator_x.log")
        self.assertIn("tier=operator", line)
        self.assertIn("outcome=NEEDS-PROVISION", line)


class CadenceLogStampTests(unittest.TestCase):
    def test_parses_a_runner_log_name(self):
        stamp = cr.parse_cadence_log_stamp("nightly_2026-08-04_230501.log")
        self.assertEqual(stamp, datetime(2026, 8, 4, 23, 5, 1, tzinfo=timezone.utc))

    def test_rejects_history_txt_and_foreign_names(self):
        for name in ("history.txt", "index.html", "2026-08-04_harness.log",
                     "summary.txt", "weekly_2026-08-04_230501.log",
                     "nightly_2026-08-04.log", "nightly.log", ""):
            self.assertIsNone(cr.parse_cadence_log_stamp(name), name)

    def test_rejects_an_unparseable_stamp(self):
        self.assertIsNone(cr.parse_cadence_log_stamp("daily_2026-13-45_999999.log"))


class SelectStaleCadenceLogsTests(unittest.TestCase):
    NOW = datetime(2026, 8, 5, 12, 0, 0, tzinfo=timezone.utc)

    def test_older_than_keep_days_is_selected_oldest_first(self):
        names = ["daily_2026-01-01_220000.log", "nightly_2026-03-01_230000.log",
                 "daily_2026-08-04_220000.log"]
        self.assertEqual(cr.select_stale_cadence_logs(names, self.NOW, keep_days=60),
                         ["daily_2026-01-01_220000.log", "nightly_2026-03-01_230000.log"])

    def test_the_boundary_day_is_kept(self):
        exactly = "daily_2026-06-06_120000.log"   # exactly 60 days before NOW
        self.assertEqual(cr.select_stale_cadence_logs([exactly], self.NOW, keep_days=60), [])

    def test_one_second_past_the_boundary_is_stale(self):
        just_over = "daily_2026-06-06_115959.log"
        self.assertEqual(cr.select_stale_cadence_logs([just_over], self.NOW, keep_days=60),
                         [just_over])

    def test_a_future_stamp_is_never_deleted(self):
        """Clock skew (or a wake-timer machine whose RTC jumped) must not eat
        evidence."""
        self.assertEqual(
            cr.select_stale_cadence_logs(["daily_2030-01-01_120000.log"], self.NOW), [])

    def test_never_selects_history_or_anything_it_cannot_identify(self):
        names = ["history.txt", "index.html", "2020-01-01_harness.log",
                 "notes_2020-01-01_120000.log", "daily_2020-01-01_120000.log.bak"]
        self.assertEqual(cr.select_stale_cadence_logs(names, self.NOW), [])

    def test_empty_directory_is_empty_selection(self):
        self.assertEqual(cr.select_stale_cadence_logs([], self.NOW), [])


class LockPreflightTests(unittest.TestCase):
    """The preflight composed over provlib.acquire_lock. Fake payloads only --
    the lease/liveness rules are provlib's and are tested there; what is proven
    here is that the runner asks the RIGHT question and reads the answer."""

    NOW = 1_000_000.0

    def test_no_lock_proceeds(self):
        proceed, reason = cr.evaluate_lock_preflight(None, 4242, self.NOW,
                                                     lambda pid: True)
        self.assertTrue(proceed)
        self.assertEqual(reason, "acquired-free")

    def test_live_holder_within_lease_skips(self):
        lock = {"pid": 999, "timestamp": self.NOW - 60, "worktree": "C:/x/Parsek-foo",
                "selection": "tier:nightly", "startedIso": "2026-08-05T00:00:00Z"}
        proceed, reason = cr.evaluate_lock_preflight(lock, 4242, self.NOW,
                                                     lambda pid: True)
        self.assertFalse(proceed)
        self.assertEqual(reason, "refused-live")

    def test_dead_holder_proceeds(self):
        lock = {"pid": 999, "timestamp": self.NOW - 60}
        proceed, reason = cr.evaluate_lock_preflight(lock, 4242, self.NOW,
                                                     lambda pid: False)
        self.assertTrue(proceed)
        self.assertEqual(reason, "reclaimed-stale")

    def test_expired_lease_under_a_live_holder_proceeds(self):
        lock = {"pid": 999, "timestamp": self.NOW - (provlib.DEFAULT_LEASE_SECONDS + 1)}
        proceed, _ = cr.evaluate_lock_preflight(lock, 4242, self.NOW, lambda pid: True)
        self.assertTrue(proceed)

    def test_the_default_lease_is_provlibs_not_a_local_copy(self):
        """A locally re-declared lease would drift from the authority the two
        real shells use."""
        lock = {"pid": 999, "timestamp": self.NOW - (provlib.DEFAULT_LEASE_SECONDS - 1)}
        proceed, _ = cr.evaluate_lock_preflight(lock, 4242, self.NOW, lambda pid: True)
        self.assertFalse(proceed)

    def test_a_probe_that_fails_closed_is_honoured_as_a_skip(self):
        """pid_alive fails CLOSED (reports alive), so an unprobeable holder
        makes the cadence skip rather than queue behind it."""
        lock = {"pid": 999, "timestamp": self.NOW}
        proceed, _ = cr.evaluate_lock_preflight(lock, 4242, self.NOW, lambda pid: True)
        self.assertFalse(proceed)

    def test_our_own_pid_is_reentrant(self):
        lock = {"pid": 4242, "timestamp": self.NOW}
        proceed, _ = cr.evaluate_lock_preflight(lock, 4242, self.NOW, lambda pid: True)
        self.assertTrue(proceed)

    def test_describe_holder_renders_the_skip_message_fields(self):
        lock = {"pid": 999, "worktree": "C:/x/Parsek-foo", "selection": "tier:nightly",
                "startedIso": "2026-08-05T00:00:00Z"}
        from machinelock import describe_holder
        text = describe_holder(lock)
        for token in ("pid=999", "worktree=C:/x/Parsek-foo", "selection=tier:nightly",
                      "since=2026-08-05T00:00:00Z"):
            self.assertIn(token, text)


class TierVocabularyTests(unittest.TestCase):
    def test_only_the_three_cadence_tiers_are_offered(self):
        self.assertEqual(cr.TIER_CHOICES, ("daily", "nightly", "operator"))

    def test_every_offered_tier_is_a_real_harness_tier(self):
        import hlib
        for tier in cr.TIER_CHOICES:
            self.assertIn(tier, hlib.TIERS)

    def test_a_typo_tier_is_rejected_by_argparse_rather_than_flying_nothing(self):
        # stderr is swallowed: argparse's usage dump is the EXPECTED behaviour
        # here, and letting it print would make a passing suite look broken.
        with contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                cr.main(["--tier", "nightlyy"])

    def test_a_missing_tier_is_rejected(self):
        with contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                cr.main([])


class _FailingConsole:
    """A console writer that dies after ``ok_lines`` successful writes -- the
    broken-pipe / closed-stdout shape, injected rather than monkeypatched so the
    cell exercises the REAL TeeLog code path."""

    def __init__(self, exc, ok_lines=0):
        self.exc = exc
        self.ok_lines = ok_lines
        self.written = []
        self.calls = 0

    def __call__(self, text):
        self.calls += 1
        if self.calls > self.ok_lines:
            raise self.exc
        self.written.append(text)


class TeeLogConsoleLossTests(unittest.TestCase):
    """Losing the console must DOWNGRADE the run, never end it: the dated log is
    the record of truth, and a runner started and walked away from lives behind
    a pipe (an agent's, a wrapper's, a terminal that gets closed) that can die
    under it at any moment."""

    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="parsek-cadence-teelog-")
        self.path = os.path.join(self.dir, "daily_2026-08-05_120000.log")

    def tearDown(self):
        shutil.rmtree(self.dir, ignore_errors=True)

    def _read(self):
        with open(self.path, "r", encoding="utf-8") as fh:
            return fh.read().splitlines()

    def test_a_dead_console_does_not_raise_and_the_file_keeps_every_line(self):
        console = _FailingConsole(OSError(22, "The pipe is being closed"))
        log = cr.TeeLog(self.path, console_write=console)
        log.raw("first")
        log.info("Invoke", "second")
        log.raw("third")
        log.close()
        lines = self._read()
        self.assertIn("first", lines)
        self.assertIn("[Cadence][Info][Invoke] second", lines)
        self.assertIn("third", lines)

    def test_the_console_loss_warn_lands_in_the_file_and_never_on_the_console(self):
        console = _FailingConsole(OSError("broken pipe"))
        log = cr.TeeLog(self.path, console_write=console)
        log.raw("first")
        log.close()
        lines = self._read()
        self.assertTrue(any(l.startswith("[Cadence][Warn][Log] console lost") for l in lines),
                        lines)
        self.assertTrue(any("continuing file-only" in l for l in lines), lines)
        self.assertEqual(console.written, [])   # nothing ever reached the console

    def test_the_warn_precedes_the_line_that_failed(self):
        console = _FailingConsole(OSError("broken pipe"))
        log = cr.TeeLog(self.path, console_write=console)
        log.raw("the line that broke it")
        log.close()
        lines = self._read()
        self.assertLess(next(i for i, l in enumerate(lines) if "console lost" in l),
                        lines.index("the line that broke it"))

    def test_the_console_is_latched_off_and_never_retried(self):
        """A broken pipe stays broken; retrying per line would raise on every
        subsequent write and re-enter the same failure for hours."""
        console = _FailingConsole(OSError("broken pipe"))
        log = cr.TeeLog(self.path, console_write=console)
        for i in range(25):
            log.raw("line %d" % i)
        log.close()
        self.assertTrue(log.console_dead)
        self.assertEqual(console.calls, 1)
        self.assertEqual(len([l for l in self._read() if "console lost" in l]), 1)

    def test_a_closed_stdout_valueerror_is_treated_the_same_as_a_broken_pipe(self):
        console = _FailingConsole(ValueError("I/O operation on closed file"))
        log = cr.TeeLog(self.path, console_write=console)
        log.raw("after close")
        log.close()
        self.assertTrue(log.console_dead)
        self.assertIn("after close", self._read())

    def test_lines_written_before_the_loss_reach_both_surfaces(self):
        console = _FailingConsole(OSError("broken pipe"), ok_lines=2)
        log = cr.TeeLog(self.path, console_write=console)
        log.raw("alpha")
        log.raw("beta")
        log.raw("gamma")
        log.close()
        self.assertEqual(console.written, ["alpha", "beta"])
        for line in ("alpha", "beta", "gamma"):
            self.assertIn(line, self._read())

    def test_losing_the_file_too_still_does_not_raise(self):
        """Both surfaces gone is not a reason to kill a flight for a LOG."""
        console = _FailingConsole(OSError("broken pipe"))
        log = cr.TeeLog(self.path, console_write=console)
        log._fh.close()          # simulate the handle dying underneath us
        log.raw("into the void")  # must not raise
        log.raw("still not raising")
        log.close()

    def test_a_healthy_console_is_untouched_by_any_of_this(self):
        seen = []
        log = cr.TeeLog(self.path, console_write=seen.append)
        log.info("Start", "hello")
        log.close()
        self.assertEqual(seen, ["[Cadence][Info][Start] hello"])
        self.assertFalse(log.console_dead)
        self.assertEqual(self._read(), ["[Cadence][Info][Start] hello"])


class MainErrorPathTests(unittest.TestCase):
    """The internal-error handler must leave a history line even when REPORTING
    the fault fails. Announcing a problem is itself I/O and can die; a handler
    that dies mid-announcement leaves no trace of the problem at all."""

    def setUp(self):
        self.order = []
        self._saved = (cr.append_history, cr.run, cr.TeeLog)

        def fake_append_history(line, log=None):
            self.order.append(("history", line))

        def boom(*a, **kw):
            raise RuntimeError("something went wrong deep inside")

        order = self.order

        class DeadLogger:
            def __init__(self, path, console_write=None):
                pass

            def error(self, step, msg):
                order.append(("log", msg))
                raise OSError("broken pipe")

            def close(self):
                pass

        cr.append_history = fake_append_history
        cr.run = boom
        cr.TeeLog = DeadLogger

    def tearDown(self):
        cr.append_history, cr.run, cr.TeeLog = self._saved

    def test_history_is_appended_even_when_reporting_the_fault_raises(self):
        self.assertEqual(cr.main(["--tier", "daily"]), cr.EXIT_ERROR)
        kinds = [k for k, _ in self.order]
        self.assertIn("history", kinds)

    def test_history_is_appended_before_the_report_attempt(self):
        cr.main(["--tier", "nightly"])
        kinds = [k for k, _ in self.order]
        self.assertEqual(kinds.index("history"), 0)
        self.assertIn("log", kinds)   # the report WAS attempted, it just died

    def test_the_history_line_names_the_error_outcome_and_carries_the_traceback(self):
        cr.main(["--tier", "operator"])
        line = next(v for k, v in self.order if k == "history")
        self.assertIn("outcome=ERROR", line)
        self.assertIn("exit=4", line)
        self.assertIn("tier=operator", line)
        self.assertIn("something went wrong deep inside", line)


class BuildRunArgvTests(unittest.TestCase):
    """The one shell-seam cell: reads an env var, touches nothing else."""

    def setUp(self):
        self._saved = os.environ.get("PARSEK_CADENCE_RUNPY_ARGS")
        os.environ.pop("PARSEK_CADENCE_RUNPY_ARGS", None)

    def tearDown(self):
        os.environ.pop("PARSEK_CADENCE_RUNPY_ARGS", None)
        if self._saved is not None:
            os.environ["PARSEK_CADENCE_RUNPY_ARGS"] = self._saved

    def test_default_invocation_is_run_py_tier(self):
        argv = cr.build_run_argv("nightly")
        self.assertEqual(argv[0], sys.executable)
        self.assertTrue(argv[1].endswith("run.py"))
        self.assertEqual(argv[2:], ["--tier", "nightly"])

    def test_the_test_seam_appends_and_never_removes_the_tier(self):
        os.environ["PARSEK_CADENCE_RUNPY_ARGS"] = "--dry-run --no-coverage"
        argv = cr.build_run_argv("daily")
        self.assertEqual(argv[2:], ["--tier", "daily", "--dry-run", "--no-coverage"])

    def test_a_blank_seam_value_changes_nothing(self):
        os.environ["PARSEK_CADENCE_RUNPY_ARGS"] = "   "
        self.assertEqual(cr.build_run_argv("operator")[2:], ["--tier", "operator"])


if __name__ == "__main__":
    unittest.main()
