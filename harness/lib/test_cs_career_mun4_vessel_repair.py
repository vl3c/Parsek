"""Drift and shape gates for the `cs-career-mun4-vessel-repair` fixture (CT-3's host).

The fixture is DERIVED from `cs-career-mun4` by `harness/tools/build_cs_career_mun4_vessel_repair.py`:
RSQ-1's career plus one Offered stock `VesselRepairContract` and the R&D experimental count of the
EVA repair kit its requirement reads. A hand edit to either save, or a change to the derivation,
reds here instead of in a flight whose contract expired before the accept.

Stdlib only; ASCII only; no em dashes.
"""
import importlib.util
import os
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_cs_career_mun4_vessel_repair.py")
FIXTURE = os.path.join(HARNESS_ROOT, "fixtures", "saves", "cs-career-mun4-vessel-repair", "persistent.sfs")
SOURCE = os.path.join(HARNESS_ROOT, "fixtures", "saves", "cs-career-mun4", "persistent.sfs")
SPEC = os.path.join(HARNESS_ROOT, "scenarios", "CT-3-vessel-repair-rebuilt-at-space-center.toml")


def _load_builder():
    spec = importlib.util.spec_from_file_location("build_cs_career_mun4_vessel_repair", BUILDER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _read(path):
    with open(path, encoding="utf-8", newline="") as fh:
        return fh.read()


class CsCareerMun4VesselRepairDriftTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.b = _load_builder()
        cls.sfs = _read(FIXTURE)

    def test_the_committed_fixture_is_byte_identical_to_a_fresh_derivation(self):
        self.assertEqual([], self.b.check(), "re-run python harness/tools/build_cs_career_mun4_vessel_repair.py")

    def test_the_built_save_verifies(self):
        self.assertEqual([], self.b.verify(self.sfs))

    def test_the_derivation_only_adds_the_contract_and_the_count(self):
        src, out = _read(SOURCE).split("\n"), self.sfs.split("\n")
        added = len(self.b.contract_lines(0)) + 4
        self.assertEqual(len(src) + added, len(out))
        # Every source line survives in order: removing the added lines gives the source back.
        start = out.index(self.b.CONTRACT_HEADER, out.index("\t\t\t\tguid = " + self.b.REPAIR_GUID) - 2)
        end = start + len(self.b.contract_lines(0))
        exp = out.index("\t\tExpParts")
        stripped = out[:exp] + out[exp + 4:start] + out[end:]
        self.assertEqual(src, stripped)

    def test_the_offer_outlives_the_lane(self):
        lines = [l.strip() for l in self.sfs.split("\n")]
        at = lines.index("guid = " + self.b.REPAIR_GUID)
        values = next(l for l in lines[at:at + 12] if l.startswith("values = "))
        expire = float(values[len("values = "):].split(",")[8])
        # CT-3's last step is the warp to 185980; the offer must still stand at the accept.
        self.assertGreater(expire, 186000.0)

    def test_verify_catches_a_missing_repair_kit_count(self):
        broken = self.sfs.replace("\t\t\tevaRepairKit = 1\n", "")
        self.assertNotEqual(self.sfs, broken)
        self.assertTrue(any("ExpParts" in p for p in self.b.verify(broken)))

    def test_verify_catches_an_absolute_craft_path(self):
        broken = self.sfs.replace("roverCraftDef = GameData/", "roverCraftDef = C:/KSP/KSP_x64_Data/../GameData/")
        self.assertNotEqual(self.sfs, broken)
        self.assertTrue(any("roverCraftDef" in p for p in self.b.verify(broken)))

    def test_the_lane_names_the_fixture_the_contract_and_the_vessel(self):
        spec = _read(SPEC)
        self.assertIn('saveTemplate = "fixtures/saves/cs-career-mun4-vessel-repair"', spec)
        self.assertIn(self.b.REPAIR_GUID, spec)
        self.assertIn(self.b.VESSEL_NAME, spec)


if __name__ == "__main__":
    unittest.main()
