using System;
using System.Collections.Generic;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Scope around <c>SpaceCenterBuilding.RepairFacility(bool deduceFunds)</c>, the stock
    /// funded-repair entry point. The prefix records each destroyed building's funds share
    /// (stock's <c>GetRepairsCost</c> split per building) BEFORE the repair runs; the
    /// <c>OnKSCStructureRepairing</c> events that <c>RepairStructures</c> fires inside the call
    /// read their cost from it, and the finalizer writes the facility's repair rows in one
    /// batch (see <see cref="FacilityRepairCapture"/>). Observation only: the prefix never
    /// skips the original, and a finalizer (not a postfix) closes the scope so an exception
    /// inside stock cannot leave it open.
    /// </summary>
    [HarmonyPatch(typeof(SpaceCenterBuilding), "RepairFacility", new[] { typeof(bool) })]
    internal static class FacilityRepairScopePatch
    {
        static void Prefix(SpaceCenterBuilding __instance, bool deduceFunds)
        {
            try
            {
                bool fundsCharged = deduceFunds && Funding.Instance != null;
                float multiplier = 1f;
                if (HighLogic.CurrentGame != null && HighLogic.CurrentGame.Parameters != null)
                    multiplier = HighLogic.CurrentGame.Parameters.Career.FundsLossMultiplier;

                var inputs = FacilityRepairCapturePatchHelpers.ReadBuildings(__instance);
                var shares = FacilityRepairCapture.BuildRepairCostShares(inputs, multiplier, fundsCharged);
                string facility = __instance != null && __instance.Facility != null
                    ? __instance.Facility.id
                    : __instance != null ? __instance.facilityName : null;
                FacilityRepairCapture.BeginScope(facility, shares);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("GameStateRecorder",
                    $"FacilityRepairScopePatch prefix failed ({ex.GetType().Name}: {ex.Message}) - " +
                    "repair rows of this call carry cost 0");
            }
        }

        static Exception Finalizer(Exception __exception)
        {
            FacilityRepairCapture.EndScope(__exception == null ? "repair-returned" : "repair-threw");
            return __exception;
        }
    }

    /// <summary>
    /// Captures the silent repair inside <c>SpaceCenterBuilding.ResetStructures</c> (private;
    /// called by UpgradeFacility and DowngradeFacility before <c>SetLevel</c>). It resets
    /// every building to intact through <c>DestructibleBuilding.Reset()</c>, which fires no
    /// event, so upgrading a destroyed facility would otherwise repair it with no ledger row.
    /// The upgrade's own funds are the StructureConstruction debit of the FacilityUpgrade row,
    /// so these repairs cost 0.
    /// </summary>
    [HarmonyPatch(typeof(SpaceCenterBuilding), "ResetStructures")]
    internal static class FacilityResetStructuresPatch
    {
        static void Prefix(SpaceCenterBuilding __instance, out List<string> __state)
        {
            __state = null;
            try
            {
                __state = FacilityRepairCapture.SelectResetRepairedBuildings(
                    FacilityRepairCapturePatchHelpers.ReadBuildings(__instance));
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("GameStateRecorder",
                    $"FacilityResetStructuresPatch prefix failed ({ex.GetType().Name}: {ex.Message})");
            }
        }

        static void Postfix(List<string> __state)
        {
            if (__state == null || __state.Count == 0)
                return;
            FacilityRepairCapture.ReportStructuresReset(__state);
        }
    }

    internal static class FacilityRepairCapturePatchHelpers
    {
        internal static List<FacilityRepairCapture.BuildingRepairInput> ReadBuildings(
            SpaceCenterBuilding building)
        {
            var list = new List<FacilityRepairCapture.BuildingRepairInput>();
            if (building == null || building.destructibles == null)
                return list;
            var array = building.destructibles;
            for (int i = 0; i < array.Length; i++)
            {
                var db = array[i];
                if (db == null)
                    continue;
                list.Add(new FacilityRepairCapture.BuildingRepairInput
                {
                    BuildingId = db.id,
                    RepairCost = db.RepairCost,
                    IsDestroyed = db.IsDestroyed
                });
            }
            return list;
        }
    }
}
