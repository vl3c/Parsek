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

    FOLD = [("Parsek.Host", "Parsek.HostService")]

    LAMBDA_BEFORE = """\
.class public auto ansi beforefieldinit Parsek.Host
  extends [mscorlib]System.Object
{
  .field private int32 count
  .class nested private auto ansi sealed beforefieldinit '<>c__DisplayClass3_0'
    extends [mscorlib]System.Object
  {
    .field public class Parsek.Host '<>4__this'
    .method assembly hidebysig instance int32 '<Read>b__0'() cil managed
    {
      // Code size       12 (0xc)
      IL_0000:  ldarg.0
      IL_0001:  ldfld      class Parsek.Host Parsek.Host/'<>c__DisplayClass3_0'::'<>4__this'
      IL_0006:  ldfld      int32 Parsek.Host::count
      IL_000b:  ret
    } // end of method '<>c__DisplayClass3_0'::'<Read>b__0'

  } // end of class '<>c__DisplayClass3_0'

} // end of class Parsek.Host
"""

    def test_fold_maps_a_moved_lambda_display_class_and_its_host_hop(self):
        after = """\
.class public auto ansi beforefieldinit Parsek.Host
  extends [mscorlib]System.Object
{
  .field private int32 count
} // end of class Parsek.Host
.class private auto ansi sealed beforefieldinit Parsek.HostService
  extends [mscorlib]System.Object
{
  .class nested private auto ansi sealed beforefieldinit '<>c__DisplayClass9_0'
    extends [mscorlib]System.Object
  {
    .field public class Parsek.HostService '<>4__this'
    .method assembly hidebysig instance int32 '<Read>b__0'() cil managed
    {
      // Code size       17 (0x11)
      IL_0000:  ldarg.0
      IL_0001:  ldfld      class Parsek.HostService Parsek.HostService/'<>c__DisplayClass9_0'::'<>4__this'
      IL_0006:  ldfld      class Parsek.Host Parsek.HostService::host
      IL_000b:  ldfld      int32 Parsek.Host::count
      IL_0010:  ret
    } // end of method '<>c__DisplayClass9_0'::'<Read>b__0'

  } // end of class '<>c__DisplayClass9_0'

} // end of class Parsek.HostService
"""
        self.assertEqual([], self.diffs(self.LAMBDA_BEFORE, after, self.FOLD))

    def test_fold_merges_a_delegate_cache_both_types_carry(self):
        before = """\
.class public auto ansi beforefieldinit Parsek.Host
  extends [mscorlib]System.Object
{
  .class abstract auto ansi sealed nested private beforefieldinit '<>O'
    extends [mscorlib]System.Object
  {
    .field public static class [mscorlib]System.Action '<0>__Keep'
    .field public static class [mscorlib]System.Action '<1>__Moved'
  } // end of class '<>O'

} // end of class Parsek.Host
"""
        after = """\
.class public auto ansi beforefieldinit Parsek.Host
  extends [mscorlib]System.Object
{
  .class abstract auto ansi sealed nested private beforefieldinit '<>O'
    extends [mscorlib]System.Object
  {
    .field public static class [mscorlib]System.Action '<0>__Keep'
  } // end of class '<>O'

} // end of class Parsek.Host
.class private auto ansi sealed beforefieldinit Parsek.HostService
  extends [mscorlib]System.Object
{
  .class abstract auto ansi sealed nested private beforefieldinit '<>O'
    extends [mscorlib]System.Object
  {
    .field public static class [mscorlib]System.Action '<0>__Moved'
  } // end of class '<>O'

} // end of class Parsek.HostService
"""
        self.assertEqual([], self.diffs(before, after, self.FOLD))

    def test_fold_still_counts_each_display_class_once_per_side(self):
        dc = """\
  .class nested private auto ansi sealed beforefieldinit '<>c__DisplayClass{0}_0'
    extends [mscorlib]System.Object
  {{
    .field public int32 x
  }} // end of class '<>c__DisplayClass{0}_0'

"""
        def host(name, body):
            return (".class public auto ansi beforefieldinit Parsek.%s\n"
                    "  extends [mscorlib]System.Object\n{\n%s} // end of class Parsek.%s\n"
                    % (name, body, name))
        before = host("Host", dc.format(1) + dc.format(2))
        split = host("Host", dc.format(1)) + host("HostService", dc.format(7))
        self.assertEqual([], self.diffs(before, split, self.FOLD))
        lost = host("Host", dc.format(1)) + host("HostService", "")
        self.assertNotEqual([], self.diffs(before, lost, self.FOLD))

    def test_fold_reads_a_compound_assignment_through_the_host_as_this(self):
        before = """\
.class public auto ansi beforefieldinit Parsek.Host
  extends [mscorlib]System.Object
{
  .field assembly int32 count
  .method assembly hidebysig instance void Bump() cil managed
  {
    // Code size       15 (0xf)
    IL_0000:  ldarg.0
    IL_0001:  ldarg.0
    IL_0002:  ldfld      int32 Parsek.Host::count
    IL_0007:  ldc.i4.1
    IL_0008:  add
    IL_0009:  stfld      int32 Parsek.Host::count
    IL_000e:  ret
  } // end of method Host::Bump

} // end of class Parsek.Host
"""
        after = """\
.class public auto ansi beforefieldinit Parsek.Host
  extends [mscorlib]System.Object
{
  .field assembly int32 count
} // end of class Parsek.Host
.class private auto ansi sealed beforefieldinit Parsek.HostService
  extends [mscorlib]System.Object
{
  .field private initonly class Parsek.Host host
  .method assembly hidebysig instance void Bump() cil managed
  {
    // Code size       20 (0x14)
    IL_0000:  ldarg.0
    IL_0001:  ldfld      class Parsek.Host Parsek.HostService::host
    IL_0006:  dup
    IL_0007:  ldfld      int32 Parsek.Host::count
    IL_000c:  ldc.i4.1
    IL_000d:  add
    IL_000e:  stfld      int32 Parsek.Host::count
    IL_0013:  ret
  } // end of method HostService::Bump

} // end of class Parsek.HostService
"""
        diffs = self.diffs(before, after, self.FOLD)
        self.assertEqual([], [d for d in diffs if d[0].startswith("method")])

    def test_fold_merges_the_static_lambda_cache_singleton(self):
        def cache(owner, lambdas):
            return """\
  .class auto ansi sealed nested private beforefieldinit '<>c'
    extends [mscorlib]System.Object
  {{
    .field public static initonly class {o}/'<>c' '<>9'
{f}
    .method private hidebysig specialname rtspecialname static void .cctor() cil managed
    {{
      // Code size       11 (0xb)
      IL_0000:  newobj     instance void {o}/'<>c'::.ctor()
      IL_0005:  stsfld     class {o}/'<>c' {o}/'<>c'::'<>9'
      IL_000a:  ret
    }} // end of method '<>c'::.cctor

    .method public hidebysig specialname rtspecialname instance void .ctor() cil managed
    {{
      // Code size       8 (0x8)
      IL_0000:  ldarg.0
      IL_0001:  call       instance void [mscorlib]System.Object::.ctor()
      IL_0006:  nop
      IL_0007:  ret
    }} // end of method '<>c'::.ctor

  }} // end of class '<>c'

""".format(o=owner, f="\n".join("    .field public static class [mscorlib]System.Func`1<int32> '<>9__%s'" % x
                                    for x in lambdas))

        def cls(name, body):
            return (".class public auto ansi beforefieldinit %s\n  extends [mscorlib]System.Object\n{\n%s"
                    "} // end of class %s\n" % (name, body, name))
        before = cls("Parsek.Host", cache("Parsek.Host", ["1_0", "2_0"]))
        after = (cls("Parsek.Host", cache("Parsek.Host", ["1_0"]))
                 + cls("Parsek.HostService", cache("Parsek.HostService", ["2_0"])))
        self.assertEqual([], self.diffs(before, after, self.FOLD))

    @staticmethod
    def _method(access, name, body):
        return ("  .method %s hidebysig instance void %s() cil managed\n  {\n%s  }"
                " // end of method X::%s\n\n" % (access, name, body, name))

    @staticmethod
    def _cls(name, body):
        return (".class public auto ansi beforefieldinit %s\n  extends [mscorlib]System.Object\n{\n%s"
                "} // end of class %s\n" % (name, body, name))

    ORIGINAL = ("    IL_0000:  ldarg.0\n"
                "    IL_0001:  call       instance void Parsek.Host::Step()\n"
                "    IL_0006:  ret\n")
    FORWARDER = ("    IL_0000:  ldarg.0\n"
                 "    IL_0001:  call       instance class Parsek.HostService Parsek.Host::get_Service()\n"
                 "    IL_0006:  callvirt   instance void Parsek.HostService::OnEvent()\n"
                 "    IL_000b:  ret\n")

    def test_fold_pairs_a_moved_handler_with_the_forwarder_it_left(self):
        before = self._cls("Parsek.Host", self._method("private", "OnEvent", self.ORIGINAL))
        moved = self.ORIGINAL.replace("ldarg.0\n", "ldarg.0\n    IL_0000:  ldfld      class Parsek.Host Parsek.HostService::host\n", 1)
        after = (self._cls("Parsek.Host", self._method("private", "OnEvent", self.FORWARDER))
                 + self._cls("Parsek.HostService", self._method("assembly", "OnEvent", moved)))
        ta = il_compare.parse_il(il_compare.apply_folds(before, self.FOLD, "host"))
        tb = il_compare.fold_types(il_compare.parse_il(il_compare.apply_folds(after, self.FOLD, "host")), self.FOLD)
        widened = il_compare.widenings(ta, tb, ["Parsek.Host"])
        self.assertEqual(["forwarded"], [w[0] for w in widened])
        kinds = [d[0] for d in il_compare.compare(ta, tb, widened)]
        self.assertEqual(["method-body"], kinds)  # the forwarder, never the moved body

        changed = moved.replace("Parsek.Host::Step()", "Parsek.Host::Other()")
        after2 = (self._cls("Parsek.Host", self._method("private", "OnEvent", self.FORWARDER))
                  + self._cls("Parsek.HostService", self._method("assembly", "OnEvent", changed)))
        tb2 = il_compare.fold_types(il_compare.parse_il(il_compare.apply_folds(after2, self.FOLD, "host")), self.FOLD)
        widened2 = il_compare.widenings(ta, tb2, ["Parsek.Host"])
        self.assertEqual([], widened2)
        self.assertIn("method-only-after", [d[0] for d in il_compare.compare(ta, tb2, widened2)])

    def test_a_non_private_member_moved_behind_a_forwarder_is_told_apart(self):
        # internal on both sides: the forwarder and the moved original share one key.
        before = self._cls("Parsek.Host", self._method("assembly", "OnEvent", self.ORIGINAL))
        moved = self.ORIGINAL.replace("ldarg.0\n", "ldarg.0\n    IL_0000:  ldfld      class Parsek.Host Parsek.HostService::host\n", 1)
        after = (self._cls("Parsek.Host", self._method("assembly", "OnEvent", self.FORWARDER))
                 + self._cls("Parsek.HostService", self._method("assembly", "OnEvent", moved)))
        ta = il_compare.parse_il(il_compare.apply_folds(before, self.FOLD, "host"))
        tb = il_compare.fold_types(il_compare.parse_il(il_compare.apply_folds(after, self.FOLD, "host")), self.FOLD)
        self.assertEqual(["method-forwarder"], [d[0] for d in il_compare.compare(ta, tb, [])])

        changed = moved.replace("Parsek.Host::Step()", "Parsek.Host::Other()")
        after2 = (self._cls("Parsek.Host", self._method("assembly", "OnEvent", self.FORWARDER))
                  + self._cls("Parsek.HostService", self._method("assembly", "OnEvent", changed)))
        tb2 = il_compare.fold_types(il_compare.parse_il(il_compare.apply_folds(after2, self.FOLD, "host")), self.FOLD)
        self.assertEqual(["method-body"], [d[0] for d in il_compare.compare(ta, tb2, [])])

    def test_fold_reads_host_start_coroutine_as_this(self):
        call = ("    IL_0000:  ldarg.0\n    IL_0001:  ldnull\n"
                "    IL_0002:  {op}   instance class [UnityEngine.CoreModule]UnityEngine.Coroutine "
                "[UnityEngine.CoreModule]UnityEngine.MonoBehaviour::StartCoroutine(class [mscorlib]System.Collections.IEnumerator)\n"
                "    IL_0007:  pop\n    IL_0008:  ret\n")
        before = self._cls("Parsek.Host", self._method("assembly", "Go", call.format(op="call")))
        moved = call.format(op="callvirt").replace(
            "ldarg.0\n", "ldarg.0\n    IL_0000:  ldfld      class Parsek.Host Parsek.HostService::host\n", 1)
        after = self._cls("Parsek.Host", "") + self._cls("Parsek.HostService", self._method("assembly", "Go", moved))
        self.assertEqual([], [d for d in self.diffs(before, after, self.FOLD) if d[0].startswith("method")])
        virt = call.replace("MonoBehaviour::StartCoroutine", "MonoBehaviour::SomeVirtual")
        before_v = self._cls("Parsek.Host", self._method("assembly", "Go", virt.format(op="call")))
        after_v = self._cls("Parsek.Host", "") + self._cls("Parsek.HostService", self._method(
            "assembly", "Go", virt.format(op="callvirt").replace(
                "ldarg.0\n", "ldarg.0\n    IL_0000:  ldfld      class Parsek.Host Parsek.HostService::host\n", 1)))
        self.assertNotEqual([], [d for d in self.diffs(before_v, after_v, self.FOLD) if d[0].startswith("method")])

    def test_fold_drops_the_accessor_reached_through_a_local(self):
        before = """\
.class public auto ansi beforefieldinit Parsek.Host
  extends [mscorlib]System.Object
{
  .method assembly hidebysig static bool Go(class Parsek.Host h) cil managed
  {
    // Code size       12 (0xc)
    IL_0000:  ldarg.0
    IL_0001:  brfalse.s  IL_000a
    IL_0003:  ldarg.0
    IL_0004:  callvirt   instance bool Parsek.Host::Step()
    IL_0009:  ret
    IL_000a:  ldc.i4.0
    IL_000b:  ret
  } // end of method Host::Go

} // end of class Parsek.Host
"""
        after = before.replace(
            "    IL_0004:  callvirt   instance bool Parsek.Host::Step()\n"
            "    IL_0009:  ret\n"
            "    IL_000a:  ldc.i4.0\n"
            "    IL_000b:  ret\n",
            "    IL_0004:  callvirt   instance class Parsek.HostService Parsek.Host::get_Service()\n"
            "    IL_0009:  callvirt   instance bool Parsek.HostService::Step()\n"
            "    IL_000e:  ret\n"
            "    IL_000f:  ldc.i4.0\n"
            "    IL_0010:  ret\n").replace("brfalse.s  IL_000a", "brfalse.s  IL_000f")
        self.assertEqual([], [d for d in self.diffs(before, after, self.FOLD) if d[0] == "method-body"])

    def test_a_retargeted_branch_is_still_a_difference(self):
        changed = self.LAMBDA_BEFORE.replace(
            "      IL_0006:  ldfld      int32 Parsek.Host::count\n",
            "      IL_0006:  ldfld      int32 Parsek.Host::count\n"
            "      IL_0007:  brtrue.s   IL_0001\n")
        self.assertEqual(["method-body"], [d[0] for d in self.diffs(self.LAMBDA_BEFORE, changed)])

    def test_short_and_long_branch_forms_are_not_a_difference(self):
        def with_branch(op):
            return self.LAMBDA_BEFORE.replace(
                "      IL_0006:  ldfld      int32 Parsek.Host::count\n",
                "      IL_0006:  ldfld      int32 Parsek.Host::count\n"
                "      IL_0007:  %s IL_000b\n" % op)
        self.assertEqual([], self.diffs(with_branch("brtrue.s  "), with_branch("brtrue    ")))
        self.assertEqual(["method-body"],
                         [d[0] for d in self.diffs(with_branch("brtrue.s  "), with_branch("brfalse   "))])

    def test_widening_is_listed_not_a_difference_and_the_body_is_still_compared(self):
        widened = IL_BASE.replace(".field private int32 count", ".field assembly int32 count")
        a = il_compare.parse_il(IL_BASE)
        b = il_compare.parse_il(widened)
        w = il_compare.widenings(a, b, ["Parsek.Host"])
        self.assertEqual([("field", "Parsek.Host")], [(k, p) for k, p, _, _ in w])
        self.assertEqual([], il_compare.compare(a, b, w))
        both = widened.replace("method public hidebysig instance void Tick",
                               "method assembly hidebysig instance void Tick")
        both_src = IL_BASE.replace("method public hidebysig instance void Tick",
                                   "method private hidebysig instance void Tick")
        changed = both.replace('ldstr "a { brace"', 'ldstr "b { brace"')
        a2, b2 = il_compare.parse_il(both_src), il_compare.parse_il(changed)
        w2 = il_compare.widenings(a2, b2, ["Parsek.Host"])
        self.assertEqual(["method-body"], [d[0] for d in il_compare.compare(a2, b2, w2)])


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
