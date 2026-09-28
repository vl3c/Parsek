using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek
{
    /// <summary>
    /// The text that explains one reservation: a short <see cref="Title"/> (a status in the
    /// Kerbals window's words, for a label or a tooltip header) and a <see cref="Body"/>, the
    /// one sentence both the hover and the refused-click dialog show, so they say the same
    /// thing. Since the owner's 2026-09-27 wording the whole sentence is the
    /// <see cref="Fact"/>; <see cref="Rule"/> and <see cref="WayOut"/> are kept for the
    /// composition only and are empty for every builder.
    /// </summary>
    internal struct ReservationText
    {
        internal string Title;
        internal string Fact;
        internal string Rule;
        internal string WayOut;

        /// <summary>Fact, rule and way out, space-joined; empty parts are skipped.</summary>
        internal string Body
        {
            get
            {
                var sb = new StringBuilder();
                Append(sb, Fact);
                Append(sb, Rule);
                Append(sb, WayOut);
                return sb.ToString();
            }
        }

        private static void Append(StringBuilder sb, string part)
        {
            if (string.IsNullOrEmpty(part)) return;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(part);
        }
    }

    /// <summary>
    /// What a reserved kerbal's explanation needs to know about the flight that holds him.
    /// </summary>
    internal struct KerbalHold
    {
        /// <summary>The holding flight's display name, or null.</summary>
        internal string FlightName;
        /// <summary>How the holding flight ends for this kerbal.</summary>
        internal KerbalEndState EndState;
        /// <summary>The reservation's end (<c>KerbalReservation.ReservedUntilUT</c>);
        /// +inf for an open-ended hold.</summary>
        internal double ReleaseUT;
        /// <summary>The slot owner the kerbal is reserved for, or null / his own name.</summary>
        internal string SlotOwner;
        /// <summary>The reserved kerbal.</summary>
        internal string KerbalName;
    }

    /// <summary>
    /// One pure builder per reservation kind
    /// (docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md, section 6). Owner
    /// wording of 2026-09-27: the sentence starts with the participle, then the exact date
    /// and time (the compact date with hour and minute), then "blocked by timeline until
    /// then."; a kerbal reads "Reserved by timeline for 'Flight' until DATE.". No
    /// append-only rule sentence, no way-out sentence, and never "committed", "your
    /// timeline" or "the timeline" (the committed flight's name may appear). Dates go
    /// through an injectable formatter; production passes <see cref="DefaultDateFormatter"/>
    /// (<c>KerbalsWindowUI.FormatRowDate</c>, i.e. <c>KSPUtil.PrintDateCompact</c> with the
    /// time of day). Crew wording follows the Kerbals window (one crew vocabulary).
    /// </summary>
    internal static class ReservationExplanation
    {
        /// <summary>The closing clause of every dated block (a marker the in-game cells read).</summary>
        internal const string BlockedByTimeline = "blocked by timeline";

        /// <summary>The closing clause of a dated block: the item happens on that date.</summary>
        internal const string BlockedUntilThen = ", " + BlockedByTimeline + " until then.";

        /// <summary>The closing clause of a block with no end date to name.</summary>
        internal const string BlockedEnd = ", " + BlockedByTimeline + ".";

        /// <summary>The opening of every held-kerbal sentence (a marker the in-game cells read).</summary>
        internal const string ReservedByTimeline = "Reserved by timeline";

        /// <summary>The production date formatter.</summary>
        internal static readonly Func<double, string> DefaultDateFormatter = ut => KerbalsWindowUI.FormatRowDate(ut);

        /// <summary>A calendar date; invariant whole seconds only when no formatter is given.</summary>
        internal static string FormatDate(double ut, Func<double, string> formatDate)
        {
            string text = formatDate != null ? formatDate(ut) : null;
            if (!string.IsNullOrEmpty(text)) return text;
            return "UT " + ut.ToString("F0", CultureInfo.InvariantCulture);
        }

        /// <summary><c>&lt;Participle&gt; on &lt;date&gt;, blocked by timeline until then.</c>
        /// with the title <c>&lt;Participle&gt; on &lt;date&gt;</c>.</summary>
        private static ReservationText Dated(string participle, double ut, Func<double, string> formatDate)
        {
            string head = participle + " on " + FormatDate(ut, formatDate);
            return new ReservationText { Title = head, Fact = head + BlockedUntilThen };
        }

        /// <summary>
        /// The Timeline row hover's hold sentence for a FUTURE ledger row that holds a stock
        /// control: <c>Holds &lt;control&gt; until &lt;row date&gt;.</c>, or null when the row is
        /// not ahead of <paramref name="currentUT"/> or holds nothing. Every "holds" answer is
        /// the click-block predicate itself over the same <see cref="CommittedFutureIndex"/>
        /// the blocks read (<see cref="StockUiReservationPredicates"/>,
        /// <see cref="StrategyReservationPredicates"/>), so the Timeline never names a hold
        /// stock does not enforce. The row's own date is repeated because the first future
        /// row's time column shows a countdown, not a date. A part purchase depends on live
        /// stock state (difficulty, already purchased), so the caller passes the live
        /// decision as <paramref name="partPurchaseBlocked"/>; null reads no part hold. A
        /// strategy's Administration buttons depend on whether stock has it active now
        /// (<paramref name="strategyActiveNow"/>): an inactive strategy offers only Activate,
        /// an active one only Deactivate; null reads it as unknown, which names Activate for
        /// an activation row and Deactivate for a deactivation row. A facility repair's
        /// block depends on the building being destroyed NOW, which the row cannot know, so
        /// a repair row never claims one.
        /// </summary>
        internal static string ForTimelineRow(
            TimelineEntry entry, CommittedFutureIndex index, double currentUT,
            Func<double, string> formatDate, Func<string, bool> partPurchaseBlocked = null,
            Func<string, bool> strategyActiveNow = null)
        {
            if (entry == null || entry.Action == null || index == null) return null;
            if (!CommittedFutureIndex.IsFuture(entry.UT, currentUT)) return null;
            CommittedFutureKind kind;
            string key;
            if (!CommittedFutureIndex.TryClassify(entry.Action, out kind, out key)) return null;
            string control = TimelineHeldControl(
                kind, key, index, currentUT, partPurchaseBlocked, strategyActiveNow);
            if (control == null) return null;
            return TimelineHoldsPrefix + control + " until " + FormatDate(entry.UT, formatDate) + ".";
        }

        /// <summary>The opening of every Timeline hold sentence.</summary>
        internal const string TimelineHoldsPrefix = "Holds ";

        /// <summary>The stock control a future row of (kind, key) holds now, or null.</summary>
        internal static string TimelineHeldControl(
            CommittedFutureKind kind, string key, CommittedFutureIndex index, double currentUT,
            Func<string, bool> partPurchaseBlocked, Func<string, bool> strategyActiveNow = null)
        {
            switch (kind)
            {
                case CommittedFutureKind.TechResearch:
                    return StockUiReservationPredicates.IsTechResearchBlocked(index, key, currentUT)
                        ? "Research in R&D" : null;
                case CommittedFutureKind.ContractAccept:
                    return StockUiReservationPredicates.IsContractAcceptBlocked(index, key, currentUT)
                        ? "Accept and Decline in Mission Control" : null;
                case CommittedFutureKind.ContractComplete:
                case CommittedFutureKind.ContractFail:
                case CommittedFutureKind.ContractCancel:
                    return StockUiReservationPredicates.IsContractCancelBlocked(index, key, currentUT)
                        ? "Cancel in Mission Control" : null;
                case CommittedFutureKind.FacilityUpgrade:
                    return StockUiReservationPredicates.IsFacilityUpgradeBlocked(index, key, currentUT)
                        ? "Upgrade on this facility" : null;
                case CommittedFutureKind.KerbalHire:
                    return StockUiReservationPredicates.IsKerbalHireBlocked(index, key, currentUT)
                        ? "Hire in the Astronaut Complex" : null;
                case CommittedFutureKind.StrategyActivate:
                {
                    // Stock offers Activate on an inactive strategy and Deactivate on an
                    // active one, so the hold names the button the player can see: an active
                    // strategy's refused Deactivate (any future row of it raises that), else
                    // EvaluateActivation's first refusal (FutureActivation).
                    if (index == null
                        || index.FirstFuture(CommittedFutureKind.StrategyActivate, key, currentUT) == null)
                        return null;
                    bool active = strategyActiveNow != null && strategyActiveNow(key);
                    if (active)
                        return StrategyReservationPredicates.IsDeactivationBlocked(index, key, currentUT)
                            ? "Deactivate in Administration" : null;
                    return "Activate in Administration";
                }
                case CommittedFutureKind.StrategyDeactivate:
                    // Deactivate is refused only on the player path, and only an active
                    // strategy offers it; a strategy known inactive now holds nothing here.
                    if (strategyActiveNow != null && !strategyActiveNow(key)) return null;
                    return StrategyReservationPredicates.IsDeactivationBlocked(index, key, currentUT)
                        ? "Deactivate in Administration" : null;
                case CommittedFutureKind.PartPurchase:
                    return partPurchaseBlocked != null && partPurchaseBlocked(key)
                        ? "Purchase for this part" : null;
                default:
                    return null;
            }
        }

        /// <summary>A part entry purchase timeline makes later (P1).</summary>
        internal static ReservationText PartPurchase(CommittedFutureEntry entry, Func<double, string> formatDate)
        {
            return Dated("Purchased", entry != null ? entry.UT : 0.0, formatDate);
        }

        internal static ReservationText Tech(CommittedFutureEntry entry, Func<double, string> formatDate)
        {
            return Dated("Researched", entry != null ? entry.UT : 0.0, formatDate);
        }

        /// <summary>The accept explanation; the Decline refusal and the Mission Control
        /// detail panel read the same text.</summary>
        internal static ReservationText ContractAccept(CommittedFutureEntry entry, Func<double, string> formatDate)
        {
            return Dated("Accepted", entry != null ? entry.UT : 0.0, formatDate);
        }

        /// <summary>
        /// An Active contract timeline resolves later (owner ruling D7, section 7.3): the
        /// Cancel refusal, the Mission Control detail panel and the Active-row label read the
        /// same text. The participle follows the row's kind: Completed / Failed / Cancelled,
        /// and Expired for a fail that is the deadline running out (the Timeline's
        /// "Expired"); any other kind reads as a completion.
        /// </summary>
        internal static ReservationText ContractResolution(CommittedFutureEntry entry, Func<double, string> formatDate)
        {
            string participle;
            switch (entry != null ? entry.Kind : CommittedFutureKind.ContractComplete)
            {
                case CommittedFutureKind.ContractFail:
                    participle = entry.DeadlineExpiry ? "Expired" : "Failed";
                    break;
                case CommittedFutureKind.ContractCancel:
                    participle = "Cancelled";
                    break;
                default:
                    participle = "Completed";
                    break;
            }
            return Dated(participle, entry != null ? entry.UT : 0.0, formatDate);
        }

        /// <summary>
        /// Accepting a contract now would leave no Mission Control slot for a contract
        /// timeline accepts later (section 4 "C2"). <paramref name="starvedAccept"/> is the
        /// earliest such accept; its title and agent are named when the row carries them
        /// (several offers can share one title, and the agent is what Mission Control shows
        /// beside each). The detail panel, the Accept backstop and Contract Configurator's
        /// refusal all read this text.
        /// </summary>
        internal static ReservationText ContractSlot(CommittedFutureEntry starvedAccept, Func<double, string> formatDate)
        {
            string date = FormatDate(starvedAccept != null ? starvedAccept.UT : 0.0, formatDate);
            string what = starvedAccept != null && starvedAccept.Title != null
                ? "'" + starvedAccept.Title + "'"
                : "a contract";
            if (starvedAccept != null && starvedAccept.AgentTitle != null)
                what += " (" + starvedAccept.AgentTitle + ")";
            return new ReservationText
            {
                Title = "Slot needed from " + date,
                Fact = "Slot needed from " + date + " for " + what + BlockedEnd
            };
        }

        /// <summary>
        /// The facility-upgrade explanation over every upgrade of the facility timeline makes
        /// later (UT ascending). The block lifts once the clock passes the last one, so every
        /// date is listed.
        /// </summary>
        internal static ReservationText FacilityUpgrade(
            IReadOnlyList<CommittedFutureEntry> futureUpgrades, Func<double, string> formatDate)
        {
            if (futureUpgrades == null || futureUpgrades.Count == 0)
                return new ReservationText { Title = "Upgraded later", Fact = "Upgraded later" + BlockedEnd };

            var first = futureUpgrades[0];
            string firstDate = FormatDate(first.UT, formatDate);
            var parts = new List<string>();
            for (int i = 0; i < futureUpgrades.Count; i++)
                parts.Add(LevelPhrase(futureUpgrades[i]) + "on " + FormatDate(futureUpgrades[i].UT, formatDate));
            return new ReservationText
            {
                Title = "Upgraded on " + firstDate,
                Fact = "Upgraded " + JoinAnd(parts) + BlockedUntilThen
            };
        }

        /// <summary>
        /// The facility-repair explanation over the repairs timeline makes later that already
        /// cover the facility's current destruction (UT ascending, one per destroyed building).
        /// Stock repairs a facility's buildings together, so the rows usually share one date;
        /// distinct dates are all listed, because the block lifts only once the clock passes
        /// the last one.
        /// </summary>
        internal static ReservationText FacilityRepair(
            IReadOnlyList<CommittedFutureEntry> coveringRepairs, Func<double, string> formatDate)
        {
            if (coveringRepairs == null || coveringRepairs.Count == 0)
                return new ReservationText { Title = "Repaired later", Fact = "Repaired later" + BlockedEnd };

            var dates = new List<string>();
            for (int i = 0; i < coveringRepairs.Count; i++)
            {
                string date = FormatDate(coveringRepairs[i].UT, formatDate);
                if (!dates.Contains(date)) dates.Add(date);
            }
            return new ReservationText
            {
                Title = "Repaired on " + dates[0],
                Fact = "Repaired on " + JoinAnd(dates) + BlockedUntilThen
            };
        }

        private static string LevelPhrase(CommittedFutureEntry entry)
        {
            return entry != null && entry.FacilityToLevel > 0
                ? "to level " + entry.FacilityToLevel.ToString(CultureInfo.InvariantCulture) + " "
                : "";
        }

        private static string JoinAnd(List<string> parts)
        {
            if (parts.Count == 1) return parts[0];
            if (parts.Count == 2) return parts[0] + " and " + parts[1];
            return string.Join(", ", parts.GetRange(0, parts.Count - 1).ToArray()) + " and " + parts[parts.Count - 1];
        }

        /// <summary>
        /// Activating a strategy timeline activates later. The Administration reason field
        /// and the refused-click dialog both show this; when stock itself refuses, this
        /// sentence follows stock's reason.
        /// </summary>
        internal static ReservationText StrategyActivation(CommittedFutureEntry entry, Func<double, string> formatDate)
        {
            return Dated("Activated", entry != null ? entry.UT : 0.0, formatDate);
        }

        /// <summary>
        /// Activating a strategy now would leave no free slot for an activation timeline makes
        /// later. <paramref name="committedTitle"/> names the strategy that activation switches
        /// on, or null when it is unknown.
        /// </summary>
        internal static ReservationText StrategySlot(
            CommittedFutureEntry entry, string committedTitle, Func<double, string> formatDate)
        {
            string date = FormatDate(entry != null ? entry.UT : 0.0, formatDate);
            string what = string.IsNullOrEmpty(committedTitle) ? "a strategy" : "'" + committedTitle + "'";
            return new ReservationText
            {
                Title = "Slot needed from " + date,
                Fact = "Slot needed from " + date + " for " + what + BlockedEnd
            };
        }

        /// <summary>
        /// Activating a strategy now would make stock's conflict rule refuse an activation of
        /// another strategy timeline makes later. The end date is given only when timeline
        /// also deactivates that strategy (<paramref name="conflictEnd"/>).
        /// </summary>
        internal static ReservationText StrategyConflict(
            CommittedFutureEntry committedActivation, CommittedFutureEntry conflictEnd,
            string otherTitle, Func<double, string> formatDate)
        {
            string date = FormatDate(committedActivation != null ? committedActivation.UT : 0.0, formatDate);
            string name = !string.IsNullOrEmpty(otherTitle)
                ? otherTitle
                : (committedActivation != null ? committedActivation.Key : "another strategy");
            string span = conflictEnd != null
                ? " from " + date + " until " + FormatDate(conflictEnd.UT, formatDate)
                : " from " + date;
            return new ReservationText
            {
                Title = "Conflicts with '" + name + "'",
                Fact = "Conflicts with '" + name + "'" + span + BlockedEnd
            };
        }

        /// <summary>
        /// Deactivating a strategy timeline changes later. <paramref name="entry"/> is the
        /// strategy's earliest row still ahead: a deactivation (it stays active until then) or
        /// a re-activation.
        /// </summary>
        internal static ReservationText StrategyDeactivation(CommittedFutureEntry entry, Func<double, string> formatDate)
        {
            bool again = entry != null && entry.Kind == CommittedFutureKind.StrategyActivate;
            return Dated(again ? "Activated again" : "Deactivated", entry != null ? entry.UT : 0.0, formatDate);
        }

        internal static ReservationText KerbalHire(CommittedFutureEntry entry, Func<double, string> formatDate)
        {
            return Dated("Hired", entry != null ? entry.UT : 0.0, formatDate);
        }

        /// <summary>
        /// A dismissal timeline makes later (<c>CrewRemoved</c>). Worded "Dismissed", not
        /// "Retired": the Kerbals window's <c>Retired</c> is a stand-in whose seat went back
        /// to its owner, a different state.
        /// </summary>
        internal static ReservationText KerbalRetire(CommittedFutureEntry entry, Func<double, string> formatDate)
        {
            return Dated("Dismissed", entry != null ? entry.UT : 0.0, formatDate);
        }

        /// <summary>
        /// A kerbal a flight on timeline holds: <c>Reserved by timeline for 'X' until DATE.</c>
        /// A finite hold names its end; an open-ended one (aboard / unknown) ends when the
        /// flight's vessel is recovered. A stand-in held in another kerbal's seat names that
        /// seat. A looped segment never holds anyone open-ended: the loop is visual only
        /// (design 12.7, operator ruling 2026-09-27).
        /// </summary>
        internal static ReservationText KerbalOnFlight(KerbalHold hold, Func<double, string> formatDate)
        {
            bool forSomeoneElse = !string.IsNullOrEmpty(hold.SlotOwner)
                && !string.Equals(hold.SlotOwner, hold.KerbalName, StringComparison.Ordinal);
            bool finite = !double.IsNaN(hold.ReleaseUT) && !double.IsInfinity(hold.ReleaseUT);

            string title;
            if (forSomeoneElse) title = "Reserved for " + hold.SlotOwner;
            else if (finite) title = "Reserved until " + FormatDate(hold.ReleaseUT, formatDate);
            else title = "Reserved";

            var sb = new StringBuilder(ReservedByTimeline);
            if (!string.IsNullOrEmpty(hold.FlightName)) sb.Append(" for '").Append(hold.FlightName).Append('\'');
            if (forSomeoneElse) sb.Append(" in ").Append(hold.SlotOwner).Append("'s seat");
            if (finite)
                sb.Append(" until ").Append(FormatDate(hold.ReleaseUT, formatDate)).Append('.');
            else
                sb.Append(string.IsNullOrEmpty(hold.FlightName)
                    ? " until that flight is recovered."
                    : " until it is recovered.");

            return new ReservationText { Title = title, Fact = sb.ToString() };
        }

        /// <summary>
        /// An active stand-in covering the seat of a kerbal timeline holds (the Kerbals
        /// window's <c>Stand-in for &lt;owner&gt;</c>): <c>Standing in for OWNER, reserved by
        /// timeline for 'X'.</c> <paramref name="ownerFlightName"/> is the flight that holds
        /// the owner, or null.
        /// </summary>
        internal static string StandingIn(string owner, string ownerFlightName)
        {
            return "Standing in for " + owner + ", reserved by timeline"
                + (string.IsNullOrEmpty(ownerFlightName) ? "" : " for '" + ownerFlightName + "'")
                + ".";
        }

        /// <summary>
        /// A retired stand-in (the Kerbals window's <c>Retired</c>): he stood in for a
        /// reserved owner on a flight on timeline, and that owner is free again
        /// (<c>KerbalsModule.ComputeRetiredSet</c>). <c>KerbalsModule.ShouldFilterFromCrewDialog</c>
        /// keeps him off new crews.
        /// </summary>
        internal static ReservationText KerbalRetiredStandIn(string slotOwner)
        {
            bool hasOwner = !string.IsNullOrEmpty(slotOwner);
            return new ReservationText
            {
                Title = "Retired",
                Fact = "Retired after standing in for " + (hasOwner ? slotOwner : "a reserved kerbal")
                       + ", kept off new crews by timeline."
            };
        }

        /// <summary>
        /// A kerbal a flight on timeline killed: <c>Lost on 'X' on DATE.</c> When the death's
        /// stock crew respawn is pending (owner ruling S8), the sentence names the day he is
        /// back (<paramref name="respawnUT"/>, NaN for a permanent death). NaN
        /// <paramref name="deathUT"/> or a null flight name drops that part.
        /// </summary>
        internal static ReservationText KerbalLost(
            string flightName, double deathUT = double.NaN, double respawnUT = double.NaN,
            Func<double, string> formatDate = null)
        {
            bool respawns = !double.IsNaN(respawnUT) && !double.IsInfinity(respawnUT);
            bool dated = !double.IsNaN(deathUT) && !double.IsInfinity(deathUT);
            string respawnDate = respawns ? FormatDate(respawnUT, formatDate) : null;
            var sb = new StringBuilder("Lost");
            if (!string.IsNullOrEmpty(flightName)) sb.Append(" on '").Append(flightName).Append('\'');
            if (dated) sb.Append(" on ").Append(FormatDate(deathUT, formatDate));
            if (respawns) sb.Append(", back on ").Append(respawnDate);
            sb.Append('.');
            return new ReservationText
            {
                Title = respawns ? "Lost until " + respawnDate : "Lost",
                Fact = sb.ToString()
            };
        }
    }
}
