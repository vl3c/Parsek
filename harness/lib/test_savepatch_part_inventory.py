"""Tests for `savepatch`'s `[[fixture.partInventory]]` surface (the ground-science
cluster lanes, 2026-09-28).

The surface seeds a FLIGHTSTATE container part's stored parts in stock's own compact
form: set (or insert) the ModuleInventoryPart module's lowercase `inventory` CSV and
drop its `STOREDPARTS` child, so KSP's `ModuleInventoryPart.OnLoad` builds every
stored part from the part prefab. Nothing else in the save may move.

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

SPLASHDOWN_SFS = os.path.join(HARNESS_ROOT, "fixtures", "saves",
                              "kerbin-splashdown-recorded", "persistent.sfs")
MUN_LANDING_SFS = os.path.join(HARNESS_ROOT, "fixtures", "saves",
                               "mun-landing-recorded", "persistent.sfs")

# The landed Kerbal X capsule both host fixtures carry (same craft, same baked pid).
CAPSULE_PID = 2708531065
ASTEROID_PID = 2815888106

_SYNTHETIC = "\n".join([
    "GAME",
    "{",
    "\tFLIGHTSTATE",
    "\t{",
    "\t\tactiveVessel = 0",
    "\t\tVESSEL",
    "\t\t{",
    "\t\t\tpersistentId = 111",
    "\t\t\tname = Alpha",
    "\t\t\tPART",
    "\t\t\t{",
    "\t\t\t\tname = mk1-3pod",
    "\t\t\t\tMODULE",
    "\t\t\t\t{",
    "\t\t\t\t\tname = ModuleInventoryPart",
    "\t\t\t\t\tisEnabled = True",
    "\t\t\t\t\tSTOREDPARTS",
    "\t\t\t\t\t{",
    "\t\t\t\t\t\tSTOREDPART",
    "\t\t\t\t\t\t{",
    "\t\t\t\t\t\t\tslotIndex = 0",
    "\t\t\t\t\t\t}",
    "\t\t\t\t\t}",
    "\t\t\t\t\tUPGRADESAPPLIED",
    "\t\t\t\t\t{",
    "\t\t\t\t\t}",
    "\t\t\t\t}",
    "\t\t\t}",
    "\t\t\tPART",
    "\t\t\t{",
    "\t\t\t\tname = parachuteLarge",
    "\t\t\t}",
    "\t\t}",
    "\t\tVESSEL",
    "\t\t{",
    "\t\t\tpersistentId = 222",
    "\t\t\tname = Beta",
    "\t\t\tPART",
    "\t\t\t{",
    "\t\t\t\tname = mk1-3pod",
    "\t\t\t\tMODULE",
    "\t\t\t\t{",
    "\t\t\t\t\tname = ModuleInventoryPart",
    "\t\t\t\t\tinventory = evaChute",
    "\t\t\t\t}",
    "\t\t\t}",
    "\t\t}",
    "\t}",
    "}",
    "",
])

_ALPHA = {"pid": 111, "part": "mk1-3pod",
          "inventory": "DeployedRTG,DeployedSeismicSensor,DeployedGoExOb"}


def _lines(text):
    return text.replace("\r\n", "\n").split("\n")


def _spec(part_inventory):
    fixture = {"saveTemplate": "fixtures/saves/kerbin-splashdown-recorded",
               "injectedRecordings": "none", "craft": []}
    if part_inventory is not None:
        fixture["partInventory"] = part_inventory
    return {
        "schema": hlib.SCHEMA_VERSION,
        "id": "PARTINV-probe",
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


class ApplyPartInventoryTests(unittest.TestCase):

    def test_no_entries_is_an_identity(self):
        self.assertEqual((_SYNTHETIC, []), savepatch.apply_part_inventory(_SYNTHETIC, []))

    def test_inserts_the_csv_beside_the_module_name_and_drops_only_storedparts(self):
        out, notes = savepatch.apply_part_inventory(_SYNTHETIC, [_ALPHA])
        lines = _lines(out)
        at = lines.index("\t\t\t\t\tname = ModuleInventoryPart")
        self.assertEqual(
            "\t\t\t\t\tinventory = DeployedRTG,DeployedSeismicSensor,DeployedGoExOb",
            lines[at + 1])
        self.assertNotIn("STOREDPARTS", out)
        self.assertNotIn("slotIndex = 0", out)
        # The sibling UPGRADESAPPLIED node, the other part and Beta survive.
        self.assertIn("\t\t\t\t\tUPGRADESAPPLIED", lines)
        self.assertIn("\t\t\t\tname = parachuteLarge", lines)
        self.assertEqual(1, out.count("inventory = evaChute"))
        # Seven STOREDPARTS lines out, one inventory line in.
        self.assertEqual(len(_lines(_SYNTHETIC)) - 7 + 1, len(lines))
        self.assertEqual(
            ["pid=111 name=Alpha part=mk1-3pod inventory -->"
             "DeployedRTG,DeployedSeismicSensor,DeployedGoExOb storedPartsDropped=1"],
            notes)

    def test_rewrites_an_existing_csv_in_place(self):
        entry = {"pid": 222, "part": "mk1-3pod", "inventory": "DeployedRTG"}
        out, notes = savepatch.apply_part_inventory(_SYNTHETIC, [entry])
        self.assertEqual(len(_lines(_SYNTHETIC)), len(_lines(out)))
        self.assertIn("\t\t\t\t\tinventory = DeployedRTG", _lines(out))
        self.assertNotIn("inventory = evaChute", out)
        self.assertIn("inventory evaChute->DeployedRTG storedPartsDropped=0", notes[0])
        # Alpha's stored part is untouched by an entry that names Beta.
        self.assertIn("slotIndex = 0", out)

    def test_is_idempotent(self):
        once, _ = savepatch.apply_part_inventory(_SYNTHETIC, [_ALPHA])
        twice, notes = savepatch.apply_part_inventory(once, [_ALPHA])
        self.assertEqual(once, twice)
        self.assertIn("storedPartsDropped=0", notes[0])

    def test_preserves_crlf(self):
        crlf = _SYNTHETIC.replace("\n", "\r\n")
        out, _ = savepatch.apply_part_inventory(crlf, [_ALPHA])
        self.assertNotIn("\n", out.replace("\r\n", ""))
        self.assertIn("inventory = DeployedRTG,DeployedSeismicSensor,DeployedGoExOb\r\n", out)

    def test_fails_closed_on_an_unknown_vessel(self):
        with self.assertRaises(savepatch.LiveStatePatchError) as ctx:
            savepatch.apply_part_inventory(_SYNTHETIC, [dict(_ALPHA, pid=999)])
        self.assertIn("persistentId 999", str(ctx.exception))

    def test_fails_closed_on_a_part_the_vessel_does_not_carry(self):
        with self.assertRaises(savepatch.LiveStatePatchError) as ctx:
            savepatch.apply_part_inventory(_SYNTHETIC, [dict(_ALPHA, part="mk1pod.v2")])
        self.assertIn("0 PART(s)", str(ctx.exception))

    def test_fails_closed_on_a_part_without_an_inventory_module(self):
        with self.assertRaises(savepatch.LiveStatePatchError) as ctx:
            savepatch.apply_part_inventory(_SYNTHETIC, [dict(_ALPHA, part="parachuteLarge")])
        self.assertIn("0 ModuleInventoryPart", str(ctx.exception))

    def _committed(self, path):
        with open(path, "rb") as fh:
            return fh.read().decode("utf-8")

    def test_both_host_fixtures_take_the_patch_on_the_capsule_only(self):
        for path in (SPLASHDOWN_SFS, MUN_LANDING_SFS):
            text = self._committed(path)
            entry = {"pid": CAPSULE_PID, "part": "mk1-3pod",
                     "inventory": "DeployedRTG,DeployedSeismicSensor,DeployedGoExOb"}
            out, notes = savepatch.apply_part_inventory(text, [entry])
            self.assertEqual(1, len(notes), path)
            # The fixture's pod container was empty, so nothing was dropped but
            # the node itself, and the new key sits inside the capsule's module.
            self.assertIn("storedPartsDropped=0", notes[0])
            self.assertEqual(
                1, out.count("inventory = DeployedRTG,DeployedSeismicSensor,DeployedGoExOb"))
            lines = _lines(out)
            vessels = [(n, p, s) for n, p, s in savepatch.flightstate_vessels(lines)]
            capsule = [s for n, p, s in vessels if p == str(CAPSULE_PID)][0]
            self.assertIn(
                "inventory = DeployedRTG,DeployedSeismicSensor,DeployedGoExOb",
                "\n".join(lines[capsule[0]:capsule[1]]))
            # Every other vessel is byte-identical.
            before = savepatch.flightstate_vessels(_lines(text))
            for (n0, p0, s0), (n1, p1, s1) in zip(before, vessels):
                self.assertEqual(p0, p1)
                if p0 != str(CAPSULE_PID):
                    self.assertEqual(_lines(text)[s0[0]:s0[1]], lines[s1[0]:s1[1]])

    def test_the_cluster_hosts_asteroid_removal_keeps_the_capsule_focused(self):
        """EVA-9 / EVA-10 remove the index-0 asteroid so the capsule becomes vessel
        0 (what a Space Center save focuses). The removal must re-point the focus
        onto the same capsule, and the part patch must still find it by pid."""
        for path in (SPLASHDOWN_SFS, MUN_LANDING_SFS):
            text = self._committed(path)
            out, live = savepatch.apply_live_state(
                text, [{"pid": ASTEROID_PID, "remove": True}])
            self.assertIn("activeVessel=1->0 removed=1", live[0], path)
            lines = _lines(out)
            vessels = savepatch.flightstate_vessels(lines)
            self.assertEqual(str(CAPSULE_PID), vessels[0][1])
            self.assertEqual(0, savepatch._active_vessel_index(lines))
            out2, notes = savepatch.apply_part_inventory(
                out, [{"pid": CAPSULE_PID, "part": "mk1-3pod", "inventory": "DeployedRTG"}])
            self.assertEqual(1, len(notes))


class ValidatePartInventoryTests(unittest.TestCase):

    def test_shapes(self):
        v = savepatch.validate_part_inventory
        self.assertEqual([], v({}))
        self.assertEqual([], v({"partInventory": [_ALPHA]}))
        self.assertTrue(v({"partInventory": []}))
        self.assertTrue(v({"partInventory": {"pid": 1}}))
        self.assertTrue(v({"partInventory": [dict(_ALPHA, pid="111")]}))
        self.assertTrue(v({"partInventory": [dict(_ALPHA, pid=True)]}))
        self.assertTrue(v({"partInventory": [dict(_ALPHA, pid=0)]}))
        self.assertTrue(v({"partInventory": [dict(_ALPHA, part="")]}))
        self.assertTrue(v({"partInventory": [dict(_ALPHA, part="mk1 pod")]}))
        self.assertTrue(v({"partInventory": [dict(_ALPHA, inventory="a, b")]}))
        self.assertTrue(v({"partInventory": [dict(_ALPHA, inventory="a,,b")]}))
        self.assertTrue(v({"partInventory": [dict(_ALPHA, slots=3)]}))
        self.assertTrue(v({"partInventory": [_ALPHA, dict(_ALPHA)]}))
        # The same part name on two different vessels is two containers.
        self.assertEqual([], v({"partInventory": [_ALPHA, dict(_ALPHA, pid=222)]}))

    def test_a_good_block_adds_no_error(self):
        base = hlib.validate_spec(_spec(None), {}, bug_ids=[])
        good = hlib.validate_spec(_spec([_ALPHA]), {}, bug_ids=[])
        self.assertEqual(base.errors, good.errors)

    def test_a_bad_block_reaches_validate_spec(self):
        bad = hlib.validate_spec(_spec([dict(_ALPHA, part="")]), {}, bug_ids=[])
        self.assertTrue(any("partInventory" in e for e in bad.errors), bad.errors)


class CommittedSpecUsageTests(unittest.TestCase):
    """Every committed spec declaring partInventory stages cleanly against its own
    committed fixture bytes, together with the liveState / crewInventory it
    declares beside it, in run.py's order. A declaration that could only fail at
    staging would otherwise surface as INVALID(staging) on a prepared instance
    under the machine lock."""

    def test_every_declaring_spec_patches_its_own_fixture(self):
        import tomllib
        scenarios = os.path.join(HARNESS_ROOT, "scenarios")
        declaring = []
        for name in sorted(n for n in os.listdir(scenarios) if n.endswith(".toml")):
            with open(os.path.join(scenarios, name), "rb") as fh:
                spec = tomllib.load(fh)
            fixture = spec.get("fixture") or {}
            entries = savepatch.declared_part_inventory(fixture)
            if not entries:
                continue
            declaring.append(name)
            template = fixture.get("saveTemplate") or ""
            sfs = os.path.join(HARNESS_ROOT, template, "persistent.sfs")
            with open(sfs, "rb") as fh:
                text = fh.read().decode("utf-8")
            leaf = template.rsplit("/", 1)[-1]
            text, _ = savepatch.apply_live_state(
                text, savepatch.declared_live_state(fixture), leaf)
            text, _ = savepatch.apply_crew_inventory(
                text, savepatch.declared_crew_inventory(fixture))
            out, notes = savepatch.apply_part_inventory(text, entries)
            self.assertEqual(len(entries), len(notes), name)
            for entry in entries:
                self.assertIn("inventory = %s" % entry["inventory"], out, name)
        # The three ground-science cluster lanes, at least.
        for expected in ("EVA-8-ground-science-cluster-place.toml",
                         "EVA-9-ground-science-cluster-spawn-flight.toml",
                         "EVA-10-mun-ground-science-cluster.toml"):
            self.assertIn(expected, declaring)


if __name__ == "__main__":
    unittest.main()
