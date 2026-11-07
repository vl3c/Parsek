"""Tests for the semantic (compiled IL) type graph: ilgraph.py and semantic.py.

The pure cells drive the parser with inline ikdasm-shaped IL. The real-tree
cells, the architecture residue ratchet among them, need a built Parsek.dll: set PARSEK_ARCH_IL to it (or to an ikdasm .il
dump). CI sets it after the build (.github/workflows/tests.yml); without it
those cells skip with that reason, and with it set they never skip (a missing
file or a missing ikdasm fails them).
"""

import contextlib
import io
import os
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import archview  # noqa: E402
import ilgraph  # noqa: E402
import semantic  # noqa: E402
import test_archview  # noqa: E402

REAL_SOURCE = archview.REPO_ROOT / "Source" / "Parsek"
IL_ENV = "PARSEK_ARCH_IL"


def _method(name, body, flags="public hidebysig static", ret="void", params=""):
    lines = ["  .method %s %s %s(%s) cil managed" % (flags, ret, name, params), "  {",
             "    // Code size       8 (0x8)", "    .maxstack  8"]
    for index, instruction in enumerate(body):
        if instruction.startswith(" "):
            lines.append(instruction)  # an operand continued on the next line
        else:
            lines.append("    IL_%04x:  %s" % (index, instruction))
    lines.append("  } // end of method X::%s" % name)
    return "\n".join(lines)


def _class(name, *members, header_extra=""):
    return "\n".join(
        [".class public auto ansi beforefieldinit %s" % name,
         "       extends [mscorlib]System.Object%s" % header_extra, "{"]
        + list(members)
        + ["} // end of class %s" % name, ""]
    )


def _ctor():
    return _method(
        ".ctor",
        ["ldarg.0", "call       instance void [mscorlib]System.Object::.ctor()", "nop", "ret"],
        flags="public hidebysig specialname rtspecialname instance",
    )


# A store whose CommitPendingTree forwards to a service (the Debug-build
# shape: nop, call, nop, ret), a caller of the forwarder, and the service.
FORWARDER_IL = "\n".join(
    [
        _class(
            "Parsek.TreeCommitService",
            _method("CommitPendingTree", [
                "nop", "call       void Parsek.Recorder::Flush()",
                "call       void Parsek.Recorder::Flush()", "ret",
            ]),
        ),
        _class("Parsek.Recorder", _method("Flush", ["nop", "ret"])),
        _class(
            "Parsek.RecordingStore",
            _method("CommitPendingTree", [
                "nop", "call       void Parsek.TreeCommitService::CommitPendingTree()", "nop", "ret",
            ]),
            _method("CanRewind", [
                "nop", "ldarg.0", "ldarg.1",
                "call       bool Parsek.RewindLauncher::CanRewind(class Parsek.Recording,",
                "                                              bool)",
                "stloc.0", "br.s       IL_0007", "ldloc.0", "ret",
            ], ret="bool", params="class Parsek.Recording rec, bool isRecording"),
        ),
        _class(
            "Parsek.MergeDialog",
            _method("Commit", [
                "nop", "call       void Parsek.RecordingStore::CommitPendingTree()", "nop", "ret",
            ]),
        ),
    ]
)


class ParseIlTests(unittest.TestCase):
    def test_member_operand_split(self):
        parsed = ilgraph.parse_member_operand(
            "bool Parsek.RewindLauncher::CanRewind(class Parsek.Recording, string&, bool)"
        )
        self.assertEqual(parsed, ("Parsek.RewindLauncher", "CanRewind", "", 3))
        generic = ilgraph.parse_member_operand(
            "instance void class [mscorlib]System.Collections.Generic.List`1"
            "<class Parsek.Logistics.Outer/Inner>::Add(!0)"
        )
        self.assertEqual(generic[1:], ("Add", "", 1))
        self.assertIn("Parsek.Logistics.Outer/Inner", generic[0])
        field = ilgraph.parse_member_operand("int32 Parsek.Outer/Nested::Count")
        self.assertEqual(field, ("Parsek.Outer/Nested", "Count", "", None))
        method_generic = ilgraph.parse_member_operand(
            "!!0 Parsek.Pool::Take<class Parsek.Thing>(int32)"
        )
        self.assertEqual(method_generic, ("Parsek.Pool", "Take", "class Parsek.Thing", 1))

    def test_type_tokens_map_generated_and_nested_paths(self):
        tokens = ilgraph.type_tokens(
            "valuetype Parsek.Logistics.RouteEndpointPartAdoption/EndpointAdoptionResult> x "
            "class Parsek.Outer/'<>c__DisplayClass1_0' y Parsek.GreyLedger`1<!0> "
            "[mscorlib]System.String"
        )
        self.assertEqual(
            tokens,
            [
                "Parsek.Logistics.RouteEndpointPartAdoption/EndpointAdoptionResult",
                "Parsek.Outer",
                "Parsek.GreyLedger",
            ],
        )
        self.assertEqual(ilgraph.short_name("Parsek.Logistics.Outer/Inner"), "Inner")
        self.assertEqual(ilgraph.source_owner("Parsek.Outer/<M>d__3/<>c"), "Parsek.Outer")

    def test_uses_are_credited_to_the_source_type_and_cover_declarations(self):
        il = "\n".join(
            [
                ".class public auto ansi beforefieldinit Parsek.Host",
                "       extends Parsek.BaseThing",
                "       implements Parsek.IThing",
                "{",
                "  .field private static literal int32 Limit = int32(0x00000005)",
                "  .field private class Parsek.FieldType held",
                "  .class auto ansi sealed nested private beforefieldinit '<>c__DisplayClass1_0'",
                "         extends [mscorlib]System.Object",
                "  {",
                _method("'<Run>b__0'", ["ldsfld     class Parsek.Lambda Parsek.Lambda::Instance", "pop", "ret"],
                        flags="assembly hidebysig instance"),
                "  } // end of class '<>c__DisplayClass1_0'",
                "  .class auto ansi nested assembly beforefieldinit Inner",
                "         extends [mscorlib]System.Object",
                "  {",
                _method("Go", ["castclass  Parsek.CastTarget", "ret"]),
                "  } // end of class Inner",
                "  .method public hidebysig static void  Run(class Parsek.ParamType p) cil managed",
                "  {",
                "    .maxstack  8",
                "    .locals init (class Parsek.LocalType V_0,",
                "             int32 V_1)",
                "    IL_0000:  ldstr      \"Parsek.StringOnly // not a comment\"",
                "    IL_0005:  newobj     instance void Parsek.Built::.ctor()",
                "    IL_000a:  ret",
                "  } // end of method Host::Run",
                "  .method private hidebysig newslot virtual final",
                "          instance void  Parsek.IThing.Act() cil managed",
                "  {",
                "    IL_0000:  ret",
                "  } // end of method Host::Parsek.IThing.Act",
                "} // end of class Parsek.Host",
            ]
        )
        parsed = ilgraph.parse_il(il)
        names = {
            "Host", "BaseThing", "IThing", "FieldType", "Lambda", "Inner", "CastTarget",
            "ParamType", "LocalType", "Built", "StringOnly", "Act",
        }
        graph = ilgraph.type_graph_from_il(parsed, names)
        self.assertEqual(
            graph["Host"],
            ["BaseThing", "Built", "FieldType", "IThing", "Lambda", "LocalType", "ParamType"],
        )
        self.assertEqual(graph["Inner"], ["CastTarget"])
        self.assertEqual(parsed["literals"], {"Parsek.Host": ["Limit"]})
        self.assertIn("Parsek.Host/Inner", parsed["types"])

    def test_catch_clauses_and_callee_signatures(self):
        il = _class(
            "Parsek.Caller",
            "\n".join(
                [
                    "  .method public hidebysig static void  Run() cil managed",
                    "  {",
                    "    .try",
                    "    {",
                    "      IL_0000:  call       class Parsek.ReturnOnly Parsek.Callee::Make(class Parsek.ArgOnly)",
                    "      IL_0005:  pop",
                    "      IL_0006:  leave.s    IL_000a",
                    "    }  // end .try",
                    "    catch Parsek.CaughtError ",
                    "    {",
                    "      IL_0008:  pop",
                    "      IL_0009:  leave.s    IL_000a",
                    "    }  // end handler",
                    "    IL_000a:  ret",
                    "  } // end of method Caller::Run",
                ]
            ),
        )
        graph = ilgraph.type_graph_from_il(
            ilgraph.parse_il(il), {"Caller", "Callee", "ReturnOnly", "ArgOnly", "CaughtError"}
        )
        # The callee's own signature types are not the caller's references.
        self.assertEqual(graph["Caller"], ["Callee", "CaughtError"])


class ForwarderTests(unittest.TestCase):
    NAMES = {"TreeCommitService", "Recorder", "RecordingStore", "MergeDialog", "RewindLauncher",
             "Recording"}

    def test_a_call_to_a_forwarder_is_credited_to_its_target(self):
        parsed = ilgraph.parse_il(FORWARDER_IL)
        table = ilgraph.forwarder_table(parsed["methods"])
        self.assertEqual(
            table[("Parsek.RecordingStore", "CommitPendingTree", 0)], "Parsek.TreeCommitService"
        )
        self.assertEqual(table[("Parsek.RecordingStore", "CanRewind", 2)], "Parsek.RewindLauncher")
        raw = ilgraph.type_graph_from_il(parsed, self.NAMES, resolve_forwarders=False)
        resolved = ilgraph.type_graph_from_il(parsed, self.NAMES)
        self.assertEqual(raw["MergeDialog"], ["RecordingStore"])
        self.assertEqual(resolved["MergeDialog"], ["TreeCommitService"])
        # The forwarder's own type still references its target.
        self.assertEqual(resolved["RecordingStore"], ["Recording", "RewindLauncher", "TreeCommitService"])
        self.assertEqual(
            ilgraph.forwarder_calls(parsed, self.NAMES),
            [("MergeDialog", "RecordingStore", "CommitPendingTree", "TreeCommitService")],
        )

    def test_deleting_a_forwarder_leaves_the_callers_edges_unchanged(self):
        direct = FORWARDER_IL.replace(
            "call       void Parsek.RecordingStore::CommitPendingTree()",
            "call       void Parsek.TreeCommitService::CommitPendingTree()",
        )
        with_forwarder = ilgraph.type_graph_from_il(ilgraph.parse_il(FORWARDER_IL), self.NAMES)
        without = ilgraph.type_graph_from_il(ilgraph.parse_il(direct), self.NAMES)
        self.assertEqual(with_forwarder["MergeDialog"], without["MergeDialog"])

    def test_a_service_accessor_before_the_call_is_still_a_forwarder(self):
        il = _class(
            "Parsek.ParsekFlight",
            _method("get_Spawn", ["ldarg.0", "ldfld      class Parsek.SpawnHost Parsek.ParsekFlight::spawn", "ret"],
                    flags="assembly hidebysig specialname instance", ret="class Parsek.SpawnHost"),
            _method("OnSpawn", [
                "nop", "ldarg.0", "call       instance class Parsek.SpawnHost Parsek.ParsekFlight::get_Spawn()",
                "ldarg.1", "callvirt   instance void Parsek.SpawnHost::OnSpawn(int32)", "nop", "ret",
            ], flags="assembly hidebysig instance", params="int32 index"),
        )
        table = ilgraph.forwarder_table(ilgraph.parse_il(il)["methods"])
        self.assertEqual(table[("Parsek.ParsekFlight", "OnSpawn", 1)], "Parsek.SpawnHost")
        self.assertNotIn(("Parsek.ParsekFlight", "get_Spawn", 0), table)

    def test_what_is_not_a_forwarder(self):
        il = _class(
            "Parsek.Derived",
            _method(".ctor", ["ldarg.0", "call       instance void Parsek.BaseThing::.ctor()", "ret"],
                    flags="public hidebysig specialname rtspecialname instance"),
            _method("Twice", [
                "call       void Parsek.Other::A()", "call       void Parsek.Other::B()", "ret",
            ]),
            _method("Branchy", [
                "ldarg.0", "brfalse.s  IL_0004", "call       void Parsek.Other::A()", "ret",
            ], params="bool go"),
            _method("Virt", ["call       void Parsek.Other::A()", "ret"],
                    flags="public hidebysig newslot virtual instance"),
            _method("Getter", ["ldarg.0", "ldfld      int32 Parsek.Other::count", "ret"],
                    ret="int32", params="class Parsek.Other o"),
            _method("ToBcl", ["ldarg.0", "call       bool [mscorlib]System.String::IsNullOrEmpty(string)", "ret"],
                    ret="bool", params="string s"),
            _method("Mixed", ["call       void Parsek.Other::A()", "ret"], params="int32 a"),
            _method("Mixed", ["ldarg.0", "call       void Parsek.Elsewhere::A(int32)", "ret"], params="string a"),
        )
        table = ilgraph.forwarder_table(ilgraph.parse_il(il)["methods"])
        # Mixed: two overloads of one arity forwarding to different types.
        self.assertEqual(table, {})

    def test_a_forward_chain_resolves_to_its_end_and_a_cycle_does_not(self):
        il = "\n".join(
            [
                _class("Parsek.Front", _method("Go", ["call       void Parsek.Middle::Go()", "ret"]),
                       _method("Loop", ["call       void Parsek.Middle::Loop()", "ret"])),
                _class("Parsek.Middle", _method("Go", ["call       void Parsek.Back::Go()", "ret"]),
                       _method("Loop", ["call       void Parsek.Front::Loop()", "ret"])),
                _class("Parsek.Back", _method("Go", ["ldc.i4.1", "pop", "ret"])),
            ]
        )
        table = ilgraph.forwarder_table(ilgraph.parse_il(il)["methods"])
        self.assertEqual(table[("Parsek.Front", "Go", 0)], "Parsek.Back")
        self.assertEqual(table[("Parsek.Middle", "Go", 0)], "Parsek.Back")
        self.assertNotIn(("Parsek.Front", "Loop", 0), table)


class ClassifyTests(unittest.TestCase):
    KINDS = {"Status": "enum", "Limits": "class", "Outer": "class", "Kind": "class", "Holder": "class"}
    LITERALS = {"Status": {"Active"}, "Limits": {"MaxFrames"}}
    NESTED = {"Outer": {"Inner"}}

    def classify(self, prefix, suffix, target):
        return semantic.classify_text_occurrence(
            prefix, suffix, target, self.KINDS, self.LITERALS, self.NESTED, {"Holder"}
        )

    def test_each_reason(self):
        cases = [
            ("x = ", ".MaxFrames;", "Limits", "const"),
            ("x = ", ".MaxFramesHold", "Limits", "const"),
            ("if (s == ", ".Active)", "Status", "enum-constant"),
            ("var y = (", ")raw;", "Status", "enum-cast"),
            ("log(nameof(", "));", "Limits", "nameof"),
            ("List<", ".Inner> rows;", "Outer", "nested-qualifier"),
            ("[HarmonyPatch(typeof(", "))]", "Limits", "attribute-argument"),
            ("new Row { ", " = k,", "Kind", "initializer-member"),
            ("var s = rec?.", ";", "Limits", "null-conditional-member"),
            ("c.ContractState != Contract.", ".Active)", "Kind", "foreign-qualifier"),
            ("return Outer.", ";", "Kind", "qualified-member"),
            ("public Contract.State ", ";", "Kind", "declared-member-name"),
            ("static readonly ", "<int> cmp;", "Kind", "foreign-generic"),
            ("static readonly ", "<int> cmp;", "Holder", "other"),
            ("x = (", ")y;", "Limits", "other"),
        ]
        for prefix, suffix, target, expected in cases:
            with self.subTest(prefix=prefix, target=target):
                self.assertEqual(self.classify(prefix, suffix, target), expected)


class ProseNumberTests(unittest.TestCase):
    def model(self):
        return {
            "modules": [
                {"name": "Alpha", "files": 3, "tooling": False, "fanIn": 1, "fanOut": 1,
                 "instability": 0.5},
                {"name": "Beta", "files": 2, "tooling": False, "fanIn": 1, "fanOut": 1,
                 "instability": 0.5},
                {"name": "Tools", "files": 7, "tooling": True, "fanIn": 0, "fanOut": 1,
                 "instability": 1.0},
            ],
            "edges": [
                {"from": "Alpha", "to": "Beta", "upward": True, "weight": 1, "types": [],
                 "files": []},
                {"from": "Beta", "to": "Alpha", "upward": False, "weight": 1, "types": [],
                 "files": []},
                {"from": "Tools", "to": "Alpha", "upward": True, "weight": 1, "types": [],
                 "files": []},
            ],
            "types": [],
            "knots": [{"size": 12}],
        }

    def test_undated_mismatch_is_stale_and_dated_is_history(self):
        prose = {
            "page": {
                "map_note": "Two production modules. Tooling modules (tests, 9 files together) hidden.",
                "layering_note": "On 2026-11-07, 3 module edges run upward on this branch (88 on main).",
                "knot": "The largest knot holds 12 types.",
            }
        }
        findings = archview.prose_findings(self.model(), prose)
        self.assertEqual(findings["staleNumbers"], ["page.map_note: tooling files says 9, measured 7"])
        self.assertEqual(
            findings["datedNumbers"],
            ["page.layering_note: upward module edges says 3, measured 1 (dated)"],
        )

    def test_a_claim_the_model_cannot_measure_is_skipped(self):
        prose = {"x": ["664 methods of 90 lines or more.", "Three production modules."]}
        rows = archview.prose_number_findings(self.model(), prose)
        self.assertEqual([row["claim"] for row in rows], ["production modules"])
        self.assertEqual(rows[0]["path"], "x[1]")

    def test_check_prints_the_number_lines(self):
        prose = {"page": {"map_note": "Seven production modules."}}
        captured = io.StringIO()
        with contextlib.redirect_stdout(captured):
            archview.run_check(
                dict(self.model(), knots=[], typeLevels={"max": 0, "histogram": {}}), [], [], prose
            )
        text = captured.getvalue()
        self.assertIn(
            "undated prose numbers that disagree with the model (1): page.map_note:"
            " production modules says 7, measured 2",
            text,
        )
        self.assertIn("dated prose numbers the model has moved past: none.", text)


class CutOrderTests(unittest.TestCase):
    def test_the_text_cut_order_has_one_definition(self):
        self.assertEqual(archview.RATCHET_CUT_ORDER, test_archview.RATCHET_CUT_ORDER)
        self.assertIs(semantic.TEXT_CUT_ORDER, archview.RATCHET_CUT_ORDER)

    def test_residue_members_matches_the_replayed_size(self):
        graph = {"A": ["B"], "B": ["C"], "C": ["A"], "D": ["A"], "E": []}
        self.assertEqual(semantic.residue_members(graph, []), ["A", "B", "C"])
        self.assertEqual(semantic.residue_members(graph, ["A"]), [])
        self.assertEqual(archview.replay_cut_residue(graph, []), 3)


# The RecordingStore forwarders the arch refresh (2026-11-07) lists as kept for
# the text metric, with the service each one forwards to.
STORE_FORWARDERS = {
    ("CanRewind", 3): "RewindLauncher",
    ("CanRewindWithResolvedSaveState", 4): "RewindLauncher",
    ("CanFastForward", 3): "RewindLauncher",
    ("CanFastForwardAtUT", 4): "RewindLauncher",
    ("InitiateRewind", 1): "RewindLauncher",
    ("InitiateRewindToCareerStart", 1): "RewindLauncher",
    ("CommitPendingTree", 0): "TreeCommitService",
    ("UnionActiveReFlyTreeIntoCommittedForMerge", 5): "TreeCommitService",
    ("RefreshSaveAndQuicksaveAfterDiscard", 2): "TreeDiscardService",
    ("RunOptimizationPass", 0): "CommittedListOptimizer",
    ("TryDiscardActiveSwitchSegmentAttempt", 1): "SwitchSegmentAttempts",
}


@unittest.skipUnless(os.environ.get(IL_ENV), "%s is not set (CI sets it after the build)" % IL_ENV)
class RealTreeSemanticTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        il_path = Path(os.environ[IL_ENV])
        if not il_path.is_file():
            raise AssertionError("%s=%s does not exist (build Parsek first)" % (IL_ENV, il_path))
        rules, tooling, _forbidden, _allowed = archview.load_rules(archview.DEFAULT_MODULES)
        cls.model = archview.build_model(REAL_SOURCE, rules, tooling, measure_sizes=False)
        cls.result = semantic.build(cls.model, ilgraph.load_il(il_path), REAL_SOURCE)
        report = io.StringIO()
        with contextlib.redirect_stdout(report):
            semantic.print_report(cls.result)
        sys.stderr.write("\n" + report.getvalue())

    def test_the_semantic_graph_has_the_text_models_nodes(self):
        self.assertEqual(set(self.result["semantic"]), set(self.result["text"]))
        self.assertGreater(len(semantic.edge_set(self.result["semantic"])), 5000)

    def test_the_documented_store_forwarders_resolve_to_their_services(self):
        table = ilgraph.forwarder_table(self.result["parsed"]["methods"])
        resolved = {
            (name, arity): ilgraph.short_name(target)
            for (owner, name, arity), target in table.items()
            if owner == "Parsek.RecordingStore"
        }
        for key, service in STORE_FORWARDERS.items():
            with self.subTest(member=key):
                self.assertEqual(resolved.get(key), service)

    def test_the_semantic_residue_ratchet(self):
        # The architecture ratchet (W4a2, 2026-11-07). The bound is the
        # semantic residue measured on 088feb067, the tree where the text
        # residue (test_archview.test_cut_sequence_halves_the_largest_knot)
        # still passed its bound 283: greedy 258, replayed 258 with
        # SEMANTIC_CUT_ORDER. Like the text ratchet before it, the measure is
        # min(greedy residue, replayed residue); the replay never rises when a
        # reference is removed. A call through a forwarder is a call to the
        # forwarded type here, so adding or deleting a forwarder does not move
        # it. Tighten the bound to the measured value in the PR that lowers it;
        # never loosen it, not even for a new type that only splits a residue
        # member.
        self.assertLessEqual(semantic.ratchet_residue(self.result["semanticMeasure"]), 258)

if __name__ == "__main__":
    unittest.main()
