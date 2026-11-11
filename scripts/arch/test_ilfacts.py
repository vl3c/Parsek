"""Tests for the per-member IL facts layer (ilfacts.py and the facts ilgraph.parse_il records).

The fixture cells read scripts/arch/fixtures/ilfacts_sample.il, blocks cut
verbatim from an ikdasm dump of the Debug Parsek.dll, because the cases that
broke the scratch prototype are all ikdasm formatting (a `switch` whose
targets sit on continuation lines, a quoted generated name that holds `<`).
The real-tree cells need PARSEK_ARCH_IL (a built Parsek.dll or an .il dump),
like test_semantic's; they assert structure only, never a count that moves
with ordinary code changes.
"""

import contextlib
import fnmatch
import io
import os
import re
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import archview  # noqa: E402
import ilfacts  # noqa: E402
import ilgraph  # noqa: E402
import test_semantic  # noqa: E402

FIXTURE = Path(__file__).resolve().parent / "fixtures" / "ilfacts_sample.il"
IL_ENV = test_semantic.IL_ENV


def fixture_parse():
    return ilgraph.parse_il(FIXTURE.read_text(encoding="utf-8"))


def il_method(parsed, type_path, name):
    found = [m for m in parsed["methods"] if m["type"] == type_path and m["name"] == name]
    if len(found) != 1:
        raise AssertionError("%s::%s found %d times" % (type_path, name, len(found)))
    return found[0]


class FixtureFactsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.parsed = fixture_parse()
        cls.facts = ilfacts.build(cls.parsed)

    def method(self, key):
        self.assertIn(key, self.facts["methods"], sorted(self.facts["methods"]))
        return self.facts["methods"][key]

    def test_a_multi_line_switch_counts_each_distinct_target(self):
        # `switch ( IL_001b, IL_0023, IL_002b)`: ikdasm prints each target on
        # its own continuation line, which the prototype dropped (1 arm).
        raw = il_method(self.parsed, "Parsek.HeaviestSpawnBuildTypeExtensions", "ToLogToken")
        self.assertEqual((raw["switchTargets"], raw["switchArms"], raw["branches"]), (3, 3, 0))
        entry = self.method("Parsek.HeaviestSpawnBuildTypeExtensions::ToLogToken(Parsek.HeaviestSpawnBuildType)")
        self.assertEqual(entry["decisions"], 3)

    def test_repeated_switch_targets_count_once(self):
        il = test_semantic._class(
            "Parsek.Sw",
            test_semantic._method("Pick", [
                "switch     ( ",
                "                          IL_0010,",
                "                          IL_0020,",
                "                          IL_0020)",
                "brtrue.s   IL_0010",
                "ret",
            ]),
        )
        raw = il_method(ilgraph.parse_il(il), "Parsek.Sw", "Pick")
        self.assertEqual((raw["switchTargets"], raw["switchArms"], raw["branches"]), (2, 3, 1))

    def test_a_quoted_generated_name_keeps_its_angle_brackets(self):
        self.assertEqual(
            ilgraph.split_declaring("Parsek.EngineFxBuilder/'<>c'"),
            (None, "Parsek.EngineFxBuilder/<>c", ""),
        )
        self.assertEqual(
            ilgraph.split_declaring("class Parsek.GreyLedger`1<!TKey>"), (None, "Parsek.GreyLedger", "!TKey")
        )
        self.assertEqual(
            ilgraph.split_declaring("['Assembly-CSharp']Part"), ("Assembly-CSharp", "Part", "")
        )
        raw = il_method(self.parsed, "Parsek.CommittedActionDialog", "ShowBlocked")
        lambdas = [ref for ref in raw["refs"] if ref[0] == "ldftn"]
        self.assertEqual([(r[2], r[3]) for r in lambdas],
                         [("Parsek.CommittedActionDialog/<>c", "<ShowBlocked>b__1_0")])

    def test_lambdas_and_closures_fold_into_the_method_that_wrote_them(self):
        # The `<>c` lambda, reached by ldftn; the `<>c` cache's constructors
        # follow it because this method is the cache's only user.
        dialog = self.method("Parsek.CommittedActionDialog::ShowBlocked(string,string,string)")
        self.assertEqual(dialog["generated"]["methods"], 3)
        # A closure: newobj of the display class and ldftn of its lambda.
        prefix = self.method("Parsek.Patches.GhostOrbitDominantBodyPatch::Prefix(OrbitDriver,bool&)")
        self.assertEqual(prefix["generated"]["methods"], 2)
        for entry in (dialog, prefix):
            self.assertFalse([c for c in entry["calls"] if "<" in c], entry["calls"])

    def test_an_iterator_body_folds_into_the_method_that_constructs_it(self):
        # Nothing calls MoveNext by name: the state machine is reached through
        # its constructor, and every member of `<Emit>d__5` comes with it.
        emit = self.method("Parsek.MapRender.TracedPhase::Emit(Parsek.MapRender.SampleContext)")
        machine = [m for m in self.parsed["methods"] if m["type"] == "Parsek.MapRender.TracedPhase/<Emit>d__5"]
        self.assertEqual(emit["generated"]["methods"], len(machine))
        self.assertEqual(emit["generated"]["instructions"], sum(len(m["ops"]) for m in machine))
        self.assertIn("Parsek.MapRender.TracedPhase::ResolveFrameBodyName(Parsek.MapRender.SampleContext)",
                      emit["calls"])  # called from MoveNext
        self.assertIn("mscorlib|System.Runtime.CompilerServices.IteratorStateMachineAttribute",
                      emit["attributes"])

    def test_no_output_key_names_generated_code(self):
        self.assertEqual(self.facts["counts"]["generatedBy:reference"], self.facts["counts"]["generatedMethods"])
        for key in list(self.facts["methods"]) + list(self.facts["types"]):
            self.assertNotRegex(key, r"<|d__\d|b__\d|DisplayClass", key)
        for entry in self.facts["methods"].values():
            for callee in entry["calls"]:
                self.assertNotRegex(callee, r"d__\d|b__\d|DisplayClass", callee)

    def test_same_short_name_in_two_namespaces_stays_two_types(self):
        hover = self.facts["types"]["Parsek.TimelineRowHoverTracker/Memo"]
        launch = self.facts["types"]["Parsek.Patches.ClaimedVesselLaunchSite/Memo"]
        self.assertEqual((hover["kind"], launch["kind"]), ("struct", "class"))
        self.assertIn("Parsek.TimelineRowHoverTracker/Memo::BuiltFuture", self.facts["fields"])
        self.assertIn("Parsek.Patches.ClaimedVesselLaunchSite/Memo::StockProceedText", self.facts["fields"])
        self.assertIn("Parsek.Patches.ClaimedVesselLaunchSite/Memo::.ctor()", self.facts["methods"])

    def test_a_static_auto_property_backing_field_is_tagged(self):
        field = self.facts["fields"]["Parsek.DisplayNames::<Version>k__BackingField"]
        self.assertEqual(
            (field["static"], field["initonly"], field["literal"], field["compilerGenerated"], field["backingFor"]),
            (True, False, False, True, "Version"),
        )
        self.assertEqual(field["writers"], ["Parsek.DisplayNames::set_Version(int32)"])
        self.assertEqual(field["readers"], ["Parsek.DisplayNames::get_Version()"])
        self.assertIn("Parsek.DisplayNames::set_Version(int32)",
                      self.method("Parsek.DisplayNames::NoteChanged(string,string)")["calls"])

    def test_an_address_taken_static_is_recorded(self):
        # A field-like event's add / remove pass the backing field by ref to
        # Interlocked.CompareExchange: ldsflda, a write the stsfld scan misses.
        field = self.facts["fields"]["Parsek.DisplayNames::DisplayNamesChanged"]
        self.assertTrue(field["event"])
        self.assertEqual(field["addressTakenBy"], [
            "Parsek.DisplayNames::add_DisplayNamesChanged(System.Action)",
            "Parsek.DisplayNames::remove_DisplayNamesChanged(System.Action)",
        ])
        self.assertEqual(field["writers"], ["Parsek.DisplayNames::ResetSubscribersForTesting()"])
        self.assertEqual(self.method("Parsek.DisplayNames::add_DisplayNamesChanged(System.Action)")["staticAddress"],
                         ["Parsek.DisplayNames::DisplayNamesChanged"])

    def test_attributes_on_types_and_methods_are_captured(self):
        self.assertEqual(self.facts["types"]["Parsek.RevertToLaunchInterceptor"]["attributes"],
                         ["0Harmony|HarmonyLib.HarmonyPatch"])
        self.assertEqual(self.method("Parsek.RevertToLaunchInterceptor::Prefix()")["attributes"],
                         ["0Harmony|HarmonyLib.HarmonyPrefix"])
        # A field's attributes stay on the field, not on the type.
        self.assertEqual(self.facts["types"]["Parsek.DisplayNames"]["attributes"], [])
        self.assertTrue(self.method("Parsek.DisplayNames::get_Version()")["compilerGenerated"])
        self.assertFalse(self.method("Parsek.DisplayNames::NoteChanged(string,string)")["compilerGenerated"])

    def test_overloads_of_one_arity_are_two_keys_and_calls_resolve_exactly(self):
        by_string = self.method("Parsek.Logistics.RouteIds::Short(string)")
        by_route = self.method("Parsek.Logistics.RouteIds::Short(Parsek.Logistics.Route)")
        self.assertEqual(by_route["calls"], ["Parsek.Logistics.RouteIds::Short(string)"])
        self.assertEqual(by_string["calls"], [])

    def test_external_types_are_split_by_scope(self):
        prefix = self.method("Parsek.Patches.GhostOrbitDominantBodyPatch::Prefix(OrbitDriver,bool&)")
        self.assertIn("Assembly-CSharp|OrbitDriver", prefix["external"]["signature"])
        self.assertFalse([t for scope in prefix["external"].values() for t in scope if t.startswith("mscorlib|")])

    def test_a_call_outside_the_dump_is_unresolved_not_dropped(self):
        prefix = self.method("Parsek.RevertToLaunchInterceptor::Prefix()")
        self.assertEqual(prefix["unresolvedCalls"],
                         ["Parsek.RevertInterceptor::Prefix(Parsek.RevertTarget,EditorFacility)"])

    def test_the_summary_counts_production_by_the_module_map(self):
        table = {path: {"Core"} for path in self.facts["types"]}
        table["Parsek.DisplayNames"] = {"InGameTests"}
        facts = ilfacts.build(self.parsed, table)
        numbers = ilfacts.summary(facts, tooling={"InGameTests"})
        # TestHookForTesting is the one production reassignable static; the
        # DisplayNames ones are tooling here.
        self.assertEqual(numbers["reassignableStatics"], 1)
        numbers = ilfacts.summary(facts)
        self.assertEqual(numbers["reassignableStatics"], 3)
        self.assertEqual(numbers["reassignableStaticBackingFields"], 1)
        self.assertIn("reassignable statics: 3", ilfacts.format_summary(numbers))


class NormalizationTests(unittest.TestCase):
    def test_a_declaration_and_a_call_site_print_the_same_parameters(self):
        declared = ilgraph.normalize_params(
            "[mscorlib]System.Collections.Generic.List`1<!!T> list, [out] class Parsek.Foo/'<>c'& x,"
            " valuetype [mscorlib]System.Nullable`1<!TKey> n",
            type_generics=("TKey",), method_generics=("T",), named=True,
        )
        called = ilgraph.normalize_params(
            "[mscorlib]System.Collections.Generic.List`1<!!0>, class Parsek.Foo/'<>c'&,"
            " valuetype [mscorlib]System.Nullable`1<!0>"
        )
        self.assertEqual(declared, called)
        self.assertEqual(declared, (
            "System.Collections.Generic.List`1<!!0>", "Parsek.Foo/<>c&", "System.Nullable`1<!0>"))

    def test_generic_methods_and_p_invoke_headers_key_like_their_calls(self):
        il = test_semantic._class(
            "Parsek.Gen",
            "  .method private hidebysig static void  ReplaceContents<T>([mscorlib]System.Collections.Generic.List`1<!!T> list,\n"
            "                                                            [mscorlib]System.Collections.Generic.List`1<!!T> saved) cil managed\n"
            "  {\n    IL_0000:  ret\n  }",
            "  .method assembly hidebysig static pinvokeimpl(\"user32.dll\" winapi) \n"
            "            bool  marshal( bool)  GetCursorPos([out] valuetype Parsek.Gen/POINT& point) cil managed preservesig\n"
            "  {\n  }",
            test_semantic._method("Use", [
                "call       void Parsek.Gen::ReplaceContents<valuetype Parsek.TrajectoryPoint>([mscorlib]System.Collections.Generic.List`1<!!0>,",
                "                                                            [mscorlib]System.Collections.Generic.List`1<!!0>)",
                "call       bool Parsek.Gen::GetCursorPos(valuetype Parsek.Gen/POINT&)",
                "ret",
            ]),
        )
        facts = ilfacts.build(ilgraph.parse_il(il))
        self.assertEqual(facts["counts"]["unresolvedCalls"], 0)
        self.assertEqual(facts["methods"]["Parsek.Gen::Use()"]["calls"], [
            "Parsek.Gen::GetCursorPos(Parsek.Gen/POINT&)",
            "Parsek.Gen::ReplaceContents``1(System.Collections.Generic.List`1<!!0>,System.Collections.Generic.List`1<!!0>)",
        ])

    def test_the_type_graph_ignores_the_facts(self):
        # The switch-target continuation lines the facts now read must not
        # change what the type graph sees.
        il = test_semantic._class(
            "Parsek.Host",
            test_semantic._method("Run", [
                "switch     ( ",
                "                          IL_0010,",
                "                          IL_0020)",
                "call       void Parsek.Callee::Go()",
                "ret",
            ]),
        )
        parsed = ilgraph.parse_il(il)
        self.assertEqual(ilgraph.type_graph_from_il(parsed, {"Host", "Callee"}), {"Host": ["Callee"], "Callee": []})
        self.assertEqual([op for op, _use in il_method(parsed, "Parsek.Host", "Run")["ops"]], ["switch", "call", "ret"])


class ModuleMapTests(unittest.TestCase):
    SOURCE = """
namespace Parsek
{
    // class Decoy in a comment is not a declaration
    internal static class VesselIdentity { }
}
namespace Parsek.Logistics
{
    internal sealed class RouteEndpointTransfer
    {
        internal struct VesselIdentity { }
    }
    internal delegate void RouteChanged(string id);
}
"""

    def test_namespace_and_nesting_make_the_path(self):
        stripped = archview.strip_comments_and_strings(self.SOURCE)
        self.assertEqual(sorted(ilfacts.declared_type_paths(stripped)), [
            "Parsek.Logistics.RouteChanged",
            "Parsek.Logistics.RouteEndpointTransfer",
            "Parsek.Logistics.RouteEndpointTransfer/VesselIdentity",
            "Parsek.VesselIdentity",
        ])

    def test_a_type_takes_its_own_module_or_its_enclosing_types(self):
        table = {
            "Parsek.VesselIdentity": {"Missions"},
            "Parsek.Logistics.RouteEndpointTransfer": {"Logistics"},
            "Parsek.Split": {"Recording", "Controllers"},
        }
        self.assertEqual(ilfacts.module_for("Parsek.VesselIdentity", table), "Missions")
        self.assertEqual(ilfacts.module_for("Parsek.Logistics.RouteEndpointTransfer/VesselIdentity", table),
                         "Logistics")
        self.assertIsNone(ilfacts.module_for("Parsek.Split/Inner", table))
        self.assertIsNone(ilfacts.module_for("Parsek.Unknown", table))


class WorkflowTests(unittest.TestCase):
    def test_the_compiled_il_step_also_runs_these_cells(self):
        # The real-tree cells below skip without PARSEK_ARCH_IL; the step that
        # sets it must discover this file too.
        steps = test_semantic.workflow_steps(test_semantic.WORKFLOW.read_text(encoding="utf-8"))
        runs = [run for _name, _keys, env, run in steps if IL_ENV in env and "scripts/arch" in run]
        self.assertTrue(runs)
        patterns = [m.group(1) for run in runs for m in [re.search(r"-p\s+['\"]?([^'\"\s]+)", run)] if m]
        self.assertTrue(any(fnmatch.fnmatch("test_ilfacts.py", p) for p in patterns), patterns)


@unittest.skipUnless(os.environ.get(IL_ENV), "%s is not set (CI sets it after the build)" % IL_ENV)
class RealTreeFactsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        il_path = Path(os.environ[IL_ENV])
        if not il_path.is_file():
            raise AssertionError("%s=%s does not exist (build Parsek first)" % (IL_ENV, il_path))
        rules, cls.tooling, _forbidden, _allowed = archview.load_rules(archview.DEFAULT_MODULES)
        cls.parsed = ilgraph.load_parsed(il_path)
        cls.facts = ilfacts.build(cls.parsed, ilfacts.source_type_modules(test_semantic.REAL_SOURCE, rules))
        report = io.StringIO()
        with contextlib.redirect_stdout(report):
            print(ilfacts.format_summary(ilfacts.summary(cls.facts, cls.tooling)))
        sys.stderr.write("\n" + report.getvalue() + "\n")

    def test_every_source_type_is_a_facts_type(self):
        source = {
            path for path in self.parsed["types"]
            if ilfacts.is_root_path(ilfacts.outermost(path)) and not ilfacts.is_generated_path(path)
        }
        self.assertEqual(source, set(self.facts["types"]))
        # A type the source scan cannot place (a name archview skips, a partial
        # type split across two modules) keeps `module: null`. That is a fact
        # about the code, so it is reported with the summary, not asserted.
        unmapped = sorted(path for path, entry in self.facts["types"].items() if entry["module"] is None)
        sys.stderr.write("types with no module: %d %s\n" % (len(unmapped), unmapped[:20]))

    def test_types_sharing_a_short_name_stay_apart(self):
        identities = sorted(path for path in self.facts["types"] if path.split("/")[-1].split(".")[-1] == "VesselIdentity")
        self.assertGreaterEqual(len(identities), 2, identities)
        self.assertIn("Parsek.VesselIdentity", identities)

    def test_keys_are_unique_and_every_call_resolves(self):
        self.assertEqual(self.facts["keyCollisions"], [])
        unresolved = {k: m["unresolvedCalls"] for k, m in self.facts["methods"].items() if m["unresolvedCalls"]}
        self.assertEqual(unresolved, {})

    def test_generated_code_is_folded_and_never_keyed(self):
        counts = self.facts["counts"]
        self.assertGreater(counts["generatedBy:reference"], 0)
        self.assertEqual(counts.get("generatedBy:none", 0), 0)
        for key in self.facts["methods"]:
            self.assertNotRegex(key.split("(", 1)[0], r"d__\d|b__\d|DisplayClass|<>c", key)

    def test_entry_point_attributes_are_read(self):
        attributes = {a for m in self.facts["methods"].values() for a in m["attributes"]}
        type_attributes = {a for t in self.facts["types"].values() for a in t["attributes"]}
        self.assertIn("0Harmony|HarmonyLib.HarmonyPrefix", attributes)
        self.assertIn("Parsek.InGameTests.InGameTestAttribute", attributes)
        self.assertIn("0Harmony|HarmonyLib.HarmonyPatch", type_attributes)
        self.assertIn("Assembly-CSharp|KSPAddon", type_attributes)


if __name__ == "__main__":
    unittest.main()
