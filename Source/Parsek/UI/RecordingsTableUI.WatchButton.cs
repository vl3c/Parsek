using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClickThroughFix;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // The per-row and per-group Watch button: enable rule, reason and tooltip text, the transition-log caches and their stale-entry pruning, and the observability suffix.
    internal partial class RecordingsTableUI
    {
        /// <summary>
        /// Bug #279 follow-up: removes lastCanWatchByRecId / lastCanWatchByGroup
        /// entries whose RecordingId is no longer present in the committed
        /// recording list. Called once per <see cref="DrawRecordingsWindow"/>
        /// invocation. Delegates to <see cref="PruneStaleWatchEntries"/> so the
        /// pruning logic is testable without instantiating RecordingsTableUI
        /// (which requires a live ParsekUI / Unity GameObject).
        /// </summary>
        internal void PruneStaleWatchTransitionEntries(IReadOnlyList<Recording> committed)
        {
            PruneStaleWatchEntries(
                lastCanWatchByRecId,
                lastCanWatchByGroup,
                lastResolvedWatchTargetByGroup,
                groupWatchCursorByGroupName,
                committed);
        }

        /// <summary>
        /// Pure static prune logic for the watch-transition dicts. Removes
        /// every key whose recording ID is not present in <paramref name="committed"/>.
        /// Per-row dict keys ARE recording ids; group dict keys are
        /// "{groupName}/{recordingId}" and we extract the trailing segment.
        /// Both dicts are cleared if the committed list is empty/null. Callers
        /// pass references to the live dictionaries — the method mutates them
        /// in place.
        /// </summary>
        internal static void PruneStaleWatchEntries(
            Dictionary<string, bool> lastCanWatchByRecId,
            Dictionary<string, bool> lastCanWatchByGroup,
            IReadOnlyList<Recording> committed)
        {
            PruneStaleWatchEntries(
                lastCanWatchByRecId,
                lastCanWatchByGroup,
                null,
                null,
                committed);
        }

        internal static void PruneStaleWatchEntries(
            Dictionary<string, bool> lastCanWatchByRecId,
            Dictionary<string, bool> lastCanWatchByGroup,
            Dictionary<string, string> lastResolvedWatchTargetByGroup,
            Dictionary<string, string> groupWatchCursorByGroupName,
            IReadOnlyList<Recording> committed)
        {
            if (committed == null || committed.Count == 0)
            {
                lastCanWatchByRecId?.Clear();
                lastCanWatchByGroup?.Clear();
                lastResolvedWatchTargetByGroup?.Clear();
                groupWatchCursorByGroupName?.Clear();
                return;
            }

            // Build a HashSet of live recording IDs once for O(1) lookup.
            var liveIds = new HashSet<string>();
            for (int i = 0; i < committed.Count; i++)
            {
                var id = committed[i].RecordingId;
                if (!string.IsNullOrEmpty(id))
                    liveIds.Add(id);
            }

            // Per-row dict: prune by direct id membership.
            if (lastCanWatchByRecId != null && lastCanWatchByRecId.Count > 0)
            {
                List<string> stale = null;
                foreach (var key in lastCanWatchByRecId.Keys)
                {
                    if (!liveIds.Contains(key))
                    {
                        if (stale == null) stale = new List<string>();
                        stale.Add(key);
                    }
                }
                if (stale != null)
                    for (int i = 0; i < stale.Count; i++)
                        lastCanWatchByRecId.Remove(stale[i]);
            }

            // Group dict: extract the trailing "/{recordingId}" segment and
            // check membership. Keys without a "/" (shouldn't happen) are
            // dropped defensively.
            if (lastCanWatchByGroup != null && lastCanWatchByGroup.Count > 0)
            {
                List<string> stale = null;
                foreach (var key in lastCanWatchByGroup.Keys)
                {
                    int slash = key.LastIndexOf('/');
                    string recId = slash >= 0 ? key.Substring(slash + 1) : null;
                    if (string.IsNullOrEmpty(recId) || !liveIds.Contains(recId))
                    {
                        if (stale == null) stale = new List<string>();
                        stale.Add(key);
                    }
                }
                if (stale != null)
                    for (int i = 0; i < stale.Count; i++)
                    {
                        lastCanWatchByGroup.Remove(stale[i]);
                        lastResolvedWatchTargetByGroup?.Remove(stale[i]);
                    }
            }

            // Bug #382: per-group rotation cursor. Keys are group names,
            // values are RecordingIds. Drop entries whose RecordingId is
            // no longer live — the rotation naturally falls back to the
            // first eligible entry on the next press.
            if (groupWatchCursorByGroupName != null && groupWatchCursorByGroupName.Count > 0)
            {
                List<string> stale = null;
                foreach (var kv in groupWatchCursorByGroupName)
                {
                    if (string.IsNullOrEmpty(kv.Value) || !liveIds.Contains(kv.Value))
                    {
                        if (stale == null) stale = new List<string>();
                        stale.Add(kv.Key);
                    }
                }
                if (stale != null)
                    for (int i = 0; i < stale.Count; i++)
                        groupWatchCursorByGroupName.Remove(stale[i]);
            }
        }

        internal static bool IsWatchButtonEnabled(
            bool hasGhost, bool sameBody, bool inRange, bool isDebris)
        {
            return hasGhost && sameBody && inRange && !isDebris;
        }

        internal static bool ShouldEnableWatchButton(bool canWatch, bool isWatching)
        {
            return canWatch || isWatching;
        }

        internal static bool UpdateWatchButtonTransitionCache(
            Dictionary<string, bool> lastCanWatchByRecId, string watchKey, bool canWatch)
        {
            if (lastCanWatchByRecId == null || string.IsNullOrEmpty(watchKey))
                return false;

            bool previousCanWatch;
            if (lastCanWatchByRecId.TryGetValue(watchKey, out previousCanWatch)
                && previousCanWatch == canWatch)
                return false;

            lastCanWatchByRecId[watchKey] = canWatch;
            return true;
        }

        /// <summary>
        /// WATCH-ENTRY-REFUSED-INSIDE-QUOTED-RANGE. <c>bodyReadingCurrent</c> splits the
        /// <c>!sameBody</c> refusal into the two things it actually covers. The body a ghost
        /// reports is seeded at spawn and refreshed only while it is being positioned, which
        /// the render-zone hide skips - so a ghost the zone has hidden answers with a
        /// spawn-time value that may be arbitrarily stale, and the same-body comparison
        /// answers false. V7M measured exactly that: a ghost reporting <c>Kerbin</c> while
        /// both it and the observer were at Minmus, 144 km apart, and the affordance told the
        /// player it was on a different body. Defaulted to <c>true</c> so the historic meaning
        /// is what a caller that does not measure it gets.
        ///
        /// <para>THAT REFUSAL IS NOW FIXED TOO (2026-08-28): <c>IsGhostOnSameBody</c> resolves
        /// a not-current ghost's body from the recording's own trajectory, so the V7M shape
        /// ENTERS rather than being explained. This wording therefore now covers the RESIDUAL
        /// case - the trajectory could not resolve either - while a genuinely cross-body ghost
        /// still gets the "different body" text off a current reading.</para>
        /// </summary>
        internal static string GetWatchButtonReason(
            bool canWatch, bool hasGhost, bool sameBody, bool inRange, bool isDebris,
            bool bodyReadingCurrent = true)
        {
            if (canWatch) return "enabled";
            if (isDebris) return "disabled (debris)";
            if (!hasGhost) return "disabled (no ghost)";
            if (!sameBody) return bodyReadingCurrent ? "disabled (different body)" : "disabled (not rendered)";
            if (!inRange) return "disabled (out of range)";
            return "disabled (unknown)";
        }

        /// <inheritdoc cref="GetWatchButtonReason"/>
        internal static string GetWatchButtonTooltip(
            bool isWatching, bool hasGhost, bool sameBody, bool inRange, bool isDebris,
            bool bodyReadingCurrent = true)
        {
            if (isWatching)
                return "Exit watch mode";
            if (isDebris)
                return "Debris is not watchable";
            if (!hasGhost)
                return "No active ghost - recording is in the past/future or has no trajectory points";
            if (!sameBody)
                return bodyReadingCurrent
                    ? "Ghost is on a different body"
                    : "Ghost is too far away to be drawn right now, so Parsek cannot tell which body it is at - "
                      + "get closer and the Watch button comes back";
            if (!inRange)
                return "Ghost is beyond the fixed 300 km watch range";
            return "Follow ghost in watch mode";
        }

        private string BuildWatchObservabilitySuffix(ParsekFlight flight, int index)
        {
            if (flight == null)
                return "watchEval(unavailable=flight-null) watch=unavailable";
            return flight.DescribeWatchEligibilityForLogs(index) + " " + flight.DescribeWatchFocusForLogs();
        }

        private string BuildWatchObservabilitySuffix(ParsekFlight flight, int sourceIndex, int resolvedIndex)
        {
            if (flight == null)
                return "watchEval(unavailable=flight-null) watch=unavailable";
            if (resolvedIndex < 0 || resolvedIndex == sourceIndex)
                return BuildWatchObservabilitySuffix(flight, sourceIndex);

            return "source=" + flight.DescribeWatchEligibilityForLogs(sourceIndex)
                + " resolved=" + flight.DescribeWatchEligibilityForLogs(resolvedIndex)
                + " " + flight.DescribeWatchFocusForLogs();
        }
    }
}
