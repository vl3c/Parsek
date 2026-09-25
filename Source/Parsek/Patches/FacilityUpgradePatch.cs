using HarmonyLib;
using Upgradeables;

namespace Parsek.Patches
{
    /// <summary>
    /// Harmony prefix on UpgradeableFacility.SetLevel. SetLevel does NOT deduct funds
    /// itself (the stock deduction lives in SpaceCenterBuilding.UpgradeFacility, which is
    /// gated pre-deduction by <see cref="FacilityUpgradeSpendPatch"/>), so this prefix is
    /// a non-destructive backstop for direct SetLevel callers and blocks already-committed
    /// facility upgrades only.
    /// </summary>
    [HarmonyPatch(typeof(UpgradeableFacility), nameof(UpgradeableFacility.SetLevel))]
    internal static class FacilityUpgradePatch
    {
        static bool Prefix(UpgradeableFacility __instance, int lvl)
        {
            if (__instance == null) return true;

            // Only block upgrades (level increases), not downgrades or resets
            int currentLevel = __instance.FacilityLevel;
            if (lvl <= currentLevel)
            {
                ParsekLog.Verbose("FacilityUpgradePatch",
                    $"Allowing facility level change: '{__instance.id}' level {currentLevel} -> {lvl} (not an upgrade)");
                return true;
            }

            return !TryBlockFacilityUpgrade(__instance);
        }

        /// <summary>
        /// Shared facility-upgrade block decision used by both the pre-deduction
        /// <see cref="FacilityUpgradeSpendPatch"/> (SpaceCenterBuilding.UpgradeFacility)
        /// and the post-deduction backstop here (UpgradeableFacility.SetLevel). Returns
        /// true when the upgrade must be BLOCKED (and emits the log + blocked dialog as a
        /// side effect); false to allow. Funds affordability is intentionally NOT checked
        /// (stock's own CurrencyModifierQuery enforces it, and SetLevel does not receive
        /// the cost); this only blocks a facility a committed future row still upgrades.
        /// The block lifts once the clock passes the last committed upgrade of that
        /// facility, so a later upgrade the committed timeline never made is allowed.
        /// </summary>
        internal static bool TryBlockFacilityUpgrade(UpgradeableFacility facility)
        {
            if (facility == null) return false;

            string facilityId = facility.id;
            if (string.IsNullOrEmpty(facilityId)) return false;

            if (GameStateRecorder.IsReplayingActions)
            {
                ParsekLog.Verbose("FacilityUpgradePatch",
                    $"Bypassing block for '{facilityId}' - action replay in progress");
                return false;
            }

            var index = CommittedFutureIndexCache.Current;
            double nowUT = CommittedFutureIndexCache.CurrentUT();
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            if (!StockUiReservationPredicates.IsFacilityUpgradeBlocked(index, facilityId, nowUT))
            {
                ParsekLog.Verbose("FacilityUpgradePatch",
                    $"Allowing facility upgrade: '{facilityId}' - no committed future upgrade " +
                    $"(nowUT={nowUT.ToString("F0", ic)})");
                return false;
            }

            var future = index.FutureEntries(CommittedFutureKind.FacilityUpgrade, facilityId, nowUT);
            var last = future[future.Count - 1];
            ParsekLog.Info("FacilityUpgradePatch",
                $"Blocking facility upgrade: '{facilityId}' - {future.Count.ToString(ic)} committed future " +
                $"upgrade(s), last ut={last.UT.ToString("F0", ic)} toLevel={last.FacilityToLevel.ToString(ic)} " +
                $"nowUT={nowUT.ToString("F0", ic)}");

            var text = StockUiReservationPredicates.ExplainFacilityUpgrade(
                index, facilityId, nowUT, ReservationExplanation.DefaultDateFormatter);
            CommittedActionDialog.ShowBlocked(
                "Cannot upgrade \"" + FacilityDisplayNames.ResolveBuildingDisplayName(facilityId) + "\"",
                text.Body,
                "");

            return true;
        }
    }
}
