using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Patches
{
    /// <summary>
    /// The facility-repair click block (KSC-REPAIR-AFTER-REWIND-DOUBLE-CHARGE). After a
    /// Parsek rewind to a UT between a building's destruction and its committed repair, the
    /// repair is a future ledger row; repairing again now would put a second repair of the
    /// same destruction in the ledger and the funds walk would charge both. The refusal runs
    /// first in the <c>SpaceCenterBuilding.RepairFacility(bool)</c> prefix
    /// (<see cref="FacilityRepairScopePatch"/>), BEFORE stock's affordability check and
    /// funds debit, so a refused repair deducts nothing and repairs nothing. It reads
    /// <see cref="StockUiReservationPredicates.IsFacilityRepairBlocked"/>, the predicate the
    /// facility menu's greyed Repair button reads through
    /// <see cref="StockUiDecorationQuery.ForFacilityMenuRepair"/> (the pairing rule), and
    /// shows the same text.
    /// </summary>
    internal static class FacilityRepairBlock
    {
        private const string Tag = "FacilityRepairPatch";

        /// <summary>The refusal over a live building: true when the repair must be BLOCKED.</summary>
        internal static bool TryBlockFacilityRepair(SpaceCenterBuilding building)
        {
            if (building == null) return false;
            string facilityId = building.Facility != null ? building.Facility.id : building.facilityName;
            return TryBlockFacilityRepairForBuildings(facilityId,
                FacilityRepairCapturePatchHelpers.ReadBuildings(building));
        }

        /// <summary>
        /// The refusal over one facility's buildings as stock holds them now (id, destroyed).
        /// Returns true when the repair must be BLOCKED, and shows the refusal dialog as a
        /// side effect; false to let stock repair. <paramref name="facilityId"/> only names
        /// the facility in the log and the dialog title.
        /// </summary>
        internal static bool TryBlockFacilityRepairForBuildings(
            string facilityId, IList<FacilityRepairCapture.BuildingRepairInput> buildings)
        {
            var ic = CultureInfo.InvariantCulture;
            string name = string.IsNullOrEmpty(facilityId) ? "<none>" : facilityId;
            if (GameStateRecorder.IsReplayingActions)
            {
                ParsekLog.Verbose(Tag, $"Bypassing repair block for '{name}' - action replay in progress");
                return false;
            }

            var index = CommittedFutureIndexCache.Current;
            double nowUT = CommittedFutureIndexCache.CurrentUT();
            int destroyed = 0;
            if (buildings != null)
                for (int i = 0; i < buildings.Count; i++)
                    if (buildings[i].IsDestroyed) destroyed++;

            var covering = StockUiReservationPredicates.CommittedRepairsCoveringFacility(index, buildings, nowUT);
            if (covering.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    $"Allowing facility repair: '{name}' - destroyedBuildings={destroyed.ToString(ic)}, " +
                    $"no committed future repair covers the destruction (nowUT={nowUT.ToString("F0", ic)})");
                return false;
            }

            var last = covering[covering.Count - 1];
            ParsekLog.Info(Tag,
                $"Blocking facility repair: '{name}' - {covering.Count.ToString(ic)} destroyed building(s) of " +
                $"{destroyed.ToString(ic)} already repaired by a committed future row, first building='{covering[0].Key}' " +
                $"ut={covering[0].UT.ToString("F0", ic)} last ut={last.UT.ToString("F0", ic)} " +
                $"nowUT={nowUT.ToString("F0", ic)}");

            var text = StockUiReservationPredicates.ExplainFacilityRepair(
                index, buildings, nowUT, ReservationExplanation.DefaultDateFormatter);
            CommittedActionDialog.ShowBlocked(BlockedDialogTitle(facilityId ?? covering[0].Key), text.Body, "");
            return true;
        }

        /// <summary>The refusal dialog's title: the building's stock name, never the raw id.</summary>
        internal static string BlockedDialogTitle(string facilityId)
        {
            return "Cannot repair \"" + FacilityDisplayNames.ResolveBuildingDisplayName(facilityId) + "\"";
        }
    }
}
