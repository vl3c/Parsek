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


if __name__ == "__main__":
    unittest.main()
