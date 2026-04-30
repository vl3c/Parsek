namespace Parsek.Rendering
{
    /// <summary>
    /// Phase 6 §7.11 priority resolver (design doc §18 Phase 6 / §7.11). Pure
    /// table lookup over <see cref="AnchorSource"/>. Lower rank wins; ties
    /// are broken deterministically by enum value (HR-3).
    ///
    /// <para>
    /// The order is fixed per design doc §7.11 and is NOT a tunable. If a
    /// future phase needs runtime configurability, add the table to the
    /// <c>.pann</c> <c>ConfigurationHash</c> canonical encoding and bump
    /// <see cref="PannotationsSidecarBinary.AlgorithmStampVersion"/> so
    /// existing files invalidate. Phase 6 deliberately avoids that path —
    /// runtime reordering would silently change ε-winner outcomes across
    /// saves and break determinism.
    /// </para>
    /// </summary>
    internal static class AnchorPriority
    {
        /// <summary>
        /// Phase 6 §7.11 rank vector. Index = <see cref="AnchorSource"/>
        /// value; cell = priority rank where lower wins. Constants laid out
        /// alongside their §7 source so a future reorder is visible at the
        /// diff.
        /// </summary>
        private static readonly int[] Rank = new int[10]
        {
            /* 0 LiveSeparation     */ 1, // §7.1, top priority — live reference
            /* 1 DockOrMerge        */ 4, // §7.2 / §7.3, DAG-propagated
            /* 2 RelativeBoundary   */ 2, // §7.4, real persistent-vessel reference
            /* 3 OrbitalCheckpoint  */ 3, // §7.5, analytical-orbit reference
            /* 4 SoiTransition      */ 3, // §7.6, analytical-orbit reference
            /* 5 BubbleEntry        */ 6, // §7.7
            /* 6 BubbleExit         */ 6, // §7.7
            /* 7 CoBubblePeer       */ 5, // §7.8 (Phase 5 territory; reserved)
            /* 8 SurfaceContinuous  */ 6, // §7.9, terrain reference. Demoted in
                                          // Phase 6 from rank 2 to rank 6 so a
                                          // Phase-7-pending ε = 0 stub cannot
                                          // outrank a real OrbitalCheckpoint /
                                          // SoiTransition ε. Phase 7 will
                                          // promote back to rank 2 when the
                                          // per-frame terrain raycast lands;
                                          // that bump must accompany an
                                          // AlgorithmStampVersion bump so
                                          // existing .pann files re-resolve.
            /* 9 Loop               */ 2, // §7.10
        };

        /// <summary>Priority rank for a single source (lower is higher priority).</summary>
        internal static int RankOf(AnchorSource source)
        {
            int idx = (int)source;
            if (idx < 0 || idx >= Rank.Length)
                return int.MaxValue; // unknown source loses every tie
            return Rank[idx];
        }

        /// <summary>
        /// Returns true when <paramref name="candidate"/> should replace
        /// <paramref name="existing"/> at the same anchor slot. Lower rank
        /// always wins; equal-rank ties go to the lower enum value (HR-3
        /// deterministic resolution).
        /// </summary>
        internal static bool ShouldReplace(AnchorSource existing, AnchorSource candidate)
        {
            int rExisting = RankOf(existing);
            int rCandidate = RankOf(candidate);
            if (rCandidate < rExisting) return true;
            if (rCandidate > rExisting) return false;
            // Equal rank — break the tie by enum value (lower wins).
            return (int)candidate < (int)existing;
        }
    }
}
