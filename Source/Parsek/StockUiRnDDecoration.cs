using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// R&amp;D tech tree annotation on stock mechanisms (docs/dev/research/
    /// stock-ui-reservation-overlays-2026-09-25.md, section 5): a node a committed future
    /// researches gets a tinted stock icon (mark), the explanation appended to its stock
    /// hover tooltip and to the side panel's stock description (why), and a
    /// non-interactable Research button (block at the control). The mark and the block
    /// read the same predicate the <c>TechResearchPatch</c> backstop reads
    /// (<see cref="StockUiReservationPredicates.IsTechResearchBlocked"/>), through
    /// <see cref="StockUiDecorationQuery.ForRnD"/>.
    /// </summary>
    internal static class StockUiRnDDecoration
    {
        private const string Tag = "StockUiOverlay";

        /// <summary>The mark tint of a node's stock icon (RGB; the stock alpha is kept).</summary>
        internal static readonly Color MarkTint = new Color(1.0f, 0.84f, 0.22f, 1.0f);

        /// <summary>Rich-text colour of the appended explanation (stock prints its own
        /// research refusals in orange).</summary>
        internal const string ReasonColorHex = "#f97306";

        private const float TintTolerance = 0.01f;

        /// <summary>
        /// The icon colour a node should carry after stock's <c>SetButtonState</c> ran.
        /// Marked: the mark tint at the stock alpha. Unmarked: stock's colour, except that a
        /// FADED node keeps the previous RGB (stock only rewrites its alpha), so a node whose
        /// mark cleared still carries the tint and is reset to white at the stock alpha.
        /// Returns false when the colour is already right.
        /// </summary>
        internal static bool TryComputeIconColor(bool marked, Color current, out Color next)
        {
            if (marked)
            {
                next = new Color(MarkTint.r, MarkTint.g, MarkTint.b, current.a);
                return !SameRgb(current, next);
            }
            if (SameRgb(current, MarkTint))
            {
                next = new Color(1f, 1f, 1f, current.a);
                return true;
            }
            next = current;
            return false;
        }

        internal static bool SameRgb(Color a, Color b)
        {
            return Math.Abs(a.r - b.r) <= TintTolerance
                && Math.Abs(a.g - b.g) <= TintTolerance
                && Math.Abs(a.b - b.b) <= TintTolerance;
        }

        /// <summary>
        /// Whether the side panel's Research button is disabled. The same
        /// <c>actionButton</c> is the "purchase all parts" button on a researched node and
        /// is hidden on a FADED one (<c>RDController.UpdatePanel</c>), so only the research
        /// state is touched.
        /// </summary>
        internal static bool ShouldDisableResearch(bool blocked, bool isResearched, bool isFaded)
        {
            return blocked && !isResearched && !isFaded;
        }

        /// <summary>
        /// Appends the explanation to a stock caption on its own line, in the stock reason
        /// colour. Idempotent: a caption that already carries it is returned unchanged.
        /// </summary>
        internal static string AppendReason(string stockText, string why)
        {
            if (string.IsNullOrEmpty(why)) return stockText;
            string line = "<color=" + ReasonColorHex + ">" + why + "</color>";
            if (string.IsNullOrEmpty(stockText)) return line;
            if (stockText.Contains(line)) return stockText;
            return stockText + "\n" + line;
        }

        // ---------------- live applicators (Harmony postfix bodies) ----------------

        private static string TechIdOf(RDNode node)
        {
            return node != null && node.tech != null ? node.tech.techID : null;
        }

        /// <summary><c>RDNode.UpdateGraphics</c> postfix: tint or un-tint the stock icon.</summary>
        internal static void ApplyNodeMark(RDNode node)
        {
            if (node == null || !node.treeNode || node.graphics == null) return;
            string techId = TechIdOf(node);
            if (string.IsNullOrEmpty(techId)) return;
            var d = StockUiLiveSnapshot.Current.Tech(techId);
            Color current = node.graphics.GetIconColor();
            Color next;
            if (TryComputeIconColor(d.Marked, current, out next))
            {
                node.graphics.SetIconColor(next);
                ParsekLog.VerboseRateLimited(Tag, "rnd-tint-" + techId,
                    "R&D node " + techId + (d.Marked ? " tinted (committed future research)" : " tint cleared")
                    + " state=" + node.state);
            }
        }

        /// <summary><c>RDNode.GetTooltipCaption</c> postfix: append the explanation.</summary>
        internal static string AppendNodeTooltip(RDNode node, string caption)
        {
            if (node == null || !node.treeNode) return caption;
            string techId = TechIdOf(node);
            if (string.IsNullOrEmpty(techId)) return caption;
            var d = StockUiLiveSnapshot.Current.Tech(techId);
            return d.Marked ? AppendReason(caption, d.Why) : caption;
        }

        /// <summary>
        /// <c>RDController.UpdatePanel</c> postfix: stock re-enables Research every time a
        /// node is shown, so the block is re-applied after it. Returns true when Parsek
        /// disabled the button (the caller greys it, <see cref="SyncActionButtonGreyed"/>).
        /// </summary>
        internal static bool ApplyPanelBlock(RDController controller)
        {
            if (controller == null || controller.actionButton == null) return false;
            RDNode node = controller.node_selected;
            string techId = TechIdOf(node);
            if (string.IsNullOrEmpty(techId)) return false;
            var d = StockUiLiveSnapshot.Current.Tech(techId);
            bool faded = node.state == RDNode.State.FADED;
            if (!ShouldDisableResearch(d.Blocked, node.IsResearched, faded))
                return false;
            if (GameStateRecorder.IsReplayingActions)
            {
                ParsekLog.Verbose(Tag, "R&D Research button block bypassed for " + techId + " - action replay in progress");
                return false;
            }
            controller.actionButton.Enable(false);
            ParsekLog.InfoRateLimited(Tag, "rnd-research-disabled-" + techId,
                "R&D Research button disabled for " + techId + " - committed future research ut="
                + d.UT.ToString("F0", CultureInfo.InvariantCulture) + " why=\"" + d.Why + "\"");
            return true;
        }

        /// <summary>
        /// After every <c>RDController.UpdatePanel</c>: grey the side panel's action button
        /// exactly when Parsek disabled it for the node on show (Research or purchase-all),
        /// else give back the stock look Parsek saved. The button is a <c>UIStateButton</c>
        /// whose research / purchase states draw no distinct disabled look, so a
        /// Parsek-disabled button looked live without this.
        /// </summary>
        internal static void SyncActionButtonGreyed(RDController controller, bool disabledByParsek)
        {
            if (controller == null || controller.actionButton == null) return;
            StockUiGreyedButton.Sync(controller.actionButton.Button, disabledByParsek, "R&D actionButton");
        }

        /// <summary>
        /// <c>RDController.ShowNodePanel</c> postfix: stock rewrites the side panel's
        /// description on every show, then the explanation is appended beside the disabled
        /// Research button.
        /// </summary>
        internal static void AppendPanelDescription(RDController controller, RDNode node)
        {
            if (controller == null || node == null) return;
            string techId = TechIdOf(node);
            if (string.IsNullOrEmpty(techId)) return;
            var d = StockUiLiveSnapshot.Current.Tech(techId);
            if (!d.Marked) return;
            object description = StockUiText.LabelField(controller, typeof(RDController), "node_description");
            string text = StockUiText.Get(description);
            if (text == null) return;
            StockUiText.Set(description, AppendReason(text, d.Why));
        }

        /// <summary>
        /// One pass log for the open tree: <c>decorate screen=RnD tab=Tree items=N marked=M
        /// blocked=B</c> plus one Verbose line per marked node. Called after
        /// <c>RDTechTree.RefreshUI</c> and once after the tree spawns, never per node.
        /// </summary>
        internal static void LogPass(RDController controller, string reason)
        {
            if (controller == null || controller.nodes == null) return;
            var ids = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < controller.nodes.Count; i++)
            {
                string id = TechIdOf(controller.nodes[i]);
                if (!string.IsNullOrEmpty(id) && seen.Add(id)) ids.Add(id);
            }
            var snapshot = StockUiLiveSnapshot.Current;
            var decorations = StockUiDecorationQuery.ForRnD(snapshot.Index, snapshot.UT, ids,
                ReservationExplanation.DefaultDateFormatter);
            ParsekLog.Verbose(Tag, "R&D decoration pass (" + (reason ?? "refresh") + ")");
            StockUiDecorationQuery.LogPass(StockUiScreen.RnD, new[] { StockUiDecorationQuery.RnDTab }, decorations);
        }
    }
}
