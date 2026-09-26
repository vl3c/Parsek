"""Tests for `savepatch`'s `[[fixture.crewInventory]]` surface (coverage wave 10).

The surface writes a roster kerbal's personal inventory in stock's own compact form:
rewrite the INVENTORY node's `inventory` CSV and drop its `STOREDPARTS` child, so
KSP's `ModuleInventoryPart.OnLoad` builds every stored part from the part prefab.
Nothing else in the save may move.

Runnable with the stdlib runner only::

    cd harness && python -m unittest discover -s lib -q
"""

import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(HERE)
TOOLS_DIR = os.path.join(HARNESS_ROOT, "tools")
for _p in (HARNESS_ROOT, HERE, TOOLS_DIR):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import hlib  # noqa: E402
import savepatch  # noqa: E402
import build_career_pad_craft as nodebase  # noqa: E402

GLOOPS_SFS = os.path.join(HARNESS_ROOT, "fixtures", "saves", "gloops-airshow",
                          "persistent.sfs")

_SYNTHETIC = "\n".join([
    "GAME",
    "{",
    "\tROSTER",
    "\t{",
    "\t\tKERBAL",
    "\t\t{",
    "\t\t\tname = Jebediah Kerman",
    "\t\t\tINVENTORY",
    "\t\t\t{",
    "\t\t\t\tinventory = evaChute,evaJetpack",
    "\t\t\t\tSTOREDPARTS",
    "\t\t\t\t{",
    "\t\t\t\t\tSTOREDPART",
    "\t\t\t\t\t{",
    "\t\t\t\t\t\tslotIndex = 0",
    "\t\t\t\t\t}",
    "\t\t\t\t}",
    "\t\t\t\tUPGRADESAPPLIED",
    "\t\t\t\t{",
    "\t\t\t\t}",
    "\t\t\t}",
    "\t\t}",
    "\t\tKERBAL",
    "\t\t{",
    "\t\t\tname = Bill Kerman",
    "\t\t\tINVENTORY",
    "\t\t\t{",
    "\t\t\t\tinventory = evaChute,evaJetpack",
    "\t\t\t}",
    "\t\t}",
    "\t}",
    "}",
    "",
])

_JEB = {"kerbal": "Jebediah Kerman", "inventory": "evaChute,DeployedSeismicSensor"}


def _lines(text):
    return text.replace("\r\n", "\n").split("\n")


def _spec(crew_inventory):
    fixture = {"saveTemplate": "fixtures/saves/gloops-airshow",
               "injectedRecordings": "none", "craft": []}
    if crew_inventory is not None:
        fixture["crewInventory"] = crew_inventory
    return {
        "schema": hlib.SCHEMA_VERSION,
        "id": "CREWINV-probe",
        "tier": "nightly",
        "instanceProfile": "stock-minimal",
        "fixture": fixture,
        "driver": {"kind": "seam", "steps": [
            {"cmd": "LoadGame", "args": {"save": "${runSave}"}, "expect": "OK"},
            {"cmd": "FlushAndQuit", "expect": "OK"},
        ]},
        "expectations": {"allowedAnomalies": []},
        "runtime": {"budgetSeconds": 600},
        "retry": {"policy": "once"},
    }


class ApplyCrewInventoryTests(unittest.TestCase):

    def test_no_entries_is_an_identity(self):
        self.assertEqual((_SYNTHETIC, []), savepatch.apply_crew_inventory(_SYNTHETIC, []))

    def test_rewrites_the_csv_and_drops_only_storedparts(self):
        out, notes = savepatch.apply_crew_inventory(_SYNTHETIC, [_JEB])
        lines = _lines(out)
        self.assertIn("\t\t\t\tinventory = evaChute,DeployedSeismicSensor", lines)
        self.assertNotIn("STOREDPARTS", out)
        self.assertNotIn("slotIndex = 0", out)
        # The sibling UPGRADESAPPLIED node and Bill's untouched inventory survive.
        self.assertIn("\t\t\t\tUPGRADESAPPLIED", lines)
        self.assertEqual(1, out.count("inventory = evaChute,evaJetpack"))
        # Exactly the seven STOREDPARTS lines left.
        self.assertEqual(len(_lines(_SYNTHETIC)) - 7, len(lines))
        self.assertEqual(1, len(notes))
        self.assertIn("storedPartsDropped=1", notes[0])

    def test_is_idempotent(self):
        once, _ = savepatch.apply_crew_inventory(_SYNTHETIC, [_JEB])
        twice, notes = savepatch.apply_crew_inventory(once, [_JEB])
        self.assertEqual(once, twice)
        self.assertIn("storedPartsDropped=0", notes[0])

    def test_preserves_crlf(self):
        crlf = _SYNTHETIC.replace("\n", "\r\n")
        out, _ = savepatch.apply_crew_inventory(crlf, [_JEB])
        self.assertNotIn("\n", out.replace("\r\n", ""))
        self.assertIn("inventory = evaChute,DeployedSeismicSensor\r\n", out)

    def test_fails_closed_on_an_unknown_kerbal(self):
        with self.assertRaises(savepatch.LiveStatePatchError):
            savepatch.apply_crew_inventory(
                _SYNTHETIC, [{"kerbal": "Valentina Kerman", "inventory": "evaChute"}])

    def test_fails_closed_without_an_inventory_node(self):
        text = _SYNTHETIC.replace("\t\t\tname = Bill Kerman\n\t\t\tINVENTORY",
                                  "\t\t\tname = Bill Kerman\n\t\t\tNOTINVENTORY")
        self.assertNotEqual(text, _SYNTHETIC)
        with self.assertRaises(savepatch.LiveStatePatchError):
            savepatch.apply_crew_inventory(
                text, [{"kerbal": "Bill Kerman", "inventory": "evaChute"}])

    def test_fails_closed_without_a_roster(self):
        with self.assertRaises(savepatch.LiveStatePatchError):
            savepatch.apply_crew_inventory("GAME\n{\n}\n", [_JEB])

    def test_the_committed_gloops_fixture_takes_the_patch(self):
        with open(GLOOPS_SFS, "rb") as fh:
            text = fh.read().decode("utf-8")
        out, notes = savepatch.apply_crew_inventory(text, [_JEB])
        self.assertIn("storedPartsDropped=1", notes[0])
        self.assertIn("inventory = evaChute,DeployedSeismicSensor", out)
        # Only Jebediah's node moved: every other kerbal keeps its stored parts.
        self.assertEqual(text.count("STOREDPARTS") - 1, out.count("STOREDPARTS"))
        # Nothing outside the ROSTER changed.
        before, after = _lines(text), _lines(out)
        r0 = nodebase.find_node(before, "ROSTER")
        r1 = nodebase.find_node(after, "ROSTER")
        self.assertEqual(before[:r0[0]], after[:r1[0]])
        self.assertEqual(before[r0[1]:], after[r1[1]:])


class ValidateCrewInventoryTests(unittest.TestCase):

    def test_shapes(self):
        v = savepatch.validate_crew_inventory
        self.assertEqual([], v({}))
        self.assertEqual([], v({"crewInventory": [_JEB]}))
        self.assertTrue(v({"crewInventory": []}))
        self.assertTrue(v({"crewInventory": {"kerbal": "x"}}))
        self.assertTrue(v({"crewInventory": [{"kerbal": "", "inventory": "a"}]}))
        self.assertTrue(v({"crewInventory": [{"kerbal": "x", "inventory": "a, b"}]}))
        self.assertTrue(v({"crewInventory": [{"kerbal": "x", "inventory": "a,,b"}]}))
        self.assertTrue(v({"crewInventory": [{"kerbal": "x", "inventory": "a", "slot": 1}]}))
        self.assertTrue(v({"crewInventory": [_JEB, dict(_JEB)]}))

    def test_a_good_block_adds_no_error(self):
        base = hlib.validate_spec(_spec(None), {}, bug_ids=[])
        good = hlib.validate_spec(_spec([_JEB]), {}, bug_ids=[])
        self.assertEqual(base.errors, good.errors)

    def test_a_bad_block_reaches_validate_spec(self):
        bad = hlib.validate_spec(_spec([{"kerbal": "", "inventory": "a"}]), {}, bug_ids=[])
        self.assertTrue(any("crewInventory" in e for e in bad.errors), bad.errors)


if __name__ == "__main__":
    unittest.main()
