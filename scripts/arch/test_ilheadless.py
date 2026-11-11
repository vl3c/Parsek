"""Tests for the headless modules and their leak gate (headless.py).

The declaration cells and the pins' shape need no DLL: the committed
`[headless.modules]` names real production modules with a reason each, and the
committed pins record the declared module list and the facts.toml [coupling]
lists, so a module dropped from the declaration or a widened inert list reds
the DLL-free suite until a re-pin. The rule cells build small ikdasm-shaped
assemblies inline (test_ilanalysis' helpers) and show each kind of leak, what
is not a leak, and that the generated-code shapes a compiler may choose give
the same leaks. The real-tree cells need PARSEK_ARCH_IL, like test_semantic's;
CI's compiled-IL step runs them over the Debug build, from the report
test_ilanalysis already holds, and mutates the real facts to show each gate
reds on the change it exists for and passes the ones it must not.
"""

import contextlib
import copy
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
import headless  # noqa: E402
import ilanalysis  # noqa: E402
import ilfacts  # noqa: E402
import ilgraph  # noqa: E402
import layers  # noqa: E402
import test_ilanalysis  # noqa: E402
import test_ilfacts  # noqa: E402
import test_semantic  # noqa: E402
from test_ilanalysis import cls, ctor, meth  # noqa: E402

IL_ENV = test_semantic.IL_ENV
CONFIG = ilanalysis.load_config()
HEADLESS = ["Rules"]
FETCH = "call       class ['Assembly-CSharp']FlightGlobals ['Assembly-CSharp']FlightGlobals::get_fetch()"
GENERATED = "auto ansi sealed nested private beforefieldinit"
GENERATED_ATTR = ["  .custom instance void [mscorlib]System.Runtime.CompilerServices.CompilerGeneratedAttribute"
                  "::.ctor() = ( 01 00 00 00 )"]
INSTANCE = "public hidebysig newslot virtual final instance"
ABSTRACT = "public hidebysig newslot abstract virtual instance"
INTERFACE = "public abstract auto ansi interface"


def lambda_shape(shape):
    """Parsek.Pure::WithLambda naming FlightGlobals only inside generated code, in
    one of the shapes a compiler emits: a `<>c` lambda cache, a closure class or
    a local function."""
    if shape == "cache":
        nested = cls("'<>c'", "  .field public static initonly class Parsek.Pure/'<>c' '<>9'",
                     meth(".cctor", ["newobj     instance void Parsek.Pure/'<>c'::.ctor()",
                                     "stsfld     class Parsek.Pure/'<>c' Parsek.Pure/'<>c'::'<>9'", "ret"],
                          flags="private hidebysig specialname rtspecialname static"),
                     ctor(), meth("'<WithLambda>b__0_0'", [FETCH, "pop", "ret"], flags="assembly hidebysig instance"),
                     flags=GENERATED, attrs=GENERATED_ATTR)
        body = ["ldsfld     class Parsek.Pure/'<>c' Parsek.Pure/'<>c'::'<>9'",
                "ldftn      instance void Parsek.Pure/'<>c'::'<WithLambda>b__0_0'()",
                "newobj     instance void [mscorlib]System.Action::.ctor(object, native int)", "pop", "ret"]
        return nested, body
    if shape == "closure":
        nested = cls("'<>c__DisplayClass0_0'", "  .field public int32 x", ctor(),
                     meth("'<WithLambda>b__0'", [FETCH, "pop", "ret"], flags="assembly hidebysig instance"),
                     flags=GENERATED, attrs=GENERATED_ATTR)
        body = ["newobj     instance void Parsek.Pure/'<>c__DisplayClass0_0'::.ctor()",
                "ldftn      instance void Parsek.Pure/'<>c__DisplayClass0_0'::'<WithLambda>b__0'()",
                "newobj     instance void [mscorlib]System.Action::.ctor(object, native int)", "pop", "ret"]
        return nested, body
    nested = meth("'<WithLambda>g__Local|0_0'", [FETCH, "pop", "ret"], flags="assembly hidebysig static",
                  attrs=GENERATED_ATTR)
    return nested, ["call       void Parsek.Pure::'<WithLambda>g__Local|0_0'()", "ret"]


def fixture_il(shape="cache", extra_pure=(), pure_overrides=None):
    """Rules (headless): Pure and Rules2 and the slot IClock. Ctl: Settings, the
    slot IHost and the live implementations of both slots. LogIO: ParsekLog."""
    nested, lambda_body = lambda_shape(shape)
    pure_methods = {
        "Add": meth("Add", ["ldc.i4.1", "ldc.i4.2", "add", "pop", "ret"]),
        "UsesVector": meth("UsesVector", ["call       valuetype [UnityEngine.CoreModule]UnityEngine.Vector3"
                                          " [UnityEngine.CoreModule]UnityEngine.Vector3::get_zero()", "pop", "ret"]),
        "UsesConfigNode": meth("UsesConfigNode", ["newobj     instance void ['Assembly-CSharp']ConfigNode::.ctor()",
                                                  "pop", "ret"]),
        "Euler": meth("Euler", ["ldc.r4     0.0", "ldc.r4     0.0", "ldc.r4     0.0",
                                "call       valuetype [UnityEngine.CoreModule]UnityEngine.Quaternion"
                                " [UnityEngine.CoreModule]UnityEngine.Quaternion::Euler(float32,float32,float32)",
                                "pop", "ret"]),
        "Logs": meth("Logs", ["call       void Parsek.ParsekLog::Info()", "ret"]),
        "CallsHelper": meth("CallsHelper", ["call       void Parsek.Rules2::Touch()", "ret"]),
        "ReadsSettings": meth("ReadsSettings", ["call       object Parsek.Settings::get_Current()", "pop", "ret"]),
        "CallsCtlPure": meth("CallsCtlPure", ["call       void Parsek.Settings::Pure()", "ret"]),
        "UsesClock": meth("UsesClock", ["ldnull", "callvirt   instance void Parsek.IClock::Now()", "ret"]),
        "UsesHost": meth("UsesHost", ["ldnull", "callvirt   instance void Parsek.IHost::Poke()", "ret"]),
        "WithLambda": meth("WithLambda", lambda_body),
    }
    pure_methods.update(pure_overrides or {})
    pure_methods = {name: text for name, text in pure_methods.items() if text is not None}
    return "\n".join([
        cls("Parsek.Pure", nested, *pure_methods.values(), *extra_pure),
        cls("Parsek.Rules2", meth("Touch", [FETCH, "pop", "ret"]),
            meth("TakesVessel", params="class ['Assembly-CSharp']Vessel v")),
        cls("Parsek.IClock", meth("Now", (), flags=ABSTRACT), flags=INTERFACE),
        cls("Parsek.Settings", meth("get_Current", ["call       class ['Assembly-CSharp']HighLogic"
                                                    " ['Assembly-CSharp']HighLogic::get_fetch()", "ret"],
                                    ret="object"),
            meth("Pure")),
        cls("Parsek.IHost", meth("Poke", (), flags=ABSTRACT), flags=INTERFACE),
        cls("Parsek.LiveClock", meth("Now", ["call       float64 ['Assembly-CSharp']Planetarium::GetUniversalTime()",
                                             "pop", "ret"], flags=INSTANCE), implements=["Parsek.IClock"]),
        cls("Parsek.LiveHost", meth("Poke", [FETCH, "pop", "ret"], flags=INSTANCE), implements=["Parsek.IHost"]),
        cls("Parsek.ParsekLog", meth("Info", [FETCH, "pop", "ret"])),
    ])


MODULES = {"Parsek.Pure": "Rules", "Parsek.Rules2": "Rules", "Parsek.IClock": "Rules", "Parsek.Settings": "Ctl",
           "Parsek.IHost": "Ctl", "Parsek.LiveClock": "Ctl", "Parsek.LiveHost": "Ctl", "Parsek.ParsekLog": "LogIO"}


def fixture_facts(il=None):
    parsed = ilgraph.parse_il(il or fixture_il())
    table = {path: {MODULES[path]} for path in parsed["typeInfo"] if path in MODULES}
    return ilfacts.build(parsed, table)


def run(facts=None, modules=HEADLESS, config=CONFIG):
    return headless.analyse(ilanalysis.Index(facts or fixture_facts(), ()), config, modules)


EXPECTED_LEAKS = [
    "Parsek.Pure::Euler -> game:UnityEngine.Quaternion::Euler",
    "Parsek.Pure::ReadsSettings -> call:Parsek.Settings::get_Current",
    "Parsek.Pure::UsesHost -> call:Parsek.IHost::Poke",
    "Parsek.Pure::WithLambda -> game:FlightGlobals",
    "Parsek.Rules2::TakesVessel -> game:Vessel",
    "Parsek.Rules2::Touch -> game:FlightGlobals",
]


def pins_for(result, config=CONFIG):
    return tomllib.loads(headless.render_pins(result, config, "Parsek.dll", "2026-11-11"))


def with_method(facts, key, entry):
    """A shallow copy of the facts with one method entry replaced, added or (None) removed."""
    out = dict(facts)
    out["methods"] = dict(facts["methods"])
    if entry is None:
        del out["methods"][key]
    else:
        out["methods"][key] = entry
    return out


def facts_with(il, extra_modules=None):
    """fixture_facts over `il` with more types placed in modules."""
    parsed = ilgraph.parse_il(il)
    modules = dict(MODULES, **(extra_modules or {}))
    table = {path: {modules[path]} for path in parsed["typeInfo"] if path in modules}
    return ilfacts.build(parsed, table)


def new_leaks(result):
    return sorted(set(result["leaks"]["Rules"]) - set(EXPECTED_LEAKS))


# Generated code ilfacts cannot give to one source method stays on the
# `Type::(generated)` pseudo-method; two ordinary C# shapes produce it.
NESTED_LAMBDA = [
    # `Outer` holds `xs.Where(a => a.Parts.Any(p => p.vessel == FlightGlobals.ActiveVessel))`:
    # the inner lambda is referenced only from inside `<>c`, which `Other` shares.
    cls("'<>c'", "  .field public static initonly class Parsek.Pure/'<>c' '<>9'",
        meth(".cctor", ["newobj     instance void Parsek.Pure/'<>c'::.ctor()",
                        "stsfld     class Parsek.Pure/'<>c' Parsek.Pure/'<>c'::'<>9'", "ret"],
             flags="private hidebysig specialname rtspecialname static"),
        ctor(),
        meth("'<Outer>b__1_0'", ["ldsfld     class Parsek.Pure/'<>c' Parsek.Pure/'<>c'::'<>9'",
                                 "ldftn      instance void Parsek.Pure/'<>c'::'<Outer>b__1_1'()",
                                 "newobj     instance void [mscorlib]System.Action::.ctor(object, native int)",
                                 "pop", "ret"], flags="assembly hidebysig instance"),
        meth("'<Outer>b__1_1'", [FETCH, "pop", "ret"], flags="assembly hidebysig instance"),
        meth("'<Other>b__2_0'", ["ret"], flags="assembly hidebysig instance"),
        flags=GENERATED, attrs=GENERATED_ATTR),
    meth("Outer", ["ldsfld     class Parsek.Pure/'<>c' Parsek.Pure/'<>c'::'<>9'",
                   "ldftn      instance void Parsek.Pure/'<>c'::'<Outer>b__1_0'()",
                   "newobj     instance void [mscorlib]System.Action::.ctor(object, native int)", "pop", "ret"]),
    meth("Other", ["ldsfld     class Parsek.Pure/'<>c' Parsek.Pure/'<>c'::'<>9'",
                   "ldftn      instance void Parsek.Pure/'<>c'::'<Other>b__2_0'()",
                   "newobj     instance void [mscorlib]System.Action::.ctor(object, native int)", "pop", "ret"]),
]


def field_initializer_type(constructors=2):
    """Parsek.Init (headless) with `Action f = () => FlightGlobals...;`, a field
    initializer the compiler emits into every constructor."""
    cache = cls("'<>c'", "  .field public static initonly class Parsek.Init/'<>c' '<>9'",
                meth(".cctor", ["newobj     instance void Parsek.Init/'<>c'::.ctor()",
                                "stsfld     class Parsek.Init/'<>c' Parsek.Init/'<>c'::'<>9'", "ret"],
                     flags="private hidebysig specialname rtspecialname static"),
                ctor(), meth("'<.ctor>b__1_0'", [FETCH, "pop", "ret"], flags="assembly hidebysig instance"),
                flags=GENERATED, attrs=GENERATED_ATTR)
    body = ["ldarg.0", "ldsfld     class Parsek.Init/'<>c' Parsek.Init/'<>c'::'<>9'",
            "ldftn      instance void Parsek.Init/'<>c'::'<.ctor>b__1_0'()",
            "newobj     instance void [mscorlib]System.Action::.ctor(object, native int)",
            "stfld      class [mscorlib]System.Action Parsek.Init::f",
            "ldarg.0", "call       instance void [mscorlib]System.Object::.ctor()", "ret"]
    flags = "public hidebysig specialname rtspecialname instance"
    ctors = [meth(".ctor", body, flags=flags), meth(".ctor", body, flags=flags, params="int32 x")][:constructors]
    return cls("Parsek.Init", cache, "  .field private class [mscorlib]System.Action f", *ctors)


# A live implementation of the headless slot IClock, outside the headless modules,
# that a headless method can reach without an injected instance.
KSP_CLOCK = cls("Parsek.KspClock", "  .field public static initonly class Parsek.KspClock Instance", ctor(),
                meth("Create", ["newobj     instance void Parsek.KspClock::.ctor()", "ret"],
                     ret="class Parsek.KspClock"),
                meth("Now", ["call       float64 ['Assembly-CSharp']Planetarium::GetUniversalTime()", "pop", "ret"],
                     flags=INSTANCE), implements=["Parsek.IClock"])
CLOCK_NOW = "callvirt   instance void Parsek.IClock::Now()"


def clock_run(add_body, extra_pure=()):
    il = fixture_il("closure", extra_pure=extra_pure, pure_overrides={"Add": meth("Add", add_body)}) + "\n" + KSP_CLOCK
    return run(facts_with(il, {"Parsek.KspClock": "Ctl"}))


# A live implementation handed back by code outside the headless modules: a
# factory on a third type, a locator calling that factory, and a subclass that
# inherits its base's live fill. Every type here is in the live module "Ctl".
LIVE_NOW = ["call       float64 ['Assembly-CSharp']Planetarium::GetUniversalTime()", "pop", "ret"]
SERVICES = cls("Parsek.Services", "  .field private static class Parsek.IClock injected",
               meth("get_Clock", ["newobj     instance void Parsek.KspClock::.ctor()", "ret"],
                    ret="class Parsek.IClock"),
               meth("get_Injected", ["ldsfld     class Parsek.IClock Parsek.Services::injected", "ret"],
                    ret="class Parsek.IClock"),
               meth("Inject", ["ldarg.0", "stsfld     class Parsek.IClock Parsek.Services::injected", "ret"],
                    params="class Parsek.IClock clock"),
               meth("Format", ["ldc.i4.0", "pop", "ret"]))
LOCATOR = cls("Parsek.Locator", meth("Find", ["call       class Parsek.IClock Parsek.Services::get_Clock()", "ret"],
                                     ret="class Parsek.IClock"))
CLOCK_BASE = cls("Parsek.KspClockBase", ctor(),
                 meth("Now", LIVE_NOW, flags="public hidebysig newslot virtual instance"), implements=["Parsek.IClock"])
CLOCK_INHERITS = cls("Parsek.KspClock2", ctor("Parsek.KspClockBase"), extends="Parsek.KspClockBase")
CLOCK_PURE_OVERRIDE = cls("Parsek.KspClock4", ctor("Parsek.KspClockBase"),
                          meth("Now", ["ret"], flags="public hidebysig virtual instance"),
                          extends="Parsek.KspClockBase")
WIRING_TYPES = (SERVICES, LOCATOR, CLOCK_BASE, CLOCK_INHERITS, CLOCK_PURE_OVERRIDE)


def wiring_run(add_body):
    il = "\n".join([fixture_il("closure", pure_overrides={"Add": meth("Add", add_body)}), KSP_CLOCK]
                    + list(WIRING_TYPES))
    names = ["Parsek.KspClock", "Parsek.Services", "Parsek.Locator", "Parsek.KspClockBase", "Parsek.KspClock2",
             "Parsek.KspClock4"]
    return run(facts_with(il, {name: "Ctl" for name in names}))


# --- the declaration, no DLL ---------------------------------------------------------------


class DeclarationTests(unittest.TestCase):
    PRODUCTION = {"Core", "Trajectory", "UI"}

    def test_a_well_formed_declaration_has_no_problem(self):
        table = {"modules": {"Core": "the kernel", "Trajectory": "math"}}
        self.assertEqual(headless.declaration_problems(table, self.PRODUCTION, {"Tools"}), [])
        self.assertEqual(headless.declared(table), ["Core", "Trajectory"])

    def test_every_malformed_declaration_reds(self):
        cases = {
            "missing": ({}, "[headless] is missing"),
            "no modules": ({"modules": {}}, "must map at least one module"),
            "a list, not a table": ({"modules": ["Core"]}, "must map at least one module"),
            "a name no rule declares": ({"modules": {"Kore": "typo"}}, "Kore names no module"),
            "a tooling module": ({"modules": {"Tools": "r"}}, "Tools is a tooling module"),
            "no reason": ({"modules": {"Core": " "}}, "Core has no reason"),
            "an unknown key": ({"modules": {"Core": "r"}, "modulse": {}}, "unknown key 'modulse'"),
        }
        for label, (table, needle) in cases.items():
            with self.subTest(case=label):
                problems = headless.declaration_problems(table, self.PRODUCTION, {"Tools"})
                self.assertTrue(any(needle in p for p in problems), problems)


class CommittedDeclarationTests(unittest.TestCase):
    """The declaration and the pins as committed (no DLL)."""

    def test_the_declaration_names_production_modules_with_reasons(self):
        production, tooling = layers.declared_modules()
        table = headless.load_headless()
        self.assertEqual(headless.declaration_problems(table, production, tooling), [])
        self.assertIn("Core", headless.declared(table))

    def test_the_pins_match_the_declaration_and_the_coupling_lists(self):
        self.assertEqual(headless.pins_problems(headless.load_pins(), headless.declared(headless.load_headless()),
                                                CONFIG), [])

    def test_a_widened_inert_list_reds_until_a_repin(self):
        pins = headless.load_pins()
        modules = headless.declared(headless.load_headless())
        for key, value in (("inert_types", "FlightGlobals"), ("live_members", "UnityEngine.Mathf::Abs"),
                           ("not_followed", "Parsek.ParsekSettings"), ("game_assemblies", "Extra")):
            with self.subTest(key=key):
                config = copy.deepcopy(CONFIG)
                config["coupling"][key] = list(config["coupling"][key]) + [value]
                problems = headless.pins_problems(pins, modules, config)
                self.assertEqual(len(problems), 1, problems)
                self.assertIn("[coupling] %s changed since the pin (added: %s" % (key, value), problems[0])
                self.assertIn("--write-pins", problems[0])
        config = copy.deepcopy(CONFIG)
        config["coupling"]["game_assemblies"] = [a for a in config["coupling"]["game_assemblies"]
                                                 if a != "Assembly-CSharp"]
        problems = headless.pins_problems(pins, modules, config)
        self.assertTrue(any("removed: Assembly-CSharp" in p for p in problems), problems)

    def test_a_module_left_out_or_added_without_a_repin_reds(self):
        pins = headless.load_pins()
        modules = headless.declared(headless.load_headless())
        for changed in (modules[1:], modules + ["Rendering"]):
            with self.subTest(modules=changed):
                problems = headless.pins_problems(pins, changed, CONFIG)
                self.assertEqual(len(problems), 1, problems)
                self.assertIn("are not the declared [headless.modules]", problems[0])

    def test_malformed_pins_red(self):
        pins = pins_for(run())
        pins["module"].append({"name": "Rules", "leaks": []})
        self.assertTrue(any("twice" in p for p in headless.pins_problems(pins, HEADLESS, CONFIG)))
        pins = pins_for(run())
        pins["module"][0]["leaks"] += ["Parsek.Pure::Add -> FlightGlobals", "Parsek.Pure::Add -> FlightGlobals"]
        problems = headless.pins_problems(pins, HEADLESS, CONFIG)
        self.assertTrue(any("lists a leak twice" in p for p in problems), problems)
        self.assertTrue(any("is not `Type::Name -> game:X`" in p for p in problems), problems)


# --- the rule over inline IL ---------------------------------------------------------------


class LeakRuleTests(unittest.TestCase):
    def test_the_fixture_leaks(self):
        result = run()
        self.assertEqual(result["leaks"], {"Rules": EXPECTED_LEAKS})
        self.assertEqual(result["seams"], {"Rules": ["Parsek.Pure::UsesClock -> Parsek.IClock::Now"]})
        self.assertEqual(result["unexplained"], [])
        details = result["details"]
        self.assertEqual(details["Parsek.Rules2::Touch -> game:FlightGlobals"], "names FlightGlobals")
        self.assertIn("reaches the game: game:HighLogic",
                      details["Parsek.Pure::ReadsSettings -> call:Parsek.Settings::get_Current"])
        self.assertIn("Parsek.LiveHost::Poke", details["Parsek.Pure::UsesHost -> call:Parsek.IHost::Poke"])

    def test_what_is_not_a_leak(self):
        index = ilanalysis.Index(fixture_facts(), ())
        _direct, reach = ilanalysis.game_reach(index, CONFIG)
        leaking = {leak.split(" -> ")[0] for leak in run()["leaks"]["Rules"]}
        for name, why in (("Add", "pure"), ("UsesVector", "an inert value type"), ("UsesConfigNode", "inert"),
                          ("Logs", "ParsekLog is not followed"), ("CallsCtlPure", "a pure method of a live module")):
            with self.subTest(name=name, why=why):
                self.assertNotIn("Parsek.Pure::%s()" % name, reach)
                self.assertNotIn("Parsek.Pure::" + name, leaking)
        # A live callee inside the headless modules: CallsHelper can reach the game,
        # through Rules2::Touch, whose own leak is the one listed.
        self.assertIn("Parsek.Pure::CallsHelper()", reach)
        self.assertNotIn("Parsek.Pure::CallsHelper", leaking)
        # A slot declared in a headless module is a seam, whatever fills it.
        self.assertIn("Parsek.Pure::UsesClock()", reach)
        self.assertNotIn("Parsek.Pure::UsesClock", leaking)

    def test_every_compiler_shape_of_generated_code_gives_the_same_leaks(self):
        results = {shape: run(fixture_facts(fixture_il(shape))) for shape in ("cache", "closure", "local")}
        for shape, result in results.items():
            with self.subTest(shape=shape):
                self.assertEqual(result["leaks"], {"Rules": EXPECTED_LEAKS})
                self.assertEqual(result["unexplained"], [])

    def test_the_whole_module_set_is_pure_exactly_when_it_has_no_leak(self):
        # Delete the six leaking methods and the slot implementations stay: no
        # headless method can reach the game any more, except through the seam.
        facts = fixture_facts()
        for key in [k for k in facts["methods"] if headless.method_name(k) in
                    {leak.split(" -> ")[0] for leak in EXPECTED_LEAKS}]:
            facts = with_method(facts, key, None)
        index = ilanalysis.Index(facts, ())
        _direct, reach = ilanalysis.game_reach(index, CONFIG)
        result = headless.analyse(index, CONFIG, HEADLESS)
        self.assertEqual(result["leaks"], {"Rules": []})
        live = sorted(k for k in reach if index.module(index.methods[k]["type"]) == "Rules")
        self.assertEqual(live, ["Parsek.Pure::UsesClock()"])

    def test_the_counts_per_module(self):
        rows = run()["byModule"]
        self.assertTrue(rows["Rules"]["headless"])
        self.assertEqual(rows["Rules"]["leaks"], 6)
        self.assertEqual(rows["Rules"]["leakingMethods"], 6)
        self.assertFalse(rows["Ctl"]["headless"])
        self.assertIsNone(rows["Ctl"]["leaks"])
        self.assertEqual((rows["Ctl"]["methods"], rows["Ctl"]["reachMethods"], rows["Ctl"]["directMethods"]),
                         (5, 3, 3))


class HiddenLiveCodeTests(unittest.TestCase):
    """Live code a headless method reaches without naming it: generated code no
    single method owns, and a live implementation of a headless slot hard-wired
    by construction or through a static. Each reds; an injected one stays a seam."""

    def problems(self, result):
        return headless.ratchet_problems(result["leaks"], pins_for(run()), result["details"])

    def test_a_nested_lambda_left_to_its_type_reds(self):
        result = run(fixture_facts(fixture_il("closure", extra_pure=NESTED_LAMBDA)))
        self.assertEqual(new_leaks(result), ["Parsek.Pure::(generated) -> game:FlightGlobals"])
        self.assertEqual(result["unexplained"], [])
        problems = self.problems(result)
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("Parsek.Pure::(generated) -> game:FlightGlobals (names FlightGlobals)", problems[0])

    def test_a_field_initializer_lambda_in_two_constructors_reds(self):
        il = fixture_il("closure") + "\n" + field_initializer_type(2)
        result = run(facts_with(il, {"Parsek.Init": "Rules"}))
        self.assertEqual(new_leaks(result), ["Parsek.Init::(generated) -> game:FlightGlobals"])
        self.assertEqual(len(self.problems(result)), 1)
        # With one constructor the initializer has one owner, and the leak is its.
        il = fixture_il("closure") + "\n" + field_initializer_type(1)
        self.assertEqual(new_leaks(run(facts_with(il, {"Parsek.Init": "Rules"}))),
                         ["Parsek.Init::.ctor -> game:FlightGlobals"])

    def test_a_pseudo_method_names_no_leak_when_its_code_is_pure(self):
        il = fixture_il("closure", extra_pure=[part.replace(FETCH, "ldnull") for part in NESTED_LAMBDA])
        result = run(fixture_facts(il))
        self.assertEqual(new_leaks(result), [])
        self.assertEqual(self.problems(result), [])

    def test_a_live_slot_implementation_hard_wired_reds_in_every_form(self):
        forms = {
            "constructed, then called through the slot": (
                ["newobj     instance void Parsek.KspClock::.ctor()", CLOCK_NOW, "ret"],
                "Parsek.Pure::Add -> call:Parsek.KspClock::.ctor", "constructs Parsek.KspClock::.ctor"),
            "read from its static singleton": (
                ["ldsfld     class Parsek.KspClock Parsek.KspClock::Instance", CLOCK_NOW, "ret"],
                "Parsek.Pure::Add -> static:Parsek.KspClock::Instance", "reads the static Parsek.KspClock::Instance"),
            "made by its static factory": (
                ["call       class Parsek.KspClock Parsek.KspClock::Create()", CLOCK_NOW, "ret"],
                "Parsek.Pure::Add -> call:Parsek.KspClock::Create", "calls the static Parsek.KspClock::Create"),
        }
        for label, (body, leak, detail) in forms.items():
            with self.subTest(form=label):
                result = clock_run(body)
                self.assertEqual(new_leaks(result), [leak])
                self.assertIn(detail, result["details"][leak])
                self.assertIn("fills the headless slot Parsek.IClock::Now with live code", result["details"][leak])
                problems = self.problems(result)
                self.assertEqual(len(problems), 1, problems)
                self.assertIn(leak, problems[0])
        # The pins accept the third leak kind.
        pins = pins_for(clock_run(forms["read from its static singleton"][0]))
        self.assertEqual(headless.pins_problems(pins, HEADLESS, CONFIG), [])

    def test_an_injected_implementation_stays_a_seam(self):
        cases = {
            "a parameter": (meth("Injected", ["ldarg.0", CLOCK_NOW, "ret"], params="class Parsek.IClock clock"), []),
            "an instance field": (meth("Injected", ["ldarg.0", "ldfld      class Parsek.IClock Parsek.Pure::clock",
                                                    CLOCK_NOW, "ret"], flags="public hidebysig instance"),
                                  ["  .field private class Parsek.IClock clock"]),
        }
        for label, (method, fields) in cases.items():
            with self.subTest(injected=label):
                result = clock_run(["ldc.i4.0", "pop", "ret"], extra_pure=fields + [method])
                self.assertEqual(new_leaks(result), [])
                self.assertIn("Parsek.Pure::Injected -> Parsek.IClock::Now", result["seams"]["Rules"])
                self.assertEqual(self.problems(result), [])
                self.assertEqual(result["unexplained"], [])

    def test_a_live_implementation_handed_back_by_other_live_code_reds(self):
        forms = {
            "a factory on a third type": (
                "call       class Parsek.IClock Parsek.Services::get_Clock()",
                "Parsek.Pure::Add -> call:Parsek.Services::get_Clock", "Parsek.Services::get_Clock"),
            "a locator calling that factory (two hops)": (
                "call       class Parsek.IClock Parsek.Locator::Find()",
                "Parsek.Pure::Add -> call:Parsek.Locator::Find",
                "Parsek.Locator::Find -> Parsek.Services::get_Clock"),
            "a subclass inheriting a live fill": (
                "newobj     instance void Parsek.KspClock2::.ctor()",
                "Parsek.Pure::Add -> call:Parsek.KspClock2::.ctor", "constructs Parsek.KspClock2::.ctor"),
        }
        for label, (call, leak, detail) in forms.items():
            with self.subTest(form=label):
                result = wiring_run([call, CLOCK_NOW, "ret"])
                self.assertEqual(new_leaks(result), [leak])
                self.assertIn(detail, result["details"][leak])
                self.assertIn("with live code", result["details"][leak])
                self.assertIn("Parsek.Pure::Add -> Parsek.IClock::Now", result["seams"]["Rules"])
                problems = self.problems(result)
                self.assertEqual(len(problems), 1, problems)
                self.assertIn(leak, problems[0])

    def test_live_code_that_hands_back_no_live_implementation_stays_out(self):
        forms = {
            "a live-module helper that constructs and reads nothing live": [
                "call       void Parsek.Services::Format()", "ret"],
            "a subclass overriding the live fill with pure code": [
                "newobj     instance void Parsek.KspClock4::.ctor()", CLOCK_NOW, "ret"],
            # Injection through a static the slot type declares, assigned elsewhere
            # (Services.Inject): a seam, the rule's known limit.
            "a slot-typed static assigned elsewhere": [
                "call       class Parsek.IClock Parsek.Services::get_Injected()", CLOCK_NOW, "ret"],
        }
        for label, body in forms.items():
            with self.subTest(form=label):
                result = wiring_run(body)
                self.assertEqual(new_leaks(result), [])
                self.assertEqual(self.problems(result), [])
                self.assertEqual(result["unexplained"], [])


class RatchetTests(unittest.TestCase):
    def facts(self):
        return fixture_facts()

    def check(self, facts, pins=None):
        result = run(facts)
        return headless.ratchet_problems(result["leaks"], pins or pins_for(run()), result["details"]), result

    def test_the_pins_read_back_and_hold(self):
        pins = pins_for(run())
        self.assertEqual(sorted(headless.pinned(pins)["Rules"]), EXPECTED_LEAKS)
        self.assertEqual(pins["coupling"], headless.coupling_lists(CONFIG))
        self.assertEqual(self.check(self.facts(), pins)[0], [])
        self.assertEqual(headless.ratchet_slack(run()["leaks"], pins), [])
        self.assertEqual(headless.pins_problems(pins, HEADLESS, CONFIG), [])

    def test_a_headless_method_newly_naming_a_game_type_reds(self):
        il = fixture_il(pure_overrides={"Add": meth("Add", [FETCH, "pop", "ret"])})
        problems, _result = self.check(fixture_facts(il))
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("headless module Rules: 1 new leak", problems[0])
        self.assertIn("Parsek.Pure::Add -> game:FlightGlobals (names FlightGlobals)", problems[0])
        for needle in ("injected seam", "inert_types", "--write-pins"):
            self.assertIn(needle, problems[0])

    def test_a_headless_method_newly_calling_a_live_method_elsewhere_reds(self):
        il = fixture_il(pure_overrides={"Add": meth("Add", ["call       object Parsek.Settings::get_Current()",
                                                             "pop", "ret"])})
        problems, _result = self.check(fixture_facts(il))
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("Parsek.Pure::Add -> call:Parsek.Settings::get_Current (calls Parsek.Settings::get_Current,"
                      " which reaches the game: game:HighLogic)", problems[0])

    def test_a_rename_of_a_leaking_method_reds(self):
        # The pins are a set keyed by name: the renamed method's leak is new.
        il = fixture_il().replace("Touch", "Poked")
        problems, _result = self.check(fixture_facts(il))
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("Parsek.Rules2::Poked -> game:FlightGlobals", problems[0])

    def test_what_must_not_red(self):
        cases = {
            "a pure method added": fixture_il(extra_pure=[meth("Fresh", ["ldc.i4.0", "pop", "ret"])]),
            "a call to an inert type added": fixture_il(pure_overrides={"Add": meth("Add", [
                "call       valuetype [UnityEngine.CoreModule]UnityEngine.Vector3"
                " [UnityEngine.CoreModule]UnityEngine.Vector3::get_up()", "pop",
                "newobj     instance void ['Assembly-CSharp']ConfigNode::.ctor()", "pop", "ret"])}),
            "a call to a live headless method added": fixture_il(pure_overrides={"Add": meth("Add", [
                "call       void Parsek.Rules2::Touch()", "ret"])}),
            "a call through the headless seam added": fixture_il(pure_overrides={"Add": meth("Add", [
                "ldnull", "callvirt   instance void Parsek.IClock::Now()", "ret"])}),
            "a leaking method's parameters changed": fixture_il().replace(
                ".method public hidebysig static void Touch()", ".method public hidebysig static void Touch(int32 a)"
            ).replace("Parsek.Rules2::Touch()", "Parsek.Rules2::Touch(int32)"),
            "a leaking method deleted": fixture_il(pure_overrides={"Euler": None}),
            "the closure shape changed": fixture_il("closure"),
        }
        for label, il in cases.items():
            with self.subTest(case=label):
                self.assertEqual(self.check(fixture_facts(il))[0], [])

    def test_a_deleted_leak_is_slack_until_a_repin(self):
        pins = pins_for(run())
        facts = with_method(self.facts(), "Parsek.Rules2::Touch()", None)
        result = run(facts)
        self.assertEqual(headless.ratchet_problems(result["leaks"], pins, result["details"]), [])
        self.assertEqual(headless.ratchet_slack(result["leaks"], pins),
                         ["Rules: 1 pinned leak gone: Parsek.Rules2::Touch -> game:FlightGlobals"])

    def test_a_module_new_to_the_declaration_reds_every_leak(self):
        pins = pins_for(run())
        result = run(modules=["Rules", "Ctl"])
        problems = headless.ratchet_problems(result["leaks"], pins, result["details"])
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("headless module Ctl: 3 new leaks", problems[0])

    def test_a_widened_inert_list_hides_leaks_from_the_ratchet_alone(self):
        # Why the pins record the coupling lists: with FlightGlobals inert the
        # set ratchet passes (leaks only vanish), and pins_problems is what reds.
        pins = pins_for(run())
        config = copy.deepcopy(CONFIG)
        config["coupling"]["inert_types"] = list(config["coupling"]["inert_types"]) + ["FlightGlobals"]
        result = run(config=config)
        self.assertNotIn("Parsek.Rules2::Touch -> game:FlightGlobals", result["leaks"]["Rules"])
        self.assertEqual(headless.ratchet_problems(result["leaks"], pins, result["details"]), [])
        self.assertEqual(len(headless.pins_problems(pins, HEADLESS, config)), 1)

    def test_the_cli_refuses_a_release_pin(self):
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            headless.main(["Source/Parsek/bin/Release/Parsek.dll", "--write-pins"])


class ViewTests(unittest.TestCase):
    def report(self):
        report = test_ilanalysis.analyse(fixture_il())
        report["headless"] = run()
        return report

    def test_the_summary(self):
        head = headless.summary(run())
        self.assertEqual((head["leaks"], head["leakingMethods"], head["seams"]), (6, 6, 1))
        self.assertEqual(head["topThings"][0], ["game:FlightGlobals", 2])
        self.assertIsNone(headless.summary(None))

    def test_the_report_lines(self):
        text = "\n".join(headless.format_report(run()))
        self.assertIn("Rules *", text)
        self.assertIn("leaks: 6 (6 methods)", text)
        self.assertNotIn("PROBLEM", text)

    def test_the_check_section_and_the_atlas(self):
        summary = headless.summary(run())
        captured = io.StringIO()
        with contextlib.redirect_stdout(captured):
            archview.print_headless_section(summary)
        self.assertIn("HEADLESS MODULES", captured.getvalue())
        self.assertIn("leaks 6", captured.getvalue())
        report = self.report()
        text = archview._atlas_code_facts(report).split("Headless modules", 1)[1]
        for needle in ("Rules", "6 leaks", "game:FlightGlobals"):
            self.assertIn(needle, text)
        report["headless"] = None
        self.assertIn("Not measured.", archview._atlas_code_facts(report).split("Headless modules", 1)[1])

    def test_ilanalysis_report_carries_the_headless_result(self):
        facts = fixture_facts()
        report = ilanalysis.report(facts, (), CONFIG, None, HEADLESS)
        self.assertEqual(report["headless"]["leaks"], {"Rules": EXPECTED_LEAKS})
        self.assertIsNone(ilanalysis.report(facts, (), CONFIG)["headless"])


class WorkflowTests(unittest.TestCase):
    def test_the_compiled_il_step_runs_these_cells(self):
        steps = test_semantic.workflow_steps(test_semantic.WORKFLOW.read_text(encoding="utf-8"))
        runs = [run for _name, _keys, env, run in steps if IL_ENV in env and "scripts/arch" in run]
        patterns = [m.group(1) for run in runs for m in [re.search(r"-p\s+['\"]?([^'\"\s]+)", run)] if m]
        # This file's own name, so a rename out of the pattern reds here.
        self.assertTrue(any(fnmatch.fnmatch(Path(__file__).name, p) for p in patterns), patterns)


README = Path(__file__).resolve().parents[2] / "docs" / "dev" / "arch" / "README.md"


class ReadmeTests(unittest.TestCase):
    """The README's "Headless modules" table and its two totals lines are what
    the committed pins' [measured] block renders, so the documented numbers
    cannot drift from the tool. Needs no DLL: the block is written at re-pin."""

    def setUp(self):
        self.text = README.read_text(encoding="utf-8")
        self.pins = headless.load_pins()

    def test_the_committed_readme_matches_the_pins(self):
        self.assertEqual(headless.readme_problems(self.text, self.pins), [])

    def test_the_measured_block_counts_the_pinned_leaks(self):
        measured = self.pins["measured"]
        self.assertEqual(measured["leaks"], sum(len(entry.get("leaks", [])) for entry in self.pins["module"]))
        self.assertEqual(measured["leaking_methods"], len({leak.split(" -> ")[0]
                                                           for entry in self.pins["module"]
                                                           for leak in entry.get("leaks", [])}))

    def test_a_changed_table_number_reds(self):
        row = next(line for line in headless.readme_lines(self.pins["measured"]) if line.startswith("| Missions"))
        cells = row.split(" | ")
        cells[-1] = str(int(cells[-1].rstrip(" |")) + 1) + " |"
        problems = headless.readme_problems(self.text.replace(row, " | ".join(cells)), self.pins)
        self.assertTrue(any("table rows" in p for p in problems), problems)

    def test_a_changed_total_line_reds(self):
        line = headless.readme_lines(self.pins["measured"])[-2]
        problems = headless.readme_problems(self.text.replace(line, line.replace("leaks in", "leaks across")),
                                            self.pins)
        self.assertTrue(any("lacks the pinned line" in p for p in problems), problems)

    def test_a_measured_block_that_disagrees_with_its_modules_reds(self):
        pins = copy.deepcopy(self.pins)
        pins["module"][0]["leaks"] = pins["module"][0]["leaks"][1:]
        problems = headless.readme_problems(self.text, pins)
        self.assertTrue(any("does not count its own" in p for p in problems), problems)

    def test_pins_without_a_measured_block_red(self):
        pins = {key: value for key, value in self.pins.items() if key != "measured"}
        self.assertEqual(len(headless.readme_problems(self.text, pins)), 1)
        self.assertEqual(len(headless.readme_problems("no section here", self.pins)), 1)


# --- the real tree -----------------------------------------------------------------------------


@unittest.skipUnless(os.environ.get(IL_ENV), "%s is not set (CI sets it after the build)" % IL_ENV)
class RealTreeHeadlessTests(unittest.TestCase):
    """The leak pins over the built Debug Parsek.dll, from the report test_ilanalysis
    holds, and mutants of the real facts: each gate reds on its change and passes
    the changes it must not red on."""

    @classmethod
    def setUpClass(cls):
        il_path = Path(os.environ[IL_ENV])
        if not il_path.is_file():
            raise AssertionError("%s=%s does not exist (build Parsek first)" % (IL_ENV, il_path))
        cls.result = test_ilanalysis.real_tree_report(il_path)["headless"]
        cls.facts, cls.tooling = test_ilfacts.real_tree_facts(il_path)
        cls.index = ilanalysis.Index(cls.facts, cls.tooling)
        cls.direct, cls.reach = ilanalysis.game_reach(cls.index, CONFIG)
        cls.modules = headless.declared(headless.load_headless())
        cls.pins = headless.load_pins()
        report = io.StringIO()
        with contextlib.redirect_stdout(report):
            print("\n".join(headless.format_report(cls.result, top=5)))
            for line in headless.ratchet_slack(cls.result["leaks"], cls.pins):
                print("  slack (re-pin to tighten): " + line)
        sys.stderr.write("\n" + report.getvalue())

    def problems(self, result, pins=None):
        return headless.ratchet_problems(result["leaks"], pins or self.pins, result["details"])

    def rerun(self, facts=None, modules=None, config=CONFIG):
        index = ilanalysis.Index(facts, self.tooling) if facts is not None else self.index
        return headless.analyse(index, config, modules or self.modules)

    def pure_headless(self, module="Core"):
        """A headless method of `module` that cannot reach the game and calls nothing in the assembly."""
        return min(k for k, e in self.facts["methods"].items()
                   if self.index.module(e["type"]) == module and k not in self.reach and not e["calls"]
                   and not e["compilerGenerated"] and "abstract" not in e["flags"] and "::.ctor" not in k)

    def edited(self, key, **changes):
        entry = copy.deepcopy(self.facts["methods"][key])
        for name, value in changes.items():
            if name == "body":
                entry["external"]["body"] = sorted(set(entry["external"]["body"]) | set(value))
            elif name == "members":
                entry["externalMembers"] = sorted(set(entry.get("externalMembers", ())) | set(value))
            else:
                entry[name] = sorted(set(entry[name]) | set(value))
        return with_method(self.facts, key, entry)

    # --- the pins hold ---------------------------------------------------------------------

    def test_the_report_used_the_declared_modules(self):
        self.assertEqual(self.result["modules"], self.modules)

    def test_the_leaks_hold_their_pins(self):
        self.assertEqual(self.problems(self.result), [])

    def test_every_live_headless_method_reaches_a_leak_or_a_seam(self):
        self.assertEqual(self.result["unexplained"], [])
        live = [k for k in self.reach if self.index.module(self.index.methods[k]["type"]) in self.modules]
        self.assertGreater(len(live), self.result["leakCount"] // 4)

    def test_the_measure_is_the_coupling_report(self):
        coupling = test_ilanalysis.real_tree_report(Path(os.environ[IL_ENV]))["coupling"]["byModule"]
        for module, row in self.result["byModule"].items():
            with self.subTest(module=module):
                self.assertEqual(row["instructions"], coupling[module]["instructions"])
                self.assertEqual(row["reachInstructions"], coupling[module]["reachInstructions"])
                self.assertEqual(row["reachMethods"], coupling[module]["reachMethods"])

    # --- each gate reds on its change ------------------------------------------------------

    def test_a_headless_method_newly_naming_a_game_type_reds(self):
        key = self.pure_headless()
        result = self.rerun(self.edited(key, body=["UnityEngine.CoreModule|UnityEngine.Transform"]))
        problems = self.problems(result)
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("%s -> game:UnityEngine.Transform" % headless.method_name(key), problems[0])
        self.assertIn("inert_types", problems[0])

    def test_a_headless_method_newly_calling_a_live_method_of_another_module_reds(self):
        key = self.pure_headless("Trajectory")
        target = "Parsek.ParsekSettings::get_Current()"
        self.assertIn(target, self.reach)
        self.assertNotIn(self.index.module("Parsek.ParsekSettings"), self.modules)
        result = self.rerun(self.edited(key, calls=[target]))
        problems = self.problems(result)
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("%s -> call:Parsek.ParsekSettings::get_Current" % headless.method_name(key), problems[0])
        self.assertIn("which reaches the game", problems[0])

    def test_a_rename_of_a_leaking_method_reds(self):
        leak = self.pins["module"][0]["leaks"][0]
        name = leak.split(" -> ")[0]
        key = min(k for k in self.facts["methods"] if headless.method_name(k) == name)
        path, _sep, rest = key.partition("::")
        renamed = path + "::Renamed" + rest[len(name.split("::", 1)[1]):]
        entry = dict(self.facts["methods"][key], name="Renamed")
        facts = with_method(with_method(self.facts, key, None), renamed, entry)
        problems = self.problems(self.rerun(facts))
        self.assertTrue(problems, "a renamed leaking method must red")
        self.assertIn(path + "::Renamed -> ", problems[0])

    def test_a_widened_inert_list_is_caught_by_the_pinned_coupling_lists(self):
        config = copy.deepcopy(CONFIG)
        config["coupling"]["inert_types"] = list(config["coupling"]["inert_types"]) + ["CelestialBody", "Orbit"]
        result = self.rerun(config=config)
        self.assertLess(result["leakCount"], self.result["leakCount"])
        self.assertEqual(self.problems(result), [])  # leaks only vanish: the set ratchet passes
        problems = headless.pins_problems(self.pins, self.modules, config)
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("inert_types changed since the pin (added: CelestialBody, Orbit", problems[0])

    def test_a_module_dropped_from_the_declaration_is_caught_by_the_pinned_module_list(self):
        for module in self.modules:
            with self.subTest(dropped=module):
                kept = [m for m in self.modules if m != module]
                self.assertEqual(len(headless.pins_problems(self.pins, kept, CONFIG)), 1)
        # The leak sets are not monotone in the declaration: a call into a module
        # that stops being headless becomes a leak of its caller. Reaim calls into
        # Missions, so dropping Missions reds Reaim's pins as well.
        result = self.rerun(modules=[m for m in self.modules if m != "Missions"])
        problems = self.problems(result)
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("headless module Reaim:", problems[0])
        self.assertIn("-> call:Parsek.MissionPeriodicity::", problems[0])

    # --- and passes the changes it must not red on -----------------------------------------

    def test_deleting_a_leaking_method_passes_and_reports_slack(self):
        leak = self.pins["module"][0]["leaks"][0]
        name = leak.split(" -> ")[0]
        facts = self.facts
        for key in [k for k in self.facts["methods"] if headless.method_name(k) == name]:
            facts = with_method(facts, key, None)
        result = self.rerun(facts)
        self.assertEqual(self.problems(result), [])
        self.assertTrue(any(leak.split(" -> ")[0] in line for line in headless.ratchet_slack(result["leaks"],
                                                                                             self.pins)))

    def test_adding_a_pure_method_or_an_inert_call_passes(self):
        key = self.pure_headless()
        entry = copy.deepcopy(self.facts["methods"][key])
        path = entry["type"]
        added = with_method(self.facts, path + "::BrandNewPureHelper()", dict(entry, name="BrandNewPureHelper"))
        self.assertEqual(self.problems(self.rerun(added)), [])
        inert = self.edited(key, body=["UnityEngine.CoreModule|UnityEngine.Vector3", "Assembly-CSharp|ConfigNode"],
                            members=["UnityEngine.CoreModule|UnityEngine.Vector3::get_zero",
                                     "Assembly-CSharp|ConfigNode::GetValue"])
        self.assertEqual(self.problems(self.rerun(inert)), [])

    def test_a_new_call_to_a_live_headless_method_adds_no_leak(self):
        key = self.pure_headless()
        callee = min(k for k in self.reach if self.index.module(self.index.methods[k]["type"]) in self.modules
                     and self.index.module(self.index.methods[k]["type"]) != "Core")
        result = self.rerun(self.edited(key, calls=[callee]))
        self.assertEqual(self.problems(result), [])
        self.assertEqual(result["leakCount"], self.result["leakCount"])
        self.assertEqual(result["unexplained"], [])

    def test_generated_code_no_method_owns_is_gated_on_its_type(self):
        pseudo = headless.pseudo_methods(self.index, set(self.modules))
        self.assertTrue(pseudo, "no headless type has unattributed generated code; pick another mutant")
        key = min(pseudo)
        path = pseudo[key]["type"]
        facts = dict(self.facts)
        facts["types"] = dict(self.facts["types"])
        entry = copy.deepcopy(self.facts["types"][path])
        entry["unattributed"]["external"]["body"].append("Assembly-CSharp|FlightGlobals")
        facts["types"][path] = entry
        problems = self.problems(self.rerun(facts))
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("%s -> game:FlightGlobals" % key, problems[0])

    def test_constructing_a_live_implementation_of_a_headless_slot_reds(self):
        filled = sorted(k for k in self.reach if self.index.module(self.index.methods[k]["type"]) not in self.modules
                        and any(self.index.module(self.index.methods[s]["type"]) in self.modules
                                for s in self.index.slots.get(k, ()) if s in self.index.methods))
        self.assertTrue(filled, "no live implementation of a headless slot outside them; pick another mutant")
        path = self.index.methods[filled[0]]["type"]
        constructor = min(k for k in self.index.by_type[path] if self.index.methods[k]["name"] == ".ctor")
        key = self.pure_headless()
        problems = self.problems(self.rerun(self.edited(key, calls=[constructor])))
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("%s -> call:%s::.ctor (constructs" % (headless.method_name(key), path), problems[0])


if __name__ == "__main__":
    unittest.main()
