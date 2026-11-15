using System;
using System.Globalization;
using KSP.UI.Screens;
using KSP.UI.Screens.Editor;

namespace Parsek
{
    /// <summary>
    /// The block half of the part-upgrade reservation (todo PART-UPGRADE-PURCHASE-UNRESERVED),
    /// the upgrade sibling of <see cref="StockUiPartPurchase"/>. Stock sells a part upgrade
    /// only through the part list tooltip (editor and R&amp;D):
    /// <c>PartListTooltipController.onPurchaseUpgrade</c> -> <c>onPurchaseUpgradeProceed</c>
    /// -> <c>PartUpgradeHandler.SetUnlocked</c> + <c>GameEvents.OnPartUpgradePurchased</c>,
    /// on which stock's Funding charges the upgrade's <c>entryCost</c>. Two refusals, one
    /// decision: an upgrade the committed timeline buys later (the walk would charge it
    /// twice), and an upgrade whose price would dig into what later costs and open contract
    /// penalties need (committed-value rule 3). The tooltip greys its purchase button and
    /// says why; the backstop on <c>onPurchaseUpgrade</c> refuses with the same text. The
    /// state half is <c>KspStatePatcher.PatchUnlockedUpgrades</c>.
    /// </summary>
    internal static class StockUiPartUpgradePurchase
    {
        internal const string Tag = "PartPurchasePatch";

        /// <summary>The decision for one upgrade.</summary>
        internal struct Decision
        {
            internal string UpgradeName;
            internal bool Blocked;
            /// <summary>The explanation body, or null when not blocked.</summary>
            internal string Why;
            internal string Title;
            /// <summary>The committed purchase's UT, or NaN (also for a reserve refusal).</summary>
            internal double UT;
        }

        /// <summary>Pure: the committed-future decision for one upgrade over the index.</summary>
        internal static Decision Decide(
            CommittedFutureIndex index, string upgradeName, double currentUT,
            bool bypassEntryPurchase, bool unlockedInStock, Func<double, string> formatDate)
        {
            var d = new Decision { UpgradeName = upgradeName, UT = double.NaN };
            if (!StockUiReservationPredicates.IsPartUpgradePurchaseBlocked(
                    index, upgradeName, currentUT, bypassEntryPurchase, unlockedInStock))
                return d;
            var entry = StockUiReservationPredicates.CommittedPartUpgradePurchaseAfter(index, upgradeName, currentUT);
            var text = ReservationExplanation.PartUpgradePurchase(entry, formatDate);
            d.Blocked = true;
            d.Why = text.Body;
            d.Title = text.Title;
            d.UT = entry != null ? entry.UT : double.NaN;
            return d;
        }

        /// <summary>
        /// Pure: the click-reservation layer (committed-value rule 3), as for a part: an
        /// upgrade no committed purchase blocks is refused when buying it now would spend funds
        /// a later cost, or an open contract's penalty while such a cost lies ahead, needs.
        /// </summary>
        internal static Decision LayerReserve(Decision d, PurchaseRefusal r)
        {
            if (d.Blocked || !r.Refused) return d;
            d.Blocked = true;
            d.Why = ClickReservationGate.PurchaseReason(r);
            d.Title = ReservationExplanation.ClickReserve().Title;
            d.UT = double.NaN;
            return d;
        }

        /// <summary>
        /// Pure: whether the reserve layer applies at all. Stock offers the purchase only when
        /// the upgrade is still locked and bypass-entry-purchase is off, and charges nothing
        /// for a price that is not positive.
        /// </summary>
        internal static bool NeedsReserveCheck(bool bypassEntryPurchase, bool unlockedInStock, float chargedCost)
        {
            return !bypassEntryPurchase && !unlockedInStock && chargedCost > 0f;
        }

        /// <summary>Test seam for the stock "already unlocked" read. Null in production.</summary>
        internal static Func<string, bool> UnlockedInStockProviderForTesting;

        /// <summary>Whether stock shows the upgrade unlocked.</summary>
        internal static bool IsUnlockedInStock(string upgradeName)
        {
            var provider = UnlockedInStockProviderForTesting;
            if (provider != null) return provider(upgradeName);
            return ReadUnlockedInStock(upgradeName);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool ReadUnlockedInStock(string upgradeName)
        {
            return PartUpgradeManager.Handler.IsUnlocked(upgradeName);
        }

        /// <summary>The live decision: the current index, clock, difficulty, stock state and pools.</summary>
        internal static Decision DecideLive(PartUpgradeHandler.Upgrade upgrade)
        {
            if (upgrade == null || string.IsNullOrEmpty(upgrade.name))
                return new Decision { UT = double.NaN };
            bool bypass = GameStateRecorder.IsBypassEntryPurchaseAfterResearch();
            bool unlocked = IsUnlockedInStock(upgrade.name);
            var d = Decide(
                CommittedFutureIndexCache.Current,
                upgrade.name,
                CommittedFutureIndexCache.CurrentUT(),
                bypass,
                unlocked,
                ReservationExplanation.DefaultDateFormatter);
            float charged = GameStateRecorder.ComputePartUpgradeChargedCost(upgrade.entryCost, bypass);
            if (d.Blocked || !NeedsReserveCheck(bypass, unlocked, charged))
                return d;
            return LayerReserve(d, ClickReservationGate.EvaluatePurchaseNow(StockPool.Funds, charged,
                "part upgrade " + upgrade.name));
        }

        private static string TitleOf(PartUpgradeHandler.Upgrade upgrade)
        {
            if (upgrade == null) return "(unknown upgrade)";
            return string.IsNullOrEmpty(upgrade.title) ? upgrade.name : upgrade.title;
        }

        /// <summary>
        /// The click backstop: true when buying <paramref name="upgrade"/> now must be refused,
        /// with the log line and (unless <paramref name="showDialog"/> is false) the blocked
        /// dialog carrying the same text as the greyed tooltip. Bypassed during action replay.
        /// </summary>
        internal static bool TryBlockPurchase(PartUpgradeHandler.Upgrade upgrade, string source, bool showDialog)
        {
            if (upgrade == null) return false;
            if (GameStateRecorder.IsReplayingActions)
            {
                ParsekLog.Verbose(Tag, "Bypassing part upgrade purchase block for '" + upgrade.name + "' (" + source
                    + ") - action replay in progress");
                return false;
            }

            var d = DecideLive(upgrade);
            if (!d.Blocked)
            {
                ParsekLog.Verbose(Tag, "Part upgrade purchase allowed: '" + upgrade.name + "' source=" + source);
                return false;
            }

            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Info(Tag,
                "Blocking part upgrade purchase: '" + upgrade.name + "' (" + TitleOf(upgrade) + ") source=" + source
                + (double.IsNaN(d.UT) ? " - reserved for later costs" : " - committed future purchase ut=" + d.UT.ToString("F0", ic))
                + " nowUT=" + CommittedFutureIndexCache.CurrentUT().ToString("F0", ic)
                + " dialog=" + (showDialog ? "yes" : "no"));
            if (showDialog)
                CommittedActionDialog.ShowBlocked("Cannot purchase \"" + TitleOf(upgrade) + "\"", d.Why, null);
            return true;
        }

        // ---------------- the tooltip purchase button ----------------

        /// <summary>
        /// <c>PartListTooltip.Setup(AvailablePart, Upgrade, ...)</c> postfix: disable both
        /// purchase buttons for a blocked upgrade, give back buttons a reused tooltip carries
        /// disabled otherwise. Shares the disabled-tooltip set with the part path, since one
        /// tooltip instance serves both.
        /// </summary>
        internal static void ApplyTooltipButtons(PartListTooltip tooltip, PartUpgradeHandler.Upgrade upgrade)
        {
            if (tooltip == null) return;
            var d = DecideLive(upgrade);
            if (d.Blocked && GameStateRecorder.IsReplayingActions)
                return;
            int id = tooltip.GetInstanceID();
            bool? write = StockUiPartPurchase.PurchaseButtonsInteractable(d.Blocked, StockUiPartPurchase.IsTooltipDisabled(id));
            if (!write.HasValue) return;
            StockUiPartPurchase.SetTooltipButtonsInteractable(tooltip, write.Value);
            if (write.Value)
            {
                ParsekLog.VerboseRateLimited(Tag, "upgrade-tooltip-restored",
                    "Upgrade tooltip purchase buttons given back (" + (upgrade != null ? upgrade.name : "(none)") + ")");
            }
            else
            {
                ParsekLog.InfoRateLimited(Tag, "upgrade-tooltip-greyed-" + d.UpgradeName,
                    "Upgrade tooltip purchase button disabled for '" + d.UpgradeName + "' - "
                    + (double.IsNaN(d.UT) ? "reserved for later costs" : "committed future purchase ut="
                        + d.UT.ToString("F0", CultureInfo.InvariantCulture)) + " why=\"" + d.Why + "\"");
            }
        }

        /// <summary>
        /// <c>PartListTooltipController.CreateTooltip</c> postfix for an upgrade icon: the
        /// reason goes in the stock <c>textGreyoutMessage</c> label, appended to any stock text.
        /// </summary>
        internal static void ApplyTooltipReason(PartListTooltip tooltip, EditorPartIcon partIcon)
        {
            if (tooltip == null || partIcon == null || partIcon.isPart || partIcon.isEmptySlot) return;
            if (partIcon.upgrade == null) return;
            var d = DecideLive(partIcon.upgrade);
            if (!d.Blocked) return;
            StockUiPartPurchase.AppendTooltipGreyoutReason(tooltip, d.Why, "upgrade tooltip greyout message");
        }

        internal static void ResetForTesting()
        {
            UnlockedInStockProviderForTesting = null;
        }
    }
}
