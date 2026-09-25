using System.Collections;
using System.Collections.Generic;
using KSP.UI.Screens;
using Parsek.Patches;
using UnityEngine;
using UnityEngine.UI;

namespace Parsek.InGameTests
{
    /// <summary>
    /// The KSC facility context menu annotation on stock mechanisms
    /// (docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md, section 10 step 8):
    /// a facility the committed timeline upgrades later gets a non-interactable Upgrade
    /// button with the explanation in a stock tooltip. The cell opens the menu with its
    /// own no-op dismiss callback, so no dismissal can upgrade, repair or demolish
    /// anything: SPACECENTER batches restore <c>persistent.sfs</c> on disk only, so a cell
    /// here must never change career state. The committed upgrade is an in-memory ledger
    /// row removed in <c>finally</c>.
    /// </summary>
    public partial class FlightIntegrationTests
    {
        [InGameTest(Category = "StockUiOverlay", Scene = GameScenes.SPACECENTER,
            Description = "Stock-UI annotation: the KSC facility menu of a facility the committed timeline upgrades later shows a non-interactable Upgrade button with the explanation in a stock tooltip, through a stock refresh; another facility's menu stays stock; Upgrade comes back when the committed row goes.")]
        public IEnumerator FacilityMenuUpgradeDisabledWithReasonAndRestored()
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
                InGameAssert.Skip($"KSC facility upgrade annotation needs a career or science game (mode={HighLogic.CurrentGame.Mode})");
                yield break;
            }
            if (!TryPickUpgradeableFacilityBuildings(out SpaceCenterBuilding target, out SpaceCenterBuilding other,
                    out string pickReason))
            {
                InGameAssert.Skip(pickReason);
                yield break;
            }

            string facilityId = target.Facility.id;
            int toLevel = target.Facility.FacilityLevel + 2;
            Recording recording = null;
            string recordingId = null;
            KSCFacilityContextMenu menu = null;
            KSCFacilityContextMenu otherMenu = null;
            try
            {
                recordingId = "stockui-facility-" + System.Guid.NewGuid().ToString("N");
                recording = AddCommittedFacilityUpgradeFixture(recordingId, facilityId, toLevel);
                NotifyTimelineDataChangedForOverlayTest();

                menu = KSCFacilityContextMenu.Create(target, OnFacilityMenuTestDismiss);
                KSCFacilityContextMenu openMenu = menu;
                // The buttons fill in the menu's Start, a frame after Create.
                yield return WaitUntilTrue(() => FacilityMenuShowsReason(openMenu),
                    $"The '{facilityId}' menu should grey out Upgrade with the committed-upgrade explanation", 5f);

                Button upgrade = StockUiFacilityDecoration.UpgradeButtonOf(menu);
                InGameAssert.IsNotNull(upgrade, "The facility menu's Upgrade button should resolve");
                InGameAssert.IsFalse(upgrade.interactable, $"Upgrade should be non-interactable for '{facilityId}'");
                string shown = FacilityMenuReasonText(menu);
                InGameAssert.IsTrue(shown.Contains("Upgraded to level " + toLevel),
                    $"The reason should name the committed level {toLevel}; got '{shown}'");
                var state = StockUiFacilityDecoration.StateOf(menu);
                if (state != null && state.Tooltip != null && state.Tooltip.prefab != null)
                {
                    InGameAssert.IsTrue(state.Tooltip.enabled, "The stock tooltip on the disabled Upgrade should be enabled");
                    InGameAssert.IsFalse(state.Tooltip.RequireInteractable,
                        "The tooltip must show on a non-interactable button (RequireInteractable=false)");
                    string tipText = state.Tooltip.textString ?? "";
                    InGameAssert.IsTrue(tipText.Replace('\n', ' ').Contains(ReservationExplanation.TimelineRule),
                        $"The stock tooltip should carry the explanation; got '{tipText}'");
                    if (state.TooltipOwned)
                    {
                        // The borrowed prefab has no maximum width: the text itself must wrap.
                        string[] lines = tipText.Split('\n');
                        for (int i = 0; i < lines.Length; i++)
                            InGameAssert.IsTrue(lines[i].Length <= StockUiFacilityDecoration.TooltipLineChars,
                                $"Every tooltip line should fit {StockUiFacilityDecoration.TooltipLineChars} chars; line {i} has {lines[i].Length}: '{lines[i]}'");
                    }
                }
                else
                {
                    ParsekLog.Info("TestRunner",
                        "FacilityMenu cell: no stock tooltip prefab on this install - asserting the description fallback");
                }

                // Stock's own refresh (the structure collapse / repair path) re-enables Upgrade.
                FacilityMenuUpgradeBlockPatch.ResolveTargetMethodForTesting().Invoke(menu, null);
                InGameAssert.IsFalse(upgrade.interactable, "Upgrade should stay disabled through a stock OnFacilityValuesModified");
                InGameAssert.AreEqual(1, CountOccurrences(FacilityMenuReasonText(menu), ReservationExplanation.TimelineRule),
                    "The reason should appear exactly once after a stock refresh");

                // Menus are per facility: another facility's menu carries none of it.
                otherMenu = KSCFacilityContextMenu.Create(other, OnFacilityMenuTestDismiss);
                KSCFacilityContextMenu openOther = otherMenu;
                // Start (and with it the stock button fill) runs on the next frame; the
                // prefab's own label text may already be non-empty, so wait it out first.
                yield return null;
                yield return null;
                yield return WaitUntilTrue(() => FacilityMenuFilled(openOther),
                    $"The '{other.Facility.id}' menu should fill its buttons", 5f);
                Button otherUpgrade = StockUiFacilityDecoration.UpgradeButtonOf(otherMenu);
                InGameAssert.IsTrue(otherUpgrade != null && otherUpgrade.interactable,
                    $"Upgrade on the other facility '{other.Facility.id}' should stay interactable");
                InGameAssert.IsNull(StockUiFacilityDecoration.StateOf(otherMenu),
                    "Parsek should not have touched the other facility's menu");
                InGameAssert.IsFalse(FacilityMenuReasonText(otherMenu).Contains(ReservationExplanation.TimelineRule),
                    "The other facility's menu should carry no explanation");
                DismissFacilityMenuForTest(otherMenu);
                otherMenu = null;

                // The block lifts: the timeline refresh re-runs stock's fill on the open menu.
                RemoveCommittedOverlayFixture(recordingId, recording);
                recordingId = null;
                recording = null;
                NotifyTimelineDataChangedForOverlayTest();
                yield return WaitUntilTrue(
                    () => upgrade.interactable
                        && !FacilityMenuReasonText(openMenu).Contains(ReservationExplanation.TimelineRule),
                    $"Upgrade on '{facilityId}' should come back and the reason clear once the committed row is gone", 5f);
                if (state != null && state.TooltipOwned && state.Tooltip != null)
                    InGameAssert.IsFalse(state.Tooltip.enabled, "Parsek's tooltip should be disabled once the block lifts");
            }
            finally
            {
                RemoveCommittedOverlayFixture(recordingId, recording);
                DismissFacilityMenuForTest(otherMenu);
                DismissFacilityMenuForTest(menu);
            }
            yield return WaitUntilTrue(() => Object.FindObjectOfType<KSCFacilityContextMenu>() == null,
                "The facility menus opened by the cell should be destroyed after dismissal", 5f);
        }

        /// <summary>
        /// Two buildings with distinct upgradeable facilities below their top level that no
        /// committed row upgrades yet: the target the cell reserves, and a control.
        /// </summary>
        private static bool TryPickUpgradeableFacilityBuildings(
            out SpaceCenterBuilding target, out SpaceCenterBuilding other, out string reason)
        {
            target = null;
            other = null;
            var index = CommittedFutureIndexCache.Current;
            double now = Planetarium.GetUniversalTime();
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            int buildings = 0, withFacility = 0, belowMax = 0, alreadyReserved = 0;
            foreach (var b in Object.FindObjectsOfType<SpaceCenterBuilding>())
            {
                buildings++;
                if (b == null || b.Facility == null || string.IsNullOrEmpty(b.Facility.id)) continue;
                withFacility++;
                if (!seen.Add(b.Facility.id)) continue;
                if (b.Facility.FacilityLevel >= b.Facility.MaxLevel) continue;
                belowMax++;
                if (StockUiReservationPredicates.IsFacilityUpgradeBlocked(index, b.Facility.id, now))
                {
                    alreadyReserved++;
                    continue;
                }
                if (target == null) target = b;
                else if (other == null) other = b;
            }
            if (target != null && other != null)
            {
                reason = null;
                return true;
            }
            reason = "needs two KSC facilities below their top level with no committed upgrade (buildings="
                + buildings + ", withFacility=" + withFacility + ", belowMax=" + belowMax
                + ", alreadyReserved=" + alreadyReserved + ")";
            return false;
        }

        private static Recording AddCommittedFacilityUpgradeFixture(string recordingId, string facilityId, int toLevel)
        {
            double now = Planetarium.GetUniversalTime();
            var recording = new Recording
            {
                RecordingId = recordingId,
                VesselName = "Stock UI Facility Menu Test",
                ExplicitStartUT = now - 1.0,
                ExplicitEndUT = now + 7200.0,
                MergeState = MergeState.Immutable
            };
            // Intentional no-tree fixture: the menu only needs a committed ledger row of a
            // recording in the effective set.
            RecordingStore.AddCommittedInternal(recording);
            Ledger.AddAction(new GameAction
            {
                UT = now + 3600.0,
                Type = GameActionType.FacilityUpgrade,
                RecordingId = recordingId,
                FacilityId = facilityId,
                ToLevel = toLevel,
                FacilityCost = 0f
            });
            return recording;
        }

        private static void OnFacilityMenuTestDismiss(KSCFacilityContextMenu.DismissAction action)
        {
            // Deliberately no-op: a stock building's own callback would upgrade, repair or
            // demolish on the matching button, and the cell must not change career state.
            ParsekLog.Verbose("TestRunner", "FacilityMenu cell: menu dismissed with " + action + " (no-op)");
        }

        private static void DismissFacilityMenuForTest(KSCFacilityContextMenu menu)
        {
            if (menu == null) return;
            try
            {
                menu.Dismiss(KSCFacilityContextMenu.DismissAction.None);
            }
            catch (System.Exception ex)
            {
                ParsekLog.Warn("TestRunner", $"FacilityMenu cell: dismiss threw: {ex.Message}");
            }
        }

        private static bool FacilityMenuFilled(KSCFacilityContextMenu menu)
        {
            if (menu == null) return false;
            object level = StockUiText.LabelField(menu, typeof(KSCFacilityContextMenu), "levelFieldText");
            return !string.IsNullOrEmpty(StockUiText.Get(level));
        }

        private static bool FacilityMenuShowsReason(KSCFacilityContextMenu menu)
        {
            if (!FacilityMenuFilled(menu)) return false;
            Button upgrade = StockUiFacilityDecoration.UpgradeButtonOf(menu);
            return upgrade != null && !upgrade.interactable
                && FacilityMenuReasonText(menu).Contains(ReservationExplanation.TimelineRule);
        }

        /// <summary>The explanation text the menu shows: the enabled Upgrade tooltip plus the
        /// description (where the fallback puts it).</summary>
        private static string FacilityMenuReasonText(KSCFacilityContextMenu menu)
        {
            if (menu == null) return "";
            string text = "";
            var state = StockUiFacilityDecoration.StateOf(menu);
            if (state != null && state.Tooltip != null && state.Tooltip.enabled)
                text += (state.Tooltip.textString ?? "").Replace('\n', ' ');
            object description = StockUiText.LabelField(menu, typeof(KSCFacilityContextMenu),
                StockUiFacilityDecoration.DescriptionFieldName);
            return text + "\n" + (StockUiText.Get(description) ?? "");
        }
    }
}
