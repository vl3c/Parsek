using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace Parsek
{
    // Design authority: docs/dev/design-dock-event-graph.md sections 5.1, 6.3, 6.4, 6.5, 15.2.
    //
    // The load-time GLOBAL dock-event graph (#3): every Dock / Board / Undock branch point
    // across every committed tree, lifted into a runtime-only node with its PARTNER RESOLUTION
    // attached. A dock is a two-party event recorded by a single-writer recorder, so the other
    // party is only ever a pid sidecar; this module is the one place that turns that sidecar
    // back into "who was the other vessel", so the Missions / Recordings tabs (and, later, the
    // digest / chapters / seam markers) can name BOTH sides of every dock.
    //
    // PURITY CONTRACT (design 2.4 / 6.3): this file takes the tree list and a visibility
    // predicate as PARAMETERS. It must never reference RecordingStore, ParsekScenario,
    // EffectiveState, MissionStore, or any Unity type - the host cache (DockEventGraphCache)
    // owns those. That is what keeps the module mechanically outside the ERS grep gate
    // (scripts/grep-audit-ers-els.ps1) with NO ers-els-audit-allowlist.txt entry, and headlessly
    // testable. A source-text gate test pins the absence of the raw `CommittedRecordings` read
    // (written without its leading dot HERE on purpose: the gate's pattern is a literal scan and
    // would flag this very comment).

    /// <summary>
    /// How the other party of a merge was identified. Explicit values: the numbers appear in
    /// log lines and test assertions, so they are part of the diagnostic contract.
    /// </summary>
    internal enum DockPartnerStatus
    {
        /// <summary>Two parents converge on the merge; the partner is the OTHER parent
        /// (viewer-relative, so the node stores both and <see cref="DockEventGraph.TryDescribePartner"/>
        /// picks at query time).</summary>
        TwoParentSameTree = 0,
        /// <summary>Single parent; the stamped pid + launch guid resolved to a recording in
        /// ANOTHER committed tree (the AB/CD shape).</summary>
        CrossTree = 1,
        /// <summary>Single parent; the stamped pid + launch guid resolved inside the SAME tree
        /// (the A-&gt;D cross-session shape, design 6.2).</summary>
        SameTreeRecovered = 2,
        /// <summary>No pid was stamped: pre-decoupling recordings, EVA grabs, Board merges in
        /// v1. This is the degrade-to-today path and is counted, not logged per node.</summary>
        UnstampedZero = 3,
        /// <summary>A pid was stamped but no non-debris recording matches it anywhere.</summary>
        NoMatch = 4,
        /// <summary>A pid matched but the launch guids conclusively differ, and nothing else
        /// matched. Logged: this is the one failure where an affordance vanishes with no other
        /// trace (the FindLinks evidence rule).</summary>
        GuidRejected = 5,
    }

    /// <summary>
    /// The resolved identity of a merge's other party. Value carrier (design 5.1).
    /// </summary>
    internal struct DockPartnerResolution
    {
        public DockPartnerStatus Status;
        /// <summary>The claimed recording (null unless CrossTree / SameTreeRecovered; a
        /// TwoParentSameTree partner is viewer-relative and lives in the parent lists).</summary>
        public string PartnerRecordingId;
        /// <summary>Tree of the claimed recording. On TwoParentSameTree this is the owning
        /// tree (both parents are same-tree by construction).</summary>
        public string PartnerTreeId;
        public string PartnerVesselName;
        /// <summary>Claimed recording's <c>RecordedVesselGuid</c>; may be null (pid-only
        /// fallback, walker parity).</summary>
        public string PartnerLaunchGuid;
    }

    /// <summary>
    /// One Dock / Board / Undock branch point lifted into the graph. Runtime-only; never
    /// persisted, never mutated after Build. Class (not struct): the same instance is shared
    /// across four indexes.
    /// </summary>
    internal sealed class DockEventNode
    {
        public string BranchPointId;
        public string TreeId;
        public double UT;
        public BranchPointType Kind;             // Dock / Board / Undock
        public string MergeCause;                // "DOCK" / "BOARD"; null on Undock nodes
        public List<string> ParentRecordingIds = new List<string>();
        /// <summary>Vessel names parallel to <see cref="ParentRecordingIds"/> (index-aligned;
        /// entry null when the tree carries no such recording). Captured at build so
        /// <see cref="DockEventGraph.TryDescribePartner"/> can name a two-parent partner
        /// without touching any store.</summary>
        public List<string> ParentVesselNames = new List<string>();
        /// <summary>Copy of <c>bp.ChildRecordingIds</c>; [0] is the continuing / merged child
        /// by recorder convention.</summary>
        public List<string> ChildRecordingIds = new List<string>();
        public string MergedChildRecordingId;    // ChildRecordingIds[0]; null when absent
        public string MergedChildVesselName;     // display name of the merged stack
        public uint PartnerPid;                  // bp.TargetVesselPersistentId (0 on Undock)
        public DockPartnerResolution Partner;

        /// <summary>
        /// True when this node's own recording (the merged child for merges, the parent for
        /// Undock nodes) is NOT in the caller's visible set - a re-fly-superseded subtree.
        /// The node is still BUILT (topology is tree-level; there is no supersede filter over
        /// branch points), but every naming / digest / chapter consumer SKIPS it so a
        /// superseded dock cannot surface a phantom story row. Design 6.3 step 3.
        /// </summary>
        public bool VisibilityFlagged;

        public bool IsMerge => Kind == BranchPointType.Dock || Kind == BranchPointType.Board;
    }

    /// <summary>
    /// The viewer-relative answer to "who did I dock with here?" (design 6.5). Value carrier.
    /// </summary>
    internal struct DockPartnerDescription
    {
        public string PartnerVesselName;
        /// <summary>Resolved through the caller-supplied mission-name resolver; null when the
        /// caller supplied none or the partner tree has no mission.</summary>
        public string PartnerMissionName;
        public string PartnerRecordingId;
        /// <summary>Tree owning <see cref="PartnerRecordingId"/>. The UI resolves the mission
        /// id from this when it needs one.</summary>
        public string PartnerTreeId;
        /// <summary>Reserved for the PR4 digest's GoTo wiring: the pure core takes a NAME
        /// resolver only (keeping it free of Mission / MissionStore references), so PR3 leaves
        /// this null and UI call sites resolve the id from <see cref="PartnerTreeId"/>.</summary>
        public string PartnerMissionId;
        public DockPartnerStatus Status;
    }

    /// <summary>
    /// The build output: nodes plus the four lookup indexes consumers read. Instances are
    /// immutable-by-convention after <see cref="Build"/> returns.
    /// </summary>
    internal sealed class DockEventGraph
    {
        // ---- instance shape (design 5.1) ----

        /// <summary>Every node, sorted by (UT, BranchPointId) for deterministic consumption.</summary>
        public readonly List<DockEventNode> Nodes = new List<DockEventNode>();

        public readonly Dictionary<string, DockEventNode> NodesByBranchPointId =
            new Dictionary<string, DockEventNode>(StringComparer.Ordinal);

        /// <summary>
        /// Nodes visible to a tree: its OWN branch points AND every node RESOLVED INTO it (so a
        /// CrossTree node appears under both trees, and mission CD's surfaces see the dock that
        /// mission AB recorded). Per-list order follows <see cref="Nodes"/>.
        /// </summary>
        public readonly Dictionary<string, List<DockEventNode>> NodesByTreeId =
            new Dictionary<string, List<DockEventNode>>(StringComparer.Ordinal);

        /// <summary>Every recording that took part in a node: its parents, its merged child,
        /// and the resolved partner recording.</summary>
        public readonly Dictionary<string, List<DockEventNode>> ParticipantsByRecordingId =
            new Dictionary<string, List<DockEventNode>>(StringComparer.Ordinal);

        /// <summary>(owning tree, partner tree) for every CrossTree node - the adjacency the
        /// R6 double-clock advisory reads. Stored in claim direction; use
        /// <see cref="AreDockConnected"/> for an order-free test.</summary>
        public readonly HashSet<(string, string)> DockConnectedTreePairs =
            new HashSet<(string, string)>();

        /// <summary>The host cache's topology signature this graph was built against; null for
        /// a headless build. Compared with <c>Ordinal</c> equality by the host.</summary>
        public string BuildSignature;

        /// <summary>Order-free dock adjacency between two trees (either claim direction).</summary>
        internal bool AreDockConnected(string treeA, string treeB)
        {
            if (string.IsNullOrEmpty(treeA) || string.IsNullOrEmpty(treeB))
                return false;
            return DockConnectedTreePairs.Contains((treeA, treeB))
                || DockConnectedTreePairs.Contains((treeB, treeA));
        }

        internal List<DockEventNode> NodesForTree(string treeId)
        {
            if (!string.IsNullOrEmpty(treeId)
                && NodesByTreeId.TryGetValue(treeId, out List<DockEventNode> list))
                return list;
            return EmptyNodes;
        }

        private static readonly List<DockEventNode> EmptyNodes = new List<DockEventNode>();

        // ---- static build / query surface ----

        private const string Tag = "DockGraph";

        /// <summary>
        /// Set true by per-frame / per-tick callers so the per-build Verbose summaries do not
        /// flood - mirrors <see cref="MissionCrossTreeDock.SuppressLogging"/>. The host cache
        /// leaves it false: a signature-gated rebuild is exactly the event worth one line.
        /// </summary>
        internal static bool SuppressLogging;

        /// <summary>
        /// Builds the graph over <paramref name="trees"/> in one synchronous pass (design 6.3).
        /// <paramref name="isRecordingVisible"/> is the host's ERS-derived visibility predicate;
        /// null means nothing is flagged. <paramref name="buildSignature"/> is the host's cache
        /// key and is stored verbatim. Pure: no store reads, no mutation of any input.
        /// </summary>
        internal static DockEventGraph Build(
            IReadOnlyList<RecordingTree> trees,
            Func<string, bool> isRecordingVisible,
            string buildSignature = null)
        {
            var ic = CultureInfo.InvariantCulture;
            var sw = Stopwatch.StartNew();
            var graph = new DockEventGraph { BuildSignature = buildSignature };
            if (trees == null || trees.Count == 0)
            {
                sw.Stop();
                EmitBuildSummary(graph, new BuildTally(), sw, ic);
                return graph;
            }

            // The same-tree scanner's own `SameTreeDock: ... guidRejected=N` summary is left
            // UNSUPPRESSED (it is suppressed only when this graph is), and that is deliberate.
            // This build's `guidRejected` counter and per-node line cover the CROSS-TREE scan
            // only: the scanner returns claims, not rejections, so a dock whose ONLY pid match is
            // a same-tree recording with a conclusively different launch guid classifies NoMatch
            // here. The UI outcome is identical (both statuses are silent), the EVIDENCE is not -
            // and that summary line is the only trace the case leaves, which design 6.3 step 2e /
            // 15.2 make the rule. Rebuilds are signature-gated, so the cost is one line per tree
            // with a claim-or-rejection per topology change, not per frame.
            bool prevCrossTreeSuppress = MissionCrossTreeDock.SuppressLogging;
            if (SuppressLogging)
                MissionCrossTreeDock.SuppressLogging = true;
            Dictionary<string, Dictionary<string, SameTreeDockClaim>> sameTreeClaimsByTree;
            try
            {
                sameTreeClaimsByTree = IndexSameTreeClaims(trees);
            }
            finally
            {
                MissionCrossTreeDock.SuppressLogging = prevCrossTreeSuppress;
            }

            var tally = new BuildTally();
            for (int t = 0; t < trees.Count; t++)
            {
                RecordingTree tree = trees[t];
                if (tree?.BranchPoints == null || string.IsNullOrEmpty(tree.Id))
                    continue;
                sameTreeClaimsByTree.TryGetValue(
                    tree.Id, out Dictionary<string, SameTreeDockClaim> sameTreeClaims);

                for (int b = 0; b < tree.BranchPoints.Count; b++)
                {
                    BranchPoint bp = tree.BranchPoints[b];
                    if (bp == null || string.IsNullOrEmpty(bp.Id))
                        continue;
                    if (bp.Type != BranchPointType.Dock
                        && bp.Type != BranchPointType.Board
                        && bp.Type != BranchPointType.Undock)
                        continue;
                    if (graph.NodesByBranchPointId.ContainsKey(bp.Id))
                        continue;   // duplicate id (hand-edited save): first wins, deterministically

                    DockEventNode node = CreateNode(tree, bp);
                    if (node.IsMerge)
                        ResolveMergePartner(node, tree, trees, sameTreeClaims, ic);
                    else
                        node.Partner = new DockPartnerResolution
                        {
                            Status = DockPartnerStatus.UnstampedZero,
                        };

                    ApplyVisibility(node, isRecordingVisible, tally);
                    graph.Nodes.Add(node);
                    graph.NodesByBranchPointId[bp.Id] = node;
                    Count(tally, node);
                }
            }

            graph.Nodes.Sort(CompareNodes);
            BuildIndexes(graph);
            sw.Stop();
            EmitBuildSummary(graph, tally, sw, ic);
            return graph;
        }

        /// <summary>
        /// Names the OTHER party of a merge relative to <paramref name="viewerRecordingId"/>
        /// (design 6.5). Direction rules:
        /// <list type="bullet">
        /// <item>TwoParentSameTree: the partner is the parent that is NOT the viewer. A viewer
        /// that is neither parent (including the merged child, where both parents are equally
        /// "the partner" and naming one would be arbitrary) declines.</item>
        /// <item>CrossTree / SameTreeRecovered: when the viewer IS the claimed recording the
        /// partner is the merged stack (the controller side - today the unnamed side); when it is
        /// a parent or the merged child the partner is the claimed recording. Any OTHER viewer
        /// declines - it has no direction, and guessing "controller side" would name a later
        /// segment of the claimed line its own earliest segment.</item>
        /// </list>
        /// Unresolvable statuses (UnstampedZero / NoMatch / GuidRejected), Undock nodes and
        /// visibility-flagged nodes all return false: the tables stay SILENT exactly where they
        /// are silent today (design Q4 keeps the generic "unrecorded vessel" text to the digest).
        /// <paramref name="missionNameResolver"/> is <c>(treeId, recordingId) -&gt; mission name
        /// or null</c>, supplied by the UI so this core stays free of Mission references. Pure.
        /// </summary>
        internal static bool TryDescribePartner(
            DockEventGraph graph,
            string branchPointId,
            string viewerRecordingId,
            Func<string, string, string> missionNameResolver,
            out DockPartnerDescription description)
        {
            description = default(DockPartnerDescription);
            if (graph == null || string.IsNullOrEmpty(branchPointId)
                || !graph.NodesByBranchPointId.TryGetValue(branchPointId, out DockEventNode node)
                || node == null || !node.IsMerge || node.VisibilityFlagged)
                return false;

            string partnerRecordingId;
            string partnerVesselName;
            string partnerTreeId;
            switch (node.Partner.Status)
            {
                case DockPartnerStatus.TwoParentSameTree:
                {
                    int viewerIdx = IndexOfOrdinal(node.ParentRecordingIds, viewerRecordingId);
                    if (viewerIdx < 0 || node.ParentRecordingIds.Count != 2)
                        return false;
                    int otherIdx = 1 - viewerIdx;
                    partnerRecordingId = node.ParentRecordingIds[otherIdx];
                    partnerVesselName = otherIdx < node.ParentVesselNames.Count
                        ? node.ParentVesselNames[otherIdx]
                        : null;
                    partnerTreeId = node.TreeId;
                    break;
                }
                case DockPartnerStatus.CrossTree:
                case DockPartnerStatus.SameTreeRecovered:
                {
                    bool viewerIsClaimed = !string.IsNullOrEmpty(viewerRecordingId)
                        && string.Equals(viewerRecordingId, node.Partner.PartnerRecordingId,
                            StringComparison.Ordinal);
                    // A viewer that is not a PARTICIPANT of this merge cannot be given a
                    // direction, and defaulting it to "the controller side" is not safe: the
                    // claimed line can carry more than one recording (a switch / chain
                    // continuation D0 -> D1 claims the EARLIEST, D0), so a caller passing D1
                    // would be told its partner is D0 - its own vessel, the exact regression
                    // this API exists to prevent. Decline instead; PR3's own call sites pass a
                    // parent, the merged child, or the claimed recording.
                    if (!viewerIsClaimed
                        && !IsParticipant(node, viewerRecordingId))
                        return false;
                    if (viewerIsClaimed)
                    {
                        partnerRecordingId = node.MergedChildRecordingId;
                        partnerVesselName = node.MergedChildVesselName;
                        partnerTreeId = node.TreeId;
                    }
                    else
                    {
                        partnerRecordingId = node.Partner.PartnerRecordingId;
                        partnerVesselName = node.Partner.PartnerVesselName;
                        partnerTreeId = node.Partner.PartnerTreeId;
                    }
                    break;
                }
                default:
                    return false;   // UnstampedZero / NoMatch / GuidRejected: silence, as today
            }

            if (string.IsNullOrEmpty(partnerRecordingId) || string.IsNullOrEmpty(partnerVesselName))
                return false;   // nothing nameable

            description = new DockPartnerDescription
            {
                PartnerVesselName = partnerVesselName,
                PartnerMissionName = missionNameResolver != null
                    ? missionNameResolver(partnerTreeId, partnerRecordingId)
                    : null,
                PartnerRecordingId = partnerRecordingId,
                PartnerTreeId = partnerTreeId,
                PartnerMissionId = null,
                Status = node.Partner.Status,
            };
            return true;
        }

        // ---- private build helpers ----

        private sealed class BuildTally
        {
            public int Dock;
            public int Board;
            public int Undock;
            public int TwoParent;
            public int CrossTree;
            public int SameTreeRecovered;
            public int Zero;
            public int NoMatch;
            public int GuidRejected;
            public int VisibilityFlagged;
        }

        private static Dictionary<string, Dictionary<string, SameTreeDockClaim>> IndexSameTreeClaims(
            IReadOnlyList<RecordingTree> trees)
        {
            var byTree = new Dictionary<string, Dictionary<string, SameTreeDockClaim>>(
                StringComparer.Ordinal);
            for (int t = 0; t < trees.Count; t++)
            {
                RecordingTree tree = trees[t];
                if (tree == null || string.IsNullOrEmpty(tree.Id) || byTree.ContainsKey(tree.Id))
                    continue;
                List<SameTreeDockClaim> claims = MissionCrossTreeDock.FindSameTreeDockClaims(tree);
                if (claims.Count == 0)
                    continue;
                var byBp = new Dictionary<string, SameTreeDockClaim>(StringComparer.Ordinal);
                for (int c = 0; c < claims.Count; c++)
                    if (!string.IsNullOrEmpty(claims[c].BranchPointId))
                        byBp[claims[c].BranchPointId] = claims[c];
                byTree[tree.Id] = byBp;
            }
            return byTree;
        }

        private static DockEventNode CreateNode(RecordingTree tree, BranchPoint bp)
        {
            var node = new DockEventNode
            {
                BranchPointId = bp.Id,
                TreeId = tree.Id,
                UT = bp.UT,
                Kind = bp.Type,
                MergeCause = bp.Type == BranchPointType.Undock ? null : bp.MergeCause,
                PartnerPid = bp.Type == BranchPointType.Undock ? 0u : bp.TargetVesselPersistentId,
            };
            if (bp.ParentRecordingIds != null)
            {
                for (int i = 0; i < bp.ParentRecordingIds.Count; i++)
                {
                    string id = bp.ParentRecordingIds[i];
                    node.ParentRecordingIds.Add(id);
                    node.ParentVesselNames.Add(LookupVesselName(tree, id));
                }
            }
            if (bp.ChildRecordingIds != null)
                node.ChildRecordingIds.AddRange(bp.ChildRecordingIds);
            if (node.ChildRecordingIds.Count > 0)
            {
                node.MergedChildRecordingId = node.ChildRecordingIds[0];
                node.MergedChildVesselName = LookupVesselName(tree, node.MergedChildRecordingId);
            }
            return node;
        }

        private static string LookupVesselName(RecordingTree tree, string recordingId)
        {
            if (tree?.Recordings == null || string.IsNullOrEmpty(recordingId))
                return null;
            return tree.Recordings.TryGetValue(recordingId, out Recording rec) && rec != null
                ? rec.VesselName
                : null;
        }

        // Design 6.3 step 2: first match wins, in this exact order. The ordering is the
        // contract - a two-parent merge already closes the partner's line (so the same-tree
        // scanner never sees it), and cross-tree beats same-tree so the AB/CD shape does not
        // get re-derived as a self-claim.
        private static void ResolveMergePartner(
            DockEventNode node,
            RecordingTree owningTree,
            IReadOnlyList<RecordingTree> trees,
            Dictionary<string, SameTreeDockClaim> sameTreeClaims,
            CultureInfo ic)
        {
            // (a) two-parent same-tree merge: the partner is the other parent, viewer-relative.
            if (node.ParentRecordingIds.Count == 2)
            {
                node.Partner = new DockPartnerResolution
                {
                    Status = DockPartnerStatus.TwoParentSameTree,
                    PartnerTreeId = node.TreeId,
                };
                return;
            }

            // (b) nothing stamped: the degrade-to-today path.
            if (node.PartnerPid == 0)
            {
                node.Partner = new DockPartnerResolution { Status = DockPartnerStatus.UnstampedZero };
                return;
            }

            // (c) cross-tree: FindLinks' claim rule, evaluated from the OWNING tree's side.
            // The expected launch guid comes from the owning tree (where the partner's pid may
            // survive on the merged child / departing offshoot); earliest-starting match across
            // all other trees wins, exactly like FindLinks' earliest-start preference.
            // ONE DELIBERATE DIVERGENCE: FindLinks additionally requires the claiming branch
            // point's merged child to resolve in its own tree, because a partner-journey row
            // needs that recording's name. The graph does not - a malformed branch point still
            // gets a NODE (topology is what the graph is for), and TryDescribePartner declines
            // the partner-side direction on its own when the merged child has no name.
            string expectedGuid = MissionCrossTreeDock.ResolveLaunchGuidForPid(
                owningTree, node.PartnerPid);
            Recording bestCross = null;
            RecordingTree bestCrossTree = null;
            bool rejectedByGuid = false;
            for (int t = 0; t < trees.Count; t++)
            {
                RecordingTree other = trees[t];
                if (other?.Recordings == null || string.IsNullOrEmpty(other.Id)
                    || string.Equals(other.Id, owningTree.Id, StringComparison.Ordinal))
                    continue;
                Recording claimed = MissionCrossTreeDock.FindClaimedRecording(
                    other, node.PartnerPid, expectedGuid, out bool treeRejected);
                if (treeRejected)
                    rejectedByGuid = true;
                if (claimed == null)
                    continue;
                if (bestCross == null || claimed.StartUT < bestCross.StartUT)
                {
                    bestCross = claimed;
                    bestCrossTree = other;
                }
            }
            if (bestCross != null)
            {
                node.Partner = new DockPartnerResolution
                {
                    Status = DockPartnerStatus.CrossTree,
                    PartnerRecordingId = bestCross.RecordingId,
                    PartnerTreeId = bestCrossTree.Id,
                    PartnerVesselName = bestCross.VesselName,
                    PartnerLaunchGuid = bestCross.RecordedVesselGuid,
                };
                return;
            }

            // (d) same-tree recovery (design 6.2, the A->D shape).
            if (sameTreeClaims != null
                && sameTreeClaims.TryGetValue(node.BranchPointId, out SameTreeDockClaim claim))
            {
                Recording rec = null;
                owningTree.Recordings?.TryGetValue(claim.ClaimedRecordingId, out rec);
                node.Partner = new DockPartnerResolution
                {
                    Status = DockPartnerStatus.SameTreeRecovered,
                    PartnerRecordingId = claim.ClaimedRecordingId,
                    PartnerTreeId = owningTree.Id,
                    PartnerVesselName = rec?.VesselName,
                    PartnerLaunchGuid = rec?.RecordedVesselGuid,
                };
                if (!SuppressLogging)
                    ParsekLog.Verbose(Tag,
                        "sameTreeRecovered bp=" + node.BranchPointId + " tree=" + node.TreeId
                        + " partnerPid=" + node.PartnerPid.ToString(ic)
                        + " claimed=" + (claim.ClaimedRecordingId ?? "?"));
                return;
            }

            // (e) a pid match existed but the guid gate conclusively rejected it, and nothing
            // else matched. The one failure where an affordance vanishes with no other trace,
            // so it leaves evidence (FindLinks' logging rationale, design 6.3 step 2e).
            if (rejectedByGuid)
            {
                node.Partner = new DockPartnerResolution { Status = DockPartnerStatus.GuidRejected };
                if (!SuppressLogging)
                    ParsekLog.Verbose(Tag,
                        "guidRejected bp=" + node.BranchPointId + " tree=" + node.TreeId
                        + " partnerPid=" + node.PartnerPid.ToString(ic)
                        + " expectedGuid=" + (expectedGuid ?? "unknown"));
                return;
            }

            // (f) stamped, but no recording carries that pid anywhere.
            node.Partner = new DockPartnerResolution { Status = DockPartnerStatus.NoMatch };
        }

        private static void ApplyVisibility(
            DockEventNode node, Func<string, bool> isRecordingVisible, BuildTally tally)
        {
            if (isRecordingVisible == null)
                return;
            // Merges hang off their merged child; an Undock node hangs off the parent that split.
            string subject = node.IsMerge
                ? node.MergedChildRecordingId
                : (node.ParentRecordingIds.Count > 0 ? node.ParentRecordingIds[0] : null);
            if (string.IsNullOrEmpty(subject) || isRecordingVisible(subject))
                return;
            node.VisibilityFlagged = true;
            tally.VisibilityFlagged++;
        }

        private static void Count(BuildTally tally, DockEventNode node)
        {
            switch (node.Kind)
            {
                case BranchPointType.Dock: tally.Dock++; break;
                case BranchPointType.Board: tally.Board++; break;
                default: tally.Undock++; break;
            }
            if (!node.IsMerge)
                return;
            switch (node.Partner.Status)
            {
                case DockPartnerStatus.TwoParentSameTree: tally.TwoParent++; break;
                case DockPartnerStatus.CrossTree: tally.CrossTree++; break;
                case DockPartnerStatus.SameTreeRecovered: tally.SameTreeRecovered++; break;
                case DockPartnerStatus.UnstampedZero: tally.Zero++; break;
                case DockPartnerStatus.NoMatch: tally.NoMatch++; break;
                case DockPartnerStatus.GuidRejected: tally.GuidRejected++; break;
            }
        }

        private static int CompareNodes(DockEventNode a, DockEventNode b)
        {
            int cmp = a.UT.CompareTo(b.UT);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.BranchPointId, b.BranchPointId);
        }

        // Design 6.3 step 4. Built AFTER the sort so every per-key list inherits node order.
        private static void BuildIndexes(DockEventGraph graph)
        {
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                DockEventNode node = graph.Nodes[i];
                AddTo(graph.NodesByTreeId, node.TreeId, node);
                // A CrossTree node registers under the PARTNER's tree too, so the partner
                // mission's surfaces see a dock its own tree never recorded.
                if (!string.IsNullOrEmpty(node.Partner.PartnerTreeId)
                    && !string.Equals(node.Partner.PartnerTreeId, node.TreeId, StringComparison.Ordinal))
                    AddTo(graph.NodesByTreeId, node.Partner.PartnerTreeId, node);

                for (int p = 0; p < node.ParentRecordingIds.Count; p++)
                    AddTo(graph.ParticipantsByRecordingId, node.ParentRecordingIds[p], node);
                AddTo(graph.ParticipantsByRecordingId, node.MergedChildRecordingId, node);
                AddTo(graph.ParticipantsByRecordingId, node.Partner.PartnerRecordingId, node);

                if (node.Partner.Status == DockPartnerStatus.CrossTree
                    && !string.IsNullOrEmpty(node.TreeId)
                    && !string.IsNullOrEmpty(node.Partner.PartnerTreeId))
                    graph.DockConnectedTreePairs.Add((node.TreeId, node.Partner.PartnerTreeId));
            }
        }

        private static void AddTo(
            Dictionary<string, List<DockEventNode>> index, string key, DockEventNode node)
        {
            if (string.IsNullOrEmpty(key))
                return;
            if (!index.TryGetValue(key, out List<DockEventNode> list))
            {
                list = new List<DockEventNode>();
                index[key] = list;
            }
            if (!list.Contains(node))
                list.Add(node);
        }

        // A recording this node actually involves: one of its parents, or its merged child.
        // (The claimed recording is checked separately by the caller, which needs the direction.)
        private static bool IsParticipant(DockEventNode node, string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return false;
            return IndexOfOrdinal(node.ParentRecordingIds, recordingId) >= 0
                || string.Equals(recordingId, node.MergedChildRecordingId, StringComparison.Ordinal);
        }

        private static int IndexOfOrdinal(List<string> ids, string value)
        {
            if (ids == null || string.IsNullOrEmpty(value))
                return -1;
            for (int i = 0; i < ids.Count; i++)
                if (string.Equals(ids[i], value, StringComparison.Ordinal))
                    return i;
            return -1;
        }

        // Design 15.2: ONE grep-stable line per rebuild. The UnstampedZero population is
        // deliberately counted here rather than logged per node (it is the today-behavior path).
        private static void EmitBuildSummary(
            DockEventGraph graph, BuildTally tally, Stopwatch sw, CultureInfo ic)
        {
            if (SuppressLogging)
                return;
            ParsekLog.Verbose(Tag,
                "build nodes=" + graph.Nodes.Count.ToString(ic)
                + " dock=" + tally.Dock.ToString(ic)
                + " board=" + tally.Board.ToString(ic)
                + " undock=" + tally.Undock.ToString(ic)
                + " twoParent=" + tally.TwoParent.ToString(ic)
                + " crossTree=" + tally.CrossTree.ToString(ic)
                + " sameTreeRecovered=" + tally.SameTreeRecovered.ToString(ic)
                + " zero=" + tally.Zero.ToString(ic)
                + " noMatch=" + tally.NoMatch.ToString(ic)
                + " guidRejected=" + tally.GuidRejected.ToString(ic)
                + " visibilityFlagged=" + tally.VisibilityFlagged.ToString(ic)
                + " ms=" + sw.Elapsed.TotalMilliseconds.ToString("F3", ic));
        }
    }
}
