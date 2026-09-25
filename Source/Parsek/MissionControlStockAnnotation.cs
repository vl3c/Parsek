using System;
using System.Collections.Generic;
using Contracts;

namespace Parsek
{
    /// <summary>
    /// Pure text and decision helpers for the Mission Control stock-control annotations
    /// (docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md, sections 5, 7.2 and 7.3).
    /// An Offered contract a committed future accepts gets: the row's own stock label
    /// extended with a short status, the committed-accept explanation appended to the
    /// stock detail text, and Accept + Decline greyed out. An Active contract a committed
    /// row later completes, fails or cancels gets the same label and detail treatment and
    /// a greyed Cancel. Every one of those reads <see cref="Decide"/>, which reads
    /// <see cref="StockUiDecorationQuery.ForMissionControl"/>, which reads
    /// <see cref="StockUiReservationPredicates.IsContractAcceptBlocked"/> (Available) or
    /// <see cref="StockUiReservationPredicates.CommittedContractResolutionAfter"/> (Active):
    /// the same predicates the Accept, Decline and Cancel click-blocks read (the pairing rule).
    /// No Unity calls here, so the xUnit suite drives every branch.
    /// </summary>
    internal static class MissionControlStockAnnotation
    {
        /// <summary>The colour stock <c>MissionControl.AddItem</c> gives a row title.</summary>
        internal const string StockTitleColor = "fefa87";

        /// <summary>The colour of the Parsek status text on a row and in the detail panel.</summary>
        internal const string StatusColor = "8fd3ff";

        /// <summary>Everything from this marker to the end of a row label is Parsek's
        /// status; <see cref="StripRowStatus"/> cuts there so a relabel never doubles it.</summary>
        internal const string RowStatusMarker = " <color=#" + StatusColor + ">- ";

        /// <summary>Everything from this marker to the end of the detail text is Parsek's
        /// block; <see cref="StripDetail"/> cuts there.</summary>
        internal const string DetailMarker = "\n\n<b><color=#" + StatusColor + ">";

        /// <summary>The heading of the detail-panel block for a committed accept: says what is greyed out.</summary>
        internal const string DetailHeading = "Accept and Decline are unavailable";

        /// <summary>The heading of the detail-panel block for a committed resolution.</summary>
        internal const string CancelDetailHeading = "Cancel is unavailable";

        /// <summary>The row tail after the reservation title.</summary>
        internal const string RowStatusTail = " on your committed timeline";

        /// <summary>
        /// The label stock <c>MissionControl.AddItem</c> draws for a row passed an empty
        /// label (KSP 1.12.5: <c>"&lt;color=#fefa87&gt;" + contract.Title + "&lt;/color&gt;"</c>).
        /// A non-empty AddItem label REPLACES the whole title, so the prefix rebuilds it.
        /// </summary>
        internal static string StockDefaultLabel(string contractTitle)
        {
            return "<color=#" + StockTitleColor + ">" + (contractTitle ?? "") + "</color>";
        }

        /// <summary>
        /// The Mission Control tab a contract's row belongs to: Offered is Available,
        /// Active is Active, anything else is Archive.
        /// </summary>
        internal static string TabFor(Contract.State state)
        {
            switch (state)
            {
                case Contract.State.Offered:
                    return StockUiDecorationQuery.MissionControlAvailableTab;
                case Contract.State.Active:
                    return StockUiDecorationQuery.MissionControlActiveTab;
                default:
                    return StockUiDecorationQuery.MissionControlArchiveTab;
            }
        }

        /// <summary>
        /// The one decision every Mission Control annotation and block reads: the
        /// decoration of one contract row, over the committed-future index. Marked and
        /// Blocked are both true exactly when the contract is Offered and a committed
        /// future row accepts it after <paramref name="currentUT"/> (kind
        /// <see cref="StockUiDecorationKind.ContractAccept"/>), or the contract is Active and
        /// a committed row completes, fails or cancels it after <paramref name="currentUT"/>
        /// (kind <see cref="StockUiDecorationKind.ContractResolution"/>).
        /// </summary>
        internal static StockUiDecoration Decide(
            CommittedFutureIndex index,
            double currentUT,
            string contractKey,
            Contract.State state,
            Func<double, string> formatDate)
        {
            string tab = TabFor(state);
            if (string.IsNullOrEmpty(contractKey))
            {
                return new StockUiDecoration
                {
                    Screen = StockUiScreen.MissionControl,
                    Tab = tab,
                    Kind = StockUiDecorationKind.None,
                    Id = contractKey ?? "",
                    UT = double.NaN
                };
            }

            List<StockUiDecoration> one = StockUiDecorationQuery.ForMissionControl(
                index, currentUT, new[] { new StockUiItem(contractKey, tab) }, formatDate);
            return one[0];
        }

        /// <summary>True when the decision greys out Accept and Decline (a committed accept).</summary>
        internal static bool BlocksAcceptAndDecline(StockUiDecoration decoration)
        {
            return decoration.Blocked && decoration.Kind == StockUiDecorationKind.ContractAccept;
        }

        /// <summary>True when the decision greys out Cancel (a committed resolution).</summary>
        internal static bool BlocksCancel(StockUiDecoration decoration)
        {
            return decoration.Blocked && decoration.Kind == StockUiDecorationKind.ContractResolution;
        }

        /// <summary>The detail-panel heading naming the buttons a decision greys out.</summary>
        internal static string DetailHeadingFor(StockUiDecorationKind kind)
        {
            return kind == StockUiDecorationKind.ContractResolution ? CancelDetailHeading : DetailHeading;
        }

        /// <summary>
        /// The row status: the reservation title with a lower-case first letter plus
        /// <see cref="RowStatusTail"/>, e.g. <c>accepted on Y2, D114 on your committed timeline</c>
        /// or <c>completes on Y2, D114 on your committed timeline</c>.
        /// </summary>
        internal static string RowStatus(string reservationTitle)
        {
            if (string.IsNullOrEmpty(reservationTitle))
                return "accepted" + RowStatusTail;
            return char.ToLowerInvariant(reservationTitle[0]) + reservationTitle.Substring(1) + RowStatusTail;
        }

        /// <summary>Removes a Parsek row status (and anything after it) from a label.</summary>
        internal static string StripRowStatus(string label)
        {
            if (string.IsNullOrEmpty(label)) return label ?? "";
            int at = label.IndexOf(RowStatusMarker, StringComparison.Ordinal);
            return at >= 0 ? label.Substring(0, at) : label;
        }

        /// <summary>
        /// The row label to draw. <paramref name="currentLabel"/> is what stock would
        /// draw (an AddItem label, or a row's current title text); empty means stock's
        /// default title. Any earlier Parsek status is stripped, and the status is
        /// appended only for a marked decoration, so an unmarked row comes back exactly
        /// as stock drew it and a relabel is idempotent.
        /// </summary>
        internal static string ComposeRowLabel(string currentLabel, string contractTitle, StockUiDecoration decoration)
        {
            string baseLabel = string.IsNullOrEmpty(currentLabel)
                ? StockDefaultLabel(contractTitle)
                : StripRowStatus(currentLabel);
            if (!decoration.Marked)
                return baseLabel;
            return baseLabel + RowStatusMarker + RowStatus(decoration.Title) + "</color>";
        }

        /// <summary>True when <paramref name="label"/> carries a Parsek row status.</summary>
        internal static bool HasRowStatus(string label)
        {
            return !string.IsNullOrEmpty(label)
                   && label.IndexOf(RowStatusMarker, StringComparison.Ordinal) >= 0;
        }

        /// <summary>Removes the Parsek detail block (and anything after it) from the detail text.</summary>
        internal static string StripDetail(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            int at = text.IndexOf(DetailMarker, StringComparison.Ordinal);
            return at >= 0 ? text.Substring(0, at) : text;
        }

        /// <summary>
        /// The detail-panel text: the stock text, then (for a blocked decoration) a bold
        /// heading naming the greyed-out buttons and the reservation explanation, the
        /// same body the refused-click dialog shows. Idempotent: any earlier block is
        /// stripped first. KSPCF's ShowContractFinishDates splices into Archive text only,
        /// which is never blocked, so the two never touch the same text.
        /// </summary>
        internal static string ComposeDetailText(string stockText, StockUiDecoration decoration)
        {
            string baseText = StripDetail(stockText);
            if (!decoration.Blocked || string.IsNullOrEmpty(decoration.Why))
                return baseText;
            return baseText + DetailMarker + DetailHeadingFor(decoration.Kind) + "</color></b>\n" + decoration.Why;
        }
    }
}
