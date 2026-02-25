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
        static bool Prefix(UpgradeableFacility __instance, int level)
        {
            if (__instance == null) return true;

            // Only block upgrades (level increases), not downgrades or resets
            int currentLevel = __instance.FacilityLevel;
            if (level <= currentLevel) return true;

            string facilityId = __instance.id;
            if (string.IsNullOrEmpty(facilityId)) return true;

            var committedFacilities = MilestoneStore.GetCommittedFacilityUpgrades();
            if (!committedFacilities.Contains(facilityId))
                return true;

            var ev = MilestoneStore.FindCommittedEvent(
                GameStateEventType.FacilityUpgraded, facilityId);

            string utStr = ev.HasValue
                ? " at UT " + ev.Value.ut.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
                : "";

            CommittedActionDialog.ShowBlocked(
                "Cannot upgrade \"" + facilityId + "\"",
                "This facility upgrade is already committed on your timeline" + utStr + ".",
                "");

            return false;
        }
    }
}
