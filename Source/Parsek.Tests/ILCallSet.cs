using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Parsek.Tests
{
    /// <summary>
    /// Reads the CALL SET of a compiled method straight out of its IL, so a gate can assert
    /// what a production method actually calls instead of what a comment or a hand-written
    /// literal array says it calls.
    ///
    /// <para>This is the AST-walk equivalent for a compiled assembly and the reason to prefer
    /// it over a source scan: comments and log strings are not in the IL, so a needle with a
    /// comment twin cannot fail GREEN, and a DELETED call site is visible (a name-existence
    /// reflection check is not - the method still exists, it is just no longer called).</para>
    ///
    /// <para>Decoding is Harmony's <c>PatchProcessor.ReadMethodBody</c>, which resolves each
    /// call token to the <see cref="MethodBase"/> it targets. 0Harmony is already a reference
    /// of this test project.</para>
    /// </summary>
    internal static class ILCallSet
    {
        private const BindingFlags AnyDeclared =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        /// <summary>
        /// The declared method <paramref name="name"/> on <paramref name="type"/>. Throws with
        /// a named message when it is missing, so a rename reads as a gate failure naming the
        /// symbol rather than a null-reference somewhere downstream.
        /// </summary>
        internal static MethodBase Method(Type type, string name)
        {
            var m = type.GetMethod(name, AnyDeclared);
            if (m == null)
                throw new InvalidOperationException(
                    $"IL gate: {type.FullName} declares no method named '{name}' " +
                    "(renamed or removed? the gate must be re-pointed at the new name)");
            return m;
        }

        /// <summary>
        /// Every method called by <paramref name="method"/> (call / callvirt / newobj), in IL
        /// order, duplicates included.
        /// </summary>
        internal static List<MethodBase> CalledMethods(MethodBase method)
        {
            var called = new List<MethodBase>();
            foreach (var instruction in HarmonyLib.PatchProcessor.ReadMethodBody(method))
            {
                OpCode op = instruction.Key;
                if (op != OpCodes.Call && op != OpCodes.Callvirt && op != OpCodes.Newobj)
                    continue;
                var target = instruction.Value as MethodBase;
                if (target != null)
                    called.Add(target);
            }
            return called;
        }

        /// <summary>
        /// The set of called method NAMES, optionally restricted to calls whose name matches
        /// <paramref name="namePredicate"/>.
        /// </summary>
        internal static HashSet<string> CalledMethodNames(
            MethodBase method, Func<string, bool> namePredicate = null)
        {
            var names = CalledMethods(method).Select(m => m.Name);
            if (namePredicate != null) names = names.Where(namePredicate);
            return new HashSet<string>(names, StringComparer.Ordinal);
        }

        /// <summary>
        /// True when <paramref name="method"/> calls <paramref name="name"/> declared on
        /// <paramref name="declaringType"/> (the type check keeps a same-named member of an
        /// unrelated type from satisfying the gate).
        /// </summary>
        internal static bool Calls(MethodBase method, Type declaringType, string name)
        {
            return CalledMethods(method).Any(m =>
                string.Equals(m.Name, name, StringComparison.Ordinal) &&
                m.DeclaringType != null &&
                declaringType.IsAssignableFrom(m.DeclaringType));
        }

        /// <summary>
        /// How many times <paramref name="method"/> calls <paramref name="name"/> declared on
        /// <paramref name="declaringType"/>. A gate that pins TWO mirrored call sites needs
        /// the count, not the boolean: one of the two can be reverted to an inline write with
        /// the boolean still satisfied by its twin.
        /// </summary>
        internal static int CallCount(MethodBase method, Type declaringType, string name)
        {
            return CalledMethods(method).Count(m =>
                string.Equals(m.Name, name, StringComparison.Ordinal) &&
                m.DeclaringType != null &&
                declaringType.IsAssignableFrom(m.DeclaringType));
        }

        /// <summary>
        /// Every FIELD <paramref name="method"/> READS (ldfld / ldflda / ldsfld / ldsflda),
        /// in IL order, duplicates included.
        ///
        /// <para>A call-set-only gate is blind to a public field: swapping a
        /// <c>v.transform.position</c> call for a <c>v.CoM</c> field read removes a call and
        /// adds no call, so a "calls X and does not call Y" gate stays GREEN through exactly
        /// the substitution it exists to forbid. Reading the field set closes that hole.</para>
        /// </summary>
        internal static List<FieldInfo> ReadFields(MethodBase method)
        {
            return FieldOperands(
                method, OpCodes.Ldfld, OpCodes.Ldflda, OpCodes.Ldsfld, OpCodes.Ldsflda);
        }

        /// <summary>
        /// Every FIELD <paramref name="method"/> WRITES (stfld / stsfld), in IL order,
        /// duplicates included.
        /// </summary>
        internal static List<FieldInfo> WrittenFields(MethodBase method)
        {
            return FieldOperands(method, OpCodes.Stfld, OpCodes.Stsfld);
        }

        /// <summary>
        /// True when <paramref name="method"/> reads the field <paramref name="name"/>
        /// declared on <paramref name="declaringType"/> (or on a base of it).
        /// </summary>
        internal static bool ReadsField(MethodBase method, Type declaringType, string name)
        {
            return MatchesField(ReadFields(method), declaringType, name);
        }

        /// <summary>
        /// True when <paramref name="method"/> writes the field <paramref name="name"/>
        /// declared on <paramref name="declaringType"/> (or on a base of it).
        /// </summary>
        internal static bool WritesField(MethodBase method, Type declaringType, string name)
        {
            return MatchesField(WrittenFields(method), declaringType, name);
        }

        private static bool MatchesField(
            IEnumerable<FieldInfo> fields, Type declaringType, string name)
        {
            return fields.Any(f =>
                string.Equals(f.Name, name, StringComparison.Ordinal) &&
                f.DeclaringType != null &&
                declaringType.IsAssignableFrom(f.DeclaringType));
        }

        private static List<FieldInfo> FieldOperands(MethodBase method, params OpCode[] opcodes)
        {
            var wanted = new HashSet<OpCode>(opcodes);
            var fields = new List<FieldInfo>();
            foreach (var instruction in HarmonyLib.PatchProcessor.ReadMethodBody(method))
            {
                if (!wanted.Contains(instruction.Key))
                    continue;
                var field = instruction.Value as FieldInfo;
                if (field != null)
                    fields.Add(field);
            }
            return fields;
        }
    }
}
