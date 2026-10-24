"""Fixture + spec gates for `career-requirements-loss-pad` and
`L14-contract-requirements-loss-terminal-row`.

The fixture is `career-science-pad` plus one Active `PartTest` whose requirement
(`FirstLaunch` complete) is false in a career that never launched, and its ledger
accept row. Getting any of its literals wrong costs a flight that reads like a product
defect: a FirstLaunch node anywhere makes the requirement true and the lane measures
nothing; an advance moves the HARD-gated funds pool; a deadline in reach lets PrePass
fail the contract on its own; an accept past the save clock is pruned on load. Each is a
cell below, and the committed bytes must be what the builder makes from the current
donors.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import re
import unittest

try:
    import tomllib
except ImportError:  # pragma: no cover - Python < 3.11
    import tomli as tomllib  # type: ignore

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)
_SAVES = os.path.join(_HARNESS, "fixtures", "saves")

FIXTURE_DIR = os.path.join(_SAVES, "career-requirements-loss-pad")
FIXTURE_SFS = os.path.join(FIXTURE_DIR, "persistent.sfs")
FIXTURE_META = os.path.join(FIXTURE_DIR, "persistent.loadmeta")
FIXTURE_LEDGER = os.path.join(FIXTURE_DIR, "Parsek", "GameState", "ledger.pgld")
BASE_DIR = os.path.join(_SAVES, "career-science-pad")
DONOR_SFS = os.path.join(_SAVES, "career-earned-pad", "persistent.sfs")
SPEC_PATH = os.path.join(_HARNESS, "scenarios",
                         "L14-contract-requirements-loss-terminal-row.toml")


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_career_requirements_loss_pad.py")
    spec = importlib.util.spec_from_file_location(
        "build_career_requirements_loss_pad", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _raw(path):
    with open(path, "rb") as fh:
        return fh.read()


class CareerRequirementsLossPadFixtureDriftTests(unittest.TestCase):
    """WIRES `build_career_requirements_loss_pad.py --check` INTO THE SUITE."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.base = cls.builder.read_lines(os.path.join(BASE_DIR, "persistent.sfs"))
        cls.donor = cls.builder.read_lines(DONOR_SFS)

    def test_the_committed_fixture_satisfies_every_post_condition(self):
        b = self.builder
        self.assertEqual([], b.verify(b.read_lines(FIXTURE_SFS), self.base,
                                      b.read_lines(FIXTURE_LEDGER)))

    def test_the_committed_save_is_byte_identical_to_a_fresh_rebuild(self):
        b = self.builder
        produced = "\r\n".join(b.build(self.base, self.donor, b.TITLE)).encode("utf-8")
        self.assertEqual(_raw(FIXTURE_SFS), produced,
                         "career-requirements-loss-pad/persistent.sfs has drifted from "
                         "build_career_requirements_loss_pad.py over the current donors; "
                         "re-run the builder and commit")

    def test_the_committed_loadmeta_and_ledger_are_the_recipe(self):
        b = self.builder
        meta = b.build_loadmeta(b.read_lines(os.path.join(BASE_DIR, "persistent.loadmeta")))
        self.assertEqual(_raw(FIXTURE_META), "\r\n".join(meta).encode("utf-8"))
        self.assertEqual(_raw(FIXTURE_LEDGER), "\r\n".join(b.LEDGER_LINES).encode("utf-8"))

    def test_the_requirement_is_false_in_the_committed_save(self):
        # The whole experiment: PartTest.MeetRequirements() reads
        # ProgressTracking.NodeComplete("FirstLaunch"), so no FirstLaunch anywhere.
        text = _raw(FIXTURE_SFS).decode("utf-8")
        self.assertNotIn("FirstLaunch", text)
        self.assertIn("\t\t\t\ttype = PartTest\r\n", text)
        self.assertIn("\t\t\t\tstate = Active\r\n", text)

    def test_a_first_launch_node_would_be_caught(self):
        # Mutation check of the verify gate: a FirstLaunch node in the Progress node reds.
        b = self.builder
        lines = b.read_lines(FIXTURE_SFS)
        idx = next(i for i, l in enumerate(lines) if l.strip() == "Progress")
        mutated = lines[:idx + 2] + ["\t\t\tFirstLaunch", "\t\t\t{",
                                     "\t\t\t\tcompleted = 1", "\t\t\t}"] + lines[idx + 2:]
        problems = b.verify(mutated, None, b.read_lines(FIXTURE_LEDGER))
        self.assertTrue(any("FirstLaunch" in p for p in problems), problems)

    def test_the_contract_and_the_row_agree(self):
        b = self.builder
        values = b.SPLICED_VALUES.split(",")
        self.assertEqual(b.CONTRACT_ACCEPT_UT, values[9])
        self.assertEqual(b.CONTRACT_DEADLINE_UT, values[10])
        self.assertEqual(b.CONTRACT_FUNDS_PENALTY, values[4])
        self.assertEqual(b.CONTRACT_REP_PENALTY, values[7])
        self.assertEqual("0", values[2])
        self.assertLess(float(b.CONTRACT_ACCEPT_UT), b.SAVE_CLOCK_UT)


class L14SpecFixtureSyncTests(unittest.TestCase):
    """The spec's pinned literals are the fixture's own."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        with open(SPEC_PATH, "rb") as fh:
            cls.spec = tomllib.load(fh)

    def test_the_spec_flies_this_fixture(self):
        self.assertEqual("fixtures/saves/career-requirements-loss-pad",
                         self.spec["fixture"]["saveTemplate"])

    def test_every_guid_pin_names_the_fixture_contract(self):
        lc = self.spec["expectations"]["logContracts"]
        guids = set()
        for pattern in lc["required"] + lc["forbidden"]:
            guids.update(re.findall(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
                                    pattern))
        self.assertEqual({self.builder.CONTRACT_GUID}, guids)

    def test_the_spec_carries_an_empty_ledger_manifest_seeded_from_the_template(self):
        ledger = self.spec["expectations"]["ledger"]
        self.assertEqual("template", ledger["seedFrom"])
        self.assertNotIn("manifest", ledger)

    def test_the_fix_literals_are_required(self):
        required = "\n".join(self.spec["expectations"]["logContracts"]["required"])
        for literal in ("source='ContractOfferExpired'",
                        "ended by stock as OfferExpired",
                        "requirementsLost=True"):
            self.assertIn(literal, required)


if __name__ == "__main__":
    unittest.main()
