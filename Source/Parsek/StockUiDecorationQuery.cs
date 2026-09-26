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
        MissionControl,
        /// <summary>The VAB/SPH crew assignment dialog (<c>BaseCrewAssignmentDialog</c>).</summary>
        CrewAssignment,
        /// <summary>The KSC facility context menu (the building right-click menu).</summary>
        FacilityMenu
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
        /// <summary>An Offered contract the committed timeline does not accept, when
        /// accepting it now would leave no Mission Control slot for a committed accept
        /// (C2). Blocked, never marked: it applies to every such row at once, so the
        /// detail-panel reason is the annotation.</summary>
        ContractSlot,
        KerbalHire,
        KerbalRetire,
        KerbalOnFlight,
        KerbalLost,
        KerbalRetiredStandIn,
        /// <summary>A facility a committed row upgrades later: the facility menu's greyed
        /// Upgrade button and the upgrade refusal.</summary>
        FacilityUpgrade,
        /// <summary>An active stand-in (the active occupant of another kerbal's slot chain):
        /// the informational row label <c>Stand-in for &lt;owner&gt;</c> and the dismissal
        /// block, whose refusal text is the record's why.</summary>
        KerbalStandIn,
        /// <summary>A facility whose current destruction a committed row already repairs
        /// later: the facility menu's greyed Repair button and the repair refusal.</summary>
        FacilityRepair
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
        /// <summary>The dismissal block's refusal text, or null when dismissal is allowed:
        /// the SAME predicate the dismiss button, <c>KerbalRoster.Remove</c> and
        /// <c>SackAvailable</c> refuse with (<c>KerbalDismissalPatch.DescribeDismissalRefusal</c>),
        /// so the record's blocked flag and why cannot disagree with the drawn row.</summary>
        internal Func<string, string> DismissalRefusal;
        /// <summary>The owner whose seat this kerbal is the ACTIVE stand-in for, or null
        /// (<c>KerbalsModule.FindActiveStandInOwner</c>).</summary>
        internal Func<string, string> ActiveStandInOwner;
        /// <summary>The owner whose seat this active stand-in shares under the active-crew
        /// count's own subtraction rule, or null (<c>StandInSeatCount.SeatSharedOwner</c>):
        /// only then does the stand-in's tooltip say it does not count against the limit.</summary>
        internal Func<string, string> SeatSharedOwner;
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

        /// <summary>
        /// The committed future repair that already covers one building's CURRENT
        /// destruction: the earliest committed <c>FacilityRepair</c> row of the building
        /// after <paramref name="currentUT"/>, when no committed <c>FacilityDestruction</c>
        /// of it comes first (or at the same UT). A committed destruction first means the
        /// committed timeline had the building intact until then, so the building's present
        /// destruction is not the one the committed repair repairs, and repairing it now is
        /// not a second charge. Null otherwise. Key: the destructible building id.
        /// </summary>
        internal static CommittedFutureEntry CommittedRepairCoveringDestruction(
            CommittedFutureIndex index, string buildingId, double currentUT)
        {
            if (index == null || string.IsNullOrEmpty(buildingId)) return null;
            var repair = index.FirstFuture(CommittedFutureKind.FacilityRepair, buildingId, currentUT);
            if (repair == null) return null;
            var destruction = index.FirstFuture(CommittedFutureKind.FacilityDestruction, buildingId, currentUT);
            if (destruction != null && destruction.UT <= repair.UT) return null;
            return repair;
        }

        /// <summary>
        /// The covering committed repairs of every DESTROYED building of one facility
        /// (<see cref="CommittedRepairCoveringDestruction"/>), UT ascending. Only destroyed
        /// buildings count: stock's <c>RepairFacility</c> charges and repairs exactly those.
        /// </summary>
        internal static List<CommittedFutureEntry> CommittedRepairsCoveringFacility(
            CommittedFutureIndex index, IEnumerable<FacilityRepairCapture.BuildingRepairInput> buildings,
            double currentUT)
        {
            var result = new List<CommittedFutureEntry>();
            if (index == null || buildings == null) return result;
            foreach (var b in buildings)
            {
                if (!b.IsDestroyed) continue;
                var entry = CommittedRepairCoveringDestruction(index, b.BuildingId, currentUT);
                if (entry != null && !result.Contains(entry)) result.Add(entry);
            }
            result.Sort((x, y) =>
            {
                int c = x.UT.CompareTo(y.UT);
                return c != 0 ? c : string.CompareOrdinal(x.Key, y.Key);
            });
            return result;
        }

        /// <summary>
        /// The repair block (KSC-REPAIR-AFTER-REWIND-DOUBLE-CHARGE): repairing a facility now
        /// is refused while any of its destroyed buildings has a committed future repair
        /// that already covers that destruction, because the ledger walk would charge both
        /// repairs. It lifts once the clock passes the covering repair (the walk then shows
        /// the building repaired). The greyed Repair button and the
        /// <c>SpaceCenterBuilding.RepairFacility</c> refusal read this one predicate.
        /// </summary>
        internal static bool IsFacilityRepairBlocked(
            CommittedFutureIndex index, IEnumerable<FacilityRepairCapture.BuildingRepairInput> buildings,
            double currentUT)
        {
            return CommittedRepairsCoveringFacility(index, buildings, currentUT).Count > 0;
        }

        internal static ReservationText ExplainFacilityRepair(
            CommittedFutureIndex index, IEnumerable<FacilityRepairCapture.BuildingRepairInput> buildings,
            double currentUT, Func<double, string> formatDate)
        {
            return ReservationExplanation.FacilityRepair(
                CommittedRepairsCoveringFacility(index, buildings, currentUT), formatDate);
        }

        internal static bool IsKerbalHireBlocked(CommittedFutureIndex index, string kerbalName, double currentUT)
        {
            return index != null && index.HasFuture(CommittedFutureKind.KerbalHire, kerbalName, currentUT);
        }

        /// <summary>
        /// The committed part purchase still ahead of <paramref name="currentUT"/>: the
        /// earliest <c>FundsSpending</c> / <c>Other</c> row for this part (key =
        /// <c>AvailablePart.name</c>, the runtime dot-form the capture stores), or null.
        /// A zero-cost row reserves the part too: it is an identical part stock bought
        /// free with a paid sibling, and <c>KspStatePatcher.PatchPurchasedParts</c> marks
        /// it purchased at its UT, so buying it now would pay for what the timeline gets
        /// free. Under bypass-entry-purchase every row is zero-cost and
        /// <see cref="IsPartPurchaseBlocked"/> is inert.
        /// </summary>
        internal static CommittedFutureEntry CommittedPartPurchaseAfter(
            CommittedFutureIndex index, string partName, double currentUT)
        {
            if (index == null || string.IsNullOrEmpty(partName)) return null;
            return index.FirstFuture(CommittedFutureKind.PartPurchase, partName, currentUT);
        }

        /// <summary>
        /// The part-purchase block (P1): buying <paramref name="partName"/> now is refused
        /// when the committed timeline buys it later and stock still shows it unpurchased.
        /// Inert with stock's bypass-entry-purchase difficulty option on (purchases are free
        /// and every researched part is rehydrated). It lifts at the committed row's UT,
        /// when <c>KspStatePatcher.PatchPurchasedParts</c> marks the part purchased. The
        /// greyed tooltip button, the editor / R&amp;D purchase refusals and the R&amp;D
        /// purchase-all skip all read this one predicate (the pairing rule).
        /// </summary>
        internal static bool IsPartPurchaseBlocked(
            CommittedFutureIndex index, string partName, double currentUT,
            bool bypassEntryPurchase, bool purchasedInStock)
        {
            if (bypassEntryPurchase || purchasedInStock) return false;
            return CommittedPartPurchaseAfter(index, partName, currentUT) != null;
        }

        internal static ReservationText ExplainPartPurchase(
            CommittedFutureIndex index, string partName, double currentUT, Func<double, string> formatDate)
        {
            return ReservationExplanation.PartPurchase(
                CommittedPartPurchaseAfter(index, partName, currentUT), formatDate);
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
        /// <summary>The crew assignment dialog's available-crew list (<c>scrollListAvail</c>).</summary>
        internal const string CrewAssignmentAvailableTab = "Available";
        /// <summary>The facility menu has no tabs; the tab names the decorated control
        /// (Upgrade, or <see cref="FacilityMenuRepairTab"/>).</summary>
        internal const string FacilityMenuTab = "Upgrade";
        /// <summary>The facility menu's Repair control.</summary>
        internal const string FacilityMenuRepairTab = "Repair";

        /// <summary>The retired stand-in's text: the Kerbals window's <c>Retired</c>
        /// status, qualified because the stock list does not say he is a stand-in.</summary>
        internal const string RetiredStandInText = "Retired stand-in (Parsek)";

        /// <summary>The active stand-in's row status, the Kerbals window's Stand-in wording.</summary>
        internal static string StandInTitle(string ownerName)
        {
            return "Stand-in for " + ownerName;
        }

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
        /// accepts is marked and its Accept and Decline are refused; any other Offered
        /// contract has its Accept (only) refused, unmarked, when <paramref name="slots"/>
        /// says a new accept now would leave no slot for a committed accept (C2); an Active
        /// contract (the Active tab) a committed row later completes, fails or cancels is
        /// marked and its Cancel is refused. Archive rows are listed undecorated. A null
        /// <paramref name="slots"/> reserves no slot. <paramref name="newAcceptReleaseUT"/> gives
        /// the UT an Offered row's contract would release its slot by deadline if accepted now
        /// (<see cref="ContractSlotReservation.NewAcceptReleaseUT(Contracts.Contract, double)"/>);
        /// null means no deadline.
        /// </summary>
        internal static List<StockUiDecoration> ForMissionControl(
            CommittedFutureIndex index,
            double currentUT,
            IEnumerable<StockUiItem> rows,
            Func<double, string> formatDate,
            ContractSlotForecast slots = null,
            Func<string, double> newAcceptReleaseUT = null)
        {
            ReservationText slotText = default(ReservationText);
            bool slotTextBuilt = false;
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
                else if (tab == MissionControlAvailableTab
                    && ContractSlotReservation.IsAcceptSlotBlocked(
                        slots, index, row.Id, Contracts.Contract.State.Offered, false, currentUT,
                        newAcceptReleaseUT != null ? newAcceptReleaseUT(row.Id) : double.PositiveInfinity))
                {
                    if (!slotTextBuilt)
                    {
                        slotText = ContractSlotReservation.Explain(slots, formatDate);
                        slotTextBuilt = true;
                    }
                    d.Kind = StockUiDecorationKind.ContractSlot;
                    d.Marked = false;
                    d.Blocked = true;
                    d.Why = slotText.Body;
                    d.Title = slotText.Title;
                    d.UT = slots.FirstStarvedAccept != null ? slots.FirstStarvedAccept.UT : double.NaN;
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
        /// by a committed flight (Lost when permanent), a retired stand-in, an active stand-in
        /// (<c>Stand-in for &lt;owner&gt;</c>). Blocked is the hire block for a future hire and
        /// the dismissal block otherwise; a dismissal-blocked row no kind marks carries the
        /// refusal text as its why.
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
                string dismissalRefusal = context.DismissalRefusal != null ? context.DismissalRefusal(name) : null;
                bool dismissalBlocked = dismissalRefusal != null;
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
                else if (dismissalBlocked)
                {
                    // Not reserved or retired but refused dismissal: an active stand-in (a
                    // per-member fact, Kerbals window ruling 10), a displaced chain member, or
                    // a returned owner a committed flight names. The why is the refusal the
                    // dismiss button's tooltip draws; only the active stand-in gets a label.
                    string owner = context.ActiveStandInOwner != null ? context.ActiveStandInOwner(name) : null;
                    if (!string.IsNullOrEmpty(owner))
                    {
                        d.Kind = StockUiDecorationKind.KerbalStandIn;
                        d.Marked = true;
                        d.Title = StandInTitle(owner);
                    }
                    d.Why = dismissalRefusal;
                    if (d.Kind == StockUiDecorationKind.KerbalStandIn)
                    {
                        string seatOwner = context.SeatSharedOwner != null ? context.SeatSharedOwner(name) : null;
                        if (!string.IsNullOrEmpty(seatOwner))
                            d.Why = StandInSeatCount.AppendSeatSharedSentence(dismissalRefusal, seatOwner);
                    }
                }
                result.Add(d);
            }
            return result;
        }

        /// <summary>
        /// KSC facility context menu: a facility a committed row upgrades later is marked
        /// and its Upgrade button refused, over
        /// <see cref="StockUiReservationPredicates.IsFacilityUpgradeBlocked"/>, the same
        /// predicate and text the <c>FacilityUpgradeSpendPatch</c> /
        /// <c>FacilityUpgradePatch</c> refusal reads. While
        /// <paramref name="replaying"/> the refusal is bypassed, so the menu is left stock
        /// too (the pairing rule holds in both states). A null or empty id (a building with
        /// no upgradeable facility) is undecorated.
        /// </summary>
        internal static StockUiDecoration ForFacilityMenu(
            CommittedFutureIndex index,
            double currentUT,
            string facilityId,
            bool replaying,
            Func<double, string> formatDate)
        {
            var d = Undecorated(StockUiScreen.FacilityMenu, FacilityMenuTab, facilityId);
            if (string.IsNullOrEmpty(facilityId) || replaying)
                return d;
            if (!StockUiReservationPredicates.IsFacilityUpgradeBlocked(index, facilityId, currentUT))
                return d;
            var text = StockUiReservationPredicates.ExplainFacilityUpgrade(index, facilityId, currentUT, formatDate);
            Mark(ref d, StockUiDecorationKind.FacilityUpgrade, text,
                index.FirstFuture(CommittedFutureKind.FacilityUpgrade, facilityId, currentUT).UT);
            d.Blocked = true;
            return d;
        }

        /// <summary>
        /// KSC facility context menu, the Repair control: a facility whose destroyed
        /// building's destruction a committed row already repairs later is marked and its
        /// Repair refused, over <see cref="StockUiReservationPredicates.IsFacilityRepairBlocked"/>,
        /// the same predicate and text the <c>RepairFacility</c> refusal reads. Bypassed while
        /// <paramref name="replaying"/>, as the refusal is. <paramref name="facilityId"/> only
        /// names the item; the decision reads <paramref name="buildings"/>.
        /// </summary>
        internal static StockUiDecoration ForFacilityMenuRepair(
            CommittedFutureIndex index,
            double currentUT,
            string facilityId,
            IEnumerable<FacilityRepairCapture.BuildingRepairInput> buildings,
            bool replaying,
            Func<double, string> formatDate)
        {
            var d = Undecorated(StockUiScreen.FacilityMenu, FacilityMenuRepairTab, facilityId);
            if (replaying) return d;
            var covering = StockUiReservationPredicates.CommittedRepairsCoveringFacility(index, buildings, currentUT);
            if (covering.Count == 0) return d;
            Mark(ref d, StockUiDecorationKind.FacilityRepair,
                ReservationExplanation.FacilityRepair(covering, formatDate), covering[0].UT);
            d.Blocked = true;
            return d;
        }

        /// <summary>
        /// Logs one facility menu Repair decoration. A marked one is the per-item line of the
        /// menu's pass (<see cref="FormatItemLine"/>, Verbose, after the Upgrade line), so the
        /// GUI mirror lists it as the menu's second row; an unmarked one is a plain Verbose
        /// sentence saying why Repair is left to stock.
        /// </summary>
        internal static void LogFacilityMenuRepair(StockUiDecoration d, bool replaying, bool anyDestroyed, string reason)
        {
            if (d.Marked || d.Blocked)
            {
                ParsekLog.Verbose(Tag, FormatItemLine(d));
                return;
            }
            string why;
            if (replaying)
                why = "action replay in progress (the repair refusal is bypassed too)";
            else if (!anyDestroyed)
                why = "no destroyed building";
            else
                why = "no committed future repair covers the destruction";
            ParsekLog.Verbose(Tag, "FacilityMenu " + (string.IsNullOrEmpty(d.Id) ? "<none>" : d.Id)
                + " Repair left to stock (" + (reason ?? "refresh") + "): " + why);
        }

        /// <summary>The facility menu's one Info line per decoration:
        /// <c>decorate screen=FacilityMenu facility=&lt;id&gt; marked=&lt;b&gt; blocked=&lt;b&gt;</c>.</summary>
        internal static string FormatFacilityMenuLine(StockUiDecoration d)
        {
            return "decorate screen=" + StockUiScreen.FacilityMenu
                   + " facility=" + (string.IsNullOrEmpty(d.Id) ? "<none>" : d.Id)
                   + " marked=" + (d.Marked ? "true" : "false")
                   + " blocked=" + (d.Blocked ? "true" : "false");
        }

        /// <summary>
        /// Logs one facility menu decoration: the Info line, then one Verbose line with the
        /// why (the explanation for a marked facility, else the reason it is left stock).
        /// </summary>
        internal static void LogFacilityMenu(StockUiDecoration d, bool replaying, string reason)
        {
            ParsekLog.Info(Tag, FormatFacilityMenuLine(d));
            string why;
            if (d.Marked)
                why = "why=\"" + (d.Why ?? "") + "\"";
            else if (string.IsNullOrEmpty(d.Id))
                why = "unmarked: the building has no upgradeable facility";
            else if (replaying)
                why = "unmarked: action replay in progress (the upgrade refusal is bypassed too)";
            else
                why = "unmarked: no committed future upgrade of this facility";
            ParsekLog.Verbose(Tag, "decorate screen=" + StockUiScreen.FacilityMenu
                + " facility=" + (string.IsNullOrEmpty(d.Id) ? "<none>" : d.Id)
                + " (" + (reason ?? "refresh") + ") " + why);
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
            // Slot-refused Accepts (C2) share one reason and can cover every Offered row,
            // so they get one summary line instead of one line each.
            int slotBlocked = 0;
            string slotWhy = null;
            for (int i = 0; i < decorations.Count; i++)
            {
                var d = decorations[i];
                if (!d.Marked && !d.Blocked) continue;
                if (!d.Marked && d.Kind == StockUiDecorationKind.ContractSlot)
                {
                    slotBlocked++;
                    if (slotWhy == null) slotWhy = d.Why;
                    continue;
                }
                ParsekLog.Verbose(Tag, FormatItemLine(d));
            }
            if (slotBlocked > 0)
                ParsekLog.Verbose(Tag, "decorate screen=" + screen + " kind=" + StockUiDecorationKind.ContractSlot
                    + " blocked=" + slotBlocked.ToString(ic) + " marked=0 why=\"" + (slotWhy ?? "") + "\"");
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
