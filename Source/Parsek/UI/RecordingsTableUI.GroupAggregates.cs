using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClickThroughFix;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // Group and chain aggregates: span and start time, the main / forward / rewind member pick, group and chain status, display blocks and root-vessel block absorption.
    internal partial class RecordingsTableUI
    {
        /// <summary>
        /// Returns the earliest StartUT among the given descendant recording indices.
        /// Returns double.MaxValue if no descendants exist.
        /// </summary>
        internal static double GetGroupEarliestStartUT(HashSet<int> descendants, IReadOnlyList<Recording> committed)
        {
            double earliest = double.MaxValue;
            foreach (int idx in descendants)
            {
                double st = committed[idx].StartUT;
                if (st < earliest) earliest = st;
            }
            return earliest;
        }

        /// <summary>
        /// The wall-clock SPAN a folder's recordings cover: the latest EndUT minus the
        /// earliest StartUT over the descendants, the same figure a chain block shows. A sum
        /// of durations double-counts every parallel branch (a booster, debris, an EVA flying
        /// at the same time as its parent), so a mission read longer than it took.
        /// Recordings whose EndUT precedes their StartUT carry no trajectory data (the
        /// no-data fallback reads StartUT = ExplicitStartUT, EndUT = 0) and are skipped so
        /// they cannot stretch the span. Zero when nothing qualifies.
        /// </summary>
        internal static double GetGroupSpanDuration(IEnumerable<int> descendants, IReadOnlyList<Recording> committed)
        {
            if (descendants == null || committed == null) return 0;
            double earliest = double.MaxValue;
            double latest = double.MinValue;
            foreach (int idx in descendants)
            {
                if (idx < 0 || idx >= committed.Count) continue;
                var rec = committed[idx];
                if (rec == null) continue;
                double start = rec.StartUT;
                double end = rec.EndUT;
                if (end < start) continue;
                if (start < earliest) earliest = start;
                if (end > latest) latest = end;
            }
            if (earliest == double.MaxValue || latest == double.MinValue) return 0;
            return Math.Max(0, latest - earliest);
        }

        /// <summary>
        /// Bug #382: builds a stable fingerprint of the eligible descendant set
        /// for the group W button transition log. The fingerprint is the sorted,
        /// comma-joined RecordingIds of eligible descendants, so the bug #279
        /// transition log only fires when the eligibility set itself changes
        /// (vessel comes into range, goes out of range, etc.) — cursor advances
        /// within a steady set do not retrigger the log.
        /// </summary>
        private static string BuildEligibleSetFingerprint(
            HashSet<int> descendants, IReadOnlyList<Recording> committed, Func<int, bool> isEligible)
        {
            if (descendants == null || committed == null || isEligible == null) return string.Empty;
            var ids = new List<string>(descendants.Count);
            foreach (int idx in descendants)
            {
                if (idx < 0 || idx >= committed.Count) continue;
                var r = committed[idx];
                if (r == null || string.IsNullOrEmpty(r.RecordingId)) continue;
                if (!isEligible(idx)) continue;
                ids.Add(r.RecordingId);
            }
            ids.Sort(StringComparer.Ordinal);
            return string.Join(",", ids);
        }

        /// <summary>
        /// Returns the earliest non-debris recording from an aggregate row
        /// such as a group, chain, or grouped recording block.
        /// </summary>
        internal static int FindAggregateMainRecordingIndex(
            IEnumerable<int> members, IReadOnlyList<Recording> committed)
        {
            if (members == null || committed == null) return -1;

            int bestIdx = -1;
            double bestUT = double.MaxValue;
            foreach (int idx in members)
            {
                if (idx < 0 || idx >= committed.Count) continue;
                var rec = committed[idx];
                if (rec == null) continue;
                if (rec.IsDebris) continue;
                if (rec.StartUT < bestUT)
                {
                    bestUT = rec.StartUT;
                    bestIdx = idx;
                }
            }
            return bestIdx;
        }

        /// <summary>
        /// Group-specific wrapper for <see cref="FindAggregateMainRecordingIndex"/>.
        /// </summary>
        internal static int FindGroupMainRecordingIndex(
            HashSet<int> descendants, IReadOnlyList<Recording> committed)
        {
            return FindAggregateMainRecordingIndex(descendants, committed);
        }

        /// <summary>
        /// Returns the aggregate's future launch target, or -1 once the
        /// aggregate's main recording has reached/passed launch.
        /// </summary>
        internal static int FindAggregateForwardRecordingIndex(
            IEnumerable<int> members, IReadOnlyList<Recording> committed, double now)
        {
            int mainIdx = FindAggregateMainRecordingIndex(members, committed);
            if (mainIdx < 0 || committed == null || mainIdx >= committed.Count)
                return -1;
            var main = committed[mainIdx];
            return main != null && ShouldShowForwardButton(main, now) ? mainIdx : -1;
        }

        /// <summary>
        /// Returns the earliest aggregate member that owns a launch rewind save,
        /// using the group/block predicate that ignores row-level UF suppression.
        /// </summary>
        internal static int FindAggregateLegacyRewindRecordingIndex(
            IEnumerable<int> members, IReadOnlyList<Recording> committed, double now)
        {
            if (members == null || committed == null) return -1;

            int bestIdx = -1;
            double bestUT = double.MaxValue;
            foreach (int idx in members)
            {
                if (idx < 0 || idx >= committed.Count) continue;
                var rec = committed[idx];
                if (rec == null) continue;
                if (!ShouldShowGroupLegacyRewindButton(rec, now)) continue;
                Recording owner;
                double launchStartUT;
                if (!TryResolveLaunchRewindSurface(rec, out owner, out launchStartUT)) continue;
                if (launchStartUT < bestUT || (launchStartUT == bestUT && (bestIdx < 0 || idx < bestIdx)))
                {
                    bestUT = launchStartUT;
                    bestIdx = idx;
                }
            }
            return bestIdx;
        }

        /// <summary>
        /// Group-specific wrapper for <see cref="FindAggregateLegacyRewindRecordingIndex"/>.
        /// </summary>
        internal static int FindGroupLegacyRewindRecordingIndex(
            HashSet<int> descendants, IReadOnlyList<Recording> committed, double now)
        {
            return FindAggregateLegacyRewindRecordingIndex(descendants, committed, now);
        }

        /// <summary>
        /// Returns the status text and style order (0=future, 1=active, 2=past) for a group,
        /// based on the most relevant descendant: most recently activated (active with StartUT
        /// closest to now) wins, then closest future, then past.
        /// </summary>
        internal static void GetGroupStatus(HashSet<int> descendants, IReadOnlyList<Recording> committed,
            double now, out string statusText, out int statusOrder)
        {
            // Find the best candidate: prefer active (closest T-), then future (closest), then past
            double bestActiveDelta = double.MaxValue;   // smallest |now - StartUT| among active
            double bestFutureDelta = double.MaxValue;   // smallest (StartUT - now) among future
            int activeIdx = -1;
            int futureIdx = -1;
            bool anyPast = false;
            TerminalState? bestTerminal = null;
            double bestTerminalEndUT = double.MinValue;

            foreach (int idx in descendants)
            {
                var rec = committed[idx];
                // A recording with TerminalStateValue is done — don't treat as active
                if (now >= rec.StartUT && now <= rec.EndUT && !rec.TerminalStateValue.HasValue)
                {
                    // Active recording -- prefer closest T- (= StartUT - now, which is negative;
                    // we want the one closest to now, i.e., smallest |delta|)
                    double delta = Math.Abs(rec.StartUT - now);
                    if (delta < bestActiveDelta)
                    {
                        bestActiveDelta = delta;
                        activeIdx = idx;
                    }
                }
                else if (now < rec.StartUT)
                {
                    double delta = rec.StartUT - now;
                    if (delta < bestFutureDelta)
                    {
                        bestFutureDelta = delta;
                        futureIdx = idx;
                    }
                }
                else
                {
                    anyPast = true;
                    // Pick terminal state from the latest-ending non-debris recording
                    if (rec.TerminalStateValue.HasValue && !rec.IsDebris && rec.EndUT > bestTerminalEndUT)
                    {
                        bestTerminal = rec.TerminalStateValue.Value;
                        bestTerminalEndUT = rec.EndUT;
                    }
                }
            }

            if (activeIdx >= 0)
            {
                var rec = committed[activeIdx];
                statusOrder = 1; // active
                statusText = rec.Points.Count > 0
                    ? ParsekTimeFormat.FormatCountdown(rec.StartUT - now)
                    : "active";
            }
            else if (futureIdx >= 0)
            {
                var rec = committed[futureIdx];
                statusOrder = 0; // future
                statusText = rec.Points.Count > 0
                    ? ParsekTimeFormat.FormatCountdown(rec.StartUT - now)
                    : "future";
            }
            else if (anyPast)
            {
                statusOrder = 2;
                statusText = bestTerminal.HasValue ? bestTerminal.Value.ToString() : "past";
            }
            else
            {
                statusOrder = 2;
                statusText = "-";
            }
        }

        /// <summary>
        /// Computes aggregate status for a chain block header.
        /// Delegates to GetGroupStatus with a HashSet built from the member list.
        /// </summary>
        private static void GetChainStatus(List<int> members, IReadOnlyList<Recording> committed,
            double now, out string statusText, out int statusOrder)
        {
            chainStatusBuffer.Clear();
            for (int i = 0; i < members.Count; i++)
                chainStatusBuffer.Add(members[i]);
            GetGroupStatus(chainStatusBuffer, committed, now, out statusText, out statusOrder);
        }

        private static string GetGroupDisplayIdentity(Recording rec)
        {
            if (rec == null) return null;

            if (!string.IsNullOrEmpty(rec.TreeId) && rec.VesselPersistentId != 0)
                return $"treevessel:{rec.TreeId}:{rec.VesselPersistentId}";
            if (!string.IsNullOrEmpty(rec.ChainId))
                return $"chain:{rec.ChainId}";
            return null;
        }

        private static void AddDisplayBlockMember(List<int> members, int ri)
        {
            if (!members.Contains(ri))
                members.Add(ri);
        }

        private static void AddDisplayBlockMembers(List<int> members, List<int> additions)
        {
            if (additions == null) return;
            for (int i = 0; i < additions.Count; i++)
                AddDisplayBlockMember(members, additions[i]);
        }

        private static string ResolveGroupDisplayBlockName(List<int> members, IReadOnlyList<Recording> committed)
        {
            for (int i = 0; i < members.Count; i++)
            {
                int ri = members[i];
                if (ri < 0 || ri >= committed.Count) continue;
                string vesselName = committed[ri].VesselName;
                if (!string.IsNullOrEmpty(vesselName))
                    return vesselName;
            }

            return "Recording";
        }

        internal static List<GroupDisplayBlock> BuildGroupDisplayBlocks(
            string groupName,
            List<int> directMembers,
            IReadOnlyList<Recording> committed,
            Dictionary<string, List<int>> chainToRecs)
        {
            var blocks = new List<GroupDisplayBlock>();
            if (directMembers == null || committed == null)
                return blocks;

            var membersByKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < directMembers.Count; i++)
            {
                int ri = directMembers[i];
                if (ri < 0 || ri >= committed.Count)
                    continue;

                var rec = committed[ri];
                string identity = GetGroupDisplayIdentity(rec);
                if (string.IsNullOrEmpty(identity))
                    continue;

                string blockKey = (groupName ?? "") + "::" + identity;
                if (!membersByKey.TryGetValue(blockKey, out List<int> members))
                {
                    members = new List<int>();
                    membersByKey[blockKey] = members;
                }

                // Preserve existing grouped-chain visibility: if one row in a group
                // belongs to a chain, render the whole chain inside that block.
                if (!string.IsNullOrEmpty(rec.ChainId)
                    && chainToRecs != null
                    && chainToRecs.TryGetValue(rec.ChainId, out List<int> fullChain))
                {
                    AddDisplayBlockMembers(members, fullChain);
                }

                AddDisplayBlockMember(members, ri);
            }

            var emitted = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < directMembers.Count; i++)
            {
                int ri = directMembers[i];
                if (ri < 0 || ri >= committed.Count)
                    continue;

                var rec = committed[ri];
                string identity = GetGroupDisplayIdentity(rec);
                string blockKey = !string.IsNullOrEmpty(identity)
                    ? (groupName ?? "") + "::" + identity
                    : null;

                if (!string.IsNullOrEmpty(blockKey)
                    && membersByKey.TryGetValue(blockKey, out List<int> members)
                    && members.Count > 1)
                {
                    if (emitted.Add(blockKey))
                    {
                        blocks.Add(new GroupDisplayBlock
                        {
                            Key = blockKey,
                            DisplayName = ResolveGroupDisplayBlockName(members, committed),
                            Members = new List<int>(members)
                        });
                    }
                    continue;
                }

                blocks.Add(new GroupDisplayBlock
                {
                    Key = (groupName ?? "") + "::rec:" + ri.ToString(CultureInfo.InvariantCulture),
                    DisplayName = rec.VesselName,
                    Members = new List<int> { ri }
                });
            }

            return blocks;
        }

        // ----- Tree-root vessel block absorption (the mission folder draws its launched
        // vessel's segments directly) -----

        /// <summary>The recording a tree names as its root (the launched vessel), or null.</summary>
        internal static Recording ResolveTreeRootRecording(RecordingTree tree)
        {
            if (tree == null || tree.Recordings == null || string.IsNullOrEmpty(tree.RootRecordingId))
                return null;
            Recording root;
            return tree.Recordings.TryGetValue(tree.RootRecordingId, out root) ? root : null;
        }

        /// <summary>
        /// The display-block key the tree-root vessel's block carries inside a mission folder:
        /// the same <c>group + "::" + identity</c> <see cref="BuildGroupDisplayBlocks"/> builds,
        /// where the identity is <c>treevessel:{TreeId}:{pid}</c> or, for a root without a pid,
        /// the <c>chain:{ChainId}</c> fallback. Null when the root has neither.
        /// </summary>
        internal static string ResolveRootVesselBlockKey(string groupName, Recording rootRecording)
        {
            string identity = GetGroupDisplayIdentity(rootRecording);
            return string.IsNullOrEmpty(identity) ? null : (groupName ?? "") + "::" + identity;
        }

        /// <summary>
        /// Index of the block to absorb: the multi-member block whose key is the root vessel's.
        /// A single-member block is already drawn as a plain row, so there is nothing to
        /// absorb (-1); so is a missing key.
        /// </summary>
        internal static int FindRootVesselBlockIndex(List<GroupDisplayBlock> blocks, string rootBlockKey)
        {
            if (blocks == null || string.IsNullOrEmpty(rootBlockKey)) return -1;
            for (int i = 0; i < blocks.Count; i++)
            {
                if (blocks[i].Members != null && blocks[i].Members.Count > 1
                    && string.Equals(blocks[i].Key, rootBlockKey, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Replaces the absorbed block with one single-member block per segment, in the
        /// block's own member order and at the block's position, so the folder draws those
        /// segments as its own rows (single-member blocks draw as plain rows). A segment that
        /// another single-member block of the folder already draws is not drawn twice.
        /// </summary>
        internal static List<GroupDisplayBlock> FlattenAbsorbedBlock(
            List<GroupDisplayBlock> blocks, int absorbedIdx, string groupName)
        {
            if (blocks == null || absorbedIdx < 0 || absorbedIdx >= blocks.Count)
                return blocks;

            var alreadySingle = new HashSet<int>();
            for (int i = 0; i < blocks.Count; i++)
                if (i != absorbedIdx && blocks[i].Members != null && blocks[i].Members.Count == 1)
                    alreadySingle.Add(blocks[i].Members[0]);

            var flattened = new List<GroupDisplayBlock>(blocks.Count + blocks[absorbedIdx].Members.Count);
            for (int i = 0; i < blocks.Count; i++)
            {
                if (i != absorbedIdx)
                {
                    flattened.Add(blocks[i]);
                    continue;
                }
                List<int> members = blocks[i].Members;
                for (int m = 0; m < members.Count; m++)
                {
                    int ri = members[m];
                    if (alreadySingle.Contains(ri)) continue;
                    flattened.Add(new GroupDisplayBlock
                    {
                        Key = (groupName ?? "") + "::rec:" + ri.ToString(CultureInfo.InvariantCulture),
                        DisplayName = null,
                        Members = new List<int> { ri }
                    });
                }
            }
            return flattened;
        }

        /// <summary>
        /// The mission folder's Loop write when it absorbed its launched vessel's block: the
        /// absorbed members go WITH the auto loop range (what that block's toggle wrote), every
        /// other index in scope WITHOUT (the folder's own behaviour). Pure for unit testing.
        /// </summary>
        internal static void SplitAbsorbedLoopWrite(
            IEnumerable<int> scope, List<int> absorbedMembers,
            out List<int> withAutoRange, out List<int> withoutAutoRange)
        {
            withAutoRange = new List<int>();
            withoutAutoRange = new List<int>();
            var absorbed = absorbedMembers != null ? new HashSet<int>(absorbedMembers) : new HashSet<int>();
            if (absorbedMembers != null)
                foreach (int idx in absorbedMembers)
                    if (!withAutoRange.Contains(idx)) withAutoRange.Add(idx);
            if (scope == null) return;
            foreach (int idx in scope)
                if (!absorbed.Contains(idx) && !withoutAutoRange.Contains(idx))
                    withoutAutoRange.Add(idx);
        }

        private void LogRootBlockAbsorptionTransition(
            string groupName, string rootBlockKey, List<int> absorbedMembers)
        {
            bool absorbed = absorbedMembers != null;
            if (!UpdateEnclosingSuppressionCache(lastRootBlockAbsorbedByGroup, groupName, absorbed))
                return;
            ParsekLog.Verbose("UI", absorbed
                ? $"Group '{groupName}': the launched vessel's block is drawn as the folder's own rows "
                  + $"({absorbedMembers.Count.ToString(CultureInfo.InvariantCulture)} segments, key={rootBlockKey})"
                : $"Group '{groupName}': the launched vessel's block is no longer absorbed (key={rootBlockKey ?? "<none>"})");
        }
    }
}
