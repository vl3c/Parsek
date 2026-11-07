using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClickThroughFix;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // Sorting (sortable header, sort keys and comparers, the sorted index cache) and the status column text, style and hovers, plus the value formatters.
    internal partial class RecordingsTableUI
    {
        private void DrawSortableHeader(string label, SortColumn col, float width,
            bool expand = false, string tooltip = null)
        {
            parentUI.DrawSortableHeaderCore(label, col, ref sortColumn, ref sortAscending, width, expand, () =>
            {
                InvalidateSort();
                ParsekLog.Verbose("UI", $"Sort column changed: {sortColumn} {(sortAscending ? "asc" : "desc")}");
            }, ColHeaderHeight, tooltip);
        }

        private void InvalidateSort()
        {
            lastSortedStateVersion = int.MinValue;
        }

        private void RebuildSortedIndices(IReadOnlyList<Recording> committed, double now)
        {
            int stateVersion = RecordingStore.StateVersion;
            int displayNamesVersion = DisplayNames.Version;
            if (sortedIndices != null && lastSortedStateVersion == stateVersion
                && lastSortedDisplayNamesVersion == displayNamesVersion)
                return;

            lastSortedStateVersion = stateVersion;
            lastSortedDisplayNamesVersion = displayNamesVersion;
            // The per-row change-log dedup dicts are index-keyed; after a membership
            // change their entries describe the neighbour, so start them over.
            lastCanRewind.Clear();
            lastCanFF.Clear();
            lastStashSealInReFlyColumn.Clear();
            lastSuppressedTreeBranch.Clear();
            lastSuppressedByEnclosingRow.Clear();
            sortedIndices = new int[committed.Count];
            for (int i = 0; i < committed.Count; i++)
                sortedIndices[i] = i;

            if (sortColumn == SortColumn.Index)
            {
                if (!sortAscending)
                    Array.Reverse(sortedIndices);
                return;
            }

            var col = sortColumn;
            var asc = sortAscending;
            Array.Sort(sortedIndices, (a, b) =>
                CompareRecordings(committed[a], committed[b], col, asc, now));
        }

        /// <summary>
        /// A recording row's Status text: a countdown (or "future" / "active" with no points)
        /// before and during the flight, then its ending word (<c>Destroyed</c>, <c>Landed</c>,
        /// ...) once it is over, or "past" when it never recorded one. Debris gets its ending
        /// word like every other row (it used to read "past"). The FOLDER status
        /// (<see cref="GetGroupStatus"/>) still ignores debris when picking its word, so a
        /// mission folder does not read "Destroyed" because its booster fell. Pure for unit
        /// testing; <paramref name="statusOrder"/> is 0 future, 1 active, 2 past.
        /// </summary>
        internal static string ResolveRecordingStatusText(Recording rec, double now,
            out int statusOrder, out bool statusTextIsTerminalState)
        {
            statusTextIsTerminalState = false;
            if (now < rec.StartUT)
            {
                statusOrder = 0;
                return rec.Points.Count > 0
                    ? ParsekTimeFormat.FormatCountdown(rec.StartUT - now)
                    : "future";
            }
            if (now <= rec.EndUT && !rec.TerminalStateValue.HasValue)
            {
                statusOrder = 1;
                return rec.Points.Count > 0
                    ? ParsekTimeFormat.FormatCountdown(rec.StartUT - now)
                    : "active";
            }
            statusOrder = 2;
            if (rec.TerminalStateValue.HasValue)
            {
                statusTextIsTerminalState = true;
                return rec.TerminalStateValue.Value.ToString();
            }
            return "past";
        }

        /// <summary>
        /// The place half of a recording row's Status hover: an EVA's "EVA from vessel" (the
        /// old Start column's one fact the rest of the row does not show) and where the flight
        /// ended up (the old End column: body and biome, orbit, or the vessel boarded). A
        /// clause that would only repeat the status word, or the formatter's "-" placeholder,
        /// is dropped. Pure for unit testing; the formatters are
        /// <see cref="FormatStartPosition"/> / <see cref="FormatEndPosition"/>.
        /// </summary>
        internal static string BuildStatusPlaceTooltip(
            string startPosition, string endPosition, string statusText)
        {
            string startClause = !string.IsNullOrEmpty(startPosition)
                && startPosition.StartsWith(
                    RecordingsTableFormatters.EvaFromPrefix, StringComparison.Ordinal)
                ? startPosition
                : null;
            string endClause = null;
            if (!string.IsNullOrEmpty(endPosition)
                && endPosition != "-"
                && !string.Equals(endPosition, statusText, StringComparison.OrdinalIgnoreCase))
            {
                endClause = StatusEndPlacePrefix + endPosition;
            }
            return CombineTooltipText(startClause, endClause);
        }

        /// <summary>
        /// The stats half of a recording row's Status hover: "Max altitude 70.0km, max speed
        /// 2.2km/s", through the table's own formatters. Empty for a recording with no
        /// trajectory points (there is nothing to measure). Pure for unit testing.
        /// </summary>
        internal static string BuildStatusStatsTooltip(double maxAltitude, double maxSpeed, int pointCount)
        {
            if (pointCount <= 0) return string.Empty;
            return "Max altitude " + FormatAltitude(maxAltitude)
                + ", max speed " + FormatSpeed(maxSpeed);
        }

        internal static int GetStatusOrder(Recording rec, double now)
        {
            if (now < rec.StartUT) return 0;  // future
            if (now <= rec.EndUT && !rec.TerminalStateValue.HasValue) return 1;    // active
            return 2;                          // past
        }

        internal static string GetPhaseStyleKey(Recording rec)
        {
            if (rec == null) return null;

            if (RecordingStore.ShouldSuppressEvaBoundaryPhaseLabel(rec)
                && rec.TrackSections != null)
            {
                for (int i = rec.TrackSections.Count - 1; i >= 0; i--)
                {
                    switch (RecordingOptimizer.SplitEnvironmentClass(rec.TrackSections[i].environment))
                    {
                        case 0: return "atmo";
                        case 2: return "surface";
                        case 3: return "approach";
                        case 1: return "exo";
                    }
                }
            }

            return rec.SegmentPhase;
        }

        internal static string FormatRecordingVisualStatusText(
            RecordingVisualKind visualKind, string baseStatusText,
            bool preserveBaseStatusText)
        {
            string visualStatusText = GetRecordingVisualStatusText(visualKind);
            if (string.IsNullOrEmpty(visualStatusText))
                return baseStatusText;

            if (preserveBaseStatusText && !string.IsNullOrEmpty(baseStatusText))
            {
                string suffix = GetTerminalVisualStatusSuffix(visualKind);
                if (!string.IsNullOrEmpty(suffix))
                    return baseStatusText + " " + suffix;
            }

            return visualStatusText;
        }

        internal static string GetRecordingVisualStatusText(RecordingVisualKind visualKind)
        {
            switch (visualKind)
            {
                case RecordingVisualKind.StaticPlaceholder:
                    return "static";
                case RecordingVisualKind.StationaryTail:
                    return "stationary";
                default:
                    return null;
            }
        }

        private static string GetTerminalVisualStatusSuffix(RecordingVisualKind visualKind)
        {
            switch (visualKind)
            {
                case RecordingVisualKind.StaticPlaceholder:
                case RecordingVisualKind.StationaryTail:
                    return "still";
                default:
                    return null;
            }
        }

        internal static string GetRecordingVisualStatusTooltip(RecordingVisualKind visualKind)
        {
            switch (visualKind)
            {
                case RecordingVisualKind.StaticPlaceholder:
                    return "Static placeholder: landed background continuation with a surface position and time range, but no playable trail. Kept visible for row controls.";
                case RecordingVisualKind.StationaryTail:
                    return "Stationary tail: terminal leaf with only stationary/coasting sections and no visual events. Kept visible because it may carry end-of-recording spawn state.";
                default:
                    return null;
            }
        }

        private GUIStyle GetRecordingVisualStatusStyle(RecordingVisualKind visualKind)
        {
            switch (visualKind)
            {
                case RecordingVisualKind.StaticPlaceholder:
                    return statusStyleStatic ?? statusStylePast ?? GUI.skin.label;
                case RecordingVisualKind.StationaryTail:
                    return statusStyleStationary ?? statusStylePast ?? GUI.skin.label;
                default:
                    return statusStylePast ?? GUI.skin.label;
            }
        }

        /// <summary>
        /// "Docked with CD (mission 'CD Freighter')" for a recording that BEGAN at a dock - the
        /// merged stack naming the vessel it absorbed. This is the controller side, which today
        /// carries no partner information anywhere (design-dock-event-graph.md 6.5); the partner
        /// side keeps getting its naming from the Missions tab's link rows.
        ///
        /// <para>Returns null (silence, exactly like today) when the merge is unstamped, its pid
        /// resolves nowhere, the guid gate rejected it, the recording's subtree was superseded
        /// away, or the merge has TWO parents - in that last case both parents are equally "the
        /// partner" seen from the stack, they are already both visible in this table's own chain
        /// block, and picking one would be arbitrary.</para>
        /// </summary>
        private string ResolveDockPartnerTooltip(Recording rec)
        {
            if (rec == null || string.IsNullOrEmpty(rec.ParentBranchPointId)
                || string.IsNullOrEmpty(rec.RecordingId))
                return null;

            int frame = Time.frameCount;
            if (frame != dockEventGraphCacheFrame || dockEventGraphCache == null)
            {
                dockEventGraphCache = DockEventGraphCache.GetOrBuild();
                dockPartnerTooltipCache.Clear();
                dockEventGraphCacheFrame = frame;
            }
            if (dockPartnerTooltipCache.TryGetValue(rec.RecordingId, out string memo))
                return memo;
            string resolved = BuildDockPartnerTooltip(rec, dockEventGraphCache);
            dockPartnerTooltipCache[rec.RecordingId] = resolved;
            return resolved;
        }

        private static string BuildDockPartnerTooltip(Recording rec, DockEventGraph graph)
        {
            if (graph == null
                || !graph.NodesByBranchPointId.TryGetValue(
                    rec.ParentBranchPointId, out DockEventNode node)
                || node == null || !node.IsMerge)
                return null;

            if (!DockEventGraph.TryDescribePartner(
                    graph, node.BranchPointId, rec.RecordingId,
                    MissionsWindowUI.ResolvePartnerMissionName,
                    out DockPartnerDescription d))
                return null;

            string partner = MissionsWindowUI.FormatDockPartner(d);
            if (string.IsNullOrEmpty(partner))
                return null;
            return (node.Kind == BranchPointType.Board ? "Boarded " : "Docked ") + partner;
        }

        private static string CombineTooltipText(string primary, string secondary)
        {
            bool hasPrimary = !string.IsNullOrEmpty(primary);
            bool hasSecondary = !string.IsNullOrEmpty(secondary);
            if (!hasPrimary) return hasSecondary ? secondary : "";
            if (!hasSecondary) return primary;
            // " - ", never "\n": the help strip is ONE line, and a hard newline spends it
            // on the first clause with no horizontal scroll able to reveal the second.
            return primary + " - " + secondary;
        }

        /// <summary>
        /// Sort key for a single recording based on the current sort column.
        /// For Index/Phase sort, uses the row position to preserve sortedIndices order.
        /// </summary>
        internal static double GetRecordingSortKey(Recording rec, SortColumn column, double now, int rowFallback)
        {
            switch (column)
            {
                case SortColumn.LaunchTime: return rec.StartUT;
                case SortColumn.Duration: return rec.EndUT - rec.StartUT;
                case SortColumn.Status: return GetStatusOrder(rec, now);
                default: return rowFallback; // Index, Name, Phase, LaunchSite — string-based, handled externally
            }
        }

        /// <summary>
        /// Sort key for a chain based on the current sort column.
        /// Uses earliest StartUT for launch, the covered span for duration (the figure the
        /// chain row shows), etc.
        /// </summary>
        internal static double GetChainSortKey(List<int> members, IReadOnlyList<Recording> committed,
            SortColumn column, double now)
        {
            switch (column)
            {
                case SortColumn.LaunchTime:
                {
                    double earliest = double.MaxValue;
                    for (int m = 0; m < members.Count; m++)
                        if (committed[members[m]].StartUT < earliest)
                            earliest = committed[members[m]].StartUT;
                    return earliest;
                }
                case SortColumn.Duration:
                    return GetGroupSpanDuration(members, committed);
                case SortColumn.Status:
                {
                    // Best (lowest) status order among members
                    int best = 2;
                    for (int m = 0; m < members.Count; m++)
                    {
                        int order = GetStatusOrder(committed[members[m]], now);
                        if (order < best) best = order;
                    }
                    return best;
                }
                default:
                    return 0; // Name/Phase/Index/LaunchSite: handled via string comparison externally
            }
        }

        internal static int CompareRecordings(
            Recording ra, Recording rb,
            SortColumn column, bool ascending, double now)
        {
            int cmp = 0;
            switch (column)
            {
                case SortColumn.Index:
                    cmp = 0; // stable -- Array.Sort preserves original order for equal elements
                    break;
                case SortColumn.Phase:
                    string pa = RecordingStore.GetSegmentPhaseLabel(ra);
                    string pb = RecordingStore.GetSegmentPhaseLabel(rb);
                    cmp = string.Compare(pa, pb, StringComparison.OrdinalIgnoreCase);
                    break;
                case SortColumn.Name:
                    string na = string.IsNullOrEmpty(ra.VesselName) ? "Untitled" : ra.VesselName;
                    string nb = string.IsNullOrEmpty(rb.VesselName) ? "Untitled" : rb.VesselName;
                    cmp = string.Compare(na, nb, StringComparison.OrdinalIgnoreCase);
                    break;
                case SortColumn.LaunchTime:
                    cmp = ra.StartUT.CompareTo(rb.StartUT);
                    break;
                case SortColumn.Duration:
                    cmp = (ra.EndUT - ra.StartUT).CompareTo(rb.EndUT - rb.StartUT);
                    break;
                case SortColumn.Status:
                    cmp = GetStatusOrder(ra, now).CompareTo(GetStatusOrder(rb, now));
                    break;
                case SortColumn.LaunchSite:
                    string sa = ra.LaunchSiteName ?? "";
                    string sb = rb.LaunchSiteName ?? "";
                    cmp = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
                    if (cmp == 0)
                        cmp = ra.StartUT.CompareTo(rb.StartUT);
                    break;
            }
            return ascending ? cmp : -cmp;
        }

        internal static int[] BuildSortedIndices(
            IReadOnlyList<Recording> committed, SortColumn column, bool ascending, double now)
        {
            var indices = new int[committed.Count];
            for (int i = 0; i < committed.Count; i++)
                indices[i] = i;

            if (column == SortColumn.Index)
            {
                if (!ascending)
                    Array.Reverse(indices);
                return indices;
            }

            Array.Sort(indices, (a, b) =>
                CompareRecordings(committed[a], committed[b], column, ascending, now));
            return indices;
        }

        internal static string FormatDuration(double seconds)
            => ParsekTimeFormat.FormatDuration(seconds);

        internal static string FormatAltitude(double meters)
            => RecordingsTableFormatters.FormatAltitude(meters);

        internal static string FormatSpeed(double mps)
            => RecordingsTableFormatters.FormatSpeed(mps);

        internal static string FormatDistance(double meters)
            => RecordingsTableFormatters.FormatDistance(meters);

        /// <summary>
        /// Resolves the parent vessel name for EVA recordings by looking up ParentRecordingId.
        /// Returns null if not an EVA or parent not found.
        /// </summary>
        internal static string ResolveParentVesselName(Recording rec, IReadOnlyList<Recording> committed)
        {
            if (string.IsNullOrEmpty(rec.EvaCrewName) || string.IsNullOrEmpty(rec.ParentRecordingId))
                return null;
            for (int i = 0; i < committed.Count; i++)
                if (committed[i].RecordingId == rec.ParentRecordingId)
                    return committed[i].VesselName;
            // Also check tree recordings
            foreach (var tree in RecordingStore.CommittedTrees)
                foreach (var kvp in tree.Recordings)
                    if (kvp.Key == rec.ParentRecordingId)
                        return kvp.Value.VesselName;
            return null;
        }

        /// <summary>
        /// Formats the recording start position for the expanded stats column.
        /// Priority: launch site > EVA from vessel > situation + biome + body > biome + body > body.
        /// </summary>
        internal static string FormatStartPosition(Recording rec, string parentVesselName = null)
            => RecordingsTableFormatters.FormatStartPosition(rec, parentVesselName);

        /// <summary>
        /// Formats the recording end position for the expanded stats column.
        /// Matches timeline style: "Orbiting {body}", "{biome}, {body}", "Boarded {vessel}".
        /// Body fallback priority for terminal recordings: TerminalOrbitBody → StartBodyName.
        /// Body fallback priority for mid-segments: display body path → SegmentBodyName → last point body → StartBodyName.
        /// </summary>
        internal static string FormatEndPosition(Recording rec, string parentVesselName = null)
            => RecordingsTableFormatters.FormatEndPosition(rec, parentVesselName);

        /// <summary>
        /// Computes a sort key for a group based on the current sort column.
        /// Used to interleave groups with recordings in the sorted draw order.
        /// </summary>
        internal static double GetGroupSortKey(HashSet<int> descendants, IReadOnlyList<Recording> committed,
            SortColumn column, double now)
        {
            if (descendants.Count == 0) return double.MaxValue;

            switch (column)
            {
                case SortColumn.LaunchTime:
                    return GetGroupEarliestStartUT(descendants, committed);
                case SortColumn.Duration:
                    return GetGroupSpanDuration(descendants, committed);
                case SortColumn.Status:
                {
                    string unused;
                    int order;
                    GetGroupStatus(descendants, committed, now, out unused, out order);
                    return order;
                }
                case SortColumn.Name:
                case SortColumn.Phase:
                case SortColumn.LaunchSite:
                    return 0; // sorted by string comparison externally
                case SortColumn.Index:
                default:
                    return -1; // groups before recordings in insertion-order sort
            }
        }

        internal static int CompareRootItemsForSort(
            bool aIsGroup, string aGroupName, string aSortName, double aSortKey,
            bool bIsGroup, string bGroupName, string bSortName, double bSortKey,
            SortColumn column, bool ascending)
        {
            int cmp;
            if (column == SortColumn.Name || column == SortColumn.Phase || column == SortColumn.LaunchSite)
                cmp = string.Compare(aSortName, bSortName, StringComparison.OrdinalIgnoreCase);
            else
                cmp = aSortKey.CompareTo(bSortKey);
            return ascending ? cmp : -cmp;
        }
    }
}
