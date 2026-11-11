"""Tests for the analyses and ratchets over the compiled-IL facts (ilanalysis.py).

The rule cells build small ikdasm-shaped assemblies inline (one class per
rule, so each cell names the rule it holds) plus a few cells on the verbatim
fixture test_ilfacts reads. The real-tree cells, the three ratchets among
them, need PARSEK_ARCH_IL like test_semantic's; CI's compiled-IL step runs
them against the built Debug DLL.
"""

import contextlib
import fnmatch
import io
import os
import re
import sys
import tomllib
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import archview  # noqa: E402
import ilanalysis  # noqa: E402
import ilfacts  # noqa: E402
import ilgraph  # noqa: E402
import test_ilfacts  # noqa: E402
import test_semantic  # noqa: E402

IL_ENV = test_semantic.IL_ENV
CONFIG = ilanalysis.load_config()


def attr(name, args="", blob="01 00 00 00"):
    return "    .custom instance void %s::.ctor(%s) = ( %s )" % (name, args, blob)


def meth(name, body=("ret",), flags="public hidebysig static", ret="void", params="", attrs=()):
    lines = ["  .method %s %s %s(%s) cil managed" % (flags, ret, name, params), "  {"]
    lines.extend(attrs)
    lines.append("    .maxstack  8")
    for index, instruction in enumerate(body):
        lines.append("    IL_%04x:  %s" % (index, instruction))
    lines.append("  } // end of method %s" % name)
    return "\n".join(lines)


def ctor(base="[mscorlib]System.Object", flags="public hidebysig specialname rtspecialname instance"):
    return meth(".ctor", ["ldarg.0", "call       instance void %s::.ctor()" % base, "ret"], flags=flags)


def cls(name, *members, extends="[mscorlib]System.Object", implements=(), flags="public auto ansi beforefieldinit",
        attrs=()):
    head = [".class %s %s" % (flags, name), "       extends %s" % extends]
    if implements:
        head.append("       implements %s" % ", ".join(implements))
    return "\n".join(head + ["{"] + [a.replace("    .custom", "  .custom", 1) for a in attrs]
                     + list(members) + ["} // end of class %s" % name, ""])


def facts_of(il, tooling_types=()):
    parsed = ilgraph.parse_il(il)
    table = {path: {"Tools" if path in tooling_types else "Core"} for path in parsed["typeInfo"]}
    return ilfacts.build(parsed, table)


def analyse(il, tests_il=None, tooling_types=()):
    facts = facts_of(il, tooling_types)
    tests = ilfacts.build(ilgraph.parse_il(tests_il)) if tests_il else None
    return ilanalysis.report(facts, ("Tools",), CONFIG, tests)


def rule_of(result, key):
    for rule, keys in result["uncalled"]["rootedBy"].items():
        if key in keys:
            return rule
    return "uncalled" if key in result["uncalled"]["uncalled"] else "called"


VOID_CALL = "call       void %s()"
UNITY_BASE = "[UnityEngine.CoreModule]UnityEngine.MonoBehaviour"


class RootRuleTests(unittest.TestCase):
    """Each root rule keeps exactly what it says, and nothing it does not."""

    def check(self, il, key, expected, **kwargs):
        result = analyse(il, **kwargs)
        self.assertEqual(rule_of(result, key), expected, key)
        return result

    def test_a_plain_method_nothing_calls_is_listed(self):
        il = cls("Parsek.Lonely", meth("Unused"), meth("Used"), meth("Caller", [VOID_CALL % "Parsek.Lonely::Used", "ret"]))
        result = self.check(il, "Parsek.Lonely::Unused()", "uncalled")
        self.assertEqual(rule_of(result, "Parsek.Lonely::Used()"), "called")
        # Recursion is no caller.
        il = cls("Parsek.Self", meth("Loop", [VOID_CALL % "Parsek.Self::Loop", "ret"]))
        self.check(il, "Parsek.Self::Loop()", "uncalled")

    def test_static_constructor(self):
        il = cls("Parsek.S", meth(".cctor", flags="private hidebysig specialname rtspecialname static"))
        self.check(il, "Parsek.S::.cctor()", "static constructor")

    def test_delegate_type(self):
        il = cls("Parsek.Handler", meth("Invoke", flags="public hidebysig newslot virtual instance"),
                 extends="[mscorlib]System.MulticastDelegate", flags="public auto ansi sealed")
        self.check(il, "Parsek.Handler::Invoke()", "delegate type")

    def test_attribute_on_the_method(self):
        il = cls("Parsek.T", meth("Run", attrs=[attr("Parsek.InGameTests.InGameTestAttribute")]),
                 meth("Ext", attrs=[attr("[mscorlib]System.Runtime.CompilerServices.ExtensionAttribute")]))
        result = self.check(il, "Parsek.T::Run()", "attribute")
        # An ignored attribute (an extension method's) roots nothing.
        self.assertEqual(rule_of(result, "Parsek.T::Ext()"), "uncalled")

    def test_harmony_patch_methods_by_attribute_and_by_typeof(self):
        patch = attr("['0Harmony']HarmonyLib.HarmonyPatch")
        il = (cls("Parsek.Patches.ByAttribute", meth("Postfix"), meth("Helper"), attrs=[patch])
              + cls("Parsek.Patches.ByTypeof", meth("TargetMethod", ret="class [mscorlib]System.Reflection.MethodBase"),
                    meth("Prefix", ret="bool"))
              + cls("Parsek.Patches.Installer", meth("Install", [
                  "ldtoken    Parsek.Patches.ByTypeof", "pop", "ret"]))
              + cls("Parsek.Patches.NotHandedOver", meth("Prefix", ret="bool")))
        result = self.check(il, "Parsek.Patches.ByAttribute::Postfix()", "harmony patch method")
        self.assertEqual(rule_of(result, "Parsek.Patches.ByAttribute::Helper()"), "uncalled")
        self.assertEqual(rule_of(result, "Parsek.Patches.ByTypeof::TargetMethod()"), "harmony patch method")
        self.assertEqual(rule_of(result, "Parsek.Patches.ByTypeof::Prefix()"), "harmony patch method")
        # Nothing names it, so nothing hands it to Harmony.
        self.assertEqual(rule_of(result, "Parsek.Patches.NotHandedOver::Prefix()"), "uncalled")

    def test_unity_messages_only_on_engine_types(self):
        instance = "private hidebysig instance"
        il = (cls("Parsek.Behaviour", meth("Update", flags=instance), meth("Other", flags=instance),
                  ctor(UNITY_BASE), extends=UNITY_BASE)
              + cls("Parsek.Derived", meth("OnGUI", flags=instance), extends="Parsek.Behaviour")
              + cls("Parsek.Plain", meth("Update", flags=instance)))
        result = self.check(il, "Parsek.Behaviour::Update()", "unity message")
        self.assertEqual(rule_of(result, "Parsek.Derived::OnGUI()"), "unity message")
        self.assertEqual(rule_of(result, "Parsek.Behaviour::Other()"), "uncalled")
        self.assertEqual(rule_of(result, "Parsek.Plain::Update()"), "uncalled")
        # The engine creates it: its constructor is rooted.
        self.assertEqual(rule_of(result, "Parsek.Behaviour::.ctor()"), "constructor of a used type")

    def test_object_members_and_operators(self):
        virtual = "public hidebysig virtual instance"
        il = cls("Parsek.V", meth("ToString", flags=virtual, ret="string"),
                 meth("op_Equality", flags="public hidebysig specialname static", ret="bool",
                      params="class Parsek.V a, class Parsek.V b"))
        result = self.check(il, "Parsek.V::ToString()", "object member or operator")
        self.assertEqual(rule_of(result, "Parsek.V::op_Equality(Parsek.V,Parsek.V)"), "object member or operator")

    def test_override_of_an_outside_method(self):
        il = cls("Parsek.Scenario", meth("OnLoad", flags="public hidebysig virtual instance",
                                        params="class ['Assembly-CSharp']ConfigNode node"),
                 extends="['Assembly-CSharp']ScenarioModule")
        self.check(il, "Parsek.Scenario::OnLoad(ConfigNode)", "overrides an outside method")

    def test_implementation_of_an_outside_interface(self):
        newslot = "public hidebysig newslot virtual final instance"
        il = (cls("Parsek.Res", meth("Dispose", flags=newslot), implements=["[mscorlib]System.IDisposable"])
              + cls("Parsek.ResX", meth("System.IDisposable.Dispose", flags="private hidebysig newslot virtual final instance"),
                    implements=["[mscorlib]System.IDisposable"]))
        result = self.check(il, "Parsek.Res::Dispose()", "implements an outside interface")
        self.assertEqual(rule_of(result, "Parsek.ResX::System.IDisposable.Dispose()"), "implements an outside interface")

    def test_dispatch_from_a_used_slot_and_not_from_an_unused_one(self):
        abstract = "public hidebysig newslot abstract virtual instance"
        impl = "public hidebysig newslot virtual final instance"
        il = (cls("Parsek.IThing", meth("Go", (), flags=abstract), meth("Idle", (), flags=abstract),
                  flags="public abstract auto ansi interface")
              + cls("Parsek.Thing", meth("Go", flags=impl), meth("Idle", flags=impl), ctor(),
                    implements=["Parsek.IThing"])
              + cls("Parsek.Explicit", meth("Parsek.IThing.Go", flags="private hidebysig newslot virtual final instance"),
                    implements=["Parsek.IThing"])
              + cls("Parsek.Base", meth("Run", flags="public hidebysig newslot virtual instance"))
              + cls("Parsek.Over", meth("Run", flags="public hidebysig virtual instance"), extends="Parsek.Base")
              + cls("Parsek.User", meth("Use", [
                  "ldnull", "callvirt   instance void Parsek.IThing::Go()",
                  "ldnull", "callvirt   instance void Parsek.Base::Run()", "ret"])))
        result = self.check(il, "Parsek.Thing::Go()", "dispatch from a used slot")
        self.assertEqual(rule_of(result, "Parsek.Explicit::Parsek.IThing.Go()"), "dispatch from a used slot")
        self.assertEqual(rule_of(result, "Parsek.Over::Run()"), "dispatch from a used slot")
        self.assertEqual(rule_of(result, "Parsek.Thing::Idle()"), "uncalled")
        self.assertEqual(rule_of(result, "Parsek.IThing::Idle()"), "uncalled")

    def test_slot_with_a_used_implementation(self):
        il = (cls("Parsek.Shape", meth("Area", (), flags="public hidebysig newslot abstract virtual instance"),
                  flags="public abstract auto ansi beforefieldinit")
              + cls("Parsek.Square", meth("Area", flags="public hidebysig virtual instance"), extends="Parsek.Shape")
              + cls("Parsek.Caller", meth("Use", ["ldnull", "call       instance void Parsek.Square::Area()", "ret"])))
        self.check(il, "Parsek.Shape::Area()", "slot with a used implementation")

    def test_accessor_of_a_used_property(self):
        special = "public hidebysig specialname static"
        il = (cls("Parsek.P", meth("get_Value", ["ldc.i4.0", "ret"], flags=special, ret="int32"),
                  meth("set_Value", flags=special, params="int32 'value'"),
                  meth("get_Lost", ["ldc.i4.0", "ret"], flags=special, ret="int32"))
              + cls("Parsek.R", meth("Read", ["call       int32 Parsek.P::get_Value()", "pop", "ret"])))
        result = self.check(il, "Parsek.P::set_Value(int32)", "accessor of a used property")
        self.assertEqual(rule_of(result, "Parsek.P::get_Lost()"), "uncalled")

    def test_constructor_of_a_used_type_and_of_an_unused_one(self):
        il = (cls("Parsek.Named", ctor()) + cls("Parsek.Orphan", ctor())
              + cls("Parsek.Namer", meth("Use", ["ldnull", "castclass  Parsek.Named", "pop", "ret"])))
        result = self.check(il, "Parsek.Named::.ctor()", "constructor of a used type")
        self.assertEqual(rule_of(result, "Parsek.Orphan::.ctor()"), "uncalled")

    def test_names_in_strings(self):
        il = (cls("Parsek.Co", meth("Tick"), meth("Start", ['ldstr      "Tick"', "pop", "ret"]))
              + cls("Parsek.Far", meth("Remote"))
              + cls("Parsek.Reflector", meth("Find", ['ldstr      "Remote"', "pop", "ret"])))
        result = self.check(il, "Parsek.Co::Tick()", "named in a string of its own type")
        self.assertEqual(rule_of(result, "Parsek.Far::Remote()"), "named in a string elsewhere")

    def test_compiler_generated(self):
        il = cls("Parsek.G", meth("EqualityContract",
                                  attrs=[attr("[mscorlib]System.Runtime.CompilerServices.CompilerGeneratedAttribute")]))
        self.check(il, "Parsek.G::EqualityContract()", "compiler-generated")

    def test_a_tooling_caller_is_a_caller_and_tooling_is_not_listed(self):
        il = cls("Parsek.Prod", meth("Hook")) + cls("Parsek.InGameTests.Probe", meth(
            "Run", [VOID_CALL % "Parsek.Prod::Hook", "ret"]), meth("Orphan"))
        result = analyse(il, tooling_types=("Parsek.InGameTests.Probe",))
        self.assertEqual(rule_of(result, "Parsek.Prod::Hook()"), "called")
        self.assertNotIn("Parsek.InGameTests.Probe::Orphan()", result["uncalled"]["uncalled"])

    def test_the_test_assembly_splits_the_list(self):
        il = cls("Parsek.Svc", meth("Dead"), meth("OnlyTested"), meth("SeedForTesting"))
        tests_il = ".assembly Parsek.Tests\n{\n}\n" + cls("Parsek.Tests.SvcTests", meth("Fact", [
            "call       void [Parsek]Parsek.Svc::OnlyTested()",
            "call       void [Parsek]Parsek.Svc::SeedForTesting()", "ret"]))
        result = analyse(il, tests_il)
        dead = result["uncalled"]
        self.assertEqual(dead["noCaller"], ["Parsek.Svc::Dead()"])
        self.assertEqual(dead["testOnly"], ["Parsek.Svc::OnlyTested()", "Parsek.Svc::SeedForTesting()"])
        self.assertEqual(dead["testOnlyBySuffix"], {"ForTesting": 1, "other": 1})
        # The ratchet's set reads Parsek.dll alone and leaves hooks by name out.
        self.assertEqual(result["gates"]["uncalledInAssembly"], ["Parsek.Svc::Dead()", "Parsek.Svc::OnlyTested()"])


class StaticStateTests(unittest.TestCase):
    LIST = "class [mscorlib]System.Collections.Generic.List`1<string>"

    def build(self):
        fields = "\n".join([
            "  .field public static int32 Counter",
            "  .field public static int32 Quiet",
            "  .field public static initonly %s Items" % self.LIST,
            "  .field public static initonly %s Table" % self.LIST,
            "  .field public static initonly string[] Names",
            "  .field public static literal int32 Max = int32(0x00000005)",
            "  .field private static int32 '<Mode>k__BackingField'",
        ])
        add = "callvirt   instance void %s::Add(!0)" % self.LIST
        owner = cls(
            "Parsek.Store",
            fields,
            meth(".cctor", ["ldsfld     %s Parsek.Store::Table" % self.LIST, 'ldstr      "x"', add, "ret"],
                 flags="private hidebysig specialname rtspecialname static"),
            meth("Bump", ["ldc.i4.1", "stsfld     int32 Parsek.Store::Quiet", "ret"]),
            meth("set_Mode", ["ldarg.0", "stsfld     int32 Parsek.Store::'<Mode>k__BackingField'", "ret"],
                 flags="public hidebysig specialname static", params="int32 'value'"),
        )
        other = cls(
            "Parsek.Other",
            meth("Poke", [
                "ldc.i4.2", "stsfld     int32 Parsek.Store::Counter",
                "ldsfld     %s Parsek.Store::Items" % self.LIST, 'ldstr      "y"', add,
                "ldc.i4.3", "call       void Parsek.Store::set_Mode(int32)",
                # An element store whose VALUE is the static is no mutation of it.
                "ldnull", "ldc.i4.0", "ldsfld     string[] Parsek.Store::Names", "stelem.ref", "ret"]),
        )
        tool = cls("Parsek.InGameTests.Poker", meth("Poke", ["ldc.i4.4", "stsfld     int32 Parsek.Store::Quiet", "ret"]))
        return analyse(owner + other + tool, tooling_types=("Parsek.InGameTests.Poker",))["statics"]

    def test_rows(self):
        statics = self.build()
        rows = statics["fields"]
        self.assertNotIn("Parsek.Store::Max", rows)
        self.assertEqual(sorted(k for k, r in rows.items() if r["reassignable"]), [
            "Parsek.Store::<Mode>k__BackingField", "Parsek.Store::Counter", "Parsek.Store::Quiet"])
        # Mutated by Add outside the static constructor; Table only inside it.
        self.assertTrue(rows["Parsek.Store::Items"]["mutated"])
        self.assertFalse(rows["Parsek.Store::Table"]["mutated"])
        self.assertFalse(rows["Parsek.Store::Names"]["mutated"])
        self.assertEqual(rows["Parsek.Store::Items"]["mutatedElsewhereBy"], ["Parsek.Other::Poke()"])

    def test_written_by_another_production_type(self):
        statics = self.build()
        # stsfld from Other and a static auto-property setter Other calls; the
        # owner's own write and the tooling write do not count.
        self.assertEqual(sorted(statics["crossTypeWrites"]), [
            "Parsek.Store::<Mode>k__BackingField", "Parsek.Store::Counter"])
        self.assertEqual(statics["byModule"]["Core"]["writtenElsewhere"], 2)
        self.assertEqual(statics["byType"]["Parsek.Store"]["reassignable"], 3)


class ComplexityAndCouplingTests(unittest.TestCase):
    def test_complexity_folds_generated_code_and_the_gate_merges_overloads(self):
        branches = ["ldarg.0", "brtrue.s   IL_0000"] * 25
        il = cls("Parsek.Big", meth("Run", branches + ["ret"], params="bool a"),
                 meth("Run", branches + ["ret"], params="int32 a"), meth("Small", ["ret"]))
        result = analyse(il)
        self.assertEqual(result["complexity"]["atLeast"], {20: 2, 50: 0, 100: 0})
        il = cls("Parsek.Big", meth("Run", branches * 2 + ["ret"], params="bool a"),
                 meth("Run", branches * 2 + ["ret"], params="int32 a"))
        result = analyse(il)
        self.assertEqual(result["gates"]["complexity50"], ["Parsek.Big::Run"])
        self.assertEqual(result["complexity"]["methods"][0]["complexity"], 51)

    def test_the_fixture_folds_lambda_decisions(self):
        facts = ilfacts.build(test_ilfacts.fixture_parse())
        key = "Parsek.HeaviestSpawnBuildTypeExtensions::ToLogToken(Parsek.HeaviestSpawnBuildType)"
        self.assertEqual(ilanalysis.complexity(facts["methods"][key]), 4)
        # Emit's own body has no decision; its iterator's MoveNext has four.
        emit = facts["methods"]["Parsek.MapRender.TracedPhase::Emit(Parsek.MapRender.SampleContext)"]
        self.assertEqual((emit["decisions"], ilanalysis.complexity(emit)), (0, 5))

    def test_game_reach_follows_calls_and_dispatch_and_stops_at_seams(self):
        abstract = "public hidebysig newslot abstract virtual instance"
        il = (cls("Parsek.ILive", meth("Touch", (), flags=abstract), flags="public abstract auto ansi interface")
              + cls("Parsek.Live", meth("Touch", ["call       class ['Assembly-CSharp']FlightGlobals"
                                                  " ['Assembly-CSharp']FlightGlobals::get_fetch()", "pop", "ret"],
                                        flags="public hidebysig newslot virtual final instance"),
                    implements=["Parsek.ILive"])
              + cls("Parsek.Uses", meth("ViaInterface", ["ldnull", "callvirt   instance void Parsek.ILive::Touch()", "ret"]),
                    meth("ViaLog", [VOID_CALL % "Parsek.ParsekLog::Info", "ret"]),
                    meth("Math", ["call       valuetype [UnityEngine.CoreModule]UnityEngine.Vector3"
                                  " [UnityEngine.CoreModule]UnityEngine.Vector3::get_zero()", "pop", "ret"]),
                    meth("Euler", ["call       valuetype [UnityEngine.CoreModule]UnityEngine.Quaternion"
                                   " [UnityEngine.CoreModule]UnityEngine.Quaternion::Euler(float32,float32,float32)",
                                   "pop", "ret"]),
                    meth("TakesPart", params="class ['Assembly-CSharp']Part p"),
                    meth("Caller", [VOID_CALL % "Parsek.Uses::TakesPart", "ret"]))
              + cls("Parsek.ParsekLog", meth("Info", ["call       class ['Assembly-CSharp']FlightGlobals"
                                                     " ['Assembly-CSharp']FlightGlobals::get_fetch()", "pop", "ret"])))
        index = ilanalysis.Index(facts_of(il))
        direct, reach = ilanalysis.game_reach(index, CONFIG)
        self.assertIn("Parsek.Live::Touch()", direct)
        self.assertIn("Parsek.Uses::ViaInterface()", reach)
        self.assertIn("Parsek.ParsekLog::Info()", direct)
        self.assertNotIn("Parsek.Uses::ViaLog()", reach)  # not followed
        self.assertNotIn("Parsek.Uses::Math()", reach)  # an inert value type
        self.assertIn("Parsek.Uses::Euler()", direct)  # a live member of an inert type
        self.assertIn("Parsek.Uses::TakesPart(Part)", direct)  # the signature counts
        self.assertNotIn("Parsek.Uses::Caller()", reach)  # a TakesPart(null) call passes no Part

    def test_module_shares(self):
        il = (cls("Parsek.A", meth("Live", ["call       class ['Assembly-CSharp']FlightGlobals"
                                            " ['Assembly-CSharp']FlightGlobals::get_fetch()", "pop", "ret"]),
                  meth("Pure", ["ldc.i4.0", "pop", "ret"])))
        coupling = analyse(il)["coupling"]
        self.assertAlmostEqual(coupling["byModule"]["Core"]["share"], 0.5)

    def test_the_fixture_patch_reaches_the_game(self):
        facts = ilfacts.build(test_ilfacts.fixture_parse())
        _direct, reach = ilanalysis.game_reach(ilanalysis.Index(facts), CONFIG)
        self.assertIn("Parsek.Patches.GhostOrbitDominantBodyPatch::Prefix(OrbitDriver,bool&)", reach)
        self.assertNotIn("Parsek.Logistics.RouteIds::Short(string)", reach)


class TestReachTests(unittest.TestCase):
    def test_direct_calls_and_dispatch_are_credited(self):
        abstract = "public hidebysig newslot abstract virtual instance"
        branches = ["ldarg.0", "brtrue.s   IL_0000"] * 31
        il = (cls("Parsek.IJob", meth("Do", (), flags=abstract), flags="public abstract auto ansi interface")
              + cls("Parsek.Job", meth("Do", flags="public hidebysig newslot virtual final instance"),
                    implements=["Parsek.IJob"])
              + cls("Parsek.Hard", meth("Tangle", branches + ["ret"], params="bool a"), meth("Easy")))
        tests_il = cls("Parsek.Tests.T", meth("Fact", [
            "ldnull", "callvirt   instance void [Parsek]Parsek.IJob::Do()",
            "call       void [Parsek]Parsek.Hard::Easy()", "ret"]))
        reach = analyse(il, tests_il)["testReach"]
        row = reach["byModule"]["Core"]
        self.assertEqual((row["methods"], row["reachedMethods"]), (4, 3))
        self.assertEqual([r["key"] for r in reach["untestedComplex"]], ["Parsek.Hard::Tangle(bool)"])
        self.assertEqual(reach["unmatchedTestCalls"], [])


class RatchetTests(unittest.TestCase):
    PINS = {
        "crossTypeStaticWrites": {"bound": 2, "keys": ["A::x", "A::y"]},
        "uncalledInAssembly": {"bound": 1, "keys": ["B::M()"]},
        "complexity50": {"keys": ["C::Big", "C::Huge"]},
    }

    def gates(self, **changes):
        out = {"crossTypeStaticWrites": ["A::x", "A::y"], "uncalledInAssembly": ["B::M()"],
               "complexity50": ["C::Big", "C::Huge"]}
        out.update(changes)
        return out

    def test_the_pinned_tree_holds(self):
        self.assertEqual(ilanalysis.ratchet_problems(self.gates(), self.PINS), [])

    def test_a_rename_and_a_removal_do_not_red(self):
        for gates in (self.gates(crossTypeStaticWrites=["A::x", "A::renamed"]),
                      self.gates(crossTypeStaticWrites=["A::x"]), self.gates(uncalledInAssembly=[]),
                      self.gates(complexity50=["C::Big"])):
            with self.subTest(gates=gates):
                self.assertEqual(ilanalysis.ratchet_problems(gates, self.PINS), [])

    def test_a_rise_reds_naming_what_moved_and_how_to_repin(self):
        gates = self.gates(crossTypeStaticWrites=["A::x", "A::y", "D::z"], uncalledInAssembly=["B::M()", "E::N()"])
        problems = ilanalysis.ratchet_problems(gates, self.PINS, {"D::z": "written by F::G()"})
        self.assertEqual(len(problems), 2)
        self.assertIn("3, pinned bound 2 (+1)", problems[0])
        self.assertIn("D::z (written by F::G())", problems[0])
        self.assertIn("E::N()", problems[1])
        for problem in problems:
            self.assertIn("--write-pins", problem)

    def test_a_check_can_be_limited_to_one_ratchet(self):
        gates = self.gates(crossTypeStaticWrites=["A::x", "A::y", "D::z"], complexity50=["C::New"])
        self.assertEqual(ilanalysis.ratchet_problems(gates, self.PINS, names={"uncalledInAssembly"}), [])
        self.assertEqual(len(ilanalysis.ratchet_problems(gates, self.PINS, names={"complexity50"})), 1)

    def test_a_new_complex_method_reds_even_when_another_left(self):
        problems = ilanalysis.ratchet_problems(self.gates(complexity50=["C::Big", "C::New"]), self.PINS)
        self.assertEqual(len(problems), 1)
        self.assertIn("C::New", problems[0])

    def test_rendered_pins_read_back(self):
        pins = tomllib.loads(ilanalysis.render_pins(self.gates(), "Parsek.dll", "2026-11-11"))
        self.assertEqual(pins["crossTypeStaticWrites"], self.PINS["crossTypeStaticWrites"])
        self.assertEqual(pins["complexity50"], self.PINS["complexity50"])
        self.assertEqual(ilanalysis.ratchet_problems(self.gates(), pins), [])

    def test_the_committed_pins_are_consistent(self):
        pins = ilanalysis.load_pins()
        for name in ("crossTypeStaticWrites", "uncalledInAssembly"):
            with self.subTest(pin=name):
                self.assertEqual(pins[name]["bound"], len(pins[name]["keys"]))
                self.assertEqual(len(set(pins[name]["keys"])), len(pins[name]["keys"]))
        self.assertTrue(pins["complexity50"]["keys"])

    def test_the_config_names_every_list_the_rules_read(self):
        for section, keys in {
            "coupling": ("game_assemblies", "inert_types", "live_members", "not_followed"),
            "statics": ("mutating_members",),
            "uncalled": ("ignored_attributes", "patch_type_attributes", "patch_hooks", "patch_methods",
                         "unity_messages", "object_members", "test_hook_suffixes"),
        }.items():
            for key in keys:
                with self.subTest(key=key):
                    self.assertTrue(CONFIG[section][key])


class AtlasTests(unittest.TestCase):
    def test_the_section_says_not_measured_without_facts(self):
        self.assertIn("Not measured", archview._atlas_code_facts(None))

    def test_the_section_renders_a_report(self):
        il = cls("Parsek.Svc", meth("Dead"), meth("Live", ["call       class ['Assembly-CSharp']FlightGlobals"
                                                         " ['Assembly-CSharp']FlightGlobals::get_fetch()", "pop", "ret"]))
        tests_il = cls("Parsek.Tests.T", meth("Fact", ["call       void [Parsek]Parsek.Svc::Live()", "ret"]))
        text = archview._atlas_code_facts(analyse(il, tests_il))
        for needle in ("Core", "Svc::Dead", "Static state", "Uncalled code", "Test reach"):
            self.assertIn(needle, text)


class WorkflowTests(unittest.TestCase):
    def test_the_compiled_il_step_runs_these_cells(self):
        # The ratchets below skip without PARSEK_ARCH_IL; the step that sets it
        # must discover this file too (test_semantic guards the step itself).
        steps = test_semantic.workflow_steps(test_semantic.WORKFLOW.read_text(encoding="utf-8"))
        runs = [run for _name, _keys, env, run in steps if IL_ENV in env and "scripts/arch" in run]
        patterns = [m.group(1) for run in runs for m in [re.search(r"-p\s+['\"]?([^'\"\s]+)", run)] if m]
        self.assertTrue(any(fnmatch.fnmatch("test_ilanalysis.py", p) for p in patterns), patterns)


@unittest.skipUnless(os.environ.get(IL_ENV), "%s is not set (CI sets it after the build)" % IL_ENV)
class RealTreeRatchetTests(unittest.TestCase):
    """The three ratchets over the built Debug Parsek.dll, pinned in facts_pins.toml."""

    @classmethod
    def setUpClass(cls):
        il_path = Path(os.environ[IL_ENV])
        if not il_path.is_file():
            raise AssertionError("%s=%s does not exist (build Parsek first)" % (IL_ENV, il_path))
        facts, tooling = test_ilfacts.real_tree_facts(il_path)
        cls.result = ilanalysis.report(facts, tooling, CONFIG)
        cls.pins = ilanalysis.load_pins()
        cls.details = ilanalysis.gate_details(cls.result)
        report = io.StringIO()
        with contextlib.redirect_stdout(report):
            print(ilanalysis.format_report(cls.result, top=5))
            for line in ilanalysis.ratchet_slack(cls.result["gates"], cls.pins):
                print("  slack (re-pin to tighten): " + line)
        sys.stderr.write("\n" + report.getvalue() + "\n")

    def check(self, name):
        self.assertEqual(ilanalysis.ratchet_problems(self.result["gates"], self.pins, self.details, {name}), [])

    def test_statics_written_by_another_production_type(self):
        self.check("crossTypeStaticWrites")

    def test_no_new_method_at_complexity_50(self):
        self.check("complexity50")

    def test_uncalled_production_methods(self):
        self.check("uncalledInAssembly")

    def test_the_roots_keep_what_the_runtime_calls(self):
        listed = set(self.result["uncalled"]["uncalled"])
        for key in listed:
            name = key.split("::", 1)[1].split("(", 1)[0]
            with self.subTest(key=key):
                self.assertNotIn(name, (".cctor", "ToString", "Equals", "GetHashCode"))
        kept = self.result["uncalled"]["rootedBy"]
        self.assertTrue(kept["harmony patch method"])
        self.assertTrue(kept["unity message"])
        self.assertTrue(kept["dispatch from a used slot"])


if __name__ == "__main__":
    unittest.main()
