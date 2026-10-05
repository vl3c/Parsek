using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Arms <see cref="DockingPortSeparation"/> when Undock runs on a pre-attached docking
    /// port. Decompiled <c>ModuleDockingNode.Undock()</c> (public, no parameters, KSP 1.12.5)
    /// is the [KSPEvent] the player clicks (and the target of the Undock action group); with
    /// <c>undockPreAttached</c> it calls <c>Decouple()</c> -&gt; <c>Part.decouple()</c>. Staging
    /// a port runs <c>OnActive()</c> -&gt; <c>Decouple()</c> without passing through
    /// <c>Undock()</c>, so it never arms the note. <c>UndockSameVessel()</c> only runs the FSM
    /// undock event (no vessel split), so it is not patched.
    /// </summary>
    [HarmonyPatch(typeof(ModuleDockingNode), nameof(ModuleDockingNode.Undock))]
    internal static class DockingNodeUndockIntentPatch
    {
        static void Prefix(ModuleDockingNode __instance)
        {
            if (ParsekGameModeGate.CheckInert("DockingNodeUndockIntentPatch.Prefix")) return;
            if (__instance == null || __instance.part == null) return;
            bool preAttached = __instance.fsm != null && __instance.st_preattached != null
                && __instance.fsm.CurrentState == __instance.st_preattached;
            if (!preAttached)
            {
                ParsekLog.Verbose("DockUndockIntent",
                    $"Undock on pid={__instance.part.persistentId} is not pre-attached; " +
                    "Part.Undock raises onPartUndock, intent not armed");
                return;
            }
            uint otherPid = __instance.referenceNode?.attachedPart?.persistentId ?? 0u;
            DockingPortSeparation.ArmPreAttachedUndock(
                __instance.part.persistentId, otherPid, Planetarium.GetUniversalTime());
        }
    }
}
