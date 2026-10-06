using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Stock progress a career earned before its ledger began: the one-shot progress nodes
    /// that were already complete, and how many reward thresholds of each repeatable
    /// world-record node (RecordsAltitude / Depth / Speed / Distance) stock had already
    /// paid. The progress analogue of the FundsInitial / ScienceInitial /
    /// ReputationInitial seeds.
    ///
    /// <para>
    /// Why it exists: <see cref="KspStatePatcher.PatchMilestones"/> rebuilds every stock
    /// progress node from the ledger walk, and the walk only knows
    /// <see cref="GameActionType.MilestoneAchievement"/> rows. Progress earned before
    /// Parsek was installed has no row, so without this seed the first patch un-achieves
    /// it (contracts gated on the node are withdrawn, and stock pays the milestone again
    /// when its condition recurs) and resets every record track to its first band.
    /// </para>
    ///
    /// <para>
    /// Captured once per save, from the live tree after ProgressTracking's OnLoad has
    /// provably run (see <c>LedgerOrchestrator.EnsurePreLedgerProgressSeed</c>), and
    /// persisted as a child node of the ledger file. The seed carries no rewards: the
    /// currency seeds already contain whatever stock paid for this progress. A one-shot id
    /// is only seeded when NO ledger row (in any state) and no captured game-state event
    /// names it, so the seed can never re-achieve a node the ledger places in the future.
    /// </para>
    ///
    /// <para>
    /// Immutable once built. <see cref="NotCaptured"/> is the state of a save whose seed
    /// has not been taken yet; a captured seed may be empty.
    /// </para>
    /// </summary>
    internal sealed class PreLedgerProgressSeed
    {
        internal const string NodeName = "PROGRESS_SEED";
        internal const string CapturedKey = "captured";
        internal const string OneShotKey = "node";
        internal const string RecordNodeName = "RECORD";
        internal const string RecordIdKey = "id";
        internal const string RecordPaidCountKey = "paidCount";

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>The seed of a save whose pre-ledger progress has not been captured yet.</summary>
        internal static readonly PreLedgerProgressSeed NotCaptured =
            new PreLedgerProgressSeed(false, null, null);

        private readonly HashSet<string> oneShotIds;
        private readonly Dictionary<string, int> recordPaidCounts;

        private PreLedgerProgressSeed(
            bool captured,
            IEnumerable<string> oneShots,
            IEnumerable<KeyValuePair<string, int>> records)
        {
            Captured = captured;
            oneShotIds = new HashSet<string>(StringComparer.Ordinal);
            recordPaidCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (oneShots != null)
            {
                foreach (string id in oneShots)
                {
                    if (!string.IsNullOrEmpty(id))
                        oneShotIds.Add(id);
                }
            }
            if (records != null)
            {
                foreach (var kv in records)
                {
                    if (!string.IsNullOrEmpty(kv.Key) && kv.Value > 0)
                        recordPaidCounts[kv.Key] = kv.Value;
                }
            }
        }

        /// <summary>True once the seed has been taken for this save (it may still be empty).</summary>
        internal bool Captured { get; }

        /// <summary>Qualified ids of the one-shot nodes that were complete before the ledger began.</summary>
        internal IReadOnlyCollection<string> OneShotIds => oneShotIds;

        /// <summary>Paid reward thresholds per repeatable record node id; only positive counts are kept.</summary>
        internal IReadOnlyDictionary<string, int> RecordPaidCounts => recordPaidCounts;

        internal int OneShotCount => oneShotIds.Count;

        internal int RecordCount => recordPaidCounts.Count;

        internal bool ContainsOneShot(string milestoneId)
        {
            return !string.IsNullOrEmpty(milestoneId) && oneShotIds.Contains(milestoneId);
        }

        internal int GetRecordPaidCount(string milestoneId)
        {
            if (string.IsNullOrEmpty(milestoneId))
                return 0;
            return recordPaidCounts.TryGetValue(milestoneId, out int count) ? count : 0;
        }

        /// <summary>Builds a captured seed. Empty ids and non-positive record counts are dropped.</summary>
        internal static PreLedgerProgressSeed CreateCaptured(
            IEnumerable<string> oneShots,
            IEnumerable<KeyValuePair<string, int>> records)
        {
            return new PreLedgerProgressSeed(true, oneShots, records);
        }

        /// <summary>Invariant one-line summary for logs.</summary>
        internal string FormatSummary()
        {
            if (!Captured)
                return "not-captured";
            int thresholds = 0;
            foreach (var kv in recordPaidCounts)
                thresholds += kv.Value;
            return string.Format(IC,
                "captured oneShot={0} records={1} recordThresholds={2}",
                oneShotIds.Count, recordPaidCounts.Count, thresholds);
        }

        // ================================================================
        // Capture decision (pure)
        // ================================================================

        /// <summary>One live progress node as the capture sees it.</summary>
        internal struct NodeObservation
        {
            /// <summary>Patcher qualification: bare id at the top level, "Body/NodeId" in a subtree.</summary>
            public string QualifiedId;

            /// <summary>
            /// The node's own id when it sits in a subtree, else null. The patcher credits a
            /// subtree node from a row carrying this bare id (legacy recordings), so the
            /// capture treats such a row as owning the node too.
            /// </summary>
            public string BareId;

            public bool IsComplete;
            public bool IsRepeatableRecord;

            /// <summary>Record nodes only: whether the paid threshold count could be read off the live node.</summary>
            public bool ImpliedPaidCountKnown;

            /// <summary>Record nodes only: reward thresholds the live node state implies stock already paid.</summary>
            public int ImpliedPaidCount;
        }

        /// <summary>Batch counters of one capture, for the single summary log line.</summary>
        internal struct CaptureStats
        {
            public int NodesObserved;
            public int LiveComplete;
            public int OneShotSeeded;
            public int ExcludedByLedgerRow;
            public int ExcludedByEvent;
            public int RecordsSeeded;
            public int RecordThresholdsSeeded;
            public int RecordsOwnedByLedger;
            public int RecordsUnresolved;
        }

        /// <summary>
        /// Pure: why the capture must wait, or null when it may run now. The live tree is
        /// only trustworthy after ProgressTracking's OnLoad: between its OnAwake and its
        /// OnLoad the tree is freshly generated and every node reads unreached, so a capture
        /// there would record nothing and let the next patch clear the career's progress.
        /// </summary>
        internal static string DescribeCaptureDeferral(
            bool trackerPresent, bool trackerLoaded, bool treeAvailable)
        {
            if (!trackerPresent)
                return "ProgressTracking.Instance is null";
            if (!trackerLoaded)
                return "ProgressTracking present but its OnLoad has not run";
            if (!treeAvailable)
                return "ProgressTracking achievementTree is null";
            return null;
        }

        /// <summary>
        /// Pure: builds the seed from the live tree observations, every ledger row (any
        /// state: effective, not counted, tombstoned or after a cutoff) and every captured
        /// game-state event.
        ///
        /// <para>One-shot node: seeded when complete live and no
        /// <see cref="GameActionType.MilestoneAchievement"/> row and no
        /// <see cref="GameStateEventType.MilestoneAchieved"/> event names it (qualified id,
        /// or the bare id for a subtree node). An event without a row is a milestone the
        /// ledger is about to own (a pending flight), and an event with a row is already
        /// covered by the row check.</para>
        ///
        /// <para>Record node: seeded with the live implied paid count minus the reward
        /// thresholds the ledger rows for its id stand for minus those of its pending events
        /// (events whose (id, recording) scope has no row yet; each becomes its own row at
        /// commit, carrying the threshold count in its detail), clamped at 0. That keeps the
        /// live band exactly at capture: the walk's effective count becomes seed + the rows'
        /// thresholds (<see cref="MilestonesModule.GetRepresentedRecordThresholds"/>).</para>
        /// </summary>
        internal static PreLedgerProgressSeed Capture(
            IReadOnlyList<NodeObservation> nodes,
            IReadOnlyList<GameAction> ledgerActions,
            IReadOnlyList<GameStateEvent> events,
            out CaptureStats stats)
        {
            stats = default(CaptureStats);

            var rowCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var rowThresholds = new Dictionary<string, int>(StringComparer.Ordinal);
            var rowScopes = new HashSet<string>(StringComparer.Ordinal);
            if (ledgerActions != null)
            {
                for (int i = 0; i < ledgerActions.Count; i++)
                {
                    var action = ledgerActions[i];
                    if (action == null || action.Type != GameActionType.MilestoneAchievement)
                        continue;
                    string id = action.MilestoneId;
                    if (string.IsNullOrEmpty(id))
                        continue;
                    rowCounts[id] = (rowCounts.TryGetValue(id, out int c) ? c : 0) + 1;
                    rowThresholds[id] = (rowThresholds.TryGetValue(id, out int t) ? t : 0)
                        + MilestonesModule.GetRepresentedRecordThresholds(action);
                    rowScopes.Add(ScopeKey(id, action.RecordingId));
                }
            }

            var eventIds = new HashSet<string>(StringComparer.Ordinal);
            var pendingThresholds = new Dictionary<string, int>(StringComparer.Ordinal);
            if (events != null)
            {
                for (int i = 0; i < events.Count; i++)
                {
                    var evt = events[i];
                    if (evt.eventType != GameStateEventType.MilestoneAchieved)
                        continue;
                    string id = evt.key;
                    if (string.IsNullOrEmpty(id))
                        continue;
                    eventIds.Add(id);
                    string scope = ScopeKey(id, evt.recordingId);
                    if (rowScopes.Contains(scope))
                        continue;
                    pendingThresholds[id] = (pendingThresholds.TryGetValue(id, out int c) ? c : 0)
                        + GameStateEventConverter.ParseMilestoneRecordThresholds(evt.detail);
                }
            }

            var oneShots = new List<string>();
            var records = new List<KeyValuePair<string, int>>();
            if (nodes != null)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    var node = nodes[i];
                    if (string.IsNullOrEmpty(node.QualifiedId))
                        continue;
                    stats.NodesObserved++;

                    if (node.IsRepeatableRecord)
                    {
                        if (!node.ImpliedPaidCountKnown)
                        {
                            stats.RecordsUnresolved++;
                            continue;
                        }
                        if (node.ImpliedPaidCount <= 0)
                            continue;

                        int owned = CountOf(rowThresholds, node.QualifiedId)
                            + CountOf(pendingThresholds, node.QualifiedId);
                        int seeded = node.ImpliedPaidCount - owned;
                        if (seeded <= 0)
                        {
                            stats.RecordsOwnedByLedger++;
                            continue;
                        }
                        records.Add(new KeyValuePair<string, int>(node.QualifiedId, seeded));
                        stats.RecordsSeeded++;
                        stats.RecordThresholdsSeeded += seeded;
                        continue;
                    }

                    if (!node.IsComplete)
                        continue;
                    stats.LiveComplete++;

                    if (NamesNode(rowCounts, node))
                    {
                        stats.ExcludedByLedgerRow++;
                        continue;
                    }
                    if (NamesNode(eventIds, node))
                    {
                        stats.ExcludedByEvent++;
                        continue;
                    }
                    oneShots.Add(node.QualifiedId);
                    stats.OneShotSeeded++;
                }
            }

            return CreateCaptured(oneShots, records);
        }

        private static string ScopeKey(string milestoneId, string recordingId)
        {
            return milestoneId + "\n" + (recordingId ?? "");
        }

        private static int CountOf(Dictionary<string, int> counts, string id)
        {
            return counts.TryGetValue(id, out int c) ? c : 0;
        }

        private static bool NamesNode(Dictionary<string, int> counts, NodeObservation node)
        {
            if (counts.ContainsKey(node.QualifiedId))
                return true;
            return !string.IsNullOrEmpty(node.BareId) && counts.ContainsKey(node.BareId);
        }

        private static bool NamesNode(HashSet<string> ids, NodeObservation node)
        {
            if (ids.Contains(node.QualifiedId))
                return true;
            return !string.IsNullOrEmpty(node.BareId) && ids.Contains(node.BareId);
        }

        // ================================================================
        // Persistence (a child node of the ledger file root)
        // ================================================================

        /// <summary>
        /// Writes the seed as a <c>PROGRESS_SEED</c> child of <paramref name="ledgerRoot"/>.
        /// A not-captured seed writes nothing, so its absence on load means "capture again".
        /// Ids are written in ordinal order so the file is deterministic.
        /// </summary>
        internal void SerializeInto(ConfigNode ledgerRoot)
        {
            if (ledgerRoot == null || !Captured)
                return;

            var node = ledgerRoot.AddNode(NodeName);
            node.AddValue(CapturedKey, bool.TrueString);

            var ids = new List<string>(oneShotIds);
            ids.Sort(StringComparer.Ordinal);
            for (int i = 0; i < ids.Count; i++)
                node.AddValue(OneShotKey, ids[i]);

            var recordIds = new List<string>(recordPaidCounts.Keys);
            recordIds.Sort(StringComparer.Ordinal);
            for (int i = 0; i < recordIds.Count; i++)
            {
                var record = node.AddNode(RecordNodeName);
                record.AddValue(RecordIdKey, recordIds[i]);
                record.AddValue(RecordPaidCountKey,
                    recordPaidCounts[recordIds[i]].ToString(IC));
            }
        }

        /// <summary>
        /// Reads the seed back from a loaded ledger root. No <c>PROGRESS_SEED</c> child (a
        /// ledger written before this seed existed) is <see cref="NotCaptured"/>. A child
        /// without <c>captured = True</c> is also NotCaptured and counts as malformed, as
        /// does each unreadable entry inside a captured seed (the entry is dropped).
        /// </summary>
        internal static PreLedgerProgressSeed LoadFrom(ConfigNode ledgerRoot, out int malformedEntries)
        {
            malformedEntries = 0;
            if (ledgerRoot == null)
                return NotCaptured;

            ConfigNode node = ledgerRoot.GetNode(NodeName);
            if (node == null)
                return NotCaptured;

            string capturedStr = node.GetValue(CapturedKey);
            if (capturedStr == null || !bool.TryParse(capturedStr, out bool captured) || !captured)
            {
                malformedEntries++;
                return NotCaptured;
            }

            var oneShots = new List<string>();
            string[] ids = node.GetValues(OneShotKey);
            for (int i = 0; i < ids.Length; i++)
            {
                if (string.IsNullOrEmpty(ids[i]))
                {
                    malformedEntries++;
                    continue;
                }
                oneShots.Add(ids[i]);
            }

            var records = new List<KeyValuePair<string, int>>();
            ConfigNode[] recordNodes = node.GetNodes(RecordNodeName);
            for (int i = 0; i < recordNodes.Length; i++)
            {
                string id = recordNodes[i].GetValue(RecordIdKey);
                string countStr = recordNodes[i].GetValue(RecordPaidCountKey);
                if (string.IsNullOrEmpty(id)
                    || countStr == null
                    || !int.TryParse(countStr, NumberStyles.Integer, IC, out int count)
                    || count <= 0)
                {
                    malformedEntries++;
                    continue;
                }
                records.Add(new KeyValuePair<string, int>(id, count));
            }

            return CreateCaptured(oneShots, records);
        }
    }
}
