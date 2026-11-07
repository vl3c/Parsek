using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // Loading the committed recording trees from the scenario node and dropping the recordings
    // whose sidecars failed to hydrate.
    public partial class ParsekScenario
    {
        /// <summary>
        /// Loads committed recording trees from RECORDING_TREE ConfigNodes. Clears stale
        /// trees, loads each tree and its bulk data, and adds tree recordings to the
        /// committed recordings list for ghost playback.
        /// </summary>
        internal static void LoadRecordingTrees(ConfigNode node, IReadOnlyList<Recording> recordings)
        {
            // Always clear CommittedTrees — if the new save has no trees, stale trees
            // from the previous save would otherwise persist and contaminate this save.
            ConfigNode[] treeNodes = node.GetNodes("RECORDING_TREE");
            var committedTrees = RecordingStore.CommittedTrees;
            committedTrees.Clear();
            // Inert trees (GC-3) share the committed trees' lifetime: replaced here.
            InertRecordingTrees.BeginLoad();
            var namedCommittedRecordingIds = new HashSet<string>(StringComparer.Ordinal);
            ParsekLog.Info("Scenario",
                $"OnLoad initial: cleared CommittedTrees, loading {treeNodes.Length} tree(s)");

            if (treeNodes.Length > 0)
            {
                int treeRecCount = 0;
                int treeTotalPoints = 0;
                int treeTotalOrbitSegments = 0;
                int treeTotalPartEvents = 0;
                int treeWithTrackSections = 0;
                int treeWithSnapshots = 0;
                int activeTreeCount = 0;
                int pendingTreeCount = 0;
                for (int t = 0; t < treeNodes.Length; t++)
                {
                    var treeNode = treeNodes[t];

                    // Active-tree marker: these go into the pending-limbo slot, NOT into
                    // committedTrees. TryRestoreActiveTreeNode earlier in OnLoad may have
                    // already consumed this node, so skip it here either way.
                    if (IsActiveTreeNode(treeNode))
                    {
                        activeTreeCount++;
                        InertRecordingTrees.NoteDroppedParkedNode(treeNode, "active");
                        continue;
                    }
                    if (IsPendingTreeNode(treeNode))
                    {
                        pendingTreeCount++;
                        InertRecordingTrees.NoteDroppedParkedNode(treeNode, "pending");
                        continue;
                    }

                    CollectNamedCommittedRecordingIds(treeNode, namedCommittedRecordingIds);
                    var tree = RecordingTree.Load(treeNode);
                    // GC-3: a committed tree the schema gate rejected, wholly or in part (T6), is
                    // kept inert and written back verbatim by SaveTreeRecordings (a pass-through,
                    // not a migration); anything else that loaded empty is skipped as before.
                    string inertReason = InertRecordingTrees.ClassifyCommittedTreeSchemaRejection(treeNode, tree);
                    if (inertReason != null)
                    {
                        InertRecordingTrees.SetAside(treeNode, inertReason);
                        ParsekLog.Warn("Scenario",
                            $"LoadRecordingTrees: tree '{tree.TreeName}' id={tree.Id} was rejected by " +
                            $"the schema gate (reason={inertReason}); kept inert, not loaded " +
                            $"(loadedRecordings={(tree.Recordings != null ? tree.Recordings.Count : 0).ToString(CultureInfo.InvariantCulture)})");
                        continue;
                    }
                    if (tree.Recordings == null || tree.Recordings.Count == 0)
                    {
                        ParsekLog.Warn("Scenario",
                            $"LoadRecordingTrees: skipped tree '{tree.TreeName}' id={tree.Id} " +
                            "because it holds no recording");
                        continue;
                    }
                    int sidecarHydrationFailures = 0;
                    int syntheticFixtureFailures = 0;

                    // Load bulk data from external files for each recording in the tree.
                    foreach (var rec in tree.Recordings.Values)
                    {
                        if (!RecordingSidecarStore.LoadRecordingFiles(rec))
                        {
                            sidecarHydrationFailures++;
                            // Bug #422: distinguish synthetic-fixture markers (no .prec sidecar,
                            // zero trajectory points — expected for injected test recordings)
                            // from genuine degradations (parse error, id mismatch, stale epoch,
                            // snapshot failure, etc). The per-recording INFO line already
                            // describes the synthetic case; the roll-up WARN would duplicate it.
                            if (IsSyntheticFixtureSidecarMarker(rec))
                                syntheticFixtureFailures++;
                        }
                    }

                    int droppedHydrationFailures = DropFailedSidecarHydrationRecordings(
                        tree,
                        "LoadRecordingTrees");
                    if (tree.Recordings == null || tree.Recordings.Count == 0)
                    {
                        ParsekLog.Warn("Scenario",
                            $"LoadRecordingTrees: skipped tree '{tree.TreeName}' id={tree.Id} " +
                            $"after dropping {droppedHydrationFailures} recording(s) with incompatible sidecars");
                        continue;
                    }

                    if (RepairMissingContiguousChainPredecessors(tree, "LoadRecordingTrees") > 0)
                        tree.RebuildBackgroundMap();
                    // A tree written before a split moved its parent links names the head for
                    // events on a later segment; point them at the segment that holds them.
                    SplitParentLinks.RepairStaleParents(tree, "LoadRecordingTrees");

                    // Phase 8 review-pass-3: deferred co-bubble sweeps
                    // moved out of the per-tree loop. Cross-tree co-bubble
                    // traces are possible because commit-time DetectAndStore
                    // scans CommittedRecordings (which spans trees), so a
                    // deferred entry in tree T1 may reference a peer in
                    // tree T2. Per-tree sweeps fired before T2 was visible
                    // and silently dropped cross-tree recomputes. The
                    // global sweep runs ONCE after the loop with the full
                    // union of just-hydrated committed recordings — handles
                    // intra-tree AND cross-tree correctly.
                    committedTrees.Add(tree);

                    // Add tree recordings to CommittedRecordings for ghost playback
                    foreach (var rec in tree.Recordings.Values)
                    {
                        RecordingStore.AddCommittedInternal(rec);
                        treeRecCount++;
                        treeTotalPoints += rec.Points.Count;
                        treeTotalOrbitSegments += rec.OrbitSegments.Count;
                        treeTotalPartEvents += rec.PartEvents.Count;
                        if (rec.TrackSections != null && rec.TrackSections.Count > 0) treeWithTrackSections++;
                        if (rec.VesselSnapshot != null) treeWithSnapshots++;
                    }

                    ScenarioLog($"[Parsek Scenario] Loaded tree '{tree.TreeName}': " +
                        $"{tree.Recordings.Count} recordings, {tree.BranchPoints.Count} branch points");
                    EmitSidecarHydrationRollup(
                        tree.TreeName, sidecarHydrationFailures, syntheticFixtureFailures);
                }

                ParsekLog.Verbose("Scenario",
                    $"Loaded {treeNodes.Length} tree nodes ({treeRecCount} committed recordings + " +
                    $"{activeTreeCount} active-tree marker(s) + {pendingTreeCount} pending-tree marker(s)): " +
                    $"{treeTotalPoints} points, " +
                    $"{treeTotalOrbitSegments} orbit segments, {treeTotalPartEvents} part events, " +
                    $"{treeWithTrackSections} with track sections, {treeWithSnapshots} with snapshots");
            }
            InertRecordingTrees.LogLoadSummary();
            NoteLoadDroppedRecordings(namedCommittedRecordingIds, committedTrees);
        }

        /// <summary>
        /// Bug #422: a sidecar-hydration failure is a "synthetic-fixture marker" — not a
        /// genuine degradation — when the <c>.prec</c> trajectory file is simply missing
        /// and the recording carries zero trajectory points. That combination is the
        /// expected shape of an injected test recording whose <c>.prec</c> was never
        /// written. All other failure reasons (parse errors, id mismatches, stale
        /// epochs, snapshot failures, exceptions) indicate real data problems.
        /// </summary>
        internal static bool IsSyntheticFixtureSidecarMarker(Recording rec)
        {
            if (rec == null) return false;
            if (rec.SidecarLoadFailureReason != "trajectory-missing") return false;
            return rec.Points == null || rec.Points.Count == 0;
        }

        internal static int DropFailedSidecarHydrationRecordings(RecordingTree tree, string context)
        {
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0)
                return 0;

            HashSet<string> rejectedIds = null;
            var examples = new List<string>();
            foreach (KeyValuePair<string, Recording> kvp in tree.Recordings)
            {
                Recording rec = kvp.Value;
                if (rec == null || !rec.SidecarLoadFailed)
                    continue;
                if (IsSyntheticFixtureSidecarMarker(rec))
                    continue;

                if (rejectedIds == null)
                    rejectedIds = new HashSet<string>(StringComparer.Ordinal);

                if (!string.IsNullOrEmpty(kvp.Key))
                    rejectedIds.Add(kvp.Key);
                if (!string.IsNullOrEmpty(rec.RecordingId))
                    rejectedIds.Add(rec.RecordingId);

                if (examples.Count < 3)
                {
                    examples.Add(
                        $"{rec.RecordingId ?? kvp.Key ?? "<no-id>"}:{rec.SidecarLoadFailureReason ?? "<unknown>"}");
                }
            }

            if (rejectedIds == null || rejectedIds.Count == 0)
                return 0;

            if (!string.IsNullOrEmpty(tree.RootRecordingId)
                && rejectedIds.Contains(tree.RootRecordingId))
            {
                int removedRecordings = tree.Recordings.Count;
                int rootRemovedBranchPoints = tree.BranchPoints != null ? tree.BranchPoints.Count : 0;
                string rootRecordingId = tree.RootRecordingId;
                tree.RootRecordingId = string.Empty;
                tree.ActiveRecordingId = null;
                tree.Recordings.Clear();
                if (tree.BranchPoints != null)
                    tree.BranchPoints.Clear();
                if (tree.BackgroundMap != null)
                    tree.BackgroundMap.Clear();
                ParsekLog.Warn("Scenario",
                    $"{context}: dropped entire tree '{tree.TreeName}' id={tree.Id ?? "<none>"} " +
                    $"because root recording '{rootRecordingId}' had an incompatible sidecar; " +
                    $"removedRecordings={removedRecordings} removedBranchPoints={rootRemovedBranchPoints} " +
                    $"examples=[{string.Join(", ", examples.ToArray())}]");
                return removedRecordings;
            }

            if (!string.IsNullOrEmpty(tree.ActiveRecordingId)
                && rejectedIds.Contains(tree.ActiveRecordingId))
            {
                tree.ActiveRecordingId = null;
            }

            int removed = PruneFutureOnlyRecordings(tree, rejectedIds);
            int removedBranchPoints = RemoveEmptyBranchPoints(tree);
            foreach (Recording rec in tree.Recordings.Values)
            {
                if (rec == null)
                    continue;
                if (!string.IsNullOrEmpty(rec.ParentAnchorRecordingId)
                    && rejectedIds.Contains(rec.ParentAnchorRecordingId))
                {
                    rec.ParentAnchorRecordingId = null;
                }
            }

            tree.RebuildBackgroundMap();
            ParsekLog.Warn("Scenario",
                $"{context}: dropped {removed} recording(s) with incompatible sidecars " +
                $"from tree '{tree.TreeName}' id={tree.Id ?? "<none>"} " +
                $"removedBranchPoints={removedBranchPoints} examples=[{string.Join(", ", examples.ToArray())}]");
            return removed;
        }

        /// <summary>
        /// Bug #422: emits the per-tree sidecar-hydration roll-up. Suppresses the WARN
        /// (downgrades to INFO) when every failure in the batch is the synthetic-fixture
        /// marker — the per-recording INFO lines above already describe those cases and
        /// the WARN would duplicate them, making a clean test save look unhealthy. Any
        /// genuine degradation in the batch still emits the WARN. Internal so unit tests
        /// can exercise both branches without building a full RecordingTree fixture.
        /// </summary>
        internal static void EmitSidecarHydrationRollup(
            string treeName,
            int totalFailures,
            int syntheticFixtureFailures,
            string treeKind = "committed tree")
        {
            if (totalFailures <= 0) return;

            if (syntheticFixtureFailures >= totalFailures)
            {
                ParsekLog.Info("Scenario",
                    $"OnLoad: {treeKind} '{treeName}' had {totalFailures} " +
                    $"synthetic-fixture recording(s) with missing .prec sidecar " +
                    $"(no genuine degradations; per-recording INFO lines above describe each)");
                return;
            }

            int genuineFailures = totalFailures - syntheticFixtureFailures;
            ParsekLog.Warn("Scenario",
                $"OnLoad: {treeKind} '{treeName}' had {totalFailures} " +
                $"recording(s) with sidecar hydration failures " +
                $"({genuineFailures} genuine, {syntheticFixtureFailures} synthetic-fixture)");
        }

        /// <summary>
        /// Returns true if a RECORDING_TREE ConfigNode represents an active (in-flight)
        /// tree written by <c>SaveActiveTreeIfAny</c>, distinguished by the
        /// <c>isActive=True</c> key.
        /// </summary>
        /// <summary>
        /// Collects every recording id the save node declares in ANY RECORDING_TREE -
        /// committed, active-marked and pending-marked alike - as the durable input to the
        /// committed-overlap discard guard. Pure over the ConfigNode, so the guard's input
        /// is unit-testable without a live save.
        ///
        /// <para><b>Why the parked (isActive / isPending) nodes count too.</b> The naming
        /// says "committed", and it is tempting to read the parked nodes as "the tree being
        /// discarded, therefore fair game". They are not: <c>TryRestoreActiveTreeNode</c> /
        /// <c>TryRestorePendingTreeNode</c> restore exactly those nodes LATER IN THIS SAME
        /// OnLoad. The population this hint has to protect is "recordings this save is about
        /// to have", and a parked node's recordings are in it - their sidecars are the ones
        /// the restore will hydrate from. Deleting them in the discard window loses data the
        /// load was seconds away from bringing back. The tree actually being discarded is the
        /// IN-MEMORY stale pending tree left over from a previous save; it is not in this
        /// node at all, so including the parked nodes cannot protect it by accident.</para>
        ///
        /// <para>Feeds <see cref="RecordingStore.SetDurableCommittedRecordingIdHint"/> for
        /// the cold-load window in which <c>DiscardStalePendingState</c> runs before
        /// <c>LoadRecordingTrees</c> - see that call site for the data loss it prevents.</para>
        /// </summary>
        internal static HashSet<string> CollectCommittedRecordingIdsFromScenarioNode(ConfigNode node)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (node == null)
                return ids;
            foreach (ConfigNode treeNode in node.GetNodes("RECORDING_TREE"))
            {
                if (treeNode == null) continue;
                foreach (ConfigNode recNode in treeNode.GetNodes("RECORDING"))
                {
                    string recordingId = recNode?.GetValue("recordingId");
                    if (!string.IsNullOrEmpty(recordingId))
                        ids.Add(recordingId);
                }
            }
            return ids;
        }

        /// <summary>
        /// Revert-detection arithmetic over a SCENARIO node: the number of committed
        /// recordings the save claims. All committed recordings are serialized under
        /// RECORDING_TREE nodes, so this is the RECORDING children of every committed
        /// tree node. Active-tree (in-flight) and pending-tree marker nodes are skipped -
        /// their recordings are not committed, and counting them reads a live flight as
        /// extra committed data and suppresses the revert classification.
        /// </summary>
        internal static int CountSavedCommittedRecordingNodes(ConfigNode node)
        {
            int savedTreeRecCount = 0;
            if (node == null) return 0;

            ConfigNode[] treeNodes = node.GetNodes("RECORDING_TREE");
            for (int t = 0; t < treeNodes.Length; t++)
            {
                if (IsActiveTreeNode(treeNodes[t])) continue;
                if (IsPendingTreeNode(treeNodes[t])) continue;
                savedTreeRecCount += treeNodes[t].GetNodes("RECORDING").Length;
            }
            return savedTreeRecCount;
        }
    }
}
