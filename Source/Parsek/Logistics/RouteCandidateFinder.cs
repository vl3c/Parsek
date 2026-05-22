using System;
using System.Collections.Generic;

namespace Parsek.Logistics
{
    /// <summary>
    /// A derived (not stored) Supply Run candidate: a committed, fully-sealed
    /// recording tree whose source path carries exactly one eligible
    /// dock-deliver-undock <see cref="RouteConnectionWindow"/>. Candidates are
    /// recomputed on demand from <see cref="RecordingStore.CommittedTrees"/>;
    /// promoting one (player "Create Route") writes a stored <see cref="Route"/>.
    /// </summary>
    internal sealed class RouteCandidate
    {
        internal RecordingTree Tree;
        internal RouteAnalysisResult Analysis;
    }

    /// <summary>
    /// Derives Supply Run candidates from committed recording trees. A tree
    /// becomes a candidate only when it is <b>fully sealed</b> (every recording
    /// is <see cref="MergeState.Immutable"/> — committed and slot-closed, so the
    /// route proof can no longer drift) AND <see cref="RouteAnalysisEngine"/>
    /// finds it eligible AND its source recording is not already backing a
    /// promoted route.
    /// </summary>
    /// <remarks>
    /// The "fully sealed" gate is deliberate and load-bearing: an open
    /// <see cref="MergeState.CommittedProvisional"/> (re-flyable Unfinished
    /// Flight) or <see cref="MergeState.NotCommitted"/> recording can still be
    /// rewritten, which would flip a route built from it to
    /// <see cref="RouteStatus.SourceChanged"/>. Only sealed trees yield stable
    /// route proof, so candidates never surface from a tree that can still
    /// change underneath the player.
    /// </remarks>
    internal static class RouteCandidateFinder
    {
        private const string Tag = "Route";

        /// <summary>
        /// True when every recording in the tree is
        /// <see cref="MergeState.Immutable"/> (committed and slot-closed).
        /// A tree with no recordings, or any recording still
        /// NotCommitted / CommittedProvisional, is not fully sealed.
        /// </summary>
        internal static bool IsTreeFullySealed(RecordingTree tree)
        {
            if (tree?.Recordings == null || tree.Recordings.Count == 0)
                return false;

            foreach (Recording rec in tree.Recordings.Values)
            {
                if (rec == null)
                    continue; // a null slot can't prove un-sealed; the live recordings decide
                if (rec.MergeState != MergeState.Immutable)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Production entry point: derive candidates from the live stores.
        /// </summary>
        internal static List<RouteCandidate> DeriveCandidates()
        {
            return DeriveCandidates(RecordingStore.CommittedTrees, RouteStore.CommittedRoutes);
        }

        /// <summary>
        /// Pure derivation over the supplied trees and existing routes. Exposed
        /// for direct xUnit testing without touching the static stores.
        /// </summary>
        internal static List<RouteCandidate> DeriveCandidates(
            IReadOnlyList<RecordingTree> committedTrees,
            IReadOnlyList<Route> existingRoutes)
        {
            var result = new List<RouteCandidate>();
            if (committedTrees == null || committedTrees.Count == 0)
            {
                ParsekLog.Verbose(Tag, "DeriveCandidates: no committed trees");
                return result;
            }

            HashSet<string> promotedRecIds = BuildPromotedRecordingIdSet(existingRoutes);

            int notSealed = 0;
            int ineligible = 0;
            int alreadyPromoted = 0;
            for (int i = 0; i < committedTrees.Count; i++)
            {
                RecordingTree tree = committedTrees[i];
                if (tree == null)
                    continue;

                if (!IsTreeFullySealed(tree))
                {
                    notSealed++;
                    continue;
                }

                RouteAnalysisResult analysis = RouteAnalysisEngine.AnalyzeTree(tree);
                if (analysis == null || !analysis.IsEligible)
                {
                    ineligible++;
                    continue;
                }

                string sourceId = analysis.SourceRecording?.RecordingId;
                if (!string.IsNullOrEmpty(sourceId) && promotedRecIds.Contains(sourceId))
                {
                    alreadyPromoted++;
                    continue;
                }

                result.Add(new RouteCandidate { Tree = tree, Analysis = analysis });
            }

            ParsekLog.Verbose(Tag,
                $"DeriveCandidates: trees={committedTrees.Count} candidates={result.Count} " +
                $"notSealed={notSealed} ineligible={ineligible} alreadyPromoted={alreadyPromoted}");
            return result;
        }

        // Set of recording ids already referenced by a stored route's SourceRefs,
        // so an eligible tree that has already been promoted does not also show as
        // a candidate (no duplicate "Create Route" affordance for a live route).
        private static HashSet<string> BuildPromotedRecordingIdSet(IReadOnlyList<Route> existingRoutes)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (existingRoutes == null)
                return ids;

            for (int i = 0; i < existingRoutes.Count; i++)
            {
                Route route = existingRoutes[i];
                if (route?.SourceRefs == null)
                    continue;
                for (int s = 0; s < route.SourceRefs.Count; s++)
                {
                    string id = route.SourceRefs[s]?.RecordingId;
                    if (!string.IsNullOrEmpty(id))
                        ids.Add(id);
                }
            }
            return ids;
        }
    }
}
