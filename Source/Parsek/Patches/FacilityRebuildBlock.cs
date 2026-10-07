using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Patches
{
    /// <summary>
    /// The facility "Rebuild lvl N" click block (owner ruling 2026-10-07, closing the open
    /// limit of FACILITY-DOWNGRADE-DEBIT-NOT-LEDGERED). Levels are absolute, so a Rebuild placed
    /// before a committed future level change of the same facility rewrites that row (a
    /// committed upgrade 2 -> 3 becomes a 1 -> 3 jump charged at one step's price); and the
    /// Rebuild repairs every destroyed building for free (<c>ResetStructures</c>), so it also
    /// rewrites a committed future repair that covers the facility's current destruction. The
    /// refusal runs first in the <c>SpaceCenterBuilding.DowngradeFacility(bool)</c> prefix
    /// (<see cref="FacilityDowngradeSpendPatch"/>), BEFORE stock's affordability check, its
    /// funds debit and the downgrade capture scope, so a refused Rebuild charges, repairs and
    /// records nothing. It reads <see cref="StockUiReservationPredicates.IsFacilityRebuildBlocked"/>,
    /// the predicate the facility menu's greyed Rebuild button reads through
    /// <see cref="StockUiDecorationQuery.ForFacilityMenuRebuild"/> (the pairing rule), and shows
    /// the same text.
    /// </summary>
    internal static class FacilityRebuildBlock
    {
        private const string Tag = "FacilityRebuildPatch";

        /// <summary>
        /// The refusal over one facility (its upgradeable id, read as the menu decoration reads
        /// it) and its buildings as stock holds them now. Returns true when the Rebuild must be
        /// BLOCKED, and shows the refusal dialog as a side effect; false to let stock rebuild.
        /// </summary>
        internal static bool TryBlockFacilityRebuildFor(
            string facilityId, IList<FacilityRepairCapture.BuildingRepairInput> buildings)
        {
            var ic = CultureInfo.InvariantCulture;
            string name = string.IsNullOrEmpty(facilityId) ? "<none>" : facilityId;
            if (GameStateRecorder.IsReplayingActions)
            {
                ParsekLog.Verbose(Tag, $"Bypassing rebuild block for '{name}' - action replay in progress");
                return false;
            }

            var index = CommittedFutureIndexCache.Current;
            double nowUT = CommittedFutureIndexCache.CurrentUT();
            var blockers = StockUiReservationPredicates.FacilityLevelChangeBlockers(index, facilityId, buildings, nowUT);
            if (blockers.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    $"Allowing facility rebuild: '{name}' - no committed future level change, and no committed " +
                    $"future repair covers the destruction (nowUT={nowUT.ToString("F0", ic)})");
                return false;
            }

            int levelChanges = 0, repairs = 0;
            for (int i = 0; i < blockers.Count; i++)
            {
                if (blockers[i].Kind == CommittedFutureKind.FacilityRepair) repairs++;
                else levelChanges++;
            }
            var first = blockers[0];
            var last = blockers[blockers.Count - 1];
            ParsekLog.Info(Tag,
                $"Blocking facility rebuild: '{name}' - {levelChanges.ToString(ic)} committed future level " +
                $"change(s), {repairs.ToString(ic)} covering repair(s); earliest={DescribeRow(first)} " +
                $"ut={first.UT.ToString("F0", ic)} last ut={last.UT.ToString("F0", ic)} " +
                $"nowUT={nowUT.ToString("F0", ic)}");

            var text = ReservationExplanation.FacilityRebuild(blockers, ReservationExplanation.DefaultDateFormatter);
            CommittedActionDialog.ShowBlocked(BlockedDialogTitle(facilityId), text.Body, "");
            return true;
        }

        /// <summary>The log word for one blocking row: upgrade, rebuild (toLevel) or repair.</summary>
        private static string DescribeRow(CommittedFutureEntry row)
        {
            if (row.Kind == CommittedFutureKind.FacilityRepair) return "repair";
            return (row.FacilityDowngrade ? "rebuild" : "upgrade")
                + " toLevel=" + row.FacilityToLevel.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The refusal dialog's title: the building's stock name, never the raw id.</summary>
        internal static string BlockedDialogTitle(string facilityId)
        {
            return "Cannot rebuild \"" + FacilityDisplayNames.ResolveBuildingDisplayName(facilityId) + "\"";
        }
    }
}
