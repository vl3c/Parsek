"""Pure tests for the FORGE-save harvest tool (`harness/tools/harvest_bdock_station.py`).

The tool became GENERIC over the produced save's SITUATION when the ORBITAL forge
(FORGE-eva2-lko) landed: the two pad forges leave a PRELAUNCH craft, the orbital
one leaves a CREWED ORBITING stage, and the optional `--expect-situation` gate is
what keeps an orbital harvest honest. These cells cover the pure parsing +
gate decisions; the copy/prune half is filesystem shell work exercised by the
operator harvest itself.

The dangerous silent failure this guards: a forge run that flaked mid-ascent, or
one whose focus landed on the spent core, silently stamping a BROKEN fixture that
every consumer scenario then inherits.

Runnable with the stdlib runner only (NO pytest, NO KSP, NO network)::

    cd harness && python -m unittest discover -s lib -q
"""

import os
import sys
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_TOOLS = os.path.join(os.path.dirname(_HERE), "tools")
if _TOOLS not in sys.path:
    sys.path.insert(0, _TOOLS)

import harvest_bdock_station as harvest  # noqa: E402


# A minimal two-VESSEL FLIGHTSTATE in the real sfs shape (tabs, `name` before
# `sit`, a PART child whose own `name` must NOT be mistaken for the vessel's).
SFS = """GAME
\tTitle = forge-run (SANDBOX)
\tSCENARIO
\t{
\t\tname = ParsekScenario
\t}
\tFLIGHTSTATE
\t{
\t\tactiveVessel = 1
\t\tVESSEL
\t\t{
\t\t\tpid = aaaa
\t\t\tname = Spent Core
\t\t\ttype = Debris
\t\t\tsit = ORBITING
\t\t\tPART
\t\t\t{
\t\t\t\tname = fuelTank
\t\t\t}
\t\t}
\t\tVESSEL
\t\t{
\t\t\tpid = bbbb
\t\t\tname = Kerbal X
\t\t\ttype = Ship
\t\t\tsit = ORBITING
\t\t\tPART
\t\t\t{
\t\t\t\tname = mk1-3pod
\t\t\t}
\t\t}
\t}
"""

SFS_PAD = SFS.replace("\t\t\tsit = ORBITING\n\t\t\tPART\n\t\t\t{\n\t\t\t\tname = mk1-3pod",
                      "\t\t\tsit = PRELAUNCH\n\t\t\tPART\n\t\t\t{\n\t\t\t\tname = mk1-3pod")


class VesselRecordTests(unittest.TestCase):
    def test_reads_vessel_name_and_situation_in_flightstate_order(self):
        records = harvest.read_vessel_records(SFS)
        self.assertEqual(records, [("Spent Core", "ORBITING"),
                                   ("Kerbal X", "ORBITING")])

    def test_part_name_is_not_mistaken_for_the_vessel_name(self):
        self.assertNotIn("fuelTank", [n for n, _ in harvest.read_vessel_records(SFS)])

    def test_active_vessel_index_and_count(self):
        self.assertEqual(harvest.read_active_vessel(SFS), 1)
        self.assertEqual(harvest.count_vessels(SFS), 2)

    def test_missing_keys_read_empty_never_guessed(self):
        records = harvest.read_vessel_records("VESSEL\n\tpid = x\n")
        self.assertEqual(records, [("", "")])

    def test_vessel_node_outside_flightstate_does_not_shift_the_index(self):
        # The hazard: activeVessel is an INDEX into FLIGHTSTATE's VESSEL nodes, so a
        # VESSEL node living in a SCENARIO node AHEAD of FLIGHTSTATE would shift every
        # index by one and make records[active] name the wrong craft (here it would
        # resolve activeVessel=1 to "Spent Core" instead of "Kerbal X").
        polluted = SFS.replace(
            "\t\tname = ParsekScenario\n",
            "\t\tname = ParsekScenario\n"
            "\t\tVESSEL\n\t\t{\n\t\t\tname = Scenario Ghost\n\t\t\tsit = ORBITING\n\t\t}\n")
        records = harvest.read_vessel_records(polluted)
        self.assertEqual(records, [("Spent Core", "ORBITING"),
                                   ("Kerbal X", "ORBITING")])
        self.assertEqual(harvest.count_vessels(polluted), 2)
        active = harvest.read_active_vessel(polluted)
        self.assertEqual(records[active][0], "Kerbal X")

    def test_nodes_after_flightstate_are_not_scanned(self):
        trailing = SFS + "\tSCENARIO\n\t{\n\t\tVESSEL\n\t\t{\n\t\t\tname = Trailing\n\t\t}\n\t}\n"
        self.assertEqual(harvest.count_vessels(trailing), 2)
        self.assertNotIn("Trailing", [n for n, _ in harvest.read_vessel_records(trailing)])

    def test_flightstate_span_falls_back_when_absent_or_unbalanced(self):
        # No FLIGHTSTATE header at all -> whole input (a bare VESSEL fragment parses).
        self.assertEqual(harvest.flightstate_span("VESSEL\n"), "VESSEL\n")
        # Truncated save (braces never balance) -> everything from the open brace on,
        # so a half-written save still reports what it has instead of reading empty.
        truncated = "GAME\n{\n\tFLIGHTSTATE\n\t{\n\t\tVESSEL\n\t\t{\n\t\t\tname = Half\n"
        self.assertEqual(harvest.count_vessels(truncated), 1)


class ExpectedSituationParseTests(unittest.TestCase):
    def test_none_and_empty_disable_the_gate(self):
        self.assertEqual(harvest.parse_expected_situations(None), ())
        self.assertEqual(harvest.parse_expected_situations(""), ())
        self.assertEqual(harvest.parse_expected_situations("  ,  "), ())

    def test_comma_list_is_upper_normalized(self):
        self.assertEqual(harvest.parse_expected_situations("orbiting"), ("ORBITING",))
        self.assertEqual(harvest.parse_expected_situations("ORBITING, prelaunch"),
                         ("ORBITING", "PRELAUNCH"))


class SituationGateTests(unittest.TestCase):
    def _records(self, text=SFS):
        return harvest.read_vessel_records(text)

    def test_gate_off_always_passes(self):
        ok, _ = harvest.check_active_situation(self._records(), 1, ())
        self.assertTrue(ok)
        # Even with a nonsense index (the gate is simply not consulted).
        ok, _ = harvest.check_active_situation(self._records(), 99, ())
        self.assertTrue(ok)

    def test_orbital_forge_passes_on_an_orbiting_active_vessel(self):
        ok, detail = harvest.check_active_situation(self._records(), 1, ("ORBITING",))
        self.assertTrue(ok)
        self.assertIn("Kerbal X", detail)

    def test_orbital_gate_rejects_a_pad_save(self):
        ok, detail = harvest.check_active_situation(
            self._records(SFS_PAD), 1, ("ORBITING",))
        self.assertFalse(ok)
        self.assertIn("PRELAUNCH", detail)

    def test_pad_gate_still_works_for_the_pad_forges(self):
        ok, _ = harvest.check_active_situation(
            self._records(SFS_PAD), 1, ("PRELAUNCH",))
        self.assertTrue(ok)

    def test_gate_fails_closed_on_an_unresolvable_active_index(self):
        for idx in (None, -1, 99):
            ok, detail = harvest.check_active_situation(
                self._records(), idx, ("ORBITING",))
            self.assertFalse(ok, idx)
            self.assertIn("does not resolve", detail)

    def test_gate_fails_closed_on_an_unreadable_situation(self):
        records = [("Kerbal X", "")]
        ok, detail = harvest.check_active_situation(records, 0, ("ORBITING",))
        self.assertFalse(ok)
        self.assertIn("<unreadable>", detail)


class TitleNormalizeTests(unittest.TestCase):
    def test_first_title_line_is_rewritten_with_the_sandbox_suffix(self):
        out = harvest.normalize_title(SFS, "eva2-lko-crewed")
        self.assertIn("Title = eva2-lko-crewed (SANDBOX)", out)
        # Only the FIRST Title line, and nothing else in the file moves.
        self.assertEqual(out.count("Title ="), SFS.count("Title ="))
        self.assertEqual(len(out.splitlines()), len(SFS.splitlines()))


class TitleSuffixModeTests(unittest.TestCase):
    """The suffix is DERIVED from the save's own `Mode`, not hardcoded
    (career-ledger task C.3, done 2026-08-19).

    It read `(SANDBOX)` unconditionally before, which was correct only because
    every forge until then produced a sandbox save. The first CAREER harvest
    would have stamped a fixture whose title contradicted its own `Mode = CAREER`
    line - and a fixture that lies about its own mode is exactly the kind of thing
    a later reader trusts. `build_career_pad_craft.py` already writes `(CAREER)`
    by hand for the fixture it builds by construction, so this only brings the
    harvested path into line with the constructed one."""

    def test_a_career_save_gets_the_career_suffix(self):
        career = SFS.replace("\tTitle = forge-run (SANDBOX)",
                             "\tTitle = forge-run (SANDBOX)\n\tMode = CAREER")
        out = harvest.normalize_title(career, "c2-career-postfix")
        self.assertIn("Title = c2-career-postfix (CAREER)", out)

    def test_a_sandbox_save_is_unchanged_from_the_old_behaviour(self):
        sandbox = SFS.replace("\tTitle = forge-run (SANDBOX)",
                              "\tTitle = forge-run (SANDBOX)\n\tMode = SANDBOX")
        self.assertIn("Title = bdock-station-pad (SANDBOX)",
                      harvest.normalize_title(sandbox, "bdock-station-pad"))

    def test_science_sandbox_reads_its_own_name(self):
        sci = SFS.replace("\tTitle = forge-run (SANDBOX)",
                          "\tTitle = forge-run (SANDBOX)\n\tMode = SCIENCE_SANDBOX")
        self.assertIn("Title = x (SCIENCE_SANDBOX)", harvest.normalize_title(sci, "x"))

    def test_an_unreadable_mode_falls_back_to_the_previous_hardcoded_value(self):
        # SFS carries no Mode line at all. Falling back to (SANDBOX) keeps every
        # save this cannot read behaving EXACTLY as it did before the change, so
        # the four committed sandbox fixtures cannot move underneath a re-harvest.
        self.assertEqual("", harvest.read_game_mode(SFS))
        self.assertIn("Title = y (SANDBOX)", harvest.normalize_title(SFS, "y"))

    def test_the_mode_read_is_case_and_whitespace_normalised(self):
        self.assertEqual("CAREER", harvest.read_game_mode("GAME\n\tMode =  career \n"))
        self.assertEqual("(CAREER)", harvest.title_suffix_for_mode("CAREER"))

    def test_a_nested_mode_value_is_not_mistaken_for_the_games(self):
        # `Mode` is a common value name inside nested nodes (part modules, several
        # mods). A greedy-indent match would read whichever came first in the file,
        # so a part's setting could stamp the fixture's title. The GAME node's own
        # values sit at exactly one tab.
        nested_first = ("GAME\n\tFLIGHTSTATE\n\t{\n\t\tVESSEL\n\t\t{\n\t\t\tMODULE\n"
                        "\t\t\t{\n\t\t\t\tMode = Follow\n\t\t\t}\n\t\t}\n\t}\n"
                        "\tMode = CAREER\n")
        self.assertEqual("CAREER", harvest.read_game_mode(nested_first))

    def test_the_real_committed_career_fixture_reads_career(self):
        # The read against real bytes, not a hand-built string: this is the exact
        # save a CAREER harvest would be stamping from.
        path = os.path.join(os.path.dirname(os.path.dirname(_HERE)),
                            "harness", "fixtures", "saves", "career-pad-craft",
                            "persistent.sfs")
        if not os.path.isfile(path):
            self.skipTest("career-pad-craft fixture not present")
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            self.assertEqual("CAREER", harvest.read_game_mode(fh.read()))


class SpaceCenterStartTests(unittest.TestCase):
    """--allow-no-vessel and --expect-mode: the scripted campaign's fixtures
    (CS-0's genuine New Game, later stages that recovered everything they flew)
    start at the Space Center with no VESSEL node, which the focusable gate
    would otherwise refuse, and a stage must never be promoted from a save of
    the wrong game mode."""

    NEW_GAME = ("GAME\n{\n\tTitle = cs (SCIENCE_SANDBOX)\n\tMode = SCIENCE_SANDBOX\n"
                "\tFLIGHTSTATE\n\t{\n\t\tversion = 1.12.5\n\t\tUT = 0\n"
                "\t\tactiveVessel = 0\n\t}\n}\n")

    def _save(self):
        import tempfile
        save = tempfile.mkdtemp()
        with open(os.path.join(save, "persistent.sfs"), "w") as fh:
            fh.write(self.NEW_GAME)
        return save

    def _harvest(self, save, **kw):
        import tempfile
        out_root = tempfile.mkdtemp()
        orig = harvest._FIXTURES_SAVES
        harvest._FIXTURES_SAVES = out_root
        try:
            return harvest.harvest(save, "cs-new", "cs-new", force=False, **kw), out_root
        finally:
            harvest._FIXTURES_SAVES = orig

    def test_a_vesselless_save_is_refused_without_the_flag(self):
        with self.assertRaises(SystemExit):
            self._harvest(self._save())

    def test_a_vesselless_save_harvests_with_the_flag(self):
        rc, out_root = self._harvest(self._save(), allow_no_vessel=True,
                                     expected_mode="SCIENCE_SANDBOX")
        self.assertEqual(0, rc)
        self.assertTrue(os.path.isfile(os.path.join(out_root, "cs-new", "persistent.sfs")))

    def test_the_mode_gate_refuses_the_wrong_mode_before_writing(self):
        with self.assertRaises(SystemExit):
            self._harvest(self._save(), allow_no_vessel=True, expected_mode="CAREER")

    def test_check_game_mode_is_off_when_nothing_is_expected(self):
        self.assertEqual((True, ""), harvest.check_game_mode("garbage", ""))
        ok, detail = harvest.check_game_mode(self.NEW_GAME, "science_sandbox")
        self.assertTrue(ok, detail)


class KeepParsekModeTests(unittest.TestCase):
    """--keep-parsek: the RECORDED-STATE fixture mode (the B17 duna-direct
    recording fixture). The save's Parsek/ dir (recording sidecars) must
    SURVIVE the harvest while .parsek-backup* staging dirs are still pruned;
    default mode must stay byte-identical Parsek-clean. Filesystem cells over
    tempdirs -- this is exactly the prune decision, not operator shell work."""

    def _make_save(self, root, with_sidecar=True):
        os.makedirs(os.path.join(root, "Parsek", "Recordings"))
        if with_sidecar:
            with open(os.path.join(root, "Parsek", "Recordings", "abc.prec"),
                      "w") as fh:
                fh.write("payload")
        os.makedirs(os.path.join(root, ".parsek-backup-staging-1"))
        os.makedirs(os.path.join(root, "Ships", "VAB"))
        with open(os.path.join(root, "Ships", "VAB", "X.craft"), "w") as fh:
            fh.write("ship = X")
        with open(os.path.join(root, "persistent.sfs"), "w") as fh:
            # the sidecar's id must be REFERENCED by the save or the orphan
            # prune (correctly) removes it - a real recorded save always
            # carries its RECORDING rows
            fh.write(SFS + "RECORDING_STUB { id = abc }" + chr(10))

    def _harvest_into_temp(self, save_dir, target_name, **kw):
        import tempfile
        out_root = tempfile.mkdtemp()
        orig = harvest._FIXTURES_SAVES
        harvest._FIXTURES_SAVES = out_root
        try:
            rc = harvest.harvest(save_dir, target_name, target_name,
                                 force=False, expected_situations=(), **kw)
        finally:
            harvest._FIXTURES_SAVES = orig
        return rc, os.path.join(out_root, target_name)

    def test_keep_parsek_keeps_the_sidecars_and_prunes_the_backups(self):
        import tempfile
        save = tempfile.mkdtemp()
        self._make_save(save)
        rc, target = self._harvest_into_temp(save, "recorded-fx",
                                             keep_parsek=True)
        self.assertEqual(rc, 0)
        self.assertTrue(os.path.isfile(
            os.path.join(target, "Parsek", "Recordings", "abc.prec")),
            "the recording sidecar IS the fixture payload and must survive")
        self.assertFalse(os.path.isdir(
            os.path.join(target, ".parsek-backup-staging-1")),
            "backup staging dirs are never fixture content")

    def test_keep_parsek_refuses_a_save_with_no_sidecars(self):
        import tempfile
        save = tempfile.mkdtemp()
        self._make_save(save, with_sidecar=False)
        with self.assertRaises(SystemExit):
            self._harvest_into_temp(save, "recorded-fx", keep_parsek=True)

    def test_keep_parsek_prunes_the_retained_versions_and_the_quarantine(self):
        # Both are file-collector exhaust no fixture referrer names (persistent.sfs
        # names only the head hashes); the head sidecar beside them is the payload.
        import tempfile
        save = tempfile.mkdtemp()
        self._make_save(save)
        recordings = os.path.join(save, "Parsek", "Recordings")
        for sub in ("_versions", "_quarantine"):
            os.makedirs(os.path.join(recordings, sub))
            with open(os.path.join(recordings, sub, "abc.0123456789abcdef.prec"), "w") as fh:
                fh.write("old")
        rc, target = self._harvest_into_temp(save, "recorded-versions",
                                             keep_parsek=True)
        self.assertEqual(rc, 0)
        kept = os.path.join(target, "Parsek", "Recordings")
        self.assertTrue(os.path.isfile(os.path.join(kept, "abc.prec")))
        self.assertFalse(os.path.isdir(os.path.join(kept, "_versions")))
        self.assertFalse(os.path.isdir(os.path.join(kept, "_quarantine")))

    def test_keep_parsek_keeps_a_retained_version_the_save_names(self):
        # A committed tree plus its resumed in-flight copy: the committed rows name an
        # OLDER prec hash that only `_versions` holds. That one file must survive (the
        # load rejects the committed tree without it); an unnamed one must not.
        import tempfile
        rid = "311d98e32547491e8dd37aec2526d25d"
        save = tempfile.mkdtemp()
        self._make_save(save)
        with open(os.path.join(save, "persistent.sfs"), "a") as fh:
            fh.write("RECORDING" + chr(10) + "{" + chr(10) + "recordingId = %s" % rid + chr(10)
                     + "precHash = 0a796226a7c3ce16" + chr(10) + "}" + chr(10))
        versions = os.path.join(save, "Parsek", "Recordings", "_versions")
        os.makedirs(os.path.join(versions, "_quarantine"))
        for name in (rid + ".0a796226a7c3ce16.prec", rid + ".024e00dd6ca54b1c.prec",
                     os.path.join("_quarantine", rid + ".0a796226a7c3ce16.prec")):
            with open(os.path.join(versions, name), "w") as fh:
                fh.write("old")
        rc, target = self._harvest_into_temp(save, "recorded-named-version",
                                             keep_parsek=True)
        self.assertEqual(rc, 0)
        kept = os.path.join(target, "Parsek", "Recordings", "_versions")
        self.assertEqual([rid + ".0a796226a7c3ce16.prec"], sorted(os.listdir(kept)))

    def test_named_version_files_needs_both_the_id_and_the_hash(self):
        rid = "311d98e32547491e8dd37aec2526d25d"
        other = "cb6acf4e1f4043878db57c89ffc9948f"
        sfs = ("recordingId = %s" % rid + chr(10) + "		precHash = 0a796226a7c3ce16" + chr(13) + chr(10)
               + "vesselHash = 338e43c296772404" + chr(10))
        names = [rid + ".0a796226a7c3ce16.prec", rid + ".338e43c296772404_vessel.craft",
                 other + ".0a796226a7c3ce16.prec", rid + ".ffffffffffffffff.prec",
                 rid + ".0a796226a7c3ce16.prec.txt"]
        self.assertEqual([rid + ".0a796226a7c3ce16.prec",
                          rid + ".338e43c296772404_vessel.craft"],
                         harvest.named_version_files(names, sfs))

    def test_default_mode_still_prunes_parsek_entirely(self):
        import tempfile
        save = tempfile.mkdtemp()
        self._make_save(save)
        rc, target = self._harvest_into_temp(save, "clean-fx")
        self.assertEqual(rc, 0)
        self.assertFalse(os.path.isdir(os.path.join(target, "Parsek")),
                         "a START-state fixture must stay Parsek-clean")
        self.assertTrue(os.path.isfile(
            os.path.join(target, "Ships", "VAB", "X.craft")))


if __name__ == "__main__":
    unittest.main()

class OrphanSidecarTests(unittest.TestCase):
    """The orphan prune added after the 2026-08-24 G3a harvest committed two
    uncommitted single-POINT `.prec` stubs named nowhere in persistent.sfs
    (CommittedFixtureMirrorTests then red on the missing `_vessel.craft`
    mirrors). The membership rule is the WIDEST one - the id occurring
    ANYWHERE in the sfs text keeps the file - so the prune can only ever
    remove sidecars nothing references."""

    SFS = "GAME { RECORDING_TREE { RECORDING { id = aaaa1111 } } }"

    def test_referenced_sidecars_are_kept_whatever_their_suffix(self):
        names = ["aaaa1111.prec", "aaaa1111_vessel.craft",
                 "aaaa1111_ghost.craft", "aaaa1111.pcrf"]
        self.assertEqual([], harvest.orphan_sidecars(names, self.SFS))

    def test_unreferenced_sidecars_are_orphans(self):
        names = ["aaaa1111.prec", "dead0000.prec", "dead0000_vessel.craft"]
        self.assertEqual(["dead0000.prec", "dead0000_vessel.craft"],
                         harvest.orphan_sidecars(names, self.SFS))

    def test_the_membership_test_is_whole_text_not_recording_rows(self):
        """An id referenced by ANY node type (a novel pointer field, a group
        row, a supersede relation) keeps its sidecars - the widest rule, so a
        schema addition can never make the prune destructive."""
        sfs = "GAME { SOME_NOVEL_NODE { anchorRecordingId = bbbb2222 } }"
        self.assertEqual([], harvest.orphan_sidecars(["bbbb2222.prec"], sfs))

    def test_an_empty_or_extensionless_name_is_never_pruned_blindly(self):
        # a defensive shape: a name yielding an empty id token is kept
        self.assertEqual([], harvest.orphan_sidecars([".prec", "_x.craft"],
                                                     self.SFS))


class RewindSaveHintStripTests(unittest.TestCase):
    """The rewind-save clear, over persistent.sfs AND the RewindPoint quicksaves.

    The harvest prunes `Parsek/Saves` (the `parsek_rw_*.sfs` payload), so every
    pointer to it must be cleared or the fixture commits a dangling reference
    (`CommittedFixtureRewindSaveTests`). It used to clear only `rewindSave` in
    persistent.sfs; `bdock-second-dock-recorded`'s quicksave then carried both
    `resumeRewindSave` and `rewindSave` and needed a hand edit (PR #1768)."""

    def test_both_product_keys_are_cleared_and_kept(self):
        text = ("\t\t\tresumeRewindSave = parsek_rw_456043\n"
                "\t\t\t\trewindSave = parsek_rw_456043\n")
        out, cleared, left = harvest.strip_rewind_save_hints(text)
        self.assertEqual("\t\t\tresumeRewindSave = \n\t\t\t\trewindSave = \n", out)
        self.assertEqual(2, cleared)
        self.assertEqual([], left)

    def test_other_values_and_lookalike_keys_are_untouched(self):
        text = ("\t\trewindSaveUT = 123.5\n\t\trewindSave = \n"
                "\t\tname = parsek_rwx\n\t\tid = abc\n")
        out, cleared, left = harvest.strip_rewind_save_hints(text)
        self.assertEqual(text, out)
        self.assertEqual(0, cleared)
        self.assertEqual([], left)

    def test_crlf_line_endings_survive_the_clear(self):
        out, cleared, _ = harvest.strip_rewind_save_hints(
            "a = 1\r\n\trewindSave = parsek_rw_ab12\r\nb = 2\r\n")
        self.assertEqual("a = 1\r\n\trewindSave = \r\nb = 2\r\n", out)
        self.assertEqual(1, cleared)

    def test_a_reference_in_another_shape_is_reported_not_edited(self):
        text = "\tnote = see parsek_rw_ab12 for details\n"
        out, cleared, left = harvest.strip_rewind_save_hints(text)
        self.assertEqual(text, out)
        self.assertEqual(0, cleared)
        self.assertEqual(["1: note = see parsek_rw_ab12 for details"], left)

    def test_the_committed_quicksave_is_what_the_clear_produces(self):
        # Rebuild the source shape of the one quicksave that was hand-edited
        # (restore the pruned name into the two cleared values) and require the
        # clear to reproduce the committed bytes. The run's real source
        # (`2026-09-23_1704` snapshot) is the same text with KSP's CRLF endings;
        # the hand edit wrote it back as LF, so a re-harvest, which keeps line
        # endings, would commit this one file as CRLF. Equal modulo EOL.
        path = os.path.join(os.path.dirname(_HERE), "fixtures", "saves",
                            "bdock-second-dock-recorded", "Parsek", "RewindPoints",
                            "rp_91b25a0c8e904a02a9d40deeb07e1a53.sfs")
        if not os.path.isfile(path):
            self.skipTest("fixture absent: %s" % path)
        with open(path, "rb") as fh:
            committed = fh.read().decode("latin-1")
        source = committed.replace("\t\t\tresumeRewindSave = \n",
                                   "\t\t\tresumeRewindSave = parsek_rw_456043\n", 1)
        source = source.replace("\t\t\t\trewindSave = \n",
                                "\t\t\t\trewindSave = parsek_rw_456043\n", 1)
        self.assertEqual(2, source.count("parsek_rw_456043"))
        out, cleared, left = harvest.strip_rewind_save_hints(source)
        self.assertEqual(2, cleared)
        self.assertEqual([], left)
        self.assertEqual(committed, out)

    # -- the filesystem half, through harvest() itself -----------------------

    def _tempdir(self):
        import shutil
        import tempfile
        path = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, path, True)
        return path

    def _make_recorded_save(self, rp_text, rp_name="rp_1.sfs",
                            sfs_tail="\t\trewindSave = parsek_rw_ab12\n"):
        root = self._tempdir()
        rec = os.path.join(root, "Parsek", "Recordings")
        os.makedirs(rec)
        with open(os.path.join(rec, "abc.prec"), "w") as fh:
            fh.write("payload")
        saves = os.path.join(root, "Parsek", "Saves")
        os.makedirs(saves)
        with open(os.path.join(saves, "parsek_rw_ab12.sfs"), "w") as fh:
            fh.write("payload")
        rps = os.path.join(root, "Parsek", "RewindPoints")
        os.makedirs(rps)
        with open(os.path.join(rps, rp_name), "wb") as fh:
            fh.write(rp_text.encode("latin-1"))
        with open(os.path.join(root, "persistent.sfs"), "w") as fh:
            fh.write(SFS + "RECORDING_STUB { id = abc }\n" + sfs_tail)
        return root

    def _harvest(self, save, force=False, keep_parsek=True, sentinel=False,
                 out_root=None):
        """Harvest into a temp fixtures root. `sentinel=True` pre-creates the
        target holding one file, so a test can see whether a refusal left an
        existing fixture intact."""
        out_root = out_root or self._tempdir()
        target = os.path.join(out_root, "fx")
        if sentinel:
            os.makedirs(target)
            with open(os.path.join(target, "SENTINEL"), "w") as fh:
                fh.write("the existing committed fixture")
        orig = harvest._FIXTURES_SAVES
        harvest._FIXTURES_SAVES = out_root
        try:
            harvest.harvest(save, "fx", "fx", force=force,
                            expected_situations=(), keep_parsek=keep_parsek)
        finally:
            harvest._FIXTURES_SAVES = orig
        return target

    def test_keep_parsek_clears_the_quicksave_and_prunes_the_payload(self):
        rp = ("PARSEK_ACTIVE_TREE\r\n{\r\n\tresumeRewindSave = parsek_rw_ab12\r\n"
              "\tname = caf\xe9\r\n\tRECORDING\r\n\t{\r\n"
              "\t\trewindSave = parsek_rw_ab12\r\n\t}\r\n}\r\n")
        target = self._harvest(self._make_recorded_save(rp))
        with open(os.path.join(target, "Parsek", "RewindPoints", "rp_1.sfs"),
                  "rb") as fh:
            got = fh.read()
        want = rp.replace("parsek_rw_ab12", "").encode("latin-1")
        self.assertEqual(want, got, "only the two values may change, byte for byte")
        self.assertFalse(os.path.isdir(os.path.join(target, "Parsek", "Saves")))
        with open(os.path.join(target, "persistent.sfs")) as fh:
            self.assertNotIn("parsek_rw_", fh.read())

    def test_every_file_in_the_directory_is_cleared_not_only_sfs(self):
        # The corpus cell scans every file under RewindPoints, so the harvest does.
        target = self._harvest(self._make_recorded_save(
            "\trewindSave = parsek_rw_ab12\n", rp_name="rp_1.sfs.tmp"))
        with open(os.path.join(target, "Parsek", "RewindPoints", "rp_1.sfs.tmp")) as fh:
            self.assertEqual("\trewindSave = \n", fh.read())

    def test_an_unclearable_quicksave_reference_refuses_before_writing(self):
        save = self._make_recorded_save("\tnote = see parsek_rw_ab12\n")
        out_root = self._tempdir()
        with self.assertRaises(SystemExit) as ctx:
            self._harvest(save, sentinel=True, out_root=out_root)
        self.assertIn("rp_1.sfs:1: note = see parsek_rw_ab12", str(ctx.exception))
        self.assertEqual(["SENTINEL"], os.listdir(os.path.join(out_root, "fx")),
                         "a refusal must leave the existing fixture untouched")

    def test_an_unclearable_persistent_reference_refuses_in_default_mode(self):
        save = self._make_recorded_save(
            "\trewindSave = \n", sfs_tail="\tnote = see parsek_rw_ab12\n")
        with self.assertRaises(SystemExit) as ctx:
            self._harvest(save, keep_parsek=False)
        self.assertIn("persistent.sfs:", str(ctx.exception))
        self.assertIn("note = see parsek_rw_ab12", str(ctx.exception))

    def test_default_mode_ignores_the_pruned_quicksaves(self):
        # Without --keep-parsek the whole Parsek dir is pruned, so an odd
        # reference inside a quicksave never reaches the fixture and is no reason
        # to refuse.
        target = self._harvest(self._make_recorded_save(
            "\tnote = see parsek_rw_ab12\n"), keep_parsek=False)
        self.assertFalse(os.path.isdir(os.path.join(target, "Parsek")))

    def test_force_writes_through_the_refusal(self):
        target = self._harvest(self._make_recorded_save(
            "\tnote = see parsek_rw_ab12\n\trewindSave = parsek_rw_ab12\n"),
            force=True)
        with open(os.path.join(target, "Parsek", "RewindPoints", "rp_1.sfs")) as fh:
            self.assertEqual("\tnote = see parsek_rw_ab12\n\trewindSave = \n",
                             fh.read())

    def test_the_harvest_resets_the_launch_arguments_in_every_written_save(self):
        leaky = EnvLaunchArgsScrubTests.LEAKY
        save = self._make_recorded_save("\trewindSave = \n" + leaky + "\n",
                                        sfs_tail=leaky + "\n")
        target = self._harvest(save)
        for rel in ("persistent.sfs", os.path.join("Parsek", "RewindPoints", "rp_1.sfs")):
            with open(os.path.join(target, rel)) as fh:
                text = fh.read()
            self.assertNotIn("-logFile", text, rel)
            self.assertIn(EnvLaunchArgsScrubTests.BARE, text, rel)


class EnvLaunchArgsScrubTests(unittest.TestCase):
    """The harness launches KSP with `-logFile <instance>/Player.log`, and KSP
    writes the launch command line into every save's `envInfo`, so an unscrubbed
    harvest commits the operator's install path (their home folder) to a public
    repository. The harvest resets the Args; the corpus cell keeps it that way."""

    LEAKY = ("\tenvInfo =  - Environment Info - Win32NT 7FFFFFFFFFFFFFFF  Args: KSP_x64.exe"
             " -logFile C:\\Users\\someone\\automation\\stock-minimal\\Player.log"
             " -screen-fullscreen 0 - ")
    BARE = "\tenvInfo =  - Environment Info - Win32NT 7FFFFFFFFFFFFFFF  Args: KSP_x64.exe  - "

    def test_the_launch_arguments_are_reset_to_the_bare_form(self):
        text, n = harvest.scrub_env_launch_args("GAME\n" + self.LEAKY + "\r\n\tx = 1\n")
        self.assertEqual(1, n)
        self.assertEqual("GAME\n" + self.BARE + "\r\n\tx = 1\n", text)

    def test_an_already_bare_line_is_left_alone(self):
        text, n = harvest.scrub_env_launch_args(self.BARE + "\n")
        self.assertEqual(0, n)
        self.assertEqual(self.BARE + "\n", text)

    def test_no_committed_fixture_carries_a_launch_path(self):
        saves = os.path.join(os.path.dirname(_HERE), "fixtures", "saves")
        offenders = []
        for dirpath, _, filenames in os.walk(saves):
            for fn in filenames:
                if not fn.endswith(".sfs"):
                    continue
                path = os.path.join(dirpath, fn)
                with open(path, "rb") as fh:
                    text = fh.read().decode("latin-1")
                for line in text.split("\n"):
                    if "envInfo" in line and "Args:" in line and \
                            line.split("Args:", 1)[1].strip() != "KSP_x64.exe  -".strip():
                        offenders.append(os.path.relpath(path, saves).replace("\\", "/"))
                        break
        self.assertEqual([], sorted(offenders),
                         "a committed save carries KSP's launch arguments in envInfo "
                         "(the harness's -logFile path names the operator's home "
                         "folder); harvest_bdock_station.scrub_env_launch_args resets them")


# ---------------------------------------------------------------------------
# The race harvest (--race-folder / --race-name, multiplayer plan T-59).
# ---------------------------------------------------------------------------

import shutil  # noqa: E402
import tempfile  # noqa: E402

import hlib  # noqa: E402

_HASH = "0123456789abcdef"
_RIVAL_REC = "5f0c2a7d9b1e4c3a8d6f0e1b2c3d4e5f"
_OWN_REC = "abc"


def _write(path, text="x"):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)


def _race_listing_rows(root):
    rows = []
    for dirpath, _dirs, files in os.walk(root):
        for name in files:
            full = os.path.join(dirpath, name)
            rows.append((os.path.relpath(full, root).replace(os.sep, "/"),
                         os.path.getsize(full), harvest._digest(full)))
    return rows


class _RaceHarvestBase(unittest.TestCase):
    """A temp harness: results/, fixtures/saves and fixtures/race redirected so a
    cell never writes into the repository."""

    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.root, True)
        self.results = os.path.join(self.root, "results")
        self.saves_out = os.path.join(self.root, "fixtures", "saves")
        self.race_out = os.path.join(self.root, "fixtures", "race")
        for d in (self.results, self.saves_out, self.race_out):
            os.makedirs(d)
        saved = (harvest._RESULTS_DIR, harvest._FIXTURES_SAVES, harvest._FIXTURES_RACE)
        harvest._RESULTS_DIR = self.results
        harvest._FIXTURES_SAVES = self.saves_out
        harvest._FIXTURES_RACE = self.race_out

        def restore():
            (harvest._RESULTS_DIR, harvest._FIXTURES_SAVES,
             harvest._FIXTURES_RACE) = saved
        self.addCleanup(restore)

    def make_race_save(self, run_id="r1"):
        """A produced race save: own recording, the race files beside it, and a
        rival cache holding a readable trajectory mirror (the file the
        OptimizerTransferCohesionTests glob would read as an own recording)."""
        save = os.path.join(self.results, run_id + "_save")
        _write(os.path.join(save, "persistent.sfs"),
               SFS + "RECORDING_STUB { id = %s }\n" % _OWN_REC)
        _write(os.path.join(save, "Ships", "VAB", "X.craft"), "ship = X")
        _write(os.path.join(save, "Parsek", "Recordings", _OWN_REC + ".prec"), "own")
        _write(os.path.join(save, "Parsek", "Recordings", _OWN_REC + ".prec.txt"), "own")
        race = os.path.join(save, "Parsek", "Race")
        _write(os.path.join(race, "local.cfg"), "folder = rs/0")
        _write(os.path.join(race, "race.cfg"), "race")
        _write(os.path.join(race, "own", "player.cfg"), "me")
        _write(os.path.join(race, "own", "payload", "%s.%s.prec" % (_OWN_REC, _HASH)), "own")
        rival = os.path.join(race, "rivals", "p_b")
        _write(os.path.join(rival, "player.cfg"), "rival")
        _write(os.path.join(rival, "payload", "%s.%s.prec.txt" % (_RIVAL_REC, _HASH)),
               "rival trajectory")
        _write(os.path.join(rival, "standings", "000001.pstd"), "s")
        return save

    def make_race_folder(self, run_id="r1", listing=True, leftover=True):
        folder = os.path.join(self.results, run_id + hlib.RACE_SNAPSHOT_DIR_SUFFIX)
        _write(os.path.join(folder, "race.cfg"), "race")
        for player, rec in (("p_a", _OWN_REC), ("p_b", _RIVAL_REC)):
            base = os.path.join(folder, "players", player)
            _write(os.path.join(base, "player.cfg"), player)
            _write(os.path.join(base, "standings", "000001.pstd"), "s1")
            _write(os.path.join(base, "payload", "%s.%s.prec" % (rec, _HASH)), "p")
            _write(os.path.join(base, "payload", "trees", "t_%s.%s.cfg" % (player, _HASH)),
                   "tree")
        rows = _race_listing_rows(folder)
        if leftover:
            # a safe-write leftover: not part of the grammar, never committed
            _write(os.path.join(folder, "players", "p_b", "standings", "000002.pstd.tmp"),
                   "half")
            rows = _race_listing_rows(folder)
        if listing:
            _write(os.path.join(self.results, run_id + hlib.RACE_LISTING_SUFFIX),
                   hlib.format_race_listing(run_id, rows))
        return folder

    def run_harvest(self, save, **kw):
        return harvest.harvest(save, "race-save", "race-save", force=kw.pop("force", False),
                               expected_situations=(), keep_parsek=kw.pop("keep_parsek", True),
                               **kw)


class RaceRivalCachePruneTests(_RaceHarvestBase):
    """Only the rebuildable `Parsek/Race/rivals/` cache leaves a harvested save
    (design 4.4). The cell that fails if the prune stops working is the glob
    one: it walks the harvested fixture exactly as
    `OptimizerTransferCohesionTests.E4_FixtureInventory_NoBodySplitSurvivesAnywhere`
    walks `harness/fixtures/saves` (every `*.prec.txt`, recursively), so a rival
    trajectory left in the fixture would be read there as an own recording."""

    def _prec_txt_glob(self, root):
        found = []
        for dirpath, _dirs, files in os.walk(root):
            found.extend(os.path.relpath(os.path.join(dirpath, n), root).replace(os.sep, "/")
                         for n in files if n.endswith(".prec.txt"))
        return sorted(found)

    def test_the_trajectory_glob_finds_no_rival_file_in_the_harvest(self):
        save = self.make_race_save()
        self.assertEqual(0, self.run_harvest(save))
        target = os.path.join(self.saves_out, "race-save")
        self.assertEqual(["Parsek/Recordings/%s.prec.txt" % _OWN_REC],
                         self._prec_txt_glob(target))
        self.assertFalse(os.path.lexists(os.path.join(target, "Parsek", "Race", "rivals")))

    def test_the_prune_is_exact(self):
        save = self.make_race_save()
        # a directory of the same name anywhere else is not the rival cache
        _write(os.path.join(save, "Parsek", "rivals", "keep.txt"), "k")
        _write(os.path.join(save, "Ships", "rivals", "keep.txt"), "k")
        self.assertEqual(0, self.run_harvest(save))
        target = os.path.join(self.saves_out, "race-save")
        for kept in ("Parsek/Race/local.cfg", "Parsek/Race/race.cfg",
                     "Parsek/Race/own/player.cfg",
                     "Parsek/Race/own/payload/%s.%s.prec" % (_OWN_REC, _HASH),
                     "Parsek/rivals/keep.txt", "Ships/rivals/keep.txt",
                     "Parsek/Recordings/%s.prec" % _OWN_REC):
            self.assertTrue(os.path.isfile(os.path.join(target, *kept.split("/"))), kept)

    def test_the_prune_needs_no_race_flag(self):
        # a race save harvested without --race-folder still drops the cache
        save = self.make_race_save()
        self.assertEqual(0, self.run_harvest(save))
        self.assertFalse(os.path.lexists(os.path.join(
            self.saves_out, "race-save", "Parsek", "Race", "rivals")))

    def test_ignore_filter_matches_only_the_rival_cache_under_the_save_root(self):
        save = os.path.join(self.root, "s")
        ignore = harvest._keep_parsek_ignore(save)
        self.assertIn("rivals", ignore(os.path.join(save, "Parsek", "Race"), ["rivals", "own"]))
        self.assertNotIn("own", ignore(os.path.join(save, "Parsek", "Race"), ["rivals", "own"]))
        self.assertNotIn("rivals", ignore(os.path.join(save, "Parsek"), ["rivals"]))
        self.assertNotIn("rivals", ignore(os.path.join(save, "Ships", "Parsek", "Race"),
                                          ["rivals"]))

    def test_count_rival_cache_files(self):
        save = self.make_race_save()
        self.assertEqual(3, harvest.count_rival_cache_files(save))
        self.assertIsNone(harvest.count_rival_cache_files(os.path.join(self.root, "none")))


class RaceFolderHarvestTests(_RaceHarvestBase):
    def test_the_folder_is_copied_exactly_beside_the_save_fixture(self):
        save = self.make_race_save()
        folder = self.make_race_folder()
        self.assertEqual(0, self.run_harvest(save, race_folder=folder, race_name="flown-a"))
        target = os.path.join(self.race_out, "flown-a")
        want = sorted((p, b, h) for p, b, h in _race_listing_rows(folder)
                      if not p.endswith(".tmp"))
        self.assertEqual(want, sorted(_race_listing_rows(target)))
        self.assertTrue(os.path.isdir(os.path.join(self.saves_out, "race-save")))
        # the name the harvest wrote is one [fixture.race] can stage
        self.assertEqual([], hlib.validate_race_fixture(
            {"race": {"sharedFolder": "fixtures/race/flown-a"}}, ["flown-a"]))

    def test_a_second_harvest_replaces_the_first(self):
        save = self.make_race_save()
        folder = self.make_race_folder()
        stale = os.path.join(self.race_out, "flown-a", "players", "p_z", "player.cfg")
        _write(stale, "old")
        self.assertEqual(0, self.run_harvest(save, race_folder=folder, race_name="flown-a"))
        self.assertFalse(os.path.exists(stale))

    def _assert_refused_before_any_write(self, save, **kw):
        with self.assertRaises(SystemExit) as ctx:
            self.run_harvest(save, **kw)
        self.assertEqual([], os.listdir(self.saves_out), "the save fixture was written")
        self.assertEqual([], os.listdir(self.race_out), "the race fixture was written")
        return str(ctx.exception.code)

    def test_a_folder_outside_results_is_refused(self):
        save = self.make_race_save()
        outside = os.path.join(self.root, "elsewhere", "r9_race")
        _write(os.path.join(outside, "race.cfg"), "race")
        msg = self._assert_refused_before_any_write(save, race_folder=outside,
                                                    race_name="flown-a")
        self.assertIn("not inside", msg)

    def test_a_dotdot_path_is_refused_even_when_it_lands_inside(self):
        save = self.make_race_save()
        self.make_race_folder()
        sneaky = os.path.join(self.results, "x", "..", "r1_race")
        os.makedirs(os.path.join(self.results, "x"))
        msg = self._assert_refused_before_any_write(save, race_folder=sneaky,
                                                    race_name="flown-a")
        self.assertIn("'..'", msg)

    def test_a_symlink_out_of_results_is_refused(self):
        save = self.make_race_save()
        outside = os.path.join(self.root, "elsewhere", "r9_race")
        _write(os.path.join(outside, "race.cfg"), "race")
        link = os.path.join(self.results, "r9_race")
        try:
            os.symlink(outside, link)
        except (OSError, NotImplementedError):
            self.skipTest("symlinks unavailable here")
        msg = self._assert_refused_before_any_write(save, race_folder=link,
                                                    race_name="flown-a")
        self.assertIn("not inside", msg)

    def test_a_link_inside_the_folder_is_refused_even_with_force(self):
        save = self.make_race_save()
        folder = self.make_race_folder()
        outside = os.path.join(self.root, "secret.txt")
        _write(outside, "not race data")
        try:
            os.symlink(outside, os.path.join(folder, "players", "p_a", "payload", "l.prec"))
        except (OSError, NotImplementedError):
            self.skipTest("symlinks unavailable here")
        msg = self._assert_refused_before_any_write(save, race_folder=folder,
                                                    race_name="flown-a", force=True)
        self.assertIn("link", msg)

    def test_the_results_dir_itself_and_a_non_race_dir_are_refused(self):
        _write(os.path.join(self.results, "r1_save", "persistent.sfs"), "x")
        for raw in (self.results, os.path.join(self.results, "r1_save")):
            self.assertNotEqual("", harvest.race_folder_path_problem(raw, self.results), raw)
        self.make_race_folder()
        self.assertEqual("", harvest.race_folder_path_problem(
            os.path.join(self.results, "r1_race"), self.results))

    def test_a_bad_race_name_is_refused_even_with_force(self):
        save = self.make_race_save()
        folder = self.make_race_folder()
        for name in ("../x", "a.b", "a/b", "-x"):
            msg = self._assert_refused_before_any_write(save, race_folder=folder,
                                                        race_name=name, force=True)
            self.assertIn("--race-name", msg)

    def test_the_two_flags_go_together(self):
        save = self.make_race_save()
        folder = self.make_race_folder()
        self._assert_refused_before_any_write(save, race_folder=folder)
        self._assert_refused_before_any_write(save, race_name="flown-a")

    def test_a_file_changed_since_the_run_is_refused(self):
        save = self.make_race_save()
        folder = self.make_race_folder()
        _write(os.path.join(folder, "players", "p_a", "player.cfg"), "edited")
        msg = self._assert_refused_before_any_write(save, race_folder=folder,
                                                    race_name="flown-a")
        self.assertIn("changed since the run", msg)

    def test_a_missing_listing_is_refused_and_force_writes_anyway(self):
        save = self.make_race_save()
        folder = self.make_race_folder(listing=False)
        msg = self._assert_refused_before_any_write(save, race_folder=folder,
                                                    race_name="flown-a")
        self.assertIn("no listing", msg)
        self.assertEqual(0, self.run_harvest(save, race_folder=folder, race_name="flown-a",
                                             force=True))
        self.assertTrue(os.path.isfile(os.path.join(self.race_out, "flown-a", "race.cfg")))

    def test_the_main_entry_point_passes_the_flags(self):
        save = self.make_race_save()
        folder = self.make_race_folder()
        rc = harvest.main(["--save-dir", save, "--target-name", "race-save", "--keep-parsek",
                           "--race-folder", folder, "--race-name", "flown-a"])
        self.assertEqual(0, rc)
        self.assertTrue(os.path.isdir(os.path.join(self.race_out, "flown-a", "players")))


class RaceFolderGrammarTests(unittest.TestCase):
    GOOD = ["race.cfg", "players/p_a/player.cfg", "players/p_a/standings/000001.pstd",
            "players/p_a/standings/000002.pstd",
            "players/p_a/payload/rec1.%s.prec" % _HASH,
            "players/p_a/payload/rec1.%s_ghost.craft" % _HASH,
            "players/p_a/payload/trees/tree1.%s.cfg" % _HASH,
            "players/p_b/player.cfg"]

    def test_a_good_folder_has_no_problem_and_a_summary(self):
        problems, skipped, summary = harvest.race_folder_problems(self.GOOD)
        self.assertEqual([], problems)
        self.assertEqual((), skipped)
        self.assertIn("players=2 recordings=1 trees=1 standings=p_a:2,p_b:0 race.cfg=yes",
                      summary)

    def test_each_grammar_break_is_a_problem(self):
        cases = {
            "notes.txt": "not part of the shared-folder layout",
            "players/p_a/payload/rec1.prec": "fits no part",
            "players/p_c/standings/000001.pstd": "players/p_c/player.cfg: missing",
        }
        for extra, needle in cases.items():
            problems, _s, _sum = harvest.race_folder_problems(self.GOOD + [extra])
            self.assertTrue(any(needle in p for p in problems), (extra, problems))

    def test_no_player_is_a_problem(self):
        problems, _s, _sum = harvest.race_folder_problems(["race.cfg"])
        self.assertTrue(any("no player folder" in p for p in problems))

    def test_safe_write_leftovers_are_skipped_not_refused(self):
        leftover = "players/p_a/standings/000003.pstd.tmp"
        problems, skipped, summary = harvest.race_folder_problems(self.GOOD + [leftover])
        self.assertEqual([], problems)
        self.assertEqual((leftover,), skipped)
        self.assertIn("standings=p_a:2", summary)

    def test_the_chain_seam_reports_unchecked_until_t21(self):
        # T-21's facets plug in at race_chain_facets; until then the harvest
        # says plainly that no standing chain was checked.
        status, detail = harvest.race_chain_facets(self.GOOD)
        self.assertEqual("unchecked", status)
        self.assertIn("T-21", detail)


class RaceListingTests(unittest.TestCase):
    def test_the_listing_hlib_writes_parses_back(self):
        rows = [("players/p_a/player.cfg", 3, "a" * 64), ("race.cfg", 4, "b" * 64),
                ("players/p a/odd name.cfg", 1, "c" * 64)]
        parsed, problems = harvest.parse_race_listing(hlib.format_race_listing("r1", rows))
        self.assertEqual([], problems)
        self.assertEqual({p: (b, h) for p, b, h in rows}, parsed)

    def test_a_wrong_header_or_row_is_a_problem(self):
        _rows, problems = harvest.parse_race_listing("# something else\nzz 1 a\n")
        self.assertTrue(any("does not start with" in p for p in problems))
        _rows, problems = harvest.parse_race_listing(hlib.RACE_LISTING_HEADER + "\nbad\n")
        self.assertTrue(any("line 2" in p for p in problems))

    def test_every_difference_is_named(self):
        listed = {"a": (1, "h1"), "b": (1, "h2"), "c": (1, "h3")}
        actual = {"a": (1, "h1"), "b": (2, "h9"), "d": (1, "h4")}
        problems = harvest.race_listing_problems(listed, actual)
        self.assertEqual(3, len(problems))
        self.assertTrue(problems[0].startswith("b: changed"))
        self.assertTrue(problems[1].startswith("c: in the listing"))
        self.assertTrue(problems[2].startswith("d: in the folder"))


class CommittedFixtureRivalCacheTests(unittest.TestCase):
    """No committed save fixture carries a rival cache (`Parsek/Race/rivals/`):
    its `.prec.txt` mirrors would be globbed as own recordings by
    `OptimizerTransferCohesionTests`, and design 4.4 makes it a cache that is
    never part of a save. The harvest prunes it; this holds the committed corpus."""

    def test_no_save_fixture_holds_a_rival_cache(self):
        saves = os.path.join(os.path.dirname(_HERE), "fixtures", "saves")
        offenders = []
        for dirpath, dirnames, _files in os.walk(saves):
            if (os.path.basename(dirpath) == "Race"
                    and os.path.basename(os.path.dirname(dirpath)) == "Parsek"
                    and "rivals" in dirnames):
                offenders.append(os.path.relpath(os.path.join(dirpath, "rivals"), saves))
        self.assertEqual([], sorted(offenders),
                         "harvest_bdock_station.py prunes Parsek/Race/rivals; re-harvest")
