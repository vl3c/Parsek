using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // Keeping committed trees across a load fault: preserve the loaded state, carry a committed
    // tree node forward from the loaded save, rehydrate from the disk save.
    public partial class ParsekScenario
    {
        /// <summary>
        /// Testing seam for <see cref="PreserveRecordingStateIfLoadFault"/>: lets tests drive the
        /// committed-tree-state-loaded flag the guard reads. Production sets it only inside the
        /// cold-load OnLoad (Unity-gated, so it never runs in the xUnit host — where it defaults
        /// false, i.e. the fault path). Tests set true to pin the successful-load / wipe skip
        /// path, and reset it in teardown.
        /// </summary>
        internal static bool CommittedTreeStateLoadedForTesting
        {
            get => committedTreeStateLoaded;
            set => committedTreeStateLoaded = value;
        }

        /// <summary>
        /// Load-fault data-loss guard (the OnSave-side partner of
        /// <see cref="ParsekFileCollector"/>'s refuse-to-delete guard). Fires
        /// ONLY on the "scenario load lost its tree state" fingerprint: an INCOMPLETE cold
        /// load (<c>initialLoadDone == false</c>) left the COMMITTED store empty while the
        /// recordings directory still holds stranded sidecar bulk data. All three conditions
        /// matter:
        /// <list type="bullet">
        ///   <item><description><c>!committedTreeStateLoaded</c> — the committed tree/mission load
        ///   did not complete. A successful/fresh load and every INTENTIONAL empty state (an
        ///   internal clear that runs against a fully-loaded store) leave
        ///   <c>committedTreeStateLoaded == true</c>, so the guard can never resurrect
        ///   deliberately-removed recordings. Only a throw DURING tree/mission load leaves it
        ///   false. (initialLoadDone is unusable for this: it is set true at cold-start ENTRY,
        ///   before the trees load, so a mid-load throw would leave it true.)</description></item>
        ///   <item><description>committed count == 0 — the committed history is the surface
        ///   that gets hollowed. Gating on the COMMITTED count (not the total RECORDING_TREE
        ///   node count) means an in-flight active/pending recording — which writes its own
        ///   node via <see cref="SaveActiveTreeIfAny"/>/<see cref="SavePendingTreeIfAny"/> —
        ///   does NOT mask the loss of the committed trees.</description></item>
        ///   <item><description>stranded sidecars &gt; 0 — deleting a recording removes its
        ///   sidecars (<see cref="TreeDiscardService.RemoveRecordingAt"/> → <c>DeleteRecordingFiles</c>),
        ///   so leftover sidecars corroborate that committed recordings existed and were lost,
        ///   not that the save is genuinely empty.</description></item>
        /// </list>
        /// Writing 0 committed trees here would HOLLOW the campaign .sfs — dropping every
        /// committed tree + mission while the recorded data survives only as orphaned sidecars
        /// (the way a save is progressively emptied across sessions). Instead, re-hydrate the
        /// committed RECORDING_TREE + MISSION nodes from the on-disk save (which KSP has NOT
        /// overwritten yet at OnSave time) so the save keeps its state until the next load can
        /// load it. Additive: it only ADDS the on-disk committed nodes, preserving any live
        /// active/pending node the caller already wrote; it never touches a populated save.
        /// Never throws. NOTE: it reads the campaign persistent.sfs; a save targeting a quicksave
        /// slot during the (rare) fault window will re-inject the campaign's committed trees into
        /// that slot too, which is harmless (a quicksave is not the campaign save).
        /// </summary>
        internal static void PreserveRecordingStateIfLoadFault(ConfigNode node)
        {
            if (node == null)
                return;
            // Cheap gates first (no disk I/O on the common path): a non-empty committed store is
            // a normal populated save, and a completed tree/mission load (fresh career, populated
            // save, or an intentional wipe against a loaded store) leaves committedTreeStateLoaded
            // == true. Only a load that THREW during tree/mission load leaves it false — the case
            // that can hollow the .sfs. NB: initialLoadDone is deliberately NOT used here; it is
            // flipped true at cold-start entry (before the trees load), so it cannot distinguish a
            // completed load from a mid-load fault.
            if (RecordingStore.CommittedTrees.Count != 0 || committedTreeStateLoaded)
                return;
            var strandedIds = RecordingFileJanitor.CollectSidecarIdsOnDisk();
            if (!ShouldPreserveCommittedTreeState(
                    committedTreeStateLoaded, RecordingStore.CommittedTrees.Count, strandedIds.Count))
                return;

            string persistentPath = PersistentSavePathOverrideForTesting
                ?? RecordingPaths.ResolveSaveScopedPath(PersistentSaveFileName);
            int rehydratedTrees = TryRehydrateCommittedTreesAndMissionsFromDiskSave(
                node, persistentPath, out int rehydratedMissions);

            if (rehydratedTrees > 0)
            {
                ParsekLog.Warn("Scenario",
                    $"OnSave load-fault guard: committed store empty after an incomplete load, but {strandedIds.Count} "
                    + "stranded sidecar recording ID(s) on disk indicate the scenario load lost its tree state. "
                    + $"Re-hydrated {rehydratedTrees} committed RECORDING_TREE + {rehydratedMissions} MISSION node(s) "
                    + "from the on-disk save so it was NOT hollowed. Investigate the originating load fault.");
            }
            else
            {
                ParsekLog.Warn("Scenario",
                    $"OnSave load-fault guard: writing 0 committed RECORDING_TREE nodes after an incomplete load; disk "
                    + $"has {strandedIds.Count} stranded sidecar recording ID(s) but the on-disk save has no committed "
                    + "tree metadata to preserve. Sidecars are preserved by the file collector; recover the trees from "
                    + "quicksave.sfs or a backup.");
            }
        }

        /// <summary>
        /// Pure trigger for <see cref="PreserveRecordingStateIfLoadFault"/>: fires only on the
        /// load-fault fingerprint — the committed tree/mission load did NOT complete
        /// (<paramref name="committedTreeStateLoaded"/> == false) yet the committed store is empty
        /// while stranded sidecars remain on disk. A successful/fresh load or an intentional wipe
        /// runs with <paramref name="committedTreeStateLoaded"/> == true; a genuinely-empty save
        /// has no stranded sidecars (deletion removes them).
        /// </summary>
        internal static bool ShouldPreserveCommittedTreeState(
            bool committedTreeStateLoaded, int committedTreeCount, int strandedSidecarCount)
        {
            return !committedTreeStateLoaded && committedTreeCount == 0 && strandedSidecarCount > 0;
        }

        /// <summary>
        /// Re-hydrates the COMMITTED RECORDING_TREE + MISSION nodes from an on-disk KSP save
        /// file (<paramref name="persistentSavePath"/>, a *.sfs) into <paramref name="targetNode"/>
        /// (the ParsekScenario node being written), reading the save's own ParsekScenario SCENARIO
        /// node. Returns the number of committed RECORDING_TREE nodes copied (0 when the file is
        /// missing / unparseable / has no ParsekScenario node / has no committed trees). ADDITIVE
        /// for trees: it appends the on-disk COMMITTED trees (skipping any on-disk active/pending
        /// marker) WITHOUT removing the caller's nodes, so a live in-flight active/pending node is
        /// preserved and no second active node is introduced. Missions (which belong to the
        /// restored committed trees, and are empty in a fault since MissionStore.Load runs after
        /// LoadRecordingTrees) are replaced with the on-disk set. Deep-copies each node so the
        /// discarded loaded document is not aliased. Never throws.
        /// </summary>
        /// <summary>
        /// Pure node lookup: finds the COMMITTED RECORDING_TREE node whose <c>id</c>
        /// equals <paramref name="treeId"/> inside a loaded ParsekScenario node, or
        /// null. Active / pending marker nodes are skipped - carrying one forward as a
        /// committed tree would resurrect a parked tree as committed history.
        /// </summary>
        internal static ConfigNode FindCommittedTreeNodeById(ConfigNode parsekNode, string treeId)
        {
            if (parsekNode == null || string.IsNullOrEmpty(treeId))
                return null;
            foreach (ConfigNode treeNode in parsekNode.GetNodes("RECORDING_TREE"))
            {
                if (treeNode == null) continue;
                if (IsActiveTreeNode(treeNode) || IsPendingTreeNode(treeNode)) continue;
                if (string.Equals(treeNode.GetValue("id"), treeId, StringComparison.Ordinal))
                    return treeNode;
            }
            return null;
        }

        /// <summary>
        /// Loads the ParsekScenario node of the save the carry-forward reads its
        /// last-known-good tree nodes from. That is ALWAYS the campaign persistent.sfs
        /// (which KSP has not overwritten yet at OnSave time), whatever file this
        /// particular save is targeting.
        ///
        /// <para><b>Why it cannot be the target file.</b> Stock does not expose the target
        /// name inside <c>ScenarioModule.OnSave</c>: <c>GamePersistence.SaveGame</c> keeps
        /// <c>saveFileName</c> as a local and calls <c>game.Save(node)</c> before writing
        /// the file. There is no ambient signal to read, and an earlier draft's
        /// "hint a call site sets" was never set by anything - dead code with a doc that
        /// described an intention rather than the shipped behaviour.</para>
        ///
        /// <para><b>Why reading the campaign save is nonetheless safe.</b> The node that
        /// comes out is RECONCILED against the LIVE tree by
        /// <see cref="TryCarryForwardCommittedTreeNodeFromLoadedSave"/>: only recordings
        /// the live tree still has AND whose sidecars exist on disk survive, the tree is
        /// refused outright if its root recording did not survive, and the branch-point
        /// topology is pruned to what remains. So a save targeting a quicksave slot cannot
        /// acquire recordings this game does not have. What it can still inherit is a stale
        /// SCALAR field (a tree name, a flag) from the campaign's copy of the same tree -
        /// strictly better than dropping the tree, which is the only alternative here.</para>
        /// </summary>
        internal static ConfigNode TryLoadCarryForwardSourceParsekNode(out string sourcePath)
        {
            sourcePath = null;
            try
            {
                sourcePath = PersistentSavePathOverrideForTesting
                    ?? RecordingPaths.ResolveSaveScopedPath(PersistentSaveFileName);
                if (string.IsNullOrEmpty(sourcePath) || !System.IO.File.Exists(sourcePath))
                    return null;
                ConfigNode root = ConfigNode.Load(sourcePath);
                if (root == null)
                    return null;
                return FindParsekScenarioNodeInGame(root.GetNode("GAME") ?? root);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Scenario",
                    $"OnSave: failed to load the carry-forward source save '{sourcePath ?? "<none>"}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Copies ONE committed tree's last-known-good RECORDING_TREE node out of an already
        /// loaded save into the node being written, RECONCILED against the live tree.
        ///
        /// <para><b>Why reconcile.</b> The on-disk node is older than memory. Carried
        /// verbatim it can RESURRECT recordings the player has since deleted: the tree node
        /// would list ids whose sidecars are gone, and the next load's orphan reap - which
        /// works the other way round, deleting files nothing references - would then be
        /// operating on a corpus that disagrees with the save. So a carried RECORDING is
        /// kept only when the LIVE tree still has that id. Everything else is dropped,
        /// counted, and logged.</para>
        ///
        /// <para><b>Why membership alone, not "membership AND a sidecar on disk".</b> A
        /// deleted recording leaves the live tree, so membership already covers the
        /// resurrection hazard. A recording that is still IN the live tree but has no
        /// trajectory sidecar is not deleted data - it is exactly the sidecar fault this
        /// carry exists to survive (the tree is unserializable BECAUSE that recording
        /// cannot be written). Requiring the sidecar dropped precisely the faulted
        /// recording, and when that recording was the root the carry was refused and the
        /// whole tree was deleted at Error - the outcome the invariant above forbids. The
        /// synthetic corpus's 34 metadata-only tree recordings reproduced it on every
        /// save (H5 / S1.4 / S1.6 red from 2026-08-29). Keeping the metadata is strictly
        /// better than dropping the tree: the recording loads as trajectory-missing,
        /// plays nothing, and the player can still delete it.</para>
        ///
        /// <para><b>The topology has to follow the recordings.</b> Dropping RECORDING
        /// children alone leaves <c>rootRecordingId</c> / <c>activeRecordingId</c> /
        /// BRANCH_POINT parent+child ids pointing at recordings that are no longer in the
        /// node - and the dropped id is very often the trigger recording itself. The
        /// load side cannot rescue that: its prunes fire for schema-REJECTED ids, not for
        /// ids that were simply never written. So this prunes the way a load would want:
        /// a dropped ROOT refuses the carry outright (a tree with a dangling root loads
        /// as a broken tree), a dropped ACTIVE id is nulled, and any BRANCH_POINT
        /// referencing a dropped id is removed.</para>
        ///
        /// <para>Returns false (nothing added) when there is no such committed node, when
        /// reconciliation leaves no recordings at all - an empty shell is not worth writing
        /// and would itself read as a corrupt tree - or when the root recording did not
        /// survive. Never throws.</para>
        /// </summary>
        internal static bool TryCarryForwardCommittedTreeNodeFromLoadedSave(
            ConfigNode targetNode,
            ConfigNode sourceParsekNode,
            RecordingTree liveTree,
            out int carriedRecordings,
            out int droppedRecordings)
        {
            carriedRecordings = 0;
            droppedRecordings = 0;
            if (targetNode == null || sourceParsekNode == null || liveTree == null
                || string.IsNullOrEmpty(liveTree.Id))
                return false;
            try
            {
                ConfigNode diskTree = FindCommittedTreeNodeById(sourceParsekNode, liveTree.Id);
                if (diskTree == null)
                    return false;

                ConfigNode carried = diskTree.CreateCopy();
                var keep = new List<ConfigNode>();
                var keptIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (ConfigNode recNode in carried.GetNodes("RECORDING"))
                {
                    string recordingId = recNode?.GetValue("recordingId");
                    if (IsCarriedRecordingStillReal(liveTree, recordingId))
                    {
                        keep.Add(recNode.CreateCopy());
                        keptIds.Add(recordingId);
                    }
                    else
                    {
                        droppedRecordings++;
                    }
                }
                if (keep.Count == 0)
                {
                    ParsekLog.Warn("Scenario",
                        $"OnSave: refusing to carry forward tree id={liveTree.Id} - none of its "
                        + $"{droppedRecordings} on-disk recording(s) still exist in the live tree, "
                        + "so the carried node would resurrect deleted data");
                    return false;
                }

                // The root is structural: a carried tree whose rootRecordingId names a
                // dropped recording loads as a broken tree, and the load-side prunes never
                // fire for it (they handle schema-REJECTED ids, not absent ones).
                string carriedRoot = carried.GetValue("rootRecordingId");
                if (!string.IsNullOrEmpty(carriedRoot) && !keptIds.Contains(carriedRoot))
                {
                    ParsekLog.Warn("Scenario",
                        $"OnSave: refusing to carry forward tree id={liveTree.Id} - its root "
                        + $"recording '{carriedRoot}' did not survive reconciliation "
                        + $"(kept={keep.Count} dropped={droppedRecordings}), and a carried tree "
                        + "with a dangling root would load as a broken tree");
                    return false;
                }

                carried.RemoveNodes("RECORDING");
                for (int i = 0; i < keep.Count; i++)
                    carried.AddNode(keep[i]);

                // A dangling activeRecordingId is recoverable (the tree simply has no active
                // recording), so null it rather than refusing the whole carry.
                string carriedActive = carried.GetValue("activeRecordingId");
                if (!string.IsNullOrEmpty(carriedActive) && !keptIds.Contains(carriedActive))
                {
                    carried.RemoveValues("activeRecordingId");
                    ParsekLog.Warn("Scenario",
                        $"OnSave: carried tree id={liveTree.Id} dropped its activeRecordingId "
                        + $"'{carriedActive}' - that recording did not survive reconciliation");
                }

                int droppedBranchPoints = PruneCarriedBranchPoints(carried, keptIds);
                if (droppedBranchPoints > 0)
                    ParsekLog.Warn("Scenario",
                        $"OnSave: carried tree id={liveTree.Id} dropped {droppedBranchPoints} "
                        + "BRANCH_POINT node(s) referencing recordings that did not survive "
                        + "reconciliation");

                carriedRecordings = keep.Count;
                targetNode.AddNode(carried);
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Scenario",
                    $"OnSave: failed to carry forward the last-known-good node for tree "
                    + $"id={liveTree.Id}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Removes every BRANCH_POINT of a carried tree node that names a recording id
        /// outside <paramref name="keptIds"/>, in either its <c>parentId</c> or
        /// <c>childId</c> values. A branch point is a relation between recordings; with one
        /// end missing it is not a weaker relation, it is a dangling one. Returns how many
        /// were removed. Pure over the ConfigNode.
        /// </summary>
        internal static int PruneCarriedBranchPoints(ConfigNode carriedTree, HashSet<string> keptIds)
        {
            if (carriedTree == null)
                return 0;
            ConfigNode[] branchPoints = carriedTree.GetNodes("BRANCH_POINT");
            if (branchPoints == null || branchPoints.Length == 0)
                return 0;

            var survivors = new List<ConfigNode>();
            int dropped = 0;
            for (int i = 0; i < branchPoints.Length; i++)
            {
                ConfigNode bp = branchPoints[i];
                if (bp == null) { dropped++; continue; }
                if (AllReferencedIdsKept(bp, "parentId", keptIds)
                    && AllReferencedIdsKept(bp, "childId", keptIds))
                    survivors.Add(bp.CreateCopy());
                else
                    dropped++;
            }
            if (dropped == 0)
                return 0;

            carriedTree.RemoveNodes("BRANCH_POINT");
            for (int i = 0; i < survivors.Count; i++)
                carriedTree.AddNode(survivors[i]);
            return dropped;
        }

        private static bool AllReferencedIdsKept(ConfigNode bp, string key, HashSet<string> keptIds)
        {
            string[] values = bp.GetValues(key);
            if (values == null)
                return true;
            for (int i = 0; i < values.Length; i++)
            {
                if (string.IsNullOrEmpty(values[i]))
                    continue;
                if (keptIds == null || !keptIds.Contains(values[i]))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Reconciliation predicate for one carried RECORDING node: the id must still be in
        /// the live tree. That is the whole test. Sidecar presence is deliberately NOT
        /// consulted - see the "Why membership alone" paragraph on
        /// <see cref="TryCarryForwardCommittedTreeNodeFromLoadedSave"/>: the carry runs
        /// precisely when a live recording's sidecar cannot be written, so a sidecar
        /// requirement drops the one recording the carry is for and deletes the tree
        /// whenever that recording is the root. Pure over the tree; never touches disk.
        /// </summary>
        internal static bool IsCarriedRecordingStillReal(RecordingTree liveTree, string recordingId)
        {
            if (liveTree == null || string.IsNullOrEmpty(recordingId))
                return false;
            return liveTree.Recordings != null && liveTree.Recordings.ContainsKey(recordingId);
        }

        internal static int TryRehydrateCommittedTreesAndMissionsFromDiskSave(
            ConfigNode targetNode, string persistentSavePath, out int rehydratedMissions)
        {
            rehydratedMissions = 0;
            if (targetNode == null || string.IsNullOrEmpty(persistentSavePath))
                return 0;
            try
            {
                if (!System.IO.File.Exists(persistentSavePath))
                    return 0;
                ConfigNode root = ConfigNode.Load(persistentSavePath);
                if (root == null)
                    return 0;
                ConfigNode gameNode = root.GetNode("GAME") ?? root;
                ConfigNode parsekNode = FindParsekScenarioNodeInGame(gameNode);
                if (parsekNode == null)
                    return 0;

                // Add only the COMMITTED on-disk trees; skip any on-disk active/pending marker so
                // a second active node is never introduced, and do NOT remove the caller's nodes so
                // a live in-flight active/pending recording written this save is preserved.
                int addedTrees = 0;
                // A committed tree id the caller already wrote (an inert tree re-emitted by
                // SaveTreeRecordings, GC-3) is not added a second time. Active / pending nodes
                // do not count: a resumed tree may share its committed copy's id.
                var writtenTreeIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (ConfigNode written in targetNode.GetNodes("RECORDING_TREE"))
                {
                    if (IsActiveTreeNode(written) || IsPendingTreeNode(written))
                        continue;
                    string writtenId = written.GetValue("id");
                    if (!string.IsNullOrEmpty(writtenId))
                        writtenTreeIds.Add(writtenId);
                }
                foreach (ConfigNode diskTree in parsekNode.GetNodes("RECORDING_TREE"))
                {
                    if (IsActiveTreeNode(diskTree) || IsPendingTreeNode(diskTree))
                        continue;
                    string diskId = diskTree.GetValue("id");
                    if (!string.IsNullOrEmpty(diskId) && writtenTreeIds.Contains(diskId))
                    {
                        ParsekLog.Verbose("Scenario",
                            $"Load-fault guard: tree id={diskId} already written this save; not re-added");
                        continue;
                    }
                    targetNode.AddNode(diskTree.CreateCopy());
                    addedTrees++;
                }
                if (addedTrees == 0)
                    return 0;

                // Missions belong to the committed trees just restored; replace the (empty, from
                // the faulted load) mission set with the on-disk one.
                targetNode.RemoveNodes("MISSION");
                foreach (ConfigNode diskMission in parsekNode.GetNodes("MISSION"))
                {
                    targetNode.AddNode(diskMission.CreateCopy());
                    rehydratedMissions++;
                }
                return addedTrees;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Scenario",
                    "OnSave load-fault guard: failed to re-hydrate tree/mission state from "
                    + $"'{persistentSavePath}': {ex.Message}");
                return 0;
            }
        }

        /// <summary>Finds the ParsekScenario SCENARIO node inside a loaded GAME node, or null.</summary>
        private static ConfigNode FindParsekScenarioNodeInGame(ConfigNode gameNode)
        {
            if (gameNode == null)
                return null;
            ConfigNode[] scenarios = gameNode.GetNodes("SCENARIO");
            for (int i = 0; i < scenarios.Length; i++)
            {
                if (string.Equals(scenarios[i].GetValue("name"), nameof(ParsekScenario), StringComparison.Ordinal))
                    return scenarios[i];
            }
            return null;
        }
    }
}
