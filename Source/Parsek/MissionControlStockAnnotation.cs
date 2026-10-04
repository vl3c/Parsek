using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
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
    /// a greyed Cancel. Any other Offered contract gets a greyed Accept (only) and the reason
    /// in the detail panel, never a row mark, while the committed timeline needs every free
    /// slot (<see cref="ContractSlotReservation"/>, C2). Every one of those reads <see cref="Decide"/>, which reads
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

        /// <summary>The colour of the Parsek status on a row: an informational dated fact,
        /// so it is the one Parsek colour on the stock screens that is not the reason orange
        /// (owner decision K2-a).</summary>
        internal const string StatusColor = "8fd3ff";

        /// <summary>The colour of the detail-panel block: a block reason, so stock's own
        /// reason orange (<see cref="StockUiRnDDecoration.ReasonColorHex"/>), like every other
        /// stock-screen block reason.</summary>
        internal const string DetailColor = "f97306";

        /// <summary>Everything from this marker to the end of a row label is Parsek's
        /// status; <see cref="StripRowStatus"/> cuts there so a relabel never doubles it.</summary>
        internal const string RowStatusMarker = " <color=#" + StatusColor + ">- ";

        /// <summary>Everything from this marker to the end of the detail text is Parsek's
        /// block; <see cref="StripDetail"/> cuts there.</summary>
        internal const string DetailMarker = "\n\n<b><color=#" + DetailColor + ">";

        /// <summary>The heading of the detail-panel block for a committed accept: says what is greyed out.</summary>
        internal const string DetailHeading = "Accept and Decline are unavailable";

        /// <summary>The heading of the detail-panel block for a committed resolution.</summary>
        internal const string CancelDetailHeading = "Cancel is unavailable";

        /// <summary>The heading of the detail-panel block for a slot the committed timeline
        /// needs (C2): only Accept is greyed out, Decline stays stock's.</summary>
        internal const string SlotDetailHeading = "Accept is unavailable";

        /// <summary>
        /// The longest row status (<c>Cancelled on Y12, D426</c> is 22) the stock row fits
        /// next to a long title: <c>MCListItem</c> draws at most three lines, so the row
        /// carries the date without the time of day; the detail panel keeps the full
        /// explanation with the time.
        /// </summary>
        internal const int RowStatusMaxLength = 24;

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
        /// (kind <see cref="StockUiDecorationKind.ContractResolution"/>). An Offered contract
        /// the committed timeline does not accept is Blocked but NOT marked (kind
        /// <see cref="StockUiDecorationKind.ContractSlot"/>) when <paramref name="slots"/> says
        /// a new accept now would leave no slot for a committed accept; a null forecast
        /// reserves no slot. Pass <paramref name="autoAccept"/> for a stock auto-accepted
        /// contract: stock neither counts nor slot-checks those, so neither does Parsek.
        /// <paramref name="newContractReleaseUT"/> is when the contract, accepted now, would
        /// give its slot back by deadline (+inf for none): a committed accept after that is
        /// not starved by it.
        /// </summary>
        internal static StockUiDecoration Decide(
            CommittedFutureIndex index,
            double currentUT,
            string contractKey,
            Contract.State state,
            Func<double, string> formatDate,
            ContractSlotForecast slots = null,
            bool autoAccept = false,
            double newContractReleaseUT = double.PositiveInfinity)
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
                index, currentUT, new[] { new StockUiItem(contractKey, tab) }, formatDate,
                autoAccept ? null : slots, _ => newContractReleaseUT);
            return one[0];
        }

        /// <summary>True when the decision greys out Accept and Decline (a committed accept).</summary>
        internal static bool BlocksAcceptAndDecline(StockUiDecoration decoration)
        {
            return decoration.Blocked && decoration.Kind == StockUiDecorationKind.ContractAccept;
        }

        /// <summary>True when the decision greys out Accept only (a slot the committed timeline needs).</summary>
        internal static bool BlocksAcceptForSlot(StockUiDecoration decoration)
        {
            return decoration.Blocked && decoration.Kind == StockUiDecorationKind.ContractSlot;
        }

        /// <summary>
        /// True when the decision refuses Accept, for either reason: the greyed Accept, the
        /// <c>Contract.Accept</c> backstop and Contract Configurator's <c>CanAccept</c> all read this.
        /// </summary>
        internal static bool BlocksAccept(StockUiDecoration decoration)
        {
            return BlocksAcceptAndDecline(decoration) || BlocksAcceptForSlot(decoration);
        }

        /// <summary>True when the decision greys out Cancel (a committed resolution).</summary>
        internal static bool BlocksCancel(StockUiDecoration decoration)
        {
            return decoration.Blocked && decoration.Kind == StockUiDecorationKind.ContractResolution;
        }

        /// <summary>
        /// Which of Accept / Decline / Cancel Parsek greys for the contract on show: the same
        /// buttons the decision disables (<see cref="BlocksAccept"/>,
        /// <see cref="BlocksAcceptAndDecline"/>, <see cref="BlocksCancel"/>). An undecorated
        /// or unblocked decision greys none, which gives back any stock look Parsek saved.
        /// </summary>
        internal static void GreyedButtons(StockUiDecoration decoration, out bool accept, out bool decline, out bool cancel)
        {
            accept = BlocksAccept(decoration);
            decline = BlocksAcceptAndDecline(decoration);
            cancel = BlocksCancel(decoration);
        }

        /// <summary>The detail-panel heading naming the buttons a decision greys out.</summary>
        internal static string DetailHeadingFor(StockUiDecorationKind kind)
        {
            switch (kind)
            {
                case StockUiDecorationKind.ContractResolution: return CancelDetailHeading;
                case StockUiDecorationKind.ContractSlot: return SlotDetailHeading;
                default: return DetailHeading;
            }
        }

        /// <summary>
        /// The row status (owner decision K1-a): the reservation title's participle and the
        /// date without the time of day, in the detail panel's own form, e.g.
        /// <c>Accepted on Y1, D03</c> or <c>Completed on Y2, D114</c>. The participle is the
        /// title's words before " on " (<see cref="ReservationExplanation"/>: Accepted /
        /// Completed / Failed / Expired / Cancelled). The date is
        /// <paramref name="formatRowDate"/> of the decoration's UT (production:
        /// <see cref="MissionControlStockUi.RowDateFormatter"/>, stock's compact date without
        /// the time), else the title's own date with its time of day cut.
        /// </summary>
        internal static string RowStatus(StockUiDecoration decoration, Func<double, string> formatRowDate)
        {
            string verb = RowVerb(decoration);
            string date = null;
            if (formatRowDate != null && !double.IsNaN(decoration.UT) && !double.IsInfinity(decoration.UT))
                date = formatRowDate(decoration.UT);
            if (string.IsNullOrEmpty(date))
                date = WithoutTimeOfDay(TitleDate(decoration.Title));
            return string.IsNullOrEmpty(date) ? verb : verb + " on " + date.Trim();
        }

        /// <summary>The participle of a row status: the title's words before " on ", else
        /// the kind's own participle.</summary>
        internal static string RowVerb(StockUiDecoration decoration)
        {
            string title = decoration.Title;
            if (!string.IsNullOrEmpty(title))
            {
                int at = title.IndexOf(" on ", StringComparison.Ordinal);
                string head = at > 0 ? title.Substring(0, at) : title;
                if (head.Length > 0)
                    return char.ToUpperInvariant(head[0]) + head.Substring(1);
            }
            return decoration.Kind == StockUiDecorationKind.ContractResolution ? "Completed" : "Accepted";
        }

        /// <summary>The date part of a reservation title (<c>Accepted on X</c> is <c>X</c>), or null.</summary>
        internal static string TitleDate(string title)
        {
            if (string.IsNullOrEmpty(title)) return null;
            int at = title.IndexOf(" on ", StringComparison.Ordinal);
            return at > 0 && at + 4 < title.Length ? title.Substring(at + 4) : null;
        }

        private static readonly Regex TrailingTimeOfDay =
            new Regex(@",\s*\d{1,2}:\d{2}(:\d{2})?\s*$", RegexOptions.CultureInvariant);

        /// <summary>
        /// A compact date without its trailing time of day (<c>Y1, D03, 01:53</c> is
        /// <c>Y1, D03</c>); any other shape comes back trimmed and unchanged.
        /// </summary>
        internal static string WithoutTimeOfDay(string date)
        {
            if (string.IsNullOrEmpty(date)) return date;
            return TrailingTimeOfDay.Replace(date, "").Trim();
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
        internal static string ComposeRowLabel(string currentLabel, string contractTitle, StockUiDecoration decoration,
            Func<double, string> formatRowDate = null)
        {
            string baseLabel = string.IsNullOrEmpty(currentLabel)
                ? StockDefaultLabel(contractTitle)
                : StripRowStatus(currentLabel);
            if (!decoration.Marked)
                return baseLabel;
            baseLabel = StockUiText.ResolveStockKey(baseLabel, "Mission Control row label");
            return baseLabel + RowStatusMarker + RowStatus(decoration, formatRowDate) + "</color>";
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
        /// same body the refused-click dialog shows, both in the reason orange. Idempotent: any earlier block is
        /// stripped first. KSPCF's ShowContractFinishDates splices into Archive text only,
        /// which is never blocked, so the two never touch the same text.
        /// </summary>
        internal static string ComposeDetailText(string stockText, StockUiDecoration decoration)
        {
            string baseText = StripDetail(stockText);
            if (!decoration.Blocked || string.IsNullOrEmpty(decoration.Why))
                return baseText;
            baseText = StockUiText.ResolveStockKey(baseText, "Mission Control detail text");
            return baseText + DetailMarker + DetailHeadingFor(decoration.Kind) + "</color></b>\n"
                + "<color=#" + DetailColor + ">" + decoration.Why + "</color>";
        }
    }
}
