using System.Collections;
using System.Reflection;
using HarmonyLib;
using Parsek.Patches;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// The KSC facility menu's "Rebuild lvl N" click block (<see cref="FacilityRebuildBlock"/>,
    /// owner ruling 2026-10-07) and its mark, live. The stock button shows only on a destroyed
    /// facility above level 1 with Left Ctrl held when the menu opens, which no cell can arrange
    /// without breaking a building, so this cell drives the two halves of the pairing rule
    /// directly: the decoration the menu would apply to a shown Rebuild, and the click itself,
    /// through the private <c>SpaceCenterBuilding.DowngradeFacility(bool)</c> the button reaches,
    /// with Parsek's Harmony prefix installed. Both must read the same predicate and carry the
    /// same text.
    ///
    /// <para>SAFETY: SPACECENTER batches restore <c>persistent.sfs</c> on disk only, so the cell
    /// must change no career state. It clicks Rebuild on a facility at stock level 0, where stock's
    /// own method returns before any debit, repair or level change, so even a missing block
    /// changes nothing; the refusal dialog is caught by
    /// <see cref="CommittedActionDialog.TestHookForTesting"/>, and the committed future upgrade is
    /// an in-memory ledger row removed in <c>finally</c>.</para>
    /// </summary>
    public partial class FlightIntegrationTests
    {
        [InGameTest(Category = "StockUiOverlay", Scene = GameScenes.SPACECENTER,
            Description = "Stock-UI pairing, facility Rebuild: with a committed future upgrade of a level-0 facility the Rebuild decoration is blocked with the explanation, a shown Rebuild is greyed with that reason and a hidden one is left to stock, and the click through stock's DowngradeFacility is refused by the Harmony prefix with the same text in the blocked dialog, with no level or funds change; the block lifts when the row goes.")]
        public IEnumerator FacilityRebuildRefusedWithTheMenusReason()
        {
            yield return WaitForLoadedScene(GameScenes.SPACECENTER, 15f);
            yield return WaitForStockUiOverlayController(5f);
            if (HighLogic.CurrentGame == null)
            {
                InGameAssert.Skip("HighLogic.CurrentGame is null");
                yield break;
            }
            if (HighLogic.CurrentGame.Mode == Game.Modes.SANDBOX || HighLogic.CurrentGame.Mode == Game.Modes.MISSION)
            {
                InGameAssert.Skip($"The facility Rebuild block needs a career or science game (mode={HighLogic.CurrentGame.Mode})");
                yield break;
            }
            if (!TryPickLevelZeroFacilityBuilding(out SpaceCenterBuilding target, out string pickReason))
            {
                InGameAssert.Skip(pickReason);
                yield break;
            }

            MethodInfo downgrade = AccessTools.Method(typeof(SpaceCenterBuilding), "DowngradeFacility",
                new[] { typeof(bool) });
            InGameAssert.IsNotNull(downgrade, "SpaceCenterBuilding.DowngradeFacility(bool) should resolve");
            HarmonyLib.Patches patchInfo = Harmony.GetPatchInfo(downgrade);
            InGameAssert.IsTrue(patchInfo != null && patchInfo.Owners != null
                    && patchInfo.Owners.Contains("com.parsek.mod"),
                "Parsek's FacilityDowngradeSpendPatch should be installed on DowngradeFacility(bool)");

            string facilityId = target.Facility.id;
            int levelBefore = target.Facility.FacilityLevel;
            int toLevel = levelBefore + 2;
            Recording recording = null;
            string recordingId = null;
            System.Action<string, string, string> priorHook = CommittedActionDialog.TestHookForTesting;
            int dialogs = 0;
            string dialogTitle = null;
            string dialogReason = null;
            try
            {
                recordingId = "stockui-rebuild-" + System.Guid.NewGuid().ToString("N");
                recording = AddCommittedFacilityUpgradeFixture(recordingId, facilityId, toLevel);
                NotifyTimelineDataChangedForOverlayTest();

                // ---- the mark: what the menu applies to a Rebuild button ----
                var buildings = FacilityRepairCapturePatchHelpers.ReadBuildings(target);
                double now = CommittedFutureIndexCache.CurrentUT();
                StockUiDecoration d = StockUiDecorationQuery.ForFacilityMenuRebuild(
                    CommittedFutureIndexCache.Current, now, facilityId, buildings, false,
                    ReservationExplanation.DefaultDateFormatter);
                InGameAssert.IsTrue(d.Blocked, $"The Rebuild decoration of '{facilityId}' should be blocked by the committed upgrade");
                InGameAssert.AreEqual(StockUiDecorationKind.FacilityRebuild, d.Kind,
                    "The Rebuild decoration should carry the FacilityRebuild kind");
                InGameAssert.IsFalse(string.IsNullOrEmpty(d.Why), "The Rebuild decoration should carry an explanation");
                InGameAssert.IsTrue(d.Why.Contains(ReservationExplanation.BlockedByTimeline),
                    $"The explanation should say the timeline holds the facility; got '{d.Why}'");
                var shown = StockUiFacilityDecoration.DecideRebuild(d, true);
                InGameAssert.IsTrue(shown.DisableUpgrade, "A shown Rebuild should be greyed while the block holds");
                InGameAssert.AreEqual(d.Why, shown.Reason, "The greyed Rebuild should carry the decoration's text");
                var hidden = StockUiFacilityDecoration.DecideRebuild(d, false);
                InGameAssert.IsFalse(hidden.DisableUpgrade, "A hidden Rebuild should be left to stock");

                // ---- the click: stock's own method, through the installed prefix ----
                CommittedActionDialog.TestHookForTesting = (action, reason, detail) =>
                {
                    dialogs++;
                    dialogTitle = action;
                    dialogReason = reason;
                };
                double fundsBefore = Funding.Instance != null ? Funding.Instance.Funds : double.NaN;
                downgrade.Invoke(target, new object[] { true });
                CommittedActionDialog.TestHookForTesting = priorHook;

                InGameAssert.AreEqual(1, dialogs, "The refused Rebuild should open exactly one blocked dialog");
                InGameAssert.AreEqual(FacilityRebuildBlock.BlockedDialogTitle(facilityId), dialogTitle,
                    "The blocked dialog should name the building");
                InGameAssert.AreEqual(d.Why, dialogReason,
                    "The blocked dialog and the greyed Rebuild should carry the same text (the pairing rule)");
                InGameAssert.AreEqual(levelBefore, target.Facility.FacilityLevel,
                    $"A refused Rebuild should leave '{facilityId}' at level {levelBefore}");
                if (Funding.Instance != null)
                    InGameAssert.AreEqual(fundsBefore, Funding.Instance.Funds,
                        "A refused Rebuild should charge nothing");
                ParsekLog.Info("TestRunner", $"FacilityRebuild cell: '{facilityId}' refused with the menu's reason, " +
                    $"level {levelBefore} kept");

                // ---- the block lifts with the row ----
                RemoveCommittedOverlayFixture(recordingId, recording);
                recordingId = null;
                recording = null;
                NotifyTimelineDataChangedForOverlayTest();
                yield return null;
                InGameAssert.IsFalse(StockUiReservationPredicates.IsFacilityRebuildBlocked(
                        CommittedFutureIndexCache.Current, facilityId, buildings, CommittedFutureIndexCache.CurrentUT()),
                    $"The Rebuild of '{facilityId}' should be allowed again once the committed row is gone");
            }
            finally
            {
                CommittedActionDialog.TestHookForTesting = priorHook;
                RemoveCommittedOverlayFixture(recordingId, recording);
            }
        }

        /// <summary>
        /// A building whose upgradeable facility sits at stock level 0 (where stock's
        /// DowngradeFacility returns before any debit or change) with no committed level change
        /// or covering repair yet.
        /// </summary>
        private static bool TryPickLevelZeroFacilityBuilding(out SpaceCenterBuilding target, out string reason)
        {
            target = null;
            var index = CommittedFutureIndexCache.Current;
            double now = CommittedFutureIndexCache.CurrentUT();
            int buildings = 0, withFacility = 0, levelZero = 0, alreadyBlocked = 0;
            foreach (var b in Object.FindObjectsOfType<SpaceCenterBuilding>())
            {
                buildings++;
                if (b == null || b.Facility == null || string.IsNullOrEmpty(b.Facility.id)) continue;
                withFacility++;
                if (b.Facility.FacilityLevel != 0 || b.Facility.MaxLevel < 2) continue;
                levelZero++;
                if (StockUiReservationPredicates.IsFacilityRebuildBlocked(index, b.Facility.id,
                        FacilityRepairCapturePatchHelpers.ReadBuildings(b), now))
                {
                    alreadyBlocked++;
                    continue;
                }
                target = b;
                reason = null;
                return true;
            }
            reason = "needs a KSC facility at level 0 with no committed level change (buildings="
                + buildings + ", withFacility=" + withFacility + ", levelZero=" + levelZero
                + ", alreadyBlocked=" + alreadyBlocked + ")";
            return false;
        }
    }
}
