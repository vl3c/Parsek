using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Read-mostly chain queries over the committed recordings: mid-segment, predecessor,
    /// chain end and looping tests, plus the load-time chain validation that degrades a
    /// broken chain's recordings to standalone. Reads the committed list through
    /// <see cref="RecordingStore.CommittedRecordings"/> and never adds or removes entries.
    /// </summary>
    internal static class RecordingChainQueries
    {
        /// <summary>What <see cref="NextChainIndexAfter(Recording, IReadOnlyList{Recording})"/>
        /// returns when no piece follows: matches no recording's <see cref="Recording.ChainIndex"/>.</summary>
        internal const int NoNextChainIndex = int.MinValue;

        /// <summary>
        /// The chain index of the piece that follows <paramref name="rec"/> on its chain among
        /// <paramref name="candidates"/>: the LOWEST <see cref="Recording.ChainIndex"/> above
        /// rec's among the recordings with the same <see cref="Recording.ChainId"/> (ordinal)
        /// and <see cref="Recording.ChainBranch"/>, so a hole in the indices (a piece removed
        /// from the chain) is stepped over. <see cref="NoNextChainIndex"/> when rec has no chain
        /// id or nothing follows. The readers keep their own match loop over this index (first
        /// match, duplicate checks), so only the "+1" moved here. Pure.
        /// </summary>
        internal static int NextChainIndexAfter(Recording rec, IReadOnlyList<Recording> candidates)
        {
            int next = NoNextChainIndex;
            if (rec == null || string.IsNullOrEmpty(rec.ChainId) || candidates == null) return next;
            for (int i = 0; i < candidates.Count; i++)
                next = LowerFollowingIndex(rec, candidates[i], next);
            return next;
        }

        /// <summary>The dictionary form of <see cref="NextChainIndexAfter(Recording, IReadOnlyList{Recording})"/>
        /// (a tree's recordings). Pure.</summary>
        internal static int NextChainIndexAfter(Recording rec, Dictionary<string, Recording> candidates)
        {
            int next = NoNextChainIndex;
            if (rec == null || string.IsNullOrEmpty(rec.ChainId) || candidates == null) return next;
            foreach (var candidate in candidates.Values)
                next = LowerFollowingIndex(rec, candidate, next);
            return next;
        }

        /// <summary>What <see cref="PreviousChainIndexBefore(Recording, IReadOnlyList{Recording})"/>
        /// returns when no piece precedes: matches no recording's <see cref="Recording.ChainIndex"/>.</summary>
        internal const int NoPreviousChainIndex = int.MinValue;

        /// <summary>
        /// The mirror of <see cref="NextChainIndexAfter(Recording, IReadOnlyList{Recording})"/>: the
        /// chain index of the piece that precedes <paramref name="rec"/>, the HIGHEST non-negative
        /// <see cref="Recording.ChainIndex"/> below rec's among the recordings of the same chain id
        /// (ordinal) and branch, so a hole in the indices is stepped over.
        /// <see cref="NoPreviousChainIndex"/> when rec has no chain id or nothing precedes. Pure.
        /// </summary>
        internal static int PreviousChainIndexBefore(Recording rec, IReadOnlyList<Recording> candidates)
        {
            int previous = NoPreviousChainIndex;
            if (rec == null || string.IsNullOrEmpty(rec.ChainId) || candidates == null) return previous;
            for (int i = 0; i < candidates.Count; i++)
                previous = HigherPrecedingIndex(rec, candidates[i], previous);
            return previous;
        }

        /// <summary>The dictionary form of <see cref="PreviousChainIndexBefore(Recording, IReadOnlyList{Recording})"/>
        /// (a tree's recordings). Pure.</summary>
        internal static int PreviousChainIndexBefore(Recording rec, Dictionary<string, Recording> candidates)
        {
            int previous = NoPreviousChainIndex;
            if (rec == null || string.IsNullOrEmpty(rec.ChainId) || candidates == null) return previous;
            foreach (var candidate in candidates.Values)
                previous = HigherPrecedingIndex(rec, candidate, previous);
            return previous;
        }

        private static int HigherPrecedingIndex(Recording rec, Recording candidate, int best)
        {
            if (candidate == null
                || candidate.ChainIndex < 0
                || candidate.ChainIndex >= rec.ChainIndex
                || candidate.ChainBranch != rec.ChainBranch
                || !string.Equals(candidate.ChainId, rec.ChainId, System.StringComparison.Ordinal))
                return best;
            return candidate.ChainIndex > best ? candidate.ChainIndex : best;
        }

        private static int LowerFollowingIndex(Recording rec, Recording candidate, int best)
        {
            if (candidate == null
                || candidate.ChainIndex <= rec.ChainIndex
                || candidate.ChainBranch != rec.ChainBranch
                || !string.Equals(candidate.ChainId, rec.ChainId, System.StringComparison.Ordinal))
                return best;
            return best == NoNextChainIndex || candidate.ChainIndex < best ? candidate.ChainIndex : best;
        }

        /// <summary>
        /// Returns true if this recording is a mid-chain segment (not the last in its chain).
        /// Mid-chain ghosts should hold at their final position instead of being despawned.
        /// </summary>
        internal static bool IsChainMidSegment(Recording rec)
        {
            if (string.IsNullOrEmpty(rec.ChainId) || rec.ChainIndex < 0) return false;
            // Branch > 0 segments are parallel continuations (ghost-only); they despawn normally
            if (rec.ChainBranch > 0) return false;
            for (int i = 0; i < RecordingStore.CommittedRecordings.Count; i++)
            {
                var other = RecordingStore.CommittedRecordings[i];
                if (other.ChainId == rec.ChainId && other.ChainBranch == 0 && other.ChainIndex > rec.ChainIndex)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns the index in <c>RecordingStore.CommittedRecordings</c> of the immediate chain
        /// predecessor of <paramref name="rec"/> on branch 0 (the recording with the same ChainId,
        /// ChainBranch=0, and ChainIndex = rec.ChainIndex - 1), or -1 if no such predecessor
        /// exists, if rec is not on branch 0, if rec is a chain head (ChainIndex &lt;= 0), or if
        /// rec is not part of a chain at all. Used by the playback flag builder to detect chain-seam first-spawns
        /// (see <c>TrajectoryPlaybackFlags.isChainSeamSuccessor</c>).
        /// </summary>
        internal static int GetChainPredecessorIndex(Recording rec)
        {
            if (rec == null) return -1;
            if (string.IsNullOrEmpty(rec.ChainId)) return -1;
            if (rec.ChainBranch != 0) return -1;
            if (rec.ChainIndex <= 0) return -1;
            int expectedPredecessorIndex = PreviousChainIndexBefore(rec, RecordingStore.CommittedRecordings);
            for (int i = 0; i < RecordingStore.CommittedRecordings.Count; i++)
            {
                var other = RecordingStore.CommittedRecordings[i];
                if (other.ChainId == rec.ChainId
                    && other.ChainBranch == 0
                    && other.ChainIndex == expectedPredecessorIndex)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Returns the EndUT of the last segment in this recording's chain.
        /// Returns rec.EndUT if the recording is not part of a chain.
        /// </summary>
        internal static double GetChainEndUT(Recording rec)
        {
            if (string.IsNullOrEmpty(rec.ChainId) || rec.ChainIndex < 0) return rec.EndUT;
            double maxEnd = rec.EndUT;
            for (int i = 0; i < RecordingStore.CommittedRecordings.Count; i++)
            {
                var other = RecordingStore.CommittedRecordings[i];
                // Only branch 0 determines the chain's end (primary path)
                if (other.ChainId == rec.ChainId && other.ChainBranch == 0 && other.EndUT > maxEnd)
                    maxEnd = other.EndUT;
            }
            return maxEnd;
        }

        /// <summary>
        /// Validates chain integrity among committed recordings.
        /// Chains with gaps, duplicate indices, or non-monotonic StartUT are degraded
        /// to standalone recordings (ChainId/ChainIndex cleared).
        /// </summary>
        internal static void ValidateChains()
        {
            // Group by (ChainId, ChainBranch)
            var branches = new Dictionary<string, List<Recording>>();
            for (int i = 0; i < RecordingStore.CommittedRecordings.Count; i++)
            {
                var rec = RecordingStore.CommittedRecordings[i];
                if (string.IsNullOrEmpty(rec.ChainId)) continue;
                string key = rec.ChainId + ":" + rec.ChainBranch;
                List<Recording> list;
                if (!branches.TryGetValue(key, out list))
                {
                    list = new List<Recording>();
                    branches[key] = list;
                }
                list.Add(rec);
            }

            ParsekLog.Verbose("RecordingStore", $"Validating chains: {branches.Count} branch group(s)");

            // Track which chainIds are invalid so we degrade all branches together
            var invalidChains = new HashSet<string>();

            foreach (var kvp in branches)
            {
                var list = kvp.Value;
                list.Sort((a, b) => a.ChainIndex.CompareTo(b.ChainIndex));

                string chainId = list[0].ChainId;
                int branch = list[0].ChainBranch;
                bool valid = true;

                if (branch == 0)
                {
                    // Branch 0: indices must be 0..N-1 with no gaps or duplicates
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i].ChainIndex != i)
                        {
                            valid = false;
                            RecordingStore.Log($"[Parsek] Chain validation FAILED for chain={chainId} branch={branch}: expected index {i}, got {list[i].ChainIndex}");
                            break;
                        }
                    }
                }
                else
                {
                    // Branch > 0: indices must be contiguous and non-decreasing (don't need to start at 0)
                    for (int i = 1; i < list.Count; i++)
                    {
                        if (list[i].ChainIndex != list[i - 1].ChainIndex + 1)
                        {
                            valid = false;
                            RecordingStore.Log($"[Parsek] Chain validation FAILED for chain={chainId} branch={branch}: non-contiguous index at {list[i].ChainIndex}");
                            break;
                        }
                    }
                }

                // Check StartUT is monotonically non-decreasing within branch
                if (valid)
                {
                    for (int i = 1; i < list.Count; i++)
                    {
                        if (list[i].StartUT < list[i - 1].StartUT)
                        {
                            valid = false;
                            RecordingStore.Log($"[Parsek] Chain validation FAILED for chain={chainId} branch={branch}: non-monotonic StartUT at index {list[i].ChainIndex}");
                            break;
                        }
                    }
                }

                if (!valid)
                    invalidChains.Add(chainId);
            }

            // Degrade all recordings belonging to invalid chains
            if (invalidChains.Count == 0)
                ParsekLog.Verbose("RecordingStore", "All chains validated OK");

            if (invalidChains.Count > 0)
            {
                for (int i = 0; i < RecordingStore.CommittedRecordings.Count; i++)
                {
                    var rec = RecordingStore.CommittedRecordings[i];
                    if (!string.IsNullOrEmpty(rec.ChainId) && invalidChains.Contains(rec.ChainId))
                    {
                        RecordingStore.Log($"[Parsek]   Degrading recording '{rec.VesselName}' " +
                            $"(id={rec.RecordingId}, idx={rec.ChainIndex}, branch={rec.ChainBranch}) to standalone");
                        rec.ChainId = null;
                        rec.ChainIndex = -1;
                        rec.ChainBranch = 0;
                    }
                }
                foreach (var chainId in invalidChains)
                    RecordingStore.Log($"[Parsek] Degraded invalid chain {chainId} to standalone");
            }
        }

        /// <summary>
        /// Returns true if any branch-0 segment in the chain has LoopPlayback set.
        /// Note: deliberately does not check PlaybackEnabled (bug #433): hiding the ghost
        /// visual must not change the answer. It does NOT gate the chain tip's spawn: a
        /// looped phase keeps the chain's first run real, so the tip spawns once and later
        /// loop replays stay ghost-only (design 12.7, operator ruling 2026-09-27). It marks
        /// the chain's segments as looping-like for the replay-scope render gate and names
        /// the tip spawn in the log (GhostPlaybackLogic.LogChainLoopFirstRunSpawn).
        /// </summary>
        internal static bool IsChainLooping(string chainId)
        {
            if (string.IsNullOrEmpty(chainId)) return false;
            for (int i = 0; i < RecordingStore.CommittedRecordings.Count; i++)
            {
                var rec = RecordingStore.CommittedRecordings[i];
                if (rec.ChainId == chainId && rec.ChainBranch == 0 &&
                    rec.LoopPlayback)
                    return true;
            }
            return false;
        }
    }
}
