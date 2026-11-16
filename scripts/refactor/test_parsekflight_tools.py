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

    # --alias-field: the positioner reached lane state through `host.Lane.x` (before) and reaches
    # it through its own `lane` field (after), under the fold Host=Lane.
    LANE_FOLD = [("Parsek.Host", "Parsek.Lane")]
    LANE_ALIAS = [("Parsek.Pos", "lane", "host")]

    @staticmethod
    def _lane_types(lane_fields):
        return ("""\
.class public auto ansi beforefieldinit Parsek.Host
  extends [mscorlib]System.Object
{
  .field private class Parsek.Lane lane
  .field assembly class Parsek.Other other
  .method assembly hidebysig specialname instance class Parsek.Lane get_Lane() cil managed
  {
    IL_0000:  ldarg.0
    IL_0001:  ldfld      class Parsek.Lane Parsek.Host::lane
    IL_0006:  ret
  } // end of method Host::get_Lane

} // end of class Parsek.Host
.class assembly auto ansi sealed beforefieldinit Parsek.Lane
  extends [mscorlib]System.Object
{
%s} // end of class Parsek.Lane
.class assembly auto ansi sealed beforefieldinit Parsek.Other
  extends [mscorlib]System.Object
{
} // end of class Parsek.Other
""" % "".join("  .field assembly int32 %s\n" % f for f in lane_fields))

    @classmethod
    def _pos(cls, fields, body, type_name="Parsek.Pos"):
        decls = "".join("  .field private initonly %s\n" % f for f in fields)
        return cls._cls(type_name, decls + cls._method("assembly", "Read", body))

    LANE_BEFORE_BODY = ("    IL_0000:  ldarg.0\n"
                        "    IL_0001:  ldfld      class Parsek.Host Parsek.Pos::host\n"
                        "    IL_0006:  callvirt   instance class Parsek.Lane Parsek.Host::get_Lane()\n"
                        "    IL_000b:  ldfld      int32 Parsek.Lane::count\n"
                        "    IL_0010:  pop\n"
                        "    IL_0011:  ret\n")
    LANE_AFTER_BODY = ("    IL_0000:  ldarg.0\n"
                       "    IL_0001:  ldfld      class Parsek.Lane Parsek.Pos::lane\n"
                       "    IL_0006:  ldfld      int32 Parsek.Lane::count\n"
                       "    IL_000b:  pop\n"
                       "    IL_000c:  ret\n")
    HOST_DECL = "class Parsek.Host host"
    LANE_DECL = "class Parsek.Lane lane"

    def lane_before(self):
        return self._lane_types(["count", "total"]) + self._pos([self.HOST_DECL], self.LANE_BEFORE_BODY)

    def lane_after(self, body=None, fields=None, pos_type="Parsek.Pos"):
        return self._lane_types(["count", "total"]) + self._pos(
            fields if fields is not None else [self.HOST_DECL, self.LANE_DECL],
            body if body is not None else self.LANE_AFTER_BODY, pos_type)

    def alias_diffs(self, before, after, aliases):
        folds = self.LANE_FOLD
        ta = il_compare.fold_types(il_compare.parse_il(il_compare.apply_folds(before, folds, "host")), folds)
        text, _ = il_compare.apply_aliases(il_compare.apply_folds(after, folds, "host"), aliases, folds)
        tb = il_compare.fold_types(il_compare.parse_il(text), folds)
        return il_compare.alias_checks(ta, tb, aliases, folds) + il_compare.compare(ta, tb)

    def test_alias_folds_a_lane_field_read_onto_the_host_lane_read(self):
        diffs = self.alias_diffs(self.lane_before(), self.lane_after(), self.LANE_ALIAS)
        # The one leftover is the new field's declaration; no body and no alias check differs.
        self.assertEqual([("field", "Parsek.Pos")], [(d[0], d[1]) for d in diffs])
        self.assertIn("lane (before 0, after 1)", diffs[0][2])

    def test_alias_counts_the_reads_it_rewrote(self):
        folded = il_compare.apply_folds(self.lane_after(), self.LANE_FOLD, "host")
        _, counts = il_compare.apply_aliases(folded, self.LANE_ALIAS, self.LANE_FOLD)
        self.assertEqual([1], counts)

    def test_without_the_alias_the_renamed_read_is_a_difference(self):
        kinds = [d[0] for d in self.alias_diffs(self.lane_before(), self.lane_after(), [])]
        self.assertIn("method-body", kinds)

    def test_an_alias_on_another_type_does_not_apply(self):
        diffs = self.alias_diffs(self.lane_before(), self.lane_after(), [("Parsek.Other", "lane", "host")])
        kinds = [d[0] for d in diffs]
        self.assertIn("method-body", kinds)
        self.assertIn("alias-field", kinds)  # Parsek.Other declares neither field
        # A type whose name merely starts with TYPE, or a type nested in it, is not TYPE.
        longer = self.lane_after().replace(
            "Parsek.Pos::lane", "Parsek.PosX::lane")
        text, counts = il_compare.apply_aliases(
            il_compare.apply_folds(longer, self.LANE_FOLD, "host"), self.LANE_ALIAS, self.LANE_FOLD)
        self.assertEqual([0], counts)
        nested = self.lane_after().replace("Parsek.Pos::lane", "Parsek.Pos/'<>c__DisplayClass1_0'::lane")
        _, counts = il_compare.apply_aliases(
            il_compare.apply_folds(nested, self.LANE_FOLD, "host"), self.LANE_ALIAS, self.LANE_FOLD)
        self.assertEqual([0], counts)

    def test_an_alias_does_not_reach_a_longer_field_name_a_store_or_an_address(self):
        for before_op, op in (("ldfld", "lanes"), ("stfld", "lane"), ("ldflda", "lane")):
            line = "    IL_0001:  %s      class Parsek.Lane Parsek.Pos::%s\n" % (before_op, op)
            text, counts = il_compare.apply_aliases(line, self.LANE_ALIAS, self.LANE_FOLD)
            self.assertEqual(([0], line), (counts, text), line)

    def test_a_renamed_field_whose_type_differs_is_reported(self):
        # `lane` is a Parsek.Other: the fold does not make it the host's type, so the alias is
        # not a rename. Both the declaration check and the read itself report it.
        body = self.LANE_AFTER_BODY.replace("ldfld      class Parsek.Lane Parsek.Pos::lane",
                                            "ldfld      class Parsek.Other Parsek.Pos::lane")
        after = self.lane_after(body=body, fields=[self.HOST_DECL, "class Parsek.Other lane"])
        diffs = self.alias_diffs(self.lane_before(), after, self.LANE_ALIAS)
        kinds = [d[0] for d in diffs]
        self.assertIn("method-body", kinds)
        self.assertTrue(any(d[0] == "alias-field" and "type differs" in d[2] for d in diffs), diffs)

    def test_alias_declaration_checks(self):
        # NEW missing after, OLD missing before, NEW already declared before.
        no_new = self.lane_after(fields=[self.HOST_DECL])
        self.assertTrue(any("lane declared 0 times after" in d[2]
                            for d in self.alias_diffs(self.lane_before(), no_new, self.LANE_ALIAS)))
        wrong_old = [("Parsek.Pos", "lane", "hostX")]
        self.assertTrue(any("hostX declared 0 times before" in d[2]
                            for d in self.alias_diffs(self.lane_before(), self.lane_after(), wrong_old)))
        had_lane = self._lane_types(["count", "total"]) + self._pos(
            [self.HOST_DECL, self.LANE_DECL], self.LANE_BEFORE_BODY)
        self.assertTrue(any("lane already declared before" in d[2]
                            for d in self.alias_diffs(had_lane, self.lane_after(), self.LANE_ALIAS)))

    def test_a_read_redirected_to_another_field_of_the_same_type_is_reported(self):
        # Another positioner field of the lane's type (a second lane) read in place of `lane`.
        spare = self.LANE_AFTER_BODY.replace("Parsek.Pos::lane", "Parsek.Pos::spare")
        after = self.lane_after(body=spare, fields=[self.HOST_DECL, self.LANE_DECL, "class Parsek.Lane spare"])
        self.assertIn("method-body", [d[0] for d in self.alias_diffs(self.lane_before(), after, self.LANE_ALIAS)])
        # Another lane member of the same type read in place of `count`.
        other_member = self.LANE_AFTER_BODY.replace("Parsek.Lane::count", "Parsek.Lane::total")
        self.assertIn("method-body", [d[0] for d in self.alias_diffs(
            self.lane_before(), self.lane_after(body=other_member), self.LANE_ALIAS)])

    def test_a_body_change_beside_the_rename_is_reported(self):
        changed = self.LANE_AFTER_BODY.replace("    IL_000b:  pop\n", "    IL_000b:  pop\n    IL_000c:  nop\n")
        self.assertIn("method-body", [d[0] for d in self.alias_diffs(
            self.lane_before(), self.lane_after(body=changed), self.LANE_ALIAS)])
        # The real host is still read through `host` after: an alias never folds `host` reads.
        host_read = self.LANE_AFTER_BODY.replace("ldfld      class Parsek.Lane Parsek.Pos::lane",
                                                 "ldfld      class Parsek.Host Parsek.Pos::host\n"
                                                 "    IL_0005:  ldfld      class Parsek.Other Parsek.Host::other")
        self.assertIn("method-body", [d[0] for d in self.alias_diffs(
            self.lane_before(), self.lane_after(body=host_read), self.LANE_ALIAS)])

    def test_alias_is_applied_to_the_after_build_only(self):
        # A before body that already read `lane` must not read `host` under the alias.
        before = self._lane_types(["count", "total"]) + self._pos([self.HOST_DECL], self.LANE_AFTER_BODY)
        kinds = [d[0] for d in self.alias_diffs(before, self.lane_after(), self.LANE_ALIAS)]
        self.assertIn("method-body", kinds)

    def test_parse_alias(self):
        self.assertEqual(("Parsek.Pos", "lane", "host"), il_compare.parse_alias("Parsek.Pos::lane=host"))
        for bad in ("Parsek.Pos::lane", "lane=host", "Parsek.Pos::lane=lane", "Parsek.Pos::la ne=host",
                    "Parsek.Pos::lane=host=x"):
            self.assertIsNone(il_compare.parse_alias(bad), bad)

    def test_main_alias_flag_end_to_end(self):
        d = tempfile.mkdtemp()
        try:
            paths = []
            for name, text in (("a.il", self.lane_before()), ("b.il", self.lane_after())):
                paths.append(os.path.join(d, name))
                with open(paths[-1], "w", encoding="utf-8") as fh:
                    fh.write(text)
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                rc = il_compare.main(paths + ["--il", "--fold", "Parsek.Host=Parsek.Lane",
                                              "--alias-field", "Parsek.Pos::lane=host"])
            text = out.getvalue()
            self.assertEqual(1, rc)  # the declaration leftover
            self.assertIn("aliased Parsek.Pos::lane onto host: 1 reads", text)
            self.assertIn("differences: 1 {'field': 1}", text)
            with self.assertRaises(SystemExit), contextlib.redirect_stderr(io.StringIO()):
                il_compare.main(paths + ["--il", "--alias-field", "Parsek.Pos::lane"])
        finally:
            shutil.rmtree(d)

    def run_main(self, before, after, args):
        d = tempfile.mkdtemp()
        try:
            paths = []
            for name, text in (("a.il", before), ("b.il", after)):
                paths.append(os.path.join(d, name))
                with open(paths[-1], "w", encoding="utf-8") as fh:
                    fh.write(text)
            out, err = io.StringIO(), io.StringIO()
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                try:
                    rc = il_compare.main(paths + ["--il"] + list(args))
                except SystemExit as exc:
                    rc = exc.code
            return rc, out.getvalue() + err.getvalue()
        finally:
            shutil.rmtree(d)

    def test_two_aliases_onto_one_old_field_are_refused(self):
        # A read redirected to a second lane instance must not fold through a second alias.
        body2 = self.LANE_AFTER_BODY.replace("Parsek.Pos::lane", "Parsek.Pos::lane2")
        after = self.lane_after(body=body2, fields=[self.HOST_DECL, self.LANE_DECL, "class Parsek.Lane lane2"])
        fold = ["--fold", "Parsek.Host=Parsek.Lane"]
        for aliases in (["Parsek.Pos::lane=host", "Parsek.Pos::lane2=host"],     # shared OLD
                        ["Parsek.Pos::lane=host", "Parsek.Pos::lane=other"],     # shared TYPE::NEW
                        ["Parsek.Pos::lane2=lane", "Parsek.Pos::lane=host"]):    # a chain
            args = fold + sum((["--alias-field", a] for a in aliases), [])
            rc, out = self.run_main(self.lane_before(), after, args)
            self.assertEqual(2, rc, (aliases, out))
            self.assertIn("not a pure renaming", out)
        # The same alias on two different types is a renaming per type, so it is allowed.
        rc, out = self.run_main(self.lane_before(), self.lane_after(),
                                fold + ["--alias-field", "Parsek.Pos::lane=host",
                                        "--alias-field", "Parsek.Other::lane=host"])
        self.assertNotIn("not a pure renaming", out)

    @staticmethod
    def _host_and_lane(host_members, lane_members, read):
        body = "    IL_0000:  ldarg.0\n" + read + "    IL_000b:  pop\n    IL_000c:  ret\n"
        return (".class public auto ansi beforefieldinit Parsek.Host\n  extends [mscorlib]System.Object\n{\n"
                "  .field private class Parsek.Lane lane\n" + host_members
                + IlCompareTests._method("assembly", "Read", body) + "} // end of class Parsek.Host\n"
                ".class assembly auto ansi sealed beforefieldinit Parsek.Lane\n  extends [mscorlib]System.Object\n{\n"
                + lane_members + "} // end of class Parsek.Lane\n")

    TO_LANE = "    IL_0001:  call       instance class Parsek.Lane Parsek.Host::get_Lane()\n"

    def test_a_read_moved_between_same_named_fields_of_old_and_new_is_reported(self):
        # Both builds already hold Lane; Host and Lane each own a `count` (two storages).
        for decl, op, ref in (
                ("int32 count", "ldfld", "int32 Parsek.{0}::count"),
                ("static int32 count", "ldsfld", "int32 Parsek.{0}::count"),
                ("int32 '<Count>k__BackingField'", "ldfld", "int32 Parsek.{0}::'<Count>k__BackingField'"),
                ("class [mscorlib]System.Action Changed", "ldfld", "class [mscorlib]System.Action Parsek.{0}::Changed")):
            members = "  .field assembly %s\n" % decl
            def build(owner):
                hop = self.TO_LANE if owner == "Lane" else ""
                return self._host_and_lane(members, members, hop + "    IL_0006:  %s      %s\n"
                                           % (op, ref.format(owner)))
            rc, out = self.run_main(build("Host"), build("Lane"), ["--fold", "Parsek.Host=Parsek.Lane"])
            self.assertEqual(1, rc, (decl, out))
            self.assertIn("fold-collision", out, decl)
        # A literal constant has no storage (its reads are inlined): not a collision.
        lit = "  .field assembly static literal int32 Max = int32(0x00000005)\n"
        same = self._host_and_lane(lit, lit, "    IL_0006:  ldc.i4.5\n")
        rc, out = self.run_main(same, same, ["--fold", "Parsek.Host=Parsek.Lane"])
        self.assertEqual((0, False), (rc, "fold-collision" in out), out)

    GETTER = ("  .method assembly hidebysig specialname instance int32 get_Count() cil managed\n  {{\n"
              "    IL_0000:  ldarg.0\n{0}    IL_0006:  ret\n  }} // end of method X::get_Count\n\n")

    def test_a_call_moved_between_same_named_methods_of_old_and_new_is_reported(self):
        host_get = self.GETTER.format("    IL_0001:  ldfld      int32 Parsek.Host::a\n")
        lane_get = self.GETTER.format("    IL_0001:  ldfld      int32 Parsek.Lane::b\n")
        def build(owner):
            hop = self.TO_LANE if owner == "Lane" else ""
            return self._host_and_lane("  .field assembly int32 a\n" + host_get,
                                       "  .field assembly int32 b\n" + lane_get,
                                       hop + "    IL_0006:  callvirt   instance int32 Parsek.%s::get_Count()\n" % owner)
        rc, out = self.run_main(build("Host"), build("Lane"), ["--fold", "Parsek.Host=Parsek.Lane"])
        self.assertEqual(1, rc, out)
        self.assertIn("not one pass-through forwarder", out)
        # A forwarder pair (Host.Count => Lane.Count) is the normal shape: no collision.
        fwd = self.GETTER.format(self.TO_LANE.replace("IL_0001", "IL_0001") +
                                 "    IL_0002:  callvirt   instance int32 Parsek.Lane::get_Count()\n")
        def build_fwd(owner):
            hop = self.TO_LANE if owner == "Lane" else ""
            return self._host_and_lane(fwd, "  .field assembly int32 b\n" + lane_get,
                                       hop + "    IL_0006:  callvirt   instance int32 Parsek.%s::get_Count()\n" % owner)
        rc, out = self.run_main(build_fwd("Host"), build_fwd("Lane"), ["--fold", "Parsek.Host=Parsek.Lane"])
        self.assertNotIn("fold-collision", out)

    # A caller switches from Host::Foo(int32) to Lane::Foo(int32); both builds hold both. The
    # switch is only safe when one Foo is a pure pass-through to the other.
    @staticmethod
    def _foo(owner, body, params="int32 x"):
        return ("  .method assembly hidebysig instance int32 Foo(%s) cil managed\n  {\n%s  }"
                " // end of method %s::Foo\n\n" % (params, body, owner))

    def _foo_pair(self, host_foo, lane_foo, lane_extra=""):
        def build(target):
            hop = self.TO_LANE if target == "Lane" else ""
            caller = ("    IL_0000:  ldarg.0\n" + hop + "    IL_0006:  ldc.i4.1\n"
                      "    IL_0007:  callvirt   instance int32 Parsek.%s::Foo(int32)\n    IL_000c:  ret\n" % target)
            return (".class public auto ansi beforefieldinit Parsek.Host\n  extends [mscorlib]System.Object\n{\n"
                    "  .field private class Parsek.Lane lane\n"
                    "  .method assembly hidebysig instance int32 Caller() cil managed\n  {\n" + caller
                    + "  } // end of method Host::Caller\n\n" + self._foo("Host", host_foo)
                    + "} // end of class Parsek.Host\n"
                    ".class assembly auto ansi sealed beforefieldinit Parsek.Lane\n  extends [mscorlib]System.Object\n{\n"
                    + self._foo("Lane", lane_foo) + lane_extra + "} // end of class Parsek.Lane\n")
        return self.run_main(build("Host"), build("Lane"), ["--fold", "Parsek.Host=Parsek.Lane"])

    RET1 = "    IL_0000:  ldc.i4.1\n    IL_0001:  ret\n"
    RET2 = "    IL_0000:  ldc.i4.2\n    IL_0001:  ret\n"

    def test_a_call_to_an_overload_is_not_a_forwarder(self):
        # Lane::Foo(int32) returns Lane::Foo(int32, bool) = 2; Host::Foo returns 1.
        lane_foo = ("    IL_0000:  ldarg.0\n    IL_0001:  ldarg.1\n    IL_0002:  ldc.i4.1\n"
                    "    IL_0003:  call       instance int32 Parsek.Lane::Foo(int32, bool)\n    IL_0008:  ret\n")
        rc, out = self._foo_pair(self.RET1, lane_foo, self._foo("Lane", self.RET2, "int32 x, bool y"))
        self.assertEqual(1, rc, out)
        self.assertIn("not one pass-through forwarder", out)

    def test_a_self_call_is_not_a_forwarder(self):
        # Host::Foo calls itself on one branch (P4), or forwards to itself outright.
        rec = ("    IL_0000:  ldarg.1\n    IL_0001:  brfalse.s  IL_000a\n    IL_0003:  ldarg.0\n"
               "    IL_0004:  ldc.i4.0\n    IL_0005:  call       instance int32 Parsek.Host::Foo(int32)\n"
               "    IL_0009:  ret\n    IL_000a:  ldc.i4.1\n    IL_000b:  ret\n")
        self_fwd = ("    IL_0000:  ldarg.0\n    IL_0001:  ldarg.1\n"
                    "    IL_0002:  call       instance int32 Parsek.Host::Foo(int32)\n    IL_0007:  ret\n")
        for host_foo in (rec, self_fwd):
            rc, out = self._foo_pair(host_foo, self.RET2)
            self.assertEqual(1, rc, out)
            self.assertIn("not one pass-through forwarder", out)

    def test_a_call_to_the_other_on_one_branch_only_is_not_a_forwarder(self):
        branch = ("    IL_0000:  ldarg.1\n    IL_0001:  brfalse.s  IL_000d\n    IL_0003:  ldarg.0\n"
                  "    IL_0004:  call       instance class Parsek.Lane Parsek.Host::get_Lane()\n    IL_0005:  ldarg.1\n"
                  "    IL_0006:  callvirt   instance int32 Parsek.Lane::Foo(int32)\n"
                  "    IL_000c:  ret\n    IL_000d:  ldc.i4.1\n    IL_000e:  ret\n")
        rc, out = self._foo_pair(branch, self.RET2)
        self.assertEqual(1, rc, out)
        self.assertIn("not one pass-through forwarder", out)

    def test_a_pass_through_forwarder_is_allowed_and_a_changed_argument_is_not(self):
        fwd = ("    IL_0000:  ldarg.0\n    IL_0001:  call       instance class Parsek.Lane Parsek.Host::get_Lane()\n"
               "    IL_0006:  ldarg.1\n    IL_0007:  callvirt   instance int32 Parsek.Lane::Foo(int32)\n"
               "    IL_000c:  ret\n")
        rc, out = self._foo_pair(fwd, self.RET2)
        self.assertNotIn("fold-collision", out)
        for bad in (fwd.replace("    IL_0006:  ldarg.1\n", "    IL_0006:  ldc.i4.7\n"),          # argument replaced
                    fwd.replace("    IL_000c:  ret\n", "    IL_000c:  ldc.i4.1\n    IL_000d:  add\n    IL_000e:  ret\n")):
            rc, out = self._foo_pair(bad, self.RET2)
            self.assertIn("not one pass-through forwarder", out)

    def test_a_static_pass_through_forwarder_is_allowed(self):
        sig = ".method assembly hidebysig static int32 Foo(int32 x) cil managed"
        fwd = "{\n.maxstack 8\nldarg.0\ncall int32 Parsek.Host::Foo(int32)\nret\n}"
        self.assertTrue(il_compare._forwarder_call(sig, fwd, "Parsek.Host"))
        self.assertFalse(il_compare._forwarder_call(sig, fwd.replace("ldarg.0", "ldc.i4.0"), "Parsek.Host"))
        self.assertFalse(il_compare._forwarder_call(sig, fwd.replace("call int32", "call instance int32"),
                                                    "Parsek.Host"))
        five = (".method assembly hidebysig instance void Go(int32 a, int32 b, int32 c, int32 d, "
                "[opt] bool e) cil managed")
        body = ("{\n.param [5] = bool(true)\n.maxstack 8\nldarg.0\nldarg.1\nldarg.2\nldarg.3\n"
                "ldarg.s d\nldarg.s e\ncall instance void Parsek.Host::Go(int32,\nint32,\nint32,\nint32,\nbool)\nnop\nret\n}")
        self.assertTrue(il_compare._forwarder_call(five, body, "Parsek.Host"))
        self.assertFalse(il_compare._forwarder_call(five, body.replace("ldarg.s d\nldarg.s e", "ldarg.s e\nldarg.s d"),
                                                    "Parsek.Host"))

    def test_a_same_named_nested_type_on_old_and_new_is_reported(self):
        nested = ("  .class nested assembly sequential ansi sealed beforefieldinit Entry\n"
                  "    extends [mscorlib]System.ValueType\n  {\n    .field public int32 x\n"
                  "  } // end of class Entry\n\n")
        both = self._host_and_lane(nested, nested, "    IL_0006:  ldc.i4.0\n")
        rc, out = self.run_main(both, both, ["--fold", "Parsek.Host=Parsek.Lane"])
        self.assertEqual(1, rc, out)
        self.assertIn("nested type Entry", out)


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
