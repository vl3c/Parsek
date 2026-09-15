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
    }
}
