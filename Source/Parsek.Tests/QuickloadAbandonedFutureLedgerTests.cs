using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The quickload-resume trim over committed history, driven through the production entry
    /// <c>FlightRecorder.PrepareQuickloadResumeStateIfNeeded</c>. Phase 3 of the load design
    /// deleted the abandoned-future reconcile (owner ruling OQ-2) the resume trim used to run
    /// and the in-session retire of the untagged rows made after the loaded save.
    /// </summary>
    [Collection("Sequential")]
    public class QuickloadAbandonedFutureLedgerTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public QuickloadAbandonedFutureLedgerTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            ResetAll();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            ResetAll();
            KspStatePatcher.ResetForTesting();
        }

        private static void ResetAll()
        {
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.ClearPendingQuickloadResumeContext();
            ParsekScenario.ClearRestoredQuicksaveTreeFactsForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            FlightRecorder.QuickloadResumeUTProviderForTesting = null;
        }

        // ============================================================
        // Committed history at the quicksave: if the quicksave's active tree was already
        // committed (a copy-on-write restore clone; the save-side overlap guard writes that
        // shape only after an out-of-band sidecar write, see
        // QUICKLOAD-TRIM-CUTS-COMMITTED-HISTORY-OF-A-RESUMED-CLONE), the restore detaches it
        // too, so only the quicksave can tell its history members from the abandoned future.
        // Owner ruling D2: committed history is never retired, cut or pruned.
        // ============================================================

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TryRestoreActiveTreeNode_CommittedBeforeTheQuicksave_TrimLeavesHistoryMembersWhole(
            bool quicksaveHoldsCommittedNode)
        {
            // QUICKLOAD-TRIM-CUTS-COMMITTED-HISTORY-OF-A-RESUMED-CLONE. The quicksave (F5 at 260)
            // is a copy-on-write clone of a tree committed before it, with a fresh live segment
            // as the active recording. Committed history past the cutoff: M lands at 400, P ends
            // at a branch point at 350 and its child P1 flies 350..420. With the committed tree's
            // own node in the save (what OnSave writes) or without it (the reasons from the
            // member's own node), the trim must leave all three whole.
            var committed = MakeHistoryTree("hist_trim_" + (quicksaveHoldsCommittedNode ? "node" : "facts"));
            string mId = HistoryMemberId(committed);
            AddBranchHistory(committed, out string pId, out string p1Id, out string bpId);
            CommitInMemory(committed);
            string segmentId = "live_seg_" + committed.Id;
            RecordingTree clone = CloneWithLiveSegment(committed, segmentId);

            var resumed = RestoreQuicksaveNode(
                QuicksaveNodeOf(clone, quicksaveHoldsCommittedNode ? committed : null));
            Assert.DoesNotContain(RecordingStore.CommittedTrees, t => t.Id == committed.Id);

            logLines.Clear();
            RunResumePrep(resumed, 260.0, LoadKind.QuickloadFlight);

            Recording m = resumed.Recordings[mId];
            Assert.Equal(400.0, m.EndUT);
            Assert.Equal(400.0, m.Points[m.Points.Count - 1].ut);
            Assert.Equal(TerminalState.Landed, m.TerminalStateValue);
            Assert.True(resumed.Recordings.ContainsKey(p1Id), "the future-only history child is pruned");
            Assert.Equal(420.0, resumed.Recordings[p1Id].EndUT);
            Assert.Equal(bpId, resumed.Recordings[pId].ChildBranchPointId);
            Assert.Equal(350.0, resumed.Recordings[pId].EndUT);
            Assert.Contains(resumed.BranchPoints, bp => bp.Id == bpId);
            // The live segment is still the abandoned future's.
            Assert.True(resumed.Recordings[segmentId].EndUT <= 260.0);
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]") && l.Contains("Quickload tree trim:")
                // The node case also names the root (committed in the save; it ends before the cutoff).
                && l.Contains("keptWholeHistory=" + (quicksaveHoldsCommittedNode ? "4" : "3"))
                && l.Contains("prunedFutureRecordings=0"));
        }

        // ============================================================
        // helpers
        // ============================================================

        // A tree committed before the quicksave at 260: the root flew 100..250, member M flew
        // 120..400 and landed with Bob aboard.
        private static RecordingTree MakeHistoryTree(string id)
        {
            var tree = new RecordingTree
            {
                Id = id,
                TreeName = id,
                RootRecordingId = "root_" + id,
                ActiveRecordingId = "root_" + id,
            };
            var root = new Recording { RecordingId = "root_" + id, TreeId = id, VesselName = id, VesselPersistentId = 111u };
            SetPoints(root, 100.0, 180.0, 250.0);
            tree.AddOrReplaceRecording(root);
            var m = new Recording
            {
                RecordingId = "hist_" + id, TreeId = id, VesselName = id + " lander", VesselPersistentId = 222u,
                TerminalStateValue = TerminalState.Landed,
                GhostVisualSnapshot = CrewSnapshot("Bob Kerman"),
                CrewEndStates = new Dictionary<string, KerbalEndState> { { "Bob Kerman", KerbalEndState.Aboard } },
                CrewEndStatesResolved = true,
            };
            SetPoints(m, 120.0, 300.0, 400.0);
            tree.AddOrReplaceRecording(m);
            tree.RebuildBackgroundMap();
            return tree;
        }

        private static string HistoryMemberId(RecordingTree tree) => "hist_" + tree.Id;

        // Adds P (120..350, ends at an undock branch point at 350) and its child P1 (350..420,
        // Landed) to a history tree.
        private static void AddBranchHistory(RecordingTree tree, out string pId, out string p1Id, out string bpId)
        {
            pId = "p_" + tree.Id;
            p1Id = "p1_" + tree.Id;
            bpId = "bp_" + tree.Id + "_350";
            var p = new Recording
            {
                RecordingId = pId, TreeId = tree.Id, VesselName = "P", VesselPersistentId = 555u,
                ChildBranchPointId = bpId,
            };
            SetPoints(p, 120.0, 300.0, 350.0);
            p.ExplicitEndUT = double.NaN;
            tree.AddOrReplaceRecording(p);
            var p1 = new Recording
            {
                RecordingId = p1Id, TreeId = tree.Id, VesselName = "P1", VesselPersistentId = 666u,
                ParentBranchPointId = bpId, TerminalStateValue = TerminalState.Landed,
            };
            SetPoints(p1, 350.0, 420.0);
            tree.AddOrReplaceRecording(p1);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = bpId, UT = 350.0, Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { pId },
                ChildRecordingIds = new List<string> { p1Id },
            });
            tree.RebuildBackgroundMap();
        }

        private static void CommitInMemory(RecordingTree tree)
        {
            foreach (var rec in tree.Recordings.Values)
                RecordingStore.AddCommittedInternal(rec);
            RecordingStore.AddCommittedTreeForTesting(tree);
        }

        // The copy-on-write clone the player resumed, plus a fresh live segment that is the
        // active recording.
        private static RecordingTree CloneWithLiveSegment(RecordingTree committed, string segmentId)
        {
            var clone = RecordingTree.DeepClone(committed);
            var segment = new Recording
            {
                RecordingId = segmentId, TreeId = committed.Id, VesselName = "live", VesselPersistentId = 444u,
            };
            SetPoints(segment, 250.0, 260.0);
            clone.AddOrReplaceRecording(segment);
            clone.ActiveRecordingId = segmentId;
            return clone;
        }

        // A save node holding the active tree (and, when given, a committed tree node written
        // before it, the way OnSave writes committed trees first).
        private static ConfigNode QuicksaveNodeOf(RecordingTree activeTree, RecordingTree committedTree)
        {
            var node = new ConfigNode("PARSEK_SCENARIO");
            if (committedTree != null)
                committedTree.Save(node.AddNode("RECORDING_TREE"));
            var treeNode = node.AddNode("RECORDING_TREE");
            activeTree.Save(treeNode);
            treeNode.AddValue("isActive", "True");
            return node;
        }

        private static RecordingTree RestoreQuicksaveNode(ConfigNode node)
        {
            Assert.True(ParsekScenario.TryRestoreActiveTreeNode(node));
            return RecordingStore.PopPendingTree();
        }

        private static ConfigNode CrewSnapshot(string kerbal)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", kerbal);
            return snapshot;
        }

        private static void SetPoints(Recording rec, params double[] uts)
        {
            rec.Points.Clear();
            rec.TrackSections.Clear();
            foreach (double ut in uts)
            {
                rec.Points.Add(new TrajectoryPoint
                {
                    ut = ut, altitude = 1000.0, bodyName = "Kerbin",
                    rotation = Quaternion.identity, velocity = Vector3.zero,
                });
            }
            rec.ExplicitStartUT = uts[0];
            rec.ExplicitEndUT = uts[uts.Length - 1];
        }

        private static void RunResumePrep(RecordingTree tree, double resumeUT, LoadKind kind)
        {
            var recorder = new FlightRecorder { ActiveTree = tree };
            FlightRecorder.QuickloadResumeUTProviderForTesting = () => resumeUT;
            ParsekScenario.ConfigurePendingQuickloadResumeContext(tree, kind);
            MethodInfo method = typeof(FlightRecorder).GetMethod(
                "PrepareQuickloadResumeStateIfNeeded", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method.Invoke(recorder, null);
        }
    }
}
