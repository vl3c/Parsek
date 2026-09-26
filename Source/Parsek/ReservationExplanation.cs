using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek
{
    /// <summary>
    /// The text that explains one reservation: a short <see cref="Title"/> (a status in the
    /// Kerbals window's words, for a label or a tooltip header) and a <see cref="Body"/>
    /// made of the fact, the append-only rule and when the item frees up. The body is what
    /// both the hover and the refused-click dialog show, so they say the same thing.
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
        /// <summary>True when the holding flight's chain has a looping segment.</summary>
        internal bool IsLooping;
        /// <summary>The holding flight's recorded end, or NaN.</summary>
        internal double FlightEndUT;
        /// <summary>The slot owner the kerbal is reserved for, or null / his own name.</summary>
        internal string SlotOwner;
        /// <summary>The reserved kerbal.</summary>
        internal string KerbalName;
    }

    /// <summary>
    /// One pure builder per reservation kind (docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md,
    /// section 6): the fact, the rule that committed history is fixed, and WHEN the item
    /// frees up. There is no un-commit path to offer (owner ruling D2), so the way out is a
    /// date or, for kerbals, the event that ends the hold. Dates go through an injectable
    /// formatter; production passes <see cref="DefaultDateFormatter"/>
    /// (<c>KerbalsWindowUI.FormatRowDate</c>, i.e. <c>KSPUtil.PrintDateCompact</c>).
    /// Crew wording follows the Kerbals window (one crew vocabulary).
    /// </summary>
    internal static class ReservationExplanation
    {
        /// <summary>The append-only rule every non-crew kind states.</summary>
        internal const string TimelineRule =
            "Parsek's timeline is fixed once committed, so this cannot happen earlier or twice.";

        /// <summary>The rule a kerbal held by a committed flight states.</summary>
        internal const string CrewRule =
            "A kerbal on a committed flight cannot be used or risked before it ends.";

        /// <summary>The rule a kerbal lost on a committed flight states.</summary>
        internal const string LostRule = "That flight is fixed history.";

        /// <summary>The production date formatter.</summary>
        internal static readonly Func<double, string> DefaultDateFormatter = ut => KerbalsWindowUI.FormatRowDate(ut);

        /// <summary>A calendar date; invariant whole seconds only when no formatter is given.</summary>
        internal static string FormatDate(double ut, Func<double, string> formatDate)
        {
            string text = formatDate != null ? formatDate(ut) : null;
            if (!string.IsNullOrEmpty(text)) return text;
            return "UT " + ut.ToString("F0", CultureInfo.InvariantCulture);
        }

        /// <summary><c>by the committed flight 'X'</c> when the row has a flight name,
        /// else <c>on your committed timeline</c>.</summary>
        internal static string SourcePhrase(string recordingName)
        {
            return string.IsNullOrEmpty(recordingName)
                ? "on your committed timeline"
                : "by the committed flight '" + recordingName + "'";
        }

        /// <summary>
        /// A part entry purchase the committed timeline makes later (P1): the greyed
        /// purchase button's tooltip text and every purchase refusal read this text.
        /// </summary>
        internal static ReservationText PartPurchase(CommittedFutureEntry entry, Func<double, string> formatDate)
        {
            string date = FormatDate(entry != null ? entry.UT : 0.0, formatDate);
            return new ReservationText
            {
                Title = "Purchased on " + date,
                Fact = "Purchased on " + date + " " + SourcePhrase(entry?.RecordingName) + ".",
                Rule = TimelineRule,
                WayOut = "It becomes available on that date."
            };
        }

        internal static ReservationText Tech(CommittedFutureEntry entry, Func<double, string> formatDate)
        {
            string date = FormatDate(entry != null ? entry.UT : 0.0, formatDate);
            return new ReservationText
            {
                Title = "Researched on " + date,
                Fact = "Researched on " + date + " " + SourcePhrase(entry?.RecordingName) + ".",
                Rule = TimelineRule,
                WayOut = "It unlocks on that date."
            };
        }

        /// <summary>The accept explanation; the Decline refusal and the Mission Control
        /// detail panel read the same text.</summary>
        internal static ReservationText ContractAccept(CommittedFutureEntry entry, Func<double, string> formatDate)
        {
            string date = FormatDate(entry != null ? entry.UT : 0.0, formatDate);
            return new ReservationText
            {
                Title = "Accepted on " + date,
                Fact = "Accepted on " + date + " " + SourcePhrase(entry?.RecordingName) + ".",
                Rule = TimelineRule,
                WayOut = "It becomes active on that date."
            };
        }

        /// <summary>
        /// The explanation for an Active contract the committed timeline resolves later
        /// (owner ruling D7, section 7.3): the Cancel refusal, the Mission Control detail
        /// panel and the Active-row label read the same text. The verb follows the
        /// committed row's kind (completes / fails / cancelled, the Timeline's contract
        /// verbs); any other kind reads as a completion.
        /// </summary>
        internal static ReservationText ContractResolution(CommittedFutureEntry entry, Func<double, string> formatDate)
        {
            string date = FormatDate(entry != null ? entry.UT : 0.0, formatDate);
            string verb;
            string wayOut;
            switch (entry != null ? entry.Kind : CommittedFutureKind.ContractComplete)
            {
                case CommittedFutureKind.ContractFail:
                    verb = "Fails";
                    wayOut = "It fails and frees its slot on that date.";
                    break;
                case CommittedFutureKind.ContractCancel:
                    verb = "Cancelled";
                    wayOut = "It is cancelled and frees its slot on that date.";
                    break;
                default:
                    verb = "Completes";
                    wayOut = "It completes and frees its slot on that date.";
                    break;
            }
            return new ReservationText
            {
                Title = verb + " on " + date,
                Fact = verb + " on " + date + " " + SourcePhrase(entry?.RecordingName) + ".",
                Rule = TimelineRule,
                WayOut = wayOut
            };
        }

        /// <summary>The way out of a slot-refused accept.</summary>
        internal const string ContractSlotWayOut = "A slot frees when one of your active contracts ends.";

        /// <summary>
        /// Accepting a contract now would leave no Mission Control slot for a committed
        /// accept (section 4 "C2"). <paramref name="starvedAccept"/> is the earliest
        /// committed accept that would find no slot; its flight, contract title and agent are
        /// named when the row carries them (several offers can share one title, and the
        /// agent is what Mission Control shows beside each). The Mission Control detail
        /// panel, the Accept backstop and Contract Configurator's refusal all read this text.
        /// </summary>
        internal static ReservationText ContractSlot(CommittedFutureEntry starvedAccept, Func<double, string> formatDate)
        {
            string date = FormatDate(starvedAccept != null ? starvedAccept.UT : 0.0, formatDate);
            string who;
            if (starvedAccept != null && starvedAccept.RecordingName != null)
                who = "The committed flight '" + starvedAccept.RecordingName + "'";
            else if (starvedAccept != null && starvedAccept.RecordingId == null)
                who = "Your committed timeline";
            else
                who = "A committed flight";
            string what = starvedAccept != null && starvedAccept.Title != null
                ? "the contract '" + starvedAccept.Title + "'"
                : "a contract";
            if (starvedAccept != null && starvedAccept.AgentTitle != null)
                what += " from " + starvedAccept.AgentTitle;
            return new ReservationText
            {
                Title = "Slot needed on " + date,
                Fact = who + " accepts " + what + " on " + date + " and needs this slot.",
                Rule = TimelineRule,
                WayOut = ContractSlotWayOut
            };
        }

        /// <summary>
        /// The facility-upgrade explanation over every committed upgrade of the facility
        /// still ahead (UT ascending). The block lifts once the clock passes the last one,
        /// so every date is listed.
        /// </summary>
        internal static ReservationText FacilityUpgrade(
            IReadOnlyList<CommittedFutureEntry> futureUpgrades, Func<double, string> formatDate)
        {
            if (futureUpgrades == null || futureUpgrades.Count == 0)
            {
                return new ReservationText
                {
                    Title = "Upgraded on your committed timeline",
                    Fact = "Upgraded on your committed timeline.",
                    Rule = TimelineRule,
                    WayOut = null
                };
            }

            var first = futureUpgrades[0];
            string firstDate = FormatDate(first.UT, formatDate);
            if (futureUpgrades.Count == 1)
            {
                return new ReservationText
                {
                    Title = "Upgraded on " + firstDate,
                    Fact = "Upgraded" + LevelPhrase(first) + " on " + firstDate + " "
                           + SourcePhrase(first.RecordingName) + ".",
                    Rule = TimelineRule,
                    WayOut = "The upgrade happens on that date."
                };
            }

            // Several committed upgrades: name the flight only when one flight made them all.
            string sharedName = first.RecordingName;
            for (int i = 1; i < futureUpgrades.Count; i++)
            {
                if (!string.Equals(futureUpgrades[i].RecordingName, sharedName, StringComparison.Ordinal))
                {
                    sharedName = null;
                    break;
                }
            }

            var parts = new List<string>();
            for (int i = 0; i < futureUpgrades.Count; i++)
                parts.Add(LevelPhrase(futureUpgrades[i]).TrimStart() + " on " + FormatDate(futureUpgrades[i].UT, formatDate));
            string joined = parts.Count == 2
                ? parts[0] + " and " + parts[1]
                : string.Join(", ", parts.GetRange(0, parts.Count - 1).ToArray()) + " and " + parts[parts.Count - 1];

            return new ReservationText
            {
                Title = "Upgraded on " + firstDate,
                Fact = "Upgraded " + joined + " " + SourcePhrase(sharedName) + ".",
                Rule = TimelineRule,
                WayOut = "The upgrades happen on those dates."
            };
        }

        private static string LevelPhrase(CommittedFutureEntry entry)
        {
            return entry != null && entry.FacilityToLevel > 0
                ? " to level " + entry.FacilityToLevel.ToString(CultureInfo.InvariantCulture)
                : "";
        }

        /// <summary>
        /// Activating a strategy the committed timeline activates later. The Administration
        /// reason field and the refused-click dialog both show this.
        /// </summary>
        internal static ReservationText StrategyActivation(CommittedFutureEntry entry, Func<double, string> formatDate)
        {
            string date = FormatDate(entry != null ? entry.UT : 0.0, formatDate);
            return new ReservationText
            {
                Title = "Activated on " + date,
                Fact = "Activated on " + date + " " + SourcePhrase(entry?.RecordingName) + ".",
                Rule = TimelineRule,
                WayOut = "It becomes active on that date."
            };
        }

        /// <summary>
        /// Activating a strategy now would leave no free slot for a committed activation.
        /// <paramref name="committedTitle"/> names the strategy that activation switches on,
        /// or null when it is unknown.
        /// </summary>
        internal static ReservationText StrategySlot(
            CommittedFutureEntry entry, string committedTitle, Func<double, string> formatDate)
        {
            string date = FormatDate(entry != null ? entry.UT : 0.0, formatDate);
            string what = string.IsNullOrEmpty(committedTitle)
                ? "A committed activation"
                : "A committed activation of '" + committedTitle + "'";
            return new ReservationText
            {
                Title = "Slot needed on " + date,
                Fact = what + " on " + date + " needs this slot.",
                Rule = TimelineRule,
                WayOut = "A slot frees when one of your active strategies ends."
            };
        }

        /// <summary>
        /// Activating a strategy now would make stock's conflict rule refuse a committed
        /// activation of another strategy. The way-out line is given only when the committed
        /// timeline also deactivates that strategy (<paramref name="conflictEnd"/>); otherwise
        /// nothing honest can be said about when it frees.
        /// </summary>
        internal static ReservationText StrategyConflict(
            CommittedFutureEntry committedActivation, CommittedFutureEntry conflictEnd,
            string otherTitle, Func<double, string> formatDate)
        {
            string date = FormatDate(committedActivation != null ? committedActivation.UT : 0.0, formatDate);
            string name = !string.IsNullOrEmpty(otherTitle)
                ? otherTitle
                : (committedActivation != null ? committedActivation.Key : "another strategy");
            return new ReservationText
            {
                Title = "Conflicts with '" + name + "'",
                Fact = "Conflicts with '" + name + "', which is activated on " + date + " "
                       + SourcePhrase(committedActivation?.RecordingName) + ".",
                Rule = TimelineRule,
                WayOut = conflictEnd != null
                    ? "The conflict ends when '" + name + "' is deactivated on "
                      + FormatDate(conflictEnd.UT, formatDate) + "."
                    : null
            };
        }

        /// <summary>
        /// Deactivating a strategy the committed timeline changes later.
        /// <paramref name="entry"/> is the strategy's earliest committed row still ahead:
        /// a deactivation (it stays active until then) or a re-activation.
        /// </summary>
        internal static ReservationText StrategyDeactivation(CommittedFutureEntry entry, Func<double, string> formatDate)
        {
            string date = FormatDate(entry != null ? entry.UT : 0.0, formatDate);
            string source = SourcePhrase(entry?.RecordingName);
            if (entry != null && entry.Kind == CommittedFutureKind.StrategyActivate)
            {
                return new ReservationText
                {
                    Title = "Activated again on " + date,
                    Fact = "Activated again on " + date + " " + source + ".",
                    Rule = TimelineRule,
                    WayOut = "It can be deactivated after that date."
                };
            }
            return new ReservationText
            {
                Title = "Deactivated on " + date,
                Fact = "Deactivated on " + date + " " + source + ".",
                Rule = TimelineRule,
                WayOut = "It stays active until that date."
            };
        }

        internal static ReservationText KerbalHire(CommittedFutureEntry entry, Func<double, string> formatDate)
        {
            string date = FormatDate(entry != null ? entry.UT : 0.0, formatDate);
            return new ReservationText
            {
                Title = "Hired on " + date,
                Fact = "Hired on " + date + " " + SourcePhrase(entry?.RecordingName) + ".",
                Rule = TimelineRule,
                WayOut = "They join the roster on that date."
            };
        }

        /// <summary>
        /// A committed dismissal (<c>CrewRemoved</c>). Worded "Dismissed", not "Retired":
        /// the Kerbals window's <c>Retired</c> is a stand-in whose seat went back to its
        /// owner, a different state.
        /// </summary>
        internal static ReservationText KerbalRetire(CommittedFutureEntry entry, Func<double, string> formatDate)
        {
            string date = FormatDate(entry != null ? entry.UT : 0.0, formatDate);
            return new ReservationText
            {
                Title = "Dismissed on " + date,
                Fact = "Dismissed on " + date + " " + SourcePhrase(entry?.RecordingName) + ".",
                Rule = TimelineRule,
                WayOut = "They leave the roster on that date."
            };
        }

        /// <summary>
        /// A kerbal a committed flight holds. The way out is when the hold ends:
        /// <list type="bullet">
        /// <item>a finite hold (a Recovered flight, or an aboard hold bounded by a
        /// recovery): <c>Free after &lt;date&gt;.</c></item>
        /// <item>a Recovered flight in a looping chain (open-ended only because of the
        /// loop): stopping the loop releases it on the next ledger walk
        /// (<c>LoopHold_TurningLoopOff_ReleasesARecoveredHoldOnTheNextWalk</c>).</item>
        /// <item>an aboard / unknown flight in a looping chain: the loop keeps it, and after
        /// the loop stops only a recovery ends it
        /// (<c>LoopHold_TurningLoopOff_LeavesAnAboardHoldOpenEnded</c>).</item>
        /// <item>an aboard / unknown flight: <c>Free once 'X' is recovered.</c></item>
        /// </list>
        /// </summary>
        internal static ReservationText KerbalOnFlight(KerbalHold hold, Func<double, string> formatDate)
        {
            bool forSomeoneElse = !string.IsNullOrEmpty(hold.SlotOwner)
                && !string.Equals(hold.SlotOwner, hold.KerbalName, StringComparison.Ordinal);
            bool finite = !double.IsNaN(hold.ReleaseUT) && !double.IsInfinity(hold.ReleaseUT);
            string flightRef = string.IsNullOrEmpty(hold.FlightName) ? null : "'" + hold.FlightName + "'";

            string title;
            if (forSomeoneElse) title = "Reserved for " + hold.SlotOwner;
            else if (finite) title = "Reserved until " + FormatDate(hold.ReleaseUT, formatDate);
            else title = "Reserved";

            string seat = forSomeoneElse ? " in " + hold.SlotOwner + "'s seat" : "";
            string fact = flightRef != null
                ? "Flies " + flightRef + seat + " on your committed timeline."
                : "Flies a committed flight" + seat + " on your timeline.";

            string wayOut;
            string subject = flightRef ?? "that flight";
            // A Recovered flight's hold is finite unless its chain loops, so an open-ended
            // Recovered hold is a loop hold even when the loop flag was turned off after
            // the last ledger walk.
            bool loopHeld = !finite
                && (hold.IsLooping || hold.EndState == KerbalEndState.Recovered);
            if (finite)
            {
                wayOut = "Free after " + FormatDate(hold.ReleaseUT, formatDate) + ".";
            }
            else if (loopHeld && hold.EndState == KerbalEndState.Recovered)
            {
                bool endKnown = !double.IsNaN(hold.FlightEndUT) && !double.IsInfinity(hold.FlightEndUT);
                wayOut = "Held while " + subject + " loops. Stopping its loop frees them "
                         + (endKnown ? "after " + FormatDate(hold.FlightEndUT, formatDate) : "when it ends")
                         + ".";
            }
            else if (loopHeld)
            {
                wayOut = "Held while " + subject + " loops, and then until it is recovered.";
            }
            else
            {
                wayOut = flightRef != null
                    ? "Free once " + flightRef + " is recovered."
                    : "Free once that flight's vessel is recovered.";
            }

            return new ReservationText
            {
                Title = title,
                Fact = fact,
                Rule = CrewRule,
                WayOut = wayOut
            };
        }

        /// <summary>
        /// A retired stand-in (the Kerbals window's <c>Retired</c>): he stood in for a
        /// reserved owner on a committed flight, and that owner is free again
        /// (<c>KerbalsModule.ComputeRetiredSet</c>: displaced, flew a committed recording,
        /// not reserved now). <c>KerbalsModule.ShouldFilterFromCrewDialog</c> keeps him off
        /// new crews. No player action is known to bring him back, so there is no way-out
        /// sentence rather than an invented one.
        /// </summary>
        internal static ReservationText KerbalRetiredStandIn(string slotOwner)
        {
            bool hasOwner = !string.IsNullOrEmpty(slotOwner);
            return new ReservationText
            {
                Title = "Retired",
                Fact = hasOwner
                    ? "Stood in for " + slotOwner + " on a committed flight."
                    : "Stood in for a reserved kerbal on a committed flight.",
                Rule = (hasOwner ? slotOwner + " is" : "That kerbal is")
                       + " free again, so Parsek has retired this stand-in and they cannot join a new crew."
            };
        }

        /// <summary>
        /// A kerbal a committed flight killed. The way back is the Kerbals window's own
        /// (<see cref="KerbalsPresentation.LostReFlyRemedy"/>): a Re-Fly merge tombstones
        /// the death row, which is what releases the permanent reservation.
        /// </summary>
        internal static ReservationText KerbalLost(string flightName)
        {
            return new ReservationText
            {
                Title = "Lost",
                Fact = string.IsNullOrEmpty(flightName)
                    ? "Lost on a committed flight."
                    : "Lost on the committed flight '" + flightName + "'.",
                Rule = LostRule,
                WayOut = KerbalsPresentation.LostReFlyRemedy
            };
        }
    }
}
