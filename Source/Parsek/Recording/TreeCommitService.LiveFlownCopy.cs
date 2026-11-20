using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // The live-copy rules of owner RULE 3: kept on TreeCommitService (a partial) so the pure
    // helpers add no type to the architecture knot (they read Recording and are called by it).
    /// <summary>
    /// Owner RULE 3 ("My live flight wins"): a committed vessel flown again. The live tree is
    /// then a copy of a committed tree (same tree id, another object): a resumed committed
    /// vessel (the committed-tree restore attempt), a Re-Fly tree, or either one carried
    /// across a restart in the save's active or pending tree. Two rules hang on it:
    /// <list type="bullet">
    ///   <item><description>While such a copy exists the optimizer leaves the committed tree
    ///   alone (<see cref="HoldLiveCopiesForOptimizer"/>): a split, merge or trim there gives the
    ///   committed tree recording ids or ends the copy does not have, and the commit of the copy
    ///   then reads as a stale duplicate.</description></item>
    ///   <item><description>The commit keeps the new leg (<see cref="IsCommittedTreeFlownAgain"/>): a copy
    ///   that carries flight the committed tree does not have is unioned into it, keeping the
    ///   ids only the committed side has, instead of being dropped as a duplicate.</description></item>
    /// </list>
    /// </summary>
    internal static partial class TreeCommitService
    {
        /// <summary>Recorded data must run past the committed copy's by more than this.</summary>
        internal const double LiveCopyRecordedDataToleranceSeconds = 0.001;

        // ─── Optimizer hold ─────────────────────────────────────────────────

        /// <summary>
        /// The ids of the committed trees a live copy exists for: a tree in
        /// <paramref name="liveCopies"/> (null entries skipped) whose id names a committed tree
        /// and which is not that committed object itself, plus the committed tree the armed
        /// restore attempt (<paramref name="restoreAttemptTreeId"/>) names. Pure.
        /// </summary>
        internal static HashSet<string> CollectLiveCopyTreeIds(
            IReadOnlyList<RecordingTree> committedTrees,
            IEnumerable<RecordingTree> liveCopies,
            string restoreAttemptTreeId)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (committedTrees == null) return ids;
            if (liveCopies != null)
            {
                foreach (var copy in liveCopies)
                {
                    if (copy == null || string.IsNullOrEmpty(copy.Id)) continue;
                    RecordingTree committed = FindLiveCopyTree(committedTrees, copy.Id);
                    if (committed != null && !ReferenceEquals(committed, copy))
                        ids.Add(copy.Id);
                }
            }
            if (!string.IsNullOrEmpty(restoreAttemptTreeId)
                && FindLiveCopyTree(committedTrees, restoreAttemptTreeId) != null)
                ids.Add(restoreAttemptTreeId);
            return ids;
        }

        /// <summary>
        /// A new set holding <paramref name="heldIds"/> (may be null) plus every recording id of
        /// the committed trees named in <paramref name="liveCopyTreeIds"/>. Pure.
        /// </summary>
        internal static HashSet<string> AddLiveCopyRecordingIds(
            HashSet<string> heldIds,
            IReadOnlyList<RecordingTree> committedTrees,
            ICollection<string> liveCopyTreeIds)
        {
            var result = heldIds != null
                ? new HashSet<string>(heldIds, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            if (committedTrees == null || liveCopyTreeIds == null) return result;
            foreach (string treeId in liveCopyTreeIds)
            {
                RecordingTree tree = FindLiveCopyTree(committedTrees, treeId);
                if (tree?.Recordings == null) continue;
                foreach (string id in tree.Recordings.Keys)
                    if (!string.IsNullOrEmpty(id))
                        result.Add(id);
            }
            return result;
        }

        /// <summary>
        /// The optimizer's held set (<paramref name="routeSourceIds"/>, the route sources the
        /// pass never rewrites) widened by every recording of a committed tree a live copy exists
        /// for: <paramref name="pendingTree"/>, <paramref name="savedPendingTree"/> (the one an
        /// active-tree restore parks beside it) and the armed committed-tree restore attempt
        /// (<paramref name="restoreAttemptTreeId"/>). One Info line per held tree. The pass's
        /// per-phase "held for route source" lines count these recordings too. The caller
        /// passes the live state, so this type reads no store.
        /// </summary>
        internal static HashSet<string> HoldLiveCopiesForOptimizer(
            HashSet<string> routeSourceIds,
            IReadOnlyList<RecordingTree> committedTrees,
            RecordingTree pendingTree,
            RecordingTree savedPendingTree,
            string restoreAttemptTreeId)
        {
            var liveTreeIds = CollectLiveCopyTreeIds(
                committedTrees, new[] { pendingTree, savedPendingTree }, restoreAttemptTreeId);
            if (liveTreeIds.Count == 0)
                return routeSourceIds;

            foreach (string treeId in liveTreeIds)
            {
                RecordingTree tree = FindLiveCopyTree(committedTrees, treeId);
                ParsekLog.Info("RecordingStore",
                    $"Optimization pass: tree '{treeId}' held whole while its live copy is flown " +
                    $"(recordings={(tree?.Recordings?.Count ?? 0).ToString(CultureInfo.InvariantCulture)} " +
                    $"pendingCopy={IsDistinctLiveCopy(pendingTree, tree)} " +
                    $"savedPendingCopy={IsDistinctLiveCopy(savedPendingTree, tree)} " +
                    $"restoreAttempt={string.Equals(restoreAttemptTreeId, treeId, StringComparison.Ordinal)}) - " +
                    "no split, merge or trim until the live flight is committed or discarded");
            }
            return AddLiveCopyRecordingIds(routeSourceIds, committedTrees, liveTreeIds);
        }

        private static bool IsDistinctLiveCopy(RecordingTree copy, RecordingTree committed)
            => copy != null && committed != null && !ReferenceEquals(copy, committed)
               && string.Equals(copy.Id, committed.Id, StringComparison.Ordinal);

        private static RecordingTree FindLiveCopyTree(IReadOnlyList<RecordingTree> trees, string treeId)
        {
            if (trees == null || string.IsNullOrEmpty(treeId)) return null;
            for (int i = 0; i < trees.Count; i++)
                if (trees[i] != null && string.Equals(trees[i].Id, treeId, StringComparison.Ordinal))
                    return trees[i];
            return null;
        }

        // ─── Commit ─────────────────────────────────────────────────────────

        /// <summary>
        /// True when <paramref name="incoming"/> is a committed vessel flown again rather than a
        /// duplicate of <paramref name="existing"/>: a distinct object with the same tree id that
        /// carries flight the committed tree does not have. The evidence, first match wins:
        /// <list type="number">
        ///   <item><description>a branch point id the committed tree lacks (a separation flown
        ///   in the new leg);</description></item>
        ///   <item><description>a recording both trees hold whose recorded data (points, track
        ///   sections with samples, non-predicted orbit segments) runs past everything the
        ///   committed tree holds for that recording's chain (an optimizer split leaves the
        ///   committed copy's end on a later chain piece, so a re-cut alone is not new flight);</description></item>
        ///   <item><description>a recording only the copy holds whose recorded data runs past
        ///   every committed recording of the tree.</description></item>
        /// </list>
        /// A copy with none of these is a duplicate (the commit skips it). Predicted tails are
        /// extrapolation, not flight, and never count. Pure.
        /// </summary>
        internal static bool IsCommittedTreeFlownAgain(RecordingTree existing, RecordingTree incoming, out string evidence)
        {
            evidence = "none";
            if (existing?.Recordings == null || incoming?.Recordings == null
                || ReferenceEquals(existing, incoming)
                || !string.Equals(existing.Id, incoming.Id, StringComparison.Ordinal))
            {
                evidence = "not-a-copy";
                return false;
            }

            int newBranchPoints = CountNewBranchPointIds(existing, incoming);
            if (newBranchPoints > 0)
            {
                evidence = "newBranchPoints=" + newBranchPoints.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            var latestByChain = new Dictionary<string, double>(StringComparer.Ordinal);
            double treeLatest = double.NegativeInfinity;
            foreach (var rec in existing.Recordings.Values)
            {
                if (rec == null || !TryGetLatestRecordedDataUT(rec, out double latest)) continue;
                string key = LiveCopyChainKey(rec);
                if (!latestByChain.TryGetValue(key, out double prior) || latest > prior)
                    latestByChain[key] = latest;
                if (latest > treeLatest) treeLatest = latest;
            }

            foreach (var kvp in incoming.Recordings)
            {
                Recording rec = kvp.Value;
                if (rec == null || string.IsNullOrEmpty(kvp.Key)) continue;
                if (!TryGetLatestRecordedDataUT(rec, out double incomingLatest)) continue;
                if (existing.Recordings.TryGetValue(kvp.Key, out Recording committed) && committed != null)
                {
                    double committedLatest = latestByChain.TryGetValue(LiveCopyChainKey(committed), out double c)
                        ? c
                        : double.NegativeInfinity;
                    if (incomingLatest > committedLatest + LiveCopyRecordedDataToleranceSeconds)
                    {
                        evidence = $"recordingRunsPastCommitted rec={kvp.Key} " +
                            $"liveEnd={FormatLiveCopyUT(incomingLatest)} committedEnd={FormatLiveCopyUT(committedLatest)}";
                        return true;
                    }
                }
                else if (incomingLatest > treeLatest + LiveCopyRecordedDataToleranceSeconds)
                {
                    evidence = $"newRecordingPastCommitted rec={kvp.Key} " +
                        $"liveEnd={FormatLiveCopyUT(incomingLatest)} committedEnd={FormatLiveCopyUT(treeLatest)}";
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The commit side of RULE 3: when <paramref name="incoming"/> is a committed vessel flown
        /// again (<see cref="IsCommittedTreeFlownAgain"/>), unions it into
        /// <paramref name="committed"/> (<see cref="TryUnionLiveCopyIntoCommitted"/>), keeping
        /// the ids only the committed side has, except the split pieces the live recording
        /// covers (<see cref="FindCoveredCommittedOnlySplitPieces"/>): an optimizer split while
        /// the copy lived cut a tail off the committed recording the copy still holds whole,
        /// and kept beside it the tail would replay twice and stay the chain tip with the old
        /// ending. Each one is re-absorbed into the live recording the way an optimizer merge
        /// absorbs a chain segment (<see cref="CommittedListOptimizer.ReabsorbSplitPieceIntoLiveCopy"/>).
        /// A live recording without a chain id takes the committed copy's, and every touched
        /// chain is re-indexed over the tree. False (nothing changed) when the copy is not
        /// flown again or the union refuses; <paramref name="evidence"/> names why.
        /// </summary>
        internal static bool TryUnionFlownAgainCopy(
            RecordingTree committed, RecordingTree incoming, string strictGateReason, out string evidence)
        {
            if (!IsCommittedTreeFlownAgain(committed, incoming, out evidence))
                return false;

            // Read before the union swaps the shared entries for the live instances.
            List<KeyValuePair<string, string>> coveredPieces =
                FindCoveredCommittedOnlySplitPieces(committed, incoming);
            var absorbedPieces = new List<KeyValuePair<Recording, Recording>>();
            for (int p = 0; p < coveredPieces.Count; p++)
            {
                Recording piece = committed.Recordings[coveredPieces[p].Key];
                string refusal = CommittedListOptimizer.SplitPieceFoldRefusal(piece);
                if (refusal != null)
                {
                    ParsekLog.Info("RecordingStore",
                        $"CommitTree: kept split piece '{coveredPieces[p].Key}' beside '{coveredPieces[p].Value}' " +
                        $"although the live recording covers it: {refusal}");
                    continue;
                }
                absorbedPieces.Add(new KeyValuePair<Recording, Recording>(
                    piece, incoming.Recordings[coveredPieces[p].Value]));
            }
            HashSet<string> chains = CarryCommittedChainIdentity(committed, incoming);

            if (!TryUnionLiveCopyIntoCommitted(committed, incoming, null,
                    out int addedRecs, out int addedBps, out bool activeSwapped))
                return false;

            for (int p = 0; p < absorbedPieces.Count; p++)
                CommittedListOptimizer.ReabsorbSplitPieceIntoLiveCopy(
                    absorbedPieces[p].Value, absorbedPieces[p].Key, "flown-again-reabsorb");
            if (absorbedPieces.Count > 0)
                committed.RebuildBackgroundMap();
            var treeRecordings = new List<Recording>(committed.Recordings.Values);
            foreach (string chainId in chains)
                RecordingOptimizer.ReindexChain(treeRecordings, chainId);

            int keptCommittedOnly = CountMissingRecordingIds(committed, incoming);
            ParsekLog.Info("RecordingStore",
                $"CommitTree: committed vessel flown again - unioned the live copy of tree " +
                $"id='{incoming.Id}' into the committed tree (strictGateReason={strictGateReason}, " +
                $"evidence={evidence}, addedRecordings={addedRecs} addedBranchPoints={addedBps} " +
                $"reabsorbedSplitPieces={absorbedPieces.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"keptCommittedOnlyRecordings={keptCommittedOnly} " +
                $"activeRecordingIdSwapped={activeSwapped} " +
                $"restoreAttemptArmed={CommittedTreeRestoreAttempt.IsCommittedTreeRestoreAttemptTree(incoming.Id)})");
            return true;
        }

        /// <summary>
        /// What a folded split piece owned as its chain's tip, carried onto
        /// <paramref name="into"/> (the committed copy of the surviving id, which the commit's
        /// replace step hands on to the live recording): its <see cref="Recording.MergeState"/>
        /// outright (a slot's open / closed state is read from the tip, so a Seal or Stash taken
        /// there wins), the spawn claim (<see cref="Recording.VesselSpawned"/>,
        /// <see cref="Recording.SpawnedVesselPersistentId"/>) and the spawn counters
        /// (<see cref="PreserveSpawnCountersOnReplace"/>) and the crew death respawn stamp where
        /// <paramref name="into"/> has none, and every player setting
        /// <paramref name="into"/> left at its default (hide, ghost off, loop fields, groups).
        /// Returns the carried field names, comma-separated (empty when none). Pure.
        /// </summary>
        internal static string CarryTipOwnedStateOnFold(Recording tip, Recording into)
        {
            if (tip == null || into == null || ReferenceEquals(tip, into))
                return "";
            var carried = new List<string>();
            if (into.MergeState != tip.MergeState)
            {
                into.MergeState = tip.MergeState;
                carried.Add("MergeState");
            }
            if (!into.VesselSpawned && tip.VesselSpawned)
            {
                into.VesselSpawned = true;
                carried.Add("VesselSpawned");
            }
            if (into.SpawnedVesselPersistentId == 0u && tip.SpawnedVesselPersistentId != 0u)
            {
                into.SpawnedVesselPersistentId = tip.SpawnedVesselPersistentId;
                carried.Add("SpawnedVesselPersistentId");
            }
            int counters = 0;
            PreserveSpawnCountersOnReplace(tip, into, ref counters);
            if (counters > 0)
                carried.Add("spawnCounters=" + counters.ToString(CultureInfo.InvariantCulture));
            if (!into.CrewDeathRespawns.HasValue && tip.CrewDeathRespawns.HasValue)
            {
                into.CrewDeathRespawns = tip.CrewDeathRespawns;
                into.CrewDeathRespawnSeconds = tip.CrewDeathRespawnSeconds;
                carried.Add("CrewDeathRespawns");
            }
            CarryUnsetPlayerSettings(tip, into, carried);
            return string.Join(",", carried.ToArray());
        }

        private static void CarryUnsetPlayerSettings(Recording tip, Recording into, List<string> carried)
        {
            if (!into.Hidden && tip.Hidden) { into.Hidden = true; carried.Add("Hidden"); }
            if (into.PlaybackEnabled && !tip.PlaybackEnabled) { into.PlaybackEnabled = false; carried.Add("PlaybackEnabled"); }
            if (!into.LoopPlayback && tip.LoopPlayback)
            {
                // The loop is one setting: its interval, unit, range and anchor go with it.
                into.LoopPlayback = true;
                into.LoopIntervalSeconds = tip.LoopIntervalSeconds;
                into.LoopTimeUnit = tip.LoopTimeUnit;
                into.LoopStartUT = tip.LoopStartUT;
                into.LoopEndUT = tip.LoopEndUT;
                into.LoopAnchorVesselId = tip.LoopAnchorVesselId;
                into.LoopAnchorBodyName = tip.LoopAnchorBodyName;
                carried.Add("Loop");
            }
            if ((into.RecordingGroups == null || into.RecordingGroups.Count == 0)
                && tip.RecordingGroups != null && tip.RecordingGroups.Count > 0)
            {
                into.RecordingGroups = new List<string>(tip.RecordingGroups);
                carried.Add("RecordingGroups");
            }
        }

        /// <summary>
        /// The committed-only recordings an optimizer split cut off a recording the live copy
        /// still holds: a recording only <paramref name="existing"/> has, on the chain of a
        /// recording both trees hold (the committed copy's chain id: the split stamped it),
        /// whose span lies inside the live recording's span (start to latest recorded data).
        /// Each pair is (piece id, the shared id it folds into), ordered by piece id. A
        /// committed-only recording the live span does not cover is a real separate piece and
        /// is not returned. Pure.
        /// </summary>
        internal static List<KeyValuePair<string, string>> FindCoveredCommittedOnlySplitPieces(
            RecordingTree existing, RecordingTree incoming)
        {
            var result = new List<KeyValuePair<string, string>>();
            if (existing?.Recordings == null || incoming?.Recordings == null
                || ReferenceEquals(existing, incoming))
                return result;

            foreach (var piece in existing.Recordings)
            {
                Recording pieceRec = piece.Value;
                if (pieceRec == null || string.IsNullOrEmpty(piece.Key)
                    || string.IsNullOrEmpty(pieceRec.ChainId)
                    || incoming.Recordings.ContainsKey(piece.Key))
                    continue;
                string survivorId = FindCoveringSharedRecording(existing, incoming, pieceRec);
                if (survivorId != null)
                    result.Add(new KeyValuePair<string, string>(piece.Key, survivorId));
            }
            result.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return result;
        }

        private static string FindCoveringSharedRecording(
            RecordingTree existing, RecordingTree incoming, Recording piece)
        {
            double pieceStart = piece.StartUT;
            double pieceEnd = TryGetLatestRecordedDataUT(piece, out double latest) ? latest : piece.EndUT;
            foreach (var shared in incoming.Recordings)
            {
                if (shared.Value == null
                    || !existing.Recordings.TryGetValue(shared.Key, out Recording committedShared)
                    || committedShared == null
                    || !string.Equals(committedShared.ChainId, piece.ChainId, StringComparison.Ordinal))
                    continue;
                double liveStart = shared.Value.StartUT;
                double liveEnd = TryGetLatestRecordedDataUT(shared.Value, out double liveLatest)
                    ? liveLatest
                    : shared.Value.EndUT;
                if (pieceStart >= liveStart - LiveCopyRecordedDataToleranceSeconds
                    && pieceEnd <= liveEnd + LiveCopyRecordedDataToleranceSeconds)
                    return shared.Key;
            }
            return null;
        }

        /// <summary>
        /// A live recording copied before an optimizer split stamped its chain has no chain id:
        /// it takes the committed copy's id and index, so the chain pieces that stay keep one
        /// chain with it. Returns the chain ids of every shared recording on a chain (the ones
        /// to re-index after the union).
        /// </summary>
        private static HashSet<string> CarryCommittedChainIdentity(RecordingTree existing, RecordingTree incoming)
        {
            var chains = new HashSet<string>(StringComparer.Ordinal);
            foreach (var shared in incoming.Recordings)
            {
                if (shared.Value == null
                    || !existing.Recordings.TryGetValue(shared.Key, out Recording committedShared)
                    || committedShared == null || string.IsNullOrEmpty(committedShared.ChainId))
                    continue;
                if (string.IsNullOrEmpty(shared.Value.ChainId))
                {
                    shared.Value.ChainId = committedShared.ChainId;
                    shared.Value.ChainIndex = committedShared.ChainIndex;
                }
                chains.Add(shared.Value.ChainId);
            }
            return chains;
        }

        private static string LiveCopyChainKey(Recording rec)
            => !string.IsNullOrEmpty(rec.ChainId) ? "chain:" + rec.ChainId : "rec:" + (rec.RecordingId ?? "");

        private static string FormatLiveCopyUT(double ut)
            => double.IsNegativeInfinity(ut) ? "<none>" : ut.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>
        /// The latest UT a recording holds recorded (not predicted) data for: its last point, the
        /// end of a track section that carries samples of its own, the end of a non-predicted
        /// orbit segment. False when it holds none. Pure.
        /// </summary>
        internal static bool TryGetLatestRecordedDataUT(Recording rec, out double latestUT)
        {
            latestUT = double.NegativeInfinity;
            if (rec == null) return false;
            bool found = false;
            if (rec.Points != null && rec.Points.Count > 0)
            {
                latestUT = rec.Points[rec.Points.Count - 1].ut;
                found = true;
            }
            if (rec.TrackSections != null)
            {
                for (int i = 0; i < rec.TrackSections.Count; i++)
                {
                    // A section with no samples of its own (flat-fallback) is metadata: a
                    // frameless tail trim leaves its endUT at the trim cut, past the last
                    // flat point, and the flat points already carry its data.
                    if (!TrackSectionHoldsRecordedDataSamples(rec.TrackSections[i]))
                        continue;
                    double end = rec.TrackSections[i].endUT;
                    if (double.IsNaN(end) || double.IsInfinity(end))
                        continue;
                    if (!found || end > latestUT)
                        latestUT = end;
                    found = true;
                }
            }
            if (rec.OrbitSegments != null)
            {
                for (int i = 0; i < rec.OrbitSegments.Count; i++)
                {
                    OrbitSegment seg = rec.OrbitSegments[i];
                    if (seg.isPredicted || double.IsNaN(seg.endUT) || double.IsInfinity(seg.endUT))
                        continue;
                    if (!found || seg.endUT > latestUT)
                        latestUT = seg.endUT;
                    found = true;
                }
            }
            return found;
        }

        /// <summary>
        /// True when a track section carries recorded samples of its own (frames, body-fixed
        /// frames or a non-predicted orbit checkpoint). Pure.
        /// </summary>
        internal static bool TrackSectionHoldsRecordedDataSamples(TrackSection section)
        {
            if (section.frames != null && section.frames.Count > 0)
                return true;
            if (section.bodyFixedFrames != null && section.bodyFixedFrames.Count > 0)
                return true;
            if (section.checkpoints != null)
            {
                for (int i = 0; i < section.checkpoints.Count; i++)
                {
                    if (!section.checkpoints[i].isPredicted)
                        return true;
                }
            }
            return false;
        }
    }
}
