using System;
using System.Reflection;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// S7 save-time guard for <see cref="ReFlyRevertButtonGate"/>'s in-memory
    /// <c>Parameters.Flight.CanRestart = true</c> override (Hard preset, live re-fly only).
    ///
    /// Every serialization of a <c>GameParameters.FlightParams</c> goes through the
    /// non-virtual base <c>GameParameters.ParameterNode.Save(ConfigNode)</c> (decompiled
    /// KSP 1.12.5: <c>GameParameters.Save</c> calls <c>Flight.Save(node.AddNode("FLIGHT"))</c>;
    /// <c>Game.Save</c>, <c>GamePersistence.SaveGame</c> and <c>GameBackup</c> all reach it),
    /// which writes each public field as <c>name = value.ToString()</c>. This postfix
    /// rewrites the just-written <c>CanRestart</c> value back to False when the saved
    /// instance is the one Parsek forced, so the override can never be persisted. It
    /// touches nothing on any other instance or node, so it is inert on every save outside
    /// a Hard-preset re-fly.
    /// </summary>
    [HarmonyPatch(typeof(GameParameters.ParameterNode), nameof(GameParameters.ParameterNode.Save),
        new Type[] { typeof(ConfigNode) })]
    internal static class FlightParamsCanRestartPersistPatch
    {
        private const string HarmonyId = "com.parsek.mod";
        private static bool installedConfirmed;

        static void Postfix(GameParameters.ParameterNode __instance, ConfigNode node)
        {
            try
            {
                ReFlyRevertButtonGate.RewritePersistedCanRestart(__instance, node);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("ReFlySession",
                    $"FlightParams save guard failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// True once Harmony reports this postfix on the live <c>ParameterNode.Save</c>.
        /// <see cref="ReFlyRevertButtonGate"/> never forces the override without it.
        /// </summary>
        internal static bool IsInstalled()
        {
            if (installedConfirmed)
                return true;
            try
            {
                MethodInfo target = AccessTools.Method(
                    typeof(GameParameters.ParameterNode),
                    nameof(GameParameters.ParameterNode.Save),
                    new Type[] { typeof(ConfigNode) });
                if (target == null)
                    return false;
                HarmonyLib.Patches info = Harmony.GetPatchInfo(target);
                if (info == null || info.Postfixes == null)
                    return false;
                foreach (var p in info.Postfixes)
                {
                    if (p != null && p.owner == HarmonyId
                        && p.PatchMethod != null
                        && p.PatchMethod.DeclaringType == typeof(FlightParamsCanRestartPersistPatch))
                    {
                        installedConfirmed = true;
                        ParsekLog.Verbose("ReFlySession",
                            "FlightParams save guard confirmed installed on GameParameters.ParameterNode.Save");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("ReFlySession",
                    $"FlightParams save guard probe failed: {ex.GetType().Name}: {ex.Message}");
            }
            return false;
        }
    }
}
