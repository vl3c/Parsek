// Cut from an ikdasm dump of the Debug Parsek.dll built from b5877e51f (2026-11-11).
// Every block below is copied verbatim; whole members between the blocks are omitted
// (scripts/arch/test_ilfacts.py reads it; keep it small and never hand-edit a block).

.class private abstract auto ansi sealed beforefieldinit Parsek.HeaviestSpawnBuildTypeExtensions
       extends [mscorlib]System.Object
{
  .custom instance void [mscorlib]System.Runtime.CompilerServices.ExtensionAttribute::.ctor() = ( 01 00 00 00 ) 
  .method assembly hidebysig static string 
          ToLogToken(valuetype Parsek.HeaviestSpawnBuildType buildType) cil managed
  {
    .custom instance void [mscorlib]System.Runtime.CompilerServices.ExtensionAttribute::.ctor() = ( 01 00 00 00 ) 
    // Code size       61 (0x3d)
    .maxstack  2
    .locals init (valuetype Parsek.HeaviestSpawnBuildType V_0,
             valuetype Parsek.HeaviestSpawnBuildType V_1,
             string V_2)
    IL_0000:  nop
    IL_0001:  ldarg.0
    IL_0002:  stloc.1
    IL_0003:  ldloc.1
    IL_0004:  stloc.0
    IL_0005:  ldloc.0
    IL_0006:  ldc.i4.1
    IL_0007:  sub
    IL_0008:  switch     ( 
                          IL_001b,
                          IL_0023,
                          IL_002b)
    IL_0019:  br.s       IL_0033

    IL_001b:  ldstr      "recording-start-snapshot"
    IL_0020:  stloc.2
    IL_0021:  br.s       IL_003b

    IL_0023:  ldstr      "vessel-snapshot"
    IL_0028:  stloc.2
    IL_0029:  br.s       IL_003b

    IL_002b:  ldstr      "sphere-fallback"
    IL_0030:  stloc.2
    IL_0031:  br.s       IL_003b

    IL_0033:  ldstr      "none"
    IL_0038:  stloc.2
    IL_0039:  br.s       IL_003b

    IL_003b:  ldloc.2
    IL_003c:  ret
  } // end of method HeaviestSpawnBuildTypeExtensions::ToLogToken

} // end of class Parsek.HeaviestSpawnBuildTypeExtensions

.class private abstract auto ansi sealed beforefieldinit Parsek.RevertToLaunchInterceptor
       extends [mscorlib]System.Object
{
  .custom instance void ['0Harmony']HarmonyLib.HarmonyPatch::.ctor([mscorlib]System.Type,
                                                                   string) = ( 01 00 54 46 6C 69 67 68 74 44 72 69 76 65 72 2C   // ..TFlightDriver,
                                                                               20 41 73 73 65 6D 62 6C 79 2D 43 53 68 61 72 70   //  Assembly-CSharp
                                                                               2C 20 56 65 72 73 69 6F 6E 3D 30 2E 30 2E 30 2E   // , Version=0.0.0.
                                                                               30 2C 20 43 75 6C 74 75 72 65 3D 6E 65 75 74 72   // 0, Culture=neutr
                                                                               61 6C 2C 20 50 75 62 6C 69 63 4B 65 79 54 6F 6B   // al, PublicKeyTok
                                                                               65 6E 3D 6E 75 6C 6C 0E 52 65 76 65 72 74 54 6F   // en=null.RevertTo
                                                                               4C 61 75 6E 63 68 00 00 )                         // Launch..
  .method assembly hidebysig static bool 
          Prefix() cil managed
  {
    .custom instance void ['0Harmony']HarmonyLib.HarmonyPrefix::.ctor() = ( 01 00 00 00 ) 
    // Code size       8 (0x8)
    .maxstack  8
    IL_0000:  ldc.i4.0
    IL_0001:  ldc.i4.1
    IL_0002:  call       bool Parsek.RevertInterceptor::Prefix(valuetype Parsek.RevertTarget,
                                                               ['Assembly-CSharp']EditorFacility)
    IL_0007:  ret
  } // end of method RevertToLaunchInterceptor::Prefix

} // end of class Parsek.RevertToLaunchInterceptor

.class private abstract auto ansi sealed beforefieldinit Parsek.Logistics.RouteIds
       extends [mscorlib]System.Object
{
  .method assembly hidebysig static string 
          Short(string id) cil managed
  {
    // Code size       44 (0x2c)
    .maxstack  3
    .locals init (bool V_0,
             string V_1)
    IL_0000:  nop
    IL_0001:  ldarg.0
    IL_0002:  call       bool [mscorlib]System.String::IsNullOrEmpty(string)
    IL_0007:  stloc.0
    IL_0008:  ldloc.0
    IL_0009:  brfalse.s  IL_0013

    IL_000b:  ldstr      "<no-id>"
    IL_0010:  stloc.1
    IL_0011:  br.s       IL_002a

    IL_0013:  ldarg.0
    IL_0014:  callvirt   instance int32 [mscorlib]System.String::get_Length()
    IL_0019:  ldc.i4.8
    IL_001a:  bgt.s      IL_001f

    IL_001c:  ldarg.0
    IL_001d:  br.s       IL_0027

    IL_001f:  ldarg.0
    IL_0020:  ldc.i4.0
    IL_0021:  ldc.i4.8
    IL_0022:  callvirt   instance string [mscorlib]System.String::Substring(int32,
                                                                            int32)
    IL_0027:  stloc.1
    IL_0028:  br.s       IL_002a

    IL_002a:  ldloc.1
    IL_002b:  ret
  } // end of method RouteIds::Short

  .method assembly hidebysig static string 
          Short(class Parsek.Logistics.Route route) cil managed
  {
    // Code size       23 (0x17)
    .maxstack  1
    .locals init (string V_0)
    IL_0000:  nop
    IL_0001:  ldarg.0
    IL_0002:  brtrue.s   IL_0007

    IL_0004:  ldnull
    IL_0005:  br.s       IL_000d

    IL_0007:  ldarg.0
    IL_0008:  ldfld      string Parsek.Logistics.Route::Id
    IL_000d:  call       string Parsek.Logistics.RouteIds::Short(string)
    IL_0012:  stloc.0
    IL_0013:  br.s       IL_0015

    IL_0015:  ldloc.0
    IL_0016:  ret
  } // end of method RouteIds::Short

} // end of class Parsek.Logistics.RouteIds

.class private abstract auto ansi sealed beforefieldinit Parsek.Patches.GhostOrbitDominantBodyPatch
       extends [mscorlib]System.Object
{
  .custom instance void ['0Harmony']HarmonyLib.HarmonyPatch::.ctor([mscorlib]System.Type,
                                                                   string) = ( 01 00 53 4F 72 62 69 74 44 72 69 76 65 72 2C 20   // ..SOrbitDriver, 
                                                                               41 73 73 65 6D 62 6C 79 2D 43 53 68 61 72 70 2C   // Assembly-CSharp,
                                                                               20 56 65 72 73 69 6F 6E 3D 30 2E 30 2E 30 2E 30   //  Version=0.0.0.0
                                                                               2C 20 43 75 6C 74 75 72 65 3D 6E 65 75 74 72 61   // , Culture=neutra
                                                                               6C 2C 20 50 75 62 6C 69 63 4B 65 79 54 6F 6B 65   // l, PublicKeyToke
                                                                               6E 3D 6E 75 6C 6C 11 43 68 65 63 6B 44 6F 6D 69   // n=null.CheckDomi
                                                                               6E 61 6E 74 42 6F 64 79 00 00 )                   // nantBody..
  .class auto ansi sealed nested private beforefieldinit '<>c__DisplayClass1_0'
         extends [mscorlib]System.Object
  {
    .custom instance void [mscorlib]System.Runtime.CompilerServices.CompilerGeneratedAttribute::.ctor() = ( 01 00 00 00 ) 
    .field public uint32 pid
    .method public hidebysig specialname rtspecialname 
            instance void  .ctor() cil managed
    {
      // Code size       8 (0x8)
      .maxstack  8
      IL_0000:  ldarg.0
      IL_0001:  call       instance void [mscorlib]System.Object::.ctor()
      IL_0006:  nop
      IL_0007:  ret
    } // end of method '<>c__DisplayClass1_0'::.ctor

    .method assembly hidebysig instance string 
            '<Prefix>b__0'() cil managed
    {
      // Code size       27 (0x1b)
      .maxstack  8
      IL_0000:  call       [mscorlib]System.Globalization.CultureInfo [mscorlib]System.Globalization.CultureInfo::get_InvariantCulture()
      IL_0005:  ldstr      "Suppressed KSP on-rails dominant-body SOI transiti"
      + "on for ghost pid={0} (Parsek owns this orbit)"
      IL_000a:  ldarg.0
      IL_000b:  ldfld      uint32 Parsek.Patches.GhostOrbitDominantBodyPatch/'<>c__DisplayClass1_0'::pid
      IL_0010:  box        [mscorlib]System.UInt32
      IL_0015:  call       string [mscorlib]System.String::Format([mscorlib]System.IFormatProvider,
                                                                  string,
                                                                  object)
      IL_001a:  ret
    } // end of method '<>c__DisplayClass1_0'::'<Prefix>b__0'

  } // end of class '<>c__DisplayClass1_0'

  .method assembly hidebysig static bool 
          ShouldSkipDominantBodyCheck(uint32 persistentId) cil managed
  {
    // Code size       17 (0x11)
    .maxstack  2
    .locals init (bool V_0)
    IL_0000:  nop
    IL_0001:  ldsfld     [System.Core]System.Collections.Generic.HashSet`1<uint32> Parsek.GhostMapPresence::ghostMapVesselPids
    IL_0006:  ldarg.0
    IL_0007:  callvirt   instance bool [System.Core]System.Collections.Generic.HashSet`1<uint32>::Contains(!0)
    IL_000c:  stloc.0
    IL_000d:  br.s       IL_000f

    IL_000f:  ldloc.0
    IL_0010:  ret
  } // end of method GhostOrbitDominantBodyPatch::ShouldSkipDominantBodyCheck

  .method private hidebysig static bool  Prefix(['Assembly-CSharp']OrbitDriver __instance,
                                                bool& __result) cil managed
  {
    // Code size       139 (0x8b)
    .maxstack  4
    .locals init (class Parsek.Patches.GhostOrbitDominantBodyPatch/'<>c__DisplayClass1_0' V_0,
             ['Assembly-CSharp']Vessel V_1,
             bool V_2,
             bool V_3)
    IL_0000:  newobj     instance void Parsek.Patches.GhostOrbitDominantBodyPatch/'<>c__DisplayClass1_0'::.ctor()
    IL_0005:  stloc.0
    IL_0006:  nop
    IL_0007:  ldarg.0
    IL_0008:  ldnull
    IL_0009:  call       bool [UnityEngine.CoreModule]UnityEngine.Object::op_Inequality([UnityEngine.CoreModule]UnityEngine.Object,
                                                                                        [UnityEngine.CoreModule]UnityEngine.Object)
    IL_000e:  brtrue.s   IL_0013

    IL_0010:  ldnull
    IL_0011:  br.s       IL_0019

    IL_0013:  ldarg.0
    IL_0014:  ldfld      ['Assembly-CSharp']Vessel ['Assembly-CSharp']OrbitDriver::vessel
    IL_0019:  stloc.1
    IL_001a:  ldloc.1
    IL_001b:  ldnull
    IL_001c:  call       bool [UnityEngine.CoreModule]UnityEngine.Object::op_Equality([UnityEngine.CoreModule]UnityEngine.Object,
                                                                                      [UnityEngine.CoreModule]UnityEngine.Object)
    IL_0021:  brtrue.s   IL_0033

    IL_0023:  ldloc.1
    IL_0024:  ldfld      uint32 ['Assembly-CSharp']Vessel::persistentId
    IL_0029:  call       bool Parsek.Patches.GhostOrbitDominantBodyPatch::ShouldSkipDominantBodyCheck(uint32)
    IL_002e:  ldc.i4.0
    IL_002f:  ceq
    IL_0031:  br.s       IL_0034

    IL_0033:  ldc.i4.1
    IL_0034:  stloc.2
    IL_0035:  ldloc.2
    IL_0036:  brfalse.s  IL_003c

    IL_0038:  ldc.i4.1
    IL_0039:  stloc.3
    IL_003a:  br.s       IL_0089

    IL_003c:  ldarg.1
    IL_003d:  ldc.i4.0
    IL_003e:  stind.i1
    IL_003f:  ldloc.0
    IL_0040:  ldloc.1
    IL_0041:  ldfld      uint32 ['Assembly-CSharp']Vessel::persistentId
    IL_0046:  stfld      uint32 Parsek.Patches.GhostOrbitDominantBodyPatch/'<>c__DisplayClass1_0'::pid
    IL_004b:  ldstr      "GhostMap"
    IL_0050:  ldstr      "dominant-body-suppressed-"
    IL_0055:  ldloc.0
    IL_0056:  ldflda     uint32 Parsek.Patches.GhostOrbitDominantBodyPatch/'<>c__DisplayClass1_0'::pid
    IL_005b:  call       [mscorlib]System.Globalization.CultureInfo [mscorlib]System.Globalization.CultureInfo::get_InvariantCulture()
    IL_0060:  call       instance string [mscorlib]System.UInt32::ToString([mscorlib]System.IFormatProvider)
    IL_0065:  call       string [mscorlib]System.String::Concat(string,
                                                                string)
    IL_006a:  ldloc.0
    IL_006b:  ldftn      instance string Parsek.Patches.GhostOrbitDominantBodyPatch/'<>c__DisplayClass1_0'::'<Prefix>b__0'()
    IL_0071:  newobj     instance void [mscorlib]System.Func`1<string>::.ctor(object,
                                                                              native int)
    IL_0076:  ldc.r8     5
    IL_007f:  call       void Parsek.ParsekLog::VerboseRateLimited(string,
                                                                   string,
                                                                   [mscorlib]System.Func`1<string>,
                                                                   float64)
    IL_0084:  nop
    IL_0085:  ldc.i4.0
    IL_0086:  stloc.3
    IL_0087:  br.s       IL_0089

    IL_0089:  ldloc.3
    IL_008a:  ret
  } // end of method GhostOrbitDominantBodyPatch::Prefix

} // end of class Parsek.Patches.GhostOrbitDominantBodyPatch

.class private abstract auto ansi sealed beforefieldinit Parsek.CommittedActionDialog
       extends [mscorlib]System.Object
{
  .class auto ansi serializable sealed nested private beforefieldinit '<>c'
         extends [mscorlib]System.Object
  {
    .custom instance void [mscorlib]System.Runtime.CompilerServices.CompilerGeneratedAttribute::.ctor() = ( 01 00 00 00 ) 
    .field public static initonly class Parsek.CommittedActionDialog/'<>c' '<>9'
    .field public static ['Assembly-CSharp']Callback '<>9__1_0'
    .method private hidebysig specialname rtspecialname static 
            void  .cctor() cil managed
    {
      // Code size       11 (0xb)
      .maxstack  8
      IL_0000:  newobj     instance void Parsek.CommittedActionDialog/'<>c'::.ctor()
      IL_0005:  stsfld     class Parsek.CommittedActionDialog/'<>c' Parsek.CommittedActionDialog/'<>c'::'<>9'
      IL_000a:  ret
    } // end of method '<>c'::.cctor

    .method public hidebysig specialname rtspecialname 
            instance void  .ctor() cil managed
    {
      // Code size       8 (0x8)
      .maxstack  8
      IL_0000:  ldarg.0
      IL_0001:  call       instance void [mscorlib]System.Object::.ctor()
      IL_0006:  nop
      IL_0007:  ret
    } // end of method '<>c'::.ctor

    .method assembly hidebysig instance void 
            '<ShowBlocked>b__1_0'() cil managed
    {
      // Code size       2 (0x2)
      .maxstack  8
      IL_0000:  nop
      IL_0001:  ret
    } // end of method '<>c'::'<ShowBlocked>b__1_0'

  } // end of class '<>c'

  .field static assembly [mscorlib]System.Action`3<string,string,string> TestHookForTesting
  .method assembly hidebysig static void 
          ShowBlocked(string actionDescription,
                      string reason,
                      string resourceDetail) cil managed
  {
    // Code size       271 (0x10f)
    .maxstack  12
    .locals init (string V_0,
             [mscorlib]System.Action`3<string,string,string> V_1,
             bool V_2,
             bool V_3,
             ['Assembly-CSharp']DialogGUIBase[] V_4)
    IL_0000:  nop
    IL_0001:  ldarg.0
    IL_0002:  ldstr      "\n\n"
    IL_0007:  ldarg.1
    IL_0008:  call       string [mscorlib]System.String::Concat(string,
                                                                string,
                                                                string)
    IL_000d:  stloc.0
    IL_000e:  ldarg.2
    IL_000f:  call       bool [mscorlib]System.String::IsNullOrEmpty(string)
    IL_0014:  ldc.i4.0
    IL_0015:  ceq
    IL_0017:  stloc.2
    IL_0018:  ldloc.2
    IL_0019:  brfalse.s  IL_0028

    IL_001b:  ldloc.0
    IL_001c:  ldstr      "\n\n"
    IL_0021:  ldarg.2
    IL_0022:  call       string [mscorlib]System.String::Concat(string,
                                                                string,
                                                                string)
    IL_0027:  stloc.0
    IL_0028:  ldstr      "CommittedAction"
    IL_002d:  ldc.i4.5
    IL_002e:  newarr     [mscorlib]System.String
    IL_0033:  dup
    IL_0034:  ldc.i4.0
    IL_0035:  ldstr      "Blocked action: "
    IL_003a:  stelem.ref
    IL_003b:  dup
    IL_003c:  ldc.i4.1
    IL_003d:  ldarg.0
    IL_003e:  stelem.ref
    IL_003f:  dup
    IL_0040:  ldc.i4.2
    IL_0041:  ldstr      " - "
    IL_0046:  stelem.ref
    IL_0047:  dup
    IL_0048:  ldc.i4.3
    IL_0049:  ldarg.1
    IL_004a:  stelem.ref
    IL_004b:  dup
    IL_004c:  ldc.i4.4
    IL_004d:  ldarg.2
    IL_004e:  call       bool [mscorlib]System.String::IsNullOrEmpty(string)
    IL_0053:  brfalse.s  IL_005c

    IL_0055:  ldstr      ""
    IL_005a:  br.s       IL_006c

    IL_005c:  ldstr      " ("
    IL_0061:  ldarg.2
    IL_0062:  ldstr      ")"
    IL_0067:  call       string [mscorlib]System.String::Concat(string,
                                                                string,
                                                                string)
    IL_006c:  stelem.ref
    IL_006d:  call       string [mscorlib]System.String::Concat(string[])
    IL_0072:  call       void Parsek.ParsekLog::Info(string,
                                                     string)
    IL_0077:  nop
    IL_0078:  ldsfld     [mscorlib]System.Action`3<string,string,string> Parsek.CommittedActionDialog::TestHookForTesting
    IL_007d:  stloc.1
    IL_007e:  ldloc.1
    IL_007f:  ldnull
    IL_0080:  cgt.un
    IL_0082:  stloc.3
    IL_0083:  ldloc.3
    IL_0084:  brfalse.s  IL_0093

    IL_0086:  nop
    IL_0087:  ldloc.1
    IL_0088:  ldarg.0
    IL_0089:  ldarg.1
    IL_008a:  ldarg.2
    IL_008b:  callvirt   instance void [mscorlib]System.Action`3<string,string,string>::Invoke(!0,
                                                                                               !1,
                                                                                               !2)
    IL_0090:  nop
    IL_0091:  br.s       IL_010e

    IL_0093:  ldc.r4     0.5
    IL_0098:  ldc.r4     0.5
    IL_009d:  newobj     instance void [UnityEngine.CoreModule]UnityEngine.Vector2::.ctor(float32,
                                                                                          float32)
    IL_00a2:  ldc.r4     0.5
    IL_00a7:  ldc.r4     0.5
    IL_00ac:  newobj     instance void [UnityEngine.CoreModule]UnityEngine.Vector2::.ctor(float32,
                                                                                          float32)
    IL_00b1:  ldstr      "ParsekResourceBlock"
    IL_00b6:  ldloc.0
    IL_00b7:  ldstr      "Action Blocked"
    IL_00bc:  call       ['Assembly-CSharp']UISkinDef ['Assembly-CSharp']HighLogic::get_UISkin()
    IL_00c1:  ldc.i4.1
    IL_00c2:  newarr     ['Assembly-CSharp']DialogGUIButton
    IL_00c7:  dup
    IL_00c8:  ldc.i4.0
    IL_00c9:  ldstr      "OK"
    IL_00ce:  ldsfld     ['Assembly-CSharp']Callback Parsek.CommittedActionDialog/'<>c'::'<>9__1_0'
    IL_00d3:  dup
    IL_00d4:  brtrue.s   IL_00ed

    IL_00d6:  pop
    IL_00d7:  ldsfld     class Parsek.CommittedActionDialog/'<>c' Parsek.CommittedActionDialog/'<>c'::'<>9'
    IL_00dc:  ldftn      instance void Parsek.CommittedActionDialog/'<>c'::'<ShowBlocked>b__1_0'()
    IL_00e2:  newobj     instance void ['Assembly-CSharp']Callback::.ctor(object,
                                                                          native int)
    IL_00e7:  dup
    IL_00e8:  stsfld     ['Assembly-CSharp']Callback Parsek.CommittedActionDialog/'<>c'::'<>9__1_0'
    IL_00ed:  newobj     instance void ['Assembly-CSharp']DialogGUIButton::.ctor(string,
                                                                                 ['Assembly-CSharp']Callback)
    IL_00f2:  stelem.ref
    IL_00f3:  stloc.s    V_4
    IL_00f5:  ldloc.s    V_4
    IL_00f7:  newobj     instance void ['Assembly-CSharp']MultiOptionDialog::.ctor(string,
                                                                                   string,
                                                                                   string,
                                                                                   ['Assembly-CSharp']UISkinDef,
                                                                                   ['Assembly-CSharp']DialogGUIBase[])
    IL_00fc:  ldc.i4.0
    IL_00fd:  call       ['Assembly-CSharp']UISkinDef ['Assembly-CSharp']HighLogic::get_UISkin()
    IL_0102:  ldc.i4.1
    IL_0103:  ldstr      ""
    IL_0108:  call       ['Assembly-CSharp']PopupDialog ['Assembly-CSharp']PopupDialog::SpawnPopupDialog([UnityEngine.CoreModule]UnityEngine.Vector2,
                                                                                                         [UnityEngine.CoreModule]UnityEngine.Vector2,
                                                                                                         ['Assembly-CSharp']MultiOptionDialog,
                                                                                                         bool,
                                                                                                         ['Assembly-CSharp']UISkinDef,
                                                                                                         bool,
                                                                                                         string)
    IL_010d:  pop
    IL_010e:  ret
  } // end of method CommittedActionDialog::ShowBlocked

} // end of class Parsek.CommittedActionDialog

.class private abstract auto ansi beforefieldinit Parsek.MapRender.TracedPhase
       extends Parsek.MapRender.TrajectoryPhase
{
  .class auto ansi sealed nested private beforefieldinit '<Emit>d__5'
         extends [mscorlib]System.Object
         implements [mscorlib]System.Collections.Generic.IEnumerable`1<valuetype Parsek.MapRender.RenderSegment>,
                    [mscorlib]System.Collections.IEnumerable,
                    [mscorlib]System.Collections.Generic.IEnumerator`1<valuetype Parsek.MapRender.RenderSegment>,
                    [mscorlib]System.IDisposable,
                    [mscorlib]System.Collections.IEnumerator
  {
    .custom instance void [mscorlib]System.Runtime.CompilerServices.CompilerGeneratedAttribute::.ctor() = ( 01 00 00 00 ) 
    .field private int32 '<>1__state'
    .field private valuetype Parsek.MapRender.RenderSegment '<>2__current'
    .field private int32 '<>l__initialThreadId'
    .field private valuetype Parsek.MapRender.SampleContext ctx
    .field public valuetype Parsek.MapRender.SampleContext '<>3__ctx'
    .field public class Parsek.MapRender.TracedPhase '<>4__this'
    .method public hidebysig specialname rtspecialname 
            instance void  .ctor(int32 '<>1__state') cil managed
    {
      .custom instance void [mscorlib]System.Diagnostics.DebuggerHiddenAttribute::.ctor() = ( 01 00 00 00 ) 
      // Code size       26 (0x1a)
      .maxstack  8
      IL_0000:  ldarg.0
      IL_0001:  call       instance void [mscorlib]System.Object::.ctor()
      IL_0006:  nop
      IL_0007:  ldarg.0
      IL_0008:  ldarg.1
      IL_0009:  stfld      int32 Parsek.MapRender.TracedPhase/'<Emit>d__5'::'<>1__state'
      IL_000e:  ldarg.0
      IL_000f:  call       int32 [mscorlib]System.Environment::get_CurrentManagedThreadId()
      IL_0014:  stfld      int32 Parsek.MapRender.TracedPhase/'<Emit>d__5'::'<>l__initialThreadId'
      IL_0019:  ret
    } // end of method '<Emit>d__5'::.ctor

    .method private hidebysig newslot virtual final 
            instance void  System.IDisposable.Dispose() cil managed
    {
      .custom instance void [mscorlib]System.Diagnostics.DebuggerHiddenAttribute::.ctor() = ( 01 00 00 00 ) 
      .override [mscorlib]System.IDisposable::Dispose
      // Code size       1 (0x1)
      .maxstack  8
      IL_0000:  ret
    } // end of method '<Emit>d__5'::System.IDisposable.Dispose

    .method private hidebysig newslot virtual final 
            instance bool  MoveNext() cil managed
    {
      .override [mscorlib]System.Collections.IEnumerator::MoveNext
      // Code size       166 (0xa6)
      .maxstack  10
      .locals init (int32 V_0)
      IL_0000:  ldarg.0
      IL_0001:  ldfld      int32 Parsek.MapRender.TracedPhase/'<Emit>d__5'::'<>1__state'
      IL_0006:  stloc.0
      IL_0007:  ldloc.0
      IL_0008:  brfalse.s  IL_0012

      IL_000a:  br.s       IL_000c

      IL_000c:  ldloc.0
      IL_000d:  ldc.i4.1
      IL_000e:  beq.s      IL_0014

      IL_0010:  br.s       IL_0019

      IL_0012:  br.s       IL_001b

      IL_0014:  br         IL_009d

      IL_0019:  ldc.i4.0
      IL_001a:  ret

      IL_001b:  ldarg.0
      IL_001c:  ldc.i4.m1
      IL_001d:  stfld      int32 Parsek.MapRender.TracedPhase/'<Emit>d__5'::'<>1__state'
      IL_0022:  nop
      IL_0023:  ldarg.0
      IL_0024:  ldarg.0
      IL_0025:  ldfld      class Parsek.MapRender.TracedPhase Parsek.MapRender.TracedPhase/'<Emit>d__5'::'<>4__this'
      IL_002a:  callvirt   instance valuetype Parsek.MapRender.SegmentKind Parsek.MapRender.TracedPhase::get_LegacyKind()
      IL_002f:  ldc.i4.2
      IL_0030:  ldarg.0
      IL_0031:  ldfld      class Parsek.MapRender.TracedPhase Parsek.MapRender.TracedPhase/'<Emit>d__5'::'<>4__this'
      IL_0036:  call       instance float64 Parsek.MapRender.TrajectoryPhase::get_StartUt()
      IL_003b:  ldarg.0
      IL_003c:  ldfld      class Parsek.MapRender.TracedPhase Parsek.MapRender.TracedPhase/'<Emit>d__5'::'<>4__this'
      IL_0041:  call       instance float64 Parsek.MapRender.TrajectoryPhase::get_EndUt()
      IL_0046:  ldarg.0
      IL_0047:  ldfld      class Parsek.MapRender.TracedPhase Parsek.MapRender.TracedPhase/'<Emit>d__5'::'<>4__this'
      IL_004c:  ldarg.0
      IL_004d:  ldfld      valuetype Parsek.MapRender.SampleContext Parsek.MapRender.TracedPhase/'<Emit>d__5'::ctx
      IL_0052:  call       instance string Parsek.MapRender.TracedPhase::ResolveFrameBodyName(valuetype Parsek.MapRender.SampleContext)
      IL_0057:  call       valuetype Parsek.MapRender.SegmentPayload Parsek.MapRender.SegmentPayload::get_Traced()
      IL_005c:  ldarg.0
      IL_005d:  ldfld      class Parsek.MapRender.TracedPhase Parsek.MapRender.TracedPhase/'<Emit>d__5'::'<>4__this'
      IL_0062:  call       instance valuetype Parsek.MapRender.SegmentProvenance Parsek.MapRender.TrajectoryPhase::get_Provenance()
      IL_0067:  ldc.i4.3
      IL_0068:  ceq
      IL_006a:  ldarg.0
      IL_006b:  ldfld      class Parsek.MapRender.TracedPhase Parsek.MapRender.TracedPhase/'<Emit>d__5'::'<>4__this'
      IL_0070:  call       instance class Parsek.MapRender.PhaseSeam Parsek.MapRender.TrajectoryPhase::get_LeadingSeam()
      IL_0075:  call       valuetype Parsek.MapRender.SeamKind Parsek.MapRender.PhaseGeometry::ToLegacySeam(class Parsek.MapRender.PhaseSeam)
      IL_007a:  ldarg.0
      IL_007b:  ldfld      class Parsek.MapRender.TracedPhase Parsek.MapRender.TracedPhase/'<Emit>d__5'::'<>4__this'
      IL_0080:  call       instance class Parsek.MapRender.PhaseSeam Parsek.MapRender.TrajectoryPhase::get_TrailingSeam()
      IL_0085:  call       valuetype Parsek.MapRender.SeamKind Parsek.MapRender.PhaseGeometry::ToLegacySeam(class Parsek.MapRender.PhaseSeam)
      IL_008a:  newobj     instance void Parsek.MapRender.RenderSegment::.ctor(valuetype Parsek.MapRender.SegmentKind,
                                                                               valuetype Parsek.MapRender.Treatment,
                                                                               float64,
                                                                               float64,
                                                                               string,
                                                                               valuetype Parsek.MapRender.SegmentPayload,
                                                                               bool,
                                                                               valuetype Parsek.MapRender.SeamKind,
                                                                               valuetype Parsek.MapRender.SeamKind)
      IL_008f:  stfld      valuetype Parsek.MapRender.RenderSegment Parsek.MapRender.TracedPhase/'<Emit>d__5'::'<>2__current'
      IL_0094:  ldarg.0
      IL_0095:  ldc.i4.1
      IL_0096:  stfld      int32 Parsek.MapRender.TracedPhase/'<Emit>d__5'::'<>1__state'
      IL_009b:  ldc.i4.1
      IL_009c:  ret

      IL_009d:  ldarg.0
      IL_009e:  ldc.i4.m1
      IL_009f:  stfld      int32 Parsek.MapRender.TracedPhase/'<Emit>d__5'::'<>1__state'
      IL_00a4:  ldc.i4.0
      IL_00a5:  ret
    } // end of method '<Emit>d__5'::MoveNext

    .method private hidebysig newslot specialname virtual final 
            instance valuetype Parsek.MapRender.RenderSegment 
            'System.Collections.Generic.IEnumerator<Parsek.MapRender.RenderSegment>.get_Current'() cil managed
    {
      .custom instance void [mscorlib]System.Diagnostics.DebuggerHiddenAttribute::.ctor() = ( 01 00 00 00 ) 
      .override  method instance !0 [mscorlib]System.Collections.Generic.IEnumerator`1<valuetype Parsek.MapRender.RenderSegment>::get_Current()
      // Code size       7 (0x7)
      .maxstack  8
      IL_0000:  ldarg.0
      IL_0001:  ldfld      valuetype Parsek.MapRender.RenderSegment Parsek.MapRender.TracedPhase/'<Emit>d__5'::'<>2__current'
      IL_0006:  ret
    } // end of method '<Emit>d__5'::'System.Collections.Generic.IEnumerator<Parsek.MapRender.RenderSegment>.get_Current'

    .method private hidebysig newslot virtual final 
            instance void  System.Collections.IEnumerator.Reset() cil managed
    {
      .custom instance void [mscorlib]System.Diagnostics.DebuggerHiddenAttribute::.ctor() = ( 01 00 00 00 ) 
      .override [mscorlib]System.Collections.IEnumerator::Reset
      // Code size       6 (0x6)
      .maxstack  8
      IL_0000:  newobj     instance void [mscorlib]System.NotSupportedException::.ctor()
      IL_0005:  throw
    } // end of method '<Emit>d__5'::System.Collections.IEnumerator.Reset

    .method private hidebysig newslot specialname virtual final 
            instance object  System.Collections.IEnumerator.get_Current() cil managed
    {
      .custom instance void [mscorlib]System.Diagnostics.DebuggerHiddenAttribute::.ctor() = ( 01 00 00 00 ) 
      .override [mscorlib]System.Collections.IEnumerator::get_Current
      // Code size       12 (0xc)
      .maxstack  8
      IL_0000:  ldarg.0
      IL_0001:  ldfld      valuetype Parsek.MapRender.RenderSegment Parsek.MapRender.TracedPhase/'<Emit>d__5'::'<>2__current'
      IL_0006:  box        Parsek.MapRender.RenderSegment
      IL_000b:  ret
    } // end of method '<Emit>d__5'::System.Collections.IEnumerator.get_Current

    .method private hidebysig newslot virtual final 
            instance [mscorlib]System.Collections.Generic.IEnumerator`1<valuetype Parsek.MapRender.RenderSegment> 
            'System.Collections.Generic.IEnumerable<Parsek.MapRender.RenderSegment>.GetEnumerator'() cil managed
    {
      .custom instance void [mscorlib]System.Diagnostics.DebuggerHiddenAttribute::.ctor() = ( 01 00 00 00 ) 
      .override  method instance [mscorlib]System.Collections.Generic.IEnumerator`1<!0> [mscorlib]System.Collections.Generic.IEnumerable`1<valuetype Parsek.MapRender.RenderSegment>::GetEnumerator()
      // Code size       67 (0x43)
      .maxstack  2
      .locals init (class Parsek.MapRender.TracedPhase/'<Emit>d__5' V_0)
      IL_0000:  ldarg.0
      IL_0001:  ldfld      int32 Parsek.MapRender.TracedPhase/'<Emit>d__5'::'<>1__state'
      IL_0006:  ldc.i4.s   -2
      IL_0008:  bne.un.s   IL_0022

      IL_000a:  ldarg.0
      IL_000b:  ldfld      int32 Parsek.MapRender.TracedPhase/'<Emit>d__5'::'<>l__initialThreadId'
      IL_0010:  call       int32 [mscorlib]System.Environment::get_CurrentManagedThreadId()
      IL_0015:  bne.un.s   IL_0022

      IL_0017:  ldarg.0
      IL_0018:  ldc.i4.0
      IL_0019:  stfld      int32 Parsek.MapRender.TracedPhase/'<Emit>d__5'::'<>1__state'
      IL_001e:  ldarg.0
      IL_001f:  stloc.0
      IL_0020:  br.s       IL_0035

      IL_0022:  ldc.i4.0
      IL_0023:  newobj     instance void Parsek.MapRender.TracedPhase/'<Emit>d__5'::.ctor(int32)
      IL_0028:  stloc.0
      IL_0029:  ldloc.0
      IL_002a:  ldarg.0
      IL_002b:  ldfld      class Parsek.MapRender.TracedPhase Parsek.MapRender.TracedPhase/'<Emit>d__5'::'<>4__this'
      IL_0030:  stfld      class Parsek.MapRender.TracedPhase Parsek.MapRender.TracedPhase/'<Emit>d__5'::'<>4__this'
      IL_0035:  ldloc.0
      IL_0036:  ldarg.0
      IL_0037:  ldfld      valuetype Parsek.MapRender.SampleContext Parsek.MapRender.TracedPhase/'<Emit>d__5'::'<>3__ctx'
      IL_003c:  stfld      valuetype Parsek.MapRender.SampleContext Parsek.MapRender.TracedPhase/'<Emit>d__5'::ctx
      IL_0041:  ldloc.0
      IL_0042:  ret
    } // end of method '<Emit>d__5'::'System.Collections.Generic.IEnumerable<Parsek.MapRender.RenderSegment>.GetEnumerator'

    .method private hidebysig newslot virtual final 
            instance [mscorlib]System.Collections.IEnumerator 
            System.Collections.IEnumerable.GetEnumerator() cil managed
    {
      .custom instance void [mscorlib]System.Diagnostics.DebuggerHiddenAttribute::.ctor() = ( 01 00 00 00 ) 
      .override [mscorlib]System.Collections.IEnumerable::GetEnumerator
      // Code size       7 (0x7)
      .maxstack  8
      IL_0000:  ldarg.0
      IL_0001:  call       instance [mscorlib]System.Collections.Generic.IEnumerator`1<valuetype Parsek.MapRender.RenderSegment> Parsek.MapRender.TracedPhase/'<Emit>d__5'::'System.Collections.Generic.IEnumerable<Parsek.MapRender.RenderSegment>.GetEnumerator'()
      IL_0006:  ret
    } // end of method '<Emit>d__5'::System.Collections.IEnumerable.GetEnumerator

    .property instance valuetype Parsek.MapRender.RenderSegment
            'System.Collections.Generic.IEnumerator<Parsek.MapRender.RenderSegment>.Current'()
    {
      .get instance valuetype Parsek.MapRender.RenderSegment Parsek.MapRender.TracedPhase/'<Emit>d__5'::'System.Collections.Generic.IEnumerator<Parsek.MapRender.RenderSegment>.get_Current'()
    } // end of property '<Emit>d__5'::'System.Collections.Generic.IEnumerator<Parsek.MapRender.RenderSegment>.Current'
    .property instance object System.Collections.IEnumerator.Current()
    {
      .get instance object Parsek.MapRender.TracedPhase/'<Emit>d__5'::System.Collections.IEnumerator.get_Current()
    } // end of property '<Emit>d__5'::System.Collections.IEnumerator.Current
  } // end of class '<Emit>d__5'

  .field private initonly string '<GeometryBodyName>k__BackingField'
  .custom instance void [mscorlib]System.Runtime.CompilerServices.CompilerGeneratedAttribute::.ctor() = ( 01 00 00 00 ) 
  .custom instance void [mscorlib]System.Diagnostics.DebuggerBrowsableAttribute::.ctor([mscorlib]System.Diagnostics.DebuggerBrowsableState) = ( 01 00 00 00 00 00 00 00 ) 
  .method famandassem hidebysig specialname 
          instance string  get_GeometryBodyName() cil managed
  {
    .custom instance void [mscorlib]System.Runtime.CompilerServices.CompilerGeneratedAttribute::.ctor() = ( 01 00 00 00 ) 
    // Code size       7 (0x7)
    .maxstack  8
    IL_0000:  ldarg.0
    IL_0001:  ldfld      string Parsek.MapRender.TracedPhase::'<GeometryBodyName>k__BackingField'
    IL_0006:  ret
  } // end of method TracedPhase::get_GeometryBodyName

  .method famandassem hidebysig specialname rtspecialname 
          instance void  .ctor(valuetype Parsek.MapRender.PhaseId id,
                               valuetype Parsek.MapRender.PhaseKind kind,
                               valuetype Parsek.MapRender.SegmentProvenance provenance,
                               class Parsek.MapRender.AnchorFrame anchor,
                               float64 startUt,
                               float64 endUt,
                               class Parsek.MapRender.PhaseSeam leadingSeam,
                               class Parsek.MapRender.PhaseSeam trailingSeam,
                               string geometryBodyName) cil managed
  {
    // Code size       30 (0x1e)
    .maxstack  9
    IL_0000:  ldarg.0
    IL_0001:  ldarg.1
    IL_0002:  ldarg.2
    IL_0003:  ldarg.3
    IL_0004:  ldarg.s    anchor
    IL_0006:  ldarg.s    startUt
    IL_0008:  ldarg.s    endUt
    IL_000a:  ldarg.s    leadingSeam
    IL_000c:  ldarg.s    trailingSeam
    IL_000e:  call       instance void Parsek.MapRender.TrajectoryPhase::.ctor(valuetype Parsek.MapRender.PhaseId,
                                                                               valuetype Parsek.MapRender.PhaseKind,
                                                                               valuetype Parsek.MapRender.SegmentProvenance,
                                                                               class Parsek.MapRender.AnchorFrame,
                                                                               float64,
                                                                               float64,
                                                                               class Parsek.MapRender.PhaseSeam,
                                                                               class Parsek.MapRender.PhaseSeam)
    IL_0013:  nop
    IL_0014:  nop
    IL_0015:  ldarg.0
    IL_0016:  ldarg.s    geometryBodyName
    IL_0018:  stfld      string Parsek.MapRender.TracedPhase::'<GeometryBodyName>k__BackingField'
    IL_001d:  ret
  } // end of method TracedPhase::.ctor

  .method assembly hidebysig strict virtual 
          instance valuetype Parsek.MapRender.Treatment 
          ResolveTreatment() cil managed
  {
    // Code size       2 (0x2)
    .maxstack  8
    IL_0000:  ldc.i4.2
    IL_0001:  ret
  } // end of method TracedPhase::ResolveTreatment

  .method assembly hidebysig strict virtual 
          instance [mscorlib]System.Collections.Generic.IEnumerable`1<valuetype Parsek.MapRender.RenderSegment> 
          Emit(valuetype Parsek.MapRender.SampleContext ctx) cil managed
  {
    .custom instance void [mscorlib]System.Runtime.CompilerServices.IteratorStateMachineAttribute::.ctor([mscorlib]System.Type) = ( 01 00 27 50 61 72 73 65 6B 2E 4D 61 70 52 65 6E   // ..'Parsek.MapRen
                                                                                                                                    64 65 72 2E 54 72 61 63 65 64 50 68 61 73 65 2B   // der.TracedPhase+
                                                                                                                                    3C 45 6D 69 74 3E 64 5F 5F 35 00 00 )             // <Emit>d__5..
    // Code size       22 (0x16)
    .maxstack  8
    IL_0000:  ldc.i4.s   -2
    IL_0002:  newobj     instance void Parsek.MapRender.TracedPhase/'<Emit>d__5'::.ctor(int32)
    IL_0007:  dup
    IL_0008:  ldarg.0
    IL_0009:  stfld      class Parsek.MapRender.TracedPhase Parsek.MapRender.TracedPhase/'<Emit>d__5'::'<>4__this'
    IL_000e:  dup
    IL_000f:  ldarg.1
    IL_0010:  stfld      valuetype Parsek.MapRender.SampleContext Parsek.MapRender.TracedPhase/'<Emit>d__5'::'<>3__ctx'
    IL_0015:  ret
  } // end of method TracedPhase::Emit

  .method famandassem hidebysig newslot specialname abstract strict virtual 
          instance valuetype Parsek.MapRender.SegmentKind 
          get_LegacyKind() cil managed
  {
  } // end of method TracedPhase::get_LegacyKind

  .method famandassem hidebysig instance string 
          ResolveFrameBodyName(valuetype Parsek.MapRender.SampleContext ctx) cil managed
  {
    // Code size       85 (0x55)
    .maxstack  2
    .locals init (class Parsek.MapRender.AnchorFrame/BodyAnchor V_0,
             bool V_1,
             string V_2,
             bool V_3)
    IL_0000:  nop
    IL_0001:  ldarg.0
    IL_0002:  call       instance string Parsek.MapRender.TracedPhase::get_GeometryBodyName()
    IL_0007:  call       bool [mscorlib]System.String::IsNullOrEmpty(string)
    IL_000c:  ldc.i4.0
    IL_000d:  ceq
    IL_000f:  stloc.1
    IL_0010:  ldloc.1
    IL_0011:  brfalse.s  IL_001c

    IL_0013:  ldarg.0
    IL_0014:  call       instance string Parsek.MapRender.TracedPhase::get_GeometryBodyName()
    IL_0019:  stloc.2
    IL_001a:  br.s       IL_0053

    IL_001c:  ldarg.0
    IL_001d:  call       instance class Parsek.MapRender.AnchorFrame Parsek.MapRender.TrajectoryPhase::get_Anchor()
    IL_0022:  isinst     Parsek.MapRender.AnchorFrame/BodyAnchor
    IL_0027:  stloc.0
    IL_0028:  ldloc.0
    IL_0029:  brfalse.s  IL_003b

    IL_002b:  ldloc.0
    IL_002c:  callvirt   instance string Parsek.MapRender.AnchorFrame/BodyAnchor::get_BodyName()
    IL_0031:  call       bool [mscorlib]System.String::IsNullOrEmpty(string)
    IL_0036:  ldc.i4.0
    IL_0037:  ceq
    IL_0039:  br.s       IL_003c

    IL_003b:  ldc.i4.0
    IL_003c:  stloc.3
    IL_003d:  ldloc.3
    IL_003e:  brfalse.s  IL_0049

    IL_0040:  ldloc.0
    IL_0041:  callvirt   instance string Parsek.MapRender.AnchorFrame/BodyAnchor::get_BodyName()
    IL_0046:  stloc.2
    IL_0047:  br.s       IL_0053

    IL_0049:  ldarga.s   ctx
    IL_004b:  call       instance string Parsek.MapRender.SampleContext::get_FrameBodyName()
    IL_0050:  stloc.2
    IL_0051:  br.s       IL_0053

    IL_0053:  ldloc.2
    IL_0054:  ret
  } // end of method TracedPhase::ResolveFrameBodyName

  .property instance string GeometryBodyName()
  {
    .get instance string Parsek.MapRender.TracedPhase::get_GeometryBodyName()
  } // end of property TracedPhase::GeometryBodyName
  .property instance valuetype Parsek.MapRender.SegmentKind
          LegacyKind()
  {
    .get instance valuetype Parsek.MapRender.SegmentKind Parsek.MapRender.TracedPhase::get_LegacyKind()
  } // end of property TracedPhase::LegacyKind
} // end of class Parsek.MapRender.TracedPhase

.class private abstract auto ansi sealed beforefieldinit Parsek.DisplayNames
       extends [mscorlib]System.Object
{
  .field private static int32 '<Version>k__BackingField'
  .custom instance void [mscorlib]System.Runtime.CompilerServices.CompilerGeneratedAttribute::.ctor() = ( 01 00 00 00 ) 
  .custom instance void [mscorlib]System.Diagnostics.DebuggerBrowsableAttribute::.ctor([mscorlib]System.Diagnostics.DebuggerBrowsableState) = ( 01 00 00 00 00 00 00 00 ) 
  .field private static [mscorlib]System.Action DisplayNamesChanged
  .custom instance void [mscorlib]System.Runtime.CompilerServices.CompilerGeneratedAttribute::.ctor() = ( 01 00 00 00 ) 
  .custom instance void [mscorlib]System.Diagnostics.DebuggerBrowsableAttribute::.ctor([mscorlib]System.Diagnostics.DebuggerBrowsableState) = ( 01 00 00 00 00 00 00 00 ) 
  .method assembly hidebysig specialname static 
          int32  get_Version() cil managed
  {
    .custom instance void [mscorlib]System.Runtime.CompilerServices.CompilerGeneratedAttribute::.ctor() = ( 01 00 00 00 ) 
    // Code size       6 (0x6)
    .maxstack  8
    IL_0000:  ldsfld     int32 Parsek.DisplayNames::'<Version>k__BackingField'
    IL_0005:  ret
  } // end of method DisplayNames::get_Version

  .method private hidebysig specialname static 
          void  set_Version(int32 'value') cil managed
  {
    .custom instance void [mscorlib]System.Runtime.CompilerServices.CompilerGeneratedAttribute::.ctor() = ( 01 00 00 00 ) 
    // Code size       7 (0x7)
    .maxstack  8
    IL_0000:  ldarg.0
    IL_0001:  stsfld     int32 Parsek.DisplayNames::'<Version>k__BackingField'
    IL_0006:  ret
  } // end of method DisplayNames::set_Version

  .method assembly hidebysig specialname static 
          void  add_DisplayNamesChanged([mscorlib]System.Action 'value') cil managed
  {
    .custom instance void [mscorlib]System.Runtime.CompilerServices.CompilerGeneratedAttribute::.ctor() = ( 01 00 00 00 ) 
    // Code size       39 (0x27)
    .maxstack  3
    .locals init ([mscorlib]System.Action V_0,
             [mscorlib]System.Action V_1,
             [mscorlib]System.Action V_2)
    IL_0000:  ldsfld     [mscorlib]System.Action Parsek.DisplayNames::DisplayNamesChanged
    IL_0005:  stloc.0
    IL_0006:  ldloc.0
    IL_0007:  stloc.1
    IL_0008:  ldloc.1
    IL_0009:  ldarg.0
    IL_000a:  call       [mscorlib]System.Delegate [mscorlib]System.Delegate::Combine([mscorlib]System.Delegate,
                                                                                      [mscorlib]System.Delegate)
    IL_000f:  castclass  [mscorlib]System.Action
    IL_0014:  stloc.2
    IL_0015:  ldsflda    [mscorlib]System.Action Parsek.DisplayNames::DisplayNamesChanged
    IL_001a:  ldloc.2
    IL_001b:  ldloc.1
    IL_001c:  call       !!0 [mscorlib]System.Threading.Interlocked::CompareExchange<[mscorlib]System.Action>(!!0&,
                                                                                                              !!0,
                                                                                                              !!0)
    IL_0021:  stloc.0
    IL_0022:  ldloc.0
    IL_0023:  ldloc.1
    IL_0024:  bne.un.s   IL_0006

    IL_0026:  ret
  } // end of method DisplayNames::add_DisplayNamesChanged

  .method assembly hidebysig specialname static 
          void  remove_DisplayNamesChanged([mscorlib]System.Action 'value') cil managed
  {
    .custom instance void [mscorlib]System.Runtime.CompilerServices.CompilerGeneratedAttribute::.ctor() = ( 01 00 00 00 ) 
    // Code size       39 (0x27)
    .maxstack  3
    .locals init ([mscorlib]System.Action V_0,
             [mscorlib]System.Action V_1,
             [mscorlib]System.Action V_2)
    IL_0000:  ldsfld     [mscorlib]System.Action Parsek.DisplayNames::DisplayNamesChanged
    IL_0005:  stloc.0
    IL_0006:  ldloc.0
    IL_0007:  stloc.1
    IL_0008:  ldloc.1
    IL_0009:  ldarg.0
    IL_000a:  call       [mscorlib]System.Delegate [mscorlib]System.Delegate::Remove([mscorlib]System.Delegate,
                                                                                     [mscorlib]System.Delegate)
    IL_000f:  castclass  [mscorlib]System.Action
    IL_0014:  stloc.2
    IL_0015:  ldsflda    [mscorlib]System.Action Parsek.DisplayNames::DisplayNamesChanged
    IL_001a:  ldloc.2
    IL_001b:  ldloc.1
    IL_001c:  call       !!0 [mscorlib]System.Threading.Interlocked::CompareExchange<[mscorlib]System.Action>(!!0&,
                                                                                                              !!0,
                                                                                                              !!0)
    IL_0021:  stloc.0
    IL_0022:  ldloc.0
    IL_0023:  ldloc.1
    IL_0024:  bne.un.s   IL_0006

    IL_0026:  ret
  } // end of method DisplayNames::remove_DisplayNamesChanged

  .method assembly hidebysig static void 
          NoteChanged(string reason,
                      string detail) cil managed
  {
    // Code size       228 (0xe4)
    .maxstack  6
    .locals init ([mscorlib]System.Action V_0,
             int32 V_1,
             bool V_2,
             [mscorlib]System.Delegate[] V_3,
             int32 V_4,
             [mscorlib]System.Delegate V_5,
             [mscorlib]System.Exception V_6)
    IL_0000:  nop
    IL_0001:  nop
    IL_0002:  call       int32 Parsek.DisplayNames::get_Version()
    IL_0007:  ldc.i4.1
    IL_0008:  add
    IL_0009:  call       void Parsek.DisplayNames::set_Version(int32)
    IL_000e:  nop
    IL_000f:  nop
    IL_0010:  ldstr      "VesselNames"
    IL_0015:  ldc.i4.6
    IL_0016:  newarr     [mscorlib]System.String
    IL_001b:  dup
    IL_001c:  ldc.i4.0
    IL_001d:  ldstr      "Display names invalidated: reason="
    IL_0022:  stelem.ref
    IL_0023:  dup
    IL_0024:  ldc.i4.1
    IL_0025:  ldarg.0
    IL_0026:  dup
    IL_0027:  brtrue.s   IL_002f

    IL_0029:  pop
    IL_002a:  ldstr      "<none>"
    IL_002f:  stelem.ref
    IL_0030:  dup
    IL_0031:  ldc.i4.2
    IL_0032:  ldstr      " detail="
    IL_0037:  stelem.ref
    IL_0038:  dup
    IL_0039:  ldc.i4.3
    IL_003a:  ldarg.1
    IL_003b:  dup
    IL_003c:  brtrue.s   IL_0044

    IL_003e:  pop
    IL_003f:  ldstr      "<null>"
    IL_0044:  stelem.ref
    IL_0045:  dup
    IL_0046:  ldc.i4.4
    IL_0047:  ldstr      " version="
    IL_004c:  stelem.ref
    IL_004d:  dup
    IL_004e:  ldc.i4.5
    IL_004f:  call       int32 Parsek.DisplayNames::get_Version()
    IL_0054:  stloc.1
    IL_0055:  ldloca.s   V_1
    IL_0057:  call       [mscorlib]System.Globalization.CultureInfo [mscorlib]System.Globalization.CultureInfo::get_InvariantCulture()
    IL_005c:  call       instance string [mscorlib]System.Int32::ToString([mscorlib]System.IFormatProvider)
    IL_0061:  stelem.ref
    IL_0062:  call       string [mscorlib]System.String::Concat(string[])
    IL_0067:  call       void Parsek.ParsekLog::Verbose(string,
                                                        string)
    IL_006c:  nop
    IL_006d:  ldsfld     [mscorlib]System.Action Parsek.DisplayNames::DisplayNamesChanged
    IL_0072:  stloc.0
    IL_0073:  ldloc.0
    IL_0074:  ldnull
    IL_0075:  ceq
    IL_0077:  stloc.2
    IL_0078:  ldloc.2
    IL_0079:  brfalse.s  IL_007d

    IL_007b:  br.s       IL_00e3

    IL_007d:  nop
    IL_007e:  ldloc.0
    IL_007f:  callvirt   instance [mscorlib]System.Delegate[] [mscorlib]System.Delegate::GetInvocationList()
    IL_0084:  stloc.3
    IL_0085:  ldc.i4.0
    IL_0086:  stloc.s    V_4
    IL_0088:  br.s       IL_00dc

    IL_008a:  ldloc.3
    IL_008b:  ldloc.s    V_4
    IL_008d:  ldelem.ref
    IL_008e:  stloc.s    V_5
    IL_0090:  nop
    .try
    {
      IL_0091:  nop
      IL_0092:  ldloc.s    V_5
      IL_0094:  castclass  [mscorlib]System.Action
      IL_0099:  callvirt   instance void [mscorlib]System.Action::Invoke()
      IL_009e:  nop
      IL_009f:  nop
      IL_00a0:  leave.s    IL_00d5

    }  // end .try
    catch [mscorlib]System.Exception 
    {
      IL_00a2:  stloc.s    V_6
      IL_00a4:  nop
      IL_00a5:  ldstr      "VesselNames"
      IL_00aa:  ldstr      "DisplayNamesChanged subscriber threw: "
      IL_00af:  ldloc.s    V_6
      IL_00b1:  callvirt   instance [mscorlib]System.Type [mscorlib]System.Exception::GetType()
      IL_00b6:  callvirt   instance string [mscorlib]System.Reflection.MemberInfo::get_Name()
      IL_00bb:  ldstr      ": "
      IL_00c0:  ldloc.s    V_6
      IL_00c2:  callvirt   instance string [mscorlib]System.Exception::get_Message()
      IL_00c7:  call       string [mscorlib]System.String::Concat(string,
                                                                  string,
                                                                  string,
                                                                  string)
      IL_00cc:  call       void Parsek.ParsekLog::Warn(string,
                                                       string)
      IL_00d1:  nop
      IL_00d2:  nop
      IL_00d3:  leave.s    IL_00d5

    }  // end handler
    IL_00d5:  nop
    IL_00d6:  ldloc.s    V_4
    IL_00d8:  ldc.i4.1
    IL_00d9:  add
    IL_00da:  stloc.s    V_4
    IL_00dc:  ldloc.s    V_4
    IL_00de:  ldloc.3
    IL_00df:  ldlen
    IL_00e0:  conv.i4
    IL_00e1:  blt.s      IL_008a

    IL_00e3:  ret
  } // end of method DisplayNames::NoteChanged

  .method assembly hidebysig static void 
          ResetSubscribersForTesting() cil managed
  {
    // Code size       8 (0x8)
    .maxstack  8
    IL_0000:  nop
    IL_0001:  ldnull
    IL_0002:  stsfld     [mscorlib]System.Action Parsek.DisplayNames::DisplayNamesChanged
    IL_0007:  ret
  } // end of method DisplayNames::ResetSubscribersForTesting

  .event [mscorlib]System.Action DisplayNamesChanged
  {
    .addon void Parsek.DisplayNames::add_DisplayNamesChanged([mscorlib]System.Action)
    .removeon void Parsek.DisplayNames::remove_DisplayNamesChanged([mscorlib]System.Action)
  } // end of event DisplayNames::DisplayNamesChanged
  .property int32 Version()
  {
    .get int32 Parsek.DisplayNames::get_Version()
    .set void Parsek.DisplayNames::set_Version(int32)
  } // end of property DisplayNames::Version
} // end of class Parsek.DisplayNames

.class private auto ansi sealed beforefieldinit Parsek.TimelineRowHoverTracker
       extends [mscorlib]System.Object
{
  .class sequential ansi sealed nested private beforefieldinit Memo
         extends [mscorlib]System.ValueType
  {
    .field assembly bool BuiltFuture
    .field assembly string Text
  } // end of class Memo
} // end of class Parsek.TimelineRowHoverTracker

.class private abstract auto ansi sealed beforefieldinit Parsek.Patches.ClaimedVesselLaunchSite
       extends [mscorlib]System.Object
{
  .class auto ansi sealed nested private beforefieldinit Memo
         extends [mscorlib]System.Object
  {
    .field assembly [mscorlib]System.Collections.Generic.List`1<valuetype Parsek.ClaimedVesselDisposalGate/HeldVessel> Held
    .field assembly string StockProceedText
    .method public hidebysig specialname rtspecialname 
            instance void  .ctor() cil managed
    {
      // Code size       8 (0x8)
      .maxstack  8
      IL_0000:  ldarg.0
      IL_0001:  call       instance void [mscorlib]System.Object::.ctor()
      IL_0006:  nop
      IL_0007:  ret
    } // end of method Memo::.ctor

  } // end of class Memo
} // end of class Parsek.Patches.ClaimedVesselLaunchSite
