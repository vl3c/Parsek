using System;
using System.Reflection;
using HarmonyLib;
using KSP.UI;

namespace Parsek.Patches
{
    /// <summary>
    /// VAB/SPH crew assignment dialog, mark: after stock builds an available-crew row
    /// (<c>BaseCrewAssignmentDialog.AddAvailItem(ProtoCrewMember, out CrewListItem, UIList,
    /// ButtonTypes)</c>, which the 3-argument overload forwards to and
    /// <c>CrewAssignmentDialog</c> does not override), a kerbal the committed timeline
    /// reserves gets stock's inactive-crew look and the locked-with-reason tooltip. He is
    /// listed, not hidden (owner ruling R3). The predicate is the one the old hiding
    /// filter used (<see cref="KerbalsModule.ShouldFilterFromCrewDialog"/>).
    /// </summary>
    [HarmonyPatch]
    internal static class CrewDialogAvailItemPatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("StockUiOverlay",
                    "BaseCrewAssignmentDialog.AddAvailItem(PCM, out CrewListItem, UIList, ButtonTypes) not found - " +
                    "reserved kerbals are marked only by the CreateAvailList pass; the seat backstops still refuse them");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(BaseCrewAssignmentDialog), "AddAvailItem",
                new[]
                {
                    typeof(ProtoCrewMember),
                    typeof(CrewListItem).MakeByRefType(),
                    typeof(UIList),
                    typeof(CrewListItem.ButtonTypes)
                });
        }

        static void Postfix(BaseCrewAssignmentDialog __instance, ProtoCrewMember crew, ref CrewListItem item)
        {
            if (ParsekGameModeGate.CheckInert("CrewDialogAvailItemPatch.Postfix")) return; // S9 game-mode gate
            try
            {
                StockUiCrewDialogDecoration.DecorateAddedRow(__instance, item, crew);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("StockUiOverlay", "crew-dialog-additem-failed",
                    "crew dialog row annotation failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }
    }

    /// <summary>
    /// VAB/SPH crew assignment dialog, per-build pass: stock rebuilds the whole available
    /// list in the private <c>CreateAvailList(VesselCrewManifest)</c> on every
    /// <c>RefreshCrewLists(..., updateUI: true)</c> (every seat change, Reset, part
    /// attach, and the Astronaut Complex closing). The postfix re-derives every row and
    /// logs the pass once.
    /// </summary>
    [HarmonyPatch]
    internal static class CrewDialogAvailListPatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("StockUiOverlay",
                    "BaseCrewAssignmentDialog.CreateAvailList(VesselCrewManifest) not found - " +
                    "the crew dialog pass is not logged and stale rows are not re-derived");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(BaseCrewAssignmentDialog), "CreateAvailList",
                new[] { typeof(VesselCrewManifest) });
        }

        static void Postfix(BaseCrewAssignmentDialog __instance)
        {
            if (ParsekGameModeGate.CheckInert("CrewDialogAvailListPatch.Postfix")) return; // S9 game-mode gate
            try
            {
                StockUiCrewDialogDecoration.DecorateAvailList(__instance, "build");
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("StockUiOverlay", "crew-dialog-pass-failed",
                    "crew dialog decoration pass failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }
    }

    /// <summary>
    /// Seat backstop for the click-to-assign button and Fill: every stock placement into
    /// an empty seat goes through <c>BaseCrewAssignmentDialog.MoveCrewToEmptySeat</c>
    /// (<c>CrewAssignmentDialog</c>'s override calls base, then updates the ship manifest
    /// and rebuilds the lists, so a refusal leaves the dialog consistent).
    /// </summary>
    [HarmonyPatch]
    internal static class CrewDialogMoveToSeatPatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("CrewDialog",
                    "BaseCrewAssignmentDialog.MoveCrewToEmptySeat(UIList, UIList, UIListItem, int) not found - " +
                    "a reserved kerbal's click-to-assign is closed only by stock's disabled hover");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return StockUiCrewDialogDecoration.ResolveMoveCrewToEmptySeat();
        }

        static bool Prefix(UIListItem itemToMove)
        {
            if (ParsekGameModeGate.CheckInert("CrewDialogMoveToSeatPatch.Prefix")) return true; // S9 game-mode gate
            try
            {
                return StockUiCrewDialogDecoration.ShouldAllowAssignment(
                    StockUiCrewDialogDecoration.KerbalNameOf(itemToMove), "MoveCrewToEmptySeat");
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("CrewDialog", "crew-dialog-seat-failed",
                    "crew dialog seat check failed (" + ex.GetType().Name + ": " + ex.Message + ") - stock move allowed");
                return true;
            }
        }
    }

    /// <summary>
    /// Seat backstop for drag-and-drop: a row dragged from the available list onto the
    /// crew list lands through <c>BaseCrewAssignmentDialog.DropOnCrewList</c>. Stock's
    /// inactive look already disables the drag (<c>UIDragPanel.dragEnabled = false</c>);
    /// this refuses the drop if anything re-enables it. A seat-to-seat swap is not a new
    /// assignment and is left alone.
    /// </summary>
    [HarmonyPatch]
    internal static class CrewDialogDropOnCrewListPatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("CrewDialog",
                    "BaseCrewAssignmentDialog.DropOnCrewList(UIList, UIListItem, int) not found - " +
                    "a reserved kerbal's drag is closed only by stock's disabled drag");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(BaseCrewAssignmentDialog), "DropOnCrewList",
                new[] { typeof(UIList), typeof(UIListItem), typeof(int) });
        }

        static bool Prefix(BaseCrewAssignmentDialog __instance, UIList fromList, UIListItem insertItem)
        {
            if (ParsekGameModeGate.CheckInert("CrewDialogDropOnCrewListPatch.Prefix")) return true; // S9 game-mode gate
            try
            {
                if (__instance == null || fromList == null || fromList != __instance.scrollListAvail)
                    return true;
                return StockUiCrewDialogDecoration.ShouldAllowAssignment(
                    StockUiCrewDialogDecoration.KerbalNameOf(insertItem), "DropOnCrewList");
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("CrewDialog", "crew-dialog-drop-failed",
                    "crew dialog drop check failed (" + ex.GetType().Name + ": " + ex.Message + ") - stock drop allowed");
                return true;
            }
        }
    }

    /// <summary>
    /// Fill: stock's <c>ButtonFill</c> always takes the TOP available row, so a greyed
    /// kerbal there would stop the fill. With a refused kerbal listed, the prefix fills
    /// from the first assignable row instead (<see cref="StockUiCrewDialogDecoration.FillSkippingRefused"/>);
    /// otherwise stock's fill runs unchanged.
    /// </summary>
    [HarmonyPatch]
    internal static class CrewDialogFillPatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("CrewDialog",
                    "BaseCrewAssignmentDialog.ButtonFill() not found - Fill stops at a reserved kerbal at the top " +
                    "of the list (the seat backstop still refuses him)");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(BaseCrewAssignmentDialog), "ButtonFill", Type.EmptyTypes);
        }

        static bool Prefix(BaseCrewAssignmentDialog __instance)
        {
            if (ParsekGameModeGate.CheckInert("CrewDialogFillPatch.Prefix")) return true; // S9 game-mode gate
            try
            {
                return StockUiCrewDialogDecoration.FillSkippingRefused(__instance);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("CrewDialog", "crew-dialog-fill-failed",
                    "crew dialog fill failed (" + ex.GetType().Name + ": " + ex.Message + ") - stock Fill runs; " +
                    "the seat backstop refuses each reserved kerbal it reaches");
                return true;
            }
        }
    }
}
