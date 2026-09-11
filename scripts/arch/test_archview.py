"""Unit tests for scripts/arch/archview.py.

Run from the repo root:

    python -m unittest scripts/arch/test_archview.py

Stdlib only. The final test is a smoke test that scans the real Source/Parsek
tree, so it exercises the extractor end to end.
"""

import pathlib
import sys
import tempfile
import unittest

_HERE = pathlib.Path(__file__).resolve().parent
if str(_HERE) not in sys.path:
    sys.path.insert(0, str(_HERE))

import archview  # noqa: E402

REPO_ROOT = _HERE.parents[1]
REAL_SOURCE = REPO_ROOT / "Source" / "Parsek"

RULES = [
    {"name": "Missions", "prefix": "^Mission"},
    {"name": "Logistics", "folder": "Logistics"},
    {"name": "Core", "prefix": ".*"},
]


class AssignModuleTests(unittest.TestCase):
    def test_folder_rule_beats_prefix_rule(self):
        rules = [
            {"name": "Missions", "prefix": "^Mission"},
            {"name": "Logistics", "folder": "Logistics"},
            {"name": "Core", "prefix": ".*"},
        ]
        self.assertEqual(archview.assign_module("Logistics/MissionRoute.cs", rules), "Logistics")

    def test_first_matching_prefix_wins(self):
        rules = [
            {"name": "First", "prefix": "^Mission"},
            {"name": "Second", "prefix": "^Miss"},
        ]
        self.assertEqual(archview.assign_module("MissionStore.cs", rules), "First")

    def test_catch_all_catches_unmatched_root_file(self):
        self.assertEqual(archview.assign_module("RandomThing.cs", RULES), "Core")

    def test_unknown_folder_is_unclassified(self):
        self.assertIsNone(archview.assign_module("NewFolder/Thing.cs", RULES))

    def test_backslash_paths_are_accepted(self):
        self.assertEqual(archview.assign_module("Logistics\\RouteModule.cs", RULES), "Logistics")

    def test_bin_obj_and_properties_directories_are_skipped(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            for directory in ("bin", "obj", "Properties", "Logistics"):
                (root / directory).mkdir()
            (root / "bin" / "Skipped.cs").write_text("class Skipped {}", encoding="utf-8")
            (root / "obj" / "Skipped.cs").write_text("class Skipped {}", encoding="utf-8")
            (root / "Properties" / "Skipped.cs").write_text("class Skipped {}", encoding="utf-8")
            (root / "Logistics" / "Kept.cs").write_text("class Kept {}", encoding="utf-8")
            (root / "Root.cs").write_text("class Root {}", encoding="utf-8")
            found = [path.as_posix() for path in archview.iter_source_files(root)]
        self.assertEqual(found, ["Logistics/Kept.cs", "Root.cs"])


class StripTests(unittest.TestCase):
    def test_line_comment_inside_string_does_not_strip_the_rest(self):
        source = 'var url = "http://example.test/x"; var after = 1;'
        stripped = archview.strip_comments_and_strings(source)
        self.assertIn("after", stripped)
        self.assertNotIn("http", stripped)

    def test_block_comment_spanning_lines_is_removed(self):
        source = "class Alpha {}\n/* hidden\n   hidden */\nclass Bravo {}"
        stripped = archview.strip_comments_and_strings(source)
        self.assertIn("Alpha", stripped)
        self.assertIn("Bravo", stripped)
        self.assertNotIn("hidden", stripped)

    def test_line_comment_is_removed(self):
        source = "class Alpha {} // class Bravo {}\nclass Charlie {}"
        stripped = archview.strip_comments_and_strings(source)
        self.assertIn("Alpha", stripped)
        self.assertIn("Charlie", stripped)
        self.assertNotIn("Bravo", stripped)

    def test_type_name_in_string_literal_is_not_a_reference(self):
        source = 'var message = "MissionStore";'
        type_modules = {"MissionStore": "Missions"}
        stripped = archview.strip_comments_and_strings(source)
        self.assertEqual(archview.references(stripped, type_modules, "Core"), [])

    def test_verbatim_string_is_stripped(self):
        source = 'var path = @"C:\\Temp\\MissionStore"; var after = 1;'
        stripped = archview.strip_comments_and_strings(source)
        self.assertNotIn("MissionStore", stripped)
        self.assertIn("after", stripped)


class DeclaredTypesAndReferencesTests(unittest.TestCase):
    SNIPPET = """
    namespace Demo
    {
        internal class LogisticsThing { }

        internal class MissionThing { }

        internal class UsesBoth
        {
            LogisticsThing first;
            MissionThing second;
        }
    }
    """

    def test_declared_types(self):
        stripped = archview.strip_comments_and_strings(self.SNIPPET)
        self.assertEqual(
            archview.declared_types(stripped),
            ["LogisticsThing", "MissionThing", "UsesBoth"],
        )

    def test_references_from_a_third_module(self):
        stripped = archview.strip_comments_and_strings(self.SNIPPET)
        type_modules = {
            "LogisticsThing": "Logistics",
            "MissionThing": "Missions",
            "UsesBoth": "Core",
        }
        self.assertEqual(
            archview.references(stripped, type_modules, "Core"),
            [("LogisticsThing", "Logistics"), ("MissionThing", "Missions")],
        )

    def test_references_exclude_own_module(self):
        stripped = archview.strip_comments_and_strings(self.SNIPPET)
        type_modules = {
            "LogisticsThing": "Logistics",
            "MissionThing": "Missions",
            "UsesBoth": "Logistics",
        }
        self.assertEqual(
            archview.references(stripped, type_modules, "Logistics"),
            [("MissionThing", "Missions")],
        )

    def test_declared_types_ignore_short_names(self):
        stripped = archview.strip_comments_and_strings("class Key { } class Router { }")
        self.assertEqual(archview.declared_types(stripped), ["Router"])


class MetricsTests(unittest.TestCase):
    def test_pure_sink_is_zero_and_pure_source_is_one(self):
        result = archview.metrics(
            {"Source": 2, "Sink": 3},
            [("Source", "Sink", 5)],
        )
        self.assertEqual(result["Sink"]["fanIn"], 5)
        self.assertEqual(result["Sink"]["fanOut"], 0)
        self.assertEqual(result["Sink"]["instability"], 0.0)
        self.assertEqual(result["Source"]["fanOut"], 5)
        self.assertEqual(result["Source"]["fanIn"], 0)
        self.assertEqual(result["Source"]["instability"], 1.0)

    def test_isolated_module_has_zero_instability(self):
        result = archview.metrics({"Lonely": 1}, [])
        self.assertEqual(result["Lonely"]["instability"], 0.0)

    def test_mixed_module_splits(self):
        result = archview.metrics(
            {"A": 1, "B": 1, "C": 1},
            [("A", "B", 3), ("C", "A", 1)],
        )
        self.assertAlmostEqual(result["A"]["instability"], 0.75)


class SccTests(unittest.TestCase):
    def test_two_node_cycle_is_one_component(self):
        components = archview.sccs(["A", "B"], [("A", "B"), ("B", "A")])
        self.assertEqual(len(components), 1)
        self.assertEqual(sorted(components[0]), ["A", "B"])

    def test_chain_is_n_components(self):
        components = archview.sccs(["A", "B", "C"], [("A", "B"), ("B", "C")])
        self.assertEqual(len(components), 3)

    def test_self_loop_does_not_join_foreign_nodes(self):
        components = archview.sccs(["A", "B"], [("A", "A"), ("A", "B")])
        self.assertEqual(len(components), 2)


class CheckerHelperTests(unittest.TestCase):
    def test_parse_edge_spec(self):
        self.assertEqual(archview.parse_edge_spec("Missions -> Logistics"), ("Missions", "Logistics"))

    def test_parse_edge_spec_rejects_bad_input(self):
        with self.assertRaises(ValueError):
            archview.parse_edge_spec("Missions Logistics")

    def test_two_way_couplings(self):
        model = {
            "modules": [
                {"name": "A", "tooling": False},
                {"name": "B", "tooling": False},
                {"name": "C", "tooling": True},
            ],
            "edges": [
                {"from": "A", "to": "B", "weight": 2},
                {"from": "B", "to": "A", "weight": 5},
                {"from": "A", "to": "C", "weight": 9},
                {"from": "C", "to": "A", "weight": 9},
            ],
        }
        pairs = archview.two_way_couplings(model)
        self.assertEqual(pairs, [("A", "B", 2, 5)])


@unittest.skipUnless(REAL_SOURCE.is_dir(), "Source/Parsek is not present")
class RealTreeSmokeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        rules, tooling, _forbidden, _allowed = archview.load_rules(archview.DEFAULT_MODULES)
        cls.model = archview.build_model(REAL_SOURCE, rules, tooling)
        cls.by_name = {module["name"]: module for module in cls.model["modules"]}

    def test_logio_is_a_pure_sink(self):
        self.assertIn("LogIO", self.by_name)
        self.assertLess(self.by_name["LogIO"]["instability"], 0.1)

    def test_logistics_reads_missions(self):
        edges = {(edge["from"], edge["to"]) for edge in self.model["edges"]}
        self.assertIn(("Logistics", "Missions"), edges)

    def test_every_source_file_is_classified(self):
        self.assertEqual(self.model["unclassified"], [])


if __name__ == "__main__":
    unittest.main()
