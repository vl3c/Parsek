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
                    $"Allowing facility level change: '{__instance.id}' level {currentLevel} → {lvl} (not an upgrade)");
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
        /// the cost) — this only blocks upgrades already committed on the timeline.
        /// </summary>
        internal static bool TryBlockFacilityUpgrade(UpgradeableFacility facility)
        {
            if (facility == null) return false;

            string facilityId = facility.id;
            if (string.IsNullOrEmpty(facilityId)) return false;

            if (!(ParsekSettings.Current?.blockCommittedActions ?? true))
            {
                ParsekLog.Verbose("FacilityUpgradePatch",
                    "feature disabled by ParsekSettings");
                return false;
            }

            if (GameStateRecorder.IsReplayingActions)
            {
                ParsekLog.Verbose("FacilityUpgradePatch",
                    $"Bypassing block for '{facilityId}' — action replay in progress");
                return false;
            }

            var committedFacilities = MilestoneStore.GetCommittedFacilityUpgrades();
            if (!committedFacilities.Contains(facilityId))
            {
                ParsekLog.Verbose("FacilityUpgradePatch",
                    $"Allowing facility upgrade: '{facilityId}' — not in committed set ({committedFacilities.Count} committed)");
                return false;
            }

            var ev = MilestoneStore.FindCommittedEvent(
                GameStateEventType.FacilityUpgraded, facilityId);

            string utStr = ev.HasValue
                ? " at UT " + ev.Value.ut.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
                : "";

            ParsekLog.Info("FacilityUpgradePatch",
                $"Blocking facility upgrade: '{facilityId}' — already committed{utStr}");

            CommittedActionDialog.ShowBlocked(
                "Cannot upgrade \"" + facilityId + "\"",
                "This facility upgrade is already committed on your timeline" + utStr + ".",
                "");

            return true;
        }
    }
}
