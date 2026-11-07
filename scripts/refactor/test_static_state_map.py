"""Unit tests for static_state_map.py.

Run: python3 -m unittest discover -s scripts/refactor -p "test_static_state_map.py" -q
"""

import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import static_state_map as ssm  # noqa: E402

# Two types declare a static field named `pids`: Parsek.Host (the target) and Parsek.Other.
# Host.Tick mutates Host.pids, reads it through a local, passes it to a helper and bumps a
# counter; a lambda inside Host.Tick reads it again; Other.Run touches only Other.pids.
IL = """\
.class private abstract auto ansi sealed beforefieldinit Parsek.Host
  extends [mscorlib]System.Object
{
  .field static assembly initonly [System.Core]System.Collections.Generic.HashSet`1<uint32> pids
  .field private static int32 counter
  .field static assembly literal int32 Limit = int32(0x00000003)
  .field private static class [mscorlib]System.Func`1<float64> '<Clock>k__BackingField'
  .class nested private auto ansi sealed serializable beforefieldinit '<>c'
    extends [mscorlib]System.Object
  {
    .method assembly hidebysig instance bool '<Tick>b__3_0'(uint32 p) cil managed
    {
      IL_0000:  ldsfld     [System.Core]System.Collections.Generic.HashSet`1<uint32> Parsek.Host::pids
      IL_0005:  ldarg.1
      IL_0006:  callvirt   instance bool class [System.Core]System.Collections.Generic.HashSet`1<uint32>::Contains(!0)
      IL_000b:  ret
    } // end of method '<>c'::'<Tick>b__3_0'
  } // end of class <>c
  .method assembly hidebysig static void Tick(uint32 pid) cil managed
  {
    .locals init (class [System.Core]System.Collections.Generic.HashSet`1<uint32> V_0)
    IL_0000:  ldsfld     [System.Core]System.Collections.Generic.HashSet`1<uint32> Parsek.Host::pids
    IL_0005:  ldarg.0
    IL_0006:  callvirt   instance bool class [System.Core]System.Collections.Generic.HashSet`1<uint32>::Add(!0)
    IL_000b:  pop
    IL_000c:  ldsfld     [System.Core]System.Collections.Generic.HashSet`1<uint32> Parsek.Host::pids
    IL_0011:  stloc.0
    IL_0012:  ldloc.0
    IL_0013:  callvirt   instance int32 class [System.Core]System.Collections.Generic.HashSet`1<uint32>::get_Count()
    IL_0018:  brfalse.s  IL_0028
    IL_001a:  ldsfld     [System.Core]System.Collections.Generic.HashSet`1<uint32> Parsek.Host::pids
    IL_001f:  call       void Parsek.IndexShift::Shift(class [System.Core]System.Collections.Generic.HashSet`1<uint32>)
    IL_0024:  ldc.i4.1
    IL_0025:  br.s       IL_0029
    IL_0028:  ldc.i4.0
    IL_0029:  stsfld     int32 Parsek.Host::counter
    IL_002e:  ldsflda    int32 Parsek.Host::counter
    IL_0033:  pop
    IL_0034:  call       class [mscorlib]System.Func`1<float64> Parsek.Host::get_Clock()
    IL_0039:  pop
    IL_003a:  ret
  } // end of method Host::Tick
} // end of class Parsek.Host

.class private abstract auto ansi sealed beforefieldinit Parsek.Other
  extends [mscorlib]System.Object
{
  .field static assembly initonly [System.Core]System.Collections.Generic.HashSet`1<uint32> pids
  .method assembly hidebysig static void Run() cil managed
  {
    IL_0000:  ldsfld     [System.Core]System.Collections.Generic.HashSet`1<uint32> Parsek.Other::pids
    IL_0005:  callvirt   instance void class [System.Core]System.Collections.Generic.HashSet`1<uint32>::Clear()
    IL_000a:  call       void Parsek.Host::Tick(uint32)
    IL_000f:  ret
  } // end of method Other::Run
} // end of class Parsek.Other
"""


class AnalyzeTests(unittest.TestCase):
    def setUp(self):
        self.fields, self.access, self.calls = ssm.analyze(IL.splitlines(), "Parsek.Host")

    def test_lists_static_non_literal_fields_and_auto_properties(self):
        self.assertEqual(sorted(self.fields), ["Clock", "counter", "pids"])
        self.assertTrue(self.fields["pids"]["initonly"])
        self.assertTrue(self.fields["Clock"]["autoProperty"])

    def test_a_same_named_field_of_another_type_is_not_attributed(self):
        self.assertNotIn("Parsek.Other::Run", self.access["pids"])

    def test_access_kinds_follow_the_loaded_value(self):
        tick = self.access["pids"]["Parsek.Host::Tick"]
        self.assertIn("mutate:Add", tick)
        self.assertIn("read:get_Count", tick)  # through a local
        self.assertIn("arg->IndexShift::Shift", tick)
        counter = self.access["counter"]["Parsek.Host::Tick"]
        self.assertEqual(counter, {"reassign", "addr"})  # stsfld after a ternary merge
        self.assertEqual(self.access["Clock"]["Parsek.Host::Tick"], {"read:get"})

    def test_a_lambda_is_credited_to_the_method_that_wrote_it(self):
        self.assertEqual(self.access["pids"]["Parsek.Host::Tick"] & {"read:Contains"},
                         {"read:Contains"})
        self.assertFalse(any("<" in key for key in self.access["pids"]))

    def test_call_graph_names_root_namespace_callees(self):
        self.assertIn("Parsek.IndexShift::Shift", self.calls["Parsek.Host::Tick"])
        self.assertIn("Parsek.Host::Tick", self.calls["Parsek.Other::Run"])

    def test_preset_fields_scan_a_using_assembly(self):
        using = IL.replace("Parsek.Host::pids", "[Parsek]Parsek.Host::pids")
        _, access, _ = ssm.analyze(using.splitlines(), "Parsek.NotDeclaredHere",
                                   preset_fields=None)
        self.assertEqual(dict(access), {})
        fields, access, _ = ssm.analyze(
            using.splitlines(), "Parsek.Host", preset_fields={"pids": {"il": "", "initonly": True,
                                                                     "autoProperty": False}})
        self.assertIn("mutate:Add", access["pids"]["Parsek.Host::Tick"])


class HelperTests(unittest.TestCase):
    def test_count_params(self):
        self.assertEqual(ssm.count_params("void Parsek.A::M()"), 0)
        self.assertEqual(ssm.count_params(
            "instance bool class [x]D`2<int32,string>::TryGetValue(!0, !1&)"), 2)
        self.assertEqual(ssm.count_params(
            "void Parsek.A::M(class [x]D`2<int32,string>, int32)"), 2)

    def test_source_method_folds_generated_members(self):
        self.assertEqual(ssm.source_method("Parsek.A/'<>c__DisplayClass3_0'", "'<Run>b__0'"),
                         ("Parsek.A", "Run"))
        self.assertEqual(ssm.source_method("Parsek.A/'<Walk>d__7'", "MoveNext"),
                         ("Parsek.A", "Walk"))
        self.assertEqual(ssm.source_method("Parsek.A", "'<Run>g__Local|3_0'"), ("Parsek.A", "Run"))


if __name__ == "__main__":
    unittest.main()
