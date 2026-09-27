using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KSP.UI;
using KSP.UI.Screens;
using KSP.UI.TooltipTypes;

namespace Parsek.Patches
{
    /// <summary>
    /// Astronaut Complex mark on each new row: after stock's private
    /// <c>AstronautComplex.AddItem_Applicants / _Available / _Assigned / _Kia</c> (void; the
    /// row is appended last to that tab's list), set the row's stock label and, for a
    /// clickable block, stock's locked-with-reason button state. Harmony postfixes are
    /// scene-independent, so the complex opened from the VAB/SPH crew dialog is covered.
    /// </summary>
    [HarmonyPatch]
    internal static class AstronautComplexAddItemPatch
    {
        internal static readonly string[] TargetNames =
        {
            "AddItem_Applicants",
            "AddItem_Available",
            "AddItem_Assigned",
            "AddItem_Kia"
        };

        static IEnumerable<MethodBase> TargetMethods()
        {
            var methods = ResolveTargetMethodsForTesting();
            if (methods.Count < TargetNames.Length)
                ParsekLog.Warn("StockUiOverlay",
                    "AstronautComplex.AddItem_* resolved " + methods.Count + " of " + TargetNames.Length
                    + " - rows of the missing tabs are annotated only by the UpdateCrewCounts pass");
            return methods;
        }

        internal static List<MethodBase> ResolveTargetMethodsForTesting()
        {
            var result = new List<MethodBase>();
            for (int i = 0; i < TargetNames.Length; i++)
            {
                MethodInfo m = AccessTools.Method(typeof(AstronautComplex), TargetNames[i]);
                if (m != null && HasCrewParameter(m)) result.Add(m);
            }
            return result;
        }

        /// <summary>Every target takes the row's kerbal as a <c>ProtoCrewMember crew</c>
        /// parameter, which the postfix binds by name.</summary>
        internal static bool HasCrewParameter(MethodBase m)
        {
            var ps = m.GetParameters();
            for (int i = 0; i < ps.Length; i++)
                if (ps[i].Name == "crew" && ps[i].ParameterType == typeof(ProtoCrewMember))
                    return true;
            return false;
        }

        static void Postfix(AstronautComplex __instance, ProtoCrewMember crew, MethodBase __originalMethod)
        {
            if (ParsekGameModeGate.CheckInert("AstronautComplexAddItemPatch.Postfix")) return; // S9 game-mode gate
            try
            {
                string tab = StockUiAstronautDecoration.TabFor(__originalMethod != null ? __originalMethod.Name : null);
                StockUiAstronautDecoration.DecorateAddedRow(__instance, tab, crew);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("StockUiOverlay", "ac-additem-failed",
                    "Astronaut Complex row annotation failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }
    }

    /// <summary>
    /// Astronaut Complex re-apply: stock's private <c>UpdateCrewCounts</c> re-unlocks every
    /// applicant (<c>SetApplicantsListUnlocked(true)</c>) and runs one frame after the
    /// screen opens and after every hire and dismissal, so every row is re-decorated after
    /// it and the pass is logged there.
    /// </summary>
    [HarmonyPatch]
    internal static class AstronautComplexCrewCountsPatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("StockUiOverlay",
                    "AstronautComplex.UpdateCrewCounts() not found - the Astronaut Complex hire block is not re-applied " +
                    "after stock re-unlocks applicants (KerbalHirePatch still refuses the click)");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(AstronautComplex), "UpdateCrewCounts", Type.EmptyTypes);
        }

        static void Postfix(AstronautComplex __instance)
        {
            if (ParsekGameModeGate.CheckInert("AstronautComplexCrewCountsPatch.Postfix")) return; // S9 game-mode gate
            try
            {
                StockUiAstronautDecoration.DecorateAllRows(__instance, "crew counts");
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("StockUiOverlay", "ac-crewcounts-failed",
                    "Astronaut Complex decoration pass failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }
    }

    /// <summary>
    /// Astronaut Complex why: stock rebuilds the crew tooltip from scratch in
    /// <c>TooltipController_CrewAC.SetTooltip(ProtoCrewMember, string, string)</c> (also
    /// from <c>CrewListItem.SetButtonEnabled</c>), so the explanation is appended there and
    /// <c>showTooltip</c> forced on.
    /// </summary>
    [HarmonyPatch]
    internal static class CrewTooltipReservationPatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("StockUiOverlay",
                    "TooltipController_CrewAC.SetTooltip(ProtoCrewMember, string, string) not found - " +
                    "the crew tooltip explanation will not apply");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(TooltipController_CrewAC), nameof(TooltipController_CrewAC.SetTooltip),
                new[] { typeof(ProtoCrewMember), typeof(string), typeof(string) });
        }

        static void Postfix(TooltipController_CrewAC __instance, ProtoCrewMember pcm)
        {
            if (ParsekGameModeGate.CheckInert("CrewTooltipReservationPatch.Postfix")) return; // S9 game-mode gate
            try
            {
                StockUiAstronautDecoration.AppendCrewTooltip(__instance, pcm);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("StockUiOverlay", "ac-tooltip-failed",
                    "Crew tooltip explanation failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }
    }
}
