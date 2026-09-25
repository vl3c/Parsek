using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using KSP.UI.Screens;
using KSP.UI.Screens.Editor;
using UnityEngine;
using UnityEngine.UI;

namespace Parsek
{
    /// <summary>
    /// The block half of the part-purchase reservation (P1, docs/dev/research/
    /// stock-ui-reservation-overlays-2026-09-25.md, section 10 step 9): a part the
    /// committed timeline buys later cannot be bought now, because the walk would charge
    /// its entry cost twice and the ledger is never deduped (owner ruling D4). The stock
    /// part tooltip greys its purchase button and says why; the editor / R&amp;D purchase
    /// paths refuse with the same text. Every decision reads
    /// <see cref="StockUiReservationPredicates.IsPartPurchaseBlocked"/> over the one
    /// committed-future index, so the greyed set and the refused set cannot disagree.
    /// The state half is <c>KspStatePatcher.PatchPurchasedParts</c>.
    /// </summary>
    internal static class StockUiPartPurchase
    {
        internal const string Tag = "PartPurchasePatch";

        /// <summary>The decision for one part.</summary>
        internal struct Decision
        {
            internal string PartName;
            internal bool Blocked;
            /// <summary>The explanation body, or null when not blocked.</summary>
            internal string Why;
            internal string Title;
            /// <summary>The committed purchase's UT, or NaN.</summary>
            internal double UT;
        }

        /// <summary>Pure: the decision for one part over the index.</summary>
        internal static Decision Decide(
            CommittedFutureIndex index, string partName, double currentUT,
            bool bypassEntryPurchase, bool purchasedInStock, Func<double, string> formatDate)
        {
            var d = new Decision { PartName = partName, UT = double.NaN };
            if (!StockUiReservationPredicates.IsPartPurchaseBlocked(
                    index, partName, currentUT, bypassEntryPurchase, purchasedInStock))
                return d;
            var entry = StockUiReservationPredicates.CommittedPartPurchaseAfter(index, partName, currentUT);
            var text = ReservationExplanation.PartPurchase(entry, formatDate);
            d.Blocked = true;
            d.Why = text.Body;
            d.Title = text.Title;
            d.UT = entry != null ? entry.UT : double.NaN;
            return d;
        }

        /// <summary>Test seam for the stock "already purchased" read. Null in production.</summary>
        internal static Func<AvailablePart, bool> PurchasedInStockProviderForTesting;

        /// <summary>
        /// Whether stock shows the part purchased. <c>ResearchAndDevelopment.PartModelPurchased</c>
        /// answers true when no R&amp;D exists (sandbox), which correctly never blocks.
        /// </summary>
        internal static bool IsPurchasedInStock(AvailablePart ap)
        {
            var provider = PurchasedInStockProviderForTesting;
            if (provider != null) return provider(ap);
            return ResearchAndDevelopment.PartModelPurchased(ap);
        }

        /// <summary>The live decision: the current index, clock, difficulty and stock state.</summary>
        internal static Decision DecideLive(AvailablePart ap)
        {
            if (ap == null || string.IsNullOrEmpty(ap.name))
                return new Decision { UT = double.NaN };
            return Decide(
                CommittedFutureIndexCache.Current,
                ap.name,
                CommittedFutureIndexCache.CurrentUT(),
                GameStateRecorder.IsBypassEntryPurchaseAfterResearch(),
                IsPurchasedInStock(ap),
                ReservationExplanation.DefaultDateFormatter);
        }

        private static string TitleOf(AvailablePart ap)
        {
            if (ap == null) return "(unknown part)";
            return string.IsNullOrEmpty(ap.title) ? ap.name : ap.title;
        }

        /// <summary>
        /// The click backstop: true when buying <paramref name="ap"/> now must be refused,
        /// with the log line and (unless <paramref name="showDialog"/> is false) the blocked
        /// dialog carrying the same text as the greyed tooltip. Bypassed during action replay.
        /// </summary>
        internal static bool TryBlockPurchase(AvailablePart ap, string source, bool showDialog)
        {
            if (ap == null) return false;
            if (GameStateRecorder.IsReplayingActions)
            {
                ParsekLog.Verbose(Tag, "Bypassing part purchase block for '" + ap.name + "' (" + source
                    + ") - action replay in progress");
                return false;
            }

            var d = DecideLive(ap);
            if (!d.Blocked)
                return false;

            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Info(Tag,
                "Blocking part purchase: '" + ap.name + "' (" + TitleOf(ap) + ") source=" + source
                + " - committed future purchase ut=" + d.UT.ToString("F0", ic)
                + " nowUT=" + CommittedFutureIndexCache.CurrentUT().ToString("F0", ic)
                + " dialog=" + (showDialog ? "yes" : "no"));
            if (showDialog)
                CommittedActionDialog.ShowBlocked("Cannot purchase \"" + TitleOf(ap) + "\"", d.Why, null);
            return true;
        }

        // ---------------- the tooltip purchase button ----------------

        // Tooltips whose purchase buttons PARSEK disabled. Stock never writes
        // Button.interactable on these (it toggles between the two buttons with
        // SetActive), so a tooltip reused for another part is given its buttons back only
        // when Parsek was the one that took them.
        private static readonly HashSet<int> disabledTooltips = new HashSet<int>();

        /// <summary>What the tooltip's purchase buttons must be set to, or null to leave them.</summary>
        internal static bool? PurchaseButtonsInteractable(bool blocked, bool disabledByParsek)
        {
            if (blocked) return false;
            return disabledByParsek ? true : (bool?)null;
        }

        /// <summary>
        /// <c>PartListTooltip.Setup(AvailablePart, ...)</c> postfix: disable both purchase
        /// buttons (stock shows one of them, by affordability) for a blocked part; give them
        /// back on a later Setup for an unblocked part.
        /// </summary>
        internal static void ApplyTooltipButtons(PartListTooltip tooltip, AvailablePart ap)
        {
            if (tooltip == null) return;
            var d = DecideLive(ap);
            if (d.Blocked && GameStateRecorder.IsReplayingActions)
                return;
            int id = tooltip.GetInstanceID();
            bool? write = PurchaseButtonsInteractable(d.Blocked, disabledTooltips.Contains(id));
            if (!write.HasValue) return;
            SetInteractable(tooltip.buttonPurchase, write.Value);
            SetInteractable(tooltip.buttonPurchaseRed, write.Value);
            if (write.Value)
            {
                disabledTooltips.Remove(id);
                ParsekLog.VerboseRateLimited(Tag, "part-tooltip-restored",
                    "Part tooltip purchase buttons given back (" + (ap != null ? ap.name : "(none)") + ")");
            }
            else
            {
                disabledTooltips.Add(id);
                ParsekLog.InfoRateLimited(Tag, "part-tooltip-greyed-" + d.PartName,
                    "Part tooltip purchase button disabled for '" + d.PartName + "' - committed future purchase ut="
                    + d.UT.ToString("F0", CultureInfo.InvariantCulture) + " why=\"" + d.Why + "\"");
            }
        }

        /// <summary>
        /// The upgrade overload of <c>PartListTooltip.Setup</c> buys a part UPGRADE, never
        /// an entry purchase, so P1 never blocks it; its postfix only gives back buttons a
        /// reused tooltip may still carry disabled.
        /// </summary>
        internal static void RestoreTooltipButtons(PartListTooltip tooltip)
        {
            if (tooltip == null) return;
            int id = tooltip.GetInstanceID();
            if (!disabledTooltips.Remove(id)) return;
            SetInteractable(tooltip.buttonPurchase, true);
            SetInteractable(tooltip.buttonPurchaseRed, true);
        }

        private static void SetInteractable(Button button, bool interactable)
        {
            if (button != null && button.interactable != interactable)
                button.interactable = interactable;
        }

        /// <summary>
        /// <c>PartListTooltipController.CreateTooltip</c> postfix: the reason goes in the
        /// stock <c>textGreyoutMessage</c> label. <c>CreateTooltip</c> runs <c>Setup</c> and
        /// then, for an icon that is not greyed, enables that label with an empty text
        /// (a greyed icon gets stock's greyout message instead), so the reason is written
        /// here, after both, and appended to any stock message.
        /// </summary>
        internal static void ApplyTooltipReason(PartListTooltip tooltip, EditorPartIcon partIcon)
        {
            if (tooltip == null || partIcon == null || !partIcon.isPart || partIcon.isEmptySlot) return;
            var d = DecideLive(partIcon.partInfo);
            if (!d.Blocked) return;
            object label = StockUiText.LabelField(tooltip, typeof(PartListTooltip), "textGreyoutMessage");
            if (label == null) return;
            var behaviour = label as Behaviour;
            if (behaviour != null && !behaviour.enabled)
                behaviour.enabled = true;
            string existing = StockUiText.Get(label);
            StockUiText.Set(label, StockUiRnDDecoration.AppendReason(existing, d.Why));
        }

        // ---------------- R&D "purchase all parts" ----------------

        private static int purchaseAllDepth;

        /// <summary>True while stock's R&amp;D purchase-all loop runs; the per-part
        /// backstop then skips a blocked part without its own dialog.</summary>
        internal static bool InPurchaseAll => purchaseAllDepth > 0;

        internal static void EnterPurchaseAll() { purchaseAllDepth++; }

        internal static void ExitPurchaseAll() { if (purchaseAllDepth > 0) purchaseAllDepth--; }

        /// <summary>
        /// Whether the purchase-all button is disabled: only when every part still to buy is
        /// blocked, so a click could buy nothing. With some parts free to buy the button
        /// stays live and the blocked ones are skipped with a dialog.
        /// </summary>
        internal static bool ShouldDisablePurchaseAll(int unpurchasedCount, int blockedCount)
        {
            return unpurchasedCount > 0 && blockedCount >= unpurchasedCount;
        }

        /// <summary>The unpurchased assigned parts of an R&amp;D tech, each with its decision.</summary>
        internal static List<KeyValuePair<AvailablePart, Decision>> UnpurchasedDecisions(RDTech tech)
        {
            var result = new List<KeyValuePair<AvailablePart, Decision>>();
            if (tech == null || tech.partsAssigned == null) return result;
            for (int i = 0; i < tech.partsAssigned.Count; i++)
            {
                var ap = tech.partsAssigned[i];
                if (ap == null) continue;
                if (tech.partsPurchased != null && tech.partsPurchased.Contains(ap)) continue;
                result.Add(new KeyValuePair<AvailablePart, Decision>(ap, DecideLive(ap)));
            }
            return result;
        }

        /// <summary>
        /// The purchase-all dialog body: one line per skipped part (its title and the fact),
        /// then the shared rule and the way out. A single skipped part gets the full body.
        /// Pure.
        /// </summary>
        internal static string BuildPurchaseAllSkipReason(IList<KeyValuePair<string, ReservationText>> skipped)
        {
            if (skipped == null || skipped.Count == 0) return "";
            if (skipped.Count == 1)
                return skipped[0].Key + ": " + skipped[0].Value.Body;
            var sb = new StringBuilder();
            for (int i = 0; i < skipped.Count; i++)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(skipped[i].Key).Append(": ").Append(skipped[i].Value.Fact);
            }
            sb.Append('\n').Append(ReservationExplanation.TimelineRule)
              .Append(" They become available on those dates.");
            return sb.ToString();
        }

        /// <summary>
        /// <c>RDController.ActionButtonClick("purchase")</c> prefix: stock buys every
        /// unpurchased part of the node through <c>RDTech.PurchasePart</c>. The blocked
        /// ones are skipped there (<see cref="InPurchaseAll"/>) and named in ONE dialog
        /// here; the rest are bought as stock would.
        /// </summary>
        internal static void BeginPurchaseAll(RDController controller)
        {
            EnterPurchaseAll();
            if (GameStateRecorder.IsReplayingActions) return;
            var node = controller != null ? controller.node_selected : null;
            var decisions = UnpurchasedDecisions(node != null ? node.tech : null);
            var skipped = new List<KeyValuePair<string, ReservationText>>();
            var names = new List<string>();
            var index = CommittedFutureIndexCache.Current;
            double now = CommittedFutureIndexCache.CurrentUT();
            for (int i = 0; i < decisions.Count; i++)
            {
                if (!decisions[i].Value.Blocked) continue;
                var ap = decisions[i].Key;
                names.Add(ap.name);
                skipped.Add(new KeyValuePair<string, ReservationText>(TitleOf(ap),
                    StockUiReservationPredicates.ExplainPartPurchase(index, ap.name, now,
                        ReservationExplanation.DefaultDateFormatter)));
            }
            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Info(Tag,
                "R&D purchase all: node=" + (node != null && node.tech != null ? node.tech.techID : "(none)")
                + " unpurchased=" + decisions.Count.ToString(ic)
                + " skippedBlocked=" + skipped.Count.ToString(ic)
                + (names.Count > 0 ? " skipped=[" + string.Join(", ", names.ToArray()) + "]" : ""));
            if (skipped.Count == 0) return;
            CommittedActionDialog.ShowBlocked(
                skipped.Count == decisions.Count
                    ? "Cannot purchase these parts"
                    : "Some parts were not purchased",
                BuildPurchaseAllSkipReason(skipped),
                null);
        }

        /// <summary>
        /// <c>RDController.UpdatePanel</c> postfix, researched node: disable the
        /// purchase-all button when every part still to buy is blocked.
        /// </summary>
        internal static void ApplyPurchaseAllBlock(RDController controller)
        {
            if (controller == null || controller.actionButton == null) return;
            var node = controller.node_selected;
            if (node == null || !node.IsResearched || node.tech == null) return;
            if (GameStateRecorder.IsReplayingActions) return;
            var decisions = UnpurchasedDecisions(node.tech);
            int blocked = 0;
            for (int i = 0; i < decisions.Count; i++)
                if (decisions[i].Value.Blocked) blocked++;
            if (!ShouldDisablePurchaseAll(decisions.Count, blocked)) return;
            controller.actionButton.Enable(false);
            ParsekLog.InfoRateLimited(Tag, "rnd-purchase-all-disabled-" + node.tech.techID,
                "R&D purchase-all button disabled for " + node.tech.techID + " - all "
                + decisions.Count.ToString(CultureInfo.InvariantCulture)
                + " unpurchased part(s) have a committed future purchase");
        }

        internal static void ResetForTesting()
        {
            disabledTooltips.Clear();
            purchaseAllDepth = 0;
            PurchasedInStockProviderForTesting = null;
        }
    }
}
