using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Parsek.Logistics;

namespace Parsek
{
    /// <summary>
    /// One mission the Candidates section cannot offer as a route, as the near-miss list
    /// draws it: its tree id (what Dismiss acts on), its display name and why it is not a
    /// route. Pure input to <see cref="LogisticsNearMissPresentation.Group"/>.
    /// </summary>
    internal struct NearMissInput
    {
        public string TreeId;
        public string Name;
        public RouteAnalysisStatus Status;
        public bool NotSealed;
        public int ReflyableCount;
        public string RejectDetail;
    }

    /// <summary>One mission inside a near-miss group.</summary>
    internal sealed class NearMissMember
    {
        /// <summary>The tree Dismiss hides; null or empty draws no Dismiss.</summary>
        public string TreeId;
        /// <summary>The mission name, a repeated name numbered "Name [1]", "Name [2]".</summary>
        public string Label;
        /// <summary>The full reason sentence for this mission (may carry its own detail).</summary>
        public string Reason;
        /// <summary>The row's hover: <see cref="Reason"/> cut to the strip.</summary>
        public string Tooltip;
    }

    /// <summary>
    /// The missions that share one reason: ONE line in the near-miss list
    /// ("No dock was recorded (18): Kerbal X, Kerbal X [2], Duna Supply 1 ..."), its hover
    /// the names, and when expanded one row per mission with its own Dismiss.
    /// </summary>
    internal sealed class NearMissGroup
    {
        /// <summary>Stable key: the status name, or "NotSealed".</summary>
        public string Key;
        /// <summary>The short reason the group line opens with.</summary>
        public string ShortReason;
        /// <summary>The full sentence drawn once under an opened group: set only when
        /// every member has the same one AND it says more than <see cref="ShortReason"/>
        /// (advice, a count); else null.</summary>
        public string SharedReason;
        /// <summary>True when the members' sentences differ (each carries its own
        /// detail), so every member row reads "Name - sentence".</summary>
        public bool MembersShowReason;
        public readonly List<NearMissMember> Members = new List<NearMissMember>();
        /// <summary>"No dock was recorded (18): A, B, C ..."</summary>
        public string HeaderText;
        /// <summary>The hover: every name, cut with "+N more" to the strip.</summary>
        public string Tooltip;
    }

    /// <summary>
    /// Pure grouping for the Logistics "Missions that cannot become routes yet" list. A
    /// save with many flights that never docked used to draw one identical sentence per
    /// mission; this folds the list into one line per distinct reason. Unity-free.
    /// </summary>
    internal static class LogisticsNearMissPresentation
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>How many characters of names the group line previews.</summary>
        internal const int PreviewMaxChars = 60;

        /// <summary>The grouping key: the not-sealed gate is answered before the status.</summary>
        internal static string GroupKey(RouteAnalysisStatus status, bool notSealed)
        {
            return notSealed ? "NotSealed" : status.ToString();
        }

        /// <summary>
        /// The short reason a group line opens with, one per reject clause
        /// (<see cref="LogisticsRejectClauses"/>); the full sentence stays on the rows.
        /// </summary>
        internal static string ShortReason(RouteAnalysisStatus status, bool notSealed)
        {
            if (notSealed)
                return "Not finished yet";
            switch (status)
            {
                case RouteAnalysisStatus.MissingRouteProof: return "No dock was recorded";
                case RouteAnalysisStatus.MultipleConnectionWindows: return "Two transfers at the same moment";
                case RouteAnalysisStatus.NoDeliveryManifest: return "No cargo moved";
                case RouteAnalysisStatus.MixedPickupDelivery: return "Stored part gained from nowhere";
                case RouteAnalysisStatus.MissingEndpointProof: return "Docked vessel not identified";
                case RouteAnalysisStatus.UndockedStartOrigin: return "Started undocked with cargo aboard";
                case RouteAnalysisStatus.UntrackedCargoGain: return "Cargo gained with no recorded source";
                case RouteAnalysisStatus.FlowDoesNotClose: return "Cargo does not add up";
                case RouteAnalysisStatus.MidRecordingStartTrimUnsupported: return "Starts between two docks";
                case RouteAnalysisStatus.UnsupportedConnectionKind: return "Unsupported connection type";
                default: return "Not eligible";
            }
        }

        /// <summary>
        /// Whether a reason's full sentence says more than its short reason. Two do not:
        /// "No dock was recorded on this flight, so there is nothing to repeat." and
        /// "Endpoint vessel could not be identified at dock time." only restate "No dock
        /// was recorded" / "Docked vessel not identified", so an opened group of them
        /// shows its missions alone. Every other clause carries advice or a count.
        /// </summary>
        internal static bool ReasonAddsToShort(RouteAnalysisStatus status, bool notSealed)
        {
            if (notSealed)
                return true;
            return status != RouteAnalysisStatus.MissingRouteProof
                && status != RouteAnalysisStatus.MissingEndpointProof;
        }

        /// <summary>
        /// Numbers repeated names in order ("A", "B [1]", "B [2]"), the Mission Log
        /// convention; a name that appears once is left alone. A null or empty name reads
        /// "&lt;unnamed&gt;".
        /// </summary>
        internal static string[] NumberRepeatedNames(IReadOnlyList<string> names)
        {
            int count = names?.Count ?? 0;
            var resolved = new string[count];
            var totals = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                string n = string.IsNullOrEmpty(names[i]) ? "<unnamed>" : names[i];
                resolved[i] = n;
                totals.TryGetValue(n, out int t);
                totals[n] = t + 1;
            }
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            var result = new string[count];
            for (int i = 0; i < count; i++)
            {
                string n = resolved[i];
                if (totals[n] > 1)
                {
                    seen.TryGetValue(n, out int k);
                    k++;
                    seen[n] = k;
                    result[i] = n + " [" + k.ToString(IC) + "]";
                }
                else
                {
                    result[i] = n;
                }
            }
            return result;
        }

        /// <summary>
        /// Folds the near-miss list into one group per reason. Groups order by size
        /// (largest first), then by where their first mission sat in the input; members
        /// keep input order. Names are numbered across the WHOLE list, so a repeated name
        /// reads the same in every group. <paramref name="tooltipMaxChars"/> caps each
        /// group's hover (the window passes its strip budget; 0 or less: no cap).
        /// </summary>
        internal static List<NearMissGroup> Group(IReadOnlyList<NearMissInput> inputs, int tooltipMaxChars)
        {
            var groups = new List<NearMissGroup>();
            int count = inputs?.Count ?? 0;
            if (count == 0)
                return groups;

            var rawNames = new string[count];
            for (int i = 0; i < count; i++)
                rawNames[i] = inputs[i].Name;
            string[] labels = NumberRepeatedNames(rawNames);

            var byKey = new Dictionary<string, NearMissGroup>(StringComparer.Ordinal);
            var firstIndex = new Dictionary<NearMissGroup, int>();
            var addsByGroup = new Dictionary<NearMissGroup, bool>();
            for (int i = 0; i < count; i++)
            {
                NearMissInput nm = inputs[i];
                string key = GroupKey(nm.Status, nm.NotSealed);
                if (!byKey.TryGetValue(key, out NearMissGroup g))
                {
                    g = new NearMissGroup { Key = key, ShortReason = ShortReason(nm.Status, nm.NotSealed) };
                    byKey[key] = g;
                    firstIndex[g] = i;
                    groups.Add(g);
                }
                string reason = LogisticsRejectPresentation.DescribeNearMiss(
                    nm.Status, nm.NotSealed, nm.ReflyableCount, nm.RejectDetail);
                g.Members.Add(new NearMissMember
                {
                    TreeId = nm.TreeId,
                    Label = labels[i],
                    Reason = reason,
                    Tooltip = LogisticsRoutePresentation.CapToStrip(reason, tooltipMaxChars),
                });
                if (g.Members.Count == 1)
                    addsByGroup[g] = ReasonAddsToShort(nm.Status, nm.NotSealed);
            }

            groups.Sort((a, b) =>
            {
                int bySize = b.Members.Count.CompareTo(a.Members.Count);
                return bySize != 0 ? bySize : firstIndex[a].CompareTo(firstIndex[b]);
            });

            for (int gi = 0; gi < groups.Count; gi++)
            {
                NearMissGroup g = groups[gi];
                string shared = g.Members[0].Reason;
                for (int m = 1; m < g.Members.Count && shared != null; m++)
                    if (!string.Equals(g.Members[m].Reason, shared, StringComparison.Ordinal))
                        shared = null;
                g.MembersShowReason = shared == null;
                g.SharedReason = shared != null && addsByGroup[g] ? shared : null;
                var names = new List<string>(g.Members.Count);
                for (int m = 0; m < g.Members.Count; m++)
                    names.Add(g.Members[m].Label);
                g.HeaderText = FormatGroupHeader(g.ShortReason, names, PreviewMaxChars);
                g.Tooltip = FormatNamesTooltip(names, tooltipMaxChars);
            }
            return groups;
        }

        /// <summary>
        /// "No dock was recorded (18): Kerbal X, Kerbal X [2], Duna Supply 1 ...": the short
        /// reason, the count and as many names as fit <paramref name="previewMaxChars"/>
        /// (always at least one), " ..." when names were left out.
        /// </summary>
        internal static string FormatGroupHeader(string shortReason, IReadOnlyList<string> names, int previewMaxChars)
        {
            int count = names?.Count ?? 0;
            var sb = new StringBuilder();
            sb.Append(string.IsNullOrEmpty(shortReason) ? "Not eligible" : shortReason)
              .Append(" (").Append(count.ToString(IC)).Append(')');
            if (count == 0)
                return sb.ToString();
            sb.Append(": ");
            int used = 0;
            int shown = 0;
            for (int i = 0; i < count; i++)
            {
                string n = names[i] ?? string.Empty;
                int add = (shown > 0 ? 2 : 0) + n.Length;
                if (shown > 0 && used + add > previewMaxChars)
                    break;
                if (shown > 0) sb.Append(", ");
                sb.Append(n);
                used += add;
                shown++;
            }
            if (shown < count)
                sb.Append(" ...");
            return sb.ToString();
        }

        /// <summary>
        /// The group hover: "Missions: A, B, C" with every name, or as many as fit
        /// <paramref name="maxChars"/> followed by ", +N more". Never longer than
        /// <paramref name="maxChars"/> for any budget that holds the prefix and one suffix;
        /// 0 or less means no cap.
        /// </summary>
        internal static string FormatNamesTooltip(IReadOnlyList<string> names, int maxChars)
        {
            const string Prefix = "Missions: ";
            int count = names?.Count ?? 0;
            if (count == 0)
                return string.Empty;
            string full = Prefix + string.Join(", ", ToArray(names));
            if (maxChars <= 0 || full.Length <= maxChars)
                return full;
            var sb = new StringBuilder(Prefix);
            int shown = 0;
            for (int i = 0; i < count; i++)
            {
                string piece = (shown > 0 ? ", " : string.Empty) + (names[i] ?? string.Empty);
                int after = count - (i + 1);
                int reserve = after > 0 ? MoreSuffix(after).Length : 0;
                if (sb.Length + piece.Length + reserve > maxChars)
                    break;
                sb.Append(piece);
                shown++;
            }
            if (shown < count)
                sb.Append(shown > 0 ? MoreSuffix(count - shown) : "+" + (count - shown).ToString(IC) + " more");
            return sb.ToString();
        }

        private static string MoreSuffix(int more)
        {
            return ", +" + more.ToString(IC) + " more";
        }

        private static string[] ToArray(IReadOnlyList<string> names)
        {
            var a = new string[names.Count];
            for (int i = 0; i < a.Length; i++)
                a[i] = names[i] ?? string.Empty;
            return a;
        }
    }
}
