using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KSP.UI.Screens.Flight;
using KSP.UI.Screens.Flight.Dialogs;
using UnityEngine;

namespace Parsek.Patches
{
    /// <summary>
    /// Crew portrait, mark: postfix on the private <c>KerbalPortrait.Update()</c>. For a
    /// hovered IVA portrait stock writes <c>evaButton.interactable</c> and
    /// <c>evaTooltip.textString</c> (its own locked reason) every frame; the postfix greys the
    /// button for a held kerbal and puts the reason in that same stock tooltip
    /// (<see cref="StockUiFlightCrewDecoration.DecoratePortrait"/>).
    /// </summary>
    [HarmonyPatch]
    internal static class KerbalPortraitEvaPatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("StockUiOverlay",
                    "KerbalPortrait.Update() not found - the portrait EVA button is not greyed for a held kerbal; "
                    + "the FlightEVA.spawnEVA backstop still refuses him");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(KerbalPortrait), "Update", Type.EmptyTypes);
        }

        static void Postfix(KerbalPortrait __instance)
        {
            if (ParsekGameModeGate.CheckInert("KerbalPortraitEvaPatch.Postfix")) return; // S9 game-mode gate
            try
            {
                StockUiFlightCrewDecoration.DecoratePortrait(__instance);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("StockUiOverlay", "portrait-eva-failed",
                    "portrait EVA annotation failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }
    }

    /// <summary>
    /// Crew hatch dialog, mark: postfix on the protected
    /// <c>CrewHatchDialog.CreateList(List&lt;ProtoCrewMember&gt;)</c>, which builds one
    /// <c>CrewHatchDialogWidget</c> (name, EVA, Transfer) per crew member when the player
    /// clicks a hatch. Held kerbals' EVA and Transfer are greyed with the reason
    /// (<see cref="StockUiFlightCrewDecoration.DecorateHatchDialog"/>).
    /// </summary>
    [HarmonyPatch]
    internal static class CrewHatchDialogCreateListPatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("StockUiOverlay",
                    "CrewHatchDialog.CreateList(List<ProtoCrewMember>) not found - the hatch dialog's EVA / Transfer "
                    + "rows are not greyed for a held kerbal; the EVA and transfer backstops still refuse him");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(CrewHatchDialog), "CreateList", new[] { typeof(List<ProtoCrewMember>) });
        }

        static void Postfix(CrewHatchDialog __instance)
        {
            if (ParsekGameModeGate.CheckInert("CrewHatchDialogCreateListPatch.Postfix")) return; // S9 game-mode gate
            try
            {
                StockUiFlightCrewDecoration.DecorateHatchDialog(__instance);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("StockUiOverlay", "crew-hatch-pass-failed",
                    "crew hatch dialog annotation failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }
    }

    /// <summary>
    /// EVA backstop: prefix on <c>FlightEVA.spawnEVA(ProtoCrewMember, Part, Transform, bool)</c>,
    /// the one stock EVA entry point (the hatch dialog's EVA and, through the static
    /// <c>FlightEVA.SpawnEVA(Kerbal)</c>, the portrait's EVA both reach it). A refused EVA
    /// returns null before stock's hatch checks, <c>onAttemptEva</c> or any state change,
    /// which is stock's own "no EVA" result (an obstructed hatch returns null the same way).
    /// </summary>
    [HarmonyPatch]
    internal static class FlightEvaSpawnBackstopPatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("CrewMove",
                    "FlightEVA.spawnEVA(ProtoCrewMember, Part, Transform, bool) not found - a held kerbal's EVA is "
                    + "refused only by the greyed stock buttons");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(FlightEVA), "spawnEVA",
                new[] { typeof(ProtoCrewMember), typeof(Part), typeof(Transform), typeof(bool) });
        }

        static bool Prefix(ProtoCrewMember pCrew, Part fromPart, ref KerbalEVA __result)
        {
            if (ParsekGameModeGate.CheckInert("FlightEvaSpawnBackstopPatch.Prefix")) return true; // S9 game-mode gate
            try
            {
                if (StockUiFlightCrewDecoration.ShouldAllowEva(pCrew, fromPart)) return true;
                __result = null;
                return false;
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("CrewMove", "eva-backstop-threw",
                    "spawnEVA prefix threw, stock proceeds (" + ex.GetType().Name + ": " + ex.Message + ")");
                return true;
            }
        }
    }

    /// <summary>
    /// Transfer backstop: prefix on <c>CrewTransfer.Create(Part, ProtoCrewMember, Callback)</c>,
    /// the one stock crew-transfer entry point (called only by the hatch dialog's Transfer).
    /// A refused transfer creates no transfer host, so no part highlighting starts; the
    /// caller already tolerates a null transfer (it only null-checks it later).
    /// </summary>
    [HarmonyPatch]
    internal static class CrewTransferCreateBackstopPatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("CrewMove",
                    "CrewTransfer.Create(Part, ProtoCrewMember, Callback) not found - a held kerbal's transfer is "
                    + "refused only by the greyed stock buttons");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(CrewTransfer), "Create",
                new[] { typeof(Part), typeof(ProtoCrewMember), typeof(Callback<PartItemTransfer.DismissAction, Part>) });
        }

        static bool Prefix(Part srcPart, ProtoCrewMember crewMember, ref CrewTransfer __result)
        {
            if (ParsekGameModeGate.CheckInert("CrewTransferCreateBackstopPatch.Prefix")) return true; // S9 game-mode gate
            try
            {
                if (StockUiFlightCrewDecoration.ShouldAllowTransfer(crewMember, srcPart)) return true;
                __result = null;
                return false;
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("CrewMove", "transfer-backstop-threw",
                    "CrewTransfer.Create prefix threw, stock proceeds (" + ex.GetType().Name + ": " + ex.Message + ")");
                return true;
            }
        }
    }
}
