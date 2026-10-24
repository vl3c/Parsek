"""Fixture gates for `tourist-splashdown-career` (the TOUR-1 host).

The fixture is DERIVED by `harness/tools/build_tourist_splashdown_career.py` from
`kerbin-splashdown-recorded`, `fresh-career` (the career donor) and the stock-written tourist
and TourismContract of `Source/Parsek.Tests/Fixtures/C1Career/persistent.sfs`, so a change
to any of them must re-run the builder. These cells turn that "must" into a red and pin what
TOUR-1 depends on: the tourist is seated in the landed capsule as type Tourist, her contract
is Active with no advance and no penalty, the ledger holds its accept row below the save
clock (without it `KspStatePatcher.PatchContracts` drops the contract), and every recording
sidecar is the source's byte for byte. The lane's pinned guid and tourist name are read off
the spec so the two cannot drift apart.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import sys
import tomllib
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)
_SAVES = os.path.join(_HARNESS, "fixtures", "saves")
_SPEC = os.path.join(_HARNESS, "scenarios", "TOUR-1-tourist-spawn-stays-tourist.toml")


def _load_builder():
    tools = os.path.join(_HARNESS, "tools")
    if tools not in sys.path:
        sys.path.insert(0, tools)
    path = os.path.join(tools, "build_tourist_splashdown_career.py")
    spec = importlib.util.spec_from_file_location("build_tourist_splashdown_career", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class TouristSplashdownCareerFixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.files = cls.builder.build_files()

    def test_committed_bytes_match_the_builder(self):
        self.assertEqual([], self.builder.check())

    def test_the_fixture_verifies(self):
        self.assertEqual([], self.builder.verify(self.files))

    def test_recording_sidecars_are_the_sources_byte_for_byte(self):
        src = os.path.join(_SAVES, self.builder.SOURCE)
        moved = {self.builder.SFS, self.builder.LOADMETA, self.builder.LEDGER}
        for rel, data in self.files.items():
            if rel in moved:
                continue
            with open(os.path.join(src, rel), "rb") as fh:
                self.assertEqual(fh.read(), data, rel)

    def test_the_parsek_scenario_node_is_untouched(self):
        def parsek_node(text):
            start = text.index("\t\tname = ParsekScenario\n")
            return text[start:text.index("\n\t}\n", start)]
        with open(os.path.join(_SAVES, self.builder.SOURCE, "persistent.sfs"), "rb") as fh:
            src = fh.read().decode("utf-8").replace("\r\n", "\n")
        out = self.files["persistent.sfs"].decode("utf-8").replace("\r\n", "\n")
        self.assertEqual(parsek_node(src), parsek_node(out))

    def test_the_spec_pins_this_fixtures_guid_and_tourist(self):
        with open(_SPEC, "rb") as fh:
            spec = tomllib.load(fh)
        self.assertEqual("fixtures/saves/" + self.builder.TARGET, spec["fixture"]["saveTemplate"])
        cancel = [s for s in spec["driver"]["steps"] if s.get("cmd") == "KscAction"]
        self.assertEqual(1, len(cancel))
        self.assertEqual("cancel-contract", cancel[0]["args"]["action"])
        self.assertEqual(self.builder.CONTRACT_GUID, cancel[0]["args"]["contract"])
        required = "\n".join(spec["expectations"]["logContracts"]["required"])
        self.assertIn("Created missing tourist '%s'" % self.builder.TOURIST, required)
        self.assertIn("Tourist hold: '%s'" % self.builder.TOURIST, required)

    def test_the_tourist_is_named_only_in_the_live_save(self):
        # The Kerbal X recordings carry Jebediah only: the tourist rides the one flight
        # the lane records, so the spawn under test is that flight's.
        tourist = self.builder.TOURIST.encode("utf-8")
        for rel, data in self.files.items():
            if rel == self.builder.SFS:
                continue
            self.assertNotIn(tourist, data, rel)


if __name__ == "__main__":
    unittest.main()
