"""Unit tests for scripts/arch/archview.py.

Run from the repo root:

    python -m unittest scripts/arch/test_archview.py

Stdlib only. The final test is a smoke test that scans the real Source/Parsek
tree, so it exercises the extractor end to end.
"""

import contextlib
import io
import json
import pathlib
import re
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

    def test_preprocessor_directive_lines_are_removed(self):
        source = "class Alpha {}\n    #region Spawn Decision\nclass Bravo {}\n#endregion\n"
        stripped = archview.strip_comments_and_strings(source)
        self.assertIn("Alpha", stripped)
        self.assertIn("Bravo", stripped)
        self.assertNotIn("Decision", stripped)
        self.assertNotIn("endregion", stripped)

    def test_hash_inside_a_line_is_not_a_directive(self):
        source = "var color = Parse(x); Decision d = null; // #region not here\nint y = 1;"
        stripped = archview.strip_comments_and_strings(source)
        self.assertIn("Decision d", stripped)
        self.assertIn("int y", stripped)


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

    def test_plain_method_call_sharing_a_type_name_is_not_a_reference(self):
        type_modules = {"Decision": "GameActions"}
        source = "return Decision(true, false, x);"
        self.assertEqual(archview.references(source, type_modules, "Ghost"), [])
        source = "var d = new Decision(true);"
        self.assertEqual(archview.references(source, type_modules, "Ghost"), [("Decision", "GameActions")])
        source = "Decision d = Make(); List<Decision> all;"
        self.assertEqual(archview.references(source, type_modules, "Ghost"), [("Decision", "GameActions")])

    def test_qualified_constructor_call_is_a_reference(self):
        type_modules = {
            "SeamNodeRecord": "Logistics",
            "AnchorFrame": "Rendering",
            "ParentAnchoredChild": "Rendering",
        }
        source = "var x = new RouteProofCapture.SeamNodeRecord();"
        self.assertEqual(
            archview.references(source, type_modules, "Recorder"),
            [("SeamNodeRecord", "Logistics")],
        )
        source = "var x = new AnchorFrame.ParentAnchoredChild(a, b);"
        self.assertEqual(
            archview.references(source, type_modules, "Recorder"),
            [("AnchorFrame", "Rendering"), ("ParentAnchoredChild", "Rendering")],
        )

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


class TypeDeclarationTests(unittest.TestCase):
    def test_modifiers_bases_and_where_clause(self):
        source = (
            "[SomeAttr(1)]\n"
            "public sealed partial class OuterThing : MonoBehaviour, Parsek.Logistics.Route, List<FooThing>\n"
            "    where T : class\n"
            "{\n"
            "    private static class InnerHelper : IRunnable { int Value; }\n"
            "}\n"
        )
        declaration_list = archview.type_declarations(source)
        declarations = {d["name"]: d for d in declaration_list}
        outer = declarations["OuterThing"]
        self.assertEqual(outer["kind"], "class")
        self.assertEqual(outer["modifiers"], {"public", "sealed", "partial"})
        self.assertEqual(outer["bases"], ["MonoBehaviour", "Route", "List"])
        self.assertIsNone(outer["enclosing"])
        helper = declarations["InnerHelper"]
        self.assertEqual(helper["enclosing"], "OuterThing")
        self.assertEqual(declaration_list[helper["parent"]]["name"], "OuterThing")

    def test_span_is_the_brace_body(self):
        source = "class BodyThing\n{\n    int Value;\n}\nclass NextThing { }"
        declarations = {d["name"]: d for d in archview.type_declarations(source)}
        start, end = declarations["BodyThing"]["span"]
        self.assertEqual(source[start], "{")
        self.assertEqual(source[end - 1], "}")
        self.assertEqual(source[start:end], "{\n    int Value;\n}")

    def test_record_declarations_are_ignored(self):
        source = "record PointPair(int X, int Y);\nrecord Pair(int X) { }\nclass AfterRecord { }"
        self.assertEqual(archview.declared_types(source), ["AfterRecord"])
        self.assertEqual([d["name"] for d in archview.type_declarations(source)], ["AfterRecord"])

    def test_record_class_and_struct_declarations_are_ignored(self):
        source = (
            "public record struct PairThing(int X);\n"
            "internal record class WrapThing(int Y);\n"
            "class AfterThing { }\n"
        )
        declarations = archview.type_declarations(source)
        self.assertEqual([d["name"] for d in declarations], ["AfterThing"])
        start, end = declarations[0]["span"]
        self.assertEqual(source[start:end], "{ }")

    def test_enum_underlying_type_is_not_a_base(self):
        declarations = archview.type_declarations("internal enum ModeThing : byte { A, B }")
        self.assertEqual(declarations[0]["kind"], "enum")
        self.assertEqual(declarations[0]["bases"], [])

    def test_interface_bases_are_kept(self):
        declarations = archview.type_declarations("interface IReaderThing : IBaseThing { }")
        self.assertEqual(declarations[0]["bases"], ["IBaseThing"])


def _role_of(snippet):
    return {d["name"]: archview.type_role(d) for d in archview.type_declarations(snippet)}


class TypeRoleTests(unittest.TestCase):
    def test_one_snippet_per_role(self):
        self.assertEqual(
            _role_of("[KSPAddon(KSPAddon.Startup.Flight, false)]\npublic class AddonThing { }")[
                "AddonThing"
            ],
            "entry",
        )
        self.assertEqual(_role_of("class MonoThing : MonoBehaviour { }")["MonoThing"], "entry")
        self.assertEqual(_role_of("interface IReaderThing { }")["IReaderThing"], "interface")
        self.assertEqual(_role_of("abstract class BaseThing { }")["BaseThing"], "abstract")
        self.assertEqual(_role_of("internal enum ModeThing { A, B }")["ModeThing"], "enum")
        self.assertEqual(_role_of("static class HelperThing { }")["HelperThing"], "static")
        self.assertEqual(
            _role_of("class ImplThing : IReaderThing { }")["ImplThing"], "implements"
        )
        self.assertEqual(_role_of("internal struct PairThing { public int X; }")["PairThing"], "data")
        self.assertEqual(_role_of("class DataThing { public int X = 1; }")["DataThing"], "data")
        self.assertEqual(_role_of("class ServiceThing { void Work() { } }")["ServiceThing"], "service")

    def test_order_static_beats_implements(self):
        self.assertEqual(_role_of("static class BothThing : IReaderThing { }")["BothThing"], "static")

    def test_order_interface_beats_bases(self):
        self.assertEqual(_role_of("interface IChildThing : IBaseThing { }")["IChildThing"], "interface")

    def test_harmony_patch_attribute_is_entry(self):
        roles = _role_of("[HarmonyPatch(typeof(FooThing))]\nclass PatchThing { }")
        self.assertEqual(roles["PatchThing"], "entry")


class TypeReferenceTests(unittest.TestCase):
    def test_reference_inside_nested_type_is_not_credited_to_outer(self):
        source = "class OuterThing { AlphaThing a; class InnerThing { BetaThing b; } GammaThing c; }"
        declarations = archview.type_declarations(source)
        table = {"OuterThing", "InnerThing", "AlphaThing", "BetaThing", "GammaThing"}
        refs = archview.type_references(source, declarations, table)
        self.assertEqual(refs["OuterThing"], {"AlphaThing", "GammaThing"})
        self.assertEqual(refs["InnerThing"], {"BetaThing"})

    def test_plain_method_call_is_not_a_reference(self):
        source = "class CallerThing { void Run() { Decision(true); var d = new Decision(); } }"
        declarations = archview.type_declarations(source)
        refs = archview.type_references(source, declarations, {"CallerThing", "Decision"})
        self.assertEqual(refs["CallerThing"], {"Decision"})


class TypeLevelTests(unittest.TestCase):
    def test_chain_levels(self):
        levels = archview.type_levels({"AThing": ["BThing"], "BThing": ["CThing"], "CThing": []})
        self.assertEqual(levels["CThing"], 0)
        self.assertEqual(levels["BThing"], 1)
        self.assertEqual(levels["AThing"], 2)

    def test_cycle_shares_one_level(self):
        levels = archview.type_levels(
            {"AThing": ["BThing"], "BThing": ["AThing"], "CThing": ["AThing"]}
        )
        self.assertEqual(levels["AThing"], levels["BThing"])
        self.assertEqual(levels["CThing"], levels["AThing"] + 1)

    def test_type_with_no_references_is_level_zero(self):
        self.assertEqual(archview.type_levels({"LonelyThing": []})["LonelyThing"], 0)


class TypeFanInTests(unittest.TestCase):
    def test_fanin_counts_files_not_references(self):
        file_references = {"a.cs": {"FooThing"}, "b.cs": {"FooThing"}, "c.cs": {"BarThing"}}
        self.assertEqual(archview.type_fanin("FooThing", file_references, {"a.cs"}), 1)
        self.assertEqual(archview.type_fanin("FooThing", file_references, set()), 2)
        self.assertEqual(archview.type_fanin("BarThing", file_references, set()), 1)

    def test_fanin_counts_files_not_other_names_in_them(self):
        file_references = {"a.cs": {"FooThing", "BarThing", "BazThing"}, "b.cs": {"FooThing"}}
        self.assertEqual(archview.type_fanin("FooThing", file_references, set()), 2)
        self.assertEqual(archview.type_fanin("BarThing", file_references, set()), 1)
        self.assertEqual(archview.type_fanin("BazThing", file_references, set()), 1)


def _place_evidence(name, external, by_module=None):
    by = sorted(by_module or [], key=lambda item: (-item[1], item[0]))
    return {
        "file": name,
        "types": 1,
        "knotTypes": 0,
        "externalRefs": external,
        "byModule": by,
        "share": (by[0][1] / external) if (external and by) else 0.0,
        "hub": "",
        "hubFanIn": 0,
    }


class PlacementEvidenceTests(unittest.TestCase):
    @staticmethod
    def _model():
        return {
            "catchAll": "CatchAll",
            "fileModules": {
                "AlphaFile.cs": "CatchAll",
                "LocalFile.cs": "CatchAll",
                "EmptyFile.cs": "CatchAll",
                "BetaFile.cs": "Beta",
                "GammaFile.cs": "Gamma",
            },
            "types": [
                {
                    "name": "AlphaThing",
                    "file": "AlphaFile.cs",
                    "module": "CatchAll",
                    "knot": 1,
                    "fanIn": 5,
                    "referencedBy": ["BetaThing", "BetaThingTwo", "GammaThing", "LocalThing"],
                },
                {
                    "name": "LocalThing",
                    "file": "LocalFile.cs",
                    "module": "CatchAll",
                    "knot": None,
                    "fanIn": 1,
                    "referencedBy": [],
                },
                {
                    "name": "BetaThing",
                    "file": "BetaFile.cs",
                    "module": "Beta",
                    "knot": None,
                    "fanIn": 1,
                    "referencedBy": [],
                },
                {
                    "name": "BetaThingTwo",
                    "file": "BetaFile.cs",
                    "module": "Beta",
                    "knot": None,
                    "fanIn": 0,
                    "referencedBy": [],
                },
                {
                    "name": "GammaThing",
                    "file": "GammaFile.cs",
                    "module": "Gamma",
                    "knot": None,
                    "fanIn": 0,
                    "referencedBy": [],
                },
            ],
        }

    def test_share_and_bymodule_count_only_other_modules(self):
        evidence = archview.placement_evidence(self._model())
        self.assertEqual(
            [entry["file"] for entry in evidence],
            ["AlphaFile.cs", "EmptyFile.cs", "LocalFile.cs"],
        )
        alpha = evidence[0]
        self.assertEqual(alpha["types"], 1)
        self.assertEqual(alpha["knotTypes"], 1)
        self.assertEqual(alpha["externalRefs"], 3)
        self.assertEqual(alpha["byModule"], [("Beta", 2), ("Gamma", 1)])
        self.assertAlmostEqual(alpha["share"], 2 / 3)
        self.assertEqual(alpha["hub"], "AlphaThing")
        self.assertEqual(alpha["hubFanIn"], 5)
        local = evidence[2]
        self.assertEqual(local["externalRefs"], 0)
        self.assertEqual(local["byModule"], [])

    def test_file_with_no_external_references_reports_zero(self):
        evidence = {entry["file"]: entry for entry in archview.placement_evidence(self._model())}
        empty = evidence["EmptyFile.cs"]
        self.assertEqual(empty["types"], 0)
        self.assertEqual(empty["externalRefs"], 0)
        self.assertEqual(empty["share"], 0.0)
        self.assertEqual(empty["byModule"], [])


class PlaceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.rules = archview.default_placement_rules()

    def test_r1_family_wins_over_r2(self):
        entry = _place_evidence("GuiTreeFunnels.cs", 100, [("Logistics", 100)])
        self.assertEqual(archview.place(entry, self.rules), ("GuiTree", "R1", ""))

    def test_r1_families(self):
        cases = {
            "GameStateEvent.cs": "GameActions",
            "ParsekSettings.cs": "Config",
            "ParsekUI.cs": "UI",
            "MapRenderTrace.cs": "MapRender",
            "RouteProofMetadata.cs": "Logistics",
            "PartEvent.cs": "Recording",
            "MissionStore.cs": "Missions",
        }
        for name, destination in cases.items():
            self.assertEqual(
                archview.place(_place_evidence(name, 0), self.rules),
                (destination, "R1", ""),
            )

    def test_r2_majority_share(self):
        entry = _place_evidence("SomeFile.cs", 10, [("Logistics", 6), ("Recording", 4)])
        self.assertEqual(archview.place(entry, self.rules), ("Logistics", "R2", ""))

    def test_r2_dominance_over_the_second_module(self):
        entry = _place_evidence("SomeFile.cs", 6, [("Rewind", 4), ("Recording", 2)])
        self.assertEqual(archview.place(entry, self.rules), ("Rewind", "R2", ""))

    def test_r2_needs_the_floor(self):
        entry = _place_evidence("SomeFile.cs", 4, [("Logistics", 4)])
        self.assertEqual(archview.place(entry, self.rules), (None, "R5", "UNDECIDED"))

    def test_r3_two_owners_without_a_majority(self):
        entry = _place_evidence("SomeFile.cs", 5, [("Missions", 2), ("UI", 2)])
        self.assertEqual(
            archview.place(entry, self.rules),
            (None, "R3", "SPLIT-CANDIDATE: Missions, UI"),
        )

    def test_r3_requires_neither_owner_at_sixty_percent(self):
        # top 3 of 5 is 0.60, so R2 fires first and R3 never sees the file
        entry = _place_evidence("SomeFile.cs", 5, [("Missions", 3), ("UI", 1)])
        self.assertEqual(archview.place(entry, self.rules), ("Missions", "R2", ""))

    def test_r4_orphan(self):
        self.assertEqual(
            archview.place(_place_evidence("SomeFile.cs", 0), self.rules),
            (None, "R4", "ORPHAN"),
        )

    def test_r5_undecided(self):
        entry = _place_evidence("SomeFile.cs", 3, [("Missions", 1), ("UI", 1)])
        self.assertEqual(archview.place(entry, self.rules), (None, "R5", "UNDECIDED"))


class KnotTests(unittest.TestCase):
    def test_two_cycles_and_a_chain_largest_first(self):
        graph = {
            "AOne": ["ATwo"],
            "ATwo": ["AOne", "DOne"],
            "BOne": ["BTwo", "BThree"],
            "BTwo": ["BOne"],
            "BThree": ["BTwo"],
            "DOne": ["DTwo"],
            "DTwo": [],
        }
        modules = {
            "AOne": "ModA",
            "ATwo": "ModA",
            "BOne": "ModB",
            "BTwo": "ModB",
            "BThree": "ModB",
            "DOne": "ModD",
            "DTwo": "ModD",
        }
        result = archview.knots(graph, modules)
        self.assertEqual([knot["size"] for knot in result], [3, 2])
        self.assertEqual(result[0]["members"], ["BOne", "BThree", "BTwo"])
        self.assertEqual(result[0]["modules"], {"ModB": 3})
        self.assertEqual(result[1]["members"], ["AOne", "ATwo"])
        self.assertEqual(result[1]["modules"], {"ModA": 2})

    def test_dag_has_no_knots(self):
        graph = {"AOne": ["BTwo"], "BTwo": ["CThree"], "CThree": []}
        self.assertEqual(archview.knots(graph, {"AOne": "M", "BTwo": "M", "CThree": "M"}), [])


class KnotHubTests(unittest.TestCase):
    def test_outside_referrer_does_not_count(self):
        graph = {
            "InsideOne": ["InsideTwo"],
            "InsideTwo": ["InsideOne", "Outside"],
            "Outside": ["InsideOne"],
        }
        hubs = archview.knot_hubs(["InsideOne", "InsideTwo"], graph)
        by_name = {hub["name"]: hub for hub in hubs}
        self.assertEqual(by_name["InsideOne"]["fanIn"], 1)
        self.assertEqual(by_name["InsideTwo"]["fanIn"], 1)
        self.assertEqual(by_name["InsideTwo"]["references"], ["InsideOne"])

    def test_hubs_sorted_by_fanin_then_name(self):
        graph = {
            "HubOne": ["HubTwo"],
            "HubTwo": ["HubOne"],
            "HubThree": ["HubOne", "HubTwo"],
        }
        hubs = archview.knot_hubs(["HubThree", "HubTwo", "HubOne"], graph)
        self.assertEqual([hub["name"] for hub in hubs], ["HubOne", "HubTwo", "HubThree"])
        self.assertEqual(hubs[0]["fanIn"], 2)


class GreedySinkCutTests(unittest.TestCase):
    @staticmethod
    def _two_cycle_graph(alpha_names, beta_names):
        """Two cycles joined by alpha[0] -> beta[0] and beta[-1] -> alpha[0]."""
        graph = {}
        for index, name in enumerate(alpha_names):
            graph[name] = [alpha_names[(index + 1) % len(alpha_names)]]
        for index, name in enumerate(beta_names):
            graph[name] = [beta_names[(index + 1) % len(beta_names)]]
        graph[alpha_names[0]].append(beta_names[0])
        graph[beta_names[-1]].append(alpha_names[0])
        return graph

    def test_picks_the_sink_that_shrinks_the_component_most(self):
        # ZSink sits in the 5-cycle, ASink in the 3-cycle. Both have equal
        # in-knot fan-in, and only the size criterion can separate them: the
        # cut that leaves the 3-cycle (ZSink, alphabetically last) wins. The
        # residual 3-cycle is under 30, so the sequence must stop after it.
        graph = self._two_cycle_graph(
            ["ZSink", "QOne", "QTwo", "QThree", "QFour"], ["ASink", "POne", "PTwo"]
        )
        component = sorted(graph)
        cuts = archview.greedy_sink_cuts(component, graph)
        self.assertEqual(len(cuts), 1)
        self.assertEqual(cuts[0]["sink"], "ZSink")
        self.assertEqual(cuts[0]["sizeBefore"], 8)
        self.assertEqual(cuts[0]["sizeAfter"], 3)
        self.assertEqual(cuts[0]["droppedReferences"], ["ASink", "QOne"])

    def test_equal_result_ties_break_by_name(self):
        graph = self._two_cycle_graph(
            ["BetaSink", "XOne", "XTwo"], ["AlphaSink", "YOne", "YTwo"]
        )
        component = sorted(graph)
        cuts = archview.greedy_sink_cuts(component, graph)
        self.assertEqual(cuts[0]["sink"], "AlphaSink")
        self.assertEqual(cuts[0]["sizeAfter"], 3)

    def test_small_knot_stops_after_one_cut(self):
        graph = {"AOne": ["BTwo"], "BTwo": ["CThree"], "CThree": ["DFour"], "DFour": ["AOne"]}
        component = ["AOne", "BTwo", "CThree", "DFour"]
        cuts = archview.greedy_sink_cuts(component, graph)
        self.assertEqual(len(cuts), 1)
        self.assertLess(cuts[0]["sizeAfter"], 30)

    def test_max_steps_zero_returns_nothing(self):
        graph = {"AOne": ["BTwo"], "BTwo": ["AOne"]}
        self.assertEqual(archview.greedy_sink_cuts(["AOne", "BTwo"], graph, max_steps=0), [])


class SublevelTests(unittest.TestCase):
    def test_cutting_one_sink_turns_the_cycle_into_a_chain(self):
        graph = {"AOne": ["BTwo"], "BTwo": ["CThree"], "CThree": ["DFour"], "DFour": ["AOne"]}
        component = ["AOne", "BTwo", "CThree", "DFour"]
        cuts = archview.greedy_sink_cuts(component, graph)
        self.assertEqual(cuts[0]["sink"], "AOne")
        sublevels = archview.sublevels(component, graph, cuts)
        self.assertEqual(sublevels, {"AOne": 0, "DFour": 1, "CThree": 2, "BTwo": 3})

    def test_residual_cycle_shares_a_sublevel(self):
        graph = {
            "SinkThing": ["LoopOne"],
            "LoopOne": ["LoopTwo"],
            "LoopTwo": ["LoopOne", "LeafThing"],
            "LeafThing": ["SinkThing"],
        }
        component = sorted(graph)
        cuts = [
            {
                "step": 1,
                "sink": "SinkThing",
                "sizeBefore": 4,
                "sizeAfter": 3,
                "droppedReferences": ["LoopOne"],
            }
        ]
        sublevels = archview.sublevels(component, graph, cuts)
        self.assertEqual(sublevels["LoopOne"], sublevels["LoopTwo"])
        self.assertGreater(sublevels["LoopOne"], sublevels["LeafThing"])
        self.assertEqual(sublevels["SinkThing"], 0)

    def test_every_cut_in_the_sequence_orders_the_subgraph(self):
        graph = {
            "SinkOne": ["AOne"],
            "SinkTwo": ["BTwo"],
            "AOne": ["BTwo", "SinkTwo"],
            "BTwo": ["CThree"],
            "CThree": ["AOne", "SinkOne"],
        }
        component = sorted(graph)
        both_cuts = archview.sublevels(
            component,
            graph,
            [
                {"sink": "SinkOne", "droppedReferences": ["AOne"]},
                {"sink": "SinkTwo", "droppedReferences": ["BTwo"]},
            ],
        )
        first_cut_only = archview.sublevels(
            component, graph, [{"sink": "SinkOne", "droppedReferences": ["AOne"]}]
        )
        self.assertNotEqual(both_cuts, first_cut_only)
        self.assertEqual(both_cuts["SinkTwo"], 0)
        self.assertEqual(first_cut_only["SinkTwo"], 1)


class LadderRenderTests(unittest.TestCase):
    @staticmethod
    def _payload(text):
        match = re.search(r"const DATA = (\{.*?\});", text, re.S)
        if match is None:
            raise AssertionError("embedded DATA not found")
        return json.loads(match.group(1))

    def test_ladder_html_is_self_contained(self):
        model = {
            "modules": [{"name": "Alpha", "files": 1, "instability": 0.5, "tooling": False}],
            "types": [
                {
                    "name": "SomeThing",
                    "module": "Alpha",
                    "file": "Alpha/A.cs",
                    "kind": "class",
                    "modifiers": [],
                    "bases": [],
                    "enclosing": None,
                    "role": "service",
                    "level": 0,
                    "fanIn": 3,
                    "fanOut": 0,
                    "referencesTo": [],
                    "referencedBy": [],
                }
            ],
            "typeLevels": {"max": 0, "histogram": {0: 1}},
        }
        text = archview.render_ladder_html(model)
        self.assertNotIn("http", text)
        self.assertIn('"name":"SomeThing"', text)
        self.assertIn("role-C", text)

    def test_ladder_payload_pins_modules_maxlevel_and_knots(self):
        model = {
            "modules": [
                {"name": "Tooling", "files": 1, "instability": 0.9, "tooling": True},
                {"name": "Beta", "files": 2, "instability": 0.2, "tooling": False},
                {"name": "Alpha", "files": 3, "instability": 0.8, "tooling": False},
            ],
            "types": [
                {
                    "name": "KnotThing",
                    "module": "Alpha",
                    "file": "Alpha/A.cs",
                    "kind": "class",
                    "modifiers": [],
                    "bases": [],
                    "enclosing": None,
                    "role": "service",
                    "level": 4,
                    "knot": 1,
                    "sublevel": 0,
                    "fanIn": 2,
                    "fanOut": 1,
                    "referencesTo": ["OtherThing"],
                    "referencedBy": [],
                }
            ],
            "typeLevels": {"max": 4, "histogram": {4: 1}},
            "knots": [
                {
                    "index": 1,
                    "size": 2,
                    "members": ["KnotThing", "OtherThing"],
                    "cuts": [{"sink": "KnotThing", "droppedReferences": ["OtherThing"]}],
                }
            ],
        }
        payload = self._payload(archview.render_ladder_html(model))
        self.assertEqual([m["name"] for m in payload["modules"]], ["Alpha", "Beta"])
        self.assertEqual(payload["maxLevel"], 4)
        self.assertEqual(payload["knots"][0]["index"], 1)
        self.assertEqual(payload["knots"][0]["size"], 2)
        self.assertEqual(payload["knots"][0]["sinks"], {"KnotThing": ["OtherThing"]})
        self.assertEqual(payload["types"][0]["knot"], 1)
        self.assertEqual(payload["types"][0]["sublevel"], 0)


class ExploreRenderTests(unittest.TestCase):
    def test_explore_html_is_self_contained_and_substituted(self):
        model = {
            "modules": [
                {
                    "name": "Alpha",
                    "files": 1,
                    "fanIn": 0,
                    "fanOut": 0,
                    "instability": 0.5,
                    "tooling": False,
                }
            ],
            "edges": [],
        }
        text = archview.render_explore_html(model, 8)
        self.assertNotIn("<script src", text)
        self.assertNotIn("<link", text)
        self.assertNotIn("@@", text)
        self.assertIn('"name":"Alpha"', text)


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


    def test_parse_edge_spec_rejects_extra_arrows(self):
        with self.assertRaises(ValueError):
            archview.parse_edge_spec("A -> B -> C")

    def test_parse_edge_spec_rejects_empty_side(self):
        with self.assertRaises(ValueError):
            archview.parse_edge_spec("A -> ")


class InterpolatedStringTests(unittest.TestCase):
    def test_nested_string_literal_in_hole_does_not_leak(self):
        source = 'var s = $"{(flag ? "Scenario" : lifecycle)}: failed";'
        stripped = archview.strip_comments_and_strings(source)
        self.assertNotIn("Scenario", stripped)
        self.assertEqual(
            archview.references(stripped, {"Scenario": "InGameTests"}, "Controllers"),
            [],
        )

    def test_hole_code_is_kept_as_a_reference(self):
        source = 'var s = $"value {MissionStore.Instance} done"; var after = 1;'
        stripped = archview.strip_comments_and_strings(source)
        self.assertIn("MissionStore", stripped)
        self.assertIn("after", stripped)
        self.assertEqual(
            archview.references(stripped, {"MissionStore": "Missions"}, "Core"),
            [("MissionStore", "Missions")],
        )

    def test_verbatim_interpolated_string(self):
        source = 'var s = $@"a ""quoted"" {MissionStore.Instance} b"; var after = 1;'
        stripped = archview.strip_comments_and_strings(source)
        self.assertIn("after", stripped)
        self.assertIn("MissionStore", stripped)
        self.assertNotIn("quoted", stripped)

    def test_raw_string_with_trailing_quote_does_not_swallow_code(self):
        source = 'var s = """text""""; var after = MissionStore.Instance;'
        stripped = archview.strip_comments_and_strings(source)
        self.assertIn("after", stripped)
        self.assertIn("MissionStore", stripped)

    def test_escaped_quote_in_regular_string(self):
        source = 'var s = "a\\"MissionStore b"; var after = 1;'
        stripped = archview.strip_comments_and_strings(source)
        self.assertNotIn("MissionStore", stripped)
        self.assertIn("after", stripped)


class WalkerEdgeCaseTests(unittest.TestCase):
    def test_nested_bin_directory_is_skipped(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            (root / "Foo" / "bin").mkdir(parents=True)
            (root / "Foo" / "Kept.cs").write_text("class Kept {}", encoding="utf-8")
            (root / "Foo" / "bin" / "Skipped.cs").write_text("class Skipped {}", encoding="utf-8")
            found = [path.as_posix() for path in archview.iter_source_files(root)]
        self.assertEqual(found, ["Foo/Kept.cs"])


class ModelWiringTests(unittest.TestCase):
    @staticmethod
    def _write(root, rel, text):
        path = root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def test_cyclic_flag_marks_mutual_references(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            self._write(root, "One/A.cs", "namespace N { class AlphaThing { BetaThing b; } }")
            self._write(
                root,
                "Two/B.cs",
                "namespace N { class BetaThing { AlphaThing a; GammaThing g; } }",
            )
            self._write(root, "Three/C.cs", "namespace N { class GammaThing { } }")
            rules = [
                {"name": "One", "folder": "One"},
                {"name": "Two", "folder": "Two"},
                {"name": "Three", "folder": "Three"},
            ]
            model = archview.build_model(root, rules, set())
        edges = {(e["from"], e["to"]): e for e in model["edges"]}
        self.assertTrue(edges[("One", "Two")]["cyclic"])
        self.assertTrue(edges[("Two", "One")]["cyclic"])
        self.assertFalse(edges[("Two", "Three")]["cyclic"])
        # One: out 1, in 1 -> I=0.5. Two: out 2, in 1 -> I=0.67. Three: sink -> I=0.
        # The stable module One depending on the less stable Two is the upward edge.
        self.assertTrue(edges[("One", "Two")]["upward"])
        self.assertFalse(edges[("Two", "One")]["upward"])
        self.assertFalse(edges[("Two", "Three")]["upward"])

    def test_type_declared_in_two_modules_is_excluded(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            self._write(root, "One/A.cs", "class Entry {} class AlphaOnly {}")
            self._write(root, "Two/B.cs", "class Entry {} class BetaOnly {}")
            self._write(root, "Three/C.cs", "class Uses { Entry e; AlphaOnly a; }")
            rules = [
                {"name": "One", "folder": "One"},
                {"name": "Two", "folder": "Two"},
                {"name": "Three", "folder": "Three"},
            ]
            model = archview.build_model(root, rules, set())
        edges = {(e["from"], e["to"]): e["types"] for e in model["edges"]}
        self.assertEqual(model["ambiguousTypes"], ["Entry"])
        self.assertEqual(edges, {("Three", "One"): ["AlphaOnly"]})

    def test_is_upward(self):
        self.assertTrue(archview.is_upward(0.2, 0.8))
        self.assertFalse(archview.is_upward(0.8, 0.2))
        self.assertFalse(archview.is_upward(0.5, 0.5))

    def test_unclassified_files_are_recorded(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            self._write(root, "Mystery/X.cs", "class MysteryThing {}")
            model = archview.build_model(root, [{"name": "Core", "prefix": ".*"}], set())
        self.assertEqual(model["unclassified"], ["Mystery/X.cs"])
        self.assertEqual([m["name"] for m in model["modules"]], [])

    def test_tooling_types_are_not_in_the_graph(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            self._write(root, "Prod/A.cs", "class UsesThing { ToolThing t; }")
            self._write(root, "Tool/T.cs", "class ToolThing { }")
            rules = [{"name": "Prod", "folder": "Prod"}, {"name": "Tool", "folder": "Tool"}]
            model = archview.build_model(root, rules, {"Tool"})
        types = {t["name"]: t for t in model["types"]}
        self.assertEqual(sorted(types), ["UsesThing"])
        self.assertEqual(types["UsesThing"]["referencesTo"], [])
        self.assertEqual(types["UsesThing"]["level"], 0)

    def test_partial_declarations_merge_bases_for_the_role(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            self._write(root, "One/A.cs", "public partial class BigThing { }")
            self._write(root, "One/B.cs", "public partial class BigThing : MonoBehaviour { }")
            model = archview.build_model(root, [{"name": "One", "folder": "One"}], set())
        types = {t["name"]: t for t in model["types"]}
        self.assertEqual(types["BigThing"]["role"], "entry")
        self.assertEqual(types["BigThing"]["bases"], ["MonoBehaviour"])

    def test_types_carry_knot_and_sublevel(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            self._write(root, "One/A.cs", "class AlphaThing { BetaThing b; }")
            self._write(root, "One/B.cs", "class BetaThing { AlphaThing a; }")
            self._write(root, "One/C.cs", "class GammaThing { AlphaThing a; }")
            model = archview.build_model(root, [{"name": "One", "folder": "One"}], set())
        types = {t["name"]: t for t in model["types"]}
        self.assertEqual(types["AlphaThing"]["knot"], 1)
        self.assertEqual(types["BetaThing"]["knot"], 1)
        self.assertIsNotNone(types["AlphaThing"]["sublevel"])
        self.assertIsNone(types["GammaThing"]["knot"])
        self.assertIsNone(types["GammaThing"]["sublevel"])
        self.assertEqual(len(model["knots"]), 1)
        self.assertEqual(model["knots"][0]["size"], 2)

    def test_repeated_mentions_in_one_file_count_once(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            self._write(root, "One/A.cs", "class AlphaThing { BetaThing b; BetaThing c; }")
            self._write(root, "Two/B.cs", "class BetaThing { }")
            rules = [{"name": "One", "folder": "One"}, {"name": "Two", "folder": "Two"}]
            model = archview.build_model(root, rules, set())
        types = {t["name"]: t for t in model["types"]}
        self.assertEqual(types["BetaThing"]["fanIn"], 1)


def _small_model():
    return {
        "modules": [
            {"name": "A", "files": 1, "fanIn": 1, "fanOut": 1, "instability": 0.5, "tooling": False},
            {"name": "B", "files": 1, "fanIn": 1, "fanOut": 1, "instability": 0.5, "tooling": False},
        ],
        "edges": [
            {
                "from": "A",
                "to": "B",
                "weight": 3,
                "cyclic": False,
                "upward": False,
                "files": ["A/FileA.cs"],
                "types": ["TypeB"],
            },
            {
                "from": "B",
                "to": "A",
                "weight": 2,
                "cyclic": False,
                "upward": True,
                "files": ["B/FileB.cs"],
                "types": ["TypeA"],
            },
        ],
        "unclassified": [],
    }


class CheckerOutputTests(unittest.TestCase):
    @staticmethod
    def _last_line(text):
        return [line for line in text.splitlines() if line.strip()][-1]

    def test_violation_reported_with_files_and_marker_last(self):
        captured = io.StringIO()
        with contextlib.redirect_stdout(captured):
            archview.run_check(_small_model(), ["A -> B"], [])
        text = captured.getvalue()
        self.assertIn("VIOLATION A -> B: weight=3", text)
        self.assertIn("referenced types: TypeB", text)
        self.assertIn("A/FileA.cs", text)
        self.assertNotIn("not in [allowed]", text)
        self.assertEqual(self._last_line(text), "ARCH-CHECK report-only")

    def test_allowlist_section_only_when_nonempty(self):
        captured = io.StringIO()
        with contextlib.redirect_stdout(captured):
            archview.run_check(_small_model(), [], ["A -> B"])
        text = captured.getvalue()
        self.assertIn("not in [allowed]", text)
        self.assertIn("B -> A: weight=2", text)
        self.assertNotIn("A -> B: weight=3", text)

    def test_bad_policy_specs_are_warned_not_raised(self):
        captured = io.StringIO()
        with contextlib.redirect_stdout(captured):
            archview.run_check(_small_model(), ["NoArrow"], ["Also Bad"])
        text = captured.getvalue()
        self.assertIn("WARN bad [forbidden] edge spec", text)
        self.assertIn("WARN bad [allowed] edge spec", text)
        self.assertEqual(self._last_line(text), "ARCH-CHECK report-only")

    def test_main_check_exits_zero_on_bad_edge_spec(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            (root / "src").mkdir()
            (root / "src" / "Thing.cs").write_text("class Thing {}", encoding="utf-8")
            modules = root / "modules.toml"
            modules.write_text(
                '[[module]]\nname = "Core"\nprefix = ".*"\n\n'
                '[forbidden]\nedges = ["NoArrowHere"]\n',
                encoding="utf-8",
            )
            captured = io.StringIO()
            with contextlib.redirect_stdout(captured), contextlib.redirect_stderr(io.StringIO()):
                rc = archview.main(
                    [
                        "--source",
                        str(root / "src"),
                        "--out",
                        str(root / "out"),
                        "--modules",
                        str(modules),
                        "--check",
                    ]
                )
        self.assertEqual(rc, 0)
        self.assertEqual(self._last_line(captured.getvalue()), "ARCH-CHECK report-only")

    def test_main_check_exits_zero_on_missing_modules_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            captured = io.StringIO()
            with contextlib.redirect_stdout(captured), contextlib.redirect_stderr(io.StringIO()):
                rc = archview.main(
                    [
                        "--modules",
                        str(pathlib.Path(tmp) / "missing.toml"),
                        "--out",
                        str(pathlib.Path(tmp) / "out"),
                        "--check",
                    ]
                )
        self.assertEqual(rc, 0)
        self.assertEqual(self._last_line(captured.getvalue()), "ARCH-CHECK report-only")

    def test_main_warns_about_unclassified_files(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            (root / "src" / "Mystery").mkdir(parents=True)
            (root / "src" / "Mystery" / "X.cs").write_text("class X {}", encoding="utf-8")
            modules = root / "modules.toml"
            modules.write_text('[[module]]\nname = "Core"\nprefix = ".*"\n', encoding="utf-8")
            captured = io.StringIO()
            with contextlib.redirect_stdout(captured), contextlib.redirect_stderr(io.StringIO()):
                rc = archview.main(
                    [
                        "--source",
                        str(root / "src"),
                        "--out",
                        str(root / "out"),
                        "--modules",
                        str(modules),
                    ]
                )
        self.assertEqual(rc, 0)
        self.assertIn("WARN unclassified file: Mystery/X.cs", captured.getvalue())

    def test_type_sections_print_before_forbidden(self):
        def typed(name, role, level, fan_in):
            return {
                "name": name,
                "module": "Alpha",
                "file": "Alpha/A.cs",
                "kind": "class",
                "modifiers": [],
                "bases": [],
                "enclosing": None,
                "role": role,
                "level": level,
                "fanIn": fan_in,
                "fanOut": 0,
                "referencesTo": [],
                "referencedBy": [],
            }

        model = {
            "modules": [
                {"name": "Alpha", "files": 1, "fanIn": 0, "fanOut": 0, "instability": 0.0, "tooling": False}
            ],
            "edges": [],
            "unclassified": [],
            "types": [typed("HubThing", "static", 2, 7), typed("LeafThing", "data", 0, 1)],
            "typeLevels": {"max": 2, "histogram": {0: 1, 2: 1}},
        }
        captured = io.StringIO()
        with contextlib.redirect_stdout(captured):
            archview.run_check(model, [], [])
        text = captured.getvalue()
        self.assertIn("HUB TYPES", text)
        self.assertIn("HubThing", text)
        self.assertIn("TYPE ROLES", text)
        self.assertIn("LEVEL PROFILE", text)
        self.assertIn("Overall max level: 2", text)
        self.assertLess(text.index("HUB TYPES"), text.index("Forbidden edges"))
        self.assertEqual(self._last_line(text), "ARCH-CHECK report-only")

    def test_knot_section_reports_sizes_hubs_and_cuts(self):
        def typed(name, refs):
            return {
                "name": name,
                "module": "Alpha",
                "file": "Alpha/A.cs",
                "kind": "class",
                "modifiers": [],
                "bases": [],
                "enclosing": None,
                "role": "service",
                "level": 1,
                "knot": 1,
                "sublevel": 0,
                "fanIn": 1,
                "fanOut": 1,
                "referencesTo": refs,
                "referencedBy": [],
            }

        model = {
            "modules": [
                {"name": "Alpha", "files": 1, "fanIn": 0, "fanOut": 0, "instability": 0.0, "tooling": False}
            ],
            "edges": [],
            "unclassified": [],
            "types": [typed("AlphaThing", ["BetaThing"]), typed("BetaThing", ["AlphaThing"])],
            "typeLevels": {"max": 1, "histogram": {1: 2}},
            "knots": [
                {
                    "index": 1,
                    "members": ["AlphaThing", "BetaThing"],
                    "size": 2,
                    "modules": {"Alpha": 2},
                    "cuts": [
                        {
                            "step": 1,
                            "sink": "AlphaThing",
                            "sizeBefore": 2,
                            "sizeAfter": 1,
                            "droppedReferences": ["BetaThing"],
                        }
                    ],
                }
            ],
        }
        captured = io.StringIO()
        with contextlib.redirect_stdout(captured):
            archview.run_check(model, [], [])
        text = captured.getvalue()
        self.assertIn("KNOTS", text)
        self.assertIn("size=2", text)
        self.assertIn("Alpha=2", text)
        self.assertIn("in-knot fan-in", text)
        self.assertIn("2 -> cut AlphaThing -> 1 (drops: BetaThing)", text)
        self.assertLess(text.index("KNOTS"), text.index("Forbidden edges"))
        self.assertEqual(self._last_line(text), "ARCH-CHECK report-only")


@unittest.skipUnless(REAL_SOURCE.is_dir(), "Source/Parsek is not present")
class RealTreeSmokeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        rules, tooling, _forbidden, _allowed = archview.load_rules(archview.DEFAULT_MODULES)
        cls.model = archview.build_model(REAL_SOURCE, rules, tooling)
        cls.by_name = {module["name"]: module for module in cls.model["modules"]}
        cls.types = {entry["name"]: entry for entry in cls.model["types"]}

    def test_logio_is_a_pure_sink(self):
        self.assertIn("LogIO", self.by_name)
        self.assertLess(self.by_name["LogIO"]["instability"], 0.1)

    def test_logistics_reads_missions(self):
        edges = {(edge["from"], edge["to"]) for edge in self.model["edges"]}
        self.assertIn(("Logistics", "Missions"), edges)

    def test_every_source_file_is_classified(self):
        self.assertEqual(self.model["unclassified"], [])

    def test_parseklog_has_the_highest_fanin(self):
        top = max(self.model["types"], key=lambda entry: entry["fanIn"])
        self.assertEqual(top["name"], "ParsekLog")

    def test_recording_fanin_is_above_one_hundred(self):
        self.assertGreater(self.types["Recording"]["fanIn"], 100)

    def test_interface_count_is_between_twenty_and_forty(self):
        count = sum(1 for entry in self.model["types"] if entry["role"] == "interface")
        self.assertGreaterEqual(count, 20)
        self.assertLessEqual(count, 40)

    def test_every_type_has_a_level_and_max_is_at_least_four(self):
        self.assertTrue(all(isinstance(entry["level"], int) for entry in self.model["types"]))
        self.assertGreaterEqual(self.model["typeLevels"]["max"], 4)

    def test_largest_knot_is_large_and_spans_modules(self):
        largest = self.model["knots"][0]
        self.assertGreater(largest["size"], 300)
        self.assertGreater(len(largest["modules"]), 10)

    def test_knot_sizes_match_the_documented_tree(self):
        self.assertEqual([knot["size"] for knot in self.model["knots"]], [392, 4, 2])

    def test_phase_two_catch_all_count(self):
        # Phase 2's revised placement policy (R1 name families first, then
        # externalRefs >= 5 AND (share >= 0.5 OR top >= 2 * second) on the
        # rebuilt evidence) moves 48 files; 75 stay in the catch-all as
        # SPLIT-CANDIDATE, ORPHAN or UNDECIDED and are listed with evidence in
        # docs/dev/arch/core-placement.md.
        self.assertEqual(self.by_name["Core"]["files"], 75)

    def test_phase_two_placements(self):
        modules = self.model["fileModules"]
        self.assertEqual(modules["RouteProofMetadata.cs"], "Logistics")
        self.assertEqual(modules["GuiTreeFunnels.cs"], "GuiTree")
        self.assertEqual(modules["GameStateEvent.cs"], "GameActions")
        self.assertEqual(modules["ResourceManifest.cs"], "Logistics")
        self.assertEqual(modules["RecoveryPayoutContext.cs"], "GameActions")

    def test_first_cut_matches_the_documented_tree(self):
        first = self.model["knots"][0]["cuts"][0]
        self.assertEqual(first["sink"], "ParsekLog")
        self.assertEqual(first["sizeBefore"], 392)
        self.assertEqual(first["sizeAfter"], 335)
        self.assertEqual(first["droppedReferences"], ["ParsekSettings", "RecorderStateSnapshot"])

    def test_cut_sequence_halves_the_largest_knot(self):
        largest = self.model["knots"][0]
        self.assertLess(largest["cuts"][-1]["sizeAfter"], largest["size"] / 2)

    def test_largest_knot_has_many_sublevels(self):
        sublevels = {entry["sublevel"] for entry in self.model["types"] if entry["knot"] == 1}
        self.assertGreater(len(sublevels), 5)

    def test_types_outside_a_knot_have_no_sublevel(self):
        outside = [entry for entry in self.model["types"] if entry["knot"] is None]
        self.assertTrue(outside)
        self.assertTrue(all(entry["sublevel"] is None for entry in outside))
        inside = [entry for entry in self.model["types"] if entry["knot"] is not None]
        self.assertTrue(all(entry["knot"] >= 1 for entry in inside))
        self.assertTrue(all(entry["sublevel"] is not None for entry in inside))

    def test_no_record_declarations_in_the_tree(self):
        # The literal `\brecord\b` grep would also match a local variable named
        # `record` (KspStatePatcher.cs has one), so this pins the declaration
        # shape instead: the record keyword, an optional class/struct, then a
        # type name.
        pattern = re.compile(r"\brecord\s+(?:class\s+|struct\s+)?[A-Z]")
        for rel in archview.iter_source_files(REAL_SOURCE):
            text = (REAL_SOURCE / rel).read_text(encoding="utf-8-sig", errors="replace")
            stripped = archview.strip_comments_and_strings(text)
            self.assertIsNone(pattern.search(stripped), "record declaration in %s" % rel.as_posix())


if __name__ == "__main__":
    unittest.main()
