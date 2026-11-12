"""Tests for the log-tag map (logtags.py) the harness lane health reads.

The synthetic cells feed hand-written ikdasm-shaped IL with one case per tag
shape the extractor follows (a literal, a static readonly field, a local, a
forwarding helper and a helper of a helper, two branches that meet, the
`Write(level, tag, msg)` emitter, a catch handler) and the shapes it must
refuse to guess (two literals that meet, a call result). The real-tree cells
need PARSEK_ARCH_IL (a built Parsek.dll or an .il dump), like
test_semantic's: they check the extractor still models the assembly and that
the committed scripts/arch/log_tags.json parses. A stale committed map is
not an error (the harness counts unknown tags as unmapped); the cell prints
the difference instead.
"""

import fnmatch
import json
import os
import re
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import logtags  # noqa: E402
import ilgraph  # noqa: E402
import test_semantic  # noqa: E402

IL_ENV = test_semantic.IL_ENV

LOG = "call       void Parsek.ParsekLog::%s(string,\n                                               string)"


def _method(name, params, body, static=True, locals_=""):
    header = ".method assembly hidebysig %svoid \n          %s(%s) cil managed" % (
        "static " if static else "instance ", name, params)
    lines = ["  " + header, "  {", "    .maxstack  8"]
    if locals_:
        lines.append("    .locals init (%s)" % locals_)
    for i, ins in enumerate(body):
        if ins.startswith((".", "{", "}", "catch")):
            lines.append("    " + ins)
        elif re.match(r"^IL_[0-9a-f]+:", ins):
            lines.append("    " + ins)
        else:
            lines.append("    IL_%04x:  %s" % (i * 2 + 0x1000, ins))
    lines.append("  } // end of method")
    return "\n".join(lines)


SAMPLE = "\n".join([
    ".class private abstract auto ansi sealed beforefieldinit Parsek.ParsekLog",
    "       extends [mscorlib]System.Object",
    "{",
    _method("Info", "string subsystem, string message", [
        'ldstr      "INFO"', "ldarg.0", "ldarg.1",
        "call       void Parsek.ParsekLog::Write(string,\n                                                string,\n"
        "                                                string)", "ret"]),
    "} // end of class Parsek.ParsekLog",
    "",
    ".class private abstract auto ansi sealed beforefieldinit Parsek.Demo",
    "       extends [mscorlib]System.Object",
    "{",
    "  .field private static initonly string Tag",
    _method(".cctor", "", ['ldstr      "Beta"', "stsfld     string Parsek.Demo::Tag", "ret"]),
    _method("LiteralSite", "", ['ldstr      "Alpha"', 'ldstr      "m"', LOG % "Info", "ret"]),
    _method("StaticSite", "", ["ldsfld     string Parsek.Demo::Tag", 'ldstr      "m"', LOG % "Warn", "ret"]),
    _method("LocalSite", "", ['ldstr      "Zeta"', "stloc.0", "ldloc.0", 'ldstr      "m"', LOG % "Error", "ret"],
            locals_="string V_0"),
    _method("Log", "string tag, string msg", ["ldarg.0", "ldarg.1", LOG % "Verbose", "ret"]),
    _method("Log2", "string msg, string tag", ["ldarg.1", "ldarg.0",
                                                "call       void Parsek.Demo::Log(string,\n"
                                                "                                 string)", "ret"]),
    _method("HelperCaller", "", ['ldstr      "Gamma"', 'ldstr      "m"',
                                 "call       void Parsek.Demo::Log(string,\n                                 string)",
                                 'ldstr      "m"', 'ldstr      "Delta"',
                                 "call       void Parsek.Demo::Log2(string,\n                                  string)",
                                 "ret"]),
    # A helper forwarding one parameter to two emitters and to itself: one
    # merged entry, and the resolution settles.
    _method("Both", "string tag, bool warn", [
        "ldarg.1", "brtrue.s   IL_0100",
        "ldarg.0", 'ldstr      "m"', LOG % "Info",
        "IL_0100:  ldarg.0", 'ldstr      "m"', LOG % "Warn",
        "ldarg.0", "ldc.i4.0", "call       void Parsek.Demo::Both(string,\n                                  bool)",
        "ret"]),
    _method("BothCaller", "", ['ldstr      "Iota"', "ldc.i4.1",
                               "call       void Parsek.Demo::Both(string,\n                                  bool)",
                               "ret"]),
    _method("SameOnBothBranches", "bool b", [
        "ldarg.0", "brtrue.s   IL_0200",
        'ldstr      "Eps"', "br.s       IL_0201",
        'IL_0200:  ldstr      "Eps"',
        'IL_0201:  ldstr      "m"', LOG % "Info", "ret"]),
    _method("DifferentOnBranches", "bool b", [
        "ldarg.0", "brtrue.s   IL_0300",
        'ldstr      "X"', "br.s       IL_0301",
        'IL_0300:  ldstr      "Y"',
        'IL_0301:  ldstr      "m"', LOG % "Info", "ret"]),
    _method("WriteSite", "", [
        'ldstr      "ERROR"', 'ldstr      "Eta"', 'ldstr      "m"',
        "call       void Parsek.ParsekLog::Write(string,\n                                     string,\n"
        "                                     string)", "ret"]),
    _method("CallResult", "", [
        "call       string Parsek.Demo::MakeTag()", 'ldstr      "m"', LOG % "Info", "ret"]),
    _method("InCatch", "", [
        ".try", "{", "nop", "leave.s    IL_0400", "}", "catch [mscorlib]System.Exception", "{",
        "stloc.0", 'ldstr      "Theta"', "ldloc.0",
        "callvirt   instance string [mscorlib]System.Object::ToString()", LOG % "Warn",
        "leave.s    IL_0400", "}", "IL_0400:  ret"], locals_="[mscorlib]System.Exception V_0"),
    "} // end of class Parsek.Demo",
    "",
])


def _module_of(path):
    return {"Parsek.Demo": "DemoModule", "Parsek.ParsekLog": "Core"}.get(path)


class SyntheticTagTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.map = logtags.map_from_il(SAMPLE, _module_of)

    def test_every_followed_shape_resolves_to_its_literal(self):
        tags = self.map["tags"]
        self.assertEqual(["Alpha", "Beta", "Delta", "Eps", "Eta", "Gamma", "Iota", "Theta", "Zeta"], sorted(tags))
        for tag in tags:
            self.assertEqual({"DemoModule": tags[tag]["sites"]}, tags[tag]["modules"], tag)

    def test_levels_kinds_and_helpers(self):
        tags = self.map["tags"]
        self.assertEqual({"INFO": 1}, tags["Alpha"]["levels"])
        self.assertEqual({"WARN": 1}, tags["Beta"]["levels"])
        self.assertEqual(1, tags["Beta"]["staticSites"])
        self.assertEqual({"ERROR": 1}, tags["Eta"]["levels"])
        self.assertEqual({"ERROR": 1}, tags["Zeta"]["levels"])
        self.assertEqual({"VERBOSE": 1}, tags["Gamma"]["levels"])
        self.assertEqual(1, tags["Gamma"]["helperSites"])
        self.assertEqual({"VERBOSE": 1}, tags["Delta"]["levels"])
        self.assertEqual({"mixed": 1}, tags["Iota"]["levels"])
        self.assertEqual(sorted(["Parsek.Demo::Both -> Info+Warn(arg 0)", "Parsek.Demo::Log -> Verbose(arg 0)",
                                 "Parsek.Demo::Log2 -> Verbose(arg 1)"]), self.map["forwarders"])

    def test_parseklog_itself_is_not_a_site(self):
        self.assertNotIn("Core", json.dumps(self.map["tags"]))

    def test_unresolvable_tags_are_listed_never_guessed(self):
        self.assertEqual({"call": 1, "other": 1}, self.map["nonLiteral"]["byKind"])
        self.assertEqual({"Parsek.Demo::CallResult": 1, "Parsek.Demo::DifferentOnBranches": 1},
                         self.map["nonLiteral"]["byMethod"])
        self.assertNotIn("X", self.map["tags"])
        self.assertNotIn("Y", self.map["tags"])

    def test_totals_and_serialization_are_stable(self):
        self.assertEqual({"tags": 9, "sites": 9, "nonLiteralSites": 2, "forwarders": 3}, self.map["totals"])
        again = logtags.map_from_il(SAMPLE, _module_of)
        self.assertEqual(logtags.serialize(self.map), logtags.serialize(again))
        self.assertNotIn(str(Path.home()), logtags.serialize(self.map))

    def test_a_bytearray_literal_decodes(self):
        self.assertEqual("Tag\u00e9", logtags._bytearray_text("54 00 61 00 67 00 E9 00"))


class CommittedMapTests(unittest.TestCase):
    def test_the_committed_map_is_well_formed(self):
        doc = json.loads(logtags.DEFAULT_OUTPUT.read_text(encoding="utf-8"))
        self.assertEqual(logtags.SCHEMA, doc["schema"])
        self.assertEqual(len(doc["tags"]), doc["totals"]["tags"])
        self.assertEqual(sum(e["sites"] for e in doc["tags"].values()), doc["totals"]["sites"])
        for tag, entry in doc["tags"].items():
            self.assertTrue(entry["modules"], tag)
            self.assertEqual(entry["sites"], sum(entry["modules"].values()), tag)
            self.assertEqual(entry["sites"], sum(entry["levels"].values()), tag)
        # The harness reads the file; it never imports this module.
        harness_lib = Path(__file__).resolve().parents[2] / "harness" / "lib" / "runindex.py"
        self.assertNotIn("import logtags", harness_lib.read_text(encoding="utf-8"))
        self.assertEqual(logtags.SCHEMA, re.search(r'TAG_MAP_SCHEMA = "([^"]+)"',
                                                   harness_lib.read_text(encoding="utf-8")).group(1))


class WorkflowTests(unittest.TestCase):
    def test_the_compiled_il_step_also_runs_these_cells(self):
        steps = test_semantic.workflow_steps(test_semantic.WORKFLOW.read_text(encoding="utf-8"))
        runs = [run for _name, _keys, env, run in steps if IL_ENV in env and "scripts/arch" in run]
        self.assertTrue(runs)
        patterns = [m.group(1) for run in runs for m in [re.search(r"-p\s+['\"]?([^'\"\s]+)", run)] if m]
        self.assertTrue(any(fnmatch.fnmatch("test_iltags.py", p) for p in patterns), patterns)


@unittest.skipUnless(os.environ.get(IL_ENV), "%s is not set (CI sets it after the build)" % IL_ENV)
class RealTreeTagTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        il_path = Path(os.environ[IL_ENV])
        if not il_path.is_file():
            raise AssertionError("%s=%s does not exist (build Parsek first)" % (IL_ENV, il_path))
        cls.map = logtags.map_from_il(ilgraph.load_il(il_path), logtags.module_lookup())
        sys.stderr.write("\n" + logtags.format_summary(cls.map) + "\n")

    def test_the_extractor_still_models_the_assembly(self):
        totals = self.map["totals"]
        # Structure, not a count that moves with ordinary code: the assembly
        # logs through ParsekLog, nearly every site has a literal tag, and the
        # well-known tags resolve to the module that owns their writer.
        self.assertGreater(totals["sites"], 1000)
        self.assertLess(totals["nonLiteralSites"], totals["sites"] // 20)
        self.assertGreater(totals["forwarders"], 0)
        self.assertIn("Diagnostics", self.map["tags"])
        self.assertIn("Diagnostics", self.map["tags"]["Diagnostics"]["modules"])
        unplaced = {t: e["modules"]["(unplaced)"] for t, e in self.map["tags"].items() if "(unplaced)" in e["modules"]}
        self.assertLess(sum(unplaced.values()), totals["sites"] // 20, unplaced)

    def test_the_committed_map_against_this_build(self):
        committed = json.loads(logtags.DEFAULT_OUTPUT.read_text(encoding="utf-8"))
        added = sorted(set(self.map["tags"]) - set(committed["tags"]))
        gone = sorted(set(committed["tags"]) - set(self.map["tags"]))
        # Report-only: the harness counts a tag the map lacks as unmapped.
        sys.stderr.write("log_tags.json vs this build: %d tags new %s, %d gone %s%s\n" % (
            len(added), added[:10], len(gone), gone[:10],
            "" if not (added or gone) else " (refresh: python3 scripts/arch/logtags.py <Parsek.dll> --write)"))
        self.assertEqual(logtags.SCHEMA, committed["schema"])


if __name__ == "__main__":
    unittest.main()
