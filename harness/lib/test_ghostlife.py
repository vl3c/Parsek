"""Unit tests for ghostlife.py, the pure ghost-mesh lifecycle verifier core.

Runnable with the stdlib runner only (NO pytest, NO KSP, NO network)::

    python -m unittest discover -s harness/lib

Three corpora, deliberately:

1. PRODUCTION-SHAPED synthetic log text, authored against the C# emitters
   (``GhostRenderTrace.BuildPrefix`` + ``GhostPlaybackEngine.
   EmitMeshLifecycleTrace``) rather than pasted out of a KSP.log. Every
   load-bearing quirk is exercised: spaces in the vessel name, spaces in the
   destroy reason, ``NaN`` / ``Infinity`` UTs, negative ghost indices, the
   ``<none>`` sentinels ``Token`` / ``ShortId`` emit for an empty id, and a
   ``recId`` whose spaces the producer has already underscored.

2. ADVERSARIAL text: a line that merely NAMES ``phase=MeshSpawned`` without the
   subsystem tag (the ``_anomaly_reasons`` lesson - S1.7's first flight red on a
   TestRunner line whose diagnostic label happened to contain the token), a
   tagged line whose fields are torn, a tagged line whose tail lost its
   ``reason=``, an empty log, and a ``None`` log.

3. A SOURCE-DERIVED GUARD over the two C# emitters (``EmitterSourceGuardTests``).
   Read that class's docstring before touching it: this repo has been bitten
   three times by regexes over C# source reading COMMENTS as code, so the parse
   there is deliberately narrow (one extracted expression, one brace-matched
   body, comment text stripped first) and never a whole-file scan.

The binding property throughout: a declared block over a log with ZERO
``MeshSpawned`` lines is a MISMATCH, never a vacuous pass. That is the whole
point of the row - it exists to catch ghosts that never rendered, and "nothing
rendered" is exactly what a bare count-based clause would green off.
"""

import os
import re
import unittest

import ghostlife
import saveparse

LIB_DIR = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(LIB_DIR)
REPO_ROOT = os.path.dirname(HARNESS_ROOT)
PARSEK_SOURCE_DIR = os.path.join(REPO_ROOT, "Source", "Parsek")

TAG = ghostlife.TRACE_SUBSYSTEM_TAG


def line(phase, rec_id="rec00001aaaabbbbccccddddeeeeffff", ghost_index=0,
         frame=1234, current_ut="1000.000", playback_ut="1000.000",
         vessel="Test Craft", reason=None, level="INFO", tag=TAG):
    """One production-shaped lifecycle line.

    Built field by field in BuildPrefix's emit order rather than as one literal,
    so a cell that needs to vary ONE field cannot accidentally vary the shape
    too. ``rec=`` is derived the way ``ShortId`` derives it (first 8 chars).
    """
    if reason is None:
        reason = (ghostlife.SPAWN_REASON if phase == ghostlife.PHASE_SPAWNED
                  else "playback completed")
    short = rec_id[:8] if len(rec_id) > 8 else rec_id
    return ("[Parsek][%s]%s phase=%s rec=%s recId=%s ghostIndex=%d frame=%d "
            "currentUT=%s playbackUT=%s vessel=%s reason=%s"
            % (level, tag, phase, short, rec_id, ghost_index, frame,
               current_ut, playback_ut, vessel, reason))


def log(*lines):
    """A log body with realistic NOISE around the lifecycle lines: the scanner
    has to survive a file that is overwhelmingly not its own subsystem."""
    body = ["[LOG 00:00:00.000] some stock KSP chatter",
            "[Parsek][INFO][Engine] Ghost #0 \"Test Craft\" created"]
    body.extend(lines)
    body.append("[Parsek][VERBOSE][Recorder] sampled 42 points")
    return "\n".join(body) + "\n"


class ParserTests(unittest.TestCase):
    def test_a_spawn_and_a_destroy_parse_every_field(self):
        snap = ghostlife.parse_ghost_lifecycle(log(
            line(ghostlife.PHASE_SPAWNED, rec_id="abcdef0123456789", ghost_index=3,
                 frame=99, current_ut="12.500", playback_ut="7.250",
                 vessel="Kerbal X"),
            line(ghostlife.PHASE_DESTROYED, rec_id="abcdef0123456789", ghost_index=3,
                 frame=180, current_ut="20.000", playback_ut="14.750",
                 vessel="Kerbal X", reason="playback completed")))
        self.assertTrue(snap.parsed)
        self.assertEqual(0, snap.malformed)
        self.assertEqual(2, len(snap.lines))
        spawn = snap.spawns[0]
        self.assertEqual(ghostlife.PHASE_SPAWNED, spawn.phase)
        self.assertEqual("abcdef01", spawn.rec)
        self.assertEqual("abcdef0123456789", spawn.rec_id)
        self.assertEqual(3, spawn.ghost_index)
        self.assertEqual(99, spawn.frame)
        self.assertEqual("12.500", spawn.current_ut)
        self.assertEqual("7.250", spawn.playback_ut)
        self.assertEqual("Kerbal X", spawn.vessel)
        self.assertEqual("ghost-created", spawn.reason)
        self.assertEqual("playback completed", snap.destroys[0].reason)

    def test_spaces_survive_in_both_tail_fields(self):
        # The whole reason the tail is cut by string search instead of tokenised.
        # A whitespace split would truncate BOTH of these.
        snap = ghostlife.parse_ghost_lifecycle(log(
            line(ghostlife.PHASE_DESTROYED, vessel="Jebediah Kerman's Big Rocket Mk2",
                 reason="auto-followed to next stage")))
        self.assertEqual(1, len(snap.destroys))
        self.assertEqual("Jebediah Kerman's Big Rocket Mk2", snap.destroys[0].vessel)
        self.assertEqual("auto-followed to next stage", snap.destroys[0].reason)

    def test_the_documented_destroy_reasons_all_round_trip(self):
        reasons = ["playback completed", "watch hold expired",
                   "auto-followed to next stage", "lifecycle"]
        snap = ghostlife.parse_ghost_lifecycle(log(*[
            line(ghostlife.PHASE_DESTROYED, rec_id="r%d" % i, reason=r)
            for i, r in enumerate(reasons)]))
        self.assertEqual(reasons, [d.reason for d in snap.destroys])

    def test_a_vessel_name_containing_the_word_reason_still_cuts_at_the_field(self):
        # `reason` as a WORD in a vessel name must not fool the cut - only the
        # ` reason=` FIELD separator does. (A vessel literally named
        # "... reason=..." would still fool it; that is documented in the module
        # and is not a shape KSP can produce from the VAB.)
        snap = ghostlife.parse_ghost_lifecycle(log(
            line(ghostlife.PHASE_DESTROYED, vessel="The Reason Probe",
                 reason="playback completed")))
        self.assertEqual("The Reason Probe", snap.destroys[0].vessel)
        self.assertEqual("playback completed", snap.destroys[0].reason)

    def test_non_finite_uts_parse_as_the_producer_writes_them(self):
        # GhostRenderTrace.FormatDouble emits these three verbatim; a float()
        # over them would raise or invent a value, so they stay strings.
        for raw in ("NaN", "Infinity", "-Infinity"):
            with self.subTest(ut=raw):
                snap = ghostlife.parse_ghost_lifecycle(log(
                    line(ghostlife.PHASE_SPAWNED, current_ut=raw, playback_ut=raw)))
                self.assertEqual(1, len(snap.spawns))
                self.assertEqual(raw, snap.spawns[0].current_ut)
                self.assertEqual(raw, snap.spawns[0].playback_ut)

    def test_the_none_sentinels_and_a_negative_index_parse(self):
        # ShortId / Token both emit "<none>" for an empty id, and a ghost index
        # is an int the engine can hand out below zero on a degenerate state.
        snap = ghostlife.parse_ghost_lifecycle(log(
            line(ghostlife.PHASE_SPAWNED, rec_id="<none>", ghost_index=-1,
                 vessel="Unknown")))
        self.assertEqual(1, len(snap.spawns))
        self.assertEqual("<none>", snap.spawns[0].rec_id)
        self.assertEqual(-1, snap.spawns[0].ghost_index)

    def test_an_underscored_recid_parses_as_one_token(self):
        # Token() replaces spaces with underscores, so recId is single-token BY
        # CONSTRUCTION - the parser relies on that and this pins it.
        snap = ghostlife.parse_ghost_lifecycle(log(
            line(ghostlife.PHASE_SPAWNED, rec_id="my_recording_id_with_spaces")))
        self.assertEqual("my_recording_id_with_spaces", snap.spawns[0].rec_id)

    def test_a_line_without_the_subsystem_tag_is_not_a_hit(self):
        # THE ANCHORING PROPERTY. A log line that merely NAMES the phase token -
        # a TestRunner diagnostic, a quoted comment, a report pasted into the log
        # - must not count as a ghost mesh. This is hlib's `_anomaly_reasons`
        # lesson, which cost S1.7 a flight.
        noise = ("[Parsek][INFO][TestRunner] checking for phase=MeshSpawned "
                 "rec=deadbeef recId=deadbeefcafe ghostIndex=0 frame=1 "
                 "currentUT=0.000 playbackUT=0.000 vessel=X reason=probe")
        snap = ghostlife.parse_ghost_lifecycle(log(noise))
        self.assertEqual((), snap.lines)
        self.assertEqual(0, snap.malformed)

    def test_an_unrelated_tracer_phase_is_ignored(self):
        # GhostRenderTrace emits many other phases; only the two lifecycle ones
        # are this row's business, and a FrameStart line must not be malformed.
        snap = ghostlife.parse_ghost_lifecycle(log(
            "[Parsek][VERBOSE]%s phase=FrameStart rec=abcdef01 recId=abcdef012345 "
            "ghostIndex=0 frame=5 currentUT=1.000 playbackUT=1.000 "
            "reason=first-seen" % TAG))
        self.assertEqual((), snap.lines)
        self.assertEqual(0, snap.malformed)

    def test_a_torn_line_counts_malformed_and_never_silently_drops(self):
        # A producer-shape change must show up as a nonzero malformed count
        # beside a collapsed spawn count, not as a bare zero.
        snap = ghostlife.parse_ghost_lifecycle(log(
            "[Parsek][INFO]%s phase=MeshSpawned rec=abcdef01 ghostIndex=notanint" % TAG))
        self.assertEqual((), snap.lines)
        self.assertEqual(1, snap.malformed)

    def test_a_tail_with_no_reason_field_counts_malformed(self):
        snap = ghostlife.parse_ghost_lifecycle(log(
            "[Parsek][INFO]%s phase=MeshDestroyed rec=abcdef01 recId=abcdef012345 "
            "ghostIndex=0 frame=7 currentUT=1.000 playbackUT=1.000 "
            "vessel=Test Craft" % TAG))
        self.assertEqual((), snap.lines)
        self.assertEqual(1, snap.malformed)

    def test_an_empty_log_parses_clean_and_a_none_log_does_not(self):
        # "We looked and saw none" and "we never looked" are different
        # statements, and the evaluator says each of them differently.
        empty = ghostlife.parse_ghost_lifecycle("")
        self.assertTrue(empty.parsed)
        self.assertEqual((), empty.lines)
        self.assertEqual("", empty.error)
        absent = ghostlife.parse_ghost_lifecycle(None)
        self.assertFalse(absent.parsed)
        self.assertEqual((), absent.lines)
        self.assertTrue(absent.error)

    def test_crlf_text_parses(self):
        # collect-logs snapshots a Windows KSP.log; splitlines() handles it, and
        # a trailing \r must not ride into the reason.
        body = log(line(ghostlife.PHASE_DESTROYED, reason="watch hold expired"))
        snap = ghostlife.parse_ghost_lifecycle(body.replace("\n", "\r\n"))
        self.assertEqual(1, len(snap.destroys))
        self.assertEqual("watch hold expired", snap.destroys[0].reason)


class FacetTests(unittest.TestCase):
    def test_distinct_recordings_versus_lines(self):
        # `spawned` counts DISTINCT recordings; spawnLines counts LINES. One
        # ghost respawning five times (loop playback / overlap retarget) is one
        # spawned recording, and collapsing the two would let a single looping
        # ghost satisfy a `{ min = 3 }` fleet floor.
        snap = ghostlife.parse_ghost_lifecycle(log(
            line(ghostlife.PHASE_SPAWNED, rec_id="aaa"),
            line(ghostlife.PHASE_SPAWNED, rec_id="aaa"),
            line(ghostlife.PHASE_SPAWNED, rec_id="bbb"),
            line(ghostlife.PHASE_DESTROYED, rec_id="aaa"),
            line(ghostlife.PHASE_DESTROYED, rec_id="aaa"),
            line(ghostlife.PHASE_DESTROYED, rec_id="bbb")))
        f = ghostlife.observed_ghost_lifecycle_facets(snap)[ghostlife.GHOST_LIFECYCLE_BLOCK]
        self.assertEqual(2, f["spawned"])
        self.assertEqual(3, f["spawnLines"])
        self.assertEqual(3, f["destroyLines"])
        self.assertEqual(2, f["destroyedRecordings"])
        self.assertEqual(["aaa", "bbb"], f["spawnedRecordingIds"])
        self.assertEqual([], f["unbalanced"])

    def test_the_reason_census_is_sorted_and_counts_every_raise(self):
        snap = ghostlife.parse_ghost_lifecycle(log(
            line(ghostlife.PHASE_DESTROYED, rec_id="a", reason="watch hold expired"),
            line(ghostlife.PHASE_DESTROYED, rec_id="b", reason="playback completed"),
            line(ghostlife.PHASE_DESTROYED, rec_id="c", reason="playback completed")))
        f = ghostlife.observed_ghost_lifecycle_facets(snap)[ghostlife.GHOST_LIFECYCLE_BLOCK]
        self.assertEqual({"playback completed": 2, "watch hold expired": 1},
                         f["destroyedReasons"])
        self.assertEqual(["playback completed", "watch hold expired"],
                         list(f["destroyedReasons"]))

    def test_a_blank_reason_rides_as_blank_rather_than_being_dropped(self):
        # A reason the writer could not name is still a destroy.
        snap = ghostlife.parse_ghost_lifecycle(log(
            line(ghostlife.PHASE_DESTROYED, reason="")))
        f = ghostlife.observed_ghost_lifecycle_facets(snap)[ghostlife.GHOST_LIFECYCLE_BLOCK]
        self.assertEqual({"(blank)": 1}, f["destroyedReasons"])

    def test_unbalanced_carries_the_vessel_name(self):
        # An 8-char prefix and a 32-hex id name nothing an operator can act on.
        snap = ghostlife.parse_ghost_lifecycle(log(
            line(ghostlife.PHASE_SPAWNED, rec_id="leaky01", vessel="Munar Lander"),
            line(ghostlife.PHASE_SPAWNED, rec_id="clean01", vessel="Kerbal X"),
            line(ghostlife.PHASE_DESTROYED, rec_id="clean01", vessel="Kerbal X")))
        rows = ghostlife.unbalanced_recordings(snap)
        self.assertEqual(1, len(rows))
        self.assertEqual("leaky01", rows[0]["recId"])
        self.assertEqual("Munar Lander", rows[0]["vessel"])

    def test_balance_is_per_recording_not_per_ghost_index(self):
        # The deliberate weakening documented on `unbalanced_recordings`:
        # ghostIndex is REUSED across spawns, so a per-index ledger would call a
        # legitimate respawn cycle unbalanced.
        snap = ghostlife.parse_ghost_lifecycle(log(
            line(ghostlife.PHASE_SPAWNED, rec_id="aaa", ghost_index=0),
            line(ghostlife.PHASE_DESTROYED, rec_id="aaa", ghost_index=0),
            line(ghostlife.PHASE_SPAWNED, rec_id="aaa", ghost_index=0)))
        self.assertEqual((), ghostlife.unbalanced_recordings(snap))

    def test_an_unreadable_log_measures_nothing_rather_than_zero(self):
        # ABSENT means "not measured", never zero - the facet convention.
        self.assertEqual({}, ghostlife.observed_ghost_lifecycle_facets(None))
        self.assertEqual({}, ghostlife.observed_ghost_lifecycle_facets(
            ghostlife.parse_ghost_lifecycle(None)))

    def test_every_window_key_is_an_unconditional_facet_key(self):
        # The anti-default pin: a window over a facet the module does not always
        # measure could only ever be answered by a default, and a defaulted zero
        # passes a `{max = 0}` clause off a surface that never ran.
        f = ghostlife.observed_ghost_lifecycle_facets(
            ghostlife.parse_ghost_lifecycle(""))[ghostlife.GHOST_LIFECYCLE_BLOCK]
        for key in ghostlife.GHOST_LIFECYCLE_WINDOW_KEYS:
            self.assertIn(key, f, key)


class SpecSurfaceTests(unittest.TestCase):
    def _errs(self, block):
        return ghostlife.validate_ghost_lifecycle_expectations(block)

    def test_no_block_is_valid(self):
        self.assertEqual([], self._errs(None))

    def test_a_non_table_block_is_refused(self):
        self.assertTrue(self._errs(["spawned"]))

    def test_unknown_key_is_a_spec_error(self):
        errs = self._errs({"spawnd": {"min": 1}})
        self.assertEqual(1, len(errs), errs)
        self.assertIn("unknown key(s)", errs[0])
        self.assertIn("spawnd", errs[0])

    def test_the_accepted_key_set_is_exactly_the_four(self):
        self.assertEqual(
            ("gating", "spawned", "requireBalanced", "destroyedReasons"),
            ghostlife.GHOST_LIFECYCLE_BLOCK_KEYS)

    def test_gating_must_be_a_bool(self):
        self.assertTrue(any("must be a bool" in e
                            for e in self._errs({"gating": "true"})))

    def test_require_balanced_must_be_a_bool(self):
        self.assertTrue(any("requireBalanced" in e and "must be a bool" in e
                            for e in self._errs({"requireBalanced": "yes"})))

    def test_window_shapes(self):
        self.assertEqual([], self._errs({"spawned": 3}))
        self.assertEqual([], self._errs({"spawned": {"min": 1}}))
        self.assertEqual([], self._errs({"spawned": {"min": 1, "max": 8}}))
        self.assertTrue(self._errs({"spawned": {}}))
        self.assertTrue(self._errs({"spawned": {"min": 5, "max": 2}}))
        self.assertTrue(self._errs({"spawned": {"min": -1}}))
        self.assertTrue(self._errs({"spawned": {"mn": 1}}))
        self.assertTrue(self._errs({"spawned": True}))
        self.assertTrue(self._errs({"spawned": "many"}))

    def test_an_armed_min_zero_window_is_refused(self):
        errs = self._errs({"gating": True, "spawned": {"min": 0}})
        self.assertTrue(any("can never red" in e for e in errs), errs)
        # ...but with a max it CAN red, so it is legal.
        self.assertEqual([], self._errs({"gating": True,
                                         "spawned": {"min": 0, "max": 4}}))

    def test_an_armed_bare_block_is_LEGAL_and_that_is_deliberate(self):
        # The one notch saveparse / rendercompose carry and this module does NOT.
        # An armed bare block still asserts "at least one ghost mesh spawned, and
        # every one that spawned was destroyed" - the vacuity floor plus the
        # requireBalanced default - so refusing it would refuse the block's most
        # useful minimal form. Asserted here so a future copy-paste of the
        # sibling notch has to argue with a test.
        self.assertEqual([], self._errs({"gating": True}))

    def test_destroyed_reasons_shapes(self):
        self.assertEqual([], self._errs(
            {"destroyedReasons": {"forbidden": ["exploded", "^kraken"]}}))
        self.assertTrue(self._errs({"destroyedReasons": ["exploded"]}))
        self.assertTrue(self._errs({"destroyedReasons": {}}))
        self.assertTrue(self._errs({"destroyedReasons": {"forbiden": ["x"]}}))
        self.assertTrue(self._errs({"destroyedReasons": {"forbidden": []}}))
        self.assertTrue(self._errs({"destroyedReasons": {"forbidden": "exploded"}}))
        self.assertTrue(self._errs({"destroyedReasons": {"forbidden": [7]}}))

    def test_a_bad_regex_is_a_pre_launch_error(self):
        # The logContracts precedent: a broken pattern found post-flight costs a
        # whole KSP boot, and an evaluator that swallowed the compile error would
        # report "nothing matched" for a clause that never ran.
        errs = self._errs({"destroyedReasons": {"forbidden": ["(unclosed"]}})
        self.assertTrue(any("not a valid regex" in e for e in errs), errs)

    def test_declared_and_armed_and_the_declared_copy(self):
        exp = {"ghostLifecycle": {"spawned": {"min": 1}}}
        self.assertEqual(("ghostLifecycle",),
                         ghostlife.declared_ghost_lifecycle_blocks(exp))
        self.assertEqual((), ghostlife.armed_ghost_lifecycle_blocks(exp))
        self.assertFalse(ghostlife.gating_armed(exp))
        exp["ghostLifecycle"]["gating"] = True
        self.assertEqual(("ghostLifecycle",),
                         ghostlife.armed_ghost_lifecycle_blocks(exp))
        self.assertTrue(ghostlife.gating_armed(exp))
        # `declared` is a COPY: a later mutation of the spec dict must not
        # rewrite the record of what ran (the 2026-08-25_1811 audit lesson).
        snapshot = ghostlife.declared_ghost_lifecycle_block(exp)
        exp["ghostLifecycle"]["spawned"] = {"min": 99}
        self.assertEqual({"min": 1}, snapshot["spawned"])
        self.assertIsNone(ghostlife.declared_ghost_lifecycle_block({}))

    def test_gating_key_is_imported_not_copied(self):
        # A copy would be a second literal to keep in step, and a drift would
        # make an armed block read as unarmed - silently.
        self.assertIs(saveparse.GATING_KEY, ghostlife.GATING_KEY)

    def test_the_window_validator_agrees_with_saveparse(self):
        # The copied-not-imported helpers must not drift from the originals.
        # Run BOTH over the same inputs (saveparse's copy is reached through its
        # public structure validator, which wraps the same private helper).
        cases = [3, {"min": 1}, {"min": 1, "max": 8}, {}, {"min": 5, "max": 2},
                 {"min": -1}, {"mn": 1}, True, "many"]
        for case in cases:
            with self.subTest(case=case):
                mine = bool(ghostlife.validate_ghost_lifecycle_expectations(
                    {"spawned": case}))
                theirs = bool(saveparse.validate_structure_expectations(
                    {"trees": case}))
                self.assertEqual(theirs, mine,
                                 "window grammar drifted from saveparse on %r" % (case,))


class TracerCouplingWarningTests(unittest.TestCase):
    """The ONE pre-launch warning: a declared block with no
    `SetSetting ghostRenderTracing = true` step measures nothing."""

    BLOCK = {"ghostLifecycle": {"spawned": {"min": 1}}}
    ON = {"cmd": "SetSetting",
          "args": {"name": "ghostRenderTracing", "value": "true"}}

    def test_no_block_no_warning(self):
        self.assertEqual([], ghostlife.ghost_lifecycle_expectation_warnings({}, []))

    def test_block_without_the_setting_warns(self):
        w = ghostlife.ghost_lifecycle_expectation_warnings(self.BLOCK, [
            {"cmd": "StartRecording"}, {"cmd": "FlushAndQuit"}])
        self.assertEqual(1, len(w), w)
        self.assertIn("ghostRenderTracing", w[0])
        self.assertIn("ZERO MeshSpawned", w[0])

    def test_block_with_the_setting_is_quiet(self):
        self.assertEqual([], ghostlife.ghost_lifecycle_expectation_warnings(
            self.BLOCK, [{"cmd": "StartRecording"}, self.ON]))

    def test_the_setting_turned_OFF_still_warns(self):
        off = {"cmd": "SetSetting",
               "args": {"name": "ghostRenderTracing", "value": "false"}}
        self.assertEqual(1, len(ghostlife.ghost_lifecycle_expectation_warnings(
            self.BLOCK, [off])))

    def test_a_different_tracer_does_not_satisfy_it(self):
        other = {"cmd": "SetSetting",
                 "args": {"name": "mapRenderTracing", "value": "true"}}
        self.assertEqual(1, len(ghostlife.ghost_lifecycle_expectation_warnings(
            self.BLOCK, [other])))

    def test_it_is_a_warning_and_never_an_error(self):
        # The deliberate break with the ExportRenderManifest coupling rule: the
        # vacuity floor already reds the tracer-off run, and an ERROR would
        # refuse that floor's own negative control.
        self.assertEqual([], ghostlife.validate_ghost_lifecycle_expectations(
            self.BLOCK["ghostLifecycle"]))


class EvaluatorTests(unittest.TestCase):
    HEALTHY = log(
        line(ghostlife.PHASE_SPAWNED, rec_id="aaa", vessel="Kerbal X"),
        line(ghostlife.PHASE_SPAWNED, rec_id="bbb", vessel="Munar Lander"),
        line(ghostlife.PHASE_DESTROYED, rec_id="aaa", vessel="Kerbal X",
             reason="playback completed"),
        line(ghostlife.PHASE_DESTROYED, rec_id="bbb", vessel="Munar Lander",
             reason="watch hold expired"))

    def _eval(self, block, text=None):
        exp = {} if block is None else {"ghostLifecycle": block}
        snap = ghostlife.parse_ghost_lifecycle(
            self.HEALTHY if text is None else text)
        return ghostlife.evaluate_ghost_lifecycle(exp, snap)

    def test_no_block_reports_facets_and_no_mismatches(self):
        r = self._eval(None)
        self.assertEqual(ghostlife.STATUS_REPORT, r.status)
        self.assertFalse(r.gating)
        self.assertEqual((), r.blocks)
        self.assertEqual((), r.mismatches)
        self.assertIsNone(r.declared)
        # Facets are recorded UNCONDITIONALLY - that is how a lane earns its
        # first honest window off a green report-only run.
        self.assertEqual(2, r.observed[ghostlife.GHOST_LIFECYCLE_BLOCK]["spawned"])

    def test_a_healthy_run_under_an_armed_block_passes(self):
        r = self._eval({"gating": True, "spawned": {"min": 1, "max": 8}})
        self.assertEqual(ghostlife.STATUS_PASS, r.status)
        self.assertTrue(r.gating)
        self.assertEqual((), r.mismatches)
        self.assertEqual(("ghostLifecycle",), r.armed_blocks)

    def test_zero_spawn_is_a_mismatch_and_that_is_the_whole_point(self):
        # THE VACUITY FLOOR. A declared block over a log with no MeshSpawned
        # lines must never read as a pass - this row exists to catch ghosts that
        # never rendered.
        r = self._eval({"spawned": {"min": 0, "max": 4}}, text=log())
        self.assertEqual(ghostlife.STATUS_REPORT, r.status)
        self.assertTrue(any("no MeshSpawned lines" in m for m in r.mismatches),
                        r.mismatches)
        # ...and the `{min = 0, max = 4}` window it would otherwise SATISFY does
        # not rescue it: the floor is independent of the window.
        self.assertFalse(any("spawned 0" in m for m in r.mismatches), r.mismatches)

    def test_zero_spawn_gates_when_armed(self):
        r = self._eval({"gating": True}, text=log())
        self.assertEqual(ghostlife.STATUS_FAIL, r.status)
        self.assertEqual(r.mismatches, r.armed_mismatches)

    def test_zero_spawn_with_no_block_is_silent(self):
        r = self._eval(None, text=log())
        self.assertEqual((), r.mismatches)

    def test_the_spawned_window_min_and_max_and_exact_pin(self):
        self.assertTrue(any("spawned 2 < min 5" in m
                            for m in self._eval({"spawned": {"min": 5}}).mismatches))
        self.assertTrue(any("spawned 2 > max 1" in m
                            for m in self._eval({"spawned": {"max": 1}}).mismatches))
        self.assertTrue(any("spawned 2 != 3" in m
                            for m in self._eval({"spawned": 3}).mismatches))
        self.assertEqual((), self._eval({"spawned": 2}).mismatches)

    def test_require_balanced_defaults_on(self):
        text = log(line(ghostlife.PHASE_SPAWNED, rec_id="leaky01",
                        vessel="Orbital Probe"))
        r = self._eval({}, text=text)
        hits = [m for m in r.mismatches if "requireBalanced" in m]
        self.assertEqual(1, len(hits), r.mismatches)
        self.assertIn("Orbital Probe", hits[0])
        self.assertIn("leaky01", hits[0])

    def test_require_balanced_can_be_opted_out(self):
        text = log(line(ghostlife.PHASE_SPAWNED, rec_id="leaky01"))
        r = self._eval({"requireBalanced": False}, text=text)
        self.assertEqual([], [m for m in r.mismatches if "requireBalanced" in m])
        # The vacuity floor is NOT what opted out - a spawn did happen.
        self.assertEqual((), r.mismatches)

    def test_require_balanced_reports_every_leaked_recording(self):
        text = log(line(ghostlife.PHASE_SPAWNED, rec_id="l1", vessel="A"),
                   line(ghostlife.PHASE_SPAWNED, rec_id="l2", vessel="B"),
                   line(ghostlife.PHASE_SPAWNED, rec_id="ok", vessel="C"),
                   line(ghostlife.PHASE_DESTROYED, rec_id="ok", vessel="C"))
        r = self._eval({}, text=text)
        hits = [m for m in r.mismatches if "requireBalanced" in m]
        self.assertEqual(2, len(hits), r.mismatches)

    def test_forbidden_reason_regex_matches_by_search(self):
        # `re.search`, the logContracts contract: a spec can forbid a SUBSTRING
        # without knowing the whole English phrase.
        text = log(line(ghostlife.PHASE_SPAWNED, rec_id="a", vessel="Doomed"),
                   line(ghostlife.PHASE_DESTROYED, rec_id="a", vessel="Doomed",
                        reason="vessel exploded on reentry"))
        r = self._eval({"destroyedReasons": {"forbidden": ["explod"]}}, text=text)
        hits = [m for m in r.mismatches if "forbidden destroy reason" in m]
        self.assertEqual(1, len(hits), r.mismatches)
        self.assertIn("Doomed", hits[0])
        self.assertIn("vessel exploded on reentry", hits[0])

    def test_forbidden_reason_that_never_fires_is_silent(self):
        r = self._eval({"destroyedReasons": {"forbidden": ["^kraken"]}})
        self.assertEqual((), r.mismatches)

    def test_forbidden_reason_counts_every_hit_and_names_the_first(self):
        text = log(line(ghostlife.PHASE_SPAWNED, rec_id="a"),
                   line(ghostlife.PHASE_DESTROYED, rec_id="a", vessel="First",
                        reason="destroyed by kraken"),
                   line(ghostlife.PHASE_SPAWNED, rec_id="b"),
                   line(ghostlife.PHASE_DESTROYED, rec_id="b", vessel="Second",
                        reason="destroyed by kraken"))
        r = self._eval({"destroyedReasons": {"forbidden": ["kraken"]}}, text=text)
        hits = [m for m in r.mismatches if "forbidden destroy reason" in m]
        self.assertEqual(1, len(hits))
        self.assertIn("2 MeshDestroyed line(s)", hits[0])
        self.assertIn("First", hits[0])

    def test_a_drifted_spec_shape_is_a_no_op_never_a_crash(self):
        # The saveparse rule: shapes rejected at spec validation are tolerated
        # here, because crashing the verifier chain would lose every other row's
        # evidence.
        for block in ({"spawned": "many"}, {"spawned": True},
                      {"destroyedReasons": ["exploded"]},
                      {"destroyedReasons": {"forbidden": "exploded"}},
                      {"destroyedReasons": {"forbidden": ["(unclosed"]}},
                      {"requireBalanced": "yes"}):
            with self.subTest(block=block):
                r = self._eval(block)
                self.assertIn(r.status, (ghostlife.STATUS_REPORT,))
                self.assertEqual((), tuple(
                    m for m in r.mismatches if "forbidden" in m))

    def test_a_non_bool_require_balanced_falls_back_to_the_default(self):
        text = log(line(ghostlife.PHASE_SPAWNED, rec_id="leaky01"))
        r = self._eval({"requireBalanced": "yes"}, text=text)
        self.assertTrue(any("requireBalanced" in m for m in r.mismatches))

    def test_an_absent_log_is_a_named_mismatch_under_a_declared_block(self):
        r = ghostlife.evaluate_ghost_lifecycle(
            {"ghostLifecycle": {"gating": True}}, None)
        self.assertEqual(ghostlife.STATUS_FAIL, r.status)
        self.assertTrue(any("log absent" in m for m in r.mismatches), r.mismatches)
        self.assertIsNone(r.parsed)
        # ...and the declared block is STILL recorded: a control flight that lost
        # its log has to say what it was carrying.
        self.assertEqual({"gating": True}, r.declared)

    def test_an_unreadable_log_is_a_named_mismatch_under_a_declared_block(self):
        r = ghostlife.evaluate_ghost_lifecycle(
            {"ghostLifecycle": {}}, ghostlife.parse_ghost_lifecycle(None))
        self.assertEqual(ghostlife.STATUS_REPORT, r.status)
        self.assertTrue(any("log unreadable" in m for m in r.mismatches), r.mismatches)
        self.assertEqual({}, r.observed)

    def test_an_unreadable_log_with_no_block_is_silent(self):
        r = ghostlife.evaluate_ghost_lifecycle({}, None)
        self.assertEqual((), r.mismatches)
        self.assertEqual(ghostlife.STATUS_REPORT, r.status)

    def test_mismatches_are_deduplicated_in_first_seen_order(self):
        text = log(line(ghostlife.PHASE_SPAWNED, rec_id="l1", vessel="A"),
                   line(ghostlife.PHASE_SPAWNED, rec_id="l1", vessel="A"))
        r = self._eval({"spawned": {"min": 4}}, text=text)
        self.assertEqual(len(set(r.mismatches)), len(r.mismatches))
        self.assertIn("min 4", r.mismatches[0])

    def test_report_only_mismatches_never_reach_armed_mismatches(self):
        r = self._eval({"spawned": {"min": 5}})
        self.assertTrue(r.mismatches)
        self.assertEqual((), r.armed_mismatches)
        self.assertEqual(ghostlife.STATUS_REPORT, r.status)


class EmitterSourceGuardTests(unittest.TestCase):
    """The parser's field order, pinned against the C# emitters it transcribes.

    HOW THIS PARSE IS DELIBERATELY NARROW, and why the narrowness is the point.
    A regex swept over a whole C# file reads COMMENTS AS CODE - this repo has
    been bitten by exactly that three times, and the failure mode is the bad one:
    the guard goes GREEN off a comment that quotes the shape while the real code
    has moved. So neither cell below scans a file. Each one:

      1. locates ONE anchor (a method signature) by exact string,
      2. extracts ONE region from it - the `return` EXPRESSION for BuildPrefix,
         the brace-matched BODY for EmitMeshLifecycleTrace,
      3. STRIPS `//` comment text from every line of that region BEFORE looking
         at it,
      4. and only then reads the ordered field literals out.

    Known and accepted limitation of step 3: a `//` inside a string literal
    would truncate that line. Neither region contains one (checked at authoring),
    and the alternative - a real C# lexer - is not worth carrying for two
    methods. If a future edit puts a URL in one of these strings, this guard
    goes conservative (it drops text) rather than permissive.

    These cells are SKIPPED, not failed, when Source/Parsek is absent: the
    harness suite must stay runnable from a checkout that has only harness/.
    """

    @classmethod
    def setUpClass(cls):
        if not os.path.isdir(PARSEK_SOURCE_DIR):
            raise unittest.SkipTest("Source/Parsek absent (harness-only checkout)")

    @staticmethod
    def _read(rel):
        with open(os.path.join(PARSEK_SOURCE_DIR, rel), "r",
                  encoding="utf-8", errors="replace") as fh:
            return fh.read()

    @staticmethod
    def _strip_comments(text):
        """Drop `//`-to-end-of-line from every line. See the class docstring for
        the accepted limitation."""
        out = []
        for ln in text.splitlines():
            cut = ln.find("//")
            out.append(ln if cut < 0 else ln[:cut])
        return "\n".join(out)

    @classmethod
    def _return_expression(cls, text, signature):
        """The single `return ...;` expression inside the method whose signature
        line is `signature`. Comments are stripped BEFORE the anchor search, so
        a comment that merely quotes the signature (the "regex read a comment
        as code" trap this class exists to avoid) cannot seat the anchor."""
        stripped = cls._strip_comments(text)
        at = stripped.index(signature)
        body = stripped[at:]
        start = body.index("return ")
        # A C# statement ends at the first `;` at depth 0 of any string; these
        # two expressions contain no `;` inside a literal, so a plain find is
        # exact here.
        end = body.index(";", start)
        return body[start:end + 1]

    @classmethod
    def _method_body(cls, text, signature):
        """The brace-matched body of the method whose signature line is
        `signature`. Comments stripped BEFORE the anchor search (see
        _return_expression) and before brace matching, so neither a quoted
        signature nor a `{` inside a comment can mislead the extraction."""
        stripped = cls._strip_comments(text)
        at = stripped.index(signature)
        body = stripped[at:]
        open_at = body.index("{")
        depth = 0
        for i in range(open_at, len(body)):
            if body[i] == "{":
                depth += 1
            elif body[i] == "}":
                depth -= 1
                if depth == 0:
                    return body[open_at:i + 1]
        raise AssertionError("unbalanced braces after %r" % (signature,))

    def test_build_prefix_emits_the_fields_in_the_parsed_order(self):
        src = self._read("GhostRenderTrace.cs")
        expr = self._return_expression(src, "private static string BuildPrefix(")
        fields = re.findall(r'"\s*(\w+)=', expr)
        self.assertEqual(list(ghostlife.PREFIX_FIELDS), fields,
                         "GhostRenderTrace.BuildPrefix's field order moved; "
                         "ghostlife._LINE_RE parses the OLD order and would "
                         "report every lifecycle line as malformed")

    def test_build_prefix_joins_the_fields_with_single_spaces(self):
        # The NAME sweep above tolerates any joiner (`"\s*(\w+)=` matches a
        # zero-width gap), so a `", rec="` rewrite would pass it while
        # `_LINE_RE`'s `\s+rec=` reports every line malformed. Pin the joiner:
        # the first field opens its literal bare, every later field's literal
        # opens with exactly one space.
        src = self._read("GhostRenderTrace.cs")
        expr = self._return_expression(src, "private static string BuildPrefix(")
        first = ghostlife.PREFIX_FIELDS[0]
        self.assertIn('"%s=' % first, expr,
                      "BuildPrefix no longer opens with %s=" % first)
        for name in ghostlife.PREFIX_FIELDS[1:]:
            self.assertIn('" %s=' % name, expr,
                          "BuildPrefix's separator before %s= is no longer a "
                          "single space; _LINE_RE's \\s+ anchors would report "
                          "every lifecycle line as malformed" % name)

    def test_the_tail_puts_vessel_before_reason(self):
        src = self._read("GhostPlaybackEngine.cs")
        body = self._method_body(src, "private static void EmitMeshLifecycleTrace(")
        v, r = body.find("vessel="), body.find(" reason=")
        self.assertGreaterEqual(v, 0, "EmitMeshLifecycleTrace no longer emits vessel=")
        self.assertGreaterEqual(r, 0, "EmitMeshLifecycleTrace no longer emits ' reason='")
        self.assertLess(v, r, "vessel= and reason= swapped; ghostlife cuts the "
                              "tail at the LAST ' reason=' (rfind), which is "
                              "only correct while reason is the final field")

    def test_both_phase_tokens_are_live_call_sites(self):
        # The two phases are string ARGUMENTS at the call sites, not constants in
        # the emitter, so they are pinned where they are passed. Comments
        # stripped first - GhostPlaybackEngine.cs discusses both tokens in prose
        # right above the emitter, which is exactly the trap this class exists
        # to avoid.
        src = self._strip_comments(self._read("GhostPlaybackEngine.cs"))
        for phase in ghostlife.LIFECYCLE_PHASES:
            with self.subTest(phase=phase):
                self.assertIn('EmitMeshLifecycleTrace("%s"' % phase, src,
                              "no live EmitMeshLifecycleTrace call site passes "
                              "%r; ghostlife scans for a phase nothing emits"
                              % (phase,))

    def test_the_spawn_reason_token_is_the_one_the_call_site_passes(self):
        src = self._strip_comments(self._read("GhostPlaybackEngine.cs"))
        self.assertIn('"%s"' % ghostlife.SPAWN_REASON, src)

    def test_the_subsystem_tag_matches_the_emitters_subsystem(self):
        # ParsekLog.Write renders "[Parsek][{level}][{subsystem}] {message}", and
        # the tracer routes through ParsekLog.Info("GhostRenderTrace", ...). The
        # ANCHOR depends on that spelling, so pin it at the emit site.
        src = self._strip_comments(self._read("GhostRenderTrace.cs"))
        self.assertIn('ParsekLog.Info("GhostRenderTrace"', src)
        self.assertEqual("[GhostRenderTrace]", ghostlife.TRACE_SUBSYSTEM_TAG)


if __name__ == "__main__":  # pragma: no cover
    unittest.main()
