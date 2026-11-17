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
    /// A committed recording tree that is NOT (yet) a Supply Run candidate, with
    /// the reason surfaced for the M3 near-miss subsection. Two disjoint reason
    /// families:
    /// <list type="bullet">
    ///   <item><b>Not fully sealed</b> (<see cref="NotSealed"/> true): at least one
    ///   recording is still re-flyable (<see cref="MergeState"/> !=
    ///   <see cref="MergeState.Immutable"/>); <see cref="ReflyableCount"/> counts
    ///   them. <see cref="Status"/> is unused in this case.</item>
    ///   <item><b>Sealed but ineligible</b> (<see cref="NotSealed"/> false):
    ///   <see cref="Status"/> carries the <see cref="RouteAnalysisEngine"/> reject
    ///   status.</item>
    /// </list>
    /// Eligible trees (whether already promoted or still an open candidate) are NOT
    /// near-misses and are excluded by <see cref="RouteCandidateFinder.DeriveNearMisses"/>.
    /// </summary>
    internal sealed class RouteNearMiss
    {
        internal RecordingTree Tree;
        internal RouteAnalysisStatus Status;
        internal bool NotSealed;
        internal int ReflyableCount;
        /// <summary>
        /// Optional reject quantifier copied from
        /// <see cref="RouteAnalysisResult.RejectDetail"/> (M2, plan finding
        /// 12) - e.g. the unaccounted <c>UntrackedCargoGain</c> amount. Null
        /// for statuses without one and for the not-sealed family.
        /// </summary>
        internal string RejectDetail;
    }

    /// <summary>
    /// Derives Supply Run candidates from committed recording trees. A tree
    /// becomes a candidate only when it is <b>fully sealed</b> (every recording
    /// reads <see cref="MergeState.Immutable"/> through its slot's effective tip:
    /// committed and slot-closed, so the route proof can no longer drift) AND <see cref="RouteAnalysisEngine"/>
    /// finds it eligible AND its source recording is not already backing a
    /// promoted route.
    /// </summary>
    /// <remarks>
    /// The candidate's sealed / eligible / dedup gates are PROOF gates, unchanged
    /// by the Missions-foundation rebase (design §0). The route's render geometry
    /// (the looped <c>[launch .. undock]</c> Mission segment) is derived separately
    /// by <see cref="RouteBackingMission"/> at creation time from the eligible
    /// window's <c>UndockUT</c>; this finder does not scan trajectory geometry.
    ///
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
        /// True when every recording in the tree reads
        /// <see cref="MergeState.Immutable"/> (committed and slot-closed) through
        /// <see cref="EffectiveState.ResolveMemberSealState"/>: a member counts by the
        /// state of its slot's effective chain+supersede tip, because a seal closes
        /// only that tip and leaves a split chain head or a superseded recording at
        /// CommittedProvisional for good (todo SEAL-LEAVES-SPLIT-CHAIN-HEAD-PROVISIONAL).
        /// A tree with no recordings, or any member that reads NotCommitted /
        /// CommittedProvisional, is not fully sealed. Reads the loaded scenario's
        /// supersede rows.
        /// </summary>
        internal static bool IsTreeFullySealed(RecordingTree tree)
        {
            return IsTreeFullySealed(tree, EffectiveState.CurrentSupersedesOrEmpty());
        }

        /// <summary>
        /// <see cref="IsTreeFullySealed(RecordingTree)"/> over explicit supersede rows.
        /// Logs one rate-limited summary when the tree reads sealed only because some
        /// members were read through their tip.
        /// </summary>
        internal static bool IsTreeFullySealed(
            RecordingTree tree, IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            if (tree?.Recordings == null || tree.Recordings.Count == 0)
                return false;

            int readThroughTip = 0;
            foreach (KeyValuePair<string, Recording> kvp in tree.Recordings)
            {
                Recording rec = kvp.Value;
                if (rec == null)
                    continue; // a null slot can't prove un-sealed; the live recordings decide
                if (EffectiveState.ResolveMemberSealState(rec, kvp.Key, tree, supersedes)
                    != MergeState.Immutable)
                    return false;
                if (rec.MergeState != MergeState.Immutable)
                    readThroughTip++;
            }

            if (readThroughTip > 0)
            {
                ParsekLog.VerboseRateLimited(Tag, "tree-sealed-through-tip-" + (tree.Id ?? "<no-id>"),
                    $"IsTreeFullySealed: tree={tree.Id ?? "<no-id>"} reads sealed with " +
                    $"{readThroughTip.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                    "member(s) not Immutable themselves, read through their slot's closed effective tip " +
                    "(a split chain head or a superseded recording)");
            }
            return true;
        }

        /// <summary>
        /// Production entry point: derive candidates from the live stores,
        /// skipping trees the player dismissed as route candidates (M6
        /// candidate intent helper, <see cref="RouteStore.DismissedCandidateTreeIds"/>).
        /// </summary>
        internal static List<RouteCandidate> DeriveCandidates()
        {
            return DeriveCandidates(
                RecordingStore.CommittedTrees, RouteStore.CommittedRoutes,
                RouteStore.DismissedCandidateTreeIds);
        }

        /// <summary>
        /// Pure derivation over the supplied trees and existing routes. Exposed
        /// for direct xUnit testing without touching the static stores.
        /// <paramref name="dismissedTreeIds"/> (M6 candidate intent helper) is
        /// an optional id-membership set of player-dismissed trees to skip;
        /// null means nothing is dismissed.
        /// </summary>
        internal static List<RouteCandidate> DeriveCandidates(
            IReadOnlyList<RecordingTree> committedTrees,
            IReadOnlyList<Route> existingRoutes,
            ICollection<string> dismissedTreeIds = null)
        {
            var result = new List<RouteCandidate>();
            if (committedTrees == null || committedTrees.Count == 0)
            {
                ParsekLog.Verbose(Tag, "DeriveCandidates: no committed trees");
                return result;
            }

            HashSet<string> promotedRecIds = BuildPromotedRecordingIdSet(existingRoutes);
            IReadOnlyList<RecordingSupersedeRelation> supersedes =
                EffectiveState.CurrentSupersedesOrEmpty();

            int notSealed = 0;
            int ineligible = 0;
            int alreadyPromoted = 0;
            int dismissed = 0;
            // Per-reason breakdown of the ineligible count, so the single batch
            // summary preserves the diagnosability the per-tree engine logs used
            // to give. The engine runs in Quiet mode here (this sweep polls ~1/s),
            // so it emits no per-tree INFO; the detailed per-tree reason is still
            // logged at INFO on the one-shot Create Route path (Diagnostic mode).
            // M4a (plan D1): MultipleConnectionWindows is re-purposed to the
            // genuinely-UNORDERABLE case (duplicate / NaN DockUT), not "multi-stop
            // not yet supported" - multi-stop windows are now accepted + ordered.
            int missingProof = 0, unorderableWindows = 0, missingEndpoint = 0,
                mixedPickup = 0, noManifest = 0, undockedStart = 0, untrackedGain = 0,
                flowNotClosed = 0, startTrimUnsupported = 0,
                unsupportedKind = 0;
            for (int i = 0; i < committedTrees.Count; i++)
            {
                RecordingTree tree = committedTrees[i];
                if (tree == null)
                    continue;

                // M6 candidate intent helper: a player-dismissed tree disappears
                // from the whole Candidates section (candidates AND near-misses).
                // Id-membership only; the set lives in RouteStore and persists
                // alongside the routes.
                if (dismissedTreeIds != null && !string.IsNullOrEmpty(tree.Id)
                    && dismissedTreeIds.Contains(tree.Id))
                {
                    dismissed++;
                    continue;
                }

                if (!IsTreeFullySealed(tree, supersedes))
                {
                    notSealed++;
                    continue;
                }

                RouteAnalysisResult analysis =
                    RouteAnalysisEngine.AnalyzeTree(tree, RouteAnalysisLogMode.Quiet);
                if (analysis == null || !analysis.IsEligible)
                {
                    ineligible++;
                    switch (analysis?.Status)
                    {
                        case RouteAnalysisStatus.MissingRouteProof: missingProof++; break;
                        case RouteAnalysisStatus.MultipleConnectionWindows: unorderableWindows++; break;
                        case RouteAnalysisStatus.MissingEndpointProof: missingEndpoint++; break;
                        case RouteAnalysisStatus.MixedPickupDelivery: mixedPickup++; break;
                        case RouteAnalysisStatus.NoDeliveryManifest: noManifest++; break;
                        case RouteAnalysisStatus.UndockedStartOrigin: undockedStart++; break;
                        case RouteAnalysisStatus.UntrackedCargoGain: untrackedGain++; break;
                        case RouteAnalysisStatus.FlowDoesNotClose: flowNotClosed++; break;
                        case RouteAnalysisStatus.MidRecordingStartTrimUnsupported: startTrimUnsupported++; break;
                        case RouteAnalysisStatus.UnsupportedConnectionKind: unsupportedKind++; break;
                    }
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
                $"notSealed={notSealed} ineligible={ineligible} alreadyPromoted={alreadyPromoted} dismissed={dismissed} " +
                $"[missingProof={missingProof} unorderableWindows={unorderableWindows} " +
                $"missingEndpoint={missingEndpoint} mixedPickup={mixedPickup} " +
                $"noManifest={noManifest} undockedStart={undockedStart} " +
                $"untrackedGain={untrackedGain} flowNotClosed={flowNotClosed} " +
                $"startTrimUnsupported={startTrimUnsupported} " +
                $"unsupportedKind={unsupportedKind}]");
            return result;
        }

        /// <summary>
        /// Production entry point: derive near-misses from the live stores,
        /// skipping trees the player dismissed as route candidates (M6
        /// candidate intent helper - a dismissed tree disappears from the
        /// near-miss list too, not just the candidates).
        /// </summary>
        internal static List<RouteNearMiss> DeriveNearMisses()
        {
            return DeriveNearMisses(
                RecordingStore.CommittedTrees, RouteStore.DismissedCandidateTreeIds);
        }

        /// <summary>
        /// Pure derivation of the "recently committed trees not yet eligible"
        /// near-miss list. Walks the same committed trees in the same order as
        /// <see cref="DeriveCandidates"/>; for each tree: a not-fully-sealed tree
        /// yields a <see cref="RouteNearMiss"/> with <see cref="RouteNearMiss.NotSealed"/>
        /// set and a re-flyable count; a sealed-but-ineligible tree yields a
        /// near-miss carrying its <see cref="RouteAnalysisStatus"/>; an eligible
        /// tree (whether already promoted to a route or still an open candidate) is
        /// NOT a near-miss and is skipped. Exposed for direct xUnit testing without
        /// touching the static stores. Gate-safe: reads the members' MergeState through
        /// <see cref="EffectiveState.ResolveMemberSealState"/> (which owns its own
        /// store lookup) and the <see cref="RouteAnalysisEngine"/>, never a raw ledger /
        /// committed read here.
        /// </summary>
        internal static List<RouteNearMiss> DeriveNearMisses(
            IReadOnlyList<RecordingTree> committedTrees,
            ICollection<string> dismissedTreeIds = null)
        {
            var result = new List<RouteNearMiss>();
            if (committedTrees == null || committedTrees.Count == 0)
            {
                ParsekLog.Verbose(Tag, "DeriveNearMisses: no committed trees");
                return result;
            }

            IReadOnlyList<RecordingSupersedeRelation> supersedes =
                EffectiveState.CurrentSupersedesOrEmpty();

            int notSealed = 0;
            int ineligible = 0;
            int dismissed = 0;
            // Per-reason breakdown of the ineligible count, mirroring DeriveCandidates'
            // batch-summary convention (one Verbose line after the loop, never per item).
            // M4a (plan D1): MultipleConnectionWindows is re-purposed to the
            // genuinely-UNORDERABLE case (duplicate / NaN DockUT).
            int missingProof = 0, unorderableWindows = 0, missingEndpoint = 0,
                mixedPickup = 0, noManifest = 0, undockedStart = 0, untrackedGain = 0,
                flowNotClosed = 0, startTrimUnsupported = 0,
                unsupportedKind = 0;
            for (int i = 0; i < committedTrees.Count; i++)
            {
                RecordingTree tree = committedTrees[i];
                // Skip null and empty trees: an empty tree (no recordings) is not a
                // meaningful near-miss, and reporting it as "not fully sealed
                // (0 recordings still re-flyable)" is self-contradictory.
                if (tree?.Recordings == null || tree.Recordings.Count == 0)
                    continue;

                // M6 candidate intent helper: a player-dismissed tree disappears
                // from the near-miss list too (the whole Candidates section),
                // mirroring the DeriveCandidates skip.
                if (dismissedTreeIds != null && !string.IsNullOrEmpty(tree.Id)
                    && dismissedTreeIds.Contains(tree.Id))
                {
                    dismissed++;
                    continue;
                }

                if (!IsTreeFullySealed(tree, supersedes))
                {
                    notSealed++;
                    result.Add(new RouteNearMiss
                    {
                        Tree = tree,
                        NotSealed = true,
                        ReflyableCount = CountReflyableRecordings(tree, supersedes)
                    });
                    continue;
                }

                RouteAnalysisResult analysis =
                    RouteAnalysisEngine.AnalyzeTree(tree, RouteAnalysisLogMode.Quiet);
                if (analysis == null || !analysis.IsEligible)
                {
                    ineligible++;
                    RouteAnalysisStatus status =
                        analysis?.Status ?? RouteAnalysisStatus.MissingRouteProof;
                    switch (status)
                    {
                        case RouteAnalysisStatus.MissingRouteProof: missingProof++; break;
                        case RouteAnalysisStatus.MultipleConnectionWindows: unorderableWindows++; break;
                        case RouteAnalysisStatus.MissingEndpointProof: missingEndpoint++; break;
                        case RouteAnalysisStatus.MixedPickupDelivery: mixedPickup++; break;
                        case RouteAnalysisStatus.NoDeliveryManifest: noManifest++; break;
                        case RouteAnalysisStatus.UndockedStartOrigin: undockedStart++; break;
                        case RouteAnalysisStatus.UntrackedCargoGain: untrackedGain++; break;
                        case RouteAnalysisStatus.FlowDoesNotClose: flowNotClosed++; break;
                        case RouteAnalysisStatus.MidRecordingStartTrimUnsupported: startTrimUnsupported++; break;
                        case RouteAnalysisStatus.UnsupportedConnectionKind: unsupportedKind++; break;
                    }
                    result.Add(new RouteNearMiss
                    {
                        Tree = tree,
                        Status = status,
                        NotSealed = false,
                        // M2 (plan finding 12): carry the reject quantifier so
                        // the near-miss row can name the unaccounted amount.
                        RejectDetail = analysis?.RejectDetail
                    });
                    continue;
                }

                // Eligible (candidate or already promoted): not a near-miss, skip.
            }

            ParsekLog.Verbose(Tag,
                $"DeriveNearMisses: trees={committedTrees.Count} nearMisses={result.Count} " +
                $"notSealed={notSealed} ineligible={ineligible} dismissed={dismissed} " +
                $"[missingProof={missingProof} unorderableWindows={unorderableWindows} " +
                $"missingEndpoint={missingEndpoint} mixedPickup={mixedPickup} " +
                $"noManifest={noManifest} undockedStart={undockedStart} " +
                $"untrackedGain={untrackedGain} flowNotClosed={flowNotClosed} " +
                $"startTrimUnsupported={startTrimUnsupported} " +
                $"unsupportedKind={unsupportedKind}]");
            return result;
        }

        // Count of recordings in the tree that still read re-flyable (NotCommitted or
        // CommittedProvisional through EffectiveState.ResolveMemberSealState, the read
        // IsTreeFullySealed uses). Null slots are excluded to match IsTreeFullySealed,
        // which treats a null slot as can't-prove-unsealed and skips it.
        internal static int CountReflyableRecordings(
            RecordingTree tree, IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            if (tree?.Recordings == null)
                return 0;
            int count = 0;
            foreach (KeyValuePair<string, Recording> kvp in tree.Recordings)
            {
                Recording rec = kvp.Value;
                if (rec == null)
                    continue;
                if (EffectiveState.ResolveMemberSealState(rec, kvp.Key, tree, supersedes)
                    != MergeState.Immutable)
                    count++;
            }
            return count;
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
