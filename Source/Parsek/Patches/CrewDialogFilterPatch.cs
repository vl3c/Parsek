using System.Reflection;
using HarmonyLib;
using KSP.UI;

namespace Parsek.Patches
{
    /// <summary>
    /// Filters Parsek-reserved and retired kerbals from the VAB/SPH crew
    /// assignment dialog's available crew list.
    ///
    /// BaseCrewAssignmentDialog.CreateAvailList iterates Available kerbals and
    /// calls AddAvailItem for each. This prefix intercepts that call and skips
    /// kerbals that KerbalsModule.ShouldFilterFromCrewDialog identifies as
    /// reserved or retired.
    ///
    /// This replaces the old approach of setting rosterStatus = Assigned (which
    /// caused ValidateAssignments tug-of-war and Astronaut Complex count mismatch).
    /// </summary>
    [HarmonyPatch]
    internal static class CrewDialogFilterPatch
    {
        static MethodBase TargetMethod()
        {
            // Target the 3-param overload that CreateAvailList calls.
            // C# bakes default arguments at the call site, so AddAvailItem(crew)
            // compiles to a call to AddAvailItem(crew, null, ButtonTypes.V).
            var method = AccessTools.Method(
                typeof(BaseCrewAssignmentDialog),
                "AddAvailItem",
                new[] { typeof(ProtoCrewMember), typeof(UIList), typeof(CrewListItem.ButtonTypes) });

            if (method == null)
                ParsekLog.Warn("CrewDialogFilter",
                    "BaseCrewAssignmentDialog.AddAvailItem(PCM,UIList,ButtonTypes) not found " +
                    "— crew dialog filtering will not apply. " +
                    "Harmony will skip this patch (caught by ParsekHarmony try/catch).");

            // Returning null causes Harmony to throw during patching.
            // ParsekHarmony.Awake wraps each patch in try/catch, so this
            // just skips the patch and increments the failed counter.
            return method;
        }

        static bool Prefix(ProtoCrewMember crew)
        {
            if (crew == null) return true;

            var kerbals = LedgerOrchestrator.Kerbals;
            if (kerbals == null) return true;

            if (kerbals.ShouldFilterFromCrewDialog(crew.name))
            {
                ParsekLog.Verbose("CrewDialogFilter",
                    $"Filtered '{crew.name}' from crew assignment dialog");
                return false;
            }

            return true;
        }
    }
}
