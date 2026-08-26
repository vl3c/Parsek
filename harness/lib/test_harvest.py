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
