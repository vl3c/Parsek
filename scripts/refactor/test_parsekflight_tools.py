"""Unit tests for split_parsekflight.py and il_compare.py.

Run: python3 -m unittest discover -s scripts/refactor -p "test_*.py" -q
"""

import collections
import contextlib
import io
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import il_compare  # noqa: E402
import split_parsekflight as spf  # noqa: E402


IL_BASE = """\
.class public auto ansi beforefieldinit Parsek.Host
  extends [mscorlib]System.Object
{
  .field private int32 count
  .method public hidebysig instance void Tick() cil managed
  {
    // Code size 8
    .maxstack 8
    IL_0000:  ldarg.0
    IL_0001:  ldstr "a { brace"
    IL_0006:  call void Parsek.Host::Log(string)
    IL_000b:  ret
  } // end of method Host::Tick

  .method private hidebysig instance void '<Tick>b__12_0'() cil managed
  {
    IL_0000:  ret
  } // end of method Host::'<Tick>b__12_0'

  .class nested private auto ansi sealed beforefieldinit '<>c__DisplayClass12_0'
    extends [mscorlib]System.Object
  {
    .field public int32 x
  } // end of class '<>c__DisplayClass12_0'

} // end of class Parsek.Host
"""


class IlCompareTests(unittest.TestCase):
    def diffs(self, a, b, folds=()):
        ta = il_compare.parse_il(il_compare.apply_folds(a, folds, "host"))
        tb = il_compare.fold_types(il_compare.parse_il(il_compare.apply_folds(b, folds, "host")), folds)
        return il_compare.compare(ta, tb)

    def test_identical_dumps_have_no_difference(self):
        self.assertEqual([], self.diffs(IL_BASE, IL_BASE))

    def test_parse_counts_types_fields_and_methods(self):
        types = il_compare.parse_il(IL_BASE)
        self.assertEqual((2, 2, 2), il_compare.summarize(types))

    def test_ordinal_renumbering_is_not_a_difference(self):
        moved = IL_BASE.replace("b__12_0", "b__40_0").replace("DisplayClass12_0", "DisplayClass40_0")
        self.assertEqual([], self.diffs(IL_BASE, moved))

    def test_member_order_is_not_a_difference(self):
        tick_start = IL_BASE.index("  .method public")
        lam_start = IL_BASE.index("  .method private")
        nested = IL_BASE.index("  .class nested")
        tick = IL_BASE[tick_start:lam_start]
        lam = IL_BASE[lam_start:nested]
        swapped = IL_BASE[:tick_start] + lam + tick + IL_BASE[nested:]
        self.assertEqual([], self.diffs(IL_BASE, swapped))

    def test_state_machine_attribute_ordinal_is_not_a_difference(self):
        def with_iter(ordinal):
            return IL_BASE.replace(
                "    // Code size 8\n",
                "    .custom instance void [mscorlib]System.Runtime.CompilerServices."
                "IteratorStateMachineAttribute::.ctor([mscorlib]System.Type) = "
                "( 01 00 15 50 61 72 73 65 6B 2E 48 6F 73 74 2B   // ..Parsek.Host+\n"
                "      3C 54 3E 64 5F 5F %s 00 00 )   // <T>d__..\n"
                "    // Code size 8\n" % ordinal)
        self.assertEqual([], self.diffs(with_iter("31 32"), with_iter("39 37 30")))

    def test_other_attribute_blob_change_is_reported(self):
        def with_attr(byte):
            return IL_BASE.replace(
                "    // Code size 8\n",
                "    .custom instance void Parsek.InGameTestAttribute::.ctor() = ( 01 00 %s 00 )\n"
                "    // Code size 8\n" % byte)
        self.assertEqual(["method-body"], [d[0] for d in self.diffs(with_attr("01"), with_attr("02"))])

    def test_data_field_rva_is_not_a_difference(self):
        def with_data(rva):
            return IL_BASE.replace(
                "  .field private int32 count\n",
                "  .field private int32 count\n"
                "  .field static assembly initonly int64 'ABCD' at I_%s\n" % rva)
        self.assertEqual([], self.diffs(with_data("00A4D730"), with_data("00A4D788")))

    def test_body_change_is_reported(self):
        changed = IL_BASE.replace('ldstr "a { brace"', 'ldstr "b { brace"')
        kinds = [d[0] for d in self.diffs(IL_BASE, changed)]
        self.assertEqual(["method-body"], kinds)

    def test_field_change_is_reported(self):
        changed = IL_BASE.replace(".field private int32 count", ".field assembly int32 count")
        kinds = sorted(d[0] for d in self.diffs(IL_BASE, changed))
        self.assertEqual(["field", "field"], kinds)  # the old declaration gone, the new one added

    def test_type_header_change_is_reported(self):
        changed = IL_BASE.replace("public auto ansi beforefieldinit Parsek.Host",
                                  "public auto ansi Parsek.Host", 1)
        self.assertIn("type-header", [d[0] for d in self.diffs(IL_BASE, changed)])

    def test_fold_maps_a_moved_body_back_onto_its_host(self):
        before = """\
.class public auto ansi beforefieldinit Parsek.Host
  extends [mscorlib]System.Object
{
  .field private int32 count
  .method assembly hidebysig instance int32 Read() cil managed
  {
    IL_0000:  ldarg.0
    IL_0001:  ldfld int32 Parsek.Host::count
    IL_0006:  ret
  } // end of method Host::Read

} // end of class Parsek.Host
"""
        after = """\
.class public auto ansi beforefieldinit Parsek.Host
  extends [mscorlib]System.Object
{
  .field private int32 count
} // end of class Parsek.Host
.class assembly auto ansi sealed beforefieldinit Parsek.HostService
  extends [mscorlib]System.Object
{
  .method assembly hidebysig instance int32 Read() cil managed
  {
    IL_0000:  ldarg.0
    IL_0001:  ldfld class Parsek.Host Parsek.HostService::host
    IL_0006:  ldfld int32 Parsek.Host::count
    IL_000b:  ret
  } // end of method HostService::Read

} // end of class Parsek.HostService
"""
        diffs = self.diffs(before, after, [("Parsek.Host", "Parsek.HostService")])
        self.assertEqual([], [d for d in diffs if d[0].startswith("method")])

    def test_fold_drops_the_lazy_accessor_hop_in_a_host_body(self):
        before = """\
.class public auto ansi beforefieldinit Parsek.Host
  extends [mscorlib]System.Object
{
  .method assembly hidebysig instance void Go() cil managed
  {
    IL_0000:  ldarg.0
    IL_0001:  call instance void Parsek.Host::Step()
    IL_0006:  ret
  } // end of method Host::Go

} // end of class Parsek.Host
"""
        after = before.replace(
            "    IL_0001:  call instance void Parsek.Host::Step()\n",
            "    IL_0001:  call instance class Parsek.HostService Parsek.Host::get_Service()\n"
            "    IL_0006:  callvirt instance void Parsek.HostService::Step()\n")
        diffs = self.diffs(before, after, [("Parsek.Host", "Parsek.HostService")])
        self.assertEqual([], [d for d in diffs if d[0] == "method-body"])


SRC = """\
using System;

namespace Parsek
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public partial class ParsekFlight : MonoBehaviour, IGhostPositioner
    {
        #region State
        private int a = 1;
        #endregion

        #region Playback
        void Play()
        {
            a++;
        }

        void Stop()
        {
            a--;
        }
        #endregion

        void Keep()
        {
        }
    }
}
"""


class SplitParsekFlightTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.src = os.path.join(self.dir, "ParsekFlight.cs")
        with open(self.src, "w", encoding="utf-8", newline="") as fh:
            fh.write(SRC)
        self.saved = spf.AREAS
        spf.AREAS = collections.OrderedDict([("Playback", ("Playback members.", ["Play", "Stop"]))])

    def tearDown(self):
        spf.AREAS = self.saved
        shutil.rmtree(self.dir)

    def run_main(self, *args):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            rc = spf.main(["--src", self.src] + list(args))
        return rc, out.getvalue()

    def read(self, name):
        with open(os.path.join(self.dir, name), encoding="utf-8") as fh:
            return fh.read()

    def test_strip_regions_drops_directives_without_double_blanks(self):
        lines = SRC.split("\n")
        kept, dropped, where = spf.strip_regions(lines)
        self.assertFalse(any("#region" in l or "#endregion" in l for l in kept))
        self.assertEqual(4, len(dropped))
        self.assertEqual("State", where[kept.index("        private int a = 1;")])
        kept, dropped, _ = spf.strip_regions(["        }", "", "        #endregion", "", "        void M()"])
        self.assertEqual(["        }", "", "        void M()"], kept)
        self.assertEqual(2, len(dropped))

    def test_unbalanced_region_fails(self):
        with self.assertRaises(spf.SplitError):
            spf.strip_regions(["        #region Open", "        void M() { }"])

    def test_split_moves_mapped_methods_and_keeps_fields(self):
        rc, out = self.run_main()
        self.assertEqual(0, rc)
        main = self.read("ParsekFlight.cs")
        part = self.read("ParsekFlight.Playback.cs")
        self.assertIn("private int a = 1;", main)
        self.assertIn("void Keep()", main)
        self.assertNotIn("void Play()", main)
        self.assertIn("void Play()", part)
        self.assertIn("void Stop()", part)
        self.assertIn("    public partial class ParsekFlight\n", part)
        self.assertNotIn("#region", main + part)

    def test_mapping_a_field_is_refused(self):
        spf.AREAS = collections.OrderedDict([("Playback", ("x", ["a"]))])
        with self.assertRaises(spf.SplitError):
            self.run_main("--check")

    def test_stale_mapping_is_refused(self):
        spf.AREAS = collections.OrderedDict([("Playback", ("x", ["Gone"]))])
        with self.assertRaises(spf.SplitError):
            self.run_main("--check")

    def test_check_writes_nothing(self):
        self.run_main("--check")
        self.assertFalse(os.path.exists(os.path.join(self.dir, "ParsekFlight.Playback.cs")))
        self.assertEqual(SRC, self.read("ParsekFlight.cs"))


if __name__ == "__main__":
    unittest.main()
