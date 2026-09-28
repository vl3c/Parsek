using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek
{
    /// <summary>
    /// Pure builders for the Timeline's row hover: the text the window's bottom help strip
    /// shows while the mouse is over a row's description. One builder per row kind; the
    /// window composes them (<see cref="Compose"/>) in the order not-counted reason, stock
    /// hold (<see cref="ReservationExplanation.ForTimelineRow"/>), row details. Only facts
    /// the model already holds are named; a missing fact drops its sentence. Every builder
    /// returns null when it has nothing to say, so a row with no facts keeps no hover.
    /// Wording follows the reservation text: "timeline" as a name, never "the timeline",
    /// "your timeline" or "committed".
    /// </summary>
    internal static class TimelineRowHover
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>The stock surname KSP gives generated kerbals; dropped on the crew list.</summary>
        internal const string StockSurnameSuffix = " Kerman";

        /// <summary>How many crew the launch hover names before "+N".</summary>
        internal const int CrewNameCap = 3;

        /// <summary>
        /// Why the walk left a greyed row not counted, as one sentence, or null for
        /// <see cref="GameActionNotCountedReason.None"/> (the row gives no reason to name).
        /// </summary>
        internal static string NotCounted(GameActionNotCountedReason reason)
        {
            switch (reason)
            {
                case GameActionNotCountedReason.ContractAlreadyCompleted:
                    return "Not counted: already completed earlier on timeline, so no reward was paid.";
                case GameActionNotCountedReason.ContractDeadlinePassed:
                    return "Not counted: the deadline had already passed, so no reward was paid.";
                case GameActionNotCountedReason.ContractAlreadyResolved:
                    return "Not counted: already failed or cancelled earlier on timeline, so no reward was paid.";
                case GameActionNotCountedReason.ContractOutcomeAfterEnd:
                    return "Not counted: the contract had already ended earlier on timeline, so no penalty was charged.";
                case GameActionNotCountedReason.MilestoneAlreadyAchieved:
                    return "Not counted: already achieved earlier on timeline, so no reward was paid.";
                case GameActionNotCountedReason.FacilityAlreadyIntact:
                    return "Not counted: already repaired earlier on timeline, so nothing was charged.";
                default:
                    return null;
            }
        }

        /// <summary>
        /// A ContractAccept row's terms: <c>Deadline DATE. Advance A, reward F funds + R rep.
        /// Agent: NAME.</c> The advance and deadline come from the accept row; the rewards
        /// from the contract's completion row when the ledger has one
        /// (<paramref name="pairedComplete"/>), else they are not named.
        /// </summary>
        internal static string ContractAccept(
            GameAction accept, GameAction pairedComplete, string agentTitle, Func<double, string> formatDate)
        {
            if (accept == null) return null;
            var sb = new StringBuilder();

            double deadline = accept.DeadlineUT;
            if (!double.IsNaN(deadline) && !double.IsInfinity(deadline) && deadline > 0.0)
                AppendSentence(sb, "Deadline " + ReservationExplanation.FormatDate(deadline, formatDate) + ".");

            string rewards = pairedComplete != null
                ? JoinRewards(pairedComplete.FundsReward, pairedComplete.RepReward, pairedComplete.ScienceReward, " + ")
                : null;
            bool hasAdvance = accept.AdvanceFunds > 0f;
            if (hasAdvance && rewards != null)
                AppendSentence(sb, "Advance " + FormatWhole(accept.AdvanceFunds) + ", reward " + rewards + ".");
            else if (hasAdvance)
                AppendSentence(sb, "Advance " + FormatWhole(accept.AdvanceFunds) + " funds.");
            else if (rewards != null)
                AppendSentence(sb, "Reward " + rewards + ".");

            if (!string.IsNullOrEmpty(agentTitle))
                AppendSentence(sb, "Agent: " + agentTitle + ".");

            return sb.Length > 0 ? sb.ToString() : null;
        }

        /// <summary>
        /// A contract end row (complete, fail, cancel): the flight that closed it, then what
        /// the row text does not already show. A completion's row shows its funds, so the
        /// hover adds <c>Also +R rep, +S sci.</c>; a fail or cancel adds its penalty.
        /// </summary>
        internal static string ContractOutcome(GameAction outcome, string flightName)
        {
            if (outcome == null) return null;
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(flightName))
                AppendSentence(sb, "By flight '" + flightName + "'.");

            switch (outcome.Type)
            {
                case GameActionType.ContractComplete:
                {
                    var extras = new List<string>();
                    if (outcome.RepReward > 0f) extras.Add("+" + FormatWhole(outcome.RepReward) + " rep");
                    if (outcome.ScienceReward > 0f) extras.Add("+" + FormatScience(outcome.ScienceReward) + " sci");
                    if (extras.Count > 0)
                        AppendSentence(sb, "Also " + string.Join(", ", extras.ToArray()) + ".");
                    break;
                }
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                {
                    var parts = new List<string>();
                    if (outcome.FundsPenalty > 0f) parts.Add(FormatWhole(outcome.FundsPenalty) + " funds");
                    if (outcome.RepPenalty > 0f) parts.Add(FormatWhole(outcome.RepPenalty) + " rep");
                    if (parts.Count > 0)
                        AppendSentence(sb, "Penalty " + string.Join(" + ", parts.ToArray()) + ".");
                    break;
                }
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        /// <summary>
        /// A launch (RecordingStart) row: <c>Crew: Jeb, Bill, Bob. Ends landed at Midlands on
        /// Mun. Mission: Mun Landing.</c> <paramref name="endSentence"/> is
        /// <see cref="LaunchEnd"/>'s output (or null).
        /// </summary>
        internal static string Launch(IReadOnlyList<string> crewNames, string endSentence, string missionName)
        {
            var sb = new StringBuilder();
            string crew = CrewList(crewNames);
            if (crew != null) AppendSentence(sb, "Crew: " + crew + ".");
            if (!string.IsNullOrEmpty(endSentence)) AppendSentence(sb, endSentence);
            if (!string.IsNullOrEmpty(missionName)) AppendSentence(sb, "Mission: " + missionName + ".");
            return sb.Length > 0 ? sb.ToString() : null;
        }

        /// <summary>
        /// Up to <see cref="CrewNameCap"/> crew names, the stock <c>" Kerman"</c> surname
        /// dropped, then <c>+N</c> for the rest; duplicates and blanks skipped. Null when no
        /// name is left.
        /// </summary>
        internal static string CrewList(IReadOnlyList<string> crewNames)
        {
            if (crewNames == null || crewNames.Count == 0) return null;
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < crewNames.Count; i++)
            {
                string n = crewNames[i];
                if (string.IsNullOrEmpty(n) || !seen.Add(n)) continue;
                names.Add(ShortCrewName(n));
            }
            if (names.Count == 0) return null;
            int shown = Math.Min(names.Count, CrewNameCap);
            string text = string.Join(", ", names.GetRange(0, shown).ToArray());
            if (names.Count > shown)
                text += " +" + (names.Count - shown).ToString(IC);
            return text;
        }

        internal static string ShortCrewName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            if (name.Length > StockSurnameSuffix.Length
                && name.EndsWith(StockSurnameSuffix, StringComparison.Ordinal))
                return name.Substring(0, name.Length - StockSurnameSuffix.Length);
            return name;
        }

        /// <summary>
        /// How the launched vessel's line ends, as one sentence from the end recording's
        /// terminal state, body and biome (<c>Ends landed at Midlands on Mun.</c>), or null
        /// while it has no terminal state (still flying, or not recorded).
        /// </summary>
        internal static string LaunchEnd(Recording end)
        {
            if (end == null || !end.TerminalStateValue.HasValue) return null;
            string body = end.TerminalPosition.HasValue && !string.IsNullOrEmpty(end.TerminalPosition.Value.body)
                ? end.TerminalPosition.Value.body
                : FirstNonEmpty(end.TerminalOrbitBody, end.EndpointBodyName, end.SegmentBodyName);
            switch (end.TerminalStateValue.Value)
            {
                case TerminalState.Landed:
                case TerminalState.Splashed:
                {
                    string verb = end.TerminalStateValue.Value == TerminalState.Landed ? "landed" : "splashed";
                    if (!string.IsNullOrEmpty(body) && !string.IsNullOrEmpty(end.EndBiome))
                        return "Ends " + verb + " at " + end.EndBiome + " on " + body + ".";
                    if (!string.IsNullOrEmpty(body))
                        return "Ends " + verb + " on " + body + ".";
                    return "Ends " + verb + ".";
                }
                case TerminalState.Orbiting:
                    return string.IsNullOrEmpty(body) ? "Ends in orbit." : "Ends orbiting " + body + ".";
                case TerminalState.SubOrbital:
                    return string.IsNullOrEmpty(body) ? "Ends sub-orbital." : "Ends sub-orbital over " + body + ".";
                case TerminalState.Destroyed: return "Ends destroyed.";
                case TerminalState.Recovered: return "Ends recovered.";
                case TerminalState.Docked: return "Ends docked.";
                case TerminalState.Boarded: return "Ends boarded.";
                case TerminalState.Disassembled: return "Ends disassembled.";
                default: return null;
            }
        }

        /// <summary>
        /// The recording where the launched vessel's own line ends: among the launch
        /// recording, its chain segments (same <c>ChainId</c>) and the recordings of its tree
        /// that are the same launch (<see cref="VesselLaunchIdentity.RecordingsShareLaunch"/>,
        /// debris excluded), the one ending last; a terminated one beats an unterminated one.
        /// Returns <paramref name="launch"/> when nothing else qualifies.
        /// </summary>
        internal static Recording ResolveLaunchEnd(Recording launch, IReadOnlyList<Recording> recordings)
        {
            if (launch == null) return null;
            Recording best = launch;
            if (recordings == null) return best;
            for (int i = 0; i < recordings.Count; i++)
            {
                Recording r = recordings[i];
                if (r == null || ReferenceEquals(r, launch) || r.IsDebris) continue;
                bool sameChain = !string.IsNullOrEmpty(launch.ChainId)
                    && string.Equals(r.ChainId, launch.ChainId, StringComparison.Ordinal);
                bool sameTreeLaunch = !string.IsNullOrEmpty(launch.TreeId)
                    && string.Equals(r.TreeId, launch.TreeId, StringComparison.Ordinal)
                    && VesselLaunchIdentity.RecordingsShareLaunch(launch, r);
                if (!sameChain && !sameTreeLaunch) continue;
                if (IsLaterEnd(r, best)) best = r;
            }
            return best;
        }

        private static bool IsLaterEnd(Recording candidate, Recording best)
        {
            bool cTerm = candidate.TerminalStateValue.HasValue;
            bool bTerm = best.TerminalStateValue.HasValue;
            if (cTerm != bTerm) return cTerm;
            return candidate.EndUT > best.EndUT;
        }

        /// <summary>Space-joins the non-empty parts; null when every part is empty.</summary>
        internal static string Compose(params string[] parts)
        {
            if (parts == null) return null;
            var sb = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
                AppendSentence(sb, parts[i]);
            return sb.Length > 0 ? sb.ToString() : null;
        }

        private static string JoinRewards(float funds, float rep, float science, string separator)
        {
            var parts = new List<string>();
            if (funds > 0f) parts.Add(FormatWhole(funds) + " funds");
            if (rep > 0f) parts.Add(FormatWhole(rep) + " rep");
            if (science > 0f) parts.Add(FormatScience(science) + " sci");
            return parts.Count > 0 ? string.Join(separator, parts.ToArray()) : null;
        }

        private static string FormatWhole(float value)
        {
            return value.ToString("0", IC);
        }

        private static string FormatScience(float value)
        {
            return value.ToString("0.#", IC);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            for (int i = 0; i < values.Length; i++)
                if (!string.IsNullOrEmpty(values[i])) return values[i];
            return null;
        }

        private static void AppendSentence(StringBuilder sb, string sentence)
        {
            if (string.IsNullOrEmpty(sentence)) return;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(sentence);
        }
    }

    /// <summary>
    /// Which Timeline row the help strip explains, and the memoized hover text per entry.
    /// The row whose rect held the mouse on the last Repaint is recorded at the end of that
    /// pass; the next frame's Layout and Repaint both hand only that row a tooltip, so the
    /// hover text is built for one row, never for every row every frame, and both passes
    /// of one frame agree. The memo is cleared whenever the cached timeline rebuilds, and an
    /// entry's text is rebuilt when the row crosses "now" (its hold sentence depends on it).
    /// </summary>
    internal sealed class TimelineRowHoverTracker
    {
        private struct Memo
        {
            internal bool BuiltFuture;
            internal string Text;
        }

        private readonly Dictionary<TimelineEntry, Memo> memo = new Dictionary<TimelineEntry, Memo>();
        private int candidate = -1;
        private bool inRepaint;

        /// <summary>The visible-list row index hovered on the last Repaint, or -1.</summary>
        internal int HoveredRow { get; private set; } = -1;

        /// <summary>Hover texts built since construction (a test seam for the memo).</summary>
        internal int BuildCount { get; private set; }

        internal int MemoCount => memo.Count;

        /// <summary>Starts a list pass; only a Repaint pass observes rows.</summary>
        internal void BeginList(bool isRepaint)
        {
            inRepaint = isRepaint;
            if (isRepaint) candidate = -1;
        }

        /// <summary>Records that row <paramref name="rowIndex"/>'s rect held the mouse.</summary>
        internal void ObserveRow(int rowIndex, bool rectContainsMouse)
        {
            if (inRepaint && rectContainsMouse) candidate = rowIndex;
        }

        /// <summary>Ends the pass; a Repaint pass publishes its hovered row for the next frame.</summary>
        internal void EndList()
        {
            if (inRepaint) HoveredRow = candidate;
            inRepaint = false;
        }

        /// <summary>True only for the row hovered on the last Repaint.</summary>
        internal bool WantsTooltip(int rowIndex)
        {
            return rowIndex >= 0 && rowIndex == HoveredRow;
        }

        /// <summary>
        /// The memoized hover text of <paramref name="entry"/>, built through
        /// <paramref name="build"/> on a miss or when the row crossed "now" since.
        /// </summary>
        internal string GetText(TimelineEntry entry, bool isFuture, Func<TimelineEntry, bool, string> build)
        {
            if (entry == null || build == null) return null;
            Memo m;
            if (memo.TryGetValue(entry, out m) && m.BuiltFuture == isFuture)
                return m.Text;
            string text = build(entry, isFuture);
            memo[entry] = new Memo { BuiltFuture = isFuture, Text = text };
            BuildCount++;
            return text;
        }

        /// <summary>Drops every memoized text and the hovered row (the list rebuilt).</summary>
        internal void Reset()
        {
            memo.Clear();
            candidate = -1;
            HoveredRow = -1;
        }
    }
}
