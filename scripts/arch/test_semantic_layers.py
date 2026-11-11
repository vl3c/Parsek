"""Tests for the declared layers (scripts/arch/layers.py): the declaration in
modules.toml, the backward / lateral split, the per-pair ratchet pinned in
layer_pins.toml and the forbidden-edge gates.

The file name matches CI's compiled-IL step pattern (`test_[ims]*.py`), so the
real-tree cells run there against the built Debug DLL; the pure cells and the
declaration cells run in the DLL-free suite as well. Without PARSEK_ARCH_IL
the real-tree cells skip with that reason.
"""

import contextlib
import fnmatch
import io
import os
import pathlib
import re
import sys
import tempfile
import tomllib
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import archview  # noqa: E402
import ilgraph  # noqa: E402
import layers  # noqa: E402
import test_semantic  # noqa: E402
from test_semantic import _class, _method  # noqa: E402

IL_ENV = test_semantic.IL_ENV

LAYERS = {
    "order": ["floor", "model", "top"],
    "modules": {"floor": ["Log"], "model": ["Store", "Ledger"], "top": ["Ui"]},
}
PRODUCTION = {"Log", "Store", "Ledger", "Ui"}


def pins_for(backward):
    """A loaded pins file for `backward`, through the real renderer."""
    return tomllib.loads(layers.render_pins(backward, LAYERS, "Parsek.dll", "2026-11-11"))


class DeclarationTests(unittest.TestCase):
    def test_a_complete_declaration_has_no_problem(self):
        self.assertEqual(layers.layer_problems(LAYERS, PRODUCTION, {"Tests"}), [])

    def test_a_production_module_without_a_layer_reds(self):
        problems = layers.layer_problems(LAYERS, PRODUCTION | {"NewThing"}, set())
        self.assertEqual(len(problems), 1)
        self.assertIn("NewThing has no layer", problems[0])
        self.assertIn("[layers.modules]", problems[0])

    def test_every_malformed_declaration_reds(self):
        cases = {
            "tooling placed": ({"order": ["a"], "modules": {"a": ["Log", "Store", "Ledger", "Ui", "Tests"]}},
                               "tooling module"),
            "two layers": ({"order": ["a", "b"], "modules": {"a": ["Log", "Store", "Ledger", "Ui"], "b": ["Ui"]}},
                           "two layers"),
            "unknown layer": ({"order": ["a"], "modules": {"a": ["Log", "Store", "Ledger", "Ui"], "z": ["Ui"]}},
                              "not a layer"),
            "unknown module": ({"order": ["a"], "modules": {"a": ["Log", "Store", "Ledger", "Ui", "Ghosty"]}},
                               "no rule"),
            "empty layer": ({"order": ["a", "b"], "modules": {"a": ["Log", "Store", "Ledger", "Ui"]}},
                            "layer b has no module"),
            "no order": ({"modules": {"a": ["Log"]}}, "order must be"),
            "no table": ({"order": ["a"]}, "[layers.modules] is missing"),
            "repeated layer": ({"order": ["a", "a"], "modules": {"a": ["Log", "Store", "Ledger", "Ui"]}},
                               "twice"),
        }
        for label, (declaration, needle) in cases.items():
            with self.subTest(case=label):
                problems = layers.layer_problems(declaration, PRODUCTION, {"Tests"})
                self.assertTrue(any(needle in problem for problem in problems), problems)


class CommittedDeclarationTests(unittest.TestCase):
    """The declaration and the pins as committed (no DLL, no source scan)."""

    def test_every_production_module_has_one_layer(self):
        production, tooling = layers.declared_modules()
        self.assertEqual(layers.layer_problems(layers.load_layers(), production, tooling), [])

    def test_four_to_six_layers(self):
        self.assertTrue(4 <= len(layers.load_layers()["order"]) <= 6)

    def test_the_committed_pins_are_consistent(self):
        pins = layers.load_pins()
        edges = [entry["edge"] for entry in pins["backward"]]
        self.assertEqual(len(edges), len(set(edges)))
        placed = layers.layer_of(layers.load_layers())
        for entry in pins["backward"]:
            with self.subTest(edge=entry["edge"]):
                self.assertEqual(entry["count"], len(entry["pairs"]))
                self.assertEqual(len(set(entry["pairs"])), len(entry["pairs"]))
                frm, to = [part.strip() for part in entry["edge"].split("->")]
                self.assertIn(frm, placed)
                self.assertIn(to, placed)
                # A pin for a pair the declaration no longer calls backward is a
                # stale pin: re-pin after moving a module between layers.
                self.assertLess(placed[frm][0], placed[to][0])

    def test_the_forbidden_specs_name_real_modules(self):
        production, tooling = layers.declared_modules()
        _rules, _tooling, forbidden, _allowed = archview.load_rules(archview.DEFAULT_MODULES)
        self.assertTrue(forbidden)
        self.assertEqual(layers.forbidden_problems(forbidden, {}, production | tooling), [])


class MeasureTests(unittest.TestCase):
    MODULES = {"LogThing": "Log", "StoreThing": "Store", "LedgerThing": "Ledger", "UiThing": "Ui",
               "UiHelper": "Ui"}

    def test_pairs_classify_by_declared_layer(self):
        graph = {
            "StoreThing": ["LogThing", "LedgerThing", "UiThing", "UiHelper"],
            "LedgerThing": ["StoreThing"],
            "UiThing": ["StoreThing", "UiHelper"],
            "LogThing": [],
            "UiHelper": [],
            "Unplaced": ["UiThing"],
        }
        pairs = layers.module_type_pairs(graph, self.MODULES)
        self.assertNotIn(("Ui", "Ui"), pairs)
        classified = layers.classify(pairs, LAYERS)
        self.assertEqual(classified["backward"], {("Store", "Ui"): {("StoreThing", "UiThing"),
                                                                   ("StoreThing", "UiHelper")}})
        self.assertEqual(set(classified["lateral"]), {("Store", "Ledger"), ("Ledger", "Store")})
        self.assertEqual(set(classified["downward"]), {("Store", "Log"), ("Ui", "Store")})
        self.assertEqual(layers.summary(classified), {
            "backwardModulePairs": 1, "backwardTypePairs": 2,
            "lateralModulePairs": 2, "lateralTypePairs": 2})

    def test_more_call_sites_between_paired_types_change_nothing(self):
        # One call against three calls plus a field and a local of the same
        # type: the semantic graph holds the type pair once either way.
        one = _class("Parsek.StoreThing", _method("Go", ["call       void Parsek.UiThing::Show()", "ret"]))
        many = _class(
            "Parsek.StoreThing",
            "  .field private class Parsek.UiThing held",
            _method("Go", ["call       void Parsek.UiThing::Show()", "call       void Parsek.UiThing::Show()",
                           "ret"]),
            _method("Again", ["newobj     instance void Parsek.UiThing::.ctor()", "pop",
                              "call       void Parsek.UiThing::Hide()", "ret"]),
        )
        ui = _class("Parsek.UiThing", _method("Show", ["ret"]), _method("Hide", ["ret"]))
        names = {"StoreThing", "UiThing"}
        measured = []
        for il in (one + "\n" + ui, many + "\n" + ui):
            graph = ilgraph.type_graph_from_il(ilgraph.parse_il(il), names)
            measured.append(layers.classify(layers.module_type_pairs(graph, self.MODULES), LAYERS))
        self.assertEqual(measured[0], measured[1])
        self.assertEqual(layers.summary(measured[0])["backwardTypePairs"], 1)

    def test_moving_a_type_between_files_of_one_module_changes_nothing(self):
        # The text view: a type's module is its file's module, so a move inside
        # the folder leaves every pair. (The IL view keys types by name and
        # takes the module from the same text model, so the same holds there.)
        def build(files):
            with tempfile.TemporaryDirectory() as tmp:
                root = pathlib.Path(tmp)
                for rel, text in files.items():
                    path = root / rel
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_text(text, encoding="utf-8")
                rules = [{"name": "Store", "folder": "Store"}, {"name": "Ui", "folder": "Ui"}]
                return archview.build_model(root, rules, set(), measure_sizes=False)

        before = build({
            "Store/A.cs": "class StoreThing { UiThing u; } class StoreHelper { UiThing u; }",
            "Ui/U.cs": "class UiThing { }",
        })
        after = build({
            "Store/A.cs": "class StoreThing { UiThing u; }",
            "Store/Moved.cs": "class StoreHelper { UiThing u; }",
            "Ui/U.cs": "class UiThing { }",
        })
        self.assertEqual(layers.text_pairs(before), layers.text_pairs(after))
        self.assertEqual(len(layers.text_pairs(after)[("Store", "Ui")]), 2)

    def test_the_report_names_layers_and_pairs(self):
        classified = layers.classify({("Store", "Ui"): {("StoreThing", "UiThing")},
                                      ("Store", "Ledger"): {("StoreThing", "LedgerThing")}}, LAYERS)
        text = "\n".join(layers.format_report(classified, LAYERS, "TITLE:", {("Store", "Ui"): 7}))
        self.assertIn("layer 1 model         Ledger, Store", text)
        self.assertIn("Store -> Ui (model -> top): type pairs=1 weight=7; StoreThing -> UiThing", text)
        self.assertIn("Store -> Ledger (model): type pairs=1", text)


class RatchetTests(unittest.TestCase):
    PINNED = {
        ("Store", "Ui"): {("StoreThing", "UiThing"), ("StoreThing", "UiHelper")},
        ("Log", "Store"): {("LogThing", "StoreThing")},
    }

    def measured(self, **changes):
        out = {key: set(value) for key, value in self.PINNED.items()}
        for key, value in changes.items():
            frm, to = key.split("_")
            if value is None:
                out.pop((frm, to), None)
            else:
                out[(frm, to)] = set(value)
        return out

    def test_the_pinned_tree_holds_and_the_pins_read_back(self):
        pins = pins_for(self.PINNED)
        self.assertEqual(layers.ratchet_problems(self.measured(), pins, LAYERS), [])
        self.assertEqual(layers.pinned(pins)["Store -> Ui"]["count"], 2)
        self.assertEqual(layers.pinned(pins)["Store -> Ui"]["layers"], "model -> top")
        self.assertEqual(layers.ratchet_slack(self.measured(), pins), [])

    def test_a_new_backward_type_pair_between_pinned_modules_reds(self):
        pins = pins_for(self.PINNED)
        grown = self.measured(Store_Ui=self.PINNED[("Store", "Ui")] | {("StoreOther", "UiThing")})
        problems = layers.ratchet_problems(grown, pins, LAYERS)
        self.assertEqual(len(problems), 1)
        self.assertIn("Store -> Ui (model below top): 3 type pairs, pinned 2 (+1)", problems[0])
        self.assertIn("New since the pin: StoreOther -> UiThing.", problems[0])
        self.assertIn("--write-pins", problems[0])

    def test_a_new_backward_module_pair_reds(self):
        pins = pins_for(self.PINNED)
        problems = layers.ratchet_problems(self.measured(Ledger_Ui={("LedgerThing", "UiThing")}), pins, LAYERS)
        self.assertEqual(len(problems), 1)
        self.assertIn("new backward module pair Ledger -> Ui (model below top): 1 type pairs:"
                      " LedgerThing -> UiThing", problems[0])
        self.assertIn("--write-pins", problems[0])

    def test_what_is_not_a_new_backward_dependency_passes(self):
        pins = pins_for(self.PINNED)
        cases = {
            # A rename of either type keeps the count; only the message's
            # new / gone lists would show it.
            "rename": self.measured(Store_Ui={("StoreThing", "UiThingRenamed"), ("StoreThing", "UiHelper")}),
            "swap inside the count": self.measured(Store_Ui={("StoreThing", "UiThing"), ("StoreNew", "UiThing")}),
            "deleted type pair": self.measured(Store_Ui={("StoreThing", "UiThing")}),
            "deleted module pair": self.measured(Log_Store=None),
            "everything deleted": {},
        }
        for label, measured in cases.items():
            with self.subTest(case=label):
                self.assertEqual(layers.ratchet_problems(measured, pins, LAYERS), [])

    def test_slack_after_a_fall_is_reported_not_enforced(self):
        pins = pins_for(self.PINNED)
        fallen = self.measured(Store_Ui={("StoreThing", "UiThing")}, Log_Store=None)
        self.assertEqual(layers.ratchet_slack(fallen, pins),
                         ["Log -> Store: 0 below the pin 1", "Store -> Ui: 1 below the pin 2"])
        # Until a re-pin, the old count still admits a new pair.
        refilled = self.measured(Store_Ui={("StoreThing", "UiThing"), ("StoreNew", "UiThing")})
        self.assertEqual(layers.ratchet_problems(refilled, pins, LAYERS), [])

    def test_the_cli_refuses_a_release_pin(self):
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            layers.main(["Source/Parsek/bin/Release/Parsek.dll", "--write-pins"])


class ForbiddenTests(unittest.TestCase):
    MODULES = {"Ghost", "GameActions", "Missions", "Logistics"}

    def test_a_crossed_forbidden_edge_reds_on_the_semantic_graph(self):
        il = "\n".join([
            _class("Parsek.GhostThing", _method("Go", ["call       void Parsek.LedgerThing::Book()", "ret"])),
            _class("Parsek.LedgerThing", _method("Book", ["ret"])),
        ])
        graph = ilgraph.type_graph_from_il(ilgraph.parse_il(il), {"GhostThing", "LedgerThing"})
        pairs = layers.module_type_pairs(graph, {"GhostThing": "Ghost", "LedgerThing": "GameActions"})
        problems = layers.forbidden_problems(["Missions -> Logistics", "Ghost -> GameActions"], pairs,
                                             self.MODULES)
        self.assertEqual(len(problems), 1)
        self.assertIn("forbidden edge Ghost -> GameActions is crossed (1): GhostThing -> LedgerThing",
                      problems[0])
        self.assertEqual(layers.forbidden_problems(["Missions -> Logistics"], pairs, self.MODULES), [])

    def test_a_crossed_forbidden_edge_reds_on_the_text_scan(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            for rel, text in {
                "Missions/M.cs": "class MissionThing { RouteThing r; }",
                "Logistics/R.cs": "class RouteThing { }",
            }.items():
                (root / rel).parent.mkdir(parents=True, exist_ok=True)
                (root / rel).write_text(text, encoding="utf-8")
            rules = [{"name": "Missions", "folder": "Missions"}, {"name": "Logistics", "folder": "Logistics"}]
            model = archview.build_model(root, rules, set(), measure_sizes=False)
        problems = layers.forbidden_problems(["Missions -> Logistics"], layers.text_module_edges(model),
                                             self.MODULES)
        self.assertEqual(len(problems), 1)
        self.assertIn("Missions -> Logistics is crossed (1): RouteThing", problems[0])

    def test_a_spec_that_cannot_gate_reds(self):
        for spec, needle in (("Missions => Logistics", "is not"), ("Mission -> Logistics", "names no module"),
                             (None, "is not")):
            with self.subTest(spec=spec):
                problems = layers.forbidden_problems([spec], {}, self.MODULES)
                self.assertEqual(len(problems), 1)
                self.assertIn(needle, problems[0])


class WorkflowTests(unittest.TestCase):
    def test_the_compiled_il_step_runs_these_cells(self):
        steps = test_semantic.workflow_steps(test_semantic.WORKFLOW.read_text(encoding="utf-8"))
        runs = [run for _name, _keys, env, run in steps if IL_ENV in env and "scripts/arch" in run]
        patterns = [m.group(1) for run in runs for m in [re.search(r"-p\s+['\"]?([^'\"\s]+)", run)] if m]
        self.assertTrue(any(fnmatch.fnmatch("test_semantic_layers.py", p) for p in patterns), patterns)


@unittest.skipUnless(os.environ.get(IL_ENV), "%s is not set (CI sets it after the build)" % IL_ENV)
class RealTreeLayerTests(unittest.TestCase):
    """The ratchet and the forbidden gate over the built Debug Parsek.dll, from
    the semantic graph test_semantic's real-tree cells already hold."""

    @classmethod
    def setUpClass(cls):
        il_path = Path(os.environ[IL_ENV])
        if not il_path.is_file():
            raise AssertionError("%s=%s does not exist (build Parsek first)" % (IL_ENV, il_path))
        cls.model, result = test_semantic.real_tree_semantic(il_path)
        cls.graph = result["semantic"]
        cls.type_modules = result["typeModules"]
        cls.layers = layers.load_layers()
        cls.pins = layers.load_pins()
        cls.production, cls.tooling = layers.declared_modules()
        _rules, _tooling, cls.forbidden, _allowed = archview.load_rules(archview.DEFAULT_MODULES)
        cls.pairs = layers.module_type_pairs(cls.graph, cls.type_modules)
        cls.classified = layers.classify(cls.pairs, cls.layers)
        report = io.StringIO()
        with contextlib.redirect_stdout(report):
            head = layers.summary(cls.classified)
            print("DECLARED LAYERS (compiled IL): %d backward module pairs (%d type pairs),"
                  " %d lateral module pairs" % (head["backwardModulePairs"], head["backwardTypePairs"],
                                                head["lateralModulePairs"]))
            for line in layers.ratchet_slack(cls.classified["backward"], cls.pins):
                print("  slack (re-pin to tighten): " + line)
        sys.stderr.write("\n" + report.getvalue())

    def problems(self, graph):
        pairs = layers.module_type_pairs(graph, self.type_modules)
        backward = layers.classify(pairs, self.layers)["backward"]
        return (layers.ratchet_problems(backward, self.pins, self.layers),
                layers.forbidden_problems(self.forbidden, pairs, self.production | self.tooling))

    def mutant(self, frm, to):
        graph = {name: list(targets) for name, targets in self.graph.items()}
        graph[frm] = sorted(set(graph[frm]) | {to})
        return graph

    def first_type(self, module, exclude=()):
        return min(name for name, owner in self.type_modules.items() if owner == module and name not in exclude)

    def test_the_backward_pairs_hold_their_pins(self):
        self.assertEqual(self.problems(self.graph)[0], [])

    def test_no_forbidden_edge_is_crossed(self):
        self.assertEqual(self.problems(self.graph)[1], [])

    def test_the_layer_graph_is_the_semantic_graph(self):
        # layers.py's CLI builds the same graph semantic.build calls "semantic".
        self.assertEqual(layers.semantic_graph(self.model, _parsed()), self.graph)

    def test_each_gate_catches_a_mutant_of_the_real_graph(self):
        pinned = layers.pinned(self.pins)
        # A new type pair between a pinned backward module pair.
        first = min(pinned)
        frm_module, to_module = [part.strip() for part in first.split("->")]
        existing = {tuple(row.split(" -> ")) for row in pinned[first]["pairs"]}
        frm, to = next((a, b) for a in sorted(n for n, m in self.type_modules.items() if m == frm_module)
                       for b in sorted(n for n, m in self.type_modules.items() if m == to_module)
                       if (a, b) not in existing)
        ratchet, _forbidden = self.problems(self.mutant(frm, to))
        self.assertEqual(len(ratchet), 1)
        self.assertIn("New since the pin: %s -> %s." % (frm, to), ratchet[0])
        # A backward module pair that is not pinned: the floor's logger
        # reaching a scene controller.
        self.assertNotIn("LogIO -> Controllers", pinned)
        ratchet, _forbidden = self.problems(self.mutant("ParsekLog", "ParsekScenario"))
        self.assertEqual(len(ratchet), 1)
        self.assertIn("new backward module pair LogIO -> Controllers (floor below entry)", ratchet[0])
        # Each declared forbidden edge, crossed once.
        for spec in self.forbidden:
            with self.subTest(forbidden=spec):
                src, dst = [part.strip() for part in spec.split("->")]
                _ratchet, forbidden = self.problems(self.mutant(self.first_type(src), self.first_type(dst)))
                self.assertEqual(len(forbidden), 1)
                self.assertIn("forbidden edge %s is crossed" % spec, forbidden[0])

    def test_deleting_types_never_reds(self):
        # Every type of the module with the most backward pairs loses all its
        # references (and every reference to it), as a deletion would leave
        # the graph: counts can only fall.
        worst = max(self.classified["backward"], key=lambda key: len(self.classified["backward"][key]))[0]
        gone = {name for name, owner in self.type_modules.items() if owner == worst}
        graph = {name: [t for t in targets if t not in gone]
                 for name, targets in self.graph.items() if name not in gone}
        ratchet, forbidden = self.problems(graph)
        self.assertEqual((ratchet, forbidden), ([], []))


def _parsed():
    return ilgraph.load_parsed(Path(os.environ[IL_ENV]))


if __name__ == "__main__":
    unittest.main()
