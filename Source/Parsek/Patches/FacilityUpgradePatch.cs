using System.Collections.Generic;
using HarmonyLib;
using Upgradeables;

namespace Parsek.Patches
{
    /// <summary>
    /// Harmony prefix on UpgradeableFacility.SetLevel. SetLevel does NOT deduct funds
    /// itself (the stock deduction lives in SpaceCenterBuilding.UpgradeFacility, which is
    /// gated pre-deduction by <see cref="FacilityUpgradeSpendPatch"/>), so this prefix is
    /// a non-destructive backstop for direct SetLevel callers and blocks already-committed
    /// facility level changes only: SetLevel runs no structure reset, so the repair half of
    /// the Upgrade block (read over the buildings by the UpgradeFacility prefix) does not
    /// apply here.
    /// </summary>
    [HarmonyPatch(typeof(UpgradeableFacility), nameof(UpgradeableFacility.SetLevel))]
    internal static class FacilityUpgradePatch
    {
        private const string Tag = "FacilityUpgradePatch";

        static bool Prefix(UpgradeableFacility __instance, int lvl)
        {
            if (ParsekGameModeGate.CheckInert("FacilityUpgradePatch.Prefix")) return true; // S9 game-mode gate
            if (__instance == null) return true;

            // Only block upgrades (level increases), not downgrades or resets
            int currentLevel = __instance.FacilityLevel;
            if (lvl <= currentLevel)
            {
                ParsekLog.Verbose(Tag,
                    $"Allowing facility level change: '{__instance.id}' level {currentLevel} -> {lvl} (not an upgrade)");
                return true;
            }

            return !TryBlockFacilityUpgrade(__instance);
        }

        /// <summary>
        /// The SetLevel backstop's decision (<see cref="TryBlockFacilityUpgradeById"/>).
        /// Funds affordability is intentionally NOT checked (stock's own
        /// CurrencyModifierQuery enforces it, and SetLevel does not receive the cost).
        /// </summary>
        internal static bool TryBlockFacilityUpgrade(UpgradeableFacility facility)
        {
            if (facility == null) return false;
            return TryBlockFacilityUpgradeById(facility.id);
        }

        /// <summary>
        /// The level-change refusal over a facility id (<c>SpaceCenter/LaunchPad</c>), with no
        /// buildings read: blocked while a committed future row still changes the level of the
        /// facility, an upgrade or a stock Rebuild (owner ruling 2026-10-07). The block lifts
        /// once the clock passes the last committed level change of that facility, so a later
        /// upgrade the committed timeline never made is allowed. The SetLevel backstop's
        /// decision; the player's click goes through <see cref="TryBlockFacilityUpgradeFor"/>.
        /// </summary>
        internal static bool TryBlockFacilityUpgradeById(string facilityId)
        {
            return TryBlockFacilityUpgradeFor(facilityId, null);
        }

        /// <summary>
        /// The Upgrade refusal over a facility id and its buildings as stock holds them now,
        /// run by the <c>SpaceCenterBuilding.UpgradeFacility</c> prefix
        /// (<see cref="FacilityUpgradeSpendPatch"/>) before stock's debit: blocked while a
        /// committed future level change of the facility is ahead, or a committed future
        /// repair covers the current destruction of one of its buildings, because the
        /// upgrade's free <c>ResetStructures</c> would repair it now and rewrite that repair
        /// (FACILITY-UPGRADE-FREE-REPAIR-REWRITES-COMMITTED-REPAIR). It reads
        /// <see cref="StockUiReservationPredicates.FacilityUpgradeBlockers"/>, the rows the
        /// facility menu's greyed Upgrade button reads through
        /// <see cref="StockUiDecorationQuery.ForFacilityMenu"/> (the pairing rule), and shows
        /// the same text. Returns true when the upgrade must be BLOCKED, and emits the log
        /// line and the blocked dialog as a side effect; false to allow. A null
        /// <paramref name="buildings"/> checks level changes only.
        /// </summary>
        internal static bool TryBlockFacilityUpgradeFor(
            string facilityId, IList<FacilityRepairCapture.BuildingRepairInput> buildings)
        {
            if (string.IsNullOrEmpty(facilityId)) return false;

            if (GameStateRecorder.IsReplayingActions)
            {
                ParsekLog.Verbose(Tag,
                    $"Bypassing block for '{facilityId}' - action replay in progress");
                return false;
            }

            var index = CommittedFutureIndexCache.Current;
            double nowUT = CommittedFutureIndexCache.CurrentUT();
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var blockers = StockUiReservationPredicates.FacilityUpgradeBlockers(index, facilityId, buildings, nowUT);
            if (blockers.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    $"Allowing facility upgrade: '{facilityId}' - no committed future level change" +
                    (buildings != null ? ", and no committed future repair covers the destruction" : "") +
                    $" (nowUT={nowUT.ToString("F0", ic)})");
                return false;
            }

            var levelChanges = new List<CommittedFutureEntry>();
            var repairs = new List<CommittedFutureEntry>();
            int rebuilds = 0;
            for (int i = 0; i < blockers.Count; i++)
            {
                if (blockers[i].Kind == CommittedFutureKind.FacilityRepair)
                {
                    repairs.Add(blockers[i]);
                    continue;
                }
                levelChanges.Add(blockers[i]);
                if (blockers[i].FacilityDowngrade) rebuilds++;
            }
            if (levelChanges.Count > 0)
            {
                var last = levelChanges[levelChanges.Count - 1];
                ParsekLog.Info(Tag,
                    $"Blocking facility upgrade: '{facilityId}' - {levelChanges.Count.ToString(ic)} committed future " +
                    $"upgrade(s), last ut={last.UT.ToString("F0", ic)} toLevel={last.FacilityToLevel.ToString(ic)} " +
                    $"nowUT={nowUT.ToString("F0", ic)} rebuilds={rebuilds.ToString(ic)} repairs={repairs.Count.ToString(ic)}");
            }
            else
            {
                var first = repairs[0];
                var lastRepair = repairs[repairs.Count - 1];
                ParsekLog.Info(Tag,
                    $"Blocking facility upgrade: '{facilityId}' - no committed future level change; " +
                    $"{repairs.Count.ToString(ic)} committed future repair(s) cover a destruction the upgrade's free " +
                    $"structure reset would repair now, first building='{first.Key}' ut={first.UT.ToString("F0", ic)} " +
                    $"last ut={lastRepair.UT.ToString("F0", ic)} nowUT={nowUT.ToString("F0", ic)} " +
                    $"repairs={repairs.Count.ToString(ic)}");
            }

            var text = ReservationExplanation.FacilityUpgrade(blockers, ReservationExplanation.DefaultDateFormatter);
            CommittedActionDialog.ShowBlocked(BlockedDialogTitle(facilityId), text.Body, "");

            return true;
        }

        /// <summary>The refusal dialog's title: the building's stock name, never the raw id.</summary>
        internal static string BlockedDialogTitle(string facilityId)
        {
            return "Cannot upgrade \"" + FacilityDisplayNames.ResolveBuildingDisplayName(facilityId) + "\"";
        }
    }
}
