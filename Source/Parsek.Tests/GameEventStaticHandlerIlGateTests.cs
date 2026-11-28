using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Assembly-wide gate: no method in Parsek.dll builds a KSP GameEvents delegate
    /// (<c>EventData*.OnEvent</c>, <c>EventVoid.OnEvent</c> and the other stock event types'
    /// nested <c>OnEvent</c>) over a STATIC method.
    ///
    /// KSP's <c>EventData.Add</c> wraps the handler in an <c>EvtDelegate</c> whose constructor
    /// runs <c>evt.Target.GetType().Name</c> with no null check (decompiled, 1.12.5), so a
    /// static method group (null <c>Target</c>) throws <c>NullReferenceException</c> inside
    /// <c>Add</c>. The trap has shipped four times: a static <c>ParsekScenario</c> OnLoad
    /// handler (recording index wipe), <c>FlightCameraReloadPin.Arm</c>, the
    /// <c>RevertDetector</c> handlers, and the part parity burst driver's trigger listener
    /// (every X4 burst case read <c>trigger: NullReferenceException</c> on its first live
    /// run, 2026-11-28). The per-file source scans
    /// (<see cref="ScenarioGameEventHandlerContractTests"/>, <see cref="FlightCameraReloadPinTests"/>)
    /// cover two files; this one reads the compiled IL of every method, so it sees every
    /// subscription however the source spells it.
    ///
    /// The compiled shape of a static method group conversion is
    /// <c>ldnull; ldftn M; newobj OnEvent::.ctor</c> (a C# 11 cached conversion keeps the same
    /// three instructions behind its cache check). An instance handler loads its receiver
    /// instead of <c>ldnull</c>, and a lambda's target is the compiler's closure object, so
    /// neither matches.
    /// </summary>
    public class GameEventStaticHandlerIlGateTests
    {
        private static readonly Dictionary<short, OpCode> OpCodesByValue = BuildOpCodeTable();

        private static Dictionary<short, OpCode> BuildOpCodeTable()
        {
            var table = new Dictionary<short, OpCode>();
            foreach (FieldInfo f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.GetValue(null) is OpCode op) table[op.Value] = op;
            return table;
        }

        private struct Instr
        {
            internal OpCode Op;
            internal int Token;
        }

        private static List<Instr> Decode(byte[] il)
        {
            var list = new List<Instr>();
            int i = 0;
            while (i < il.Length)
            {
                short value = il[i];
                i++;
                if (value == 0xFE && i < il.Length)
                {
                    value = unchecked((short)(0xFE00 | il[i]));
                    i++;
                }
                if (!OpCodesByValue.TryGetValue(value, out OpCode op))
                    throw new InvalidOperationException("unknown IL opcode 0x" + value.ToString("X"));
                int token = 0;
                switch (op.OperandType)
                {
                    case OperandType.InlineNone: break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar: i += 1; break;
                    case OperandType.InlineVar: i += 2; break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR: i += 8; break;
                    case OperandType.InlineSwitch:
                        int n = BitConverter.ToInt32(il, i);
                        i += 4 + 4 * n;
                        break;
                    case OperandType.InlineMethod:
                    case OperandType.InlineField:
                    case OperandType.InlineType:
                    case OperandType.InlineTok:
                    case OperandType.InlineSig:
                    case OperandType.InlineString:
                        token = BitConverter.ToInt32(il, i);
                        i += 4;
                        break;
                    default: i += 4; break;
                }
                list.Add(new Instr { Op = op, Token = token });
            }
            return list;
        }

        /// <summary>A stock GameEvents delegate: a nested <c>OnEvent</c> of an Assembly-CSharp
        /// type whose name starts <c>Event</c> (<c>EventData`1</c>, <c>EventVoid</c>, ...).</summary>
        internal static bool IsStockGameEventDelegate(Type delegateType)
        {
            if (delegateType == null || delegateType.Name != "OnEvent") return false;
            Type owner = delegateType.DeclaringType;
            if (owner == null || !owner.Name.StartsWith("Event", StringComparison.Ordinal)) return false;
            return owner.Assembly == typeof(GameEvents).Assembly;
        }

        /// <summary>Every <c>ldnull; ldftn static; newobj OnEvent</c> in the method's body.</summary>
        private static IEnumerable<string> StaticGameEventDelegates(MethodBase method)
        {
            MethodBody body;
            try { body = method.GetMethodBody(); }
            catch (Exception) { yield break; }
            byte[] il = body?.GetILAsByteArray();
            if (il == null || il.Length == 0) yield break;
            List<Instr> code = Decode(il);
            Module module = method.Module;
            Type[] typeArgs = method.DeclaringType != null && method.DeclaringType.IsGenericType
                ? method.DeclaringType.GetGenericArguments() : null;
            Type[] methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
            for (int k = 0; k + 2 < code.Count; k++)
            {
                if (code[k].Op != OpCodes.Ldnull || code[k + 1].Op != OpCodes.Ldftn || code[k + 2].Op != OpCodes.Newobj) continue;
                MethodBase target, ctor;
                try
                {
                    target = module.ResolveMethod(code[k + 1].Token, typeArgs, methodArgs);
                    ctor = module.ResolveMethod(code[k + 2].Token, typeArgs, methodArgs);
                }
                catch (Exception) { continue; }
                if (target == null || !target.IsStatic || !IsStockGameEventDelegate(ctor?.DeclaringType)) continue;
                yield return method.DeclaringType?.FullName + "::" + method.Name + " -> static "
                    + target.DeclaringType?.FullName + "::" + target.Name;
            }
        }

        internal static List<string> FindStaticGameEventHandlers(Assembly assembly)
        {
            var offenders = new List<string>();
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (Type t in types)
            {
                if (t == null) continue;
                var methods = new List<MethodBase>();
                try
                {
                    methods.AddRange(t.GetMethods(All));
                    methods.AddRange(t.GetConstructors(All));
                }
                catch (Exception) { continue; }
                foreach (MethodBase m in methods)
                    offenders.AddRange(StaticGameEventDelegates(m));
            }
            return offenders;
        }

        [Fact]
        public void NoParsekMethod_SubscribesAStaticMethodToAKspGameEvent()
        {
            List<string> offenders = FindStaticGameEventHandlers(typeof(Parsek.ParsekScenario).Assembly);
            Assert.True(offenders.Count == 0,
                "These methods build a KSP GameEvents delegate over a STATIC method. KSP's EventData.Add "
                + "dereferences evt.Target (null for a static method) in the EvtDelegate ctor and throws "
                + "NullReferenceException. Route the handler through an instance (a small listener object "
                + "or the owning MonoBehaviour): [" + string.Join("; ", offenders) + "]");
        }

        // ----- the gate's own pins -----

        private static void StaticFixtureHandler(Part p) { }

        private sealed class InstanceFixture
        {
            internal void Handler(Part p) { }
        }

        private static void SubscribesStatic()
        {
            EventData<Part>.OnEvent d = StaticFixtureHandler;
            GC.KeepAlive(d);
        }

        private static void SubscribesInstance()
        {
            var fixture = new InstanceFixture();
            EventData<Part>.OnEvent d = fixture.Handler;
            GC.KeepAlive(d);
        }

        private static void SubscribesLambda()
        {
            EventData<Part>.OnEvent d = p => StaticFixtureHandler(p);
            GC.KeepAlive(d);
        }

        private static List<string> ScanFixture(string name)
        {
            MethodInfo m = typeof(GameEventStaticHandlerIlGateTests).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);
            return new List<string>(StaticGameEventDelegates(m));
        }

        [Fact]
        public void TheScan_FindsAStaticMethodGroupConversion()
        {
            List<string> found = ScanFixture(nameof(SubscribesStatic));
            Assert.Single(found);
            Assert.Contains(nameof(StaticFixtureHandler), found[0]);
        }

        [Fact]
        public void TheScan_PassesAnInstanceHandlerAndALambda()
        {
            Assert.Empty(ScanFixture(nameof(SubscribesInstance)));
            Assert.Empty(ScanFixture(nameof(SubscribesLambda)));
        }

        [Fact]
        public void TheTrap_IsReal_StaticAddThrowsAndInstanceAddWorks()
        {
            var evt = new EventData<Part>("parsekIlGateTrapPin");
            Assert.Throws<NullReferenceException>(() => evt.Add(StaticFixtureHandler));
            var fixture = new InstanceFixture();
            EventData<Part>.OnEvent d = fixture.Handler;
            evt.Add(d);
            evt.Remove(d);
        }

        [Fact]
        public void TheDelegateClassifier_NamesStockEventTypesOnly()
        {
            Assert.True(IsStockGameEventDelegate(typeof(EventData<Part>.OnEvent)));
            Assert.True(IsStockGameEventDelegate(typeof(EventData<PartJoint, float>.OnEvent)));
            Assert.True(IsStockGameEventDelegate(typeof(EventVoid.OnEvent)));
            Assert.False(IsStockGameEventDelegate(typeof(Action<Part>)));
            Assert.False(IsStockGameEventDelegate(null));
        }
    }
}
