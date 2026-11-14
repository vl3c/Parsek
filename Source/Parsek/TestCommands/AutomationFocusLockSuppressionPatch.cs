using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KSP.UI.Screens;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Automation only: keeps stock from setting its focus-loss input lock while the
    /// command seam is armed (<c>PARSEK_TEST_COMMANDS=1</c>), so a harness KSP that runs
    /// unfocused (parallel copies, a second virtual desktop, the operator using the PC)
    /// behaves like a focused one.
    ///
    /// <para>Decompiled (KSP 1.12.5 Assembly-CSharp.dll), five private
    /// <c>OnApplicationFocus(bool focus)</c> bodies, each of the form
    /// <c>if (!focus) SetControlLock(id) else RemoveControlLock(id)</c> (EditorDriver
    /// spells the same thing with the branches swapped), every one a
    /// <c>ControlTypes.All</c> lock:</para>
    /// <list type="bullet">
    /// <item><c>FlightDriver</c>: <c>flightDriver_ApplicationFocus</c></item>
    /// <item><c>EditorDriver</c>: <c>EditorDriver_AppFocus</c></item>
    /// <item><c>SpaceCenterMain</c>: <c>ksc_ApplicationFocus</c></item>
    /// <item><c>KSP.UI.Screens.UISpaceCenter</c>: <c>ksc_ApplicationFocus</c></item>
    /// <item><c>MainMenu</c>: <c>applicationFocus</c></item>
    /// </list>
    /// <para>The lock makes the seam refuse <c>WarpToUT</c> (<c>warp-locked</c>), stalls the
    /// editor launch, and at KSC pauses the Planetarium through
    /// <c>SpaceCenterMain.onInputLockModified</c>. The prefix skips the original only for
    /// <c>focus == false</c>; <c>focus == true</c> runs it, so a lock is still removed as
    /// stock removes it.</para>
    ///
    /// <para>No lock can predate the patch: the patch is applied from
    /// <c>ParsekHarmony.Awake</c> (Startup.Instantly, the LOADING scene), and none of the
    /// five owners exists before the main menu loads, so there is no lock to clear at arm
    /// time.</para>
    ///
    /// <para>Inert in a player build: <see cref="Prepare"/> reads the same env var the seam
    /// addon reads and answers false unless it is the literal <c>1</c>, so Harmony applies
    /// zero targets. It reads the env itself rather than the addon's cached bool because
    /// the two Instantly addons wake in no guaranteed order.</para>
    /// </summary>
    [HarmonyPatch]
    internal static class AutomationFocusLockSuppressionPatch
    {
        private const string Tag = "TestCommands";
        private const string TargetMethodName = "OnApplicationFocus";
        private const double LogIntervalSeconds = 30.0;

        /// <summary>One stock owner of a focus-loss lock: its type and the lock id it sets.</summary>
        internal struct FocusLockTarget
        {
            internal readonly Type Owner;
            internal readonly string LockId;

            internal FocusLockTarget(Type owner, string lockId)
            {
                Owner = owner;
                LockId = lockId;
            }
        }

        /// <summary>The five stock owners and their lock ids, as decompiled.</summary>
        internal static FocusLockTarget[] TargetsForTesting()
        {
            return new[]
            {
                new FocusLockTarget(typeof(FlightDriver), "flightDriver_ApplicationFocus"),
                new FocusLockTarget(typeof(EditorDriver), "EditorDriver_AppFocus"),
                new FocusLockTarget(typeof(SpaceCenterMain), "ksc_ApplicationFocus"),
                new FocusLockTarget(typeof(UISpaceCenter), "ksc_ApplicationFocus"),
                new FocusLockTarget(typeof(MainMenu), "applicationFocus"),
            };
        }

        /// <summary>Pure: whether the stock body is skipped for this focus change.</summary>
        internal static bool ShouldSkipOriginal(bool armed, bool focus) => armed && !focus;

        /// <summary>Pure: the lock id the given owner type would set, or "?".</summary>
        internal static string LockIdForOwner(Type owner)
        {
            if (owner == null) return "?";
            foreach (var target in TargetsForTesting())
            {
                if (target.Owner == owner) return target.LockId;
            }
            return "?";
        }

        /// <summary>Pure: the suppression log line (scene passed in so the test needs no game).</summary>
        internal static string FormatSuppressionMessage(string scene, string ownerName, string lockId)
        {
            return "focus lock suppressed scene=" + (scene ?? "?")
                + " owner=" + (ownerName ?? "?")
                + " lockId=" + (lockId ?? "?")
                + " (automation: PARSEK_TEST_COMMANDS=1; stock would set a ControlTypes.All"
                + " input lock on focus loss)";
        }

        /// <summary>Pure: the rate-limit key, one per lock id.</summary>
        internal static string RateLimitKey(string lockId) => "focus-lock-suppressed|" + (lockId ?? "?");

        /// <summary>Every resolvable target method (private instance <c>OnApplicationFocus(bool)</c>).</summary>
        internal static List<MethodBase> ResolveTargetMethodsForTesting()
        {
            var methods = new List<MethodBase>();
            foreach (var target in TargetsForTesting())
            {
                MethodInfo method = AccessTools.Method(target.Owner, TargetMethodName, new[] { typeof(bool) });
                if (method != null && method.DeclaringType == target.Owner)
                    methods.Add(method);
            }
            return methods;
        }

        static bool Prepare()
        {
            return ParsekTestCommandAddon.IsArmed(
                Environment.GetEnvironmentVariable(ParsekTestCommandAddon.EnvVarName));
        }

        static IEnumerable<MethodBase> TargetMethods()
        {
            List<MethodBase> methods = ResolveTargetMethodsForTesting();
            int expected = TargetsForTesting().Length;
            if (methods.Count < expected)
            {
                ParsekLog.Warn(Tag, $"focus lock suppression resolved {methods.Count}/{expected} "
                    + "OnApplicationFocus(bool) targets - an unfocused automation KSP may still "
                    + "set a focus-loss input lock in the missing scene");
            }
            else
            {
                ParsekLog.Info(Tag, $"focus lock suppression armed targets={methods.Count} "
                    + "(automation: PARSEK_TEST_COMMANDS=1)");
            }
            return methods;
        }

        static bool Prefix(MethodBase __originalMethod, bool focus)
        {
            if (!ShouldSkipOriginal(true, focus))
                return true;
            Type owner = __originalMethod != null ? __originalMethod.DeclaringType : null;
            string lockId = LockIdForOwner(owner);
            ParsekLog.InfoRateLimited(Tag, RateLimitKey(lockId),
                FormatSuppressionMessage(HighLogic.LoadedScene.ToString(),
                    owner != null ? owner.Name : null, lockId),
                LogIntervalSeconds);
            return false;
        }
    }
}
