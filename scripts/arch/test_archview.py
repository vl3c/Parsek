"""Unit tests for scripts/arch/archview.py.

Run from the repo root:

    python -m unittest scripts/arch/test_archview.py

Stdlib only. The smoke tests at the end scan the real Source/Parsek tree and
the worktree's git history, so the extractor, the atlas and the history
metrics are exercised end to end. The history tests skip when the checkout
has no git history (a tarball or shallow clone).
"""

import contextlib
import datetime
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
            "declaredTypeCounts": {
                "AlphaFile.cs": 1,
                "LocalFile.cs": 2,
                "EmptyFile.cs": 0,
                "BetaFile.cs": 2,
                "GammaFile.cs": 1,
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
        self.assertEqual(local["types"], 2)
        self.assertEqual(local["externalRefs"], 0)
        self.assertEqual(local["byModule"], [])

    def test_file_with_no_external_references_reports_zero(self):
        evidence = {entry["file"]: entry for entry in archview.placement_evidence(self._model())}
        empty = evidence["EmptyFile.cs"]
        self.assertEqual(empty["types"], 0)
        self.assertEqual(empty["externalRefs"], 0)
        self.assertEqual(empty["share"], 0.0)
        self.assertEqual(empty["byModule"], [])

    def test_hub_is_the_highest_fanin_among_the_file_types(self):
        model = {
            "catchAll": "CatchAll",
            "fileModules": {"MultiFile.cs": "CatchAll"},
            "declaredTypeCounts": {"MultiFile.cs": 2},
            "types": [
                {
                    "name": "FirstThing",
                    "file": "MultiFile.cs",
                    "module": "CatchAll",
                    "knot": None,
                    "fanIn": 2,
                    "referencedBy": [],
                },
                {
                    "name": "SecondThing",
                    "file": "MultiFile.cs",
                    "module": "CatchAll",
                    "knot": None,
                    "fanIn": 9,
                    "referencedBy": [],
                },
            ],
        }
        evidence = archview.placement_evidence(model)
        self.assertEqual(evidence[0]["hub"], "SecondThing")
        self.assertEqual(evidence[0]["hubFanIn"], 9)
        self.assertEqual(evidence[0]["types"], 2)


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

    def test_r2_majority_share_at_the_boundary(self):
        # exactly half of ten: the share clause fires, the dominance clause
        # (5 >= 2 * 5) does not, so this pins minShare == 0.5 and >=
        entry = _place_evidence("SomeFile.cs", 10, [("Logistics", 5), ("Recording", 5)])
        self.assertEqual(archview.place(entry, self.rules), ("Logistics", "R2", ""))

    def test_r2_dominance_without_a_majority(self):
        # 0.40 share is below the floor, but 2 >= 2 * 1 carries the rule
        entry = _place_evidence("SomeFile.cs", 5, [("Rewind", 2), ("Recording", 1)])
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

    def test_r2_wins_over_r3_at_the_share_boundary(self):
        # 5 of 10 is 0.50: R2 fires; R3 must not be checked first (it would
        # also match: pair 0.8 with the top below 0.6)
        entry = _place_evidence("SomeFile.cs", 10, [("Missions", 5), ("UI", 3)])
        self.assertEqual(archview.place(entry, self.rules), ("Missions", "R2", ""))

    def test_r4_orphan_needs_zero_references(self):
        self.assertEqual(
            archview.place(_place_evidence("SomeFile.cs", 1, [("Missions", 1)]), self.rules),
            (None, "R5", "UNDECIDED"),
        )
        self.assertEqual(
            archview.place(_place_evidence("SomeFile.cs", 0), self.rules),
            (None, "R4", "ORPHAN"),
        )

    def test_r5_undecided(self):
        entry = _place_evidence("SomeFile.cs", 3, [("Missions", 1), ("UI", 1)])
        self.assertEqual(archview.place(entry, self.rules), (None, "R5", "UNDECIDED"))


class PlacementReportTests(unittest.TestCase):
    def test_main_place_writes_the_report_and_prints_evidence(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            src = root / "src"
            src.mkdir()
            (src / "GuiTreeFunnels.cs").write_text("class GuiFunnelThing { }", encoding="utf-8")
            (src / "OrphanFile.cs").write_text("class OrphanThing { }", encoding="utf-8")
            (src / "SoloFile.cs").write_text("class SoloThing { }", encoding="utf-8")
            (src / "OtherFile.cs").write_text("class OtherThing { SoloThing s; }", encoding="utf-8")
            modules = root / "modules.toml"
            modules.write_text(
                '[[module]]\nname = "GuiTree"\nprefix = "^GuiTree"\nplacement = "R1"\n\n'
                '[[module]]\nname = "Other"\nprefix = "^Other"\n\n'
                '[[module]]\nname = "Cell"\nprefix = "^SoloFile[.]cs$"\n',
                encoding="utf-8",
            )
            captured = io.StringIO()
            with contextlib.redirect_stdout(captured), contextlib.redirect_stderr(io.StringIO()):
                rc_a = archview.main(
                    [
                        "--source",
                        str(src),
                        "--modules",
                        str(modules),
                        "--out",
                        str(root / "out-a"),
                        "--place",
                    ]
                )
                rc_b = archview.main(
                    [
                        "--source",
                        str(src),
                        "--modules",
                        str(modules),
                        "--out",
                        str(root / "out-b"),
                        "--place",
                    ]
                )
            self.assertEqual((rc_a, rc_b), (0, 0))
            report = (root / "out-a" / "core-placement.md").read_text(encoding="utf-8")
            self.assertEqual(report, (root / "out-b" / "core-placement.md").read_text(encoding="utf-8"))
            atlas = (root / "out-a" / "atlas.html").read_text(encoding="utf-8")
            history = json.loads((root / "out-a" / "history.json").read_text(encoding="utf-8"))
        self.assertIn("Parsek Atlas", atlas)
        self.assertEqual(
            sorted(history),
            [
                "commits",
                "filePairs",
                "files",
                "hotspots",
                "largestSweep",
                "modulePairs",
                "modules",
                "since",
                "skippedSweeps",
            ],
        )
        self.assertIn("PLACEMENT EVIDENCE", captured.getvalue())
        self.assertIn("| GuiTreeFunnels.cs | 1 | 0 | 0 | 0.00 | - | - | R1 | GuiTree |", report)
        self.assertIn("## ORPHAN", report)
        self.assertIn("- OrphanFile.cs", report)
        self.assertIn("## UNDECIDED", report)
        self.assertIn("- SoloFile.cs", report)
        # The before-state needs the historical catch-all rebuild: the live last
        # rule is an explicit one-file list, so without it the before count is 1.
        self.assertIn("Catch-all (Cell): 3 files before, 1 files after.", report)
        self.assertIn(
            "Placement rules whose destination does not match the current map: none.", report
        )


class BandForTests(unittest.TestCase):
    BANDS = {
        "entry": {"min": 0.70, "label": "Entry"},
        "feature": {"min": 0.25, "label": "Feature"},
        "model": {"min": 0.18, "label": "Model"},
        "floor": {"min": 0.00, "label": "Floor"},
    }

    def test_boundaries(self):
        self.assertEqual(archview.band_for(0.70, self.BANDS), ("entry", "Entry"))
        self.assertEqual(archview.band_for(0.699, self.BANDS), ("feature", "Feature"))
        self.assertEqual(archview.band_for(0.25, self.BANDS), ("feature", "Feature"))
        self.assertEqual(archview.band_for(0.249, self.BANDS), ("model", "Model"))
        self.assertEqual(archview.band_for(0.18, self.BANDS), ("model", "Model"))
        self.assertEqual(archview.band_for(0.0, self.BANDS), ("floor", "Floor"))

    def test_real_bands_reproduce_the_page_grouping(self):
        prose = archview.load_prose(archview.DEFAULT_ATLAS)
        bands = prose["bands"]
        self.assertEqual(archview.band_for(0.7216, bands)[0], "entry")
        self.assertEqual(archview.band_for(0.6612, bands)[0], "feature")
        self.assertEqual(archview.band_for(0.1925, bands)[0], "model")
        self.assertEqual(archview.band_for(0.0137, bands)[0], "floor")


class ProseTests(unittest.TestCase):
    @staticmethod
    def _model(module_names=("Alpha",), type_names=()):
        return {
            "modules": [
                {
                    "name": name,
                    "files": 1,
                    "fanIn": 0,
                    "fanOut": 0,
                    "instability": 0.5,
                    "tooling": False,
                }
                for name in module_names
            ],
            "edges": [],
            "types": [
                {
                    "name": name,
                    "module": "Alpha",
                    "file": "Alpha/f.cs",
                    "role": "service",
                    "level": 0,
                    "knot": None,
                    "sublevel": None,
                    "fanIn": 100 - index,
                    "fanOut": 0,
                    "referencesTo": [],
                    "referencedBy": [],
                }
                for index, name in enumerate(type_names)
            ],
        }

    def test_load_prose_reads_a_toml_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = pathlib.Path(tmp) / "prose.toml"
            path.write_text('[page]\nlede = "Hello."\n', encoding="utf-8")
            prose = archview.load_prose(path)
        self.assertEqual(prose["page"]["lede"], "Hello.")

    def test_missing_summaries(self):
        findings = archview.prose_findings(self._model(), {})
        self.assertEqual(findings["missingSummaries"], ["Alpha"])

    def test_missing_glossary_type(self):
        prose = {"glossary": {"GhostThing": {"meaning": "x"}}}
        findings = archview.prose_findings(self._model(), prose)
        self.assertEqual(findings["missingGlossary"], ["GhostThing"])

    def test_stale_glossary_outside_the_top_eighteen(self):
        names = ["Type%02d" % index for index in range(1, 20)]
        prose = {"glossary": {"Type19": {"meaning": "x"}}}
        findings = archview.prose_findings(self._model(type_names=names), prose)
        self.assertEqual(findings["staleGlossary"], ["Type19"])

    def test_stale_readings(self):
        prose = {
            "upward_readings": [
                {"edge": "Gone -> Type", "reading": "x"},
                {"edge": "NoArrow", "reading": "x"},
                {"edge": 5, "reading": "x"},
            ]
        }
        findings = archview.prose_findings(self._model(), prose)
        self.assertEqual(findings["staleReadings"], ["Gone -> Type", "NoArrow", "5"])

    def test_missing_reading_order_types(self):
        prose = {"reading_order": [{"title": "t", "types": ["NopeThing"], "text": "x"}]}
        findings = archview.prose_findings(self._model(), prose)
        self.assertEqual(findings["missingReadingOrder"], ["NopeThing"])


def _atlas_model():
    return {
        "modules": [
            {"name": "Alpha", "files": 1, "fanIn": 0, "fanOut": 6, "instability": 0.75,
             "tooling": False},
            {"name": "Beta", "files": 2, "fanIn": 4, "fanOut": 1, "instability": 0.2,
             "tooling": False},
            {"name": "Tool", "files": 1, "fanIn": 0, "fanOut": 0, "instability": 0.0,
             "tooling": True},
        ],
        "edges": [
            {"from": "Beta", "to": "Alpha", "weight": 4, "upward": True,
             "types": ["AlphaThing", "BetaThing"], "files": [], "cyclic": False},
        ],
        "types": [
            {"name": "AlphaThing", "module": "Alpha", "file": "Alpha/A.cs", "role": "service",
             "level": 1, "knot": None, "sublevel": None, "fanIn": 5, "fanOut": 0,
             "referencesTo": [], "referencedBy": []},
            {"name": "BetaThing", "module": "Beta", "file": "Beta/B.cs", "role": "data",
             "level": 0, "knot": None, "sublevel": None, "fanIn": 2, "fanOut": 0,
             "referencesTo": [], "referencedBy": []},
            {"name": "GammaThing", "module": "Alpha", "file": "Alpha/A.cs", "role": "data",
             "level": 0, "knot": None, "sublevel": None, "fanIn": 1, "fanOut": 0,
             "referencesTo": [], "referencedBy": []},
        ],
        "typeLevels": {"max": 1, "histogram": {0: 2, 1: 1}},
        "knots": [
            {
                "index": 1,
                "size": 2,
                "members": ["AlphaThing", "BetaThing"],
                "modules": {"Alpha": 1, "Beta": 1},
                "cuts": [
                    {"step": 1, "sink": "AlphaThing", "sizeBefore": 2, "sizeAfter": 1,
                     "droppedReferences": ["BetaThing"]}
                ],
            }
        ],
    }


def _atlas_prose():
    return {
        "page": {
            "lede": "Lede text.",
            "approximation_note": "Foot text.",
            "history_reading": "Co-change sentence.",
        },
        "bands": {
            "entry": {"min": 0.70, "label": "Entry"},
            "floor": {"min": 0.00, "label": "Floor"},
        },
        "modules": {
            "Alpha": {"summary": "Alpha summary."},
            "Beta": {"summary": "Beta summary."},
        },
        "glossary": {"AlphaThing": {"meaning": "Alpha meaning."}},
        "upward_readings": [
            {"edge": "Beta -> Alpha", "reading": "Reading text."},
            {"edge": "Gone -> Type", "reading": "Vanished reading."},
        ],
        "reading_order": [{"title": "Step one.", "types": ["AlphaThing"], "text": "Do it."}],
    }


class AtlasRenderTests(unittest.TestCase):
    def test_markers_replaced_and_regions_rendered(self):
        text = archview.render_atlas_html(
            _atlas_model(), _atlas_prose(), None, ["Beta -> Alpha"]
        )
        self.assertNotIn("@@", text)
        self.assertIn("modules.svg is missing", text)
        self.assertIn("Alpha summary.", text)
        self.assertIn("Beta summary.", text)
        self.assertIn("Alpha meaning.", text)
        self.assertIn("(no meaning yet)", text)
        self.assertIn("Reading text.", text)
        self.assertNotIn("Vanished reading.", text)
        self.assertIn(
            '<div class="n">4</div><div class="l">C&#35; files in one assembly</div>', text
        )
        self.assertIn(
            '<div class="n">3</div><div class="l">production types across 2 modules</div>', text
        )
        self.assertIn('<div class="n">2</div><div class="l">of those types depend on nothing', text)
        self.assertIn(
            '<div class="n knot">2</div><div class="l">types locked in one dependency cycle</div>',
            text,
        )
        self.assertIn("2 types, 67 percent", text)
        self.assertIn("Step one.", text)
        self.assertIn('<strong>Step one.</strong> <span class="id">AlphaThing</span>. Do it.', text)

    def test_reverse_reading_is_rendered(self):
        prose = _atlas_prose()
        prose["upward_readings"] = [{"edge": "Alpha -> Beta", "reading": "Reverse reading."}]
        text = archview.render_atlas_html(_atlas_model(), prose, None, ["Beta -> Alpha"])
        self.assertIn("Reverse reading.", text)

    def test_atlas_history_section(self):
        history = {
            "since": "2025-03-01",
            "commits": 10,
            "skippedSweeps": 0,
            "largestSweep": 0,
            "hotspots": [
                {"name": "AlphaThing", "module": "Alpha", "file": "Alpha/A.cs",
                 "fileCommits": 4, "fanIn": 5, "hotspot": 20}
            ],
            "modulePairs": [
                {"a": "Alpha", "b": "Beta", "both": 2, "either": 5, "jaccard": 0.4}
            ],
            "filePairs": [
                {"a": "Alpha/A.cs", "b": "Beta/B.cs", "moduleA": "Alpha",
                 "moduleB": "Beta", "count": 6}
            ],
            "modules": [],
        }
        text = archview.render_atlas_html(_atlas_model(), _atlas_prose(), None, [], history)
        self.assertIn("What changes together", text)
        self.assertIn("Co-change sentence.", text)
        self.assertIn("Alpha &harr; Beta", text)
        self.assertIn("Alpha/A.cs", text)
        self.assertIn("AlphaThing", text)

    def test_atlas_history_notice_when_empty(self):
        text = archview.render_atlas_html(_atlas_model(), _atlas_prose(), None, [], None)
        self.assertIn("No co-change history in the window.", text)

    def test_atlas_history_truncates_to_ten_rows(self):
        history = {
            "since": "2025-03-01",
            "commits": 3,
            "skippedSweeps": 0,
            "largestSweep": 0,
            "hotspots": [
                {"name": "Hot%02d" % index, "module": "Alpha", "file": "Alpha/A.cs",
                 "fileCommits": 1, "fanIn": 1, "hotspot": 1}
                for index in range(12)
            ],
            "modulePairs": [
                {"a": "Alpha", "b": "Mod%02d" % index, "both": 1, "either": 2, "jaccard": 0.5}
                for index in range(12)
            ],
            "filePairs": [
                {"a": "Alpha/F%02d.cs" % index, "b": "Beta/B.cs", "moduleA": "Alpha",
                 "moduleB": "Beta", "count": 9}
                for index in range(12)
            ],
            "modules": [],
        }
        text = archview.render_atlas_html(_atlas_model(), _atlas_prose(), None, [], history)
        self.assertIn("Hot09", text)
        self.assertNotIn("Hot10", text)
        self.assertIn("Mod09", text)
        self.assertNotIn("Mod10", text)
        self.assertIn("Alpha/F09.cs", text)
        self.assertNotIn("Alpha/F10.cs", text)

    def test_eyebrow_format_and_fallback(self):
        self.assertRegex(
            archview._atlas_eyebrow(),
            r"^Source snapshot, \d{4}-\d{2}-\d{2}, branch \S+$",
        )
        import unittest.mock as mock

        with mock.patch.object(archview.subprocess, "run", side_effect=OSError("no git")):
            eyebrow = archview._atlas_eyebrow()
        self.assertRegex(
            eyebrow, r"^Source snapshot, \d{4}-\d{2}-\d{2}, branch unknown$"
        )

    def test_module_without_a_summary(self):
        prose = _atlas_prose()
        del prose["modules"]["Beta"]
        text = archview.render_atlas_html(_atlas_model(), prose)
        self.assertIn("(no summary yet)", text)

    def test_vanished_reading_is_not_rendered(self):
        prose = _atlas_prose()
        prose["upward_readings"] = [{"edge": "Gone -> Type", "reading": "Vanished reading."}]
        text = archview.render_atlas_html(_atlas_model(), prose)
        self.assertNotIn("Vanished reading.", text)

    def test_svg_is_inlined_without_prolog_or_size(self):
        svg = (
            '<?xml version="1.0" encoding="UTF-8"?>\n'
            '<!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://x/x.dtd">\n'
            "<!-- Generated by graphviz -->\n"
            '<svg width="100pt" height="50pt" viewBox="0 0 100 50">'
            "<g/></svg>"
        )
        text = archview.render_atlas_html(_atlas_model(), _atlas_prose(), svg)
        self.assertNotIn("<?xml", text)
        self.assertEqual(text.count("<!DOCTYPE"), 1)
        self.assertNotIn("Generated by graphviz", text)
        self.assertIn('<svg viewBox="0 0 100 50">', text)
        self.assertNotIn('width="100pt"', text)


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
        self.assertIn('"role":"service"', text)

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

    def test_parse_edge_spec_rejects_non_strings(self):
        with self.assertRaises(ValueError):
            archview.parse_edge_spec(5)


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


class HistoryParseTests(unittest.TestCase):
    def test_default_since_is_eighteen_months_back_on_the_first(self):
        today = datetime.date.today()
        if today.month > 6:
            expected = datetime.date(today.year - 1, today.month - 6, 1)
        else:
            expected = datetime.date(today.year - 2, today.month + 6, 1)
        self.assertEqual(archview.default_since(), expected.isoformat())

    def test_history_git_args_pin_the_window_and_format(self):
        self.assertEqual(
            archview.history_git_args("2025-03-01"),
            [
                "git",
                "log",
                "--no-merges",
                "--since=2025-03-01",
                "--date=short",
                "--pretty=format:COMMIT%x09%H%x09%ad",
                "--name-only",
                "--",
                "Source/Parsek",
            ],
        )

    def test_parse_history_filters_skips_and_dates(self):
        sweep = "\n".join("Source/Parsek/Sweep%02d.cs" % index for index in range(41))
        text = (
            "COMMIT\tabc123\t2026-09-01\n"
            "Source/Parsek/Alpha.cs\n"
            "Source/Parsek/UI/Thing.cs\n"
            "Source/Parsek/Properties/AssemblyInfo.cs\n"
            "Source/Parsek/notes.txt\n"
            "docs/Other.cs\n"
            "\n"
            "COMMIT\tdef456\t2026-08-15\n"
            "Source/Parsek/Beta.cs\r\n"
            "\n"
            "COMMIT\ttext1\t2026-08-01\n"
            "docs/readme.md\n"
            "\n"
            "COMMIT\tsweep1\t2026-07-01\n" + sweep + "\n"
        )
        parsed = archview.parse_history(text)
        self.assertEqual([commit["sha"] for commit in parsed["commits"]], ["abc123", "def456"])
        self.assertEqual(parsed["commits"][0]["date"], "2026-09-01")
        self.assertEqual(parsed["commits"][0]["files"], ["Alpha.cs", "UI/Thing.cs"])
        self.assertEqual(parsed["commits"][1]["files"], ["Beta.cs"])
        self.assertEqual(parsed["skipped"], 1)
        self.assertEqual(parsed["largest"], 41)

    def test_parse_history_without_commits_is_empty(self):
        self.assertEqual(
            archview.parse_history(""),
            {"commits": [], "skipped": 0, "largest": 0},
        )

    def test_parse_history_dedups_and_keeps_exactly_forty(self):
        text = (
            "COMMIT\td1\t2026-09-01\n"
            "Source/Parsek/Alpha.cs\n"
            "Source/Parsek/Alpha.cs\n"
            "\n"
            "COMMIT\td2\t2026-09-02\n"
            + "\n".join("Source/Parsek/Keep%02d.cs" % index for index in range(40))
            + "\n"
        )
        parsed = archview.parse_history(text)
        self.assertEqual(parsed["commits"][0]["files"], ["Alpha.cs"])
        self.assertEqual(len(parsed["commits"][1]["files"]), 40)
        self.assertEqual(parsed["skipped"], 0)

    def test_parse_history_unquotes_a_quoted_path(self):
        text = 'COMMIT\tq1\t2026-09-01\n"Source/Parsek/Quoted.cs"\n'
        parsed = archview.parse_history(text)
        self.assertEqual(parsed["commits"][0]["files"], ["Quoted.cs"])


class HistoryMetricsTests(unittest.TestCase):
    @staticmethod
    def _model():
        return {
            "modules": [
                {"name": "One", "files": 2, "fanIn": 0, "fanOut": 0, "instability": 0.5,
                 "tooling": False},
                {"name": "Two", "files": 1, "fanIn": 0, "fanOut": 0, "instability": 0.5,
                 "tooling": False},
                {"name": "Tool", "files": 1, "fanIn": 0, "fanOut": 0, "instability": 0.5,
                 "tooling": True},
            ],
            "fileModules": {
                "One/A.cs": "One",
                "One/B.cs": "One",
                "Two/C.cs": "Two",
                "Tool/T.cs": "Tool",
            },
            "types": [
                {"name": "AlphaThing", "file": "One/A.cs", "module": "One", "fanIn": 10},
                {"name": "BetaThing", "file": "One/B.cs", "module": "One", "fanIn": 3},
                {"name": "GammaThing", "file": "Two/C.cs", "module": "Two", "fanIn": 7},
                {"name": "ToolThing", "file": "Tool/T.cs", "module": "Tool", "fanIn": 5},
            ],
        }

    def test_counts_hotspots_and_jaccard(self):
        commits = [
            {"sha": "c1", "date": "2026-09-01", "files": ["One/A.cs", "One/A.cs", "Two/C.cs"]},
            {"sha": "c2", "date": "2026-08-01", "files": ["One/A.cs", "One/B.cs"]},
            {"sha": "c3", "date": "2026-07-01", "files": ["One/B.cs"]},
            {"sha": "c4", "date": "2026-07-02", "files": ["Tool/T.cs"]},
        ]
        metrics = archview.history_metrics(commits, self._model(), [])
        files = {row["file"]: row for row in metrics["files"]}
        self.assertEqual(files["One/A.cs"]["commits"], 2)
        self.assertEqual(files["One/B.cs"]["commits"], 2)
        self.assertEqual(files["Two/C.cs"]["commits"], 1)
        self.assertNotIn("Tool/T.cs", files)
        self.assertEqual(files["One/A.cs"]["lastTouched"], "2026-09-01")
        self.assertEqual(files["One/A.cs"]["churnRank"], 1)
        self.assertEqual(files["One/B.cs"]["churnRank"], 2)
        self.assertEqual(files["Two/C.cs"]["churnRank"], 3)
        hotspots = {row["name"]: row for row in metrics["hotspots"]}
        self.assertEqual(hotspots["AlphaThing"]["fileCommits"], 2)
        self.assertEqual(hotspots["AlphaThing"]["hotspot"], 20)
        self.assertNotIn("ToolThing", hotspots)
        pair = [
            row for row in metrics["modulePairs"] if (row["a"], row["b"]) == ("One", "Two")
        ][0]
        self.assertEqual(pair["both"], 1)
        self.assertEqual(pair["either"], 3)
        self.assertAlmostEqual(pair["jaccard"], round(1 / 3, 4))
        # One cross-module co-change is below the count >= 5 floor for the list.
        self.assertEqual(metrics["filePairs"], [])

    def test_cross_module_file_pair_counted_once_per_commit(self):
        commits = [
            {"sha": "c%d" % index, "date": "2026-09-0%d" % index,
             "files": ["One/A.cs", "One/A.cs", "Two/C.cs"] if index == 1
             else ["One/A.cs", "Two/C.cs"]}
            for index in range(1, 6)
        ]
        metrics = archview.history_metrics(commits, self._model(), [])
        self.assertEqual(
            metrics["filePairs"],
            [{"a": "One/A.cs", "b": "Two/C.cs", "moduleA": "One", "moduleB": "Two", "count": 5}],
        )

    def test_cross_module_file_pair_below_the_floor(self):
        commits = [
            {"sha": "c%d" % index, "date": "2026-09-%02d" % index,
             "files": ["One/A.cs", "Two/C.cs"]}
            for index in range(1, 5)
        ]
        metrics = archview.history_metrics(commits, self._model(), [])
        self.assertEqual(metrics["filePairs"], [])

    def test_unknown_history_files_go_through_the_rules(self):
        commits = [{"sha": "c1", "date": "2026-09-01", "files": ["LegacyOld.cs"]}]
        metrics = archview.history_metrics(
            commits, self._model(), [{"name": "One", "prefix": "^Legacy"}]
        )
        files = {row["file"]: row for row in metrics["files"]}
        self.assertEqual(files["LegacyOld.cs"]["commits"], 1)
        self.assertEqual(files["LegacyOld.cs"]["module"], "One")


class KernelGuardTests(unittest.TestCase):
    def test_unplaced_root_file_is_warned_about_and_skipped(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            src = root / "src"
            src.mkdir()
            (src / "KernelFile.cs").write_text("class KernelThing { }", encoding="utf-8")
            (src / "MysteryFile.cs").write_text("class MysteryThing { }", encoding="utf-8")
            (src / "Other").mkdir()
            (src / "Other" / "Thing.cs").write_text("class OtherThing { }", encoding="utf-8")
            modules = root / "modules.toml"
            modules.write_text(
                '[[module]]\nname = "Core"\nprefix = "^KernelFile[.]cs$"\n',
                encoding="utf-8",
            )
            captured = io.StringIO()
            with contextlib.redirect_stdout(captured), contextlib.redirect_stderr(io.StringIO()):
                rc = archview.main(
                    [
                        "--source",
                        str(src),
                        "--modules",
                        str(modules),
                        "--out",
                        str(root / "out"),
                    ]
                )
            self.assertEqual(rc, 0)
            output = captured.getvalue()
            self.assertIn(
                "WARN unplaced root file: MysteryFile.cs (add a rule to modules.toml;"
                " Core is the kernel, not a catch-all)",
                output,
            )
            self.assertIn(
                "WARN unclassified file: Other/Thing.cs (add a rule to modules.toml)",
                output,
            )
            rules, tooling, _forbidden, _allowed = archview.load_rules(modules)
            model = archview.build_model(src, rules, tooling)
            self.assertEqual(model["fileModules"], {"KernelFile.cs": "Core"})
            self.assertEqual(model["unclassified"], ["MysteryFile.cs", "Other/Thing.cs"])


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


SIZE_SAMPLE = """namespace N
{
    public class SampleThing
    {
        private static int counter;
        private static readonly int Cap = 4;
        private const int Limit = 8;
        private int instanceField;
        private static readonly Dictionary<string, int> Cache = new Dictionary<string, int>();
        private static readonly Dictionary<int, (double a, double b)> Spans =
            new Dictionary<int, (double a, double b)>();
        private static readonly int[] Steps = { 1, 2, 3 };
        private static readonly string Label = "x";
        private static readonly IReadOnlyList<int> Fixed = new List<int>();
        private static SampleThing instance;
        internal static SampleThing Instance => instance;
        internal static bool Armed { get; set; }
        internal static int Seen { get; }
        internal static event Action<int> Changed;

        public SampleThing(int seed)
        {
            instanceField = seed;
        }

        internal static (bool ok, int size) Measure(string key)
        {
            return (true, key.Length);
        }

        internal static System.Collections.IEnumerator Walk()
        {
            yield return null;
        }

        internal static int Cached(string key)
        {
            return Cache[key];
        }

        internal static bool IsBig(int value)
        {
            return value > Limit;
        }

        /* a block comment
           that spans
           four lines
           in the source */
        public IEnumerator Run()
        {
            yield return null;
        }

        internal static int Doubled(int value) => value * 2;

        internal static bool TryPick<T>(T candidate) where T : class
        {
            return candidate != null;
        }

        private void Touch(Vessel v)
        {
            counter = counter + 1;
        }

        private static int Bumped(int value)
        {
            return counter + value;
        }

        public int Ratio
        {
            get
            {
                if (instanceField == 0)
                {
                    return 0;
                }
                return Doubled(instanceField);
            }
        }

        public class Nested
        {
            public void Inner()
            {
            }
        }
    }
}
"""


def _scan_sample(source, name):
    drops = []
    stripped = archview.strip_comments_and_strings(source, drops)
    line_at = archview.line_index(stripped, drops)
    declarations = archview.type_declarations(stripped)
    index = next(i for i, d in enumerate(declarations) if d["name"] == name)
    return archview.scan_declaration_members(stripped, declarations, index, line_at), stripped


class LineIndexTests(unittest.TestCase):
    def test_offsets_map_back_to_original_lines_across_a_block_comment(self):
        source = "line1\n/* two\nthree\nfour */\nTARGET\n"
        drops = []
        stripped = archview.strip_comments_and_strings(source, drops)
        line_at = archview.line_index(stripped, drops)
        self.assertEqual(line_at(stripped.index("TARGET")), 5)
        # Two newlines live inside the comment span; the one that ends the
        # `four */` line survives the strip and is counted the ordinary way.
        self.assertEqual(drops, [(6, 2)])

    def test_multi_line_string_bodies_keep_the_line_count(self):
        source = 'var x = @"one\ntwo\nthree";\nTARGET\n'
        drops = []
        stripped = archview.strip_comments_and_strings(source, drops)
        line_at = archview.line_index(stripped, drops)
        self.assertEqual(line_at(stripped.index("TARGET")), 4)

    def test_the_stripped_text_is_identical_with_and_without_the_list(self):
        source = "class A { /* x\ny */ int i; }\n"
        self.assertEqual(
            archview.strip_comments_and_strings(source),
            archview.strip_comments_and_strings(source, []),
        )


class MemberScanTests(unittest.TestCase):
    def setUp(self):
        self.facts, self.stripped = _scan_sample(SIZE_SAMPLE, "SampleThing")
        self.methods = {method["name"]: method for method in self.facts["methods"]}

    def test_block_expression_generic_tuple_and_constructor_members_are_delimited(self):
        self.assertEqual(
            sorted(self.methods),
            [
                "Bumped",
                "Cached",
                "Doubled",
                "IsBig",
                "Measure",
                "Run",
                "SampleThing",
                "Touch",
                "TryPick",
                "Walk",
            ],
        )
        self.assertEqual(self.facts["skipped"], 0)

    def test_a_constructor_is_a_member_with_a_body(self):
        ctor = self.methods["SampleThing"]
        self.assertTrue(ctor["constructor"])
        self.assertEqual(ctor["lines"], 4)
        self.assertFalse(ctor["static"])
        self.assertFalse(any(m["constructor"] for name, m in self.methods.items() if name != "SampleThing"))

    def test_a_tuple_return_type_is_read_as_the_return_type(self):
        measure = self.methods["Measure"]
        self.assertEqual(measure["ret"], "(bool ok, int size)")
        self.assertEqual(measure["lines"], 4)

    def test_a_name_that_is_a_modifier_is_a_scan_failure_not_a_member(self):
        # The shape that used to print `static 110 lines at ...`.
        source = "class Oddity\n{\n    internal static (bool a, int b) Go(int x) { return (true, x); }\n}\n"
        facts, _stripped = _scan_sample(source, "Oddity")
        self.assertEqual([m["name"] for m in facts["methods"]], ["Go"])
        self.assertEqual(facts["skipped"], 0)
        blind = "class Oddity\n{\n    internal Thing static (int x) { }\n}\n"
        facts, _stripped = _scan_sample(blind, "Oddity")
        self.assertEqual(facts["methods"], [])
        self.assertEqual(facts["skipped"], 1)

    def test_start_lines_are_the_real_source_lines(self):
        lines = SIZE_SAMPLE.splitlines()
        for name, method in self.methods.items():
            self.assertIn(name, lines[method["startLine"] - 1], name)

    def test_method_length_counts_header_to_closing_brace(self):
        self.assertEqual(self.methods["IsBig"]["lines"], 4)
        self.assertEqual(self.methods["Doubled"]["lines"], 1)

    def test_nested_type_members_belong_to_the_nested_type(self):
        self.assertNotIn("Inner", self.methods)
        self.assertEqual(self.facts["nested"], 1)
        nested, _stripped = _scan_sample(SIZE_SAMPLE, "Nested")
        self.assertEqual([m["name"] for m in nested["methods"]], ["Inner"])

    def test_statements_inside_a_property_body_are_not_members(self):
        # `return Doubled(instanceField);` and `if (instanceField == 0)` read
        # like declarations to the header pattern; neither may be counted.
        self.assertEqual(len(self.facts["methods"]), 10)

    def test_mutable_static_detection(self):
        fields = {field["name"]: field for field in self.facts["fields"]}
        self.assertEqual(
            sorted(fields),
            ["Armed", "Cache", "Cap", "Changed", "Fixed", "Label", "Limit", "Spans", "Steps",
             "counter", "instance", "instanceField"],
        )
        self.assertTrue(fields["counter"]["mutableStatic"])
        self.assertFalse(fields["Cap"]["mutableStatic"])
        self.assertFalse(fields["Limit"]["mutableStatic"])
        self.assertFalse(fields["instanceField"]["mutableStatic"])

    def test_an_expression_bodied_property_is_not_a_field(self):
        # `internal static SampleThing Instance => instance;` is a forward, not
        # state; counting its `=` made every such property read as a
        # reassignable static.
        self.assertNotIn("Instance", {field["name"] for field in self.facts["fields"]})

    def test_a_static_auto_property_with_a_setter_is_reassignable_state(self):
        fields = {field["name"]: field for field in self.facts["fields"]}
        self.assertTrue(fields["Armed"]["mutableStatic"])
        self.assertEqual(fields["Armed"]["kind"], "autoProperty")
        # Get-only has no setter to reassign through, so it is left out.
        self.assertNotIn("Seen", fields)

    def test_a_static_event_is_reassignable_state(self):
        fields = {field["name"]: field for field in self.facts["fields"]}
        self.assertTrue(fields["Changed"]["mutableStatic"])

    def test_readonly_collection_statics_are_counted_as_static_state(self):
        fields = {field["name"]: field for field in self.facts["fields"]}
        # Fixed handle, mutable contents. `Spans` also proves a tuple inside
        # the generic arguments does not hide the field from the scan.
        for name in ["Cache", "Spans", "Steps"]:
            self.assertTrue(fields[name]["readonlyCollectionStatic"], name)
            self.assertFalse(fields[name]["mutableStatic"], name)
        for name in ["Cap", "Label", "Fixed", "counter", "instanceField"]:
            self.assertFalse(fields[name]["readonlyCollectionStatic"], name)

    def test_coroutine_flag(self):
        self.assertTrue(self.methods["Run"]["coroutine"])
        self.assertFalse(self.methods["IsBig"]["coroutine"])

    def test_a_qualified_or_generic_enumerator_is_still_a_coroutine(self):
        self.assertTrue(self.methods["Walk"]["coroutine"])
        self.assertTrue(archview.is_enumerator_return("System.Collections.IEnumerator"))
        self.assertTrue(archview.is_enumerator_return("IEnumerator<int>"))
        self.assertTrue(archview.is_enumerator_return("System.Collections.Generic.IEnumerator<T>"))
        self.assertFalse(archview.is_enumerator_return("IEnumerable"))
        self.assertFalse(archview.is_enumerator_return("int"))

    def test_purity_estimate_rejects_live_types_and_shared_statics(self):
        merged = archview.merge_type_size(
            [{"file": "One/A.cs", "facts": self.facts}],
            lambda _file, start, end: self.stripped[start:end],
        )
        self.assertEqual(merged["mutableStaticNames"], ["Armed", "Changed", "counter", "instance"])
        self.assertEqual(merged["readonlyCollectionStaticNames"], ["Cache", "Spans", "Steps"])
        self.assertEqual(merged["mutableStatics"], 4)
        self.assertEqual(merged["readonlyCollectionStatics"], 3)
        # IsBig, Doubled, TryPick and Measure are static and name no shared
        # state; Bumped reads `counter`, Cached reads the shared `Cache`;
        # Touch is not static; Run and Walk are coroutines.
        self.assertEqual(merged["pureStaticMethods"], 4)
        self.assertEqual(merged["coroutines"], 2)


class PurityEstimateTests(unittest.TestCase):
    def test_a_live_ksp_identifier_is_impure(self):
        for body in [
            "{ return ResearchAndDevelopment.Instance != null; }",
            "{ File.WriteAllText(path, text); }",
            "{ var r = Resources.Load(name); }",
            "{ return part.vessel != null && Part.Count > 0; }",
            "{ return CelestialBody.Count; }",
        ]:
            self.assertFalse(archview.method_is_pure_candidate(body, []), body)

    def test_a_singleton_access_is_impure_whatever_the_type(self):
        self.assertFalse(archview.method_is_pure_candidate("{ return Whatever.Instance.X; }", []))
        self.assertFalse(archview.method_is_pure_candidate("{ return Widget.fetch.Y; }", []))
        self.assertTrue(archview.method_is_pure_candidate("{ return instanceCount; }", []))

    def test_reaching_another_type_that_holds_static_state_is_impure(self):
        body = "{ return RecordingStore.CommittedRecordings.Count; }"
        self.assertFalse(
            archview.method_is_pure_candidate(body, [], {"RecordingStore"}, "Other")
        )
        # The same call into a type with no static state stays pure.
        self.assertTrue(archview.method_is_pure_candidate(body, [], {"SomethingElse"}, "Other"))

    def test_a_types_own_name_does_not_disqualify_it(self):
        body = "{ return Thing.Parse(text); }"
        self.assertTrue(archview.method_is_pure_candidate(body, [], {"Thing"}, "Thing"))

    def test_logging_is_exempt(self):
        body = "{ ParsekLog.Verbose(\"[Size] x\"); return 1; }"
        self.assertTrue(
            archview.method_is_pure_candidate(body, [], {"ParsekLog", "RecordingStore"}, "Thing")
        )


class MutableCollectionTypeTests(unittest.TestCase):
    def test_collections_arrays_and_builders_count(self):
        for text in [
            "Dictionary<string, int>",
            "HashSet<uint>",
            "List<Recording>",
            "ConcurrentDictionary<int, string>",
            "SortedSet<int>",
            "Queue<string>",
            "Stack<int>",
            "StringBuilder",
            "int[]",
            "Vector3[,]",
            "Dictionary<int, (double a, double b)>",
        ]:
            self.assertTrue(archview.is_mutable_collection_type(text), text)

    def test_scalars_and_read_only_handles_do_not(self):
        for text in [
            "int",
            "string",
            "double",
            "CultureInfo",
            "IReadOnlyList<int>",
            "ReadOnlyCollection<string>",
            "ImmutableArray<int>",
            "FrozenDictionary<int, int>",
            "GhostState",
        ]:
            self.assertFalse(archview.is_mutable_collection_type(text), text)

    def test_a_header_the_scan_cannot_follow_is_skipped_not_guessed(self):
        source = "class Broken\n{\n    public void Cut(int a) % { }\n}\n"
        facts, _stripped = _scan_sample(source, "Broken")
        self.assertEqual(facts["methods"], [])
        self.assertEqual(facts["skipped"], 1)

    def test_member_body_kinds(self):
        self.assertEqual(archview._member_body(" { }", 0, 4)[0], "block")
        self.assertEqual(archview._member_body(" => 1;", 0, 6)[0], "expression")
        self.assertEqual(archview._member_body(";", 0, 1)[0], "none")
        self.assertEqual(archview._member_body(" % {", 0, 4)[0], "skip")

    def test_bodyless_declarations_are_counted_apart_from_methods(self):
        # An interface or abstract declaration has no body to extract from, so
        # it is not a method here; it is not a scan failure either.
        source = "interface IShape\n{\n    bool Fits(int size);\n    void Draw();\n}\n"
        facts, _stripped = _scan_sample(source, "IShape")
        self.assertEqual(facts["methods"], [])
        self.assertEqual(facts["bodyless"], 2)
        self.assertEqual(facts["skipped"], 0)


class MergeTypeSizeTests(unittest.TestCase):
    def test_partial_parts_merge_into_one_type(self):
        big = "public partial class BigThing\n{\n%s\n}\n" % "\n".join(
            "    private static int Field%d;" % index for index in range(30)
        )
        small = (
            "public partial class BigThing\n{\n    internal static bool Ok()\n    {\n"
            "        return true;\n    }\n}\n"
        )
        big_facts, big_text = _scan_sample(big, "BigThing")
        small_facts, small_text = _scan_sample(small, "BigThing")
        texts = {"Big.cs": big_text, "Small.cs": small_text}
        merged = archview.merge_type_size(
            [
                {"file": "Big.cs", "facts": big_facts},
                {"file": "Small.cs", "facts": small_facts},
            ],
            lambda name, start, end: texts[name][start:end],
        )
        self.assertEqual(merged["fields"], 30)
        self.assertEqual(merged["mutableStatics"], 30)
        self.assertEqual(merged["methods"], 1)
        self.assertEqual([row["file"] for row in merged["files"]], ["Big.cs", "Small.cs"])
        self.assertEqual(merged["lines"], sum(row["lines"] for row in merged["files"]))


def _size_entry(**overrides):
    entry = {
        "name": "Thing",
        "module": "Recording",
        "file": "One/Thing.cs",
        "files": [{"file": "One/Thing.cs", "lines": 400}],
        "lines": 400,
        "methods": 20,
        "longMethods": 0,
        "topMethods": [],
        "coroutines": 0,
        "fields": 4,
        "mutableStatics": 0,
        "readonlyCollectionStatics": 0,
        "nestedTypes": 0,
        "topLevelTypesInFile": 1,
        "primaryTypeOfItsFile": True,
        "enclosing": None,
        "partial": False,
        "pureStaticMethods": 0,
        "pureStaticLines": 0,
        "skippedMembers": 0,
        "hotspotRank": None,
        "fileCommits": 0,
        "fanIn": 3,
    }
    entry.update(overrides)
    return entry


class SizeRuleTests(unittest.TestCase):
    @staticmethod
    def _rules(entry, runtime_coupled=("Ghost",)):
        return {
            row["rule"]: row["text"]
            for row in archview.size_recommendations(entry, runtime_coupled)
        }

    def test_no_rule_fires_on_a_small_quiet_type(self):
        self.assertEqual(self._rules(_size_entry()), {})

    def test_s1_fires_on_long_methods_and_names_the_coroutines(self):
        entry = _size_entry(
            longMethods=2,
            coroutines=3,
            topMethods=[
                {"name": "Huge", "file": "One/Thing.cs", "startLine": 12, "lines": 300},
                {"name": "Small", "file": "One/Thing.cs", "startLine": 400, "lines": 10},
            ],
        )
        rules = self._rules(entry)
        self.assertIn("S1", rules)
        self.assertIn("Huge 300 lines at One/Thing.cs:12", rules["S1"])
        self.assertNotIn("Small", rules["S1"])
        self.assertIn("3 IEnumerator", rules["S1"])
        self.assertNotIn("S1", self._rules(_size_entry(longMethods=0)))

    def test_s2_needs_a_big_pool_and_a_big_share(self):
        lines = 4000
        share = int(lines * archview.PURE_POOL_SHARE) + 10
        self.assertIn(
            "S2",
            self._rules(
                _size_entry(
                    lines=lines,
                    pureStaticMethods=archview.PURE_POOL_METHODS,
                    pureStaticLines=share,
                )
            ),
        )
        self.assertIn(
            "S2",
            self._rules(
                _size_entry(
                    lines=lines,
                    pureStaticMethods=1,
                    pureStaticLines=max(archview.PURE_POOL_LINES, share),
                )
            ),
        )
        # A big pool that is a small share of a huge type does not fire: that
        # was the shape that made S2 hit 25 of the top 25 types.
        self.assertNotIn(
            "S2",
            self._rules(
                _size_entry(
                    lines=30000,
                    pureStaticMethods=300,
                    pureStaticLines=6000,
                )
            ),
        )
        self.assertNotIn(
            "S2",
            self._rules(
                _size_entry(
                    lines=lines,
                    pureStaticMethods=archview.PURE_POOL_METHODS - 1,
                    pureStaticLines=archview.PURE_POOL_LINES - 1,
                )
            ),
        )

    def test_s3_fires_on_the_largest_file_and_cites_it(self):
        entry = _size_entry(
            lines=archview.GIANT_TYPE_LINES + 2000,
            files=[
                {"file": "One/Thing.cs", "lines": archview.GIANT_TYPE_LINES},
                {"file": "One/Thing.Extra.cs", "lines": 2000},
            ],
        )
        text = self._rules(entry)["S3"]
        self.assertIn("One/Thing.cs holds", text)
        self.assertIn("across 2 file(s)", text)

    def test_s3_does_not_fire_on_a_type_already_split_into_ordinary_files(self):
        # Six 1,000-line partial files: the total clears the giant threshold,
        # but the rule asks for a split that has already happened.
        entry = _size_entry(
            lines=6000,
            files=[{"file": "One/Thing.%d.cs" % index, "lines": 1000} for index in range(6)],
        )
        self.assertNotIn("S3", self._rules(entry))

    def test_s4_fires_on_the_sum_of_both_static_kinds(self):
        half = archview.MUTABLE_STATIC_FLOOR // 2
        entry = _size_entry(
            mutableStatics=half,
            readonlyCollectionStatics=archview.MUTABLE_STATIC_FLOOR - half,
        )
        text = self._rules(entry)["S4"]
        self.assertIn("%d reassignable" % half, text)
        self.assertIn("%d readonly collections" % (archview.MUTABLE_STATIC_FLOOR - half), text)
        self.assertIn(
            "S4", self._rules(_size_entry(readonlyCollectionStatics=archview.MUTABLE_STATIC_FLOOR))
        )
        self.assertNotIn(
            "S4",
            self._rules(
                _size_entry(mutableStatics=half, readonlyCollectionStatics=half - 1)
            ),
        )

    def test_s5_gives_sibling_advice_only_to_the_row_that_owns_the_file(self):
        crowded = dict(topLevelTypesInFile=archview.SIBLING_TYPE_FLOOR + 5)
        # A nested type has no siblings of its own to move.
        self.assertNotIn(
            "S5", self._rules(_size_entry(enclosing="Outer", **crowded))
        )
        # Neither does a small top-level type sharing a file it does not own;
        # otherwise all 17 types in one file each get the same advice.
        self.assertNotIn(
            "S5", self._rules(_size_entry(primaryTypeOfItsFile=False, **crowded))
        )
        self.assertIn("S5", self._rules(_size_entry(**crowded)))

    def test_s5_sends_nested_types_to_a_partial_file_and_siblings_to_their_own(self):
        nested = self._rules(
            _size_entry(nestedTypes=archview.NESTED_TYPE_FLOOR, partial=True)
        )["S5"]
        self.assertIn("partial file of Thing itself (it is partial today)", nested)
        self.assertNotIn("their own files", nested)
        not_partial = self._rules(_size_entry(nestedTypes=archview.NESTED_TYPE_FLOOR))["S5"]
        self.assertIn("it is not partial today", not_partial)
        siblings = self._rules(
            _size_entry(topLevelTypesInFile=archview.SIBLING_TYPE_FLOOR)
        )["S5"]
        self.assertIn("sibling top-level type(s) in One/Thing.cs move to their own files", siblings)
        self.assertNotIn(
            "S5",
            self._rules(
                _size_entry(
                    nestedTypes=archview.NESTED_TYPE_FLOOR - 1,
                    topLevelTypesInFile=archview.SIBLING_TYPE_FLOOR - 1,
                )
            ),
        )

    def test_s6_fires_only_for_a_runtime_coupled_module(self):
        self.assertIn("S6", self._rules(_size_entry(module="Ghost")))
        self.assertNotIn("S6", self._rules(_size_entry(module="Recording")))

    def test_s7_fires_inside_the_hotspot_window(self):
        self.assertIn("S7", self._rules(_size_entry(hotspotRank=archview.HOTSPOT_PRIORITY_RANK)))
        self.assertNotIn(
            "S7", self._rules(_size_entry(hotspotRank=archview.HOTSPOT_PRIORITY_RANK + 1))
        )


class SizeTierTests(unittest.TestCase):
    def test_small_quiet_type_is_a_watch(self):
        self.assertEqual(archview.size_tier(_size_entry(lines=100)), ("watch", 0))

    def test_large_file_alone_is_one_point(self):
        self.assertEqual(
            archview.size_tier(_size_entry(lines=archview.LARGE_FILE_LINES)), ("watch", 1)
        )

    def test_giant_plus_long_methods_reaches_tier_one(self):
        entry = _size_entry(lines=archview.GIANT_TYPE_LINES, longMethods=8)
        self.assertEqual(archview.size_tier(entry), ("Tier 1", 4))

    def test_each_axis_contributes_once(self):
        entry = _size_entry(
            lines=archview.GIANT_TYPE_LINES,
            longMethods=3,
            mutableStatics=archview.MUTABLE_STATIC_FLOOR,
            hotspotRank=1,
        )
        self.assertEqual(archview.size_tier(entry), ("Tier 1", 5))
        self.assertEqual(
            archview.size_tier(_size_entry(lines=archview.LARGE_FILE_LINES, longMethods=3)),
            ("Tier 2", 2),
        )

    def test_the_static_point_reads_both_kinds_together(self):
        half = archview.MUTABLE_STATIC_FLOOR // 2
        entry = _size_entry(
            lines=100,
            mutableStatics=half,
            readonlyCollectionStatics=archview.MUTABLE_STATIC_FLOOR - half,
        )
        self.assertEqual(archview.size_tier(entry), ("watch", 1))

    def test_size_points_follow_the_total_not_the_largest_file(self):
        # The same 6,000 lines spread over six files: still a big type (2
        # points), even though S3 does not fire on it.
        spread = _size_entry(
            lines=6000,
            files=[{"file": "One/Thing.%d.cs" % index, "lines": 1000} for index in range(6)],
        )
        self.assertEqual(archview.size_tier(spread), ("Tier 2", 2))
        self.assertNotIn(
            "S3", [row["rule"] for row in archview.size_recommendations(spread)]
        )


class GrowthParseTests(unittest.TestCase):
    def test_no_git_output_is_an_empty_table(self):
        self.assertEqual(archview.parse_growth(""), {"files": {}, "commits": 0, "skipped": 0})

    def test_added_minus_deleted_per_file(self):
        text = (
            "COMMIT\taaa\n"
            "10\t2\tSource/Parsek/One.cs\n"
            "5\t0\tSource/Parsek/Two.cs\n"
            "COMMIT\tbbb\n"
            "1\t7\tSource/Parsek/One.cs\n"
        )
        parsed = archview.parse_growth(text)
        # One.cs: +10-2 then +1-7, so the net over the window is 2, not 11.
        self.assertEqual(parsed["files"], {"One.cs": 2, "Two.cs": 5})
        self.assertEqual(parsed["commits"], 2)

    def test_binary_rename_and_foreign_rows_are_ignored(self):
        text = (
            "COMMIT\taaa\n"
            "-\t-\tSource/Parsek/Art.png\n"
            "4\t1\tSource/Parsek/{Old => New}/Thing.cs\n"
            "3\t0\tdocs/dev/notes.md\n"
            "2\t0\tSource/Parsek/bin/Generated.cs\n"
            "6\t1\tSource/Parsek/Real.cs\n"
        )
        self.assertEqual(archview.parse_growth(text)["files"], {"Real.cs": 5})

    def test_a_sweep_commit_is_skipped(self):
        rows = "".join(
            "1\t0\tSource/Parsek/File%d.cs\n" % index
            for index in range(archview.HISTORY_SWEEP_LIMIT + 1)
        )
        parsed = archview.parse_growth("COMMIT\taaa\n" + rows)
        self.assertEqual(parsed["files"], {})
        self.assertEqual(parsed["skipped"], 1)
        self.assertEqual(parsed["commits"], 0)


class PartialFileAttributionTests(unittest.TestCase):
    """The primary file of a partial type, and the hotspot join that reads it."""

    @staticmethod
    def _write(root, rel, text):
        path = root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def _partial_model(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            # The SMALL part sorts first, so a first-seen rule attributes the
            # type to it and the churn join then reads the wrong file.
            self._write(root, "One/Aside.cs", "public partial class BigThing\n{\n    int a;\n}\n")
            self._write(
                root,
                "One/Main.cs",
                "public partial class BigThing\n{\n%s\n}\n"
                % "\n".join("    int f%d;" % index for index in range(30)),
            )
            return archview.build_model(root, [{"name": "One", "folder": "One"}], set())

    def test_two_nested_types_sharing_a_name_are_two_size_rows(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            self._write(
                root,
                "One/AlphaOwner.cs",
                "public class AlphaOwner\n{\n    internal class Handlers\n    {\n"
                "        internal static void Go() { }\n    }\n}\n",
            )
            self._write(
                root,
                "One/BetaOwner.cs",
                "public class BetaOwner\n{\n    internal class Handlers\n    {\n"
                "        internal static void Stop() { }\n        internal static void Wait() { }\n"
                "    }\n}\n",
            )
            model = archview.build_model(root, [{"name": "One", "folder": "One"}], set())
        rows = {row["name"]: row for row in model["sizes"]["types"]}
        # `Handlers` is declared twice and is not partial: one row each, keyed
        # by the enclosing type, instead of one row with both bodies in it.
        self.assertIn("AlphaOwner.Handlers", rows)
        self.assertIn("BetaOwner.Handlers", rows)
        self.assertEqual(rows["AlphaOwner.Handlers"]["methods"], 1)
        self.assertEqual(rows["BetaOwner.Handlers"]["methods"], 2)
        self.assertEqual(rows["AlphaOwner.Handlers"]["typeName"], "Handlers")

    def test_a_partial_type_still_merges_across_files(self):
        model = self._partial_model()
        rows = {row["name"]: row for row in model["sizes"]["types"]}
        self.assertEqual(len(rows["BigThing"]["files"]), 2)

    def test_primary_file_is_the_one_holding_the_most_body_lines(self):
        model = self._partial_model()
        entry = {row["name"]: row for row in model["types"]}["BigThing"]
        self.assertEqual(entry["file"], "One/Main.cs")
        self.assertEqual(entry["files"], ["One/Main.cs", "One/Aside.cs"])

    def test_hotspot_counts_the_union_of_the_commits_touching_any_part(self):
        model = self._partial_model()
        commits = [
            {"sha": "a", "date": "2026-01-01", "files": ["One/Main.cs"]},
            {"sha": "b", "date": "2026-01-02", "files": ["One/Main.cs", "One/Aside.cs"]},
            {"sha": "c", "date": "2026-01-03", "files": ["One/Aside.cs"]},
        ]
        metrics = archview.history_metrics(commits, model, [{"name": "One", "folder": "One"}])
        hotspot = {row["name"]: row for row in metrics["hotspots"]}["BigThing"]
        # Three commits touched the type; commit b touched two parts and must
        # not count twice (a sum would say 4, the primary file alone 2).
        self.assertEqual(hotspot["fileCommits"], 3)
        self.assertEqual(hotspot["file"], "One/Main.cs")
        self.assertEqual(sorted(hotspot["files"]), ["One/Aside.cs", "One/Main.cs"])


def _size_model():
    return {
        "types": [
            {
                "name": "Thing",
                "module": "Ghost",
                "file": "One/Thing.cs",
                "role": "static",
                "level": 2,
                "knot": 1,
                "fanIn": 9,
            }
        ],
        "sizes": {
            "files": [
                {
                    "file": "One/Thing.cs",
                    "module": "Ghost",
                    "lines": 6000,
                    "declaredTypes": 2,
                    "topLevelTypes": 1,
                    "types": ["Thing"],
                }
            ],
            "types": [
                {
                    "name": "Thing",
                    "module": "Ghost",
                    "file": "One/Thing.cs",
                    "files": [{"file": "One/Thing.cs", "lines": 5900}],
                    "lines": 5900,
                    "methods": 40,
                    "longMethods": 4,
                    "topMethods": [
                        {"name": "Big", "file": "One/Thing.cs", "startLine": 10, "lines": 200}
                    ],
                    "coroutines": 0,
                    "fields": 20,
                    "mutableStatics": 8,
                    "mutableStaticNames": ["one"],
                    "readonlyCollectionStatics": 4,
                    "readonlyCollectionStaticNames": ["cache"],
                    "nestedTypes": 1,
                    "topLevelTypesInFile": 1,
                    "pureStaticMethods": 2,
                    "pureStaticLines": 30,
                    "skippedMembers": 0,
                }
            ],
        },
    }


class SizeReportTests(unittest.TestCase):
    def test_history_and_growth_join_onto_the_rows(self):
        history = {
            "since": "2025-03-01",
            "files": [{"file": "One/Thing.cs", "commits": 42, "churnRank": 3}],
            "hotspots": [{"name": "Thing", "fileCommits": 42, "fanIn": 9, "hotspot": 378}],
            "filePairs": [
                {
                    "a": "One/Thing.cs",
                    "b": "Two/Other.cs",
                    "count": 9,
                    "moduleA": "Ghost",
                    "moduleB": "UI",
                }
            ],
        }
        growth = {"files": {"One/Thing.cs": 1234}, "commits": 7, "skipped": 0}
        payload = archview.size_report(_size_model(), history, growth, {"runtimeCoupled": ["Ghost"]})
        row = payload["types"][0]
        self.assertEqual(row["churnRank"], 3)
        self.assertEqual(row["hotspotRank"], 1)
        self.assertEqual(row["netLinesAdded"], 1234)
        self.assertEqual(row["coChangePartners"], [{"file": "Two/Other.cs", "count": 9}])
        self.assertEqual(row["tier"], "Tier 1")
        self.assertEqual(
            sorted(item["rule"] for item in row["recommendations"]),
            ["S1", "S3", "S4", "S6", "S7"],
        )
        # S4 fired on 8 reassignable plus 4 readonly collections.
        self.assertEqual(row["mutableStatics"], 8)
        self.assertEqual(row["readonlyCollectionStatics"], 4)
        self.assertNotIn("mutableStaticNames", row)
        self.assertNotIn("readonlyCollectionStaticNames", row)
        self.assertEqual(payload["files"][0]["netLinesAdded"], 1234)
        self.assertTrue(payload["growth"]["available"])

    def test_missing_history_and_growth_leave_null_columns(self):
        payload = archview.size_report(_size_model())
        row = payload["types"][0]
        self.assertIsNone(row["churnRank"])
        self.assertIsNone(row["hotspotRank"])
        self.assertIsNone(row["netLinesAdded"])
        self.assertEqual(row["coChangePartners"], [])
        self.assertFalse(payload["growth"]["available"])
        self.assertNotIn("S7", [item["rule"] for item in row["recommendations"]])

    def test_check_section_prints_the_tables_and_the_rules(self):
        payload = archview.size_report(_size_model(), None, None, {"runtimeCoupled": ["Ghost"]})
        captured = io.StringIO()
        with contextlib.redirect_stdout(captured):
            archview.print_size_section(payload)
        text = captured.getvalue()
        self.assertIn("SIZE (text-scan approximation", text)
        self.assertIn("One/Thing.cs", text)
        self.assertIn("S3", text)
        self.assertIn("no git history", text)

    def test_recommendation_lines_are_wrapped_for_a_terminal(self):
        payload = archview.size_report(_size_model(), None, None, {"runtimeCoupled": ["Ghost"]})
        captured = io.StringIO()
        with contextlib.redirect_stdout(captured):
            archview.print_size_section(payload)
        body = captured.getvalue().splitlines()
        rules = body[body.index("  Recommendations (candidates and evidence, never a verdict):"):]
        self.assertTrue(any(line.strip().startswith("S1") for line in rules))
        for line in rules:
            self.assertLessEqual(len(line), archview.SIZE_CHECK_WIDTH, line)
        # A wrapped rule keeps its continuation indented under the rule text.
        continuations = [line for line in rules if line.startswith("         ") and line.strip()]
        self.assertTrue(continuations)

    def test_empty_size_data_says_so(self):
        captured = io.StringIO()
        with contextlib.redirect_stdout(captured):
            archview.print_size_section(None)
        self.assertIn("no size data", captured.getvalue())

    def test_atlas_renders_the_size_section(self):
        payload = archview.size_report(_size_model(), None, None, {"runtimeCoupled": ["Ghost"]})
        text = archview._atlas_sizes(payload)
        self.assertIn("Largest files", text)
        self.assertIn("Thing", text)
        self.assertIn("S3", text)
        self.assertIn("No size data", archview._atlas_sizes(None))


class SizeSettingsTests(unittest.TestCase):
    def test_the_committed_map_names_the_runtime_coupled_modules(self):
        settings = archview.load_size_settings(archview.DEFAULT_MODULES)
        self.assertIn("Ghost", settings["runtimeCoupled"])
        self.assertIn("UI", settings["runtimeCoupled"])

    def test_a_map_without_a_size_table_falls_back_to_the_defaults(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = pathlib.Path(tmp) / "modules.toml"
            path.write_text('[[module]]\nname = "One"\nfolder = "One"\n', encoding="utf-8")
            settings = archview.load_size_settings(path)
        self.assertEqual(settings["runtimeCoupled"], sorted(archview.DEFAULT_RUNTIME_COUPLED))

    def test_a_missing_file_falls_back_to_the_defaults(self):
        settings = archview.load_size_settings(pathlib.Path("no-such-modules.toml"))
        self.assertEqual(settings["runtimeCoupled"], sorted(archview.DEFAULT_RUNTIME_COUPLED))


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

    def test_atlas_section_reports_prose_staleness(self):
        model = {
            "modules": [
                {"name": "Alpha", "files": 1, "fanIn": 0, "fanOut": 0, "instability": 0.5,
                 "tooling": False}
            ],
            "edges": [],
            "types": [],
            "typeLevels": {"max": 0, "histogram": {}},
            "knots": [],
        }
        prose = {
            "modules": {},
            "glossary": {"GoneThing": {"meaning": "x"}},
            "upward_readings": [{"edge": "Gone -> Type", "reading": "x"}],
            "reading_order": [{"title": "t", "types": ["NopeThing"], "text": "x"}],
        }
        captured = io.StringIO()
        with contextlib.redirect_stdout(captured):
            archview.run_check(model, [], [], prose)
        text = captured.getvalue()
        self.assertIn("ATLAS (prose against the live model)", text)
        self.assertIn("production modules without a summary (1): Alpha", text)
        self.assertIn("glossary entries naming a missing type (1): GoneThing", text)
        self.assertIn("upward readings whose edge no longer exists (1): Gone -> Type", text)
        self.assertIn("reading-order types not in the model (1): NopeThing", text)
        self.assertIn("glossary entries outside the live top 18: none.", text)
        self.assertLess(text.index("ATLAS"), text.index("Forbidden edges"))

    def test_atlas_section_reports_a_stale_glossary_entry(self):
        names = ["Type%02d" % index for index in range(1, 20)]
        model = {
            "modules": [
                {"name": "Alpha", "files": 1, "fanIn": 0, "fanOut": 0, "instability": 0.5,
                 "tooling": False}
            ],
            "edges": [],
            "types": [
                {"name": name, "module": "Alpha", "file": "Alpha/f.cs", "role": "service",
                 "level": 0, "knot": None, "sublevel": None, "fanIn": 100 - index, "fanOut": 0,
                 "referencesTo": [], "referencedBy": []}
                for index, name in enumerate(names)
            ],
            "typeLevels": {"max": 0, "histogram": {0: len(names)}},
            "knots": [],
        }
        prose = {"glossary": {"Type15": {"meaning": "x"}, "Type19": {"meaning": "x"}}}
        captured = io.StringIO()
        with contextlib.redirect_stdout(captured):
            archview.run_check(model, [], [], prose)
        # Type15 is inside the top 18 and must not be flagged; Type19 is not.
        self.assertIn("glossary entries outside the live top 18 (1): Type19", captured.getvalue())

    @staticmethod
    def _two_module_model():
        return {
            "modules": [
                {"name": "Alpha", "files": 1, "fanIn": 0, "fanOut": 0, "instability": 0.5,
                 "tooling": False},
                {"name": "Beta", "files": 1, "fanIn": 0, "fanOut": 0, "instability": 0.5,
                 "tooling": False},
            ],
            "edges": [],
            "types": [],
            "typeLevels": {"max": 0, "histogram": {}},
            "knots": [],
        }

    @staticmethod
    def _small_history():
        return {
            "since": "2025-03-01",
            "commits": 10,
            "skippedSweeps": 1,
            "largestSweep": 42,
            "hotspots": [
                {"name": "HubThing", "module": "Alpha", "file": "Alpha/A.cs",
                 "fileCommits": 4, "fanIn": 9, "hotspot": 36}
            ],
            "modulePairs": [
                {"a": "Alpha", "b": "Beta", "both": 2, "either": 5, "jaccard": 0.4}
            ],
            "filePairs": [
                {"a": "Alpha/A.cs", "b": "Beta/B.cs", "moduleA": "Alpha",
                 "moduleB": "Beta", "count": 6}
            ],
            "modules": [],
        }

    def test_history_section_reports_the_window(self):
        captured = io.StringIO()
        with contextlib.redirect_stdout(captured):
            archview.run_check(
                self._two_module_model(), ["Beta -> Alpha"], [], None, self._small_history()
            )
        text = captured.getvalue()
        self.assertIn("HISTORY (co-change over Source/Parsek):", text)
        self.assertIn(
            "window: since 2025-03-01, 10 commits (1 sweeps skipped, largest 42 files)", text
        )
        self.assertIn("HubThing", text)
        self.assertIn("Alpha <-> Beta: both=2, either=5, jaccard=0.40", text)
        self.assertIn("Alpha/A.cs <-> Beta/B.cs: count=6 (Alpha, Beta)", text)
        self.assertIn("Beta -> Alpha: 2", text)
        self.assertLess(text.index("ATLAS"), text.index("HISTORY"))
        self.assertLess(text.index("HISTORY"), text.index("Forbidden edges"))
        self.assertEqual(self._last_line(text), "ARCH-CHECK report-only")

    def test_history_section_without_history(self):
        captured = io.StringIO()
        with contextlib.redirect_stdout(captured):
            archview.run_check(self._two_module_model(), [], [])
        self.assertIn("no co-change history", captured.getvalue())

    def test_history_section_truncates_to_the_top_slices(self):
        def hotspot(index):
            return {"name": "Hub%02d" % index, "module": "Alpha", "file": "Alpha/A.cs",
                    "fileCommits": 1, "fanIn": 1, "hotspot": 1}

        def module_pair(index):
            return {"a": "Alpha", "b": "M%02d" % index, "both": 1, "either": 2, "jaccard": 0.5}

        def file_pair(index):
            return {"a": "Alpha/A%02d.cs" % index, "b": "Beta/B.cs", "moduleA": "Alpha",
                    "moduleB": "Beta", "count": 9}

        history = dict(self._small_history())
        history["hotspots"] = [hotspot(index) for index in range(20)]
        history["modulePairs"] = [module_pair(index) for index in range(12)]
        history["filePairs"] = [file_pair(index) for index in range(20)]
        captured = io.StringIO()
        with contextlib.redirect_stdout(captured):
            archview.run_check(self._two_module_model(), [], [], None, history)
        text = captured.getvalue()
        self.assertIn("Hub14", text)
        self.assertNotIn("Hub15", text)
        self.assertIn("M09", text)
        self.assertNotIn("M10", text)
        self.assertIn("Alpha/A14.cs", text)
        self.assertNotIn("Alpha/A15.cs", text)

    def test_non_string_policy_specs_are_warned_not_raised(self):
        captured = io.StringIO()
        with contextlib.redirect_stdout(captured):
            archview.run_check(_small_model(), [5], [5])
        text = captured.getvalue()
        self.assertIn("WARN bad [forbidden] edge spec", text)
        self.assertIn("WARN bad [allowed] edge spec", text)
        self.assertEqual(self._last_line(text), "ARCH-CHECK report-only")

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
        since = archview.default_since()
        parsed = archview.parse_history(archview.git_log_lines(archview.REPO_ROOT, since))
        cls.history = {
            "since": since,
            "commits": len(parsed["commits"]),
            "skippedSweeps": parsed["skipped"],
            "largestSweep": parsed["largest"],
        }
        cls.history.update(archview.history_metrics(parsed["commits"], cls.model, rules))
        cls.has_history = cls.history["commits"] > 0
        growth = archview.parse_growth(archview.git_numstat_lines(archview.REPO_ROOT, since))
        cls.sizes = archview.size_report(
            cls.model, cls.history, growth, archview.load_size_settings(archview.DEFAULT_MODULES)
        )
        cls.size_types = {row["name"]: row for row in cls.sizes["types"]}

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
        # The intent, not a snapshot: the kernel is ONE large cycle that runs
        # through most of the map. A refactor that shrinks it by a few dozen
        # types must not red this cell; only one that breaks the kernel apart
        # should, and then this floor is the thing to celebrate and rewrite.
        # The exact sizes are pinned by test_knot_sizes_match_the_documented_tree.
        largest = self.model["knots"][0]
        self.assertGreaterEqual(largest["size"], 200)
        self.assertGreaterEqual(len(largest["modules"]), 10)

    def test_knot_sizes_match_the_documented_tree(self):
        # A snapshot of the current tree, re-derived 2026-09-22 (was
        # [391, 4, 2] before the 2026-09-14 arch PRs shrank the kernel).
        # It is a canary: when it reds, re-derive it and the README numbers
        # from a fresh run in the same commit as whatever moved them.
        self.assertEqual([knot["size"] for knot in self.model["knots"]], [285, 4, 2, 2])

    def test_phase_two_catch_all_count(self):
        # Phase 2's revised placement policy (R1 name families first, then
        # externalRefs >= 5 AND (share >= 0.5 OR top >= 2 * second) on the
        # rebuilt evidence) moved 48 files and left 75; the R0 operator rules
        # then placed 67 of those by hand, leaving the kernel files that are
        # used across many modules (see README, "Editing modules.toml").
        # VesselSnapshotOps.cs joined them in the 2026-09-14 VesselSpawner
        # split, which named it in the Core rule, so the kernel is 9 files.
        self.assertEqual(self.by_name["Core"]["files"], 9)

    def test_kernel_files_resolve_to_core(self):
        kernel = [
            "BranchPoint.cs",
            "IPlaybackTrajectory.cs",
            "VesselLaunchIdentity.cs",
            "VesselSpawner.cs",
            "VesselSnapshotOps.cs",
            "MilestoneStore.cs",
            "GroupHierarchyStore.cs",
            "InventoryManifest.cs",
            "PlaybackTrajectoryBoundsResolver.cs",
        ]
        for name in kernel:
            self.assertEqual(self.model["fileModules"][name], "Core")
        self.assertEqual(
            sorted(f for f, m in self.model["fileModules"].items() if m == "Core"),
            sorted(kernel),
        )

    def test_phase_two_placements(self):
        modules = self.model["fileModules"]
        self.assertEqual(modules["RouteProofMetadata.cs"], "Logistics")
        self.assertEqual(modules["GuiTreeFunnels.cs"], "GuiTree")
        self.assertEqual(modules["GameStateEvent.cs"], "GameActions")
        self.assertEqual(modules["ResourceManifest.cs"], "Logistics")
        self.assertEqual(modules["RecoveryPayoutContext.cs"], "GameActions")

    def test_committed_placement_report_matches_a_fresh_render(self):
        rules, tooling, _forbidden, _allowed = archview.load_rules(archview.DEFAULT_MODULES)
        before_rules = archview.historical_catch_all_rules(
            [rule for rule in rules if not rule.get("placement")]
        )
        after_r1_rules = archview.historical_catch_all_rules(
            [
                rule
                for rule in rules
                if not rule.get("placement") or rule.get("placement") == "R1"
            ]
        )
        before_model = archview.build_model(
            REAL_SOURCE, before_rules, tooling, measure_sizes=False
        )
        after_r1_model = archview.build_model(
            REAL_SOURCE, after_r1_rules, tooling, measure_sizes=False
        )
        fresh = archview.render_placement_report(
            before_model, after_r1_model, self.model, archview.default_placement_rules()
        )
        again = archview.render_placement_report(
            before_model, after_r1_model, self.model, archview.default_placement_rules()
        )
        # The report is generated, gitignored output; pin determinism and the
        # facts a reader relies on rather than a committed copy.
        self.assertEqual(fresh, again)
        self.assertIn("| Core |", fresh)
        # 126: the historical before-state collects every root file the narrowed
        # family rules leave behind; 9 after, the kernel list as it stands today.
        self.assertIn("| Core | 126 | 9 |", fresh)

    def test_atlas_prose_matches_the_tree(self):
        prose = archview.load_prose(archview.DEFAULT_ATLAS)
        findings = archview.prose_findings(self.model, prose)
        self.assertEqual(findings["missingSummaries"], [])
        self.assertEqual(findings["missingGlossary"], [])
        self.assertEqual(findings["staleReadings"], [])
        self.assertEqual(findings["missingReadingOrder"], [])

    def test_rendered_atlas_contains_the_live_facts(self):
        prose = archview.load_prose(archview.DEFAULT_ATLAS)
        text = archview.render_atlas_html(self.model, prose, None, [])
        self.assertIn("ParsekLog", text)
        self.assertIn("%d types," % self.model["knots"][0]["size"], text)
        self.assertNotIn("(no summary yet)", text)
        self.assertNotIn("(no meaning yet)", text)

    def test_rendering_twice_is_identical_apart_from_the_date(self):
        prose = archview.load_prose(archview.DEFAULT_ATLAS)
        first = archview.render_atlas_html(self.model, prose, None, [])
        second = archview.render_atlas_html(self.model, prose, None, [])
        pattern = r"Source snapshot, \d{4}-\d{2}-\d{2}, branch \S+"

        def normalized(page):
            return re.sub(pattern, "SNAPSHOT", page)

        self.assertEqual(normalized(first), normalized(second))

    def test_production_module_count(self):
        production = [module for module in self.model["modules"] if not module["tooling"]]
        self.assertEqual(len(production), 20)

    def test_first_cut_matches_the_documented_tree(self):
        # Re-derived 2026-09-22: the 2026-09-14 arch PRs made ParsekLog a leaf,
        # so the first cut the greedy walk picks is now RecordingStore. Same
        # canary contract as the knot sizes above.
        first = self.model["knots"][0]["cuts"][0]
        self.assertEqual(first["sink"], "RecordingStore")
        self.assertEqual(first["sizeBefore"], 285)
        self.assertEqual(first["sizeAfter"], 258)
        self.assertIn("EffectiveState", first["droppedReferences"])
        self.assertEqual(len(first["droppedReferences"]), 25)

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

    def test_history_window_has_commits(self):
        if not self.has_history:
            self.skipTest("no git history available in this checkout")
        self.assertGreater(self.history["commits"], 100)

    def test_history_hotspots_exist_in_the_model(self):
        if not self.has_history:
            self.skipTest("no git history available in this checkout")
        self.assertTrue(any(row["fileCommits"] > 0 for row in self.history["hotspots"]))
        for row in self.history["hotspots"]:
            self.assertIn(row["name"], self.types)

    def test_top_hotspot_fan_in_is_at_least_thirty(self):
        if not self.has_history:
            self.skipTest("no git history available in this checkout")
        self.assertGreaterEqual(self.history["hotspots"][0]["fanIn"], 30)

    def test_module_pairs_are_symmetric_free(self):
        if not self.has_history:
            self.skipTest("no git history available in this checkout")
        self.assertEqual(len(self.history["modulePairs"]), 190)
        self.assertTrue(self.history["filePairs"])
        seen = set()
        for row in self.history["modulePairs"]:
            self.assertNotIn((row["a"], row["b"]), seen)
            self.assertNotIn((row["b"], row["a"]), seen)
            seen.add((row["a"], row["b"]))

    def test_history_caps(self):
        if not self.has_history:
            self.skipTest("no git history available in this checkout")
        self.assertEqual(len(self.history["hotspots"]), 30)
        self.assertEqual(len(self.history["filePairs"]), 40)

    def test_the_legacy_giants_lead_the_size_view(self):
        top = [row["name"] for row in self.sizes["types"][:10]]
        for name in ["ParsekFlight", "GhostMapPresence", "FlightRecorder"]:
            self.assertIn(name, top)

    def test_ghostmappresence_gets_a_split_and_a_runtime_note(self):
        row = self.size_types["GhostMapPresence"]
        rules = {item["rule"] for item in row["recommendations"]}
        # S3 (partial-class split) or S4 (static state map first), plus the
        # runtime-coupled note: this type is the case the view exists for.
        self.assertTrue(rules & {"S3", "S4"}, rules)
        self.assertIn("S6", rules)
        self.assertEqual(row["tier"], "Tier 1")
        self.assertGreater(row["lines"], archview.GIANT_TYPE_LINES)
        self.assertGreater(len(row["files"]), 1)

    def test_size_lines_do_not_exceed_the_files_they_are_measured_in(self):
        file_lines = {row["file"]: row["lines"] for row in self.sizes["files"]}
        for row in self.sizes["types"][:25]:
            for part in row["files"]:
                self.assertLessEqual(part["lines"], file_lines[part["file"]], part["file"])

    def test_the_largest_partials_are_in_the_top_hotspots(self):
        if not self.has_history:
            self.skipTest("no git history available in this checkout")
        top = [row["name"] for row in self.history["hotspots"][:10]]
        # Both are partial classes whose main file carries the churn; a
        # first-seen file attribution dropped them out of this table.
        self.assertIn("ParsekFlight", top)
        self.assertIn("GhostMapPresence", top)

    def test_growth_is_available_on_a_real_checkout(self):
        if not self.has_history:
            self.skipTest("no git history available in this checkout")
        self.assertTrue(self.sizes["growth"]["available"])
        self.assertGreater(self.size_types["ParsekFlight"]["netLinesAdded"], 0)

    def test_almost_every_member_is_delimited(self):
        skipped = sum(row["skippedMembers"] for row in self.sizes["types"])
        methods = sum(row["methods"] for row in self.sizes["types"])
        self.assertGreater(methods, 5000)
        self.assertLess(skipped, methods / 100)

    def test_no_member_is_named_after_a_modifier(self):
        # `internal static (bool ok, int n) Foo(` misparsed as name `static`
        # used to reach the output as a 110-line method called "static".
        for row in self.sizes["types"]:
            for method in row["topMethods"]:
                self.assertNotIn(method["name"], archview.MEMBER_MODIFIERS, row["name"])

    def test_the_purity_pool_discriminates(self):
        # It fired on 25 of the top 25 before the estimate was tightened,
        # which told a reader nothing.
        top = self.sizes["types"][:archview.SIZE_TOP_N]
        firing = [
            row["name"]
            for row in top
            if any(item["rule"] == "S2" for item in row["recommendations"])
        ]
        self.assertLess(len(firing), len(top) / 2)
        self.assertTrue(firing)

    def test_expression_bodied_properties_are_not_counted_as_static_state(self):
        # RecordingStore forwards about twenty of them; each used to read as a
        # reassignable static.
        row = self.size_types["RecordingStore"]
        self.assertLess(row["mutableStatics"], 45)
        self.assertGreater(row["mutableStatics"], 20)

    def test_nested_types_that_share_a_name_are_separate_rows(self):
        names = [row["name"] for row in self.sizes["types"]]
        self.assertEqual(len(names), len(set(names)))
        self.assertTrue(any("." in name for name in names))

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
