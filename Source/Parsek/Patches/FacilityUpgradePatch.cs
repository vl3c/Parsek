using HarmonyLib;
using Upgradeables;

namespace Parsek.Patches
{
    /// <summary>
    /// Harmony prefix on UpgradeableFacility.SetLevel to block upgrading
    /// facilities that are already committed in unreplayed milestones.
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

            string facilityId = __instance.id;
            if (string.IsNullOrEmpty(facilityId)) return true;

            var committedFacilities = MilestoneStore.GetCommittedFacilityUpgrades();
            if (!committedFacilities.Contains(facilityId))
            {
                ParsekLog.Verbose("FacilityUpgradePatch",
                    $"Allowing facility upgrade: '{facilityId}' level {currentLevel} → {lvl} — not in committed set ({committedFacilities.Count} committed)");
                return true;
            }

            var ev = MilestoneStore.FindCommittedEvent(
                GameStateEventType.FacilityUpgraded, facilityId);

            string utStr = ev.HasValue
                ? " at UT " + ev.Value.ut.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
                : "";

            ParsekLog.Info("FacilityUpgradePatch",
                $"Blocking facility upgrade: '{facilityId}' level {currentLevel} → {lvl} — already committed{utStr}");

            CommittedActionDialog.ShowBlocked(
                "Cannot upgrade \"" + facilityId + "\"",
                "This facility upgrade is already committed on your timeline" + utStr + ".",
                "");

            return false;
        }
    }
}
