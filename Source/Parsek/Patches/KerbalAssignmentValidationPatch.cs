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
    /// managed kerbal that was demoted. This avoids the tug-of-war that causes
    /// the Astronaut Complex "Assigned" tab to show a count but empty list.
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
}
