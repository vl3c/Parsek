using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Prevents KSP from setting Parsek-reserved kerbals to Missing.
    ///
    /// KSP's KerbalRoster.ValidateAssignments(Game) checks all Assigned kerbals
    /// and sets any that aren't on a vessel crew manifest to Missing (with a
    /// 2000s respawn timer). Parsek uses Assigned status to reserve kerbals for
    /// ghost playback without placing them on a real vessel, so this validation
    /// incorrectly demotes them every scene load.
    ///
    /// This postfix runs after validation and re-applies Assigned status to any
    /// managed kerbal that was demoted.
    /// </summary>
    [HarmonyPatch(typeof(KerbalRoster), nameof(KerbalRoster.ValidateAssignments))]
    internal static class KerbalAssignmentValidationPatch
    {
        static void Postfix(KerbalRoster __instance)
        {
            var kerbals = LedgerOrchestrator.Kerbals;
            if (kerbals == null) return;

            int restored = 0;
            foreach (ProtoCrewMember pcm in __instance.Crew)
            {
                if (pcm.rosterStatus == ProtoCrewMember.RosterStatus.Missing
                    && kerbals.IsManaged(pcm.name))
                {
                    pcm.rosterStatus = ProtoCrewMember.RosterStatus.Assigned;
                    restored++;
                }
            }

            if (restored > 0)
                ParsekLog.Verbose("KerbalValidation",
                    $"Restored {restored} managed kerbal(s) from Missing to Assigned " +
                    "after KSP ValidateAssignments");
        }
    }

    /// <summary>
    /// Fixes Astronaut Complex "Assigned" tab showing a count but empty list.
    ///
    /// KSP's GetAssignedCrewCount() counts kerbals with rosterStatus == Assigned,
    /// but CreateAssignedList() only shows kerbals from vessel crew manifests.
    /// Parsek-reserved kerbals are Assigned but not on any vessel, so the count
    /// includes them but the list doesn't.
    ///
    /// This postfix subtracts Parsek-managed kerbals that aren't on a vessel
    /// from the count, making the number match the displayed list.
    /// </summary>
    [HarmonyPatch(typeof(KerbalRoster), nameof(KerbalRoster.GetAssignedCrewCount))]
    internal static class AssignedCrewCountPatch
    {
        static void Postfix(KerbalRoster __instance, ref int __result)
        {
            var kerbals = LedgerOrchestrator.Kerbals;
            if (kerbals == null || __result == 0) return;

            var flightState = HighLogic.CurrentGame?.flightState;

            int subtract = 0;
            foreach (ProtoCrewMember pcm in __instance.Crew)
            {
                if (pcm.rosterStatus != ProtoCrewMember.RosterStatus.Assigned) continue;
                if (!kerbals.IsManaged(pcm.name)) continue;

                // Check if they're actually on a vessel
                bool onVessel = false;
                if (flightState != null)
                {
                    for (int i = 0; i < flightState.protoVessels.Count; i++)
                    {
                        if (flightState.protoVessels[i].GetVesselCrew().Contains(pcm))
                        {
                            onVessel = true;
                            break;
                        }
                    }
                }

                if (!onVessel)
                    subtract++;
            }

            if (subtract > 0)
            {
                __result -= subtract;
                if (__result < 0) __result = 0;
            }
        }
    }
}
