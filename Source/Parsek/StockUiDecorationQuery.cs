using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>A stock screen the reservation overlay decorates.</summary>
    internal enum StockUiScreen
    {
        RnD,
        AstronautComplex,
        MissionControl
    }

    /// <summary>Why a stock item is decorated. <see cref="None"/> is an undecorated item.</summary>
    internal enum StockUiDecorationKind
    {
        None,
        TechResearch,
        ContractAccept,
        /// <summary>An Active contract a committed row completes, fails or cancels later:
        /// the Active-row label and the Cancel block.</summary>
        ContractResolution,
        KerbalHire,
        KerbalRetire,
        KerbalOnFlight,
        KerbalLost,
        KerbalRetiredStandIn
    }

    /// <summary>One stock item as the screen lists it: a stable id and the tab it sits in.</summary>
    internal struct StockUiItem
    {
        internal string Id;
        internal string Tab;

        internal StockUiItem(string id, string tab)
        {
            Id = id;
            Tab = tab;
        }
    }

    /// <summary>
    /// The decoration decision for one stock item: whether it is marked, whether the
    /// click on it is refused, and the exact explanation text shown (the badge tooltip
    /// today, the stock reason field after the migration). One record per item the
    /// screen lists, decorated or not.
    /// </summary>
    internal struct StockUiDecoration
    {
        internal StockUiScreen Screen;
        internal string Tab;
        internal StockUiDecorationKind Kind;
        internal string Id;
        internal bool Marked;
        internal bool Blocked;
        /// <summary>The explanation shown for the item (<see cref="ReservationText.Body"/>),
        /// or null for an undecorated item.</summary>
        internal string Why;
        /// <summary>The short status (<see cref="ReservationText.Title"/>), or null.</summary>
        internal string Title;
        /// <summary>The committed row's UT the mark comes from, or NaN.</summary>
        internal double UT;
    }

    /// <summary>
    /// The live lookups the Astronaut Complex pass needs, as delegates so the query stays
    /// pure. Any null delegate answers "nothing known".
    /// </summary>
    internal sealed class AstronautComplexContext
    {
        internal Func<string, KerbalReservationKind> ReservationKind;
        internal Func<string, KerbalsModule.KerbalReservation> Reservation;
        internal Func<string, string> SlotOwner;
        internal Func<string, bool> DismissalBlocked;
        internal Func<string, bool> IsLoopingRecording;
        /// <summary>Names already in the live Crew or Tourist lists: a committed future
        /// hire of one of them is moot, so it is not marked.</summary>
        internal ISet<string> LiveCrewOrTourist;
    }

    /// <summary>
    /// The click-block predicates of the clickable reservation kinds. The Harmony
    /// click-blocks and the screen decoration read these, over the SAME
    /// <see cref="CommittedFutureIndex"/>, so a mark and its block cannot disagree
    /// (the pairing rule).
    /// </summary>
    internal static class StockUiReservationPredicates
    {
        internal static bool IsTechResearchBlocked(CommittedFutureIndex index, string techId, double currentUT)
        {
            return index != null && index.HasFuture(CommittedFutureKind.TechResearch, techId, currentUT);
        }

        /// <summary>The Accept block; the Decline block (<c>ContractDeclinePatch</c>), the
        /// Mission Control row label and the greyed Accept / Decline buttons read it too.</summary>
        internal static bool IsContractAcceptBlocked(CommittedFutureIndex index, string contractKey, double currentUT)
        {
            return index != null && index.HasFuture(CommittedFutureKind.ContractAccept, contractKey, currentUT);
        }

        /// <summary>
        /// The committed resolution of a contract still ahead of <paramref name="currentUT"/>:
        /// the earliest explicit <c>ContractComplete</c> / <c>ContractFail</c> /
        /// <c>ContractCancel</c> row (type, UT and recording), or null. Derived deadline
        /// expiry is not a committed row, so it never appears here (owner ruling D7, case
        /// X4). The Cancel block, the Cancel backstop, the Active-row label and the detail
        /// panel all read this one helper (the pairing rule). A UT tie between kinds
        /// resolves Complete, then Fail, then Cancel, so the answer is deterministic.
        /// </summary>
        internal static CommittedFutureEntry CommittedContractResolutionAfter(
            CommittedFutureIndex index, string contractKey, double currentUT)
        {
            if (index == null || string.IsNullOrEmpty(contractKey)) return null;
            CommittedFutureEntry best = null;
            foreach (var kind in ContractResolutionKinds)
            {
                var entry = index.FirstFuture(kind, contractKey, currentUT);
                if (entry != null && (best == null || entry.UT < best.UT))
                    best = entry;
            }
            return best;
        }

        private static readonly CommittedFutureKind[] ContractResolutionKinds =
        {
            CommittedFutureKind.ContractComplete,
            CommittedFutureKind.ContractFail,
            CommittedFutureKind.ContractCancel
        };

        /// <summary>The Cancel block: the committed timeline completes, fails or cancels
        /// this contract after <paramref name="currentUT"/> (cases X1-X3).</summary>
        internal static bool IsContractCancelBlocked(CommittedFutureIndex index, string contractKey, double currentUT)
        {
            return CommittedContractResolutionAfter(index, contractKey, currentUT) != null;
        }

        internal static ReservationText ExplainContractCancel(
            CommittedFutureIndex index, string contractKey, double currentUT, Func<double, string> formatDate)
        {
            return ReservationExplanation.ContractResolution(
                CommittedContractResolutionAfter(index, contractKey, currentUT), formatDate);
        }

        /// <summary>
        /// Blocked while any committed upgrade of this facility is still ahead. Once the
        /// clock passes the last one the block lifts, so a later upgrade the committed
        /// timeline never made is allowed (the F1 fix).
        /// </summary>
        internal static bool IsFacilityUpgradeBlocked(CommittedFutureIndex index, string facilityId, double currentUT)
        {
            return index != null && index.HasFuture(CommittedFutureKind.FacilityUpgrade, facilityId, currentUT);
        }

        internal static bool IsKerbalHireBlocked(CommittedFutureIndex index, string kerbalName, double currentUT)
        {
            return index != null && index.HasFuture(CommittedFutureKind.KerbalHire, kerbalName, currentUT);
        }

        internal static ReservationText ExplainTech(
            CommittedFutureIndex index, string techId, double currentUT, Func<double, string> formatDate)
        {
            return ReservationExplanation.Tech(
                index?.FirstFuture(CommittedFutureKind.TechResearch, techId, currentUT), formatDate);
        }

        internal static ReservationText ExplainContractAccept(
            CommittedFutureIndex index, string contractKey, double currentUT, Func<double, string> formatDate)
        {
            return ReservationExplanation.ContractAccept(
                index?.FirstFuture(CommittedFutureKind.ContractAccept, contractKey, currentUT), formatDate);
        }

        internal static ReservationText ExplainFacilityUpgrade(
            CommittedFutureIndex index, string facilityId, double currentUT, Func<double, string> formatDate)
        {
            return ReservationExplanation.FacilityUpgrade(
                index != null
                    ? index.FutureEntries(CommittedFutureKind.FacilityUpgrade, facilityId, currentUT)
                    : new List<CommittedFutureEntry>(),
                formatDate);
        }

        internal static ReservationText ExplainKerbalHire(
            CommittedFutureIndex index, string kerbalName, double currentUT, Func<double, string> formatDate)
        {
            return ReservationExplanation.KerbalHire(
                index?.FirstFuture(CommittedFutureKind.KerbalHire, kerbalName, currentUT), formatDate);
        }

        /// <summary>
        /// The explanation for a kerbal a committed flight holds: <c>Lost</c> for a
        /// permanent (death) reservation, else the on-flight text naming the holding flight.
        /// </summary>
        internal static ReservationText ExplainKerbalReservation(
            CommittedFutureIndex index,
            string kerbalName,
            KerbalsModule.KerbalReservation reservation,
            string slotOwner,
            Func<string, bool> isLoopingRecording,
            Func<double, string> formatDate)
        {
            if (reservation != null && reservation.IsPermanent)
            {
                CommittedKerbalAssignment death = null;
                if (index != null)
                {
                    var list = index.AssignmentsOf(kerbalName);
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i].EndState != KerbalEndState.Dead) continue;
                        if (death == null || EndKey(list[i]) > EndKey(death)) death = list[i];
                    }
                }
                return ReservationExplanation.KerbalLost(death?.RecordingName);
            }

            double release = reservation != null ? reservation.ReservedUntilUT : double.PositiveInfinity;
            bool openEnded = double.IsPositiveInfinity(release) || double.IsNaN(release);
            CommittedKerbalAssignment holdRow = index?.ResolveHoldAssignment(kerbalName, openEnded);
            var hold = new KerbalHold
            {
                KerbalName = kerbalName,
                SlotOwner = slotOwner,
                FlightName = holdRow?.RecordingName,
                EndState = holdRow != null ? holdRow.EndState : KerbalEndState.Unknown,
                ReleaseUT = release,
                IsLooping = holdRow != null && holdRow.RecordingId != null
                    && isLoopingRecording != null && isLoopingRecording(holdRow.RecordingId),
                FlightEndUT = holdRow != null ? holdRow.EndUT : double.NaN
            };
            return ReservationExplanation.KerbalOnFlight(hold, formatDate);
        }

        private static double EndKey(CommittedKerbalAssignment a)
        {
            return double.IsNaN(a.EndUT) ? double.NegativeInfinity : a.EndUT;
        }
    }

    /// <summary>
    /// Per-screen decoration queries over the committed-future index. Pure: the caller
    /// passes the index, the current UT and the stock items the screen lists, so the
    /// same decision is testable without Unity and loggable for the GUI mirror.
    /// </summary>
    internal static class StockUiDecorationQuery
    {
        private const string Tag = "StockUiOverlay";

        internal const string RnDTab = "Tree";
        internal const string MissionControlAvailableTab = "Available";
        internal const string MissionControlActiveTab = "Active";
        internal const string MissionControlArchiveTab = "Archive";
        internal const string AstronautApplicantsTab = "Applicants";
        internal const string AstronautAvailableTab = "Available";
        internal const string AstronautAssignedTab = "Assigned";
        internal const string AstronautKiaTab = "Kia";

        /// <summary>The retired stand-in's text: the Kerbals window's <c>Retired</c>
        /// status, qualified because the stock list does not say he is a stand-in.</summary>
        internal const string RetiredStandInText = "Retired stand-in (Parsek)";

        /// <summary>R&amp;D tech tree: a node a committed future researches is marked and
        /// its Research is refused.</summary>
        internal static List<StockUiDecoration> ForRnD(
            CommittedFutureIndex index,
            double currentUT,
            IEnumerable<string> techIds,
            Func<double, string> formatDate)
        {
            var result = new List<StockUiDecoration>();
            if (techIds == null) return result;
            foreach (string techId in techIds)
            {
                if (string.IsNullOrEmpty(techId)) continue;
                var d = Undecorated(StockUiScreen.RnD, RnDTab, techId);
                if (StockUiReservationPredicates.IsTechResearchBlocked(index, techId, currentUT))
                {
                    var text = StockUiReservationPredicates.ExplainTech(index, techId, currentUT, formatDate);
                    d.Kind = StockUiDecorationKind.TechResearch;
                    d.Marked = true;
                    d.Blocked = true;
                    d.Why = text.Body;
                    d.Title = text.Title;
                    d.UT = index.FirstFuture(CommittedFutureKind.TechResearch, techId, currentUT).UT;
                }
                result.Add(d);
            }
            return result;
        }

        /// <summary>
        /// Mission Control: an Offered contract (the Available tab) a committed future
        /// accepts is marked and its Accept and Decline are refused; an Active contract
        /// (the Active tab) a committed row later completes, fails or cancels is marked
        /// and its Cancel is refused. Archive rows are listed undecorated.
        /// </summary>
        internal static List<StockUiDecoration> ForMissionControl(
            CommittedFutureIndex index,
            double currentUT,
            IEnumerable<StockUiItem> rows,
            Func<double, string> formatDate)
        {
            var result = new List<StockUiDecoration>();
            if (rows == null) return result;
            foreach (var row in rows)
            {
                if (string.IsNullOrEmpty(row.Id)) continue;
                string tab = string.IsNullOrEmpty(row.Tab) ? MissionControlAvailableTab : row.Tab;
                var d = Undecorated(StockUiScreen.MissionControl, tab, row.Id);
                if (tab == MissionControlAvailableTab
                    && StockUiReservationPredicates.IsContractAcceptBlocked(index, row.Id, currentUT))
                {
                    var text = StockUiReservationPredicates.ExplainContractAccept(index, row.Id, currentUT, formatDate);
                    d.Kind = StockUiDecorationKind.ContractAccept;
                    d.Marked = true;
                    d.Blocked = true;
                    d.Why = text.Body;
                    d.Title = text.Title;
                    d.UT = index.FirstFuture(CommittedFutureKind.ContractAccept, row.Id, currentUT).UT;
                }
                else if (tab == MissionControlActiveTab)
                {
                    var resolution = StockUiReservationPredicates.CommittedContractResolutionAfter(index, row.Id, currentUT);
                    if (resolution != null)
                    {
                        var text = ReservationExplanation.ContractResolution(resolution, formatDate);
                        d.Kind = StockUiDecorationKind.ContractResolution;
                        d.Marked = true;
                        d.Blocked = true;
                        d.Why = text.Body;
                        d.Title = text.Title;
                        d.UT = resolution.UT;
                    }
                }
                result.Add(d);
            }
            return result;
        }

        /// <summary>
        /// Astronaut Complex, in resolution order: a committed future hire (unless the
        /// kerbal is already live crew), a committed future dismissal, a reservation held
        /// by a committed flight (Lost when permanent), a retired stand-in. Blocked is the
        /// hire block for a future hire and the dismissal block otherwise.
        /// </summary>
        internal static List<StockUiDecoration> ForAstronautComplex(
            CommittedFutureIndex index,
            double currentUT,
            IEnumerable<StockUiItem> rows,
            AstronautComplexContext context,
            Func<double, string> formatDate)
        {
            var result = new List<StockUiDecoration>();
            if (rows == null) return result;
            context = context ?? new AstronautComplexContext();
            foreach (var row in rows)
            {
                string name = row.Id;
                if (string.IsNullOrEmpty(name)) continue;
                string tab = string.IsNullOrEmpty(row.Tab) ? AstronautApplicantsTab : row.Tab;
                var d = Undecorated(StockUiScreen.AstronautComplex, tab, name);
                bool dismissalBlocked = context.DismissalBlocked != null && context.DismissalBlocked(name);
                d.Blocked = dismissalBlocked;

                bool alreadyLive = context.LiveCrewOrTourist != null && context.LiveCrewOrTourist.Contains(name);
                if (!alreadyLive && StockUiReservationPredicates.IsKerbalHireBlocked(index, name, currentUT))
                {
                    var text = StockUiReservationPredicates.ExplainKerbalHire(index, name, currentUT, formatDate);
                    Mark(ref d, StockUiDecorationKind.KerbalHire, text,
                        index.FirstFuture(CommittedFutureKind.KerbalHire, name, currentUT).UT);
                    d.Blocked = true;
                    result.Add(d);
                    continue;
                }

                var retire = index?.FirstFuture(CommittedFutureKind.KerbalRetire, name, currentUT);
                if (retire != null)
                {
                    Mark(ref d, StockUiDecorationKind.KerbalRetire,
                        ReservationExplanation.KerbalRetire(retire, formatDate), retire.UT);
                    result.Add(d);
                    continue;
                }

                var kind = context.ReservationKind != null
                    ? context.ReservationKind(name)
                    : KerbalReservationKind.NotManaged;
                if (kind == KerbalReservationKind.ReservedActive)
                {
                    var reservation = context.Reservation != null ? context.Reservation(name) : null;
                    string owner = context.SlotOwner != null ? context.SlotOwner(name) : null;
                    var text = StockUiReservationPredicates.ExplainKerbalReservation(
                        index, name, reservation, owner, context.IsLoopingRecording, formatDate);
                    Mark(ref d,
                        reservation != null && reservation.IsPermanent
                            ? StockUiDecorationKind.KerbalLost
                            : StockUiDecorationKind.KerbalOnFlight,
                        text, double.NaN);
                }
                else if (kind == KerbalReservationKind.ReservedRetired)
                {
                    d.Kind = StockUiDecorationKind.KerbalRetiredStandIn;
                    d.Marked = true;
                    d.Why = RetiredStandInText;
                    d.Title = "Retired";
                }
                result.Add(d);
            }
            return result;
        }

        private static void Mark(ref StockUiDecoration d, StockUiDecorationKind kind, ReservationText text, double ut)
        {
            d.Kind = kind;
            d.Marked = true;
            d.Why = text.Body;
            d.Title = text.Title;
            d.UT = ut;
        }

        private static StockUiDecoration Undecorated(StockUiScreen screen, string tab, string id)
        {
            return new StockUiDecoration
            {
                Screen = screen,
                Tab = tab,
                Kind = StockUiDecorationKind.None,
                Id = id,
                Marked = false,
                Blocked = false,
                Why = null,
                Title = null,
                UT = double.NaN
            };
        }

        /// <summary>
        /// Logs one decoration pass: one Info line per listed tab
        /// (<c>decorate screen=&lt;S&gt; tab=&lt;T&gt; items=N marked=M blocked=B</c>), then one
        /// Verbose line per MARKED or BLOCKED item (bounded by the committed set, unlike the
        /// full item list: a tech tree lists a hundred nodes).
        /// </summary>
        internal static void LogPass(
            StockUiScreen screen,
            IList<string> tabs,
            IReadOnlyList<StockUiDecoration> decorations)
        {
            var ic = CultureInfo.InvariantCulture;
            var order = new List<string>();
            if (tabs != null)
                for (int i = 0; i < tabs.Count; i++)
                    if (!string.IsNullOrEmpty(tabs[i]) && !order.Contains(tabs[i])) order.Add(tabs[i]);
            if (decorations != null)
                for (int i = 0; i < decorations.Count; i++)
                    if (!order.Contains(decorations[i].Tab)) order.Add(decorations[i].Tab);

            for (int t = 0; t < order.Count; t++)
            {
                string tab = order[t];
                int items = 0, marked = 0, blocked = 0;
                if (decorations != null)
                {
                    for (int i = 0; i < decorations.Count; i++)
                    {
                        if (decorations[i].Tab != tab) continue;
                        items++;
                        if (decorations[i].Marked) marked++;
                        if (decorations[i].Blocked) blocked++;
                    }
                }
                ParsekLog.Info(Tag, string.Format(ic,
                    "decorate screen={0} tab={1} items={2} marked={3} blocked={4}",
                    screen, tab, items, marked, blocked));
            }

            if (decorations == null) return;
            for (int i = 0; i < decorations.Count; i++)
            {
                var d = decorations[i];
                if (!d.Marked && !d.Blocked) continue;
                ParsekLog.Verbose(Tag, FormatItemLine(d));
            }
        }

        /// <summary>The per-item Verbose line.</summary>
        internal static string FormatItemLine(StockUiDecoration d)
        {
            return "decorate screen=" + d.Screen + " tab=" + d.Tab
                   + " item=" + d.Id
                   + " kind=" + d.Kind
                   + " marked=" + (d.Marked ? "true" : "false")
                   + " blocked=" + (d.Blocked ? "true" : "false")
                   + " why=\"" + (d.Why ?? "") + "\"";
        }
    }
}
