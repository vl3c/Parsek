"""Drift and shape gates for the five `<source>-career` route fixtures and their six lanes.

The fixtures are BUILT by `harness/tools/build_route_career.py` from committed SANDBOX
route saves: `Mode = CAREER`, `fresh-career`'s PARAMETERS (the source's ParsekSettings
child kept), its Funding / ResearchAndDevelopment / Reputation / facilities / strategy /
contract scenarios, the 29 + 3 granted tech nodes H73's host carries, every facility at
its top level, and a ledger stripped of its SANDBOX starting-value rows, its
award-bearing milestone rows and its legacy supply-route rows. Every other file is the
source's, byte for byte. A hand edit to a fixture, a change to a source save, to
`fresh-career` or to the builder reds here instead of in a flight.

The six lanes (H74-H79) are career copies of RVR-7, RVR-8, H40, H34, H39 and H58: each
must fly its source's driver steps verbatim over the career fixture, so the game mode is
the only variable moved.

Stdlib only; ASCII only; no em dashes.
"""
import importlib.util
import os
import re
import sys
import tomllib
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_route_career.py")
SAVES = os.path.join(HARNESS_ROOT, "fixtures", "saves")
SCENARIOS = os.path.join(HARNESS_ROOT, "scenarios")


def _load_builder():
    sys.path.insert(0, os.path.join(HARNESS_ROOT, "tools"))
    spec = importlib.util.spec_from_file_location("build_route_career", BUILDER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _read(path):
    with open(path, encoding="utf-8", newline="") as fh:
        return fh.read().replace("\r\n", "\n")


def _spec(name):
    with open(os.path.join(SCENARIOS, name), "rb") as fh:
        return tomllib.load(fh)


def _scenario_body(text, name):
    m = re.search(r"\n\t\tname = %s\n(.*?)\n\t}\n" % re.escape(name), text, re.S)
    return m.group(1) if m else None


# What the strip removes from (and keeps in) each source ledger, measured off the
# committed source bytes on 2026-10-21. `seed` = ScienceInitial + ReputationInitial,
# `route` = legacy supply-route rows (types 23-30, 36), `award` = MilestoneAchievement
# rows with a non-zero funds / science / reputation award. gloops-airshow has no ledger.
EXPECTED_LEDGER_COUNTS = {
    "rover-relay-c-recorded": {"seed": 2, "route": 0, "award": 5, "keep": 0},
    "depot-route-recorded": {"seed": 2, "route": 3, "award": 18, "keep": 21},
    "gloops-airshow": None,
    "bdock-recorded": {"seed": 2, "route": 0, "award": 12, "keep": 15},
    "rover-route-recorded": {"seed": 2, "route": 4, "award": 5, "keep": 0},
}

# career lane -> (source lane, source fixture)
LANES = {
    "H74-rover-relay-c-dispatch-career.toml":
        ("RVR-7-rover-relay-c-dispatch.toml", "rover-relay-c-recorded"),
    "H75-rover-relay-c-second-cycle-hold-career.toml":
        ("RVR-8-rover-relay-c-second-cycle-hold.toml", "rover-relay-c-recorded"),
    "H76-logistics-isolated-depot-route-career.toml":
        ("H40-logistics-isolated-depot-route.toml", "depot-route-recorded"),
    "H77-logistics-inter-body-career.toml":
        ("H34-logistics-inter-body.toml", "gloops-airshow"),
    "H78-logistics-isolated-bdock-career.toml":
        ("H39-logistics-isolated-bdock.toml", "bdock-recorded"),
    "H79-route-rewind-to-launch-career.toml":
        ("H58-route-rewind-to-launch.toml", "rover-route-recorded"),
}
# The seam lanes, which carry the all-three-pools gates (pool-write forbids, and the
# ledger oracle where a save point is pinned); the batch lanes carry H73's funds pair.
POOL_GATED = ("H74-rover-relay-c-dispatch-career.toml",
              "H75-rover-relay-c-second-cycle-hold-career.toml",
              "H77-logistics-inter-body-career.toml",
              "H79-route-rewind-to-launch-career.toml")
LEDGER_ORACLE = {"H74-rover-relay-c-dispatch-career.toml": "sum",
                 "H75-rover-relay-c-second-cycle-hold-career.toml": "sum",
                 "H79-route-rewind-to-launch-career.toml": "rewind-aware"}
FUNDS_CELL = ("H76-logistics-isolated-depot-route-career.toml",
              "H78-logistics-isolated-bdock-career.toml")


class RouteCareerFixtureDriftTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.b = _load_builder()

    def test_the_source_set_is_the_five_route_hosts(self):
        self.assertEqual(sorted(EXPECTED_LEDGER_COUNTS), sorted(self.b.SOURCES))
        self.assertEqual(sorted(set(src for _, src in LANES.values())),
                         sorted(self.b.SOURCES))

    def test_every_committed_fixture_is_byte_identical_to_a_fresh_build(self):
        for source in self.b.SOURCES:
            with self.subTest(source=source):
                self.assertEqual([], self.b.committed_problems(source),
                                 "re-run python harness/tools/build_route_career.py")

    def test_only_the_three_rewritten_files_differ_from_the_source(self):
        # Independent of the builder: walk both trees and compare bytes. Recordings,
        # sidecars, baselines, rewind points, AddOns and Ships stay the source's.
        rewritten = {r.replace(os.sep, "/") for r in self.b.REWRITTEN}
        for source in self.b.SOURCES:
            with self.subTest(source=source):
                src_root = os.path.join(SAVES, source)
                dst_root = os.path.join(SAVES, self.b.target_name(source))

                def files(root):
                    out = set()
                    for dirpath, _d, names in os.walk(root):
                        for n in names:
                            out.add(os.path.relpath(os.path.join(dirpath, n), root)
                                    .replace(os.sep, "/"))
                    return out
                src_files, dst_files = files(src_root), files(dst_root)
                self.assertEqual(src_files, dst_files)
                differ = set()
                for rel in src_files:
                    with open(os.path.join(src_root, rel), "rb") as a, \
                            open(os.path.join(dst_root, rel), "rb") as b:
                        if a.read() != b.read():
                            differ.add(rel)
                self.assertTrue(differ <= rewritten, sorted(differ - rewritten))
                self.assertIn("persistent.sfs", differ)
                self.assertIn("persistent.loadmeta", differ)

    def test_the_strip_counts_are_the_measured_ones_and_non_vacuous(self):
        # Every source ledger DID carry the SANDBOX starting-value pair, so the strip is
        # doing work; a re-harvest that changed the population reds here by name.
        for source, want in EXPECTED_LEDGER_COUNTS.items():
            with self.subTest(source=source):
                self.assertEqual(want, self.b.ledger_counts(source))

    def test_no_starting_value_route_or_award_row_remains(self):
        for source in self.b.SOURCES:
            path = os.path.join(SAVES, self.b.target_name(source), "Parsek", "GameState",
                                "ledger.pgld")
            if not os.path.isfile(path):
                continue
            with self.subTest(source=source):
                text = _read(path)
                types = [int(t) for t in re.findall(r"(?m)^\ttype = (\d+)$", text)]
                self.assertFalse(set(types) & {20, 21, 22}, types)
                self.assertFalse(set(types) & (set(range(23, 31)) | {36}), types)
                for key in ("milestoneFundsAwarded", "milestoneSciAwarded",
                            "milestoneRepAwarded"):
                    vals = re.findall(r"(?m)^\t%s = (\S+)$" % key, text)
                    self.assertTrue(all(float(v) == 0.0 for v in vals), (key, vals))
                self.assertNotIn("initialScience", text)
                self.assertNotIn("initialReputation", text)
                self.assertNotIn("initialFunds", text)
                self.assertEqual(EXPECTED_LEDGER_COUNTS[source]["keep"], len(types))

    def test_career_shape(self):
        for source in self.b.SOURCES:
            with self.subTest(source=source):
                text = _read(os.path.join(SAVES, self.b.target_name(source),
                                          "persistent.sfs"))
                self.assertRegex(text, r"(?m)^\tMode = CAREER$")
                self.assertNotRegex(text, r"(?m)^\tMode = SANDBOX$")
                self.assertIn("\t\tfunds = 500000", _scenario_body(text, "Funding"))
                self.assertIn("\t\tsci = 100",
                              _scenario_body(text, "ResearchAndDevelopment"))
                self.assertIn("\t\trep = 0", _scenario_body(text, "Reputation"))
                fac = _scenario_body(text, "ScenarioUpgradeableFacilities")
                levels = re.findall(r"\n\t\t\tlvl = (\S+)", fac)
                self.assertEqual(10, len(levels))
                self.assertEqual({"1"}, set(levels))
                for name in ("StrategySystem", "ScenarioContractEvents", "ContractSystem",
                             "ParsekScenario"):
                    self.assertIsNotNone(_scenario_body(text, name), name)
                meta = _read(os.path.join(SAVES, self.b.target_name(source),
                                          "persistent.loadmeta"))
                self.assertIn("gameMode = CAREER", meta)

    def test_only_the_career_surfaces_differ_inside_the_save(self):
        # Strip GAME's Title / Description / Mode, PARAMETERS and the seven inserted
        # career scenarios from the career save, and PARAMETERS + the three lines from
        # the source: what is left (FLIGHTSTATE, ROSTER, every other scenario) must be
        # identical. The source's ParsekSettings must survive inside PARAMETERS.
        inserted = [n for n, _a, _w in self.b.CAREER_SCENARIOS]

        def strip(text, drop_scenarios):
            out = re.sub(r"\n\tPARAMETERS\n\t\{.*?\n\t\}\n", "\n", text, flags=re.S)
            for name in drop_scenarios:
                out = re.sub(r"\n\tSCENARIO\n\t\{\n\t\tname = %s\n.*?\n\t\}\n" % name,
                             "\n", out, flags=re.S)
            return re.sub(r"\n\t(Title|Description|Mode) = [^\n]*", "", out)
        for source in self.b.SOURCES:
            with self.subTest(source=source):
                src = _read(os.path.join(SAVES, source, "persistent.sfs"))
                dst = _read(os.path.join(SAVES, self.b.target_name(source),
                                         "persistent.sfs"))
                self.assertEqual(strip(src, []), strip(dst, inserted))
                ps = re.compile(r"\n\t\tParsekSettings\n\t\t\{.*?\n\t\t\}\n", re.S)
                self.assertEqual(ps.findall(src), ps.findall(dst))
                self.assertEqual(1, len(ps.findall(dst)))

    def test_the_tech_grant_is_h73s(self):
        logi = _read(os.path.join(SAVES, "logi-cargo-career", "persistent.sfs"))

        def tech_ids(text):
            return re.findall(r"\n\t\t\tid = (\w+)\n",
                              _scenario_body(text, "ResearchAndDevelopment"))
        for source in self.b.SOURCES:
            with self.subTest(source=source):
                text = _read(os.path.join(SAVES, self.b.target_name(source),
                                          "persistent.sfs"))
                self.assertEqual(tech_ids(logi), tech_ids(text))
                self.assertEqual(33, len(tech_ids(text)))

    def test_fresh_career_is_an_input_not_an_output(self):
        self.assertNotIn("fresh-career", self.b.SOURCES)
        self.assertNotIn(self.b.BASE_NAME + self.b.TARGET_SUFFIX,
                         [self.b.target_name(s) for s in self.b.SOURCES])


class RouteCareerLaneTests(unittest.TestCase):

    def test_each_lane_flies_its_source_driver_verbatim_over_the_career_copy(self):
        for name, (source_name, fixture) in sorted(LANES.items()):
            with self.subTest(lane=name):
                lane, src = _spec(name), _spec(source_name)
                self.assertEqual("fixtures/saves/%s-career" % fixture,
                                 lane["fixture"]["saveTemplate"])
                self.assertEqual("fixtures/saves/%s" % fixture,
                                 src["fixture"]["saveTemplate"])
                self.assertEqual(src["driver"], lane["driver"])
                self.assertEqual(src["fixture"]["injectedRecordings"],
                                 lane["fixture"]["injectedRecordings"])
                self.assertEqual(src["expectations"]["recordings"]["count"],
                                 lane["expectations"]["recordings"]["count"])
                self.assertIn("career", lane["dimensionsCovered"]["D14"])
                self.assertNotIn("sandbox", lane["dimensionsCovered"]["D14"])
                self.assertTrue(lane["description"].startswith(
                    source_name[:-len(".toml")] + "-career style"), name)
                # All six flew green on their first flights (2026-10-21_2017 ..
                # _2025); the tag says so.
                self.assertIn("flown", lane["tags"])
                self.assertNotIn("never-flown", lane["tags"])

    def test_each_lane_keeps_its_source_tokens(self):
        # Every source token stays, the tally line included: each batch lane's first
        # flight measured its source's split exactly (H76 35/0/12, H77 2/0/45, H78
        # 34/0/13), so its whole pin is the source's line byte for byte.
        for name, (source_name, _fixture) in sorted(LANES.items()):
            with self.subTest(lane=name):
                lane = _spec(name)["expectations"]["logContracts"]
                src = _spec(source_name)["expectations"]["logContracts"]
                for pat in src["required"]:
                    self.assertIn(pat, lane["required"])
                for pat in src["forbidden"]:
                    self.assertIn(pat, lane["forbidden"])

    def test_every_lane_header_says_not_fit_for_ledger_ground_truth(self):
        for name in LANES:
            with self.subTest(lane=name):
                self.assertIn("NOT FIT FOR LEDGER GROUND TRUTH",
                              _read(os.path.join(SCENARIOS, name)))

    def test_the_career_proof_tokens(self):
        for name in LANES:
            with self.subTest(lane=name):
                lc = _spec(name)["expectations"]["logContracts"]
                self.assertIn("Seeded initial funds: amount=500000,", lc["required"])
                self.assertIn("Seeded initial science: amount=100,", lc["required"])
                self.assertIn("Built route .* mode=SANDBOX", lc["forbidden"])
                if name != "H77-logistics-inter-body-career.toml":
                    self.assertIn("Built route id=.* mode=CAREER", lc["required"])

    def test_the_pool_gates(self):
        write_forbids = ("Patch(Funds|Science): -?[0-9]+\\.[0-9] -> ",
                         "PatchReputation: -?[0-9]+\\.[0-9]+ -> ",
                         "GUARDED (DRAWDOWN|UPLIFT) clamped resource=",
                         "refusing to treat current (funds|science|reputation) as initial")
        # The forbids must match the patcher's write line and not its no-op line.
        self.assertRegex("PatchFunds: 500000.0 -> 500001.0 (delta=1.0, target=500001.0)",
                         write_forbids[0])
        self.assertNotRegex("PatchFunds: no change needed (current=500000.0, "
                            "target=500000.0)", write_forbids[0])
        self.assertRegex("PatchReputation: 0.00 -> 5.00 (target=5.00", write_forbids[1])
        for name in POOL_GATED:
            with self.subTest(lane=name):
                spec = _spec(name)
                for pat in write_forbids:
                    self.assertIn(pat, spec["expectations"]["logContracts"]["forbidden"])
        for name, model in LEDGER_ORACLE.items():
            with self.subTest(lane=name):
                ledger = _spec(name)["expectations"]["ledger"]
                self.assertEqual("template", ledger["seedFrom"])
                self.assertEqual("default", ledger["tolerances"])
                self.assertEqual(model, ledger.get("timeline", "sum"))
                self.assertNotIn("manifest", ledger)
                self.assertEqual(["funds", "science", "reputation"],
                                 _spec(name)["dimensionsCovered"]["D8"])
        for name in FUNDS_CELL:
            with self.subTest(lane=name):
                lc = _spec(name)["expectations"]["logContracts"]
                self.assertIn("RewindRedeliver_InGame: funds unchanged net=", lc["required"])
                self.assertIn("RewindRedeliver_InGame: no Funding singleton \\(Sandbox\\)",
                              lc["forbidden"])
                self.assertEqual(["funds"], _spec(name)["dimensionsCovered"]["D8"])

    def test_no_lane_arms_a_save_structure_block_before_its_reading_run(self):
        for name in LANES:
            with self.subTest(lane=name):
                exp = _spec(name)["expectations"]
                for block in (exp.get("routes"),
                              (exp.get("recordings") or {}).get("structure")):
                    if block is not None:
                        self.assertIs(False, block.get("gating"))


if __name__ == "__main__":
    unittest.main()
