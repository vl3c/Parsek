using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // Keeping committed trees across a load fault: preserve the loaded state, carry an
    // unwritable recording's sidecar keys forward from the loaded save, rehydrate from the
    // disk save.
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
        /// <para><b>Why reading the campaign save is nonetheless safe.</b> The save reads
        /// only the sidecar keys of recordings it cannot write out of it, by id, through
        /// <see cref="SpliceLastKnownGoodRecordingNodes"/>; the tree, its topology and every
        /// recording's metadata come from memory. So a save targeting a quicksave slot cannot
        /// acquire recordings this game does not have. What an unwritable member can still
        /// inherit is the campaign's naming of its own files, which is what the files on disk
        /// carry unless another save rewrote them since.</para>
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
        /// For each recording in <paramref name="unwritableIds"/>, replaces in the tree node
        /// just written from memory the keys that name that recording's sidecar files
        /// (<see cref="CarriedSidecarIdentityKeys"/>) with the values its last-known-good
        /// RECORDING node in <paramref name="diskTree"/> (the same tree in the on-disk save)
        /// holds. A key the disk node lacks is removed. Returns how many recordings were
        /// spliced.
        ///
        /// <para><b>Why the tree comes from memory.</b> The skip condition (a recording whose
        /// sidecars are missing or unwritable) is sticky, so whatever this save writes repeats
        /// on every save until the fault clears. Anything taken from the older on-disk tree
        /// node instead of memory (a recording added since, such as a Re-Fly fork, a
        /// continuation segment or a new branch, or an edit to a healthy recording) would be
        /// missing from every save while the fault lasts (todo
        /// SAVE-FREEZE-ON-MISSING-SIDECAR).</para>
        ///
        /// <para><b>Why only the sidecar keys come from disk.</b> They are what the next load
        /// resolves the files by, and the files on disk are the last ones that save named:
        /// the failed write changed nothing there. The rest of the node is metadata memory
        /// loaded from that same node and has edited since; in particular the branch-point
        /// links must stay memory's, or a recording a branch was added under would reload
        /// with no <c>childBranchPointId</c> and read as a leaf while memory's BRANCH_POINT
        /// still names it as the parent.</para>
        ///
        /// <para><b>Nothing is added.</b> The walk runs over the nodes memory wrote, so a
        /// recording the disk save still lists but the live tree has deleted is never
        /// resurrected. An unwritable id the disk tree does not hold keeps memory's node.
        /// Pure over the ConfigNodes; never throws on null inputs.</para>
        /// </summary>
        internal static int SpliceLastKnownGoodRecordingNodes(
            ConfigNode writtenTree, ConfigNode diskTree, ICollection<string> unwritableIds)
        {
            if (writtenTree == null || diskTree == null || unwritableIds == null || unwritableIds.Count == 0)
                return 0;

            var diskById = new Dictionary<string, ConfigNode>(StringComparer.Ordinal);
            foreach (ConfigNode diskRec in diskTree.GetNodes("RECORDING"))
            {
                string id = diskRec?.GetValue("recordingId");
                if (!string.IsNullOrEmpty(id) && !diskById.ContainsKey(id))
                    diskById[id] = diskRec;
            }

            string[] keys = CarriedSidecarIdentityKeys();
            int spliced = 0;
            foreach (ConfigNode writtenRec in writtenTree.GetNodes("RECORDING"))
            {
                string id = writtenRec?.GetValue("recordingId");
                if (string.IsNullOrEmpty(id) || !unwritableIds.Contains(id)
                    || !diskById.TryGetValue(id, out ConfigNode lastKnownGood))
                    continue;
                for (int k = 0; k < keys.Length; k++)
                {
                    writtenRec.RemoveValues(keys[k]);
                    string[] diskValues = lastKnownGood.GetValues(keys[k]);
                    if (diskValues == null)
                        continue;
                    for (int v = 0; v < diskValues.Length; v++)
                        writtenRec.AddValue(keys[k], diskValues[v]);
                }
                spliced++;
            }
            return spliced;
        }

        /// <summary>
        /// The RECORDING keys that describe a recording's sidecar files rather than the
        /// recording: the epoch and content hashes the loader resolves the files by, the
        /// ghost snapshot mode that picks the ghost file, and the trajectory point count.
        /// The format stamps are not here: every recording this build loaded is at the
        /// current format and generation, which memory writes.
        /// </summary>
        internal static string[] CarriedSidecarIdentityKeys()
        {
            return new[]
            {
                "sidecarEpoch", "precHash", "vesselHash", "ghostHash", "ghostSnapshotMode", "pointCount",
            };
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
