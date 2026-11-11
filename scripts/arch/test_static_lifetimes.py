"""Tests for the declared static lifetimes and their gates (lifetimes.py, clrmeta.py).

The declaration cells need no DLL: the committed lifetimes.toml is well-formed
and agrees with the C# reset gate (SaveScopedSessionResetTests' type list and
scripts/session-reset-allowlist.txt, comments stripped). The rule cells build
small ikdasm-shaped assemblies inline (test_ilanalysis' helpers) and show each
finding red and not red. The real-tree cells need PARSEK_ARCH_IL like
test_semantic's and the Debug Parsek.Tests.dll beside it; CI's compiled-IL
step runs them. They hold the pins and mutate the real result to show each
gate reds on the change it exists for and stays green on the ones it must not.
"""

import contextlib
import copy
import fnmatch
import io
import os
import re
import struct
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import clrmeta  # noqa: E402
import ilanalysis  # noqa: E402
import lifetimes  # noqa: E402
import test_ilfacts  # noqa: E402
import test_semantic  # noqa: E402
from test_ilanalysis import cls, facts_of, meth  # noqa: E402

IL_ENV = test_semantic.IL_ENV
CONFIG = ilanalysis.load_config()
LIST = "class [mscorlib]System.Collections.Generic.List`1<string>"
ALL_LIFETIMES = ("process", "save", "load-transition", "scene", "scoped", "test-hook")


def declaration(**overrides):
    decl = {
        "lifetimes": {name: "meaning of %s" % name for name in ALL_LIFETIMES},
        "cold_load": {"roots": {"Parsek.Scenario::OnLoad()": "the load"}},
        "pattern": [{"match": "*ForTesting", "lifetime": "test-hook", "reason": "named as a seam"}],
        "type": [
            {"name": "Parsek.Store", "default": "save", "reason": "the store",
             "fields": {"Version": "process: a change counter"}},
            {"name": "Parsek.Hooks", "default": "test-hook", "reason": "seams"},
        ],
    }
    decl.update(overrides)
    return decl


def fixture_il(boxed_default=True, branch_write=True):
    """Scenario::OnLoad reaches Store::Reset directly and Step::Run through an
    interface slot. Reset clears Saved (stsfld, behind a branch), Boxed
    (`ldsflda` + `initobj`, or only a read of its address) and Items (List.Clear);
    Step::Run writes ViaSlot. Missed is written only off the load path."""
    fields = "\n".join([
        "  .field public static int32 Saved",
        "  .field public static int32 Missed",
        "  .field public static int32 ViaSlot",
        "  .field public static int32 Version",
        "  .field public static valuetype Parsek.Bundle Boxed",
        "  .field public static initonly %s Items" % LIST,
    ])
    boxed = (["ldsflda    valuetype Parsek.Bundle Parsek.Store::Boxed", "initobj    Parsek.Bundle"]
             if boxed_default else
             ["ldsflda    valuetype Parsek.Bundle Parsek.Store::Boxed", "pop"])
    saved = (["ldarg.0", "brfalse.s  IL_0003", "ldc.i4.0", "stsfld     int32 Parsek.Store::Saved"]
             if branch_write else ["ldc.i4.0", "stsfld     int32 Parsek.Store::Saved"])
    store = cls(
        "Parsek.Store", fields,
        meth("Reset", saved + boxed + ["ldsfld     %s Parsek.Store::Items" % LIST,
                                       "callvirt   instance void %s::Clear()" % LIST, "ret"],
             params="bool flag"),
        meth("Arm", ["ldc.i4.1", "stsfld     int32 Parsek.Store::Missed", "ldc.i4.1",
                     "stsfld     int32 Parsek.Store::Version", "ret"]),
    )
    bundle = cls("Parsek.Bundle", "  .field public int32 X", extends="[mscorlib]System.ValueType",
                 flags="public sequential ansi sealed beforefieldinit")
    step_if = cls("Parsek.IStep", meth("Run", flags="public hidebysig newslot abstract virtual instance", body=()),
                  flags="interface public abstract auto ansi")
    step = cls("Parsek.Step",
               meth("Run", ["ldc.i4.2", "stsfld     int32 Parsek.Store::ViaSlot", "ret"],
                    flags="public hidebysig newslot virtual final instance"),
               implements=("Parsek.IStep",))
    scenario = cls(
        "Parsek.Scenario",
        meth("OnLoad", ["ldc.i4.1", "call       void Parsek.Store::Reset(bool)",
                        "ldnull", "callvirt   instance void Parsek.IStep::Run()", "ret"]),
    )
    hooks = cls(
        "Parsek.Hooks",
        "  .field public static int32 Hook\n  .field public static int32 Hook2\n  .field public static int32 Own",
        meth("Set", ["ldc.i4.1", "stsfld     int32 Parsek.Hooks::Own", "ret"]),
    )
    other = cls(
        "Parsek.Other",
        meth("Poke", ["ldc.i4.1", "stsfld     int32 Parsek.Hooks::Hook", "ret"]),
        meth("ResetForTesting", ["ldc.i4.0", "stsfld     int32 Parsek.Hooks::Hook2", "ret"]),
    )
    loose = cls(
        "Parsek.Loose",
        "  .field public static int32 Free\n  .field public static int32 ClockForTesting",
        meth("Set", ["ldc.i4.1", "stsfld     int32 Parsek.Loose::Free", "ldc.i4.1",
                     "stsfld     int32 Parsek.Loose::ClockForTesting", "ret"]),
    )
    tool = cls("Parsek.InGameTests.Poker", meth("Poke", ["ldc.i4.4", "stsfld     int32 Parsek.Hooks::Hook2", "ret"]))
    return store + bundle + step_if + step + scenario + hooks + other + loose + tool


def run_fixture(il=None, decl=None, gate=None, tests_written=None):
    facts = facts_of(il or fixture_il(), tooling_types=("Parsek.InGameTests.Poker",))
    index = ilanalysis.Index(facts, ("Tools",))
    statics = ilanalysis.static_state(index, CONFIG)
    return lifetimes.analyse(index, statics, CONFIG, decl or declaration(), gate, tests_written)


# --- the declaration, no DLL --------------------------------------------------------------


class CommittedDeclarationTests(unittest.TestCase):
    def test_the_committed_declaration_is_well_formed(self):
        self.assertEqual(lifetimes.declaration_problems(lifetimes.load_declaration()), [])

    def test_it_agrees_with_the_csharp_reset_gate(self):
        gate = lifetimes.load_reset_gate()
        self.assertGreaterEqual(len(gate["types"]), 20)
        self.assertIn("Parsek.Logistics.RouteRunPrompt", gate["types"])
        self.assertGreater(len(gate["allowlist"]), 50)
        self.assertEqual(lifetimes.reset_gate_problems(lifetimes.load_declaration(), gate), [])

    def test_the_pins_cover_every_finding(self):
        pins = lifetimes.load_pins()
        for name in lifetimes.FINDINGS:
            self.assertIn(name, pins)
            self.assertEqual(pins[name]["bound"], len(pins[name]["keys"]), name)
        self.assertEqual(len(pins["untagged"]["keys"]), len(set(pins["untagged"]["keys"])))


class DeclarationShapeTests(unittest.TestCase):
    def problems(self, **overrides):
        return lifetimes.declaration_problems(declaration(**overrides))

    def test_the_fixture_is_well_formed(self):
        self.assertEqual(self.problems(), [])

    def test_an_unknown_lifetime_reds(self):
        types = [{"name": "Parsek.Store", "default": "forever", "reason": "r"}]
        self.assertTrue(any("unknown lifetime 'forever'" in p for p in self.problems(type=types)))
        types = [{"name": "Parsek.Store", "reason": "r", "fields": {"X": "eternal: r"}}]
        self.assertTrue(any("unknown lifetime 'eternal'" in p for p in self.problems(type=types)))

    def test_a_required_lifetime_missing_reds(self):
        lifetimes_table = {name: "m" for name in ALL_LIFETIMES if name != "save"}
        problems = self.problems(lifetimes=lifetimes_table)
        self.assertTrue(any("lacks save" in p for p in problems), problems)

    def test_a_type_declared_twice_reds(self):
        types = [{"name": "Parsek.Store", "default": "save", "reason": "r"}] * 2
        self.assertTrue(any("declared twice" in p for p in self.problems(type=types)))

    def test_a_field_tagged_twice_reds(self):
        types = [{"name": "Parsek.Store", "reason": "r",
                  "fields": {"Mode": "save: r", "<Mode>k__BackingField": "save: r"}}]
        self.assertTrue(any("tags field Mode twice" in p for p in self.problems(type=types)))

    def test_a_missing_reason_reds(self):
        types = [{"name": "Parsek.Store", "default": "save"}]
        self.assertTrue(any("has no reason" in p for p in self.problems(type=types)))
        patterns = [{"match": "*X", "lifetime": "process"}]
        self.assertTrue(any("has no reason" in p for p in self.problems(pattern=patterns)))

    def test_a_typo_key_reds(self):
        types = [{"name": "Parsek.Store", "defualt": "save", "reason": "r", "fields": {"A": "save"}}]
        self.assertTrue(any("unknown key 'defualt'" in p for p in self.problems(type=types)))

    def test_a_root_that_is_no_method_key_reds(self):
        problems = self.problems(cold_load={"roots": {"Parsek.Scenario.OnLoad": "r"}})
        self.assertTrue(any("is not a method key" in p for p in problems), problems)


class ResetGateParseTests(unittest.TestCase):
    CS = """
    namespace Parsek.Tests {
      public class SaveScopedSessionResetTests {
        // typeof(Commented) is a comment, not an entry
        private static readonly Type[] InventoryTypes = {
            typeof(RecordingStore),  /* typeof(Blocked) */
            typeof(Parsek.Logistics.RouteStore),
            // typeof(AlsoCommented),
        };
        private static readonly Type[] Other = { typeof(NotThis) };
        private const string Text = "typeof(InAString)";
      }
    }
    """

    def test_comments_and_strings_are_not_entries(self):
        self.assertEqual(lifetimes.parse_inventory_types(self.CS), ["RecordingStore", "Parsek.Logistics.RouteStore"])

    def test_an_item_that_is_not_typeof_raises(self):
        with self.assertRaises(ValueError):
            lifetimes.parse_inventory_types("Type[] InventoryTypes = { typeof(A), GetB() };")

    def test_the_allowlist_parse(self):
        parsed = lifetimes.parse_allowlist("# header\n\nRecordingStore.StateVersion monotonic counter\n")
        self.assertEqual(parsed, {"RecordingStore.StateVersion": "monotonic counter"})

    def test_the_gate_reds_on_a_disagreement(self):
        gate = {"types": ["Parsek.Store"], "allowlist": {"Store.Version": "counter"}}
        self.assertEqual(lifetimes.reset_gate_problems(declaration(), gate), [])
        # A gated type whose default the reset helper does not clear.
        decl = declaration(type=[{"name": "Parsek.Store", "default": "process", "reason": "r",
                                  "fields": {"Version": "process: r"}}])
        self.assertTrue(any("default must be save" in p for p in lifetimes.reset_gate_problems(decl, gate)))
        # An allowlisted field with no field entry.
        gate2 = {"types": ["Parsek.Store"], "allowlist": {"Store.Version": "c", "Store.Other": "c"}}
        self.assertTrue(any("Store.Other has no field entry" in p
                            for p in lifetimes.reset_gate_problems(declaration(), gate2)))
        # A field tagged as not cleared that the allowlist does not name (the helper resets it).
        gate3 = {"types": ["Parsek.Store"], "allowlist": {}}
        self.assertTrue(any("Store.Version is tagged process" in p
                            for p in lifetimes.reset_gate_problems(declaration(), gate3)))
        # A gated type with no entry at all.
        gate4 = {"types": ["Parsek.Store", "Parsek.Gone"], "allowlist": {"Store.Version": "c"}}
        self.assertTrue(any("Parsek.Gone is a SaveScopedSessionResetTests type" in p
                            for p in lifetimes.reset_gate_problems(declaration(), gate4)))


# --- the metadata reader, no DLL -------------------------------------------------------


class MetadataWalkTests(unittest.TestCase):
    def test_operand_bytes_are_never_read_as_opcodes(self):
        token = 0x04000007  # Field row 7
        code = b"".join([
            bytes([0x20]) + struct.pack("<I", 0x807F807F),         # ldc.i4 whose operand looks like stsfld / ldsflda
            bytes([0x45]) + struct.pack("<I", 1) + struct.pack("<i", 0x80),  # switch with one target
            bytes([0xFE, 0x15]) + struct.pack("<I", 0x0200007F),   # initobj (two-byte opcode, 4-byte token)
            bytes([0x80]) + struct.pack("<I", token),              # stsfld
            bytes([0x7F]) + struct.pack("<I", token + 1),          # ldsflda
            bytes([0x2A]),                                         # ret
        ])
        found = clrmeta.walk_code(code)
        self.assertEqual([(op, tok) for _off, op, tok in found], [(0x80, token), (0x7F, token + 1)])

    def test_a_truncated_body_raises(self):
        with self.assertRaises(clrmeta.FormatError):
            clrmeta.walk_code(bytes([0x80, 0x01, 0x00]))
        with self.assertRaises(clrmeta.FormatError):
            clrmeta.walk_code(bytes([0xFE]))

    def test_tiny_and_fat_headers(self):
        tiny = bytes([(3 << 2) | 0x2, 0x00, 0x00, 0x2A])
        self.assertEqual(clrmeta.method_code(tiny, 0), bytes([0x00, 0x00, 0x2A]))
        fat = struct.pack("<HHII", 0x3003, 8, 2, 0) + bytes([0x00, 0x2A])
        self.assertEqual(clrmeta.method_code(fat, 0), bytes([0x00, 0x2A]))

    def test_test_writes_reads_stsfld_and_static_setters_only(self):
        facts = {"fields": {"Parsek.A::x": {}, "Parsek.A::<P>k__BackingField": {"backingFor": "P"},
                            "Parsek.A::y": {}}}
        facts["fields"]["Parsek.A::t_sink"] = {"writers": ["Parsek.A::set_Sink(System.Action)"]}
        rows = {
            ("Parsek.Tests.T", "call", "Parsek|Parsek.A::set_Sink"),
            ("Parsek.Tests.T", "stsfld", "Parsek|Parsek.A::x"),
            ("Parsek.Tests.T", "call", "Parsek|Parsek.A::set_P"),
            ("Parsek.Tests.T", "ldsflda", "Parsek|Parsek.A::y"),        # an address read is no write
            ("Parsek.Tests.T", "stsfld", "None|Parsek.Tests.T::local"),  # the test's own static
            ("Parsek.Tests.T", "stsfld", "UnityEngine|UnityEngine.X::z"),
        }
        self.assertEqual(lifetimes.test_writes(rows, facts), {
            "Parsek.A::<P>k__BackingField": ["Parsek.Tests.T"], "Parsek.A::t_sink": ["Parsek.Tests.T"],
            "Parsek.A::x": ["Parsek.Tests.T"]})


# --- the findings over inline IL ------------------------------------------------------------


class FindingTests(unittest.TestCase):
    def test_tags_resolve_field_then_pattern_then_type(self):
        result = run_fixture()
        tagged = result["tagged"]
        self.assertEqual(tagged["Parsek.Store::Version"][:2], ["process", "field"])
        self.assertEqual(tagged["Parsek.Store::Saved"][:2], ["save", "type"])
        self.assertEqual(tagged["Parsek.Loose::ClockForTesting"][:2], ["test-hook", "pattern"])
        self.assertEqual(result["untagged"], ["Parsek.Loose::Free"])

    def test_cold_load_misses(self):
        result = run_fixture()
        # Saved (stsfld behind a branch), Boxed (ldsflda + initobj), Items (List.Clear)
        # and ViaSlot (through an interface slot) are written on the load path; Missed
        # is not; Version is `process`, which a load need not touch.
        self.assertEqual(result["findings"]["coldLoadMisses"], ["Parsek.Store::Missed"])
        self.assertIn("Parsek.Store::Arm()", result["details"]["Parsek.Store::Missed"])

    def test_an_address_read_is_no_clearing_write(self):
        result = run_fixture(fixture_il(boxed_default=False))
        self.assertEqual(result["findings"]["coldLoadMisses"], ["Parsek.Store::Boxed", "Parsek.Store::Missed"])

    def test_a_write_off_any_branch_counts_the_same(self):
        self.assertEqual(run_fixture(fixture_il(branch_write=False))["findings"]["coldLoadMisses"],
                         run_fixture()["findings"]["coldLoadMisses"])

    def test_a_test_hook_written_by_another_production_type(self):
        result = run_fixture()
        # Hook: Other.Poke writes it (red). Hook2: a method named as a test hook and a
        # tooling type write it (not red). Own: its own type writes it (not red).
        self.assertEqual(result["findings"]["testHookWrittenByProduction"], ["Parsek.Hooks::Hook"])
        self.assertIn("Parsek.Other::Poke()", result["details"]["Parsek.Hooks::Hook"])

    def test_test_written_untagged(self):
        self.assertIsNone(run_fixture()["findings"]["testWrittenUntagged"])
        written = {"Parsek.Loose::Free": ["Parsek.Tests.T"], "Parsek.Store::Saved": ["Parsek.Tests.T"]}
        result = run_fixture(tests_written=written)
        self.assertEqual(result["findings"]["testWrittenUntagged"], ["Parsek.Loose::Free"])

    def test_reset_gate_disagreement(self):
        self.assertIsNone(run_fixture()["findings"]["resetGateDisagreement"])
        gate = {"types": ["Parsek.Store"], "allowlist": {"Store.Version": "counter"}}
        self.assertEqual(run_fixture(gate=gate)["findings"]["resetGateDisagreement"], [])
        result = run_fixture(gate={"types": ["Parsek.Store"], "allowlist": {}})
        self.assertEqual(result["findings"]["resetGateDisagreement"], ["Parsek.Store::Version"])

    def test_assembly_problems_name_a_missing_type_field_or_root(self):
        decl = declaration(type=declaration()["type"] + [
            {"name": "Parsek.Gone", "default": "save", "reason": "r"},
            {"name": "Parsek.Loose", "reason": "r", "fields": {"Renamed": "save: r"}},
        ], cold_load={"roots": {"Parsek.Scenario::OnLoad()": "r", "Parsek.Scenario::Missing()": "r"}})
        problems = run_fixture(decl=decl)["problems"]
        self.assertTrue(any("Parsek.Gone names no type" in p for p in problems), problems)
        self.assertTrue(any("tags field Renamed" in p for p in problems), problems)
        self.assertTrue(any("Parsek.Scenario::Missing() is no method" in p for p in problems), problems)
        self.assertEqual(run_fixture()["problems"], [])


class RatchetTests(unittest.TestCase):
    def pins_of(self, result):
        import tomllib  # noqa: PLC0415

        return tomllib.loads(lifetimes.render_pins(result["gates"], "fixture", "2026-11-11"))

    def full(self, **kwargs):
        kwargs.setdefault("gate", {"types": ["Parsek.Store"], "allowlist": {"Store.Version": "counter"}})
        kwargs.setdefault("tests_written", {"Parsek.Loose::Free": ["Parsek.Tests.T"]})
        return run_fixture(**kwargs)

    def test_the_pins_read_back_and_hold(self):
        result = self.full()
        pins = self.pins_of(result)
        self.assertEqual(pins["untagged"]["keys"], ["Parsek.Loose::Free"])
        self.assertEqual(pins["coldLoadMisses"], {"bound": 1, "keys": ["Parsek.Store::Missed"]})
        self.assertEqual(lifetimes.ratchet_problems(result["gates"], pins), [])
        self.assertEqual(lifetimes.ratchet_slack(result["gates"], pins), [])

    def test_an_unmeasured_finding_cannot_be_pinned(self):
        with self.assertRaises(ValueError):
            lifetimes.render_pins(run_fixture()["gates"])

    def test_a_new_finding_reds_naming_it_and_the_repin(self):
        pins = self.pins_of(self.full())
        result = self.full(il=fixture_il(boxed_default=False))
        problems = lifetimes.ratchet_problems(result["gates"], pins, result["details"])
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("Parsek.Store::Boxed", problems[0])
        self.assertIn("--write-pins", problems[0])

    def test_a_new_untagged_static_reds(self):
        pins = self.pins_of(self.full())
        decl = declaration(type=[{"name": "Parsek.Store", "reason": "r", "fields": {"Version": "process: r"}},
                                 {"name": "Parsek.Hooks", "default": "test-hook", "reason": "seams"}])
        result = self.full(decl=decl)
        problems = lifetimes.ratchet_problems(result["gates"], pins, result["details"])
        self.assertTrue(problems and "Parsek.Store::Saved" in problems[0], problems)

    def test_a_fixed_finding_passes_and_reports_slack(self):
        pins = self.pins_of(self.full())
        decl = declaration(type=[{"name": "Parsek.Store", "default": "save", "reason": "r",
                                  "fields": {"Version": "process: r", "Missed": "scene: r"}},
                                 {"name": "Parsek.Hooks", "default": "test-hook", "reason": "seams"}])
        gate = {"types": ["Parsek.Store"], "allowlist": {"Store.Version": "counter", "Store.Missed": "per scene"}}
        result = self.full(decl=decl, gate=gate)
        self.assertEqual(lifetimes.ratchet_problems(result["gates"], pins), [])
        self.assertEqual(lifetimes.ratchet_slack(result["gates"], pins), ["coldLoadMisses: 0 below the bound 1"])

    def test_a_release_pin_is_refused(self):
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            lifetimes.main(["Source/Parsek/bin/Release/Parsek.dll", "--write-pins"])
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            lifetimes.main(["Source/Parsek/bin/Debug/Parsek.dll", "--tests", "x/Release/net472/Parsek.Tests.dll",
                            "--write-pins"])


class ViewTests(unittest.TestCase):
    """The Atlas "Code facts" table and the --check section read lifetimes.summary."""

    def summary(self):
        return lifetimes.summary(run_fixture(tests_written={}))

    def test_the_summary_counts(self):
        head = self.summary()
        self.assertEqual((head["population"], head["tagged"], head["untagged"]), (11, 10, 1))
        self.assertEqual(head["byLifetime"]["save"], 5)
        self.assertEqual(head["findings"], {"coldLoadMisses": 1, "testHookWrittenByProduction": 1,
                                            "testWrittenUntagged": 0, "resetGateDisagreement": None})

    def test_the_atlas_table(self):
        import archview  # noqa: PLC0415
        from test_ilanalysis import analyse  # noqa: PLC0415

        report = analyse(cls("Parsek.Svc", meth("Live")))
        self.assertIn("Static lifetimes", archview._atlas_code_facts(report))
        self.assertIn("Not measured.", archview._atlas_code_facts(report).split("Static lifetimes", 1)[1])
        report["lifetimes"] = self.summary()
        text = archview._atlas_code_facts(report).split("Static lifetimes", 1)[1]
        for needle in ("11 statics, 10 tagged, 1 untagged", ">coldLoadMisses<", ">test-hook<", "not measured"):
            self.assertIn(needle, text)

    def test_the_check_section(self):
        import archview  # noqa: PLC0415

        captured = io.StringIO()
        with contextlib.redirect_stdout(captured):
            archview.print_lifetime_section(self.summary())
        text = captured.getvalue()
        self.assertIn("STATIC LIFETIMES", text)
        self.assertRegex(text, r"coldLoadMisses +1")
        self.assertRegex(text, r"resetGateDisagreement +not measured")


class WorkflowTests(unittest.TestCase):
    def test_the_compiled_il_step_runs_these_cells(self):
        steps = test_semantic.workflow_steps(test_semantic.WORKFLOW.read_text(encoding="utf-8"))
        runs = [run for _name, _keys, env, run in steps if IL_ENV in env and "scripts/arch" in run]
        patterns = [m.group(1) for run in runs for m in [re.search(r"-p\s+['\"]?([^'\"\s]+)", run)] if m]
        # This file's own name, so a rename out of the pattern reds here.
        self.assertTrue(any(fnmatch.fnmatch(Path(__file__).name, p) for p in patterns), patterns)


# --- the real tree --------------------------------------------------------------------------

_REAL = {}


def real_tree_context(il_path):
    """The facts, index, static rows, test-assembly writes and lifetime result for the
    real tree, built once per process over the facts test_ilfacts already holds."""
    key = str(Path(il_path).resolve())
    if key not in _REAL:
        tests_dll = lifetimes.DEFAULT_TESTS_DLL
        if not tests_dll.is_file():
            raise AssertionError("%s does not exist (build Parsek.Tests first: scripts/cloud-test.sh"
                                 " or dotnet build)" % tests_dll)
        facts, tooling = test_ilfacts.real_tree_facts(il_path)
        index = ilanalysis.Index(facts, tooling)
        statics = ilanalysis.static_state(index, CONFIG)
        rows, skipped = clrmeta.static_access(clrmeta.read_assembly(tests_dll))
        written = lifetimes.test_writes(rows, facts, facts.get("assembly") or "Parsek")
        decl = lifetimes.load_declaration()
        gate = lifetimes.load_reset_gate()
        result = lifetimes.analyse(index, statics, CONFIG, decl, gate, written)
        _REAL.clear()
        _REAL[key] = {"facts": facts, "tooling": tooling, "index": index, "statics": statics, "written": written,
                      "skipped": skipped, "decl": decl, "gate": gate, "result": result}
    return _REAL[key]


def real_tree_lifetimes(il_path):
    """The real tree's lifetime result (read-only), shared with test_metrics."""
    return real_tree_context(il_path)["result"]


@unittest.skipUnless(os.environ.get(IL_ENV), "%s is not set (CI sets it after the build)" % IL_ENV)
class RealTreeLifetimeTests(unittest.TestCase):
    """The lifetime pins over the built Debug Parsek.dll and Parsek.Tests.dll, and
    mutants of that result: each gate reds on its change and passes the others."""

    @classmethod
    def setUpClass(cls):
        il_path = Path(os.environ[IL_ENV])
        if not il_path.is_file():
            raise AssertionError("%s=%s does not exist (build Parsek first)" % (IL_ENV, il_path))
        cls.ctx = real_tree_context(il_path)
        cls.result = cls.ctx["result"]
        cls.pins = lifetimes.load_pins()
        cls.closure = lifetimes.cold_closure(cls.ctx["index"], cls.result["coldRoots"])
        report = io.StringIO()
        with contextlib.redirect_stdout(report):
            print("\n".join(lifetimes.format_report(cls.result, top=5)))
            for line in lifetimes.ratchet_slack(cls.result["gates"], cls.pins):
                print("  slack (re-pin to tighten): " + line)
        sys.stdout.write("\n" + report.getvalue())

    # --- helpers -----------------------------------------------------------------------

    def rerun(self, facts=None, decl=None, written=None):
        ctx = self.ctx
        index, statics = ctx["index"], ctx["statics"]
        if facts is not None:
            index = ilanalysis.Index(facts, ctx["tooling"])
            statics = ilanalysis.static_state(index, CONFIG)
        return lifetimes.analyse(index, statics, CONFIG, decl or ctx["decl"], ctx["gate"],
                                 written if written is not None else ctx["written"])

    def facts_with(self, changes):
        """A shallow copy of the facts with some field entries replaced (None deletes one)."""
        facts = dict(self.ctx["facts"])
        facts["fields"] = dict(facts["fields"])
        for key, entry in changes.items():
            if entry is None:
                del facts["fields"][key]
            else:
                facts["fields"][key] = entry
        return facts

    def without_cold_writes(self, key):
        """The field entry with every write inside the Cold load closure removed."""
        entry = dict(self.ctx["facts"]["fields"][key])
        for name in ("writers", "defaultedBy", "readers", "addressTakenBy"):
            entry[name] = [m for m in entry.get(name, ()) if m not in self.closure]
        entry["receiverCalls"] = [c for c in entry.get("receiverCalls", ())
                                  if c.rpartition("|")[0] not in self.closure]
        return entry

    def problems(self, result, names=None):
        return lifetimes.ratchet_problems(result["gates"], self.pins, result["details"], names)

    # --- the pins hold ------------------------------------------------------------------

    def test_the_declaration_matches_the_assembly(self):
        self.assertEqual(self.result["problems"], [])

    def test_every_finding_is_measured(self):
        for name in lifetimes.FINDINGS:
            self.assertIsNotNone(self.result["findings"][name], name)

    def test_the_pins_hold(self):
        self.assertEqual(self.problems(self.result), [])

    def test_the_load_path_reaches_the_reset_helper(self):
        self.assertIn("Parsek.ParsekScenario::ResetSaveScopedSessionState(string)", self.closure)
        self.assertEqual(self.result["coldClosure"], len(self.closure))

    # --- the metadata reader agrees with the facts --------------------------------------

    def test_clrmeta_reads_the_same_static_writes_as_the_facts(self):
        import ilfacts  # noqa: PLC0415

        rows, skipped = clrmeta.static_access(clrmeta.read_assembly(os.environ[IL_ENV]))
        facts = self.ctx["facts"]
        for opcode, kind in (("stsfld", "writers"), ("ldsflda", "addressTakenBy")):
            from_meta = {(ilfacts.outermost(writer), target.partition("|")[2]) for writer, op, target in rows
                         if op == opcode and target.startswith("None|") and writer}
            from_facts = set()
            for key, field in facts["fields"].items():
                for method in field.get(kind, ()):
                    from_facts.add((ilfacts.outermost(facts["methods"][method]["type"]
                                                      if method in facts["methods"] else method.split("::")[0]),
                                    key))
            from_meta = {(owner, key) for owner, key in from_meta if key in facts["fields"]
                         and facts["fields"][key]["static"]}
            self.assertEqual(from_meta - from_facts, set(), opcode)
            self.assertEqual(from_facts - from_meta, set(), opcode)

    def test_the_test_assembly_writes_are_read(self):
        written = self.ctx["written"]
        self.assertIn("Parsek.RewindInvokeContext::Pending", written)  # an stsfld
        # A hand-written static setter (ParsekLog.TestSinkForTesting assigns t_testSinkForTesting).
        self.assertIn("Parsek.ParsekLog::t_testSinkForTesting", written)
        self.assertGreater(len(written), 100)

    # --- each gate reds on its change ---------------------------------------------------

    def test_a_new_untagged_static_reds(self):
        # A copy of an untagged reassignable static, under a new name in a production type.
        model = self.pins["untagged"]["keys"][0]
        entry = copy.deepcopy(self.ctx["facts"]["fields"][model])
        key = model.split("::", 1)[0] + "::brandNewStatic"
        result = self.rerun(facts=self.facts_with({key: entry}))
        self.assertIn(key, result["untagged"])
        problems = self.problems(result, {"untagged"})
        self.assertEqual(len(problems), 1, problems)
        self.assertIn(key, problems[0])
        self.assertIn("lifetimes.toml", problems[0])

    def test_a_save_static_losing_its_clearing_write_reds(self):
        for key in ("Parsek.RecordingStore::committedRecordings",    # cleared by a List.Clear call
                    "Parsek.RewindInvokeContext::CapturedBundle"):   # cleared by ldsflda + initobj
            with self.subTest(key=key):
                self.assertIn(self.result["tagged"][key][0], lifetimes.COLD_CLEARED)
                self.assertNotIn(key, self.result["findings"]["coldLoadMisses"])
                result = self.rerun(facts=self.facts_with({key: self.without_cold_writes(key)}))
                problems = self.problems(result, {"coldLoadMisses"})
                self.assertEqual(len(problems), 1, problems)
                self.assertIn(key, problems[0])

    def test_a_test_hook_written_by_another_production_type_reds(self):
        key = "Parsek.MarkerValidator::NowUtProvider"
        self.assertEqual(self.result["tagged"][key][0], "test-hook")
        entry = dict(self.ctx["facts"]["fields"][key])
        entry["writers"] = list(entry.get("writers", ())) + ["Parsek.ParsekScenario::OnLoad(ConfigNode)"]
        result = self.rerun(facts=self.facts_with({key: entry}))
        problems = self.problems(result, {"testHookWrittenByProduction"})
        self.assertEqual(len(problems), 1, problems)
        self.assertIn(key, problems[0])
        self.assertIn("ParsekScenario::OnLoad", problems[0])

    def test_a_test_write_to_an_untagged_static_reds(self):
        key = self.pins["untagged"]["keys"][0]
        written = dict(self.ctx["written"], **{key: ["Parsek.Tests.MutantTests"]})
        problems = self.problems(self.rerun(written=written), {"testWrittenUntagged"})
        self.assertEqual(len(problems), 1, problems)
        self.assertIn(key, problems[0])

    def test_a_tag_for_a_missing_field_reds(self):
        decl = copy.deepcopy(self.ctx["decl"])
        entry = next(e for e in decl["type"] if e["name"] == "Parsek.RecordingStore")
        entry["fields"]["noSuchStatic"] = "save: a field the assembly does not have"
        decl["type"].append({"name": "Parsek.NoSuchType", "default": "save", "reason": "gone"})
        problems = self.rerun(decl=decl)["problems"]
        self.assertTrue(any("tags field noSuchStatic" in p for p in problems), problems)
        self.assertTrue(any("Parsek.NoSuchType names no type" in p for p in problems), problems)

    def test_a_reset_static_tagged_as_surviving_the_load_reds(self):
        decl = copy.deepcopy(self.ctx["decl"])
        entry = next(e for e in decl["type"] if e["name"] == "Parsek.RecordingStore")
        # A static the reset helper resets (not allowlisted), tagged as if it survived.
        key = next(k for k, (_l, source, _r) in sorted(self.result["tagged"].items())
                   if k.startswith("Parsek.RecordingStore::") and source == "type")
        entry["fields"][lifetimes.field_display_name(key.split("::", 1)[1])] = "process: wrong on purpose"
        self.assertTrue(lifetimes.reset_gate_problems(decl, self.ctx["gate"]))
        problems = self.problems(self.rerun(decl=decl), {"resetGateDisagreement"})
        self.assertEqual(len(problems), 1, problems)
        self.assertIn(key, problems[0])

    # --- and passes the changes it must not red on ------------------------------------

    def test_deleting_a_pinned_untagged_static_passes(self):
        key = self.pins["untagged"]["keys"][0]
        result = self.rerun(facts=self.facts_with({key: None}))
        self.assertEqual(self.problems(result), [])
        self.assertTrue(any(line.startswith("untagged:") for line in lifetimes.ratchet_slack(result["gates"],
                                                                                                  self.pins)))

    def test_tagging_a_pinned_untagged_static_passes(self):
        key = self.pins["untagged"]["keys"][0]
        path, name = key.split("::", 1)
        decl = copy.deepcopy(self.ctx["decl"])
        decl["type"].append({"name": path, "reason": "mutant", "fields": {
            lifetimes.field_display_name(name): "process: tagged by the mutant"}})
        result = self.rerun(decl=decl)
        self.assertNotIn(key, result["untagged"])
        self.assertEqual(self.problems(result), [])

    def test_renaming_a_static_tagged_by_its_type_default_passes(self):
        key = next(k for k, (lifetime, source, _r) in sorted(self.result["tagged"].items())
                   if k.startswith("Parsek.RecordingStore::") and source == "type"
                   and k not in self.result["findings"]["coldLoadMisses"])
        renamed = key + "Renamed"
        result = self.rerun(facts=self.facts_with({key: None, renamed: self.ctx["facts"]["fields"][key]}))
        self.assertEqual(result["tagged"][renamed][:2], ["save", "type"])
        self.assertEqual(self.problems(result), [])

    def test_a_swap_inside_a_finding_bound_passes(self):
        fixed = self.pins["coldLoadMisses"]["keys"][0]
        broken = "Parsek.RewindInvokeContext::CapturedBundle"
        healed = dict(self.ctx["facts"]["fields"][fixed])
        healed["writers"] = list(healed.get("writers", ())) + ["Parsek.ParsekScenario::OnLoad(ConfigNode)"]
        result = self.rerun(facts=self.facts_with({fixed: healed, broken: self.without_cold_writes(broken)}))
        self.assertIn(broken, result["findings"]["coldLoadMisses"])
        self.assertNotIn(fixed, result["findings"]["coldLoadMisses"])
        self.assertEqual(self.problems(result), [])


if __name__ == "__main__":
    unittest.main()
